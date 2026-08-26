using System.Globalization;
using System.Net;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Tests.Fakes;
using Xunit;

namespace Cider.Tests.Services;

public sealed class ContainerManagerCreateTests
{
    private const string EntrypointImage = "with-entrypoint";

    [Theory]
    // request entrypoint, request cmd, expected argv (image = alpine: no entrypoint, cmd ["/bin/sh"])
    [InlineData(null, null, "/bin/sh")]
    [InlineData(null, "echo|hi", "echo|hi")]
    [InlineData("/init", null, "/init")]
    [InlineData("/init", "echo|hi", "/init|echo|hi")]
    // Docker.DotNet/Testcontainers send Entrypoint:[] and Cmd:[] rather than omitting the fields
    // (the ryuk container's own create request has this exact shape) — an empty request Cmd must
    // inherit the image's Cmd exactly like a null one does, or the merged argv is empty.
    [InlineData("", "", "/bin/sh")]
    public async Task Entrypoint_and_cmd_merge_for_an_image_without_an_entrypoint(string? entrypoint, string? cmd, string expected)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", configure: request =>
        {
            request.Entrypoint = Split(entrypoint);
            request.Cmd = Split(cmd);
        });

        Assert.Equal(expected.Split('|'), Argv(record));
    }

    [Theory]
    // image entrypoint ["/entry"], image cmd ["default"]
    [InlineData(null, null, "/entry|default")]
    [InlineData(null, "run", "/entry|run")]
    [InlineData("/other", null, "/other")]
    [InlineData("/other", "run", "/other|run")]
    [InlineData("", "run", "run")]
    [InlineData("", null, "default")]
    public async Task Entrypoint_and_cmd_merge_for_an_image_with_an_entrypoint(string? entrypoint, string? cmd, string expected)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        SeedEntrypointImage(harness);

        var record = await harness.CreateAsync(EntrypointImage, configure: request =>
        {
            // "" means "explicitly cleared" (Docker's `--entrypoint ""`), null means "not sent".
            request.Entrypoint = entrypoint is null ? null : Split(entrypoint);
            request.Cmd = Split(cmd);
        });

        Assert.Equal(expected.Split('|'), Argv(record));
    }

    [Fact]
    public async Task Create_stores_the_resolved_config_and_emits_a_create_event()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();

        var record = await harness.CreateAsync("alpine", "web", request => request.Env = ["FOO=bar"]);

        await events.WaitForAsync("create");

        Assert.Equal("web", record.Name);
        Assert.Equal(64, record.Id.Length);
        Assert.Equal("alpine:latest", record.ImageRef);
        Assert.Equal("created", record.State.Status);
        Assert.Contains("FOO=bar", record.Request.Env!);
        // The image environment is merged underneath the request's.
        Assert.Contains(record.Request.Env!, entry => entry.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal(record.Id[..12], record.Request.Hostname);

        var created = events.First("create");
        Assert.Equal(record.Id, created.Actor.ID);
        Assert.Equal("web", created.Actor.Attributes["name"]);
        Assert.Equal("alpine:latest", created.Actor.Attributes["image"]);
    }

    // ---- fast-create hot path (cider-ede.17) -------------------------------------------------

    [Fact]
    public async Task Create_DoesNotListContainers()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        await harness.CreateAsync("alpine", "web");

        // The record store is authoritative for Docker names, and the runtime's own "exists" error
        // (mapped to Conflict) covers a runtime-side name collision — a create never needs to list
        // the engine's containers first.
        Assert.DoesNotContain(harness.Runtime.Calls, c => c.StartsWith("ListContainersAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_ForAnAlreadyResolvedImage_MakesNoFurtherImageRuntimeCalls()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        await harness.CreateAsync("alpine", "first");
        var imageCallsAfterFirst = CountImageRuntimeCalls(harness.Runtime);
        Assert.True(imageCallsAfterFirst > 0, "the first create should have resolved the image against the runtime");

        await harness.CreateAsync("alpine", "second");

        Assert.Equal(imageCallsAfterFirst, CountImageRuntimeCalls(harness.Runtime));
    }

    private static int CountImageRuntimeCalls(FakeContainerRuntime runtime) =>
        runtime.Calls.Count(c =>
            c.StartsWith("InspectImageAsync:", StringComparison.Ordinal) ||
            c.StartsWith("ListImagesAsync", StringComparison.Ordinal));

    [Fact]
    public async Task HostConfig_Sysctls_and_an_explicit_stop_signal_land_on_the_engine_spec()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web", request =>
        {
            request.StopSignal = "SIGINT";
            request.HostConfig = new HostConfig
            {
                Sysctls = new Dictionary<string, string> { ["net.core.somaxconn"] = "1024" },
            };
        });

        var spec = harness.Runtime.GetSpec("web");

        Assert.NotNull(spec);
        Assert.Equal("1024", spec.Sysctls["net.core.somaxconn"]);
        Assert.Equal("SIGINT", spec.StopSignal);
        Assert.Equal("SIGINT", record.StopSignal);
    }

    [Fact]
    public async Task An_unset_stop_signal_falls_back_to_the_images_config_on_the_engine_spec()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.SeedImage(new RuntimeImageDetail
        {
            Id = "sha256:" + new string('f', 64),
            References = ["docker.io/library/with-stopsignal:latest"],
            Size = 100,
            Created = DateTimeOffset.UnixEpoch,
            Architecture = "arm64",
            Os = "linux",
            Config = new ImageConfig { StopSignal = "SIGQUIT" },
        });

        var record = await harness.CreateAsync("with-stopsignal", "quitter");
        var spec = harness.Runtime.GetSpec("quitter");

        Assert.NotNull(spec);
        Assert.Empty(spec.Sysctls);
        Assert.Equal("SIGQUIT", spec.StopSignal);
        Assert.Equal("SIGQUIT", record.StopSignal);
    }

    [Fact]
    public async Task Create_uses_the_name_as_the_engine_id_and_stamps_the_identity_labels()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web");
        var spec = harness.Runtime.GetSpec("web");

        Assert.Equal("web", record.RuntimeId);
        Assert.NotNull(spec);
        Assert.Equal(record.Id, spec.Labels["com.chillicream.cider.id"]);
        Assert.Equal("web", spec.Labels["com.chillicream.cider.name"]);
    }

    [Fact]
    public async Task A_duplicate_name_is_a_409_with_Dockers_wording()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var first = await harness.CreateAsync("alpine", "web");

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(new ContainerCreateRequest { Image = "alpine" }, "web", null, default));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, error.Status);
        Assert.Contains("""The container name "/web" is already in use by container""", error.Message, StringComparison.Ordinal);
        Assert.Contains(first.Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_name_is_a_400()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(new ContainerCreateRequest { Image = "alpine" }, "no spaces", null, default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Contains("Invalid container name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("container:other")]
    public async Task Unsupported_network_modes_are_400(string mode)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(
                new ContainerCreateRequest { Image = "alpine", HostConfig = new HostConfig { NetworkMode = mode } },
                null,
                null,
                default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Equal($"cider: network mode '{mode}' is not supported by Apple container", error.Message);
    }

    // ---- network_mode: none (cider-ede.35) -------------------------------------------------------
    // The XPC transport can express "no attachments" (ContainerConfigurationBuilder.BuildNetworks
    // maps an empty Networks list to `[]`, shipped by cider-ede.6); the CLI transport cannot, since
    // omitting `--network` attaches the default network rather than none. "host" and "container:*"
    // stay unsupported on both transports (Unsupported_network_modes_are_400, above).

    [Fact]
    public async Task Network_mode_none_is_still_a_400_on_the_CLI_transport()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        Assert.False(harness.Runtime.IsXpcTransport);

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(
                new ContainerCreateRequest { Image = "alpine", HostConfig = new HostConfig { NetworkMode = "none" } },
                null,
                null,
                default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Equal("cider: network mode 'none' is not supported by Apple container", error.Message);
    }

    [Fact]
    public async Task Network_mode_none_succeeds_on_the_XPC_transport_with_no_network_attachments()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.IsXpcTransport = true;

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig { NetworkMode = "none" });

        Assert.Empty(record.Networks);
        Assert.Empty(harness.Runtime.GetSpec("web")!.Networks);
    }

    [Fact]
    public async Task Network_mode_none_combined_with_NetworkingConfig_endpoints_is_a_400()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.IsXpcTransport = true;

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(
                new ContainerCreateRequest
                {
                    Image = "alpine",
                    HostConfig = new HostConfig { NetworkMode = "none" },
                    NetworkingConfig = new NetworkingConfig
                    {
                        EndpointsConfig = { ["proj_default"] = new EndpointSettings() },
                    },
                },
                null,
                null,
                default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Equal("cider: NetworkingConfig endpoints cannot be combined with network mode 'none'", error.Message);
    }

    /// <summary>
    /// Daemon-level coverage for the create-through-inspect path a fixture-level unit test cannot
    /// reach on its own (cider-ede.35 closing audit, finding 3): starting a zero-network container
    /// must not trip <c>ApplyNetworkInfo</c>/<c>BuildNetworkSettings</c>, and inspect's
    /// <c>NetworkSettings</c> must come back with no attachments and no address.
    /// </summary>
    [Fact]
    public async Task Network_mode_none_starts_and_inspects_with_no_attachments_and_no_address()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.IsXpcTransport = true;

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig { NetworkMode = "none" });

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        var detail = await harness.Containers.InspectAsync(record.Id, size: false, CancellationToken.None);

        Assert.Empty(detail.NetworkSettings.Networks);
        Assert.Equal("", detail.NetworkSettings.IPAddress);
        Assert.Equal("", detail.NetworkSettings.Gateway);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("container:other")]
    public async Task Host_and_container_network_modes_are_still_400_on_the_XPC_transport(string mode)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.IsXpcTransport = true;

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(
                new ContainerCreateRequest { Image = "alpine", HostConfig = new HostConfig { NetworkMode = mode } },
                null,
                null,
                default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Equal($"cider: network mode '{mode}' is not supported by Apple container", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("bridge")]
    public async Task The_default_network_modes_map_to_the_bridge_network(string mode)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig { NetworkMode = mode });

        Assert.True(record.Networks.ContainsKey("bridge"));
        Assert.Equal(["default"], harness.Runtime.GetSpec("web")!.Networks);
    }

    [Fact]
    public async Task An_empty_HostPort_allocates_an_ephemeral_host_port()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["80/tcp"] = [new PortBinding { HostIp = "127.0.0.1", HostPort = "" }] },
            });

        var binding = Assert.Single(record.Ports["80/tcp"]);
        var port = int.Parse(binding.HostPort);
        Assert.InRange(port, PortAllocatorRange.Min, PortAllocatorRange.Max);
        Assert.Equal("127.0.0.1", binding.HostIp);
        Assert.True(harness.Ports.IsReserved("tcp", "127.0.0.1", port));

        // The default is proxy mode: the host port is allocated and reported, but the engine is
        // never handed `-p` (the daemon binds and forwards it itself).
        Assert.Empty(harness.Runtime.GetSpec("web")!.Ports);
        Assert.True(record.Request.ExposedPorts.ContainsKey("80/tcp"));
    }

    [Fact]
    public async Task Apple_mode_hands_the_allocated_ports_to_the_engine()
    {
        await using var harness = await ContainerTestHarness.CreateAsync(
            options => options.PortPublishing = CiderOptions.ApplePortPublishing);

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["80/tcp"] = [new PortBinding { HostIp = "127.0.0.1", HostPort = "" }] },
            });

        var port = int.Parse(record.Ports["80/tcp"][0].HostPort);
        var published = Assert.Single(harness.Runtime.GetSpec("web")!.Ports);
        Assert.Equal(80, published.ContainerPort);
        Assert.Equal(port, published.HostPort);
        Assert.Equal("127.0.0.1", published.HostIp);

        // ... and nothing is published from inside the daemon.
        await harness.Containers.StartAsync(record.Id, CancellationToken.None);
        Assert.Empty(harness.Publisher.Published);
    }

    [Fact]
    public async Task An_explicit_HostPort_is_honoured()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var free = FindFreePort();

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["8080/tcp"] = [new PortBinding { HostPort = free.ToString() }] },
            });

        Assert.Equal(free.ToString(), record.Ports["8080/tcp"][0].HostPort);
        Assert.Equal("0.0.0.0", record.Ports["8080/tcp"][0].HostIp);
    }

    [Fact]
    public async Task PublishAllPorts_allocates_for_every_exposed_port()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("nginx", "web", request =>
            request.HostConfig = new HostConfig { PublishAllPorts = true });

        Assert.True(record.Ports.ContainsKey("80/tcp"));
        Assert.NotEmpty(record.Ports["80/tcp"][0].HostPort);
    }

    [Fact]
    public async Task A_docker_sock_bind_is_rewritten_to_the_daemons_own_socket()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "ryuk", request =>
            request.HostConfig = new HostConfig
            {
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
            });

        var mount = Assert.Single(harness.Runtime.GetSpec("ryuk")!.Mounts);
        Assert.Equal(MountKind.Bind, mount.Kind);
        Assert.Equal(harness.Options.SocketPath, mount.Source);
        Assert.Equal("/var/run/docker.sock", mount.Target);

        var mountPoint = Assert.Single(record.Mounts);
        Assert.Equal("bind", mountPoint.Type);
        Assert.Equal(harness.Options.SocketPath, mountPoint.Source);
    }

    [Fact]
    public async Task Binds_named_volumes_and_anonymous_volumes_are_translated()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web", request =>
        {
            request.HostConfig = new HostConfig { Binds = ["/host/dir:/data:ro", "myvol:/var/lib/x"] };
            request.Volumes["/cache"] = EmptyStruct.Instance;
        });

        var spec = harness.Runtime.GetSpec("web")!;
        Assert.Equal(3, spec.Mounts.Count);

        var bind = spec.Mounts.Single(m => m.Target == "/data");
        Assert.Equal(MountKind.Bind, bind.Kind);
        Assert.Equal("/host/dir", bind.Source);
        Assert.True(bind.ReadOnly);

        var named = spec.Mounts.Single(m => m.Target == "/var/lib/x");
        Assert.Equal(MountKind.Volume, named.Kind);
        Assert.Equal("myvol", named.Source);

        var anonymous = spec.Mounts.Single(m => m.Target == "/cache");
        Assert.Equal(MountKind.Volume, anonymous.Kind);
        Assert.Single(record.AnonymousVolumes);

        var volume = await harness.Volumes.InspectAsync(record.AnonymousVolumes[0], default);
        Assert.True(volume.Labels.ContainsKey("com.docker.volume.anonymous"));
    }

    [Fact]
    public async Task Tmpfs_mounts_become_tmpfs_specs()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig { Tmpfs = new Dictionary<string, string> { ["/run"] = "rw,size=64m" } });

        var tmpfs = Assert.Single(harness.Runtime.GetSpec("web")!.Tmpfs);
        Assert.Equal("/run", tmpfs.Target);
        Assert.Equal("tmpfs", Assert.Single(record.Mounts).Type);
    }

    [Fact]
    public async Task The_dns_forwarder_address_is_handed_to_the_container_before_the_requested_servers()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        await harness.CreateAsync("alpine", "web", request =>
            request.HostConfig = new HostConfig { Dns = ["9.9.9.9"] });

        Assert.Equal(["192.168.64.53", "9.9.9.9"], harness.Runtime.GetSpec("web")!.DnsServers);
        Assert.Equal(["bridge"], harness.Dns.Requested);
    }

    [Fact]
    public async Task No_forwarder_means_no_dns_servers_beyond_the_requested_ones()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Dns.Address = null;

        await harness.CreateAsync("alpine", "web");

        Assert.Empty(harness.Runtime.GetSpec("web")!.DnsServers);
    }

    [Fact]
    public async Task Dns_is_skipped_entirely_when_it_is_disabled()
    {
        await using var harness = await ContainerTestHarness.CreateAsync(options => options.DnsEnabled = false);

        await harness.CreateAsync("alpine", "web");

        Assert.Empty(harness.Runtime.GetSpec("web")!.DnsServers);
        Assert.Empty(harness.Dns.Requested);
    }

    [Fact]
    public async Task Host_gateway_extra_hosts_are_silent_and_other_extra_hosts_warn()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var quiet = await harness.Containers.CreateAsync(
            new ContainerCreateRequest
            {
                Image = "alpine",
                HostConfig = new HostConfig { ExtraHosts = ["host.docker.internal:host-gateway"] },
            },
            null,
            null,
            default);
        Assert.Empty(quiet.Warnings);

        var noisy = await harness.Containers.CreateAsync(
            new ContainerCreateRequest
            {
                Image = "alpine",
                HostConfig = new HostConfig { ExtraHosts = ["db:10.0.0.5"] },
            },
            null,
            null,
            default);
        Assert.Contains(noisy.Warnings, warning => warning.Contains("db:10.0.0.5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resources_fall_back_to_the_configured_defaults()
    {
        await using var harness = await ContainerTestHarness.CreateAsync(options =>
        {
            options.DefaultCpus = 4;
            options.DefaultMemoryBytes = 512 * 1024 * 1024;
        });

        await harness.CreateAsync("alpine", "defaults");
        var defaults = harness.Runtime.GetSpec("defaults")!;
        Assert.Equal(4d, defaults.Cpus!.Value);
        Assert.Equal(512L * 1024 * 1024, defaults.MemoryBytes!.Value);

        await harness.CreateAsync("alpine", "explicit", request =>
            request.HostConfig = new HostConfig { NanoCpus = 1_500_000_000, Memory = 64 * 1024 * 1024 });
        var explicitSpec = harness.Runtime.GetSpec("explicit")!;
        Assert.Equal(1.5d, explicitSpec.Cpus!.Value);
        Assert.Equal(64L * 1024 * 1024, explicitSpec.MemoryBytes!.Value);
    }

    [Fact]
    public async Task Compose_aliases_and_the_service_label_land_on_the_endpoint()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "proj-db-1", request =>
        {
            request.Labels = new Dictionary<string, string> { ["com.docker.compose.service"] = "db" };
            request.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = { ["proj_default"] = new EndpointSettings { Aliases = ["database"] } },
            };
        });

        var endpoint = record.Networks["proj_default"];
        Assert.Contains("database", endpoint.Aliases!);
        Assert.Contains("db", endpoint.Aliases!);
        Assert.Equal(["proj_default"], harness.Runtime.GetSpec("proj-db-1")!.Networks);
    }

    [Fact]
    public async Task An_unknown_log_driver_is_rejected_in_dockerds_wording()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // The daemon captures logs one way only and says so in /info; create used to disagree and
        // accept any name at all, echoing it back on inspect while every line still went to the
        // json-file store.
        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.CreateAsync("alpine", "logs1", request =>
            request.HostConfig = new HostConfig { LogConfig = new LogConfig { Type = "nosuchdriver" } }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("logger: no log driver named 'nosuchdriver' is registered", ex.Message);
    }

    [Fact]
    public async Task A_real_docker_log_driver_this_daemon_cannot_honour_is_a_501_not_a_borrowed_400()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // `syslog` and `none` do exist in dockerd, so claiming they are not registered would be a
        // lie; they are a capability this daemon does not have, which is what it says.
        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.CreateAsync("alpine", "logs2", request =>
            request.HostConfig = new HostConfig { LogConfig = new LogConfig { Type = "none" } }));

        Assert.Equal(HttpStatusCode.NotImplemented, ex.Status);
        Assert.Contains("json-file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unset_or_json_file_log_driver_still_creates()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var unset = await harness.CreateAsync("alpine", "logs3");
        var explicitly = await harness.CreateAsync("alpine", "logs4", request =>
            request.HostConfig = new HostConfig { LogConfig = new LogConfig { Type = "json-file" } });

        Assert.Equal("json-file", unset.Request.HostConfig!.LogConfig!.Type);
        Assert.Equal("json-file", explicitly.Request.HostConfig!.LogConfig!.Type);
    }

    [Fact]
    public async Task A_static_ip_outside_the_networks_subnet_is_rejected_at_create()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(
            new NetworkCreateRequest
            {
                Name = "ipnet",
                IPAM = new Ipam { Config = [new IpamConfig { Subnet = "10.77.0.0/24" }] },
            },
            CancellationToken.None);

        // Apple has no `--ip`: the request was silently dropped and reconciliation later reported a
        // different address than the client asked for.
        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.CreateAsync("alpine", "ip1", request =>
            request.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig =
                {
                    ["ipnet"] = new EndpointSettings { IPAMConfig = new EndpointIPAMConfig { IPv4Address = "192.168.250.9" } },
                },
            }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal(
            "invalid config for network ipnet: invalid endpoint settings:\nno configured subnet or ip-range contain the IP address 192.168.250.9",
            ex.Message);
    }

    [Fact]
    public async Task A_static_ip_that_is_not_an_address_at_all_is_rejected_at_create()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(
            new NetworkCreateRequest
            {
                Name = "ipnet2",
                IPAM = new Ipam { Config = [new IpamConfig { Subnet = "10.77.0.0/24" }] },
            },
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.CreateAsync("alpine", "ip2", request =>
            request.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig =
                {
                    ["ipnet2"] = new EndpointSettings { IPAMConfig = new EndpointIPAMConfig { IPv4Address = "not-an-ip" } },
                },
            }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.EndsWith("invalid IPv4 address: not-an-ip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_ip_inside_the_subnet_is_accepted_because_dockerd_accepts_it_too()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(
            new NetworkCreateRequest
            {
                Name = "ipnet3",
                IPAM = new Ipam { Config = [new IpamConfig { Subnet = "10.77.0.0/24" }] },
            },
            CancellationToken.None);

        // Still not honoured - Apple picks the address - but that is a capability gap, not a reason
        // to fail a request dockerd would accept.
        var record = await harness.CreateAsync("alpine", "ip3", request =>
            request.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig =
                {
                    ["ipnet3"] = new EndpointSettings { IPAMConfig = new EndpointIPAMConfig { IPv4Address = "10.77.0.9" } },
                },
            });

        Assert.True(record.Networks.ContainsKey("ipnet3"));
    }

    [Fact]
    public async Task A_network_referenced_by_id_is_resolved_to_its_name_for_the_engine()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var network = await harness.Networks.CreateAsync(
            new NetworkCreateRequest { Name = "op3net" }, CancellationToken.None);

        // Aspire's DCP keys NetworkMode and EndpointsConfig by the network *id*, which dockerd
        // accepts; Apple `container create --network` only takes the name and answers
        // "network <id> not found".
        var record = await harness.CreateAsync("alpine", "byid", request =>
        {
            request.HostConfig = new HostConfig { NetworkMode = network.Id };
            request.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = { [network.Id] = new EndpointSettings { Aliases = ["alias-by-id"] } },
            };
        });

        Assert.Equal(["op3net"], harness.Runtime.GetSpec("byid")!.Networks);
        Assert.True(record.Networks.ContainsKey("op3net"), "the endpoint must be keyed by the network name");
        Assert.Contains("alias-by-id", record.Networks["op3net"].Aliases!);
    }

    [Fact]
    public async Task A_network_apple_cannot_name_is_created_under_its_folded_name()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        const string dcpName = "aspire-session-network-Ab12cdef-e2e-aspire-";
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = dcpName }, CancellationToken.None);

        var record = await harness.CreateAsync("alpine", "onaspirenet", request =>
            request.HostConfig = new HostConfig { NetworkMode = dcpName });

        // One mapping point: the container is created on exactly the name the network manager gave
        // the engine, while Docker still sees the network the client asked for.
        Assert.Equal([harness.Networks.RuntimeNameFor(dcpName)], harness.Runtime.GetSpec("onaspirenet")!.Networks);
        Assert.True(record.Networks.ContainsKey(dcpName));
    }

    [Fact]
    public async Task A_runtime_failure_releases_the_reserved_ports_and_the_name()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.CreateFailure = RuntimeException.InvalidArgument("bad spec");

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(
                new ContainerCreateRequest
                {
                    Image = "alpine",
                    HostConfig = new HostConfig { PortBindings = { ["80/tcp"] = [new PortBinding()] } },
                },
                "web",
                null,
                default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Equal(0, harness.Ports.ReservationCount);

        // The name is free again.
        var record = await harness.CreateAsync("alpine", "web");
        Assert.Equal("web", record.Name);
    }

    [Fact]
    public async Task A_missing_image_is_a_404_and_create_never_pulls()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.CreateAsync(
                new ContainerCreateRequest { Image = "ghcr.io/example/app:1" }, "app", null, default));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.Status);
        Assert.Equal("No such image: ghcr.io/example/app:1", error.Message);
        Assert.DoesNotContain(harness.Runtime.Calls, call => call.StartsWith("PullImageAsync:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_connect_recreates_the_engine_container_with_the_very_same_spec()
    {
        // `docker network connect` on a never-started container removes and re-creates the Apple
        // container, because Apple fixes its networks at create time. Everything the engine was told
        // at create time therefore has to be reproduced from the record — this is the guard against
        // CreateAsync and BuildSpecFromRecord drifting apart.
        await using var harness = await ContainerTestHarness.CreateAsync(
            options => options.PortPublishing = CiderOptions.ApplePortPublishing);
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var hostDir = Path.Combine(harness.Options.DataDir, "bind-source");
        Directory.CreateDirectory(hostDir);

        var record = await harness.CreateAsync("alpine", "rich", request =>
        {
            request.Cmd = ["sh", "-c", "sleep 30"];
            request.Env = ["A=1", "B=2"];
            request.Labels = new Dictionary<string, string> { ["team"] = "core" };
            request.WorkingDir = "/work";
            request.User = "1000:1000";
            request.Tty = true;
            request.OpenStdin = true;
            request.Hostname = "rich-host";
            request.StopSignal = "SIGUSR1";
            request.HostConfig = new HostConfig
            {
                Binds = [$"{hostDir}:/data:ro", "named-vol:/var/lib/app"],
                Tmpfs = new Dictionary<string, string> { ["/scratch"] = "" },
                PortBindings = { ["80/tcp"] = [new PortBinding { HostIp = "127.0.0.1", HostPort = "" }] },
                Memory = 512 * 1024 * 1024,
                NanoCpus = 2_000_000_000,
                CapAdd = ["NET_ADMIN"],
                CapDrop = ["CHOWN"],
                ReadonlyRootfs = true,
                ShmSize = 64 * 1024 * 1024,
                Init = true,
                Ulimits = [new Ulimit { Name = "nofile", Soft = 1024, Hard = 2048 }],
                Dns = ["1.1.1.1"],
                DnsSearch = ["example.test"],
                DnsOptions = ["ndots:2"],
                Sysctls = new Dictionary<string, string> { ["net.core.somaxconn"] = "1024" },
            };
        });

        var before = harness.Runtime.GetSpec(record.RuntimeId);
        Assert.NotNull(before);
        Assert.Equal("1024", before.Sysctls["net.core.somaxconn"]);
        Assert.Equal("SIGUSR1", before.StopSignal);

        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "rich" }, CancellationToken.None);

        var after = harness.Runtime.GetSpec(record.RuntimeId);
        Assert.NotNull(after);
        Assert.Equal(["default", "extra"], after.Networks.ToArray());
        Assert.Equal(Normalize(before), Normalize(after));

        // The host port is neither re-reserved nor dropped, and the named volume is not created a
        // second time.
        var port = int.Parse(record.Ports["80/tcp"][0].HostPort, CultureInfo.InvariantCulture);
        Assert.Equal(port, Assert.Single(after.Ports).HostPort);
        Assert.Equal(1, harness.Runtime.Calls.Count(call => call == "CreateVolumeAsync:named-vol"));
    }

    /// <summary>The engine spec with the parts a network change is allowed to move stripped out.</summary>
    private static string Normalize(ContainerSpec spec) =>
        DockerJson.Serialize(spec with { Networks = [], DnsServers = [] });

    private static void SeedEntrypointImage(ContainerTestHarness harness) =>
        harness.Runtime.SeedImage(new RuntimeImageDetail
        {
            Id = "sha256:" + new string('e', 64),
            References = ["docker.io/library/with-entrypoint:latest"],
            Size = 100,
            Created = DateTimeOffset.UnixEpoch,
            Architecture = "arm64",
            Os = "linux",
            Config = new ImageConfig { Entrypoint = ["/entry"], Cmd = ["default"] },
        });

    private static List<string>? Split(string? value) =>
        value is null ? null : value.Length == 0 ? [] : [.. value.Split('|')];

    private static string[] Argv(Core.State.ContainerRecord record) =>
        [record.Path, .. record.Args];

    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static class PortAllocatorRange
    {
        public const int Min = Core.Net.PortAllocator.EphemeralMin;
        public const int Max = Core.Net.PortAllocator.EphemeralMax;
    }
}
