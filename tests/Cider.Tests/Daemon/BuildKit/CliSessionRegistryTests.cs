using System.IO.Pipelines;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="CliSessionRegistry"/> in isolation, driven with synthetic duplex pipes so no real
/// hijacked connection or BuildKit client is needed.
/// </summary>
public sealed class CliSessionRegistryTests
{
    [Fact]
    public async Task Register_makes_the_session_visible_via_TryGet()
    {
        var registry = new CliSessionRegistry();
        await using var session = NewSession("s1");

        registry.Register(session);

        Assert.True(registry.TryGet("s1", out var found));
        Assert.Same(session, found);

        registry.Unregister("s1");
    }

    [Fact]
    public void TryGet_fails_for_an_unknown_id()
    {
        var registry = new CliSessionRegistry();
        Assert.False(registry.TryGet("nope", out var found));
        Assert.Null(found);
    }

    [Fact]
    public async Task Register_rejects_a_duplicate_id()
    {
        var registry = new CliSessionRegistry();
        await using var first = NewSession("dup");
        await using var second = NewSession("dup");

        registry.Register(first);

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(second));
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);

        registry.Unregister("dup");
    }

    [Fact]
    public async Task Unregister_removes_the_session_and_completes_Closed()
    {
        var registry = new CliSessionRegistry();
        await using var session = NewSession("s2");
        registry.Register(session);

        registry.Unregister("s2");

        Assert.False(registry.TryGet("s2", out _));
        await session.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Unregister_of_an_unknown_id_is_a_no_op()
    {
        var registry = new CliSessionRegistry();
        registry.Unregister("never-registered");
    }

    [Fact]
    public async Task WaitAsync_resolves_immediately_for_an_already_registered_session()
    {
        var registry = new CliSessionRegistry();
        await using var session = NewSession("s3");
        registry.Register(session);

        var found = await registry.WaitAsync("s3", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Same(session, found);
        registry.Unregister("s3");
    }

    [Fact]
    public async Task WaitAsync_resolves_once_the_session_registers_later()
    {
        var registry = new CliSessionRegistry();
        var waitTask = registry.WaitAsync("late", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(waitTask.IsCompleted);

        await using var session = NewSession("late");
        registry.Register(session);

        var found = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(session, found);

        registry.Unregister("late");
    }

    [Fact]
    public async Task WaitAsync_times_out_when_nothing_ever_registers()
    {
        var registry = new CliSessionRegistry();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.WaitAsync("never", TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public async Task RegisterFromStream_builds_and_registers_a_session()
    {
        var registry = new CliSessionRegistry();
        var (server, client) = CreateDuplexPair();
        await using var clientDisposable = client;

        var session = registry.RegisterFromStream("stream-1", "key", ["/a/A"], server);

        Assert.True(registry.TryGet("stream-1", out var found));
        Assert.Same(session, found);
        Assert.Equal("key", session.SharedKey);
        Assert.Contains("/a/a", session.Methods);

        registry.Unregister("stream-1");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task RegisterFromStream_rejects_a_duplicate_id()
    {
        var registry = new CliSessionRegistry();
        var (server1, client1) = CreateDuplexPair();
        var (server2, client2) = CreateDuplexPair();
        await using var c1 = client1;
        await using var c2 = client2;

        var session = registry.RegisterFromStream("dup-stream", null, [], server1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.FromResult(registry.RegisterFromStream("dup-stream", null, [], server2)));

        registry.Unregister("dup-stream");
        await session.DisposeAsync();
    }

    private static CliSession NewSession(string id)
    {
        var (server, _) = CreateDuplexPair();
        return new CliSession(id, "shared", ["/a/A"], server);
    }

    private static (DuplexStream Server, DuplexStream Client) CreateDuplexPair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        var client = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        return (server, client);
    }
}
