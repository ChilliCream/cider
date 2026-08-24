using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Time;

namespace Cider.Core.Events;

/// <summary>
/// The daemon's <c>/events</c> fan-out: publishers push <see cref="EventMessage"/>s, subscribers get
/// a replay of the recent history followed by the live stream, both filtered like Docker's
/// <c>filters</c> query parameter.
/// </summary>
public sealed class EventBus
{
    /// <summary>How many events are kept for replay to late subscribers.</summary>
    public const int HistoryCapacity = 1000;

    private const int SubscriberCapacity = 2048;

    private readonly Lock _gate = new();
    private readonly Queue<Entry> _history = new();
    private readonly List<Channel<Entry>> _subscribers = [];
    private long _sequence;

    /// <summary>Number of live <c>/events</c> subscribers (the state poller speeds up while this is &gt; 0).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Publishes an event, stamping the timestamps and Docker's legacy aliases when unset.</summary>
    public void Publish(EventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var now = DateTimeOffset.UtcNow;
        if (message.TimeNano == 0)
        {
            message.TimeNano = DockerTime.UnixNanos(now);
        }

        if (message.Time == 0)
        {
            message.Time = message.TimeNano / 1_000_000_000L;
        }

        // Docker keeps status/id/from populated for pre-1.22 clients.
        message.Status ??= message.Action;
        message.Id ??= message.Actor.ID;
        if (message.From is null && message.Actor.Attributes.TryGetValue("image", out var image))
        {
            message.From = image;
        }

        Entry entry;
        Channel<Entry>[] subscribers;
        lock (_gate)
        {
            entry = new Entry(++_sequence, message);
            _history.Enqueue(entry);
            while (_history.Count > HistoryCapacity)
            {
                _history.Dequeue();
            }

            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            // Bounded + DropOldest: a stalled client loses events instead of stalling the daemon.
            subscriber.Writer.TryWrite(entry);
        }
    }

    /// <summary>
    /// Streams events matching <paramref name="filters"/>: first the buffered history at or after
    /// <paramref name="since"/>, then live events, completing at <paramref name="until"/>.
    /// </summary>
    public async IAsyncEnumerable<EventMessage> Subscribe(
        Filters filters,
        DateTimeOffset? since,
        DateTimeOffset? until,
        [EnumeratorCancellation] CancellationToken ct)
    {
        filters ??= Filters.Empty;

        var channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        List<Entry> replay;
        lock (_gate)
        {
            replay = since is null ? [] : [.. _history];
            _subscribers.Add(channel);
        }

        try
        {
            var lastSequence = 0L;
            var sinceNanos = since is null ? 0L : DockerTime.UnixNanos(since.Value);
            var untilNanos = until is null ? long.MaxValue : DockerTime.UnixNanos(until.Value);

            foreach (var entry in replay)
            {
                lastSequence = entry.Sequence;
                if (entry.Message.TimeNano < sinceNanos)
                {
                    continue;
                }

                if (entry.Message.TimeNano > untilNanos)
                {
                    yield break;
                }

                if (Matches(entry.Message, filters))
                {
                    yield return entry.Message;
                }
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (until is not null)
            {
                var remaining = until.Value - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    yield break;
                }

                linked.CancelAfter(remaining);
            }

            while (true)
            {
                Entry? next;
                try
                {
                    next = await channel.Reader.ReadAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (ChannelClosedException)
                {
                    yield break;
                }

                if (next.Sequence <= lastSequence)
                {
                    continue;
                }

                lastSequence = next.Sequence;
                if (next.Message.TimeNano > untilNanos)
                {
                    yield break;
                }

                if (next.Message.TimeNano < sinceNanos || !Matches(next.Message, filters))
                {
                    continue;
                }

                yield return next.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        }
    }

    /// <summary>Docker's <c>/events</c> filter semantics (AND across keys, OR within one key).</summary>
    public static bool Matches(EventMessage message, Filters filters)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(filters);

        if (filters.IsEmpty)
        {
            return true;
        }

        if (!filters.MatchExact("type", message.Type) ||
            !filters.MatchExact("scope", message.Scope))
        {
            return false;
        }

        if (filters.Contains("event") && !filters.MatchExact("event", message.Action))
        {
            return false;
        }

        if (filters.Contains("action") && !filters.MatchExact("action", message.Action))
        {
            return false;
        }

        if (!filters.MatchesLabels(message.Actor.Attributes))
        {
            return false;
        }

        if (filters.Contains("id") && !filters.MatchId(message.Actor.ID))
        {
            return false;
        }

        foreach (var key in ActorKeys)
        {
            if (!filters.Contains(key))
            {
                continue;
            }

            if (!string.Equals(message.Type, key, StringComparison.Ordinal))
            {
                return false;
            }

            var name = message.Actor.Attributes.GetValueOrDefault("name", "");
            var matched = filters.MatchAny(key, candidate =>
                string.Equals(candidate, name, StringComparison.Ordinal) ||
                message.Actor.ID.StartsWith(candidate, StringComparison.Ordinal));
            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] ActorKeys = ["container", "image", "network", "volume"];

    private sealed record Entry(long Sequence, EventMessage Message);
}
