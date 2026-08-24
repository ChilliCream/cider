using System.Globalization;
using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    /// <summary>The bind target Ryuk and friends expect to talk to the daemon through.</summary>
    public const string DockerSocketTarget = "/var/run/docker.sock";

    private const string AnonymousVolumeLabel = "com.docker.volume.anonymous";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary><c>POST /containers/create</c>: resolves the image, merges the config and creates the engine container.</summary>
    public async Task<ContainerCreateResponse> CreateAsync(
        ContainerCreateRequest request,
        string? name,
        string? platform,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Image))
        {
            throw DockerErrors.BadParameter("no image specified");
        }

        var warnings = new List<string>();
        var id = DockerId.New();
        var containerName = ReserveName(name);
        var allocatedPorts = new List<PortSpec>();

        try
        {
            // Real dockerd never pulls on create: a missing image is a 404 "No such image: …" and
            // the client (docker CLI, Testcontainers, compose, …) does the pull itself against
            // /images/create, honouring its own pull policy and registry auth. Pulling here would
            // make `docker create --pull=never` impossible and hang callers against unreachable
            // registries for the length of a full pull instead of a fast 404.
            RuntimeImageDetail image;
            try
            {
                image = await _images.EnsureImageAsync(request.Image, platform, auth: null, pullIfMissing: false, progress: null, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }

            var hostConfig = request.HostConfig ??= new HostConfig();
            var imageConfig = image.Config;

            // Docker resolves an unset logging driver to the daemon's default and reports that back
            // on inspect. Echoing the client's empty string instead makes `docker compose logs`
            // silently print nothing (it only reads logs from drivers it recognizes).
            hostConfig.LogConfig ??= new LogConfig();
            if (string.IsNullOrEmpty(hostConfig.LogConfig.Type))
            {
                hostConfig.LogConfig.Type = "json-file";
            }
            else
            {
                LogDrivers.Validate(hostConfig.LogConfig.Type);
            }

            // ---- entrypoint / cmd -------------------------------------------------------------
            // Docker distinguishes a missing Entrypoint (inherit the image's) from an explicitly
            // empty one (clear the image's), so the null check has to come before the count check.
            List<string> entrypoint;
            List<string> cmd;
            if (request.Entrypoint is { Count: > 0 })
            {
                entrypoint = [.. request.Entrypoint];
                cmd = request.Cmd is null ? [] : [.. request.Cmd];
            }
            else if (request.Entrypoint is { Count: 0 })
            {
                entrypoint = [];
                cmd = request.Cmd is null ? [.. imageConfig.Cmd] : [.. request.Cmd];
            }
            else
            {
                entrypoint = [.. imageConfig.Entrypoint];
                cmd = request.Cmd is null ? [.. imageConfig.Cmd] : [.. request.Cmd];
            }

            var argv = new List<string>(entrypoint);
            argv.AddRange(cmd);

            // ---- environment and image defaults -----------------------------------------------
            var env = MergeEnv(imageConfig.Env, request.Env);
            var workingDir = !string.IsNullOrEmpty(request.WorkingDir) ? request.WorkingDir : imageConfig.WorkingDir ?? "";
            var user = !string.IsNullOrEmpty(request.User) ? request.User : imageConfig.User ?? "";
            var stopSignal = !string.IsNullOrEmpty(request.StopSignal) ? request.StopSignal : imageConfig.StopSignal;
            var hostname = !string.IsNullOrEmpty(request.Hostname) ? request.Hostname : DockerId.Short(id);

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in imageConfig.Labels)
            {
                labels[key] = value;
            }

            foreach (var (key, value) in request.Labels)
            {
                labels[key] = value;
            }

            var exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
            foreach (var port in imageConfig.ExposedPorts)
            {
                exposed[NormalizePortKey(port)] = EmptyStruct.Instance;
            }

            foreach (var port in request.ExposedPorts.Keys)
            {
                exposed[NormalizePortKey(port)] = EmptyStruct.Instance;
            }

            foreach (var port in hostConfig.PortBindings.Keys)
            {
                exposed[NormalizePortKey(port)] = EmptyStruct.Instance;
            }

            var healthcheck = request.Healthcheck ?? ToHealthConfig(imageConfig.Healthcheck);

            // ---- networks ---------------------------------------------------------------------
            var networks = await ResolveNetworksAsync(hostConfig, request.NetworkingConfig, ct);
            await ValidateEndpointIpamAsync(networks, request.NetworkingConfig, ct);

            // ---- ports ------------------------------------------------------------------------
            var recordPorts = AllocatePorts(hostConfig, exposed, allocatedPorts);

            // ---- mounts -----------------------------------------------------------------------
            var anonymousVolumes = new List<string>();
            var mountSpecs = new List<MountSpec>();
            var tmpfsSpecs = new List<TmpfsSpec>();
            var mountPoints = new List<MountPoint>();
            await BuildMountsAsync(request, hostConfig, imageConfig, mountSpecs, tmpfsSpecs, mountPoints, anonymousVolumes, ct);

            // ---- extra hosts ------------------------------------------------------------------
            foreach (var extraHost in hostConfig.ExtraHosts ?? [])
            {
                if (extraHost.EndsWith(":host-gateway", StringComparison.Ordinal))
                {
                    // Answered by the built-in DNS server; nothing to pass to the engine.
                    continue;
                }

                warnings.Add($"cider: extra host '{extraHost}' is not supported by Apple container and was ignored");
            }

            // ---- dns --------------------------------------------------------------------------
            var dnsServers = await ResolveDnsServersAsync(networks, hostConfig, ct);

            // ---- engine spec ------------------------------------------------------------------
            var runtimeId = ContainerIdentity.ResolveRuntimeId(id, containerName);
            var spec = new ContainerSpec
            {
                RuntimeId = runtimeId,
                Image = ImageReference.Parse(request.Image).Normalize().ToString(),
                Platform = platform,
                Entrypoint = argv.Count > 0 ? argv[0] : null,
                Args = argv.Count > 1 ? argv[1..] : [],
                Env = env,
                WorkingDir = string.IsNullOrEmpty(workingDir) ? null : workingDir,
                User = string.IsNullOrEmpty(user) ? null : user,
                Tty = request.Tty,
                OpenStdin = request.OpenStdin,
                Labels = ContainerIdentity.BuildLabels(id, containerName, labels),
                Mounts = mountSpecs,

                // In proxy mode the daemon binds the host ports itself once the container has an
                // address, so the engine must not be handed `-p` (its own forwarder would take the
                // port and then fail to dial the guest — see the REPORT's P0).
                Ports = ProxyPublishing ? [] : allocatedPorts,
                Networks = [.. networks.Select(_networks.RuntimeNameFor)],
                DnsServers = dnsServers,
                DnsSearch = ResolveDnsSearch(hostConfig),
                DnsOptions = hostConfig.DnsOptions ?? [],
                Cpus = ResolveCpus(hostConfig),
                MemoryBytes = hostConfig.Memory > 0 ? hostConfig.Memory : _options.DefaultMemoryBytes,
                CapAdd = hostConfig.CapAdd ?? [],
                CapDrop = hostConfig.CapDrop ?? [],
                Privileged = hostConfig.Privileged,
                ReadOnlyRootfs = hostConfig.ReadonlyRootfs,
                ShmSizeBytes = hostConfig.ShmSize > 0 ? hostConfig.ShmSize : null,
                Init = hostConfig.Init ?? false,
                Ulimits = [.. (hostConfig.Ulimits ?? []).Select(u => new UlimitSpec { Name = u.Name, Soft = u.Soft, Hard = u.Hard })],
                Tmpfs = tmpfsSpecs,
                Hostname = hostname,
            };

            try
            {
                await _runtime.CreateContainerAsync(spec, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }

            // ---- write the resolved configuration back into the stored request ----------------
            request.Cmd = cmd;
            request.Entrypoint = entrypoint.Count > 0 ? entrypoint : request.Entrypoint;
            request.Env = env;
            request.Labels = labels;
            request.ExposedPorts = exposed;
            request.WorkingDir = workingDir;
            request.User = user;
            request.Hostname = hostname;
            request.StopSignal = stopSignal;
            request.Healthcheck = healthcheck;

            var record = new ContainerRecord
            {
                Id = id,
                Name = containerName,
                RuntimeId = runtimeId,
                Created = DateTimeOffset.UtcNow,
                Request = request,
                ImageId = image.Id,
                ImageRef = ImageReference.Parse(request.Image).Familiar(),
                Platform = platform ?? image.Platforms.FirstOrDefault(),
                RequestedPlatform = platform,
                Path = argv.Count > 0 ? argv[0] : "",
                Args = argv.Count > 1 ? argv[1..] : [],
                Entrypoint = entrypoint,
                Cmd = cmd,
                State = new ContainerState { Status = "created" },
                Ports = recordPorts,
                Networks = BuildEndpoints(networks, request.NetworkingConfig, containerName, hostname, labels),
                Mounts = mountPoints,
                LogPath = _logs.PathFor(id),
                AutoRemove = hostConfig.AutoRemove,
                StopSignal = stopSignal,
                StopTimeout = request.StopTimeout,
                Healthcheck = healthcheck,
                RestartPolicy = hostConfig.RestartPolicy,
                AnonymousVolumes = anonymousVolumes,
            };

            Persist(record);
            GetHandle(id);
            Publish(record, "create");
            RaiseStateChanged(record, "create");

            return new ContainerCreateResponse { Id = id, Warnings = warnings };
        }
        catch
        {
            ReleasePorts(allocatedPorts);
            throw;
        }
        finally
        {
            ReleaseName(containerName);
        }
    }

    private string ReserveName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var generated = Names.GenerateRandomName();
                if (TryReserveName(generated, out _))
                {
                    return generated;
                }
            }

            throw DockerErrors.Internal("could not generate a free container name");
        }

        var trimmed = name.TrimStart('/');
        if (!Names.IsValidDockerName(trimmed))
        {
            throw DockerErrors.BadParameter(
                $"Invalid container name ({trimmed}), only [a-zA-Z0-9][a-zA-Z0-9_.-] are allowed");
        }

        if (!TryReserveName(trimmed, out var existingId))
        {
            throw DockerErrors.ContainerNameConflict(trimmed, existingId ?? "");
        }

        return trimmed;
    }

    private bool TryReserveName(string name, out string? existingId)
    {
        existingId = null;
        lock (_nameGate)
        {
            var existing = FindByName(name);
            if (existing is not null)
            {
                existingId = existing.Id;
                return false;
            }

            if (!_pendingNames.Add(name))
            {
                existingId = "";
                return false;
            }

            return true;
        }
    }

    private void ReleaseName(string name)
    {
        lock (_nameGate)
        {
            _pendingNames.Remove(name);
        }
    }
}
