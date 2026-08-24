using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Xunit;

namespace Cider.Tests.DockerApi.Json;

/// <summary>
/// The Docker wire format is source-generated. These tests are the proof that the
/// move off reflection did not move a single byte: <see cref="ReflectionOptions"/> is the
/// hand-built <c>JsonSerializerOptions</c> exactly as <c>DockerJson</c> carried it before the
/// change, and every option it set is asserted here twice — once as configuration, once as
/// observable behaviour, because a silently dropped option changes the wire format.
/// </summary>
public class SourceGeneratedJsonTests
{
    /// <summary>The pre-source-generation configuration, verbatim, resolved by reflection.</summary>
    private static readonly JsonSerializerOptions ReflectionOptions = CreateReflectionOptions();

    private static JsonSerializerOptions CreateReflectionOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new EmptyStructConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void AssertIdenticalToReflection<T>(T value) =>
        Assert.Equal(JsonSerializer.Serialize(value, ReflectionOptions), DockerJson.Serialize(value));

    // ---- the resolver is source-generated ------------------------------

    [Fact]
    public void Options_resolve_through_the_source_generated_context()
    {
        Assert.IsAssignableFrom<JsonSerializerContext>(DockerJson.Options.TypeInfoResolver);

        // The decisive difference from the reflection resolver: a type the context never declared
        // has no contract at all. Reflection would happily invent one here (and then fail at
        // runtime under AOT, where the metadata has been trimmed away).
        Assert.Throws<NotSupportedException>(() => DockerJson.TypeInfo<NotOnTheWire>());
        Assert.NotNull(JsonSerializer.Serialize(new NotOnTheWire(), ReflectionOptions));
    }

    /// <summary>A type deliberately absent from the wire context; see the test above.</summary>
    private sealed class NotOnTheWire
    {
        public string Value { get; set; } = "";
    }

    // ---- option by option ---------------------------------------------

    [Fact]
    public void PropertyNamingPolicy_stays_null_so_keys_are_verbatim_pascal_case()
    {
        Assert.Null(DockerJson.Options.PropertyNamingPolicy);
        Assert.Null(DockerJson.Options.DictionaryKeyPolicy);

        var json = DockerJson.Serialize(new ContainerCreateResponse { Id = "abc" });
        Assert.Contains("\"Id\":", json, StringComparison.Ordinal);
        Assert.Contains("\"Warnings\":", json, StringComparison.Ordinal);

        // A dictionary key is a Docker-supplied string and must survive untouched.
        var labels = DockerJson.Serialize(new GraphDriverData { Data = { ["UpperDir"] = "/x" } });
        Assert.Contains("\"UpperDir\":", labels, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultIgnoreCondition_never_writes_every_member_including_nulls()
    {
        Assert.Equal(JsonIgnoreCondition.Never, DockerJson.Options.DefaultIgnoreCondition);

        // MacAddress is a nullable member with no per-member opt-out: it must be written as null.
        var json = DockerJson.Serialize(new ContainerConfig());
        Assert.Contains("\"MacAddress\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"Entrypoint\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PerMember_ignore_conditions_still_win_over_the_global_never()
    {
        // ImageDeleteResponseItem opts out per member; the absent key must be omitted, not null.
        var untagged = DockerJson.Serialize(new ImageDeleteResponseItem { Untagged = "alpine:latest" });
        Assert.DoesNotContain("Deleted", untagged, StringComparison.Ordinal);
        AssertIdenticalToReflection(new ImageDeleteResponseItem { Untagged = "alpine:latest" });
    }

    [Fact]
    public void NumberHandling_reads_numbers_written_as_strings()
    {
        Assert.Equal(JsonNumberHandling.AllowReadingFromString, DockerJson.Options.NumberHandling);

        var parsed = DockerJson.Deserialize<HostConfig>("""{"Memory":"536870912","CpuShares":"512"}""");
        Assert.NotNull(parsed);
        Assert.Equal(536870912, parsed.Memory);
        Assert.Equal(512, parsed.CpuShares);
    }

    [Fact]
    public void PropertyNameCaseInsensitive_accepts_go_style_casing()
    {
        Assert.True(DockerJson.Options.PropertyNameCaseInsensitive);

        var parsed = DockerJson.Deserialize<ContainerCreateRequest>("""{"image":"alpine","hostConfig":{"privileged":true}}""");
        Assert.NotNull(parsed);
        Assert.Equal("alpine", parsed.Image);
        Assert.True(parsed.HostConfig?.Privileged);
    }

    [Fact]
    public void UnmappedMemberHandling_skips_keys_we_do_not_model()
    {
        Assert.Equal(JsonUnmappedMemberHandling.Skip, DockerJson.Options.UnmappedMemberHandling);

        var parsed = DockerJson.Deserialize<ContainerCreateRequest>(
            """{"Image":"alpine","SomeFutureDockerField":{"nested":[1,2,3]}}""");
        Assert.NotNull(parsed);
        Assert.Equal("alpine", parsed.Image);
    }

    [Fact]
    public void ReadCommentHandling_and_trailing_commas_stay_tolerant()
    {
        Assert.Equal(JsonCommentHandling.Skip, DockerJson.Options.ReadCommentHandling);
        Assert.True(DockerJson.Options.AllowTrailingCommas);

        var parsed = DockerJson.Deserialize<ContainerCreateRequest>("""{"Image":"alpine", /* hi */ }""");
        Assert.Equal("alpine", parsed?.Image);
    }

    [Fact]
    public void WriteIndented_stays_off()
    {
        Assert.False(DockerJson.Options.WriteIndented);
        Assert.DoesNotContain('\n', DockerJson.Serialize(new ContainerCreateResponse { Id = "abc" }));
    }

    [Fact]
    public void EmptyStructConverter_is_in_force_and_writes_an_empty_object()
    {
        Assert.Contains(DockerJson.Options.Converters, c => c is EmptyStructConverter);

        var config = new ContainerConfig();
        config.ExposedPorts["80/tcp"] = EmptyStruct.Instance;
        var json = DockerJson.Serialize(config);
        Assert.Contains("\"ExposedPorts\":{\"80/tcp\":{}}", json, StringComparison.Ordinal);

        // ... and reads any shape back, which is what makes `"ExposedPorts":{"80/tcp":null}` safe.
        var parsed = DockerJson.Deserialize<ContainerConfig>("""{"ExposedPorts":{"80/tcp":null,"53/udp":{"x":1}}}""");
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.ExposedPorts.Count);
    }

    [Fact]
    public void Encoder_emits_raw_utf8_rather_than_escapes()
    {
        Assert.Same(JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DockerJson.Options.Encoder);

        // '+' and non-ASCII show up in pull progress lines; Go's encoding/json never escapes them.
        var json = DockerJson.Serialize(new JsonMessage { Status = "Pulling fs layer +é" });
        Assert.Contains("+é", json, StringComparison.Ordinal);
    }

    // ---- byte-for-byte identity with the reflection path ---------------

    [Fact]
    public void ContainerInspectResponse_serializes_identically()
    {
        var inspect = new ContainerInspectResponse
        {
            Id = new string('a', 64),
            Created = "2026-08-24T10:00:00.000000000Z",
            Path = "/bin/sh",
            Args = ["-c", "echo hi"],
            State = new ContainerInspectState
            {
                Status = "running",
                Running = true,
                Pid = 4242,
                StartedAt = "2026-08-24T10:00:01Z",
                Health = new Health
                {
                    Status = "healthy",
                    FailingStreak = 0,
                    Log = [new HealthcheckResult { Start = "s", End = "e", ExitCode = 0, Output = "ok" }],
                },
            },
            Image = "sha256:" + new string('b', 64),
            Name = "/web",
            HostConfig = new HostConfig
            {
                Binds = ["/host:/container:ro"],
                NetworkMode = "bridge",
                PortBindings = { ["80/tcp"] = [new PortBinding { HostIp = "0.0.0.0", HostPort = "8080" }] },
                Memory = 536870912,
                Mounts = [new Mount { Type = "volume", Source = "data", Target = "/data" }],
                Ulimits = [new Ulimit { Name = "nofile", Soft = 1024, Hard = 2048 }],
            },
            Mounts = [new MountPoint { Type = "volume", Name = "data", Destination = "/data", RW = true }],
            Config = new ContainerConfig
            {
                Hostname = "web",
                Image = "alpine:latest",
                Env = ["PATH=/usr/bin"],
                Cmd = ["sh"],
                Entrypoint = null,
                Labels = { ["com.docker.compose.project"] = "demo" },
                ExposedPorts = { ["80/tcp"] = EmptyStruct.Instance },
                Healthcheck = new HealthConfig { Test = ["CMD", "true"], Interval = 1_000_000_000, Retries = 3 },
            },
            NetworkSettings = new NetworkSettings
            {
                Ports = { ["80/tcp"] = [new PortBinding { HostIp = "0.0.0.0", HostPort = "8080" }] },
                IPAddress = "192.168.64.3",
                Networks = { ["bridge"] = new EndpointSettings { IPAddress = "192.168.64.3", Aliases = ["web"] } },
            },
        };

        AssertIdenticalToReflection(inspect);
    }

    [Fact]
    public void ContainerSummary_serializes_identically()
    {
        var summary = new ContainerSummary
        {
            Id = new string('c', 64),
            Names = ["/web"],
            Image = "alpine:latest",
            ImageID = "sha256:" + new string('d', 64),
            Command = "sh",
            Created = 1_756_000_000,
            Ports = [new Port { IP = "0.0.0.0", PrivatePort = 80, PublicPort = 8080, Type = "tcp" }],
            Labels = { ["a"] = "b" },
            State = "running",
            Status = "Up 3 minutes",
            HostConfig = new SummaryHostConfig { NetworkMode = "bridge" },
            NetworkSettings = new NetworkSettingsSummary { Networks = { ["bridge"] = new EndpointSettings() } },
            Mounts = [new MountPoint { Type = "bind", Source = "/host", Destination = "/c" }],
        };

        AssertIdenticalToReflection(summary);
        AssertIdenticalToReflection(new List<ContainerSummary> { summary });
    }

    [Fact]
    public void EventMessage_serializes_identically()
    {
        AssertIdenticalToReflection(new EventMessage
        {
            Type = "container",
            Action = "start",
            Actor = new EventActor { ID = new string('e', 64), Attributes = { ["image"] = "alpine" } },
            Time = 1_756_000_000,
            TimeNano = 1_756_000_000_000_000_000,
            Status = "start",
            Id = new string('e', 64),
            From = "alpine",
        });

        // The legacy aliases are omitted, not written as null, when unset.
        AssertIdenticalToReflection(new EventMessage { Type = "network", Action = "create" });
    }

    [Fact]
    public void JsonMessage_serializes_identically_including_the_object_typed_aux()
    {
        AssertIdenticalToReflection(new JsonMessage { Stream = "Step 1/3 : FROM alpine\n" });
        AssertIdenticalToReflection(new JsonMessage
        {
            Status = "Downloading",
            Id = "abc123",
            ProgressDetail = new JsonProgress { Current = 10, Total = 100 },
            Progress = "[====>    ]",
        });
        AssertIdenticalToReflection(new JsonMessage
        {
            Error = "boom",
            ErrorDetail = new JsonError { Message = "boom" },
        });
        // Aux is declared `object?`; the runtime type has to resolve through the context too.
        AssertIdenticalToReflection(new JsonMessage { Aux = new BuildResultAux { ID = "sha256:abc" } });
    }

    [Fact]
    public void ContainerStats_serializes_identically()
    {
        AssertIdenticalToReflection(new ContainerStats
        {
            Read = "2026-08-24T10:00:00Z",
            Preread = "2026-08-24T09:59:59Z",
            PidsStats = new PidsStats { Current = 3, Limit = 0 },
            NumProcs = 3,
            CpuStats = new CpuStats
            {
                CpuUsage = new CpuUsage { TotalUsage = 1234, UsageInKernelmode = 12, UsageInUsermode = 34 },
                SystemCpuUsage = 999,
                OnlineCpus = 4,
            },
            PreCpuStats = new CpuStats(),
            MemoryStats = new MemoryStats { Usage = 100, Limit = 1000, Stats = { ["cache"] = 7 } },
            Name = "/web",
            Id = new string('f', 64),
            Networks = new Dictionary<string, NetworkStats>(StringComparer.Ordinal)
            {
                ["eth0"] = new() { RxBytes = 1, TxBytes = 2 },
            },
        });
    }

    [Fact]
    public void The_other_wire_roots_serialize_identically()
    {
        AssertIdenticalToReflection(new ErrorResponse { Message = "No such container: abc" });
        AssertIdenticalToReflection(new ImageSummary { Id = "sha256:abc", RepoTags = ["alpine:latest"], Size = 42 });
        AssertIdenticalToReflection(new ImageInspectResponse { Id = "sha256:abc", Size = 42 });
        AssertIdenticalToReflection(new VersionResponse { Version = "29.0.0", ApiVersion = "1.47" });
        AssertIdenticalToReflection(new SystemInfo { ID = "x", Name = "cider" });
        AssertIdenticalToReflection(new DiskUsage());
        AssertIdenticalToReflection(new VolumeListResponse { Volumes = [new Volume { Name = "data" }] });
        AssertIdenticalToReflection(new NetworkResource { Name = "bridge", Id = new string('1', 64) });
        AssertIdenticalToReflection(new ExecInspectResponse { ID = "abc", Running = true });
        AssertIdenticalToReflection(new ContainerWaitResponse { StatusCode = 137 });
        AssertIdenticalToReflection(new ContainerPathStat { Name = "f", Size = 3, Mode = 420 });
        AssertIdenticalToReflection(new AuthConfig { Username = "u", ServerAddress = "https://index.docker.io/v1/" });
    }

    // ---- the null-versus-empty contract --------------------------------

    [Fact]
    public void Entrypoint_keeps_null_and_empty_apart()
    {
        Assert.Null(DockerJson.Deserialize<ContainerCreateRequest>("""{"Image":"alpine"}""")?.Entrypoint);
        Assert.Null(DockerJson.Deserialize<ContainerCreateRequest>("""{"Entrypoint":null}""")?.Entrypoint);
        Assert.Empty(DockerJson.Deserialize<ContainerCreateRequest>("""{"Entrypoint":[]}""")!.Entrypoint!);

        Assert.Contains("\"Entrypoint\":null", DockerJson.Serialize(new ContainerConfig()), StringComparison.Ordinal);
        Assert.Contains("\"Entrypoint\":[]", DockerJson.Serialize(new ContainerConfig { Entrypoint = [] }), StringComparison.Ordinal);
    }

    [Fact]
    public void A_docker_compose_create_body_round_trips_with_its_explicit_nulls()
    {
        // The shapes that have broken us before: compose sends null for empty Go maps and slices.
        const string Body = """
            {
              "Hostname": "", "User": "", "AttachStdin": false, "Tty": false,
              "ExposedPorts": null, "Volumes": null, "Labels": null,
              "Env": ["FOO=bar"], "Cmd": null, "Entrypoint": null, "Image": "alpine:latest",
              "HostConfig": {
                "Binds": null, "PortBindings": null, "LogConfig": {"Type": "", "Config": null},
                "NetworkMode": "demo_default", "RestartPolicy": {"Name": "", "MaximumRetryCount": 0},
                "CapAdd": null, "CapDrop": null, "Ulimits": null, "Mounts": []
              },
              "NetworkingConfig": {"EndpointsConfig": {"demo_default": {"Aliases": ["web"], "IPAMConfig": null}}}
            }
            """;

        var request = DockerJson.Deserialize<ContainerCreateRequest>(Body);
        Assert.NotNull(request);

        // The coalescing setters: an explicit null becomes an empty collection.
        Assert.Empty(request.ExposedPorts);
        Assert.Empty(request.Volumes);
        Assert.Empty(request.Labels);
        Assert.Empty(request.HostConfig!.PortBindings);
        Assert.Empty(request.HostConfig.LogConfig.Config);

        // The members that mean something by null keep it.
        Assert.Null(request.Cmd);
        Assert.Null(request.Entrypoint);
        Assert.Null(request.HostConfig.Binds);
        Assert.Null(request.HostConfig.CapAdd);
        Assert.Null(request.HostConfig.Ulimits);
        Assert.Null(request.NetworkingConfig!.EndpointsConfig["demo_default"].IPAMConfig);

        AssertIdenticalToReflection(request);
    }

    [Fact]
    public void A_compose_network_and_volume_create_body_round_trips()
    {
        var network = DockerJson.Deserialize<NetworkCreateRequest>(
            """{"Name":"demo_default","Driver":"bridge","Options":null,"Labels":null,"IPAM":null,"CheckDuplicate":true}""");
        Assert.NotNull(network);
        Assert.Empty(network.Options);
        Assert.Empty(network.Labels);
        Assert.Null(network.IPAM);
        AssertIdenticalToReflection(network);

        var volume = DockerJson.Deserialize<VolumeCreateRequest>(
            """{"Name":"demo_data","Driver":"local","DriverOpts":null,"Labels":null}""");
        Assert.NotNull(volume);
        Assert.Empty(volume.DriverOpts);
        Assert.Empty(volume.Labels);
        AssertIdenticalToReflection(volume);
    }
}
