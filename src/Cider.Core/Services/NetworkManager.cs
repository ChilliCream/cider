using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Core.Time;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>Docker network operations, mapping Docker's <c>bridge</c> onto Apple container's <c>default</c>.</summary>
public sealed class NetworkManager
{
    private const string BridgeName = "bridge";
    private const string HostName = "host";
    private const string NoneName = "none";

    private static readonly string BridgeId = DeterministicId("cider-bridge");
    private static readonly string HostId = DeterministicId("cider-host");
    private static readonly string NoneId = DeterministicId("cider-none");

    private readonly IContainerRuntime _runtime;
    private readonly IRecordStore<NetworkRecord> _store;
    private readonly EventBus _events;
    private readonly ILogger<NetworkManager> _logger;
    private Func<string, IReadOnlyList<(string ContainerId, string ContainerName, EndpointSettings Endpoint)>>? _endpointsProvider;
    private IDnsForwarderService? _dnsForwarders;
    private IContainerNetworkAttachments? _attachments;

    public NetworkManager(IContainerRuntime runtime, IRecordStore<NetworkRecord> store, EventBus events, ILogger<NetworkManager> logger)
    {
        _runtime = runtime;
        _store = store;
        _events = events;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NetworkResource>> ListAsync(Filters filters, CancellationToken ct)
    {
        await EnsureDefaultAsync(ct).ConfigureAwait(false);

        var records = new List<NetworkRecord>(_store.GetAll()) { HostRecord, NoneRecord };
        var result = new List<NetworkResource>();
        foreach (var record in records)
        {
            if (!MatchesFilters(record, filters))
            {
                continue;
            }

            result.Add(await ToResourceAsync(record, ct).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<NetworkResource> InspectAsync(string idOrName, bool verbose, string? scope, CancellationToken ct)
    {
        var record = await ResolveAsync(idOrName, ct).ConfigureAwait(false);
        return await ToResourceAsync(record, ct).ConfigureAwait(false);
    }

    public async Task<NetworkCreateResponse> CreateAsync(NetworkCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureDefaultAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(request.Driver) && request.Driver is not "bridge")
        {
            throw DockerErrors.NotImplemented($"cider: network driver '{request.Driver}' is not supported");
        }

        if (request.Name is BridgeName or HostName or NoneName || _store.Get(request.Name) is not null)
        {
            throw DockerErrors.Conflict($"network with name {request.Name} already exists");
        }

        var id = DockerId.New();
        var labels = new Dictionary<string, string>(request.Labels) { [ContainerIdentity.IdLabel] = id };
        var subnet = request.IPAM?.Config?.FirstOrDefault()?.Subnet;

        var spec = new NetworkSpec
        {
            Name = RuntimeNameFor(request.Name),
            Subnet = subnet,
            Internal = request.Internal,
            Labels = RuntimeSafeLabels(labels),
            Options = request.Options,
        };

        try
        {
            await _runtime.CreateNetworkAsync(spec, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        var record = new NetworkRecord
        {
            Id = id,
            Name = request.Name,
            Request = request,
            Created = DateTimeOffset.UtcNow,
            RuntimeName = spec.Name,
        };
        _store.Upsert(request.Name, record);
        _events.Publish(DockerEvents.Network("create", id, request.Name));

        return new NetworkCreateResponse { Id = id, Warning = "" };
    }

    public async Task RemoveAsync(string idOrName, CancellationToken ct)
    {
        var record = await ResolveAsync(idOrName, ct).ConfigureAwait(false);
        if (record.Name is BridgeName or HostName or NoneName)
        {
            throw new DockerApiException(HttpStatusCode.Forbidden, $"{record.Name} is a pre-defined network and cannot be removed");
        }

        // The network's own DNS forwarder is attached to it, so it has to go first or Apple
        // refuses the delete with "has active endpoints".
        await ReleaseDnsForwarderAsync(record.Name, ct).ConfigureAwait(false);

        try
        {
            await _runtime.RemoveNetworkAsync(RuntimeNameFor(record.Name), ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.Conflict)
        {
            throw DockerErrors.Conflict($"error while removing network: network {record.Name} id {record.Id} has active endpoints");
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        _store.Delete(record.Name);
        _events.Publish(DockerEvents.Network("destroy", record.Id, record.Name));
    }

    /// <summary>
    /// <c>POST /networks/{id}/connect</c>. Apple <c>container</c> fixes a container's networks when
    /// the container is created, so this only works for a container that has never been started:
    /// <see cref="IContainerNetworkAttachments"/> updates the record and re-creates the engine
    /// container with the extended network list. Anything else answers 501.
    /// </summary>
    public async Task ConnectAsync(string idOrName, NetworkConnectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = await ResolveAsync(idOrName, ct).ConfigureAwait(false);
        RequireAttachableNetwork(record);
        RequireContainer(request.Container);

        // No container manager wired (a host that maps only the resource routes): answer exactly
        // what these endpoints answered before connect/disconnect existed.
        if (_attachments is null)
        {
            throw DockerErrors.NotImplemented(ConnectNotSupported("running"));
        }

        var container = await _attachments
            .AttachToNetworkAsync(request.Container, record.Name, request.EndpointConfig, ct)
            .ConfigureAwait(false);

        _events.Publish(DockerEvents.Network("connect", record.Id, record.Name, container.Id));
    }

    /// <summary>
    /// <c>POST /networks/{id}/disconnect</c>; the mirror image of <see cref="ConnectAsync"/> and
    /// bound by the same rule (never-started containers only). <c>Force</c> is accepted and ignored:
    /// it only means anything for a running container, which is refused either way.
    /// </summary>
    public async Task DisconnectAsync(string idOrName, NetworkDisconnectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = await ResolveAsync(idOrName, ct).ConfigureAwait(false);
        RequireAttachableNetwork(record);
        RequireContainer(request.Container);

        // See ConnectAsync: without a container manager these stay 501, as they always were.
        if (_attachments is null)
        {
            throw DockerErrors.NotImplemented(DisconnectNotSupported("running"));
        }

        var container = await _attachments
            .DetachFromNetworkAsync(request.Container, record.Name, ct)
            .ConfigureAwait(false);

        _events.Publish(DockerEvents.Network("disconnect", record.Id, record.Name, container.Id));
    }

    /// <summary>501 message for connecting a container that is past <c>created</c>.</summary>
    public static string ConnectNotSupported(string containerStatus) =>
        $"cider: connecting a {DescribeState(containerStatus)} container to a network is not supported " +
        "by Apple container (networks must be set at create time)";

    /// <summary>501 message for disconnecting a container that is past <c>created</c>.</summary>
    public static string DisconnectNotSupported(string containerStatus) =>
        $"cider: disconnecting a {DescribeState(containerStatus)} container from a network is not supported " +
        "by Apple container (networks must be set at create time)";

    private static string DescribeState(string containerStatus) => containerStatus switch
    {
        "exited" or "dead" => "stopped",
        "" or null => "running",
        _ => containerStatus,
    };

    private static void RequireContainer(string container)
    {
        if (string.IsNullOrEmpty(container))
        {
            throw DockerErrors.BadParameter("no container specified");
        }
    }

    private static void RequireAttachableNetwork(NetworkRecord record)
    {
        if (record.Name is HostName or NoneName)
        {
            throw DockerErrors.BadParameter(
                $"cider: network '{record.Name}' is not supported by Apple container");
        }
    }

    public async Task<NetworkPruneResponse> PruneAsync(Filters filters, CancellationToken ct)
    {
        // dockerd's networksAcceptedFilters (moby/daemon/prune.go / daemon/network/filter.go's
        // NewPruneFilter). `until` was accepted here but never actually applied to a candidate's
        // creation time, so it silently pruned regardless of the value.
        filters = (filters ?? Filters.Empty).Validate("label", "label!", "until");
        var until = filters.ResolveUntil();
        var deleted = new List<string>();
        foreach (var record in _store.GetAll().ToList())
        {
            var containers = _endpointsProvider?.Invoke(record.Name) ?? [];
            if (containers.Count > 0)
            {
                continue;
            }

            if (!filters.MatchesLabels(record.Request.Labels))
            {
                continue;
            }

            if (until is not null && record.Created > until)
            {
                continue;
            }

            await ReleaseDnsForwarderAsync(record.Name, ct).ConfigureAwait(false);

            try
            {
                await _runtime.RemoveNetworkAsync(RuntimeNameFor(record.Name), ct).ConfigureAwait(false);
            }
            catch (RuntimeException)
            {
                continue;
            }

            _store.Delete(record.Name);
            _events.Publish(DockerEvents.Network("destroy", record.Id, record.Name));
            deleted.Add(record.Name);
        }

        return new NetworkPruneResponse { NetworksDeleted = deleted };
    }

    public async Task<NetworkRecord> ResolveAsync(string idOrName, CancellationToken ct)
    {
        await EnsureDefaultAsync(ct).ConfigureAwait(false);

        if (idOrName is BridgeName or "default")
        {
            return _store.Get(BridgeName)!;
        }

        if (idOrName == HostName)
        {
            return HostRecord;
        }

        if (idOrName == NoneName)
        {
            return NoneRecord;
        }

        var byName = _store.Get(idOrName);
        if (byName is not null)
        {
            return byName;
        }

        var all = _store.GetAll().Append(HostRecord).Append(NoneRecord).ToList();
        var exact = all.FirstOrDefault(r => string.Equals(r.Id, idOrName, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        if (DockerId.IsHexPrefix(idOrName))
        {
            var matches = all.Where(r => r.Id.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        throw DockerErrors.NoSuchNetwork(idOrName);
    }

    private async Task ReleaseDnsForwarderAsync(string dockerNetworkName, CancellationToken ct)
    {
        if (_dnsForwarders is null)
        {
            return;
        }

        try
        {
            await _dnsForwarders.ReleaseAsync(dockerNetworkName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RuntimeException or IOException)
        {
            _logger.LogWarning(ex, "could not release the DNS forwarder of network {Network}", dockerNetworkName);
        }
    }

    /// <summary>
    /// The name the Apple runtime knows a Docker network by: Docker's <c>bridge</c> is Apple's
    /// <c>default</c>, and every other name is passed through unless Apple cannot represent it.
    /// <para>
    /// Apple <c>container network create</c> only accepts <c>[a-z0-9_-]</c> and refuses a leading or
    /// trailing <c>-</c>, while the Docker Engine API accepts far more — Aspire's DCP asks for
    /// <c>aspire-session-network-&lt;rand8&gt;-&lt;app&gt;-</c>, which is refused for its trailing
    /// hyphen alone. Such a name is folded into a safe one plus a short hash of the original, so two
    /// different Docker names can never land on the same runtime name. Nothing observable changes:
    /// the Docker-visible name, id and labels all come from this manager's own record store.
    /// </para>
    /// <para>
    /// This is the single mapping point — container create, the connect/disconnect re-create, the
    /// reconciler and the DNS forwarder all go through it, so they always agree.
    /// </para>
    /// </summary>
    public string RuntimeNameFor(string dockerNetworkName) =>
        string.Equals(dockerNetworkName, BridgeName, StringComparison.Ordinal)
            ? "default"
            : SanitizeRuntimeName(dockerNetworkName);

    /// <summary>
    /// Maps a network <em>id</em> (full or an unambiguous prefix) onto its Docker name; a name, or
    /// anything this daemon does not know, comes back unchanged. Docker resolves
    /// <c>HostConfig.NetworkMode</c> and the <c>EndpointsConfig</c> keys by id as happily as by name
    /// and DCP relies on that, but Apple <c>container create --network</c> only takes the name.
    /// </summary>
    public string ResolveDockerName(string idOrName)
    {
        if (string.IsNullOrEmpty(idOrName) || _store.Get(idOrName) is not null)
        {
            return idOrName;
        }

        var all = _store.GetAll().Append(HostRecord).Append(NoneRecord).ToList();
        var exact = all.FirstOrDefault(r => string.Equals(r.Id, idOrName, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact.Name;
        }

        if (DockerId.IsHexPrefix(idOrName))
        {
            var matches = all.Where(r => r.Id.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0].Name;
            }
        }

        return idOrName;
    }

    /// <summary>How much of a sanitised name is kept in front of the disambiguating hash.</summary>
    private const int SanitizedPrefixLength = 24;

    /// <summary>Folds a Docker network name into one Apple <c>container</c> accepts; see <see cref="RuntimeNameFor"/>.</summary>
    internal static string SanitizeRuntimeName(string dockerNetworkName)
    {
        if (string.IsNullOrEmpty(dockerNetworkName) || IsRuntimeSafeName(dockerNetworkName))
        {
            return dockerNetworkName;
        }

        var builder = new StringBuilder(dockerNetworkName.Length);
        foreach (var character in dockerNetworkName)
        {
            builder.Append(character switch
            {
                >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' => character,
                >= 'A' and <= 'Z' => char.ToLowerInvariant(character),
                _ => '-',
            });
        }

        var prefix = builder.ToString().Trim('-');
        if (prefix.Length > SanitizedPrefixLength)
        {
            prefix = prefix[..SanitizedPrefixLength].TrimEnd('-');
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dockerNetworkName)))[..8];
        return prefix.Length > 0 ? prefix + "-" + hash : "net-" + hash;
    }

    private static bool IsRuntimeSafeName(string name)
    {
        if (name[0] == '-' || name[^1] == '-')
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The subset of a network's labels Apple can store. Apple <c>network create --label</c> — and
    /// only that one, <c>container create</c> and <c>volume create</c> do not validate — rejects any
    /// key outside <c>[a-z0-9.-]</c> with <c>invalid_label_key_content</c>, which is what turns
    /// Aspire/DCP's <c>com.microsoft.developer.usvc-dev.creatorProcessId</c> into a 500 and stops an
    /// Aspire app before it starts anything. Everything Docker-visible about a network's labels is
    /// answered from <see cref="NetworkRecord.Request"/>, so dropping the rest here loses nothing.
    /// </summary>
    private static Dictionary<string, string> RuntimeSafeLabels(Dictionary<string, string> labels)
    {
        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            if (IsRuntimeSafeLabelKey(key))
            {
                safe[key] = value;
            }
        }

        return safe;
    }

    private static bool IsRuntimeSafeLabelKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Cached lookup of a Docker network's record by its Docker-facing name, with no runtime call.
    /// Used by <see cref="ContainerManager"/> right after a container starts to attach
    /// <c>NetworkID</c>/subnet info to its <see cref="EndpointSettings"/> without waiting on another
    /// <c>container network inspect</c>.
    /// </summary>
    public NetworkRecord? TryGetCachedRecord(string dockerNetworkName) => dockerNetworkName switch
    {
        HostName => HostRecord,
        NoneName => NoneRecord,
        _ => _store.Get(dockerNetworkName),
    };

    public async Task<RuntimeNetwork?> GetRuntimeNetworkAsync(string dockerNetworkName, CancellationToken ct)
    {
        if (dockerNetworkName is HostName or NoneName)
        {
            return null;
        }

        try
        {
            return await _runtime.InspectNetworkAsync(RuntimeNameFor(dockerNetworkName), ct).ConfigureAwait(false);
        }
        catch (RuntimeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The IPv4 subnet a network actually has, in CIDR form, or <c>null</c> when it has none yet.
    /// Prefers what the runtime reports over what the create request asked for, since Apple picks
    /// the range itself. Used to validate a client's requested static IP before create returns.
    /// </summary>
    public async Task<string?> SubnetOfAsync(string dockerNetworkName, CancellationToken ct)
    {
        var runtimeNetwork = await GetRuntimeNetworkAsync(dockerNetworkName, ct).ConfigureAwait(false);
        return runtimeNetwork?.Subnet is { Length: > 0 } subnet
            ? subnet
            : TryGetCachedRecord(dockerNetworkName)?.Request.IPAM?.Config?.FirstOrDefault()?.Subnet;
    }

    /// <summary>
    /// Registers the DNS forwarder service. It is not a constructor dependency because the
    /// forwarder service itself depends on this manager; the daemon wires the two together at
    /// startup, exactly like <see cref="SetContainerEndpoints"/>.
    /// </summary>
    public void SetDnsForwarders(IDnsForwarderService forwarders) => _dnsForwarders = forwarders;

    public void SetContainerEndpoints(Func<string, IReadOnlyList<(string ContainerId, string ContainerName, EndpointSettings Endpoint)>> provider) =>
        _endpointsProvider = provider;

    /// <summary>
    /// Registers the container manager that carries out <see cref="ConnectAsync"/>/
    /// <see cref="DisconnectAsync"/>. Wired by <see cref="ContainerManager"/>'s constructor, exactly
    /// like <see cref="SetContainerEndpoints"/>, because the dependency runs the other way round.
    /// Without it (a host that maps the network routes but has no container manager) connect and
    /// disconnect answer 501, as they did before either was implemented.
    /// </summary>
    public void SetContainerAttachments(IContainerNetworkAttachments attachments) => _attachments = attachments;

    public async Task EnsureDefaultAsync(CancellationToken ct)
    {
        if (_store.Get(BridgeName) is not null)
        {
            return;
        }

        RuntimeNetwork? runtimeNetwork = null;
        try
        {
            runtimeNetwork = await _runtime.InspectNetworkAsync("default", ct).ConfigureAwait(false);
        }
        catch (RuntimeException)
        {
        }

        var request = new NetworkCreateRequest
        {
            Name = BridgeName,
            Driver = "bridge",
            IPAM = new Ipam
            {
                Config = runtimeNetwork?.Subnet is not null
                    ? [new IpamConfig { Subnet = runtimeNetwork.Subnet, Gateway = runtimeNetwork.Gateway }]
                    : [],
            },
        };

        var record = new NetworkRecord
        {
            Id = BridgeId,
            Name = BridgeName,
            Request = request,
            Created = DateTimeOffset.UtcNow,
            RuntimeName = "default",
        };
        _store.Upsert(BridgeName, record);
    }

    // ---- helpers ------------------------------------------------------

    private static NetworkRecord HostRecord => new()
    {
        Id = HostId,
        Name = HostName,
        Request = new NetworkCreateRequest { Name = HostName, Driver = "host" },
        Created = DockerTime.ZeroTimeValue,
        RuntimeName = HostName,
    };

    private static NetworkRecord NoneRecord => new()
    {
        Id = NoneId,
        Name = NoneName,
        Request = new NetworkCreateRequest { Name = NoneName, Driver = "null" },
        Created = DockerTime.ZeroTimeValue,
        RuntimeName = NoneName,
    };

    /// <summary>
    /// Renders one endpoint address the way <c>GET /networks/{id}</c> does: <c>&lt;ip&gt;/&lt;prefix&gt;</c>,
    /// falling back to the network's own subnet prefix when the endpoint does not carry one.
    /// </summary>
    private static string ToCidr(string? address, int prefixLength, string? subnet)
    {
        if (string.IsNullOrEmpty(address))
        {
            return "";
        }

        if (address.Contains('/', StringComparison.Ordinal))
        {
            return address;
        }

        var length = prefixLength > 0 ? prefixLength : PrefixLengthOf(subnet);
        return length > 0 ? address + "/" + length.ToString(CultureInfo.InvariantCulture) : address;
    }

    private static int PrefixLengthOf(string? subnet)
    {
        if (string.IsNullOrEmpty(subnet))
        {
            return 0;
        }

        var slash = subnet.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && int.TryParse(subnet.AsSpan(slash + 1), CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private async Task<NetworkResource> ToResourceAsync(NetworkRecord record, CancellationToken ct)
    {
        RuntimeNetwork? runtimeNetwork = null;
        if (record.Name is not (HostName or NoneName))
        {
            try
            {
                runtimeNetwork = await _runtime.InspectNetworkAsync(RuntimeNameFor(record.Name), ct).ConfigureAwait(false);
            }
            catch (RuntimeException)
            {
            }
        }

        var subnet = runtimeNetwork?.Subnet ?? record.Request.IPAM?.Config?.FirstOrDefault()?.Subnet;
        var gateway = runtimeNetwork?.Gateway ?? record.Request.IPAM?.Config?.FirstOrDefault()?.Gateway;
        var ipamConfig = new List<IpamConfig>();
        if (subnet is not null)
        {
            ipamConfig.Add(new IpamConfig { Subnet = subnet, Gateway = gateway });
        }

        var containers = new Dictionary<string, EndpointResource>();
        if (_endpointsProvider is not null && record.Name is not (HostName or NoneName))
        {
            foreach (var (containerId, containerName, endpoint) in _endpointsProvider(record.Name))
            {
                containers[containerId] = new EndpointResource
                {
                    Name = containerName,
                    EndpointID = endpoint.EndpointID,
                    MacAddress = endpoint.MacAddress ?? "",
                    // Docker reports these in CIDR form here (unlike NetworkSettings.IPAddress);
                    // the docker CLI feeds them to netip.ParsePrefix, which panics on a bare IP.
                    IPv4Address = ToCidr(endpoint.IPAddress, endpoint.IPPrefixLen, subnet),
                    IPv6Address = ToCidr(endpoint.GlobalIPv6Address, endpoint.GlobalIPv6PrefixLen, null),
                };
            }
        }

        return new NetworkResource
        {
            Name = record.Name,
            Id = record.Id,
            Created = DockerTime.Format(record.Created),
            Scope = "local",
            Driver = record.Name switch { HostName => "host", NoneName => "null", _ => "bridge" },
            IPAM = new Ipam { Config = ipamConfig },
            Internal = record.Request.Internal,
            Attachable = record.Request.Attachable,
            Containers = containers,
            Options = new Dictionary<string, string>(record.Request.Options),
            Labels = new Dictionary<string, string>(record.Request.Labels),
        };
    }

    private static bool MatchesFilters(NetworkRecord record, Filters filters)
    {
        if (filters.IsEmpty)
        {
            return true;
        }

        if (!filters.MatchName(record.Name))
        {
            return false;
        }

        if (!filters.MatchId(record.Id))
        {
            return false;
        }

        if (!filters.MatchesLabels(record.Request.Labels))
        {
            return false;
        }

        var driver = record.Name switch { HostName => "host", NoneName => "null", _ => "bridge" };
        return filters.MatchExact("driver", driver);
    }

    private static string DeterministicId(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
}

/// <summary>
/// The container-side half of <c>docker network connect</c>/<c>disconnect</c>, implemented by
/// <see cref="ContainerManager"/> and registered with <see cref="NetworkManager"/> at construction
/// time. Both operations only ever succeed for a container that was created and never started.
/// </summary>
public interface IContainerNetworkAttachments
{
    /// <summary>Adds one network to a never-started container and re-creates it on the engine.</summary>
    Task<ContainerRecord> AttachToNetworkAsync(
        string containerIdOrName,
        string dockerNetworkName,
        EndpointSettings? endpointConfig,
        CancellationToken ct);

    /// <summary>Removes one network from a never-started container and re-creates it on the engine.</summary>
    Task<ContainerRecord> DetachFromNetworkAsync(string containerIdOrName, string dockerNetworkName, CancellationToken ct);
}
