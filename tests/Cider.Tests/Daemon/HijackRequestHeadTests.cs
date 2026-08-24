using Cider.Daemon.Hosting;
using Xunit;

namespace Cider.Tests.Daemon;

public sealed class HijackRequestHeadTests
{
    private const string ExecHead =
        "POST /v1.47/exec/abc123/start HTTP/1.1\r\n" +
        "Host: docker\r\n" +
        "User-Agent: Docker-Client/29.4.0 (darwin)\r\n" +
        "Content-Length: 42\r\n" +
        "Content-Type: application/json\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: tcp\r\n";

    [Fact]
    public void Recognizes_an_exec_start_upgrade()
    {
        var head = HijackRequestHead.TryParse(ExecHead);

        Assert.NotNull(head);
        Assert.Equal(HijackKind.ExecStart, head.Kind);
        Assert.Equal("abc123", head.Id);
        Assert.Equal(42, head.ContentLength);
        Assert.True(head.Upgrade);
    }

    [Fact]
    public void Recognizes_a_container_attach_upgrade()
    {
        var head = HijackRequestHead.TryParse(
            "POST /v1.47/containers/deadbeef/attach?stderr=1&stdout=1&stream=1 HTTP/1.1\r\n" +
            "Connection: Upgrade\r\nUpgrade: tcp\r\nContent-Length: 0\r\n");

        Assert.NotNull(head);
        Assert.Equal(HijackKind.ContainerAttach, head.Kind);
        Assert.Equal("deadbeef", head.Id);
        Assert.Equal("stderr=1&stdout=1&stream=1", head.Query);
        Assert.Equal(0, head.ContentLength);
        Assert.True(head.Upgrade);
    }

    [Fact]
    public void Recognizes_an_unprefixed_path()
    {
        var head = HijackRequestHead.TryParse("POST /exec/deadbeef/start HTTP/1.1\r\nUpgrade: tcp\r\nContent-Length: 7\r\n");

        Assert.NotNull(head);
        Assert.Equal("deadbeef", head.Id);
        Assert.Equal(7, head.ContentLength);
    }

    [Fact]
    public void Tolerates_bare_lf_line_endings()
    {
        var head = HijackRequestHead.TryParse("POST /v1.47/exec/x1/start HTTP/1.1\nUpgrade: TCP\nContent-Length: 3\n");

        Assert.NotNull(head);
        Assert.Equal("x1", head.Id);
        Assert.Equal(3, head.ContentLength);
        Assert.True(head.Upgrade);
    }

    [Fact]
    public void Reports_a_missing_upgrade_header()
    {
        var head = HijackRequestHead.TryParse("POST /exec/x1/start HTTP/1.1\r\nContent-Length: 3\r\n");

        Assert.NotNull(head);
        Assert.False(head.Upgrade);
        Assert.Equal(3, head.ContentLength);
    }

    [Fact]
    public void Defaults_the_content_length_to_zero()
    {
        var head = HijackRequestHead.TryParse("POST /exec/x1/start HTTP/1.1\r\nUpgrade: tcp\r\n");

        Assert.NotNull(head);
        Assert.Equal(0, head.ContentLength);
    }

    [Theory]
    [InlineData("GET /exec/x1/start HTTP/1.1\r\nUpgrade: tcp\r\n")]
    [InlineData("POST /v1.47/exec/x1/json HTTP/1.1\r\nUpgrade: tcp\r\n")]
    [InlineData("POST /v1.47/exec/x1/attach HTTP/1.1\r\nUpgrade: tcp\r\n")]
    [InlineData("POST /v1.47/containers/x1/start HTTP/1.1\r\nUpgrade: tcp\r\n")]
    [InlineData("POST /v1.47/containers/create HTTP/1.1\r\n")]
    [InlineData("")]
    public void Ignores_everything_else(string head) =>
        Assert.Null(HijackRequestHead.TryParse(head));

    [Fact]
    public void Accepts_a_query_string_on_exec_start()
    {
        var head = HijackRequestHead.TryParse("POST /v1.47/exec/x1/start?foo=bar HTTP/1.1\r\nUpgrade: tcp\r\n");

        Assert.NotNull(head);
        Assert.Equal("x1", head.Id);
        Assert.Equal("foo=bar", head.Query);
    }
}
