using System.Runtime.CompilerServices;
using System.Text.Json;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// Wire models for the apiserver's Swift <c>Codable</c> JSON blobs
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.0-2.6, §6, §8) — verbatim property names,
/// <c>Date</c> as double seconds since 2001-01-01, single-key-object enum unions,
/// required-vs-optional exactly as the Swift decoders demand. No live apiserver needed: everything
/// here is a pure <c>System.Text.Json</c> round trip against fixtures captured by the probe
/// (docs/spikes/xpc-probe/out-list.txt, out-create.txt, ref-inspect.json) and the full samples in
/// the spike doc's §8.
/// </summary>
public class WireModelTests
{
    // The Apple reference epoch (2001-01-01T00:00:00Z) that every fixture's "creationDate"/
    // "startedDate" is measured from (§2.0 rule 2).
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Fixture round trips -------------------------------------------------------------

    [Fact]
    public void Kernel_round_trips_getDefaultKernel_reply()
    {
        // docs/spikes/xpc-probe/out-create.txt: the real 253-byte getDefaultKernel reply.
        var json = LoadFixture("kernel.json");

        var kernel = XpcJson.Deserialize<Kernel>(json);

        Assert.Equal(
            "file:///Users/michael/Library/Application%20Support/com.apple.container/kernels/vmlinux-6.18.15-186",
            kernel.Path);
        Assert.Equal("linux", kernel.Platform.Os);
        Assert.Equal("arm64", kernel.Platform.Architecture);
        Assert.Null(kernel.Platform.Variant);
        Assert.Equal(["console=hvc0", "tsc=reliable", "panic=0"], kernel.CommandLine.KernelArgs);
        Assert.Empty(kernel.CommandLine.InitArgs);

        AssertRoundTripIsStable(kernel);
    }

    [Fact]
    public void ContainerSnapshot_list_round_trips_containerList_reply_sample()
    {
        // docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.2, full `containerList` reply sample.
        var json = LoadFixture("container-list-reply.json");

        var snapshots = XpcJson.Deserialize<List<ContainerSnapshot>>(json);

        var snapshot = Assert.Single(snapshots);
        var config = snapshot.Configuration;
        Assert.Equal("myapp", config.Id);
        Assert.Equal("docker.io/library/alpine:3.20", config.Image.Reference);
        Assert.Equal("sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc", config.Image.Descriptor.Digest);
        // creationDate 809330969.025174 <-> 2026-08-25T06:09:29.025174Z (task's verification sample).
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-25T06:09:29.025174Z", System.Globalization.CultureInfo.InvariantCulture),
            config.CreationDate);
        Assert.Equal(4, config.Resources.Cpus);
        Assert.Equal(1024UL * 1024 * 1024, config.Resources.MemoryInBytes);

        Assert.Equal(RuntimeStatus.Running, snapshot.Status);
        var attachment = Assert.Single(snapshot.Networks);
        Assert.Equal("default", attachment.Network);
        Assert.Equal("myapp", attachment.Hostname);
        Assert.Equal("192.168.64.2/24", attachment.Ipv4Address);
        Assert.Equal("192.168.64.1", attachment.Ipv4Gateway);
        Assert.Equal("f6:b2:c1:2d:3b:aa", attachment.MacAddress);
        Assert.Equal(AppleEpoch.AddSeconds(809331000.5), snapshot.StartedDate);

        AssertRoundTripIsStable(snapshots);
    }

    [Fact]
    public void ContainerConfiguration_round_trips_containerCreate_request_sample()
    {
        // docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.3, full `containerConfig` sample.
        var json = LoadFixture("container-create-config.json");

        var config = XpcJson.Deserialize<ContainerConfiguration>(json);

        Assert.Equal("myapp", config.Id);
        Assert.Equal(2, config.Mounts.Count);
        Assert.NotNull(config.Mounts[0].Type.Virtiofs);
        Assert.Equal("/Users/michael/data", config.Mounts[0].Source);
        Assert.Equal(["ro"], config.Mounts[0].Options);
        Assert.NotNull(config.Mounts[1].Type.Tmpfs);

        var port = Assert.Single(config.PublishedPorts);
        Assert.Equal("0.0.0.0", port.HostAddress);
        Assert.Equal((ushort)8080, port.HostPort);
        Assert.Equal((ushort)80, port.ContainerPort);
        Assert.Equal("tcp", port.Proto);
        Assert.Equal((ushort)1, port.Count);

        Assert.NotNull(config.Dns);
        Assert.Equal(["1.1.1.1"], config.Dns!.Nameservers);
        Assert.Equal("test", config.Dns.Domain);

        AssertRoundTripIsStable(config);
    }

    [Fact]
    public void ContainerSnapshot_round_trips_fixture_derived_from_ref_inspect()
    {
        // docs/spikes/xpc-probe/ref-inspect.json is the CLI's *display* JSON (ISO-8601 dates, a
        // nested `status` object): converted here to the real wire shape (double creationDate,
        // top-level `status` string, top-level `networks`) per the task's "note inspect prints
        // ISO dates: convert" instruction.
        var json = LoadFixture("container-snapshot-from-inspect.json");

        var snapshot = XpcJson.Deserialize<ContainerSnapshot>(json);

        Assert.Equal("xpcprobe-ref", snapshot.Configuration.Id);
        Assert.Equal(RuntimeStatus.Stopped, snapshot.Status);
        Assert.Empty(snapshot.Networks);
        Assert.Null(snapshot.StartedDate);
        // ref-inspect.json: "creationDate" : "2026-08-25T09:54:03Z"
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-25T09:54:03Z", System.Globalization.CultureInfo.InvariantCulture),
            snapshot.Configuration.CreationDate);
        Assert.Equal(User.OfId(0, 0).Id!.Uid, snapshot.Configuration.InitProcess.User.Id!.Uid);

        AssertRoundTripIsStable(snapshot);
    }

    // ---- Single-key-object union round trips (§2.0 rule 3) --------------------------------

    [Theory]
    [InlineData("{\"virtiofs\":{}}")]
    [InlineData("{\"tmpfs\":{}}")]
    public void FsType_payload_free_cases_round_trip(string json)
    {
        // Verification sample: `{"type":{"virtiofs":{}}}` is Filesystem.type's value — this is
        // that value in isolation.
        var fsType = XpcJson.Deserialize<FsType>(json);
        Assert.Equal(json, XpcJson.Serialize(fsType));
    }

    [Fact]
    public void FsType_block_case_round_trips()
    {
        const string json = "{\"block\":{\"format\":\"ext4\",\"cache\":{\"on\":{}},\"sync\":{\"fsync\":{}}}}";

        var fsType = XpcJson.Deserialize<FsType>(json);

        Assert.NotNull(fsType.Block);
        Assert.Equal("ext4", fsType.Block!.Format);
        Assert.Equal("on", fsType.Block.Cache.CaseName);
        Assert.Equal("fsync", fsType.Block.Sync.CaseName);
        Assert.Equal(json, XpcJson.Serialize(fsType));
    }

    [Fact]
    public void FsType_volume_case_round_trips()
    {
        const string json = "{\"volume\":{\"name\":\"myvol\",\"format\":\"ext4\",\"cache\":{\"on\":{}},\"sync\":{\"fsync\":{}}}}";

        var fsType = XpcJson.Deserialize<FsType>(json);

        Assert.NotNull(fsType.Volume);
        Assert.Equal("myvol", fsType.Volume!.Name);
        Assert.Equal(json, XpcJson.Serialize(fsType));
    }

    [Fact]
    public void User_raw_case_round_trips()
    {
        // Verification sample, verbatim.
        const string json = "{\"raw\":{\"userString\":\"65532:65532\"}}";

        var user = XpcJson.Deserialize<User>(json);

        Assert.Equal("65532:65532", user.Raw!.UserString);
        Assert.Null(user.Id);
        Assert.Equal(json, XpcJson.Serialize(user));
    }

    [Fact]
    public void User_id_case_round_trips()
    {
        const string json = "{\"id\":{\"uid\":0,\"gid\":0}}";

        var user = XpcJson.Deserialize<User>(json);

        Assert.Equal(0, user.Id!.Uid);
        Assert.Equal(0, user.Id.Gid);
        Assert.Null(user.Raw);
        Assert.Equal(json, XpcJson.Serialize(user));
    }

    [Fact]
    public void Union_type_rejects_more_than_one_key()
    {
        var ex = Assert.Throws<JsonException>(() => XpcJson.Deserialize<FsType>("{\"virtiofs\":{},\"tmpfs\":{}}"));
        Assert.Contains("exactly one key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Legacy-key aliases the custom `init(from:)` decoders accept -----------------------

    [Fact]
    public void Attachment_accepts_legacy_address_and_gateway_keys()
    {
        // Attachment.swift:66-75 accepts `address`/`gateway` as aliases for
        // ipv4Address/ipv4Gateway (§2.2).
        const string json = "{\"network\":\"default\",\"hostname\":\"myapp\",\"address\":\"192.168.64.2/24\",\"gateway\":\"192.168.64.1\"}";

        var attachment = XpcJson.Deserialize<Attachment>(json);

        Assert.Equal("192.168.64.2/24", attachment.Ipv4Address);
        Assert.Equal("192.168.64.1", attachment.Ipv4Gateway);

        // Encoding always uses the canonical keys, not the legacy aliases.
        var reEncoded = XpcJson.Serialize(attachment);
        Assert.Contains("\"ipv4Address\":\"192.168.64.2/24\"", reEncoded, StringComparison.Ordinal);
        Assert.Contains("\"ipv4Gateway\":\"192.168.64.1\"", reEncoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\"address\"", reEncoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gateway\"", reEncoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachment_missing_network_or_hostname_throws()
    {
        Assert.Throws<JsonException>(() => XpcJson.Deserialize<Attachment>("{\"hostname\":\"myapp\"}"));
        Assert.Throws<JsonException>(() => XpcJson.Deserialize<Attachment>("{\"network\":\"default\"}"));
    }

    [Fact]
    public void NetworkConfiguration_accepts_id_and_subnet_aliases()
    {
        // NetworkConfiguration.swift:74-105 accepts `id` as an alias for `name` and `subnet` as an
        // alias for `ipv4Subnet` (§2.2).
        const string json = "{\"id\":\"default\",\"mode\":\"nat\",\"subnet\":\"192.168.64.0/24\"}";

        var config = XpcJson.Deserialize<NetworkConfiguration>(json);

        Assert.Equal("default", config.Name);
        Assert.Equal("nat", config.Mode);
        Assert.Equal("192.168.64.0/24", config.Ipv4Subnet);

        var reEncoded = XpcJson.Serialize(config);
        Assert.Contains("\"name\":\"default\"", reEncoded, StringComparison.Ordinal);
        Assert.Contains("\"ipv4Subnet\":\"192.168.64.0/24\"", reEncoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", reEncoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\"subnet\"", reEncoded, StringComparison.Ordinal);
    }

    // ---- Required-vs-optional exactly as the Swift decoders demand (§2.0 rule 11) ---------

    [Fact]
    public void ContainerListFilters_empty_object_fails_ids_and_labels_are_required()
    {
        // §8.11 gotcha 8: `{}` must fail even though both fields are collections.
        var ex = Assert.Throws<JsonException>(() => XpcJson.Deserialize<ContainerListFilters>("{}"));
        Assert.Contains("ids", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerListFilters_all_round_trips_empty_ids_and_labels()
    {
        const string json = "{\"ids\":[],\"labels\":{}}";

        var filters = XpcJson.Deserialize<ContainerListFilters>(json);

        Assert.Empty(filters.Ids);
        Assert.Empty(filters.Labels);
        Assert.Null(filters.Status);
        AssertRoundTripIsStable(filters);
    }

    [Fact]
    public void ProcessConfiguration_missing_required_field_throws_JsonException_naming_the_field()
    {
        // All 8 fields are required on decode (§2.0 rule 11) — drop `environment`.
        const string json = """
            {"executable":"sleep","arguments":["60"],"workingDirectory":"/","terminal":false,
             "user":{"id":{"uid":0,"gid":0}},"supplementalGroups":[],"rlimits":[]}
            """;

        var ex = Assert.Throws<JsonException>(() => XpcJson.Deserialize<ProcessConfiguration>(json));
        Assert.Contains("Environment", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerStopOptions_requires_timeoutInSeconds()
    {
        Assert.Throws<JsonException>(() => XpcJson.Deserialize<ContainerStopOptions>("{}"));

        var options = XpcJson.Deserialize<ContainerStopOptions>("{\"timeoutInSeconds\":5,\"signal\":\"SIGTERM\"}");
        Assert.Equal(5, options.TimeoutInSeconds);
        Assert.Equal("SIGTERM", options.Signal);
        AssertRoundTripIsStable(options);
    }

    [Fact]
    public void ContainerStopOptions_default_is_5_seconds_daemon_chosen_signal()
    {
        Assert.Equal(5, ContainerStopOptions.Default.TimeoutInSeconds);
        Assert.Null(ContainerStopOptions.Default.Signal);
    }

    [Fact]
    public void ContainerCreateOptions_requires_autoRemove()
    {
        Assert.Throws<JsonException>(() => XpcJson.Deserialize<ContainerCreateOptions>("{}"));

        var options = XpcJson.Deserialize<ContainerCreateOptions>("{\"autoRemove\":false}");
        Assert.False(options.AutoRemove);
        Assert.Null(options.RootFsOverride);
        AssertRoundTripIsStable(options);
    }

    [Fact]
    public void AttachmentConfiguration_requires_network_and_hostname()
    {
        Assert.Throws<JsonException>(() => XpcJson.Deserialize<AttachmentConfiguration>("{}"));
        Assert.Throws<JsonException>(() =>
            XpcJson.Deserialize<AttachmentConfiguration>("{\"network\":\"default\",\"options\":{}}"));

        var config = XpcJson.Deserialize<AttachmentConfiguration>(
            "{\"network\":\"default\",\"options\":{\"hostname\":\"myapp\"}}");
        Assert.Equal("default", config.Network);
        Assert.Equal("myapp", config.Options.Hostname);
        AssertRoundTripIsStable(config);
    }

    [Fact]
    public void DnsConfiguration_requires_nameservers_searchDomains_and_options()
    {
        Assert.Throws<JsonException>(() => XpcJson.Deserialize<DnsConfiguration>("{}"));

        var dns = XpcJson.Deserialize<DnsConfiguration>("{\"nameservers\":[\"1.1.1.1\"],\"searchDomains\":[],\"options\":[]}");
        Assert.Equal(["1.1.1.1"], dns.Nameservers);
        Assert.Null(dns.Domain);
        AssertRoundTripIsStable(dns);
    }

    // ---- ContainerConfiguration.Defaults() reproduces the CLI's own defaults (§2.2) --------

    [Fact]
    public void ContainerConfiguration_Defaults_matches_the_CLI_defaults()
    {
        var image = new ImageDescription
        {
            Reference = "docker.io/library/alpine:3.20",
            Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = "sha256:abc", Size = 1 },
        };
        var initProcess = new ProcessConfiguration
        {
            Executable = "/bin/sh",
            Arguments = [],
            Environment = [],
            WorkingDirectory = "/",
            Terminal = false,
            User = User.OfId(0, 0),
            SupplementalGroups = [],
            Rlimits = [],
        };

        var config = ContainerConfiguration.Defaults("myapp", image, initProcess);

        Assert.Equal(4, config.Resources.Cpus);
        Assert.Equal(1024UL * 1024 * 1024, config.Resources.MemoryInBytes);
        Assert.Equal(1, config.Resources.CpuOverhead);
        Assert.Equal("container-runtime-linux", config.RuntimeHandler);
        Assert.Equal("linux", config.Platform.Os);
        Assert.NotNull(config.Dns);
        Assert.Equal(["1.1.1.1"], config.Dns!.Nameservers);

        AssertRoundTripIsStable(config);
    }

    // ---- Remaining structs the task's fix direction names explicitly ----------------------

    [Fact]
    public void ContainerStats_round_trips_a_partial_sample()
    {
        // Id required, every measurement optional — a stopped/just-created container may have no
        // samples yet (§2.2).
        const string json = "{\"id\":\"myapp\",\"memoryUsageBytes\":1234}";

        var stats = XpcJson.Deserialize<ContainerStats>(json);

        Assert.Equal("myapp", stats.Id);
        Assert.Equal(1234UL, stats.MemoryUsageBytes);
        Assert.Null(stats.CpuUsageUsec);
        AssertRoundTripIsStable(stats);
    }

    [Fact]
    public void DiskUsageStats_round_trips_a_sample()
    {
        const string json = """
            {"images":{"total":3,"active":2,"sizeInBytes":100,"reclaimable":10},
             "containers":{"total":1,"active":1,"sizeInBytes":50,"reclaimable":0},
             "volumes":{"total":0,"active":0,"sizeInBytes":0,"reclaimable":0}}
            """;

        var stats = XpcJson.Deserialize<DiskUsageStats>(json);

        Assert.Equal(3, stats.Images.Total);
        Assert.Equal(10UL, stats.Images.Reclaimable);
        AssertRoundTripIsStable(stats);
    }

    [Fact]
    public void VolumeConfiguration_round_trips_a_sample()
    {
        const string json = """
            {"name":"myvol","driver":"local","format":"ext4","source":"/path",
             "creationDate":809330969.025174,"labels":{},"options":{}}
            """;

        var volume = XpcJson.Deserialize<VolumeConfiguration>(json);

        Assert.Equal("myvol", volume.Name);
        Assert.Equal("local", volume.Driver);
        Assert.Null(volume.SizeInBytes);
        AssertRoundTripIsStable(volume);
    }

    [Fact]
    public void VolumeResource_decode_ignores_the_id_key()
    {
        const string json = """
            {"id":"vol-id","configuration":{"name":"myvol","driver":"local","format":"ext4",
             "source":"/path","creationDate":809330969.025174,"labels":{},"options":{}}}
            """;

        var resource = XpcJson.Deserialize<VolumeResource>(json);

        Assert.Equal("myvol", resource.Configuration.Name);
    }

    [Fact]
    public void NetworkResource_round_trips_a_sample_and_decode_ignores_the_id_key()
    {
        const string json = """
            {"id":"net-id","configuration":{"name":"default","creationDate":809330969.025174,
             "mode":"nat","ipv4Subnet":"192.168.64.0/24","labels":{},"options":{}},
             "status":{"ipv4Subnet":"192.168.64.0/24","ipv4Gateway":"192.168.64.1"}}
            """;

        var resource = XpcJson.Deserialize<NetworkResource>(json);

        Assert.Equal("default", resource.Configuration.Name);
        Assert.Equal("192.168.64.0/24", resource.Status.Ipv4Subnet);
        Assert.Equal("192.168.64.1", resource.Status.Ipv4Gateway);
        AssertRoundTripIsStable(resource);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>Loads a checked-in wire-JSON fixture, resolved relative to this source file so no
    /// project-file changes are needed to ship it alongside the test (matches the convention in
    /// Services/ImageManagerTests.cs's LoadMacOsTarFixture).</summary>
    private static string LoadFixture(string name, [CallerFilePath] string sourcePath = "")
    {
        var fixturePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "Fixtures", "xpc", name);
        return File.ReadAllText(fixturePath);
    }

    /// <summary>The task's round-trip requirement: deserialize → serialize → deserialize → serialize
    /// again, and the two serializations must be identical — i.e. nothing is lost or altered by a
    /// full trip through our wire model, whatever the fixture's own original formatting was.</summary>
    private static void AssertRoundTripIsStable<T>(T value)
    {
        var jsonA = XpcJson.Serialize(value);
        var roundTripped = XpcJson.Deserialize<T>(jsonA);
        var jsonB = XpcJson.Serialize(roundTripped);
        Assert.Equal(jsonA, jsonB);
    }
}
