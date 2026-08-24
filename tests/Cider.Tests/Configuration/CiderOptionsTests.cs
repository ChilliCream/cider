using Cider.Core.Configuration;
using Xunit;

namespace Cider.Tests.Configuration;

public sealed class CiderOptionsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cider-tests", Guid.NewGuid().ToString("n")[..12]);

    [Fact]
    public void Defaults_match_the_contract()
    {
        var options = new CiderOptions();

        Assert.EndsWith("/.cider", options.DataDir, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(options.DataDir, "docker.sock"), options.SocketPath);
        Assert.Equal("container", options.ContainerCliPath);
        Assert.Equal(2, options.DefaultCpus);
        Assert.Equal(2L * 1024 * 1024 * 1024, options.DefaultMemoryBytes);
        Assert.True(options.DnsEnabled);
        Assert.Equal("0.0.0.0:10053", options.DnsListen);
        Assert.Equal("docker.io/coredns/coredns:1.14.7", options.DnsForwarderImage);
        Assert.Equal(3, options.PollIntervalSeconds);
        Assert.Equal(64L * 1024 * 1024, options.LogMaxBytes);
        Assert.Equal("1.47", options.ApiVersion);
        Assert.Equal("1.24", options.MinApiVersion);
        Assert.Equal("29.0.0", options.EngineVersion);
    }

    [Fact]
    public void Derived_directories_hang_off_the_data_dir()
    {
        var options = new CiderOptions { DataDir = _root };

        Assert.Equal(Path.Combine(_root, "state"), options.StateDir);
        Assert.Equal(Path.Combine(_root, "logs"), options.LogsDir);
        Assert.Equal(Path.Combine(_root, "volumes"), options.VolumesDir);
        Assert.Equal(Path.Combine(_root, "tmp"), options.TmpDir);
    }

    [Fact]
    public void Load_reads_the_config_file_then_applies_the_overrides_and_creates_the_directories()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, CiderOptions.ConfigFileName),
            """
            {
              "containerCliPath": "/opt/bin/container",
              "defaultCpus": 6,
              "pollIntervalSeconds": 9,
              "logLevel": "Debug",
              "dns": { "enabled": false, "listen": "127.0.0.1:5353", "upstream": ["8.8.4.4:53"] }
            }
            """);

        var options = CiderOptions.Load(_root, socketOverride: Path.Combine(_root, "custom.sock"), logLevelOverride: "Warning");

        Assert.Equal("/opt/bin/container", options.ContainerCliPath);
        Assert.Equal(6, options.DefaultCpus);
        Assert.Equal(9, options.PollIntervalSeconds);
        Assert.False(options.DnsEnabled);
        Assert.Equal("127.0.0.1:5353", options.DnsListen);
        Assert.Equal(["8.8.4.4:53"], options.DnsUpstreams);
        Assert.Equal("Warning", options.LogLevel);
        Assert.Equal(Path.Combine(_root, "custom.sock"), options.SocketPath);

        Assert.True(Directory.Exists(options.StateDir));
        Assert.True(Directory.Exists(options.LogsDir));
        Assert.True(Directory.Exists(options.VolumesDir));
        Assert.True(Directory.Exists(options.TmpDir));
    }

    [Fact]
    public void A_broken_config_file_does_not_stop_the_daemon()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, CiderOptions.ConfigFileName), "{ not json");

        var options = CiderOptions.Load(_root, null, null);

        Assert.Equal("container", options.ContainerCliPath);
    }

    [Fact]
    public void An_over_long_socket_path_is_rejected()
    {
        var options = new CiderOptions
        {
            DataDir = _root,
            SocketPath = "/" + new string('x', CiderOptions.MaxSocketPathLength),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
