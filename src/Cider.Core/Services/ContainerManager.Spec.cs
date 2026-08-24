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
    /// <summary>
    /// The Docker networks a create request asks for, by name. Docker lets a client name a network
    /// by <em>id</em> in both <c>HostConfig.NetworkMode</c> and the <c>NetworkingConfig</c> keys —
    /// Aspire's DCP always does — while Apple <c>container create --network</c> only takes a name,
    /// so every reference is resolved through the network store first (and the endpoint config is
    /// re-keyed with it, so an endpoint's aliases are not lost with its id).
    /// </summary>
    private async Task<List<string>> ResolveNetworksAsync(
        HostConfig hostConfig,
        NetworkingConfig? networkingConfig,
        CancellationToken ct)
    {
        // The id lookups below read the network store, and `bridge` only lands in it once the
        // default network has been reconciled.
        await _networks.EnsureDefaultAsync(ct);

        var mode = _networks.ResolveDockerName(hostConfig.NetworkMode ?? "");
        if (mode.Length == 0 || string.Equals(mode, "default", StringComparison.Ordinal) ||
            string.Equals(mode, "bridge", StringComparison.Ordinal))
        {
            mode = "bridge";
        }
        else if (string.Equals(mode, "host", StringComparison.Ordinal) ||
                 string.Equals(mode, "none", StringComparison.Ordinal) ||
                 mode.StartsWith("container:", StringComparison.Ordinal))
        {
            throw DockerErrors.BadParameter(
                $"cider: network mode '{hostConfig.NetworkMode}' is not supported by Apple container");
        }

        var networks = new List<string>();
        foreach (var key in networkingConfig?.EndpointsConfig.Keys.ToList() ?? [])
        {
            var resolved = _networks.ResolveDockerName(key);
            var normalized = string.Equals(resolved, "default", StringComparison.Ordinal) ? "bridge" : resolved;
            if (!string.Equals(normalized, key, StringComparison.Ordinal) && networkingConfig is not null)
            {
                var settings = networkingConfig.EndpointsConfig[key];
                networkingConfig.EndpointsConfig.Remove(key);
                networkingConfig.EndpointsConfig[normalized] = settings;
            }

            if (!networks.Contains(normalized, StringComparer.Ordinal))
            {
                networks.Add(normalized);
            }
        }

        if (networks.Count == 0)
        {
            networks.Add(mode);
        }

        return networks;
    }

    /// <summary>
    /// Rebuilds the engine spec of an existing container from its record, with a different network
    /// list. Used by <c>docker network connect</c>/<c>disconnect</c> before the first start: Apple
    /// <c>container</c> cannot change a container's networks, so the container is removed and
    /// re-created with this spec.
    /// </summary>
    /// <remarks>
    /// Everything but the networks comes back out of the record, which holds the create request
    /// <em>after</em> <see cref="CreateAsync"/> resolved it against the image (env, cmd, entrypoint,
    /// labels, hostname, exposed ports) plus the mount points and host port bindings allocated back
    /// then — so no volume is created twice and no host port is reserved twice. One detail cannot
    /// be recovered and does not matter: the tmpfs size (Apple's <c>--tmpfs</c> has no size option,
    /// so <see cref="TmpfsSpec.SizeBytes"/> never reaches the CLI anyway). The platform comes from
    /// <see cref="ContainerRecord.RequestedPlatform"/>, i.e. exactly the <c>?platform=</c> the client
    /// sent and <c>null</c> when it sent none; <see cref="ContainerRecord.Platform"/> is the resolved
    /// image platform and would put a <c>--platform</c> on the re-create the original create never had.
    /// </remarks>
    private ContainerSpec BuildSpecFromRecord(ContainerRecord record, List<string> networks, List<string> dnsServers)
    {
        var request = record.Request;
        var hostConfig = request.HostConfig ?? new HostConfig();

        var argv = new List<string>();
        if (!string.IsNullOrEmpty(record.Path))
        {
            argv.Add(record.Path);
        }

        argv.AddRange(record.Args);

        var mountSpecs = new List<MountSpec>();
        var tmpfsSpecs = new List<TmpfsSpec>();
        foreach (var mount in record.Mounts)
        {
            switch (mount.Type)
            {
                case "tmpfs":
                    tmpfsSpecs.Add(new TmpfsSpec { Target = mount.Destination });
                    break;
                case "volume":
                    mountSpecs.Add(new MountSpec
                    {
                        Kind = MountKind.Volume,
                        Source = mount.Name ?? "",
                        Target = mount.Destination,
                        ReadOnly = !mount.RW,
                    });
                    break;
                default:
                    mountSpecs.Add(new MountSpec
                    {
                        Kind = MountKind.Bind,
                        Source = mount.Source,
                        Target = mount.Destination,
                        ReadOnly = !mount.RW,
                    });
                    break;
            }
        }

        var portSpecs = new List<PortSpec>();
        foreach (var (key, bindings) in record.Ports)
        {
            var (containerPort, proto) = SplitPortKey(key);
            foreach (var binding in bindings)
            {
                if (!int.TryParse(binding.HostPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort))
                {
                    continue;
                }

                portSpecs.Add(new PortSpec
                {
                    HostIp = string.IsNullOrEmpty(binding.HostIp) ? "0.0.0.0" : binding.HostIp,
                    HostPort = hostPort,
                    ContainerPort = containerPort,
                    Proto = proto,
                });
            }
        }

        return new ContainerSpec
        {
            RuntimeId = record.RuntimeId,
            Image = ImageReference.Parse(request.Image).Normalize().ToString(),
            Platform = record.RequestedPlatform,
            Entrypoint = argv.Count > 0 ? argv[0] : null,
            Args = argv.Count > 1 ? argv[1..] : [],
            Env = request.Env ?? [],
            WorkingDir = string.IsNullOrEmpty(request.WorkingDir) ? null : request.WorkingDir,
            User = string.IsNullOrEmpty(request.User) ? null : request.User,
            Tty = request.Tty,
            OpenStdin = request.OpenStdin,
            Labels = ContainerIdentity.BuildLabels(record.Id, record.Name, request.Labels),
            Mounts = mountSpecs,
            Ports = ProxyPublishing ? [] : portSpecs,
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
            Hostname = request.Hostname,
        };
    }

    private Dictionary<string, List<PortBinding>> AllocatePorts(
        HostConfig hostConfig,
        Dictionary<string, EmptyStruct> exposed,
        List<PortSpec> specs)
    {
        var result = new Dictionary<string, List<PortBinding>>(StringComparer.Ordinal);

        foreach (var (rawKey, bindings) in hostConfig.PortBindings)
        {
            var key = NormalizePortKey(rawKey);
            var (containerPort, proto) = SplitPortKey(key);
            var requested = bindings is { Count: > 0 } ? bindings : [new PortBinding()];
            var resolved = new List<PortBinding>();

            foreach (var binding in requested)
            {
                var hostIp = string.IsNullOrEmpty(binding.HostIp) ? "0.0.0.0" : binding.HostIp;
                int? wanted = null;
                if (!string.IsNullOrEmpty(binding.HostPort))
                {
                    // Docker accepts ranges ("8000-8010"); the low end is good enough here.
                    var text = binding.HostPort.Split('-')[0];
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        throw DockerErrors.BadParameter($"invalid port specification: \"{binding.HostPort}\"");
                    }

                    wanted = parsed;
                }

                var hostPort = _ports.Reserve(proto, hostIp, wanted);
                resolved.Add(new PortBinding { HostIp = hostIp, HostPort = hostPort.ToString(CultureInfo.InvariantCulture) });
                specs.Add(new PortSpec { HostIp = hostIp, HostPort = hostPort, ContainerPort = containerPort, Proto = proto });
            }

            result[key] = resolved;
        }

        if (hostConfig.PublishAllPorts)
        {
            foreach (var key in exposed.Keys)
            {
                if (result.ContainsKey(key))
                {
                    continue;
                }

                var (containerPort, proto) = SplitPortKey(key);
                var hostPort = _ports.Reserve(proto, "0.0.0.0", null);
                result[key] = [new PortBinding { HostIp = "0.0.0.0", HostPort = hostPort.ToString(CultureInfo.InvariantCulture) }];
                specs.Add(new PortSpec { HostIp = "0.0.0.0", HostPort = hostPort, ContainerPort = containerPort, Proto = proto });
            }
        }

        return result;
    }

    private void ReleasePorts(IEnumerable<PortSpec> specs)
    {
        foreach (var spec in specs)
        {
            _ports.Release(spec.Proto, spec.HostIp, spec.HostPort);
        }
    }

    private void ReleasePorts(ContainerRecord record)
    {
        foreach (var (key, bindings) in record.Ports)
        {
            var (_, proto) = SplitPortKey(key);
            foreach (var binding in bindings)
            {
                if (int.TryParse(binding.HostPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    _ports.Release(proto, string.IsNullOrEmpty(binding.HostIp) ? "0.0.0.0" : binding.HostIp, port);
                }
            }
        }
    }

    private async Task BuildMountsAsync(
        ContainerCreateRequest request,
        HostConfig hostConfig,
        ImageConfig imageConfig,
        List<MountSpec> mountSpecs,
        List<TmpfsSpec> tmpfsSpecs,
        List<MountPoint> mountPoints,
        List<string> anonymousVolumes,
        CancellationToken ct)
    {
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bind in hostConfig.Binds ?? [])
        {
            var parsed = ParseBind(bind);
            if (parsed is null)
            {
                throw DockerErrors.BadParameter($"invalid bind mount spec \"{bind}\"");
            }

            await AddMountAsync(parsed.Value.Source, parsed.Value.Target, parsed.Value.ReadOnly, parsed.Value.IsVolume, ct,
                mountSpecs, mountPoints, anonymousVolumes, seenTargets);
        }

        foreach (var mount in hostConfig.Mounts ?? [])
        {
            if (string.Equals(mount.Type, "tmpfs", StringComparison.OrdinalIgnoreCase))
            {
                if (seenTargets.Add(mount.Target))
                {
                    tmpfsSpecs.Add(new TmpfsSpec { Target = mount.Target, SizeBytes = mount.TmpfsOptions?.SizeBytes });
                    mountPoints.Add(new MountPoint
                    {
                        Type = "tmpfs",
                        Source = "",
                        Destination = mount.Target,
                        RW = !mount.ReadOnly,
                        Mode = "",
                        Propagation = "",
                    });
                }

                continue;
            }

            var isVolume = string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase);
            await AddMountAsync(mount.Source, mount.Target, mount.ReadOnly, isVolume, ct,
                mountSpecs, mountPoints, anonymousVolumes, seenTargets);
        }

        foreach (var (target, _) in hostConfig.Tmpfs ?? [])
        {
            if (seenTargets.Add(target))
            {
                tmpfsSpecs.Add(new TmpfsSpec { Target = target });
                mountPoints.Add(new MountPoint { Type = "tmpfs", Source = "", Destination = target, RW = true });
            }
        }

        // Anonymous volumes declared by the image or by `-v /path`.
        var declared = new List<string>();
        declared.AddRange(imageConfig.Volumes);
        declared.AddRange(request.Volumes.Keys);
        foreach (var target in declared)
        {
            if (string.IsNullOrEmpty(target) || seenTargets.Contains(target))
            {
                continue;
            }

            await AddMountAsync(source: "", target, readOnly: false, isVolume: true, ct,
                mountSpecs, mountPoints, anonymousVolumes, seenTargets);
        }
    }

    private async Task AddMountAsync(
        string source,
        string target,
        bool readOnly,
        bool isVolume,
        CancellationToken ct,
        List<MountSpec> mountSpecs,
        List<MountPoint> mountPoints,
        List<string> anonymousVolumes,
        HashSet<string> seenTargets)
    {
        if (string.IsNullOrEmpty(target) || !seenTargets.Add(target))
        {
            return;
        }

        // Ryuk & friends bind the daemon socket; point it at ours wherever it really lives.
        if (string.Equals(target, DockerSocketTarget, StringComparison.Ordinal))
        {
            mountSpecs.Add(new MountSpec { Kind = MountKind.Bind, Source = _options.SocketPath, Target = target, ReadOnly = readOnly });
            mountPoints.Add(new MountPoint
            {
                Type = "bind",
                Source = _options.SocketPath,
                Destination = target,
                RW = !readOnly,
                Mode = readOnly ? "ro" : "",
                Propagation = "rprivate",
            });
            return;
        }

        if (!isVolume)
        {
            mountSpecs.Add(new MountSpec { Kind = MountKind.Bind, Source = source, Target = target, ReadOnly = readOnly });
            mountPoints.Add(new MountPoint
            {
                Type = "bind",
                Source = source,
                Destination = target,
                RW = !readOnly,
                Mode = readOnly ? "ro" : "",
                Propagation = "rprivate",
            });
            return;
        }

        var anonymous = string.IsNullOrEmpty(source);
        var volumeName = anonymous ? DockerId.New() : source;
        var labels = anonymous
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [AnonymousVolumeLabel] = "" }
            : null;

        try
        {
            await _volumes.EnsureAsync(volumeName, labels, ct);
        }
        catch (RuntimeException ex)
        {
            throw Translate(ex);
        }

        if (anonymous)
        {
            anonymousVolumes.Add(volumeName);
        }

        mountSpecs.Add(new MountSpec { Kind = MountKind.Volume, Source = volumeName, Target = target, ReadOnly = readOnly });
        mountPoints.Add(new MountPoint
        {
            Type = "volume",
            Name = volumeName,
            Source = Path.Combine(_options.VolumesDir, volumeName),
            Destination = target,
            Driver = "local",
            RW = !readOnly,
            Mode = readOnly ? "ro" : "",
            Propagation = "",
        });
    }

    private async Task<List<string>> ResolveDnsServersAsync(List<string> networks, HostConfig hostConfig, CancellationToken ct)
    {
        var servers = new List<string>();

        if (_options.DnsEnabled && networks.Count > 0)
        {
            IPAddress? forwarder = null;
            try
            {
                forwarder = await _dnsForwarder.EnsureAsync(networks[0], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "DNS forwarder for network {Network} could not be started", networks[0]);
            }

            if (forwarder is not null)
            {
                servers.Add(forwarder.ToString() ?? "");
            }
            else if (Interlocked.Exchange(ref _dnsWarningIssued, 1) == 0)
            {
                _logger.LogWarning(
                    "no DNS forwarder available on network {Network}; containers will not resolve container names",
                    networks[0]);
            }
        }

        foreach (var server in hostConfig.Dns ?? [])
        {
            if (!string.IsNullOrEmpty(server) && !servers.Contains(server, StringComparer.Ordinal))
            {
                servers.Add(server);
            }
        }

        return servers;
    }

    private List<string> ResolveDnsSearch(HostConfig hostConfig)
    {
        var search = new List<string>(hostConfig.DnsSearch ?? []);
        if (!string.IsNullOrEmpty(_options.DnsSearchDomain) && !search.Contains(_options.DnsSearchDomain, StringComparer.Ordinal))
        {
            search.Add(_options.DnsSearchDomain);
        }

        return search;
    }

    private double? ResolveCpus(HostConfig hostConfig)
    {
        if (hostConfig.NanoCpus > 0)
        {
            return hostConfig.NanoCpus / 1_000_000_000d;
        }

        return _options.DefaultCpus > 0 ? _options.DefaultCpus : null;
    }

    /// <summary>
    /// Rejects a static address in <c>EndpointsConfig[net].IPAMConfig</c> that is malformed or falls
    /// outside the network's subnet, before create returns 201 on a request it cannot honour.
    /// The subnet lookup is per network and only happens when an address was
    /// actually asked for, which is rare, so the common create path costs nothing.
    /// </summary>
    private async Task ValidateEndpointIpamAsync(
        List<string> networks,
        NetworkingConfig? networkingConfig,
        CancellationToken ct)
    {
        if (networkingConfig is null)
        {
            return;
        }

        foreach (var network in networks)
        {
            if (!networkingConfig.EndpointsConfig.TryGetValue(network, out var settings) ||
                settings?.IPAMConfig is null)
            {
                continue;
            }

            EndpointIpam.Validate(network, settings, await _networks.SubnetOfAsync(network, ct).ConfigureAwait(false));
        }
    }

    private static Dictionary<string, EndpointSettings> BuildEndpoints(
        List<string> networks,
        NetworkingConfig? networkingConfig,
        string containerName,
        string hostname,
        IReadOnlyDictionary<string, string> labels)
    {
        var endpoints = new Dictionary<string, EndpointSettings>(StringComparer.Ordinal);
        foreach (var network in networks)
        {
            EndpointSettings settings = new();
            if (networkingConfig is not null)
            {
                if (networkingConfig.EndpointsConfig.TryGetValue(network, out var configured) ||
                    (string.Equals(network, "bridge", StringComparison.Ordinal) &&
                     networkingConfig.EndpointsConfig.TryGetValue("default", out configured)))
                {
                    settings = configured;
                }
            }

            var aliases = new List<string>(settings.Aliases ?? []);
            if (labels.TryGetValue(ComposeServiceLabel, out var service) &&
                !aliases.Contains(service, StringComparer.Ordinal))
            {
                aliases.Add(service);
            }

            settings.Aliases = aliases.Count > 0 ? aliases : null;
            settings.DNSNames = [containerName, hostname];
            endpoints[network] = settings;
        }

        return endpoints;
    }

    private static List<string> MergeEnv(IReadOnlyList<string> imageEnv, IReadOnlyList<string>? requestEnv)
    {
        var result = new List<string>(imageEnv);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < result.Count; i++)
        {
            index[EnvKey(result[i])] = i;
        }

        foreach (var entry in requestEnv ?? [])
        {
            var key = EnvKey(entry);
            if (index.TryGetValue(key, out var position))
            {
                result[position] = entry;
            }
            else
            {
                index[key] = result.Count;
                result.Add(entry);
            }
        }

        return result;
    }

    private static string EnvKey(string entry)
    {
        var separator = entry.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? entry : entry[..separator];
    }

    private static HealthConfig? ToHealthConfig(HealthcheckConfig? config)
    {
        if (config is null || config.Test.Count == 0)
        {
            return null;
        }

        return new HealthConfig
        {
            Test = [.. config.Test],
            Interval = config.Interval,
            Timeout = config.Timeout,
            Retries = config.Retries,
            StartPeriod = config.StartPeriod,
        };
    }

    /// <summary>Normalizes <c>80</c> / <c>80/tcp</c> to Docker's <c>port/proto</c> key.</summary>
    internal static string NormalizePortKey(string key)
    {
        if (key.Contains('/', StringComparison.Ordinal))
        {
            return key;
        }

        return key + "/tcp";
    }

    internal static (int Port, string Proto) SplitPortKey(string key)
    {
        var normalized = NormalizePortKey(key);
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        var portText = normalized[..slash];
        var proto = normalized[(slash + 1)..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw DockerErrors.BadParameter($"invalid port specification: \"{key}\"");
        }

        return (port, proto);
    }

    /// <summary>Parses a Docker <c>--volume</c> string: <c>[source:]destination[:options]</c>.</summary>
    internal static (string Source, string Target, bool ReadOnly, bool IsVolume)? ParseBind(string bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return null;
        }

        var parts = bind.Split(':');
        return parts.Length switch
        {
            1 => ("", parts[0], false, true),
            2 => (parts[0], parts[1], false, !IsHostPath(parts[0])),
            3 => (parts[0], parts[1], parts[2].Split(',').Contains("ro"), !IsHostPath(parts[0])),
            _ => null,
        };
    }

    private static bool IsHostPath(string source) =>
        source.StartsWith('/') || source.StartsWith("./", StringComparison.Ordinal) ||
        source.StartsWith("../", StringComparison.Ordinal) || source.StartsWith('~');
}
