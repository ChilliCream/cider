using Cider.Core.DockerApi;
using Xunit;

namespace Cider.Tests.DockerApi;

public class FiltersTests
{
    [Fact]
    public void Parses_the_map_encoding()
    {
        var filters = Filters.Parse("""{"label":{"com.docker.compose.project=app":true},"status":{"running":true}}""");

        Assert.Equal(["com.docker.compose.project=app"], filters.Get("label"));
        Assert.Equal(["running"], filters.Get("status"));
        Assert.True(filters.Contains("status"));
        Assert.False(filters.Contains("name"));
    }

    [Fact]
    public void Parses_the_legacy_array_encoding()
    {
        var filters = Filters.Parse("""{"label":["a=1","b"],"name":["web"]}""");

        Assert.Equal(["a=1", "b"], filters.Get("label"));
        Assert.Equal(["web"], filters.Get("name"));
    }

    [Fact]
    public void Both_encodings_produce_the_same_result()
    {
        var map = Filters.Parse("""{"label":{"a=1":true,"b":true}}""");
        var array = Filters.Parse("""{"label":["a=1","b"]}""");

        Assert.Equal(map.Get("label").Order(), array.Get("label").Order());
    }

    [Fact]
    public void Map_entries_set_to_false_are_dropped()
    {
        var filters = Filters.Parse("""{"dangling":{"true":false}}""");

        Assert.Empty(filters.Get("dangling"));
    }

    [Fact]
    public void Null_and_empty_input_yield_an_empty_filter_set()
    {
        Assert.True(Filters.Parse(null).IsEmpty);
        Assert.True(Filters.Parse("").IsEmpty);
        Assert.True(Filters.Parse("   ").IsEmpty);
        Assert.True(Filters.Parse("null").IsEmpty);
        Assert.True(Filters.Empty.IsEmpty);
    }

    [Fact]
    public void Malformed_json_is_a_400()
    {
        var error = Assert.Throws<DockerApiException>(() => Filters.Parse("{not json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);

        Assert.Throws<DockerApiException>(() => Filters.Parse("[1,2]"));
    }

    [Fact]
    public void MatchesLabels_supports_key_only_and_key_value()
    {
        var labels = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };

        Assert.True(Filters.Parse("""{"label":["a"]}""").MatchesLabels(labels));
        Assert.True(Filters.Parse("""{"label":["a=1"]}""").MatchesLabels(labels));
        Assert.False(Filters.Parse("""{"label":["a=2"]}""").MatchesLabels(labels));
        Assert.False(Filters.Parse("""{"label":["c"]}""").MatchesLabels(labels));

        // Multiple labels are AND-ed, like Docker.
        Assert.True(Filters.Parse("""{"label":["a=1","b=2"]}""").MatchesLabels(labels));
        Assert.False(Filters.Parse("""{"label":["a=1","b=3"]}""").MatchesLabels(labels));

        // No label filter matches everything.
        Assert.True(Filters.Empty.MatchesLabels(labels));
        Assert.True(Filters.Empty.MatchesLabels(null));
    }

    [Fact]
    public void MatchesLabels_honours_labelBang_negation()
    {
        // dockerd's matchLabels (daemon/prune.go) excludes an object once *every* `label!` entry
        // matches, on top of the ordinary AND-ed `label` requirement — the negation was accepted as
        // a filter key (Validate allows it) but silently had no effect on the match.
        var labels = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };

        Assert.False(Filters.Parse("""{"label!":["a"]}""").MatchesLabels(labels));
        Assert.False(Filters.Parse("""{"label!":["a=1"]}""").MatchesLabels(labels));
        Assert.True(Filters.Parse("""{"label!":["a=2"]}""").MatchesLabels(labels));
        Assert.True(Filters.Parse("""{"label!":["c"]}""").MatchesLabels(labels));

        // Multiple `label!` entries are AND-ed too: only excluded once ALL of them match.
        Assert.False(Filters.Parse("""{"label!":["a=1","b=2"]}""").MatchesLabels(labels));
        Assert.True(Filters.Parse("""{"label!":["a=1","b=3"]}""").MatchesLabels(labels));

        // `label` and `label!` combine: must satisfy the positive AND fail the negative.
        Assert.True(Filters.Parse("""{"label":["a=1"],"label!":["c"]}""").MatchesLabels(labels));
        Assert.False(Filters.Parse("""{"label":["a=1"],"label!":["b=2"]}""").MatchesLabels(labels));

        Assert.True(Filters.Empty.MatchesLabels(labels));
    }

    [Fact]
    public void ResolveUntil_returns_null_when_absent_and_parses_a_valid_value()
    {
        Assert.Null(Filters.Empty.ResolveUntil());

        var filters = Filters.Parse("""{"until":["1755770400"]}""");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_755_770_400), filters.ResolveUntil());
    }

    [Fact]
    public void ResolveUntil_rejects_an_unparseable_value_with_dockerds_wording()
    {
        // moby/daemon/prune.go's getUntilFromPruneFilters wraps the raw `timestamp.Parse` error with
        // errdefs.InvalidParameter and no extra text for containers/networks prune. Before this fix,
        // ContainerManager.PruneAsync/NetworkManager.PruneAsync silently swallowed the parse failure
        // and treated the filter as matching nothing to exclude, i.e. pruned everything.
        var filters = Filters.Parse("""{"until":["not-a-time"]}""");

        var ex = Assert.Throws<DockerApiException>(() => filters.ResolveUntil());

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal(
            "parsing time \"not-a-time\" as \"2006-01-02\": cannot parse \"not-a-time\" as \"2006\"",
            ex.Message);
    }

    [Fact]
    public void ResolveUntil_wraps_the_message_for_image_prune()
    {
        // moby/daemon/images/image_prune.go's getUntilFromPruneFilters prefixes the same raw error
        // with "invalid value for 'until' filter: ", unlike containers/networks.
        var filters = Filters.Parse("""{"until":["not-a-time"]}""");

        var ex = Assert.Throws<DockerApiException>(
            () => filters.ResolveUntil(detail => $"invalid value for 'until' filter: {detail}"));

        Assert.Equal(
            "invalid value for 'until' filter: parsing time \"not-a-time\" as \"2006-01-02\": cannot parse \"not-a-time\" as \"2006\"",
            ex.Message);
    }

    [Fact]
    public void ResolveUntil_rejects_more_than_one_value()
    {
        var filters = Filters.Parse("""{"until":["1755770400","1755770500"]}""");

        var ex = Assert.Throws<DockerApiException>(() => filters.ResolveUntil());

        Assert.Equal("more than one until filter specified", ex.Message);
    }

    [Fact]
    public void MatchExact_ors_the_values_of_one_key()
    {
        var filters = Filters.Parse("""{"status":{"running":true,"exited":true}}""");

        Assert.True(filters.MatchExact("status", "running"));
        Assert.True(filters.MatchExact("status", "exited"));
        Assert.False(filters.MatchExact("status", "created"));
        Assert.True(filters.MatchExact("health", "healthy"));
    }

    [Fact]
    public void MatchAny_uses_the_predicate()
    {
        var filters = Filters.Parse("""{"ancestor":["alpine"]}""");

        Assert.True(filters.MatchAny("ancestor", v => v.StartsWith("alp", StringComparison.Ordinal)));
        Assert.False(filters.MatchAny("ancestor", v => v == "busybox"));
        Assert.True(Filters.Empty.MatchAny("ancestor", _ => false));
    }

    [Fact]
    public void MatchName_uses_substring_semantics_and_ignores_leading_slash()
    {
        var filters = Filters.Parse("""{"name":["web"]}""");

        Assert.True(filters.MatchName("app-web-1"));
        Assert.True(filters.MatchName("/app-web-1"));
        Assert.True(Filters.Parse("""{"name":["/web"]}""").MatchName("web"));
        Assert.False(filters.MatchName("db"));
        Assert.True(Filters.Empty.MatchName("anything"));
    }

    [Fact]
    public void MatchId_uses_prefix_semantics()
    {
        var id = new string('a', 63) + "b";
        var filters = Filters.Parse($$"""{"id":["{{id[..12]}}"]}""");

        Assert.True(filters.MatchId(id));
        Assert.False(filters.MatchId(new string('c', 64)));
        Assert.True(Filters.Empty.MatchId(id));
    }

    [Fact]
    public void Validate_rejects_a_key_the_endpoint_does_not_accept()
    {
        var filters = Filters.Parse("""{"label":["a=b"],"bogus":["x"]}""");

        var ex = Assert.Throws<DockerApiException>(() => filters.Validate("label", "until"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("invalid filter 'bogus'", ex.Message);
    }

    [Fact]
    public void Validate_accepts_the_endpoints_own_keys_and_an_empty_filter_set()
    {
        Assert.Same(Filters.Empty, Filters.Empty.Validate("label"));

        var filters = Filters.Parse("""{"label":["a=b"],"until":["10m"]}""");
        Assert.Same(filters, filters.Validate("label", "label!", "until"));
    }

    [Fact]
    public void TryGetSingle_reads_the_first_value()
    {
        var filters = Filters.Parse("""{"until":["10m"]}""");

        Assert.True(filters.TryGetSingle("until", out var value));
        Assert.Equal("10m", value);
        Assert.False(filters.TryGetSingle("since", out _));
    }
}
