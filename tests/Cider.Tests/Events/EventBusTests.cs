using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.State;
using Xunit;

namespace Cider.Tests.Events;

public sealed class EventBusTests
{
    [Fact]
    public void Publish_stamps_time_and_the_legacy_aliases()
    {
        var bus = new EventBus();
        var message = DockerEvents.Container("start", Record("web"));

        bus.Publish(message);

        Assert.True(message.Time > 0);
        Assert.True(message.TimeNano > message.Time);
        Assert.Equal("start", message.Status);
        Assert.Equal(message.Actor.ID, message.Id);
        Assert.Equal("alpine:latest", message.From);
    }

    [Fact]
    public async Task Subscribe_replays_history_since_and_then_streams_live_events()
    {
        var bus = new EventBus();
        bus.Publish(DockerEvents.Container("create", Record("web")));
        bus.Publish(DockerEvents.Container("start", Record("web")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seen = new List<EventMessage>();

        var reader = Task.Run(async () =>
        {
            await foreach (var message in bus.Subscribe(Filters.Empty, DateTimeOffset.UnixEpoch, null, cts.Token))
            {
                seen.Add(message);
                if (seen.Count == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });

        await WaitUntil(() => seen.Count >= 2);
        bus.Publish(DockerEvents.Container("die", Record("web")));

        await reader;

        Assert.Equal(3, seen.Count);
        Assert.Equal(["create", "start", "die"], seen.Select(m => m.Action));
    }

    [Fact]
    public async Task Subscribe_without_since_skips_the_history()
    {
        var bus = new EventBus();
        bus.Publish(DockerEvents.Container("create", Record("web")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seen = new List<string>();
        var reader = Task.Run(async () =>
        {
            await foreach (var message in bus.Subscribe(Filters.Empty, null, null, cts.Token))
            {
                seen.Add(message.Action);
                await cts.CancelAsync();
            }
        });

        await Task.Delay(50);
        bus.Publish(DockerEvents.Container("start", Record("web")));
        await reader;

        Assert.Equal(["start"], seen);
    }

    [Fact]
    public async Task Subscribe_applies_type_event_and_label_filters()
    {
        var bus = new EventBus();
        var labelled = Record("web");
        labelled.Request.Labels["com.example.role"] = "db";

        bus.Publish(DockerEvents.Container("start", Record("other")));
        bus.Publish(DockerEvents.Container("start", labelled));
        bus.Publish(DockerEvents.Container("die", labelled));
        bus.Publish(DockerEvents.Image("pull", "sha256:abc", "alpine:latest"));

        var filters = Filters.Parse("""{"type":{"container":true},"event":{"start":true},"label":{"com.example.role=db":true}}""");
        var seen = await CollectAsync(bus, filters, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);

        Assert.Single(seen);
        Assert.Equal("start", seen[0].Action);
        Assert.Equal("web", seen[0].Actor.Attributes["name"]);
    }

    [Fact]
    public async Task Subscribe_filters_by_container_name_and_id_prefix()
    {
        var bus = new EventBus();
        var web = Record("web");
        bus.Publish(DockerEvents.Container("start", web));
        bus.Publish(DockerEvents.Container("start", Record("api")));

        var byName = await CollectAsync(bus, Filters.Parse("""{"container":{"web":true}}"""), DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        Assert.Single(byName);

        var byId = await CollectAsync(bus, Filters.Parse("{\"id\":{\"" + web.Id[..8] + "\":true}}"), DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        Assert.Single(byId);
    }

    [Fact]
    public async Task Subscribe_completes_at_until()
    {
        var bus = new EventBus();
        bus.Publish(DockerEvents.Container("create", Record("web")));

        var until = DateTimeOffset.UtcNow.AddMilliseconds(120);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var seen = new List<string>();
        await foreach (var message in bus.Subscribe(Filters.Empty, DateTimeOffset.UnixEpoch, until, cts.Token))
        {
            seen.Add(message.Action);
        }

        Assert.Equal(["create"], seen);
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(0, bus.SubscriberCount);
    }

    [Fact]
    public async Task SubscriberCount_tracks_live_subscriptions()
    {
        var bus = new EventBus();
        Assert.Equal(0, bus.SubscriberCount);

        using var cts = new CancellationTokenSource();
        var enumerator = bus.Subscribe(Filters.Empty, null, null, cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync();

        await WaitUntil(() => bus.SubscriberCount == 1);
        bus.Publish(DockerEvents.Container("start", Record("web")));
        Assert.True(await pending);

        await cts.CancelAsync();
        await enumerator.DisposeAsync();
        Assert.Equal(0, bus.SubscriberCount);
    }

    [Fact]
    public async Task History_replay_is_capped_at_the_ring_buffer_size()
    {
        var bus = new EventBus();
        for (var i = 0; i < EventBus.HistoryCapacity + 50; i++)
        {
            bus.Publish(DockerEvents.Container("start", Record("web")));
        }

        var seen = await CollectAsync(bus, Filters.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        Assert.Equal(EventBus.HistoryCapacity, seen.Count);
    }

    private static async Task<List<EventMessage>> CollectAsync(
        EventBus bus,
        Filters filters,
        DateTimeOffset? since,
        DateTimeOffset? until)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seen = new List<EventMessage>();
        await foreach (var message in bus.Subscribe(filters, since, until, cts.Token))
        {
            seen.Add(message);
        }

        return seen;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !condition())
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private static ContainerRecord Record(string name) => new()
    {
        Id = Core.Ids.DockerId.New(),
        Name = name,
        RuntimeId = name,
        Created = DateTimeOffset.UtcNow,
        ImageRef = "alpine:latest",
        Request = new ContainerCreateRequest { Image = "alpine" },
    };
}
