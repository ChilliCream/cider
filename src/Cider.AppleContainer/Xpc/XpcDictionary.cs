using System.Runtime.InteropServices;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// A flat XPC dictionary — the only message shape apiserver's <c>ContainerXPC</c> protocol uses,
/// on both the request and the reply side
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2, §1.3). Typed accessors here are thin,
/// direct wrappers over the matching <c>xpc_dictionary_*</c> pair; the route key and higher-level
/// framing live in <see cref="XpcMessage"/>, which wraps one of these.
/// </summary>
internal sealed class XpcDictionary : XpcObject
{
    /// <summary>Creates a new, empty dictionary — the start of an outbound request.</summary>
    public XpcDictionary()
        : base(XpcNative.xpc_dictionary_create(0, 0, 0))
    {
    }

    /// <summary>Wraps a dictionary this instance now owns — typically a reply.</summary>
    public XpcDictionary(nint handle, bool ownsHandle = true)
        : base(handle, ownsHandle)
    {
    }

    public void SetString(string key, string value) => Use(h => XpcNative.xpc_dictionary_set_string(h, key, value));

    /// <summary><c>null</c> when the key is absent.</summary>
    public string? GetString(string key) => Use(h => Marshal.PtrToStringUTF8(XpcNative.xpc_dictionary_get_string(h, key)));

    public unsafe void SetData(string key, ReadOnlySpan<byte> value)
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            var handle = DangerousGetHandle();
            fixed (byte* p = value)
            {
                XpcNative.xpc_dictionary_set_data(handle, key, p, (nuint)value.Length);
            }
        }
        finally
        {
            if (added)
            {
                DangerousRelease();
            }
        }
    }

    /// <summary><c>null</c> when the key is absent. The bytes are copied out immediately, so the
    /// result stays valid after this dictionary is released — unlike the underlying
    /// <c>xpc_dictionary_get_data</c> pointer, which the dictionary owns.</summary>
    public unsafe byte[]? GetData(string key) => Use<byte[]?>(h =>
    {
        var p = XpcNative.xpc_dictionary_get_data(h, key, out var length);
        if (p != null)
        {
            return new ReadOnlySpan<byte>(p, checked((int)length)).ToArray();
        }

        // A NULL pointer is ambiguous on its own: libxpc returns it both for "key absent" and for
        // "key present, a zero-length data value" (there is no buffer to point a 0-byte allocation
        // at) — confirmed live while building this client. Disambiguate via the key's presence
        // rather than mistake real empty data for absence.
        return XpcNative.xpc_dictionary_get_value(h, key) != 0 ? [] : null;
    });

    public void SetBool(string key, bool value) => Use(h => XpcNative.xpc_dictionary_set_bool(h, key, value));

    public bool GetBool(string key) => Use(h => XpcNative.xpc_dictionary_get_bool(h, key));

    public void SetUInt64(string key, ulong value) => Use(h => XpcNative.xpc_dictionary_set_uint64(h, key, value));

    public ulong GetUInt64(string key) => Use(h => XpcNative.xpc_dictionary_get_uint64(h, key));

    public void SetInt64(string key, long value) => Use(h => XpcNative.xpc_dictionary_set_int64(h, key, value));

    public long GetInt64(string key) => Use(h => XpcNative.xpc_dictionary_get_int64(h, key));

    /// <summary>Sets an XPC <c>date</c> value — nanoseconds since the Unix epoch, libxpc's own
    /// convention (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2), not to be confused with the
    /// seconds-since-2001 <c>Date</c> encoding inside JSON payloads (§2.0.2). A .NET tick is
    /// exactly 100 ns, so the conversion is exact.</summary>
    public void SetDate(string key, DateTimeOffset value)
    {
        var ticksSinceEpoch = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        Use(h => XpcNative.xpc_dictionary_set_date(h, key, ticksSinceEpoch * 100));
    }

    public DateTimeOffset GetDate(string key)
    {
        var nanosecondsSinceEpoch = Use(h => XpcNative.xpc_dictionary_get_date(h, key));
        var ticks = DateTimeOffset.UnixEpoch.UtcTicks + nanosecondsSinceEpoch / 100;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public void SetFd(string key, int fd) => Use(h => XpcNative.xpc_dictionary_set_fd(h, key, fd));

    /// <summary>Duplicates the fd the peer placed at <paramref name="key"/> into this process.</summary>
    public int DupFd(string key) => Use(h => XpcNative.xpc_dictionary_dup_fd(h, key));

    /// <summary>Stores a raw <c>xpc_object_t</c> value (an endpoint, a nested array/dictionary, …)
    /// under <paramref name="key"/> without taking ownership away from <paramref name="value"/>.</summary>
    public void SetValue(string key, XpcObject value) =>
        Use(h => value.Use(v => XpcNative.xpc_dictionary_set_value(h, key, v)));

    public bool ContainsKey(string key) => Use(h => XpcNative.xpc_dictionary_get_value(h, key) != 0);

    public nuint Count => Use(XpcNative.xpc_dictionary_get_count);
}
