using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Services;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

/// <summary>
/// Proxy-mode port publishing: the engine never sees <c>-p</c>, the host port is still allocated and
/// reported, and the daemon's own publisher is driven by the container's lifecycle.
/// </summary>
public sealed class ContainerManagerPortProxyTests
{
    [Fact]
    public async Task Starting_a_container_publishes_every_binding_on_the_containers_address()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings =
                {
                    ["8080/tcp"] = [new PortBinding()],
                    ["9999/udp"] = [new PortBinding { HostIp = "127.0.0.1" }],
                },
            });

        // Nothing was handed to the engine: the daemon carries the traffic itself.
        Assert.Empty(harness.Runtime.GetSpec("web")!.Ports);

        // The TCP listeners are already bound (pending an address) before
        // AwaitStartupAndRegisterNetworkNamesAsync runs (cider-ede.18); the address itself, and the
        // UDP mapping that needs it up front, are filled in by that wait or a later poller tick, so
        // this polls rather than assume which of those supplied it.
        var address = harness.Runtime.GetContainer("web")!.Address;
        var published = await WaitUntil(
            () => harness.Publisher.LiveFor(record.Id),
            ports => ports.Count == 3 && ports.All(port => port.ContainerIp is not null));

        // The wildcard binding covers both families, as Docker's does; the explicit one does not.
        Assert.Equal(3, published.Count);
        Assert.All(published, port => Assert.Equal(IPAddress.Parse(address), port.ContainerIp));

        var tcpHostPort = int.Parse(record.Ports["8080/tcp"][0].HostPort, CultureInfo.InvariantCulture);
        var udpHostPort = int.Parse(record.Ports["9999/udp"][0].HostPort, CultureInfo.InvariantCulture);

        var tcp = published.Where(port => port.Proto == "tcp").ToList();
        Assert.Equal(2, tcp.Count);
        Assert.All(tcp, port =>
        {
            Assert.Equal(tcpHostPort, port.HostPort);
            Assert.Equal(8080, port.ContainerPort);
        });

        Assert.Contains(tcp, port => port.HostIp.Equals(IPAddress.Any));
        Assert.Contains(tcp, port => port.HostIp.Equals(IPAddress.IPv6Any));

        var udp = Assert.Single(published, port => port.Proto == "udp");
        Assert.Equal(IPAddress.Loopback, udp.HostIp);
        Assert.Equal(udpHostPort, udp.HostPort);
        Assert.Equal(9999, udp.ContainerPort);

        // ... and the bindings are still reported to Docker clients exactly as before.
        Assert.InRange(tcpHostPort, 32768, 60999);
        Assert.Equal("0.0.0.0", record.Ports["8080/tcp"][0].HostIp);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Stopping_a_container_unpublishes_its_ports()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["8080/tcp"] = [new PortBinding()] },
            });

        Assert.NotEmpty(harness.Publisher.LiveFor(record.Id));

        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, default);

        Assert.Contains(record.Id, harness.Publisher.Unpublished);
        Assert.Empty(harness.Publisher.LiveFor(record.Id));
    }

    [Fact]
    public async Task Removing_a_container_unpublishes_its_ports()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateShellAsync("sleep 30", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["8080/tcp"] = [new PortBinding()] },
            });

        await harness.Containers.StartAsync(record.Id, default);
        Assert.NotEmpty(harness.Publisher.LiveFor(record.Id));

        await harness.Containers.RemoveAsync(record.Id, force: true, removeVolumes: false, default);

        Assert.Empty(harness.Publisher.LiveFor(record.Id));
    }

    [Fact]
    public async Task A_binding_on_a_port_the_host_already_uses_fails_the_create_the_way_docker_does()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        using var taken = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        taken.Bind(new IPEndPoint(IPAddress.Any, 0));
        taken.Listen(1);
        var port = ((IPEndPoint)taken.LocalEndPoint!).Port.ToString(CultureInfo.InvariantCulture);

        var error = await Assert.ThrowsAsync<DockerApiException>(() => harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["8080/tcp"] = [new PortBinding { HostPort = port }] },
            }));

        Assert.Contains($"Bind for 0.0.0.0:{port} failed: port is already allocated", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Publisher.Published);
    }

    [Fact]
    public async Task The_state_poller_publishes_a_running_container_whose_address_arrived_late()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // The engine reports no attachment at all for the first inspects, so the start path cannot
        // learn the address. The TCP listener is bound anyway (cider-ede.18) but stays pending —
        // unresolved — until something re-inspects and finds it.
        harness.Runtime.DelayNetworkAttachment("web", 100);
        harness.Containers.NetworkPollBudget = TimeSpan.FromMilliseconds(50);
        harness.Containers.StartReturnBudget = TimeSpan.FromMilliseconds(50);

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["8080/tcp"] = [new PortBinding()] },
            });

        // The wildcard binding covers both families, so this is the listener for each of them.
        var pending = harness.Publisher.LiveFor(record.Id);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, port => Assert.Null(port.ContainerIp));

        harness.Runtime.DelayNetworkAttachment("web", 0);
        await using var poller = new StatePoller(
            harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance);
        await poller.PollOnceAsync(default);

        var resolved = harness.Publisher.LiveFor(record.Id);
        Assert.NotEmpty(resolved);
        Assert.All(resolved, port => Assert.NotNull(port.ContainerIp));

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Reconciling_at_startup_republishes_a_container_that_is_still_running()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["8080/tcp"] = [new PortBinding()] },
            });

        // A daemon restart takes every listener with it, but the container keeps running.
        harness.Publisher.Unpublish(record.Id);
        Assert.Empty(harness.Publisher.LiveFor(record.Id));

        await harness.Containers.ReconcileAsync(default);

        Assert.NotEmpty(harness.Publisher.LiveFor(record.Id));

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    /// <summary>
    /// Polls until the condition holds, so the assertion does not depend on which tick (start,
    /// network refresh, poller) supplied the address.
    /// </summary>
    private static async Task<T> WaitUntil<T>(Func<T> value, Func<T, bool> isDone)
    {
        var deadline = Environment.TickCount64 + 5000;
        T current;
        do
        {
            current = value();
            if (isDone(current))
            {
                return current;
            }

            await Task.Delay(5);
        }
        while (Environment.TickCount64 < deadline);

        Assert.Fail("timed out waiting for the condition to hold");
        return current;
    }
}
