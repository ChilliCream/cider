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
        Assert.True(options.BuildKitEnabled);
        Assert.Null(options.BuilderCpus);
        Assert.Null(options.BuilderMemoryBytes);
        Assert.Equal("1.47", options.ApiVersion);
        Assert.Equal("1.24", options.MinApiVersion);
        Assert.Equal("29.0.0", options.EngineVersion);
        Assert.Equal("auto", options.RuntimeTransport);
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
              "dns": { "enabled": false, "listen": "127.0.0.1:5353", "upstream": ["8.8.4.4:53"] },
              "builder": { "enabled": false, "cpus": 4, "memory": 4294967296 }
            }
            """);

        var options = CiderOptions.Load(_root, socketOverride: Path.Combine(_root, "custom.sock"), logLevelOverride: "Warning");

        Assert.Equal("/opt/bin/container", options.ContainerCliPath);
        Assert.Equal(6, options.DefaultCpus);
        Assert.Equal(9, options.PollIntervalSeconds);
        Assert.False(options.DnsEnabled);
        Assert.Equal("127.0.0.1:5353", options.DnsListen);
        Assert.Equal(["8.8.4.4:53"], options.DnsUpstreams);
        Assert.False(options.BuildKitEnabled);
        Assert.Equal(4, options.BuilderCpus);
        Assert.Equal(4294967296L, options.BuilderMemoryBytes);
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

    [Theory]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void CIDER_BUILDKIT_overrides_the_file_and_the_default(string envValue, bool expectedEnabled)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, CiderOptions.ConfigFileName),
            """{ "builder": { "enabled": false } }""");

        Environment.SetEnvironmentVariable("CIDER_BUILDKIT", envValue);
        try
        {
            var options = CiderOptions.Load(_root, null, null);
            Assert.Equal(expectedEnabled, options.BuildKitEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CIDER_BUILDKIT", null);
        }
    }

    [Fact]
    public void Builder_cpus_and_memory_are_only_applied_when_positive()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, CiderOptions.ConfigFileName),
            """{ "builder": { "cpus": 0, "memory": -1 } }""");

        var options = CiderOptions.Load(_root, null, null);

        Assert.Null(options.BuilderCpus);
        Assert.Null(options.BuilderMemoryBytes);
    }

    [Fact]
    public void CIDER_RUNTIME_TRANSPORT_overrides_the_file_and_the_default()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, CiderOptions.ConfigFileName),
            """{ "runtime": { "transport": "xpc" } }""");

        Environment.SetEnvironmentVariable("CIDER_RUNTIME_TRANSPORT", "cli");
        try
        {
            var options = CiderOptions.Load(_root, null, null);
            Assert.Equal("cli", options.RuntimeTransport);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CIDER_RUNTIME_TRANSPORT", null);
        }
    }

    [Fact]
    public void The_config_files_runtime_transport_key_is_applied_without_the_env_override()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, CiderOptions.ConfigFileName),
            """{ "runtime": { "transport": "xpc" } }""");

        var options = CiderOptions.Load(_root, null, null);

        Assert.Equal("xpc", options.RuntimeTransport);
    }

    [Fact]
    public void ToJson_round_trips_the_runtime_transport()
    {
        var options = new CiderOptions { DataDir = _root, RuntimeTransport = "xpc" };

        var json = options.ToJson();

        Assert.Contains("\"transport\"", json, StringComparison.Ordinal);
        Assert.Contains("\"xpc\"", json, StringComparison.Ordinal);
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
