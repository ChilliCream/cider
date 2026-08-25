using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Turns the apiserver's <c>progressUpdateEndpoint</c> messages into <see cref="ProgressEvent"/>s
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §5) — the piece <see cref="XpcListener"/> (task X1)
/// deliberately left undone ("decoding progress messages into anything the daemon acts on is X9's
/// job"). Two levels of connection, exactly as the Swift client does it
/// (<c>ProgressUpdateClient.swift:23-96</c>, cited in §5):
/// <list type="number">
/// <item>An anonymous listener (<see cref="XpcListener"/>) hands its <see cref="Endpoint"/> to
/// <c>imagePull</c>/<c>imagePush</c>/<c>imageUnpack</c>. The apiserver connects back to it — this
/// arrives at the listener's own event handler as an <c>XPC_TYPE_CONNECTION</c> object, the
/// "reversed connection".</item>
/// <item>That reversed connection needs its own event handler installed and needs activating before
/// it will deliver anything — done here, in <see cref="OnOuterEvent"/> — after which the apiserver's
/// plain, reply-free <c>xpc_connection_send_message</c> calls
/// (<c>ProgressUpdateService.swift:41-80</c>) land on <see cref="OnPeerEvent"/> as ordinary
/// dictionaries.</item>
/// </list>
/// The dictionary → event mapping itself is <see cref="ProgressUpdateDecoder"/>, a pure/stateful
/// class with no native connection of its own, so <c>ProgressUpdateListenerTests.cs</c> can drive it
/// directly with a hand-built <see cref="XpcDictionary"/> — no live apiserver, no listener — instead
/// of only being exercisable end-to-end.
/// </summary>
internal sealed class ProgressUpdateListener : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action<ProgressEvent> _onEvent;
    private readonly ProgressUpdateDecoder _decoder = new();
    private readonly XpcListener _outer;
    private readonly Lock _gate = new();

    /// <summary>The accepted "reversed connection" — 0 until the apiserver connects back, at most one
    /// per instance (this listener's endpoint is only ever handed to a single call).</summary>
    private nint _peer;
    private bool _disposed;

    public ProgressUpdateListener(ILogger logger, Action<ProgressEvent> onEvent)
    {
        _logger = logger;
        _onEvent = onEvent;
        _outer = XpcListener.Create(logger, OnOuterEvent);
    }

    /// <summary>Hand this to a request's <c>progressUpdateEndpoint</c> field
    /// (<c>XpcMessage.SetValue</c>) — may be reused across more than one call on the same pull/push/
    /// unpack sequence (<c>SetValue</c> copies the value in without transferring ownership away from
    /// this instance).</summary>
    public XpcObject Endpoint => _outer.Endpoint;

    private void OnOuterEvent(nint xpcObject)
    {
        // Anything other than a fresh incoming connection is a transport-level event on the OUTER
        // listener connection itself (XpcListener's own Create already handles detaching its block on
        // the terminal one) — nothing for this type to do with those.
        if (xpcObject == 0 || XpcObject.TypeNameOf(xpcObject) != "connection")
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _peer != 0)
            {
                // Disposed already, or a second peer somehow showed up — neither is expected, but
                // silently accepting a peer this instance can never clean up would leak it.
                return;
            }

            // Deliberate extra retain regardless of exactly what ownership convention libxpc uses for
            // a freshly-accepted connection object (unclear from the public docs, and there is no
            // existing precedent for this exact case elsewhere in this codebase): a retain on
            // something already owned is harmless (an extra reference this instance releases exactly
            // once, in Dispose/OnPeerEvent's terminal branch); a missing retain on something NOT
            // already owned would be a use-after-free. The same "over-retain, release exactly once"
            // posture XpcClient uses throughout.
            XpcNative.xpc_retain(xpcObject);
            _peer = xpcObject;

            var block = XpcBlock.CreateEventHandler(OnPeerEvent);

            // Order matters here too, same as XpcListener/XpcClient's own connections: the handler
            // must be installed before activation, or a message racing activation is dropped. Both
            // calls stay inside this lock — the same one Dispose takes before it cancels/releases
            // _peer — so Dispose can never interleave between the retain above and activation: it
            // either runs entirely before this handler installs (peer never activated, and this
            // method has already returned via the _disposed check above) or entirely after (peer
            // fully activated first). Without this, a Dispose racing between the retain and
            // xpc_connection_activate could cancel and xpc_release the connection while this method
            // still holds the raw handle, a narrow native use-after-free window.
            XpcNative.xpc_connection_set_event_handler(xpcObject, block);
            XpcNative.xpc_connection_activate(xpcObject);
        }
    }

    private void OnPeerEvent(nint self, nint xpcObject)
    {
        if (xpcObject == XpcErrorSentinels.ConnectionInvalid)
        {
            // Guaranteed terminal, guaranteed last event for this connection.
            XpcBlock.Detach(self);
            return;
        }

        if (xpcObject == 0 || xpcObject == XpcErrorSentinels.ConnectionInterrupted)
        {
            return;
        }

        if (XpcObject.TypeNameOf(xpcObject) != "dictionary")
        {
            return;
        }

        try
        {
            // Not owned (a plain message delivery, not a reply this instance created) — read-only,
            // never released here.
            using var message = new XpcDictionary(xpcObject, ownsHandle: false);
            foreach (var evt in _decoder.Decode(message))
            {
                _onEvent(evt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "could not decode a progressUpdate message");
        }
    }

    public void Dispose()
    {
        nint peer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            peer = _peer;
            _peer = 0;
        }

        if (peer != 0)
        {
            // finish() on the Swift client cancels its end too (§5 item 4) — cancellation is how the
            // server learns to stop sending.
            XpcNative.xpc_connection_cancel(peer);
            XpcNative.xpc_release(peer);
        }

        _outer.Dispose();
    }
}

/// <summary>
/// The dictionary → <see cref="ProgressEvent"/> mapping, isolated from
/// <see cref="ProgressUpdateListener"/>'s native connection plumbing so it is unit-testable with a
/// plain, hand-built <see cref="XpcDictionary"/> (§5's update-key table).
/// </summary>
/// <remarks>
/// One incoming dictionary "may carry several keys = several events" (§5) — each key present maps to
/// its own <see cref="ProgressEvent"/>, all sharing whatever text is currently set from the last
/// <c>SetDescription</c> seen. The three numeric families (tasks/items/size) are each independently
/// accumulated across every message this decoder ever sees (mirroring
/// <c>ProgressUpdateClient.swift:110-157</c>'s own running totals: <c>Add*</c> increments,
/// <c>Set*</c> overwrites) — a lone delta with no running total would render as a nonsensical
/// progress bar, so this keeps the state a single raw number cannot carry on its own. All three
/// families map onto <see cref="ProgressEvent"/>'s one <c>Current</c>/<c>Total</c> pair (there is only
/// one), so a stream mixes lines from all three dimensions — acceptable for a usable Docker-shaped
/// progress stream, if not a perfect rendering of Apple's own richer multi-widget TUI.
/// </remarks>
internal sealed class ProgressUpdateDecoder
{
    public const string SetDescription = "progressUpdateSetDescription";
    public const string SetSubDescription = "progressUpdateSetSubDescription";
    public const string SetItemsName = "progressUpdateSetItemsName";
    public const string AddTasks = "progressUpdateAddTasks";
    public const string SetTasks = "progressUpdateSetTasks";
    public const string AddTotalTasks = "progressUpdateAddTotalTasks";
    public const string SetTotalTasks = "progressUpdateSetTotalTasks";
    public const string AddItems = "progressUpdateAddItems";
    public const string SetItems = "progressUpdateSetItems";
    public const string AddTotalItems = "progressUpdateAddTotalItems";
    public const string SetTotalItems = "progressUpdateSetTotalItems";
    public const string AddSize = "progressUpdateAddSize";
    public const string SetSize = "progressUpdateSetSize";
    public const string AddTotalSize = "progressUpdateAddTotalSize";
    public const string SetTotalSize = "progressUpdateSetTotalSize";

    private string? _description;
    private string? _itemsName;
    private long _tasks;
    private long _totalTasks;
    private long _items;
    private long _totalItems;
    private long _size;
    private long _totalSize;

    /// <summary>
    /// Decodes every recognised key present on <paramref name="message"/>. Present-but-zero int64
    /// values are treated exactly like an absent key (§5: "0 means absent — the reader skips zeros")
    /// — real Apple behavior, not a guess: Add-by-zero and Set-to-zero are both indistinguishable from
    /// "not sent" on this wire, so neither updates state nor produces an event.
    /// </summary>
    /// <remarks>
    /// Two passes, deliberately: every key present is first read and applied to this decoder's
    /// running totals, and only then does the second pass build one event per key that was present.
    /// A single pass that emitted each key's event immediately after applying it would make an
    /// event's <c>Total</c> depend on whether its matching <c>Set*</c>/<c>Add*</c> partner happened to
    /// be read before or after it — and <c>xpc_dictionary</c> key order is not a wire guarantee (§1.2),
    /// unlike a JSON object's, so a message setting both <c>items</c> and <c>totalItems</c> together
    /// must have both its events carry the final total regardless of enumeration order.
    /// </remarks>
    public List<ProgressEvent> Decode(XpcDictionary message)
    {
        var setDescription = message.GetString(SetDescription);
        var setSubDescription = message.GetString(SetSubDescription);
        var setItemsName = message.GetString(SetItemsName);
        var addTasks = NonZero(message, AddTasks);
        var setTasks = NonZero(message, SetTasks);
        var addTotalTasks = NonZero(message, AddTotalTasks);
        var setTotalTasks = NonZero(message, SetTotalTasks);
        var addItems = NonZero(message, AddItems);
        var setItems = NonZero(message, SetItems);
        var addTotalItems = NonZero(message, AddTotalItems);
        var setTotalItems = NonZero(message, SetTotalItems);
        var addSize = NonZero(message, AddSize);
        var setSize = NonZero(message, SetSize);
        var addTotalSize = NonZero(message, AddTotalSize);
        var setTotalSize = NonZero(message, SetTotalSize);

        if (setDescription is not null)
        {
            _description = setDescription;
        }

        if (setItemsName is not null)
        {
            _itemsName = setItemsName;
        }

        if (addTasks is { } at)
        {
            _tasks += at;
        }

        if (setTasks is { } st)
        {
            _tasks = st;
        }

        if (addTotalTasks is { } att)
        {
            _totalTasks += att;
        }

        if (setTotalTasks is { } stt)
        {
            _totalTasks = stt;
        }

        if (addItems is { } ai)
        {
            _items += ai;
        }

        if (setItems is { } si)
        {
            _items = si;
        }

        if (addTotalItems is { } ati)
        {
            _totalItems += ati;
        }

        if (setTotalItems is { } sti)
        {
            _totalItems = sti;
        }

        if (addSize is { } asz)
        {
            _size += asz;
        }

        if (setSize is { } ssz)
        {
            _size = ssz;
        }

        if (addTotalSize is { } atsz)
        {
            _totalSize += atsz;
        }

        if (setTotalSize is { } stsz)
        {
            _totalSize = stsz;
        }

        var events = new List<ProgressEvent>();

        if (setDescription is not null)
        {
            events.Add(new ProgressEvent { Status = setDescription });
        }

        if (setSubDescription is not null)
        {
            events.Add(new ProgressEvent { Status = setSubDescription, Id = _itemsName });
        }

        if (addTasks is not null || setTasks is not null)
        {
            events.Add(TaskEvent());
        }

        if (addTotalTasks is not null || setTotalTasks is not null)
        {
            events.Add(TaskEvent());
        }

        if (addItems is not null || setItems is not null)
        {
            events.Add(ItemEvent());
        }

        if (addTotalItems is not null || setTotalItems is not null)
        {
            events.Add(ItemEvent());
        }

        if (addSize is not null || setSize is not null)
        {
            events.Add(SizeEvent());
        }

        if (addTotalSize is not null || setTotalSize is not null)
        {
            events.Add(SizeEvent());
        }

        return events;
    }

    private static long? NonZero(XpcDictionary message, string key)
    {
        if (!message.ContainsKey(key))
        {
            return null;
        }

        var value = message.GetInt64(key);
        return value == 0 ? null : value;
    }

    private ProgressEvent TaskEvent() => new()
    {
        Status = _description,
        Id = _itemsName,
        Current = _tasks,
        Total = _totalTasks > 0 ? _totalTasks : null,
    };

    private ProgressEvent ItemEvent() => new()
    {
        Status = _description,
        Id = _itemsName,
        Current = _items,
        Total = _totalItems > 0 ? _totalItems : null,
    };

    private ProgressEvent SizeEvent() => new()
    {
        Status = _description,
        Id = _itemsName,
        Current = _size,
        Total = _totalSize > 0 ? _totalSize : null,
    };
}
