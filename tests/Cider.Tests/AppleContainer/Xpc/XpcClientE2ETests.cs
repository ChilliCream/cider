using Cider.AppleContainer.Xpc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// Live checks against the real <c>com.apple.container.apiserver</c> on this machine — everything
/// the task's verification section calls for. Runs only with <c>CIDER_E2E=1</c>
/// (<see cref="E2EFactAttribute"/>, defined for the whole AppleContainer suite in
/// <c>AppleContainerRuntimeE2ETests.cs</c>).
///
/// Read-only by construction: every route used here (<c>ping</c>, <c>containerList</c>, and one
/// deliberately unknown route) never creates, deletes, or otherwise mutates anything, so this
/// suite is safe to run against the user's live apiserver and its already-running containers.
/// </summary>
[Collection("apple-container-e2e")]
public class XpcClientE2ETests
{
    /// <summary>docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.1.</summary>
    private const string ApiServerService = "com.apple.container.apiserver";

    private static XpcClient NewClient() => new(ApiServerService, NullLogger.Instance);

    [E2EFact]
    public async Task Ping_returns_the_six_apiserver_identity_strings()
    {
        using var client = NewClient();
        using var reply = await client.SendAsync(new XpcMessage("ping"), XpcCallOptions.Default);

        // docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.6: appRoot, installRoot, apiServerVersion,
        // apiServerCommit, apiServerBuild, apiServerAppName are unconditional; logRoot is optional.
        foreach (var key in new[]
                 {
                     "appRoot", "installRoot", "apiServerVersion", "apiServerCommit", "apiServerBuild",
                     "apiServerAppName",
                 })
        {
            var value = reply.GetString(key);
            Assert.False(string.IsNullOrEmpty(value), $"expected a non-empty '{key}' in the ping reply");
        }

        Assert.StartsWith("container-apiserver version 1.", reply.GetString("apiServerVersion"), StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task Hundred_pings_have_a_median_round_trip_under_0_2ms()
    {
        using var client = NewClient();

        // One warm-up call so connection setup isn't counted against the budget.
        (await client.SendAsync(new XpcMessage("ping"), XpcCallOptions.Default)).Dispose();

        var samples = new List<double>(100);
        for (var i = 0; i < 100; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var reply = await client.SendAsync(new XpcMessage("ping"), XpcCallOptions.Default);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        Assert.True(median < 0.2, $"median ping round trip was {median:F4} ms, expected < 0.2 ms");
    }

    [E2EFact]
    public async Task ContainerList_with_the_all_filter_decodes()
    {
        using var client = NewClient();
        using var request = new XpcMessage("containerList");
        // §2.1: ContainerListFilters has no custom decoder — ids/labels are both required, `{}`
        // fails to decode. `{"ids":[],"labels":{}}` is `.all`.
        request.SetData("listFilters", "{\"ids\":[],\"labels\":{}}"u8);

        using var reply = await client.SendAsync(request, XpcCallOptions.List);

        var containers = reply.GetData("containers");
        Assert.NotNull(containers);

        using var doc = System.Text.Json.JsonDocument.Parse(containers);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [E2EFact]
    public async Task Unknown_route_surfaces_an_XpcException_promptly_instead_of_hanging()
    {
        using var client = NewClient();

        // §1.6: an unregistered route gets no reply at all — libxpc reports the destroyed reply
        // port as "interrupted" almost immediately, not a hang the caller's own timeout must save
        // it from. This asserts that promptness directly, with a generous budget only as a backstop.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<XpcException>(() =>
            client.SendAsync(new XpcMessage("thisRouteDoesNotExist"), XpcCallOptions.Default, cts.Token));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"unknown route took {sw.Elapsed} to surface");
        Assert.Equal(XpcErrorClass.Transport, ex.ErrorClass);
    }

    [E2EFact]
    public async Task Cancelling_the_connection_reconnects_transparently_on_the_next_send()
    {
        using var client = NewClient();

        // Establish the connection.
        (await client.SendAsync(new XpcMessage("ping"), XpcCallOptions.Default)).Dispose();

        // Simulate the apiserver-restart scenario the task calls for: cancel the connection out
        // from under the client without disposing the client itself.
        client.DebugCancelConnection();

        // Give the async event handler a moment to observe the cancellation before sending again —
        // proves the *next send* recovers regardless of whether it wins or loses that race, since
        // SendSync also marks the connection broken reactively on an "error" reply.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using var reply = await client.SendAsync(new XpcMessage("ping"), XpcCallOptions.Default);
        Assert.False(string.IsNullOrEmpty(reply.GetString("apiServerVersion")));
    }
}
