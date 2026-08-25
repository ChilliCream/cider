using Cider.AppleContainer.Xpc;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="ProgressUpdateDecoder"/>'s progress-key → <see cref="Cider.Core.Runtime.ProgressEvent"/>
/// mapping (task cider-ede.10, docs/spikes/xpc/02-apiserver-xpc-protocol.md §5's update-key table) —
/// pure, in-process <see cref="XpcDictionary"/> round trips, no live apiserver and no
/// <see cref="ProgressUpdateListener"/> connection involved, the same shape
/// <c>XpcDictionaryTests</c> already uses.
/// </summary>
public class ProgressUpdateListenerTests
{
    [Fact]
    public void SetDescription_emits_one_status_event_and_is_remembered_for_later_events()
    {
        var decoder = new ProgressUpdateDecoder();
        using var dict = new XpcDictionary();
        dict.SetString(ProgressUpdateDecoder.SetDescription, "Fetching image");

        var events = decoder.Decode(dict);

        var evt = Assert.Single(events);
        Assert.Equal("Fetching image", evt.Status);
        Assert.Null(evt.Current);
        Assert.Null(evt.Total);
    }

    [Fact]
    public void A_zero_valued_int64_key_is_treated_as_absent()
    {
        var decoder = new ProgressUpdateDecoder();
        using var dict = new XpcDictionary();
        dict.SetInt64(ProgressUpdateDecoder.SetItems, 0);

        var events = decoder.Decode(dict);

        Assert.Empty(events);
    }

    [Fact]
    public void SetItems_and_setTotalItems_on_one_message_produce_two_events_sharing_the_running_totals()
    {
        var decoder = new ProgressUpdateDecoder();
        using var dict = new XpcDictionary();
        dict.SetInt64(ProgressUpdateDecoder.SetItems, 20);
        dict.SetInt64(ProgressUpdateDecoder.SetTotalItems, 56);

        var events = decoder.Decode(dict);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(20, e.Current));
        Assert.All(events, e => Assert.Equal(56, e.Total));
    }

    [Fact]
    public void AddItems_accumulates_across_messages()
    {
        var decoder = new ProgressUpdateDecoder();

        using (var first = new XpcDictionary())
        {
            first.SetInt64(ProgressUpdateDecoder.AddItems, 3);
            var firstEvents = decoder.Decode(first);
            Assert.Equal(3, Assert.Single(firstEvents).Current);
        }

        using var second = new XpcDictionary();
        second.SetInt64(ProgressUpdateDecoder.AddItems, 4);
        var secondEvents = decoder.Decode(second);

        Assert.Equal(7, Assert.Single(secondEvents).Current);
    }

    [Fact]
    public void SetTasks_overwrites_rather_than_accumulates()
    {
        var decoder = new ProgressUpdateDecoder();

        using (var first = new XpcDictionary())
        {
            first.SetInt64(ProgressUpdateDecoder.AddTasks, 5);
            decoder.Decode(first);
        }

        using var second = new XpcDictionary();
        second.SetInt64(ProgressUpdateDecoder.SetTasks, 2);
        var events = decoder.Decode(second);

        Assert.Equal(2, Assert.Single(events).Current);
    }

    [Fact]
    public void Total_is_null_when_no_total_has_ever_been_reported()
    {
        var decoder = new ProgressUpdateDecoder();
        using var dict = new XpcDictionary();
        dict.SetInt64(ProgressUpdateDecoder.AddSize, 1024);

        var events = decoder.Decode(dict);

        var evt = Assert.Single(events);
        Assert.Equal(1024, evt.Current);
        Assert.Null(evt.Total);
    }

    [Fact]
    public void SetItemsName_carries_forward_as_the_Id_of_later_events_but_emits_no_event_of_its_own()
    {
        var decoder = new ProgressUpdateDecoder();

        using (var first = new XpcDictionary())
        {
            first.SetString(ProgressUpdateDecoder.SetItemsName, "blobs");
            Assert.Empty(decoder.Decode(first));
        }

        using var second = new XpcDictionary();
        second.SetInt64(ProgressUpdateDecoder.SetItems, 1);
        var events = decoder.Decode(second);

        Assert.Equal("blobs", Assert.Single(events).Id);
    }

    [Fact]
    public void A_message_with_every_key_present_produces_one_event_per_key()
    {
        var decoder = new ProgressUpdateDecoder();
        using var dict = new XpcDictionary();
        dict.SetString(ProgressUpdateDecoder.SetDescription, "Fetching image");
        dict.SetString(ProgressUpdateDecoder.SetSubDescription, "alpine:3.20");
        dict.SetString(ProgressUpdateDecoder.SetItemsName, "blobs");
        dict.SetInt64(ProgressUpdateDecoder.SetItems, 1);
        dict.SetInt64(ProgressUpdateDecoder.SetTotalItems, 2);

        var events = decoder.Decode(dict);

        // description + subDescription + items + totalItems = 4 events (itemsName carries no event
        // of its own — see the test above).
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public void An_empty_message_produces_no_events()
    {
        var decoder = new ProgressUpdateDecoder();
        using var dict = new XpcDictionary();

        Assert.Empty(decoder.Decode(dict));
    }
}
