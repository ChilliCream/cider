using System.Text.Json;
using System.Text.Json.Serialization;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Xunit;

namespace Cider.Tests.DockerApi.Models;

/// <summary>Guards the exact wire shapes Docker clients parse.</summary>
public class DtoShapeTests
{
    private static JsonElement Roundtrip<T>(T value) =>
        JsonDocument.Parse(DockerJson.Serialize(value)).RootElement.Clone();

    [Fact]
    public void VersionResponse_uses_pascal_case_keys()
    {
        var json = DockerJson.Serialize(new VersionResponse
        {
            Version = "29.0.0",
            ApiVersion = "1.47",
            MinAPIVersion = "1.24",
            Os = "linux",
            Arch = "arm64",
            Platform = new PlatformInfo { Name = "cider (Apple container 1.2.2)" },
            Components =
            [
                new ComponentVersion { Name = "Engine", Version = "29.0.0" },
            ],
        });

        Assert.Contains("\"Version\":\"29.0.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ApiVersion\":\"1.47\"", json, StringComparison.Ordinal);
        Assert.Contains("\"MinAPIVersion\":\"1.24\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Components\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"Platform\":{\"Name\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"version\":", json, StringComparison.Ordinal);

        var root = Roundtrip(new VersionResponse { Version = "29.0.0" });
        Assert.True(root.TryGetProperty("Version", out _));
        Assert.True(root.TryGetProperty("ApiVersion", out _));
        Assert.True(root.TryGetProperty("Components", out _));
    }

    [Fact]
    public void ComponentVersion_details_carry_KernelVersion_and_string_Experimental()
    {
        var component = new ComponentVersion
        {
            Name = "Engine",
            Details = new Dictionary<string, string>
            {
                ["KernelVersion"] = "6.18.15",
                ["Experimental"] = "false",
            },
        };

        var json = DockerJson.Serialize(component);

        Assert.Contains("\"KernelVersion\":\"6.18.15\"", json, StringComparison.Ordinal);
        // Details is Docker's map[string]string: Experimental is the literal string "false", not a bool.
        Assert.Contains("\"Experimental\":\"false\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointSettings_serializes_GwPriority_as_a_number()
    {
        var json = DockerJson.Serialize(new EndpointSettings { GwPriority = 0 });

        Assert.Contains("\"GwPriority\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemInfo_runtimes_use_lowercase_path()
    {
        var info = new SystemInfo();
        info.Runtimes["apple-container"] = new RuntimeInfo { Path = "container" };

        var json = DockerJson.Serialize(info);

        Assert.Contains("\"Runtimes\":{\"apple-container\":{\"path\":\"container\"", json, StringComparison.Ordinal);
        Assert.Contains("\"OSType\":\"linux\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Architecture\":\"aarch64\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorResponse_uses_lowercase_message()
    {
        var json = DockerJson.Serialize(new ErrorResponse { Message = "No such container: abc" });
        Assert.Equal("{\"message\":\"No such container: abc\"}", json);
    }

    [Fact]
    public void AuthConfig_uses_lowercase_keys()
    {
        var parsed = DockerJson.Deserialize<AuthConfig>(
            """{"username":"u","password":"p","serveraddress":"https://index.docker.io/v1/","email":"e@x"}""");

        Assert.NotNull(parsed);
        Assert.Equal("u", parsed.Username);
        Assert.Equal("p", parsed.Password);
        Assert.Equal("https://index.docker.io/v1/", parsed.ServerAddress);
        Assert.Contains("\"serveraddress\":", DockerJson.Serialize(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void EventMessage_uses_lowercase_scope_time_timeNano()
    {
        var message = new EventMessage
        {
            Type = "container",
            Action = "start",
            Actor = new EventActor { ID = new string('a', 64), Attributes = { ["name"] = "web" } },
            Scope = "local",
            Time = 1_755_770_400,
            TimeNano = 1_755_770_400_123_456_700,
            Status = "start",
            Id = new string('a', 64),
            From = "alpine",
        };

        var json = DockerJson.Serialize(message);

        Assert.Contains("\"Type\":\"container\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Action\":\"start\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Actor\":{\"ID\":", json, StringComparison.Ordinal);
        Assert.Contains("\"scope\":\"local\"", json, StringComparison.Ordinal);
        Assert.Contains("\"time\":1755770400", json, StringComparison.Ordinal);
        Assert.Contains("\"timeNano\":1755770400123456700", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"start\"", json, StringComparison.Ordinal);
        Assert.Contains("\"from\":\"alpine\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EventMessage_omits_legacy_fields_when_unset()
    {
        var json = DockerJson.Serialize(new EventMessage { Type = "network", Action = "create" });

        Assert.DoesNotContain("\"status\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"from\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecInspectResponse_uses_uppercase_ID_and_lowercase_ProcessConfig()
    {
        var json = DockerJson.Serialize(new ExecInspectResponse
        {
            ID = "deadbeef",
            Running = true,
            ContainerID = "abc",
            ProcessConfig = new ProcessConfig
            {
                Entrypoint = "sh",
                Arguments = ["-c", "echo hi"],
                Tty = true,
                User = "root",
            },
        });

        Assert.Contains("\"ID\":\"deadbeef\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ContainerID\":\"abc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"privileged\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"entrypoint\":\"sh\"", json, StringComparison.Ordinal);
        Assert.Contains("\"arguments\":[\"-c\",\"echo hi\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"tty\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerStats_uses_snake_case_including_nested_objects()
    {
        var stats = new ContainerStats
        {
            Read = "2026-08-21T10:00:00.123456700Z",
            Name = "/web",
            Id = new string('a', 64),
            CpuStats = new CpuStats
            {
                CpuUsage = new CpuUsage { TotalUsage = 1234, UsageInKernelmode = 12, UsageInUsermode = 34 },
                SystemCpuUsage = 99,
                OnlineCpus = 4,
                ThrottlingData = new ThrottlingData { Periods = 1 },
            },
            MemoryStats = new MemoryStats { Usage = 100, MaxUsage = 200, Limit = 300 },
            PidsStats = new PidsStats { Current = 3 },
            Networks = new Dictionary<string, NetworkStats> { ["eth0"] = new() { RxBytes = 10, TxBytes = 20 } },
        };

        var json = DockerJson.Serialize(stats);

        Assert.Contains("\"read\":", json, StringComparison.Ordinal);
        Assert.Contains("\"preread\":", json, StringComparison.Ordinal);
        Assert.Contains("\"pids_stats\":{\"current\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"cpu_stats\":{\"cpu_usage\":{\"total_usage\":1234", json, StringComparison.Ordinal);
        Assert.Contains("\"usage_in_kernelmode\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"system_cpu_usage\":99", json, StringComparison.Ordinal);
        Assert.Contains("\"online_cpus\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"throttling_data\":{\"periods\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"precpu_stats\":", json, StringComparison.Ordinal);
        Assert.Contains("\"memory_stats\":{\"usage\":100", json, StringComparison.Ordinal);
        Assert.Contains("\"blkio_stats\":", json, StringComparison.Ordinal);
        Assert.Contains("\"networks\":{\"eth0\":{\"rx_bytes\":10", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerPathStat_uses_lowercase_keys()
    {
        var json = DockerJson.Serialize(new ContainerPathStat
        {
            Name = "etc",
            Size = 4096,
            Mode = 0x800001ED,
            Mtime = "2026-08-21T10:00:00.123456700Z",
            LinkTarget = "",
        });

        Assert.Contains("\"name\":\"etc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"size\":4096", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":", json, StringComparison.Ordinal);
        Assert.Contains("\"mtime\":", json, StringComparison.Ordinal);
        Assert.Contains("\"linkTarget\":\"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonMessage_uses_lowercase_keys_and_omits_nulls()
    {
        var json = DockerJson.Serialize(new JsonMessage
        {
            Status = "Downloading",
            Id = "layer1",
            ProgressDetail = new JsonProgress { Current = 1, Total = 2 },
            Progress = "[==>   ] 1B/2B",
        });

        Assert.Equal(
            """{"status":"Downloading","progressDetail":{"current":1,"total":2},"progress":"[==>   ] 1B/2B","id":"layer1"}""",
            json);

        var buildLine = DockerJson.Serialize(new JsonMessage { Stream = "Step 1/3 : FROM alpine\n" });
        Assert.Equal("""{"stream":"Step 1/3 : FROM alpine\n"}""", buildLine);

        var errorLine = DockerJson.Serialize(new JsonMessage
        {
            Error = "boom",
            ErrorDetail = new JsonError { Message = "boom" },
        });
        Assert.Equal("""{"error":"boom","errorDetail":{"message":"boom"}}""", errorLine);

        var auxLine = DockerJson.Serialize(new JsonMessage { Aux = new BuildResultAux { ID = "sha256:abc" } });
        Assert.Equal("""{"aux":{"ID":"sha256:abc"}}""", auxLine);
    }

    [Fact]
    public void ImageDeleteResponseItem_omits_the_absent_member()
    {
        var untagged = DockerJson.Serialize(new ImageDeleteResponseItem { Untagged = "zq5tmp:probe" });
        var deleted = DockerJson.Serialize(new ImageDeleteResponseItem { Deleted = "sha256:abc" });

        // Go's omitempty: exactly one key per entry, never "Deleted":null — docker-py's
        // RemoveImageTest::test_remove is an exact dict-membership test.
        Assert.Equal("""{"Untagged":"zq5tmp:probe"}""", untagged);
        Assert.Equal("""{"Deleted":"sha256:abc"}""", deleted);
    }

    [Fact]
    public void ImageVirtualSize_is_omitted_when_unset_and_written_when_set()
    {
        var modernSummary = DockerJson.Serialize(new ImageSummary { Size = 42 });
        var legacySummary = DockerJson.Serialize(new ImageSummary { Size = 42, VirtualSize = 42 });
        var modernInspect = DockerJson.Serialize(new ImageInspectResponse { Size = 42 });
        var legacyInspect = DockerJson.Serialize(new ImageInspectResponse { Size = 42, VirtualSize = 42 });

        Assert.DoesNotContain("VirtualSize", modernSummary, StringComparison.Ordinal);
        Assert.Contains("\"VirtualSize\":42", legacySummary, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualSize", modernInspect, StringComparison.Ordinal);
        Assert.Contains("\"VirtualSize\":42", legacyInspect, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerJson_default_ignore_condition_stays_never()
    {
        // The VirtualSize/Deleted omissions above are per-member opt-outs; flipping the global
        // default would reshape every response (docs/ARCHITECTURE.md §4).
        Assert.Equal(JsonIgnoreCondition.Never, DockerJson.Options.DefaultIgnoreCondition);
        Assert.Contains("\"SharedSize\":-1", DockerJson.Serialize(new ImageSummary()), StringComparison.Ordinal);
    }

    [Fact]
    public void ImageSearchItem_uses_snake_case()
    {
        var json = DockerJson.Serialize(new ImageSearchItem { Name = "alpine", IsOfficial = true, StarCount = 7 });

        Assert.Contains("\"is_official\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"star_count\":7", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStruct_serializes_as_empty_object()
    {
        var config = new ContainerConfig();
        config.ExposedPorts["80/tcp"] = EmptyStruct.Instance;
        config.Volumes["/data"] = EmptyStruct.Instance;

        var json = DockerJson.Serialize(config);

        Assert.Contains("\"ExposedPorts\":{\"80/tcp\":{}}", json, StringComparison.Ordinal);
        Assert.Contains("\"Volumes\":{\"/data\":{}}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeListResponse_has_Volumes_and_Warnings()
    {
        var json = DockerJson.Serialize(new VolumeListResponse
        {
            Volumes = [new Volume { Name = "data", Mountpoint = "/x" }],
        });

        Assert.Contains("\"Volumes\":[{\"Name\":\"data\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Warnings\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_may_arrive_as_strings()
    {
        var parsed = DockerJson.Deserialize<ContainerWaitResponse>("""{"StatusCode":"137"}""");

        Assert.NotNull(parsed);
        Assert.Equal(137, parsed.StatusCode);
    }

    [Fact]
    public void Property_names_are_matched_case_insensitively()
    {
        var parsed = DockerJson.Deserialize<ExecStartRequest>("""{"detach":false,"tty":true}""");

        Assert.NotNull(parsed);
        Assert.True(parsed.Tty);
        Assert.False(parsed.Detach);
    }

    [Fact]
    public void Unmapped_members_are_skipped()
    {
        var parsed = DockerJson.Deserialize<VolumeCreateRequest>(
            """{"Name":"data","SomethingFromTheFuture":{"a":1}}""");

        Assert.NotNull(parsed);
        Assert.Equal("data", parsed.Name);
    }
}
