using System.Text.Json;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// <c>POST /networks/{id}/connect</c> and <c>/disconnect</c> over the daemon's real socket: Apple
/// <c>container</c> fixes a container's networks at create time, so both work only while the
/// container has never been started and answer 501 afterwards.
/// </summary>
public sealed class NetworkConnectRoutesTests
{
    [Fact]
    public async Task Connect_before_the_first_start_attaches_the_network()
    {
        await using var host = await DaemonTestHost.StartAsync();
        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net"}""")).Status);
        var id = await CreateContainerAsync(host, "nc-c1");

        var (status, body) = await host.PostJsonAsync("/networks/nc-net/connect", """{"Container":"nc-c1"}""");

        Assert.Equal(200, status);
        Assert.Equal("", body);

        var networks = await NetworksOfAsync(host, id);
        Assert.Equal(["bridge", "nc-net"], networks.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("", networks.GetProperty("nc-net").GetProperty("IPAddress").GetString());

        // ... and the addresses arrive on both networks once it starts. Address registration is a
        // detached follow-up of Start (cider-ede.26), so poll instead of asserting the first read.
        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);
        var started = await NetworksOfAsync(host, id);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (started.EnumerateObject().All(
                    network => !string.IsNullOrEmpty(network.Value.GetProperty("IPAddress").GetString())))
            {
                break;
            }

            await Task.Delay(50);
            started = await NetworksOfAsync(host, id);
        }

        foreach (var network in started.EnumerateObject())
        {
            Assert.False(
                string.IsNullOrEmpty(network.Value.GetProperty("IPAddress").GetString()),
                $"network {network.Name} has no address");
        }

        // A running container cannot be re-created, so its networks are frozen.
        var (disconnectStatus, disconnectBody) = await host.PostJsonAsync(
            "/networks/nc-net/disconnect", """{"Container":"nc-c1"}""");
        Assert.Equal(501, disconnectStatus);
        Assert.Contains("disconnecting a running container", disconnectBody, StringComparison.Ordinal);

        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net1b"}""")).Status);
        var (connectStatus, connectBody) = await host.PostJsonAsync(
            "/networks/nc-net1b/connect", """{"Container":"nc-c1"}""");
        Assert.Equal(501, connectStatus);
        Assert.Contains("connecting a running container", connectBody, StringComparison.Ordinal);

        // ... but a network it is ALREADY attached to answers dockerd's terminal 403, never the
        // retryable-looking 501 (cider-qj4: Aspire's DCP re-POSTs connect for the container it
        // created on that very network and retries a 501 forever).
        var (attachedStatus, attachedBody) = await host.PostJsonAsync(
            "/networks/bridge/connect", """{"Container":"nc-c1"}""");
        Assert.Equal(403, attachedStatus);
        Assert.Contains(
            "endpoint with name nc-c1 already exists in network bridge",
            attachedBody,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// cider-qj4: Aspire's DCP POSTs <c>/networks/{id}/connect</c> for a container it already created
    /// through cider on that very network. dockerd answers 403 "endpoint with name ... already exists
    /// in network ..." (moby daemon/libnetwork/network.go createEndpoint, types.ForbiddenErrorf),
    /// which DCP treats as terminal; the old 501 looked transient and was retried every ~8s forever.
    /// </summary>
    [Fact]
    public async Task Connect_of_an_attached_running_container_answers_dockerds_403()
    {
        await using var host = await DaemonTestHost.StartAsync();
        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net5"}""")).Status);
        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net5b"}""")).Status);
        var id = await CreateContainerAsync(host, "nc-c5");
        Assert.Equal(200, (await host.PostJsonAsync("/networks/nc-net5/connect", """{"Container":"nc-c5"}""")).Status);
        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);

        var (attachedStatus, attachedBody) = await host.PostJsonAsync(
            "/networks/nc-net5/connect", """{"Container":"nc-c5"}""");
        Assert.Equal(403, attachedStatus);
        Assert.Contains(
            "endpoint with name nc-c5 already exists in network nc-net5",
            attachedBody,
            StringComparison.Ordinal);

        // A network the running container is NOT attached to still hits the Apple limitation.
        var (notAttachedStatus, notAttachedBody) = await host.PostJsonAsync(
            "/networks/nc-net5b/connect", """{"Container":"nc-c5"}""");
        Assert.Equal(501, notAttachedStatus);
        Assert.Contains("connecting a running container", notAttachedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disconnect_before_the_first_start_detaches_the_network()
    {
        await using var host = await DaemonTestHost.StartAsync();
        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net2"}""")).Status);
        var id = await CreateContainerAsync(host, "nc-c2");
        Assert.Equal(200, (await host.PostJsonAsync("/networks/nc-net2/connect", """{"Container":"nc-c2"}""")).Status);

        var (status, _) = await host.PostJsonAsync("/networks/nc-net2/disconnect", """{"Container":"nc-c2"}""");

        Assert.Equal(200, status);
        var networks = await NetworksOfAsync(host, id);
        Assert.Equal(["bridge"], networks.EnumerateObject().Select(property => property.Name).ToArray());

        // The network has no endpoints left, so it can be removed again.
        Assert.Equal(204, await host.DeleteAsync("/networks/nc-net2"));
    }

    [Fact]
    public async Task Connect_reports_dockers_status_codes_for_bad_input()
    {
        await using var host = await DaemonTestHost.StartAsync();
        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net3"}""")).Status);
        await CreateContainerAsync(host, "nc-c3");

        var (unknownNetwork, unknownNetworkBody) = await host.PostJsonAsync(
            "/networks/nc-ghost/connect", """{"Container":"nc-c3"}""");
        Assert.Equal(404, unknownNetwork);
        Assert.Contains("network nc-ghost not found", unknownNetworkBody, StringComparison.Ordinal);

        var (unknownContainer, unknownContainerBody) = await host.PostJsonAsync(
            "/networks/nc-net3/connect", """{"Container":"nc-ghost"}""");
        Assert.Equal(404, unknownContainer);
        Assert.Contains("No such container: nc-ghost", unknownContainerBody, StringComparison.Ordinal);

        var (noContainer, _) = await host.PostJsonAsync("/networks/nc-net3/connect", "{}");
        Assert.Equal(400, noContainer);

        Assert.Equal(200, (await host.PostJsonAsync("/networks/nc-net3/connect", """{"Container":"nc-c3"}""")).Status);

        var (duplicate, duplicateBody) = await host.PostJsonAsync(
            "/networks/nc-net3/connect", """{"Container":"nc-c3"}""");
        Assert.Equal(403, duplicate);
        Assert.Contains(
            "endpoint with name nc-c3 already exists in network nc-net3",
            duplicateBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disconnect_reports_dockers_status_codes_for_bad_input()
    {
        await using var host = await DaemonTestHost.StartAsync();
        Assert.Equal(201, (await host.PostJsonAsync("/networks/create", """{"Name":"nc-net4"}""")).Status);
        var id = await CreateContainerAsync(host, "nc-c4");

        var (notConnected, notConnectedBody) = await host.PostJsonAsync(
            "/networks/nc-net4/disconnect", """{"Container":"nc-c4"}""");
        Assert.Equal(403, notConnected);
        Assert.Contains($"container {id} is not connected to network nc-net4", notConnectedBody, StringComparison.Ordinal);

        // Apple always attaches a container to at least one network, so the last one cannot go.
        var (lastNetwork, lastNetworkBody) = await host.PostJsonAsync(
            "/networks/bridge/disconnect", """{"Container":"nc-c4"}""");
        Assert.Equal(501, lastNetwork);
        Assert.Contains("at least one network", lastNetworkBody, StringComparison.Ordinal);
    }

    private static async Task<string> CreateContainerAsync(DaemonTestHost host, string name)
    {
        var (status, body) = await host.PostJsonAsync(
            $"/v1.47/containers/create?name={name}",
            """{"Image":"alpine","Cmd":["sleep","30"]}""");

        Assert.Equal(201, status);
        return JsonDocument.Parse(body).RootElement.GetProperty("Id").GetString()!;
    }

    private static async Task<JsonElement> NetworksOfAsync(DaemonTestHost host, string id)
    {
        var (status, body) = await host.GetAsync($"/containers/{id}/json");
        Assert.Equal(200, status);
        return JsonDocument.Parse(body).RootElement
            .GetProperty("NetworkSettings")
            .GetProperty("Networks")
            .Clone();
    }
}
