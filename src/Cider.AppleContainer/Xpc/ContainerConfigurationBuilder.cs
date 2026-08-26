using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Builds a <see cref="ContainerConfiguration"/> from an already-merged <see cref="ContainerSpec"/> —
/// the .NET equivalent of Apple's own <c>Utility.containerConfigFromFlags</c> +
/// <c>Parser.process</c> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §3.2), except for the
/// entrypoint/cmd/env/workdir/user/stop-signal merge against the image config that §3.2 item 6
/// describes: <c>ContainerManager.CreateAsync</c> (<c>Cider.Core</c>) already performs that merge,
/// Docker-style, before a <see cref="ContainerSpec"/> is ever built — <see cref="Cider.Core.Runtime.ContainerSpec.Entrypoint"/>/
/// <see cref="Cider.Core.Runtime.ContainerSpec.Args"/>/<see cref="Cider.Core.Runtime.ContainerSpec.Env"/>/
/// <see cref="Cider.Core.Runtime.ContainerSpec.WorkingDir"/>/<see cref="Cider.Core.Runtime.ContainerSpec.User"/>/
/// <see cref="Cider.Core.Runtime.ContainerSpec.StopSignal"/> arrive here already resolved. This type
/// only ever translates already-decided values into the wire shape — no XPC call, no image-config
/// read (there is no route for one; §6's own note: "the apiserver never reads an image config" and
/// reading one client-side is three more round trips this task's non-goals explicitly exclude) — so
/// it is a plain, deterministic function, testable straight from fixtures
/// (<c>tests/Cider.Tests/AppleContainer/Xpc/ContainerConfigurationBuilderTests.cs</c>).
/// </summary>
internal static class ContainerConfigurationBuilder
{
    /// <summary><c>ManagedContainer.nameValid</c> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §3.1):
    /// note the <c>+</c> quantifier — a single-character id is rejected.</summary>
    private static readonly Regex ContainerIdPattern =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericUserPattern =
        new(@"^\d+(?::\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Docker <c>--ulimit</c> name → Apple <c>Rlimit.limit</c> (Parser.swift:930-945,
    /// cited in this task's own fix direction). A name outside this table is dropped rather than
    /// failing the whole create — the same "defensive, not a hard requirement" posture the CLI
    /// mapper already takes for fields the wire cannot express.</summary>
    private static readonly Dictionary<string, string> RlimitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = "RLIMIT_CORE",
        ["cpu"] = "RLIMIT_CPU",
        ["data"] = "RLIMIT_DATA",
        ["fsize"] = "RLIMIT_FSIZE",
        ["locks"] = "RLIMIT_LOCKS",
        ["memlock"] = "RLIMIT_MEMLOCK",
        ["msgqueue"] = "RLIMIT_MSGQUEUE",
        ["nice"] = "RLIMIT_NICE",
        ["nofile"] = "RLIMIT_NOFILE",
        ["nproc"] = "RLIMIT_NPROC",
        ["rss"] = "RLIMIT_RSS",
        ["rtprio"] = "RLIMIT_RTPRIO",
        ["rttime"] = "RLIMIT_RTTIME",
        ["sigpending"] = "RLIMIT_SIGPENDING",
        ["stack"] = "RLIMIT_STACK",
    };

    /// <summary>Numeric signals a Docker client sends, mapped to the bare name Apple's runtime
    /// accepts before the <c>SIG</c> prefix this class always adds back on
    /// (<see cref="NormalizeSignal"/>) — the XPC-side counterpart of <c>Cli.ArgBuilder.SignalNames</c>.</summary>
    private static readonly Dictionary<string, string> SignalNumbers = new(StringComparer.Ordinal)
    {
        ["1"] = "HUP",
        ["2"] = "INT",
        ["3"] = "QUIT",
        ["6"] = "ABRT",
        ["9"] = "KILL",
        ["10"] = "USR1",
        ["12"] = "USR2",
        ["14"] = "ALRM",
        ["15"] = "TERM",
    };

    /// <summary>Resolved data <see cref="Build"/> needs beyond the spec itself: each named volume
    /// mount's on-disk configuration (keyed by volume name — resolved via <c>volumeInspect</c> by
    /// <see cref="XpcContainerRuntime.CreateContainerAsync"/> before calling <see cref="Build"/>, so
    /// this type never has to make an XPC call of its own), and the system DNS domain used only for
    /// the attachment FQDN rule (§3.4) — <see cref="SystemDnsDomainResolver"/> reads
    /// <c>containerSystemConfig.dns.domain</c> (config.toml, else <c>container system property list</c>,
    /// cached) the same way <see cref="InitImageResolver"/> reads the vminit reference; still
    /// <c>null</c> whenever no domain is configured, which is the common case (confirmed live:
    /// <c>"dns":{}</c>) — see <see cref="BuildNetworks"/>'s own doc comment.</summary>
    public readonly record struct BuildContext(IReadOnlyDictionary<string, VolumeConfiguration> Volumes, string? DnsDomain)
    {
        public static BuildContext Empty { get; } = new(new Dictionary<string, VolumeConfiguration>(), null);
    }

    public static ContainerConfiguration Build(ContainerSpec spec, ImageDescription image, BuildContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(image);

        ValidateId(spec.RuntimeId);

        var (executable, arguments) = SplitCommand(spec);
        var targetPlatform = ResolveTargetPlatform(spec.Platform);

        var initProcess = new ProcessConfiguration
        {
            Executable = executable,
            Arguments = arguments,
            Environment = [.. spec.Env.Where(static e => e.Contains('=', StringComparison.Ordinal))],
            WorkingDirectory = string.IsNullOrEmpty(spec.WorkingDir) ? "/" : spec.WorkingDir,
            Terminal = spec.Tty,
            User = BuildUser(spec.User),
            SupplementalGroups = [],
            Rlimits = BuildRlimits(spec.Ulimits),
        };

        return new ContainerConfiguration
        {
            Id = spec.RuntimeId,
            Image = image,
            InitProcess = initProcess,
            Mounts = BuildMounts(spec, context.Volumes),
            PublishedPorts = BuildPublishedPorts(spec.Ports),
            PublishedSockets = BuildPublishedSockets(spec.PublishSockets),
            Labels = spec.Labels.Count > 0 ? new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal) : [],
            Sysctls = spec.Sysctls.Count > 0 ? new Dictionary<string, string>(spec.Sysctls, StringComparer.Ordinal) : [],
            Networks = BuildNetworks(spec, context.DnsDomain),
            Dns = BuildDns(spec),
            Rosetta = ResolveRosetta(targetPlatform),
            Platform = targetPlatform,
            Resources = BuildResources(spec),
            RuntimeHandler = "container-runtime-linux",
            Virtualization = false,
            Ssh = false,
            ReadOnly = spec.ReadOnlyRootfs,
            UseInit = spec.Init,
            CapAdd = BuildCapAdd(spec),
            CapDrop = BuildCapDrop(spec),
            ShmSize = spec.ShmSizeBytes is { } shm && shm > 0 ? (ulong)shm : null,
            StopSignal = string.IsNullOrEmpty(spec.StopSignal) ? null : NormalizeSignal(spec.StopSignal, "TERM"),
            MaskedPaths = spec.Privileged ? [] : null,
            ReadonlyPaths = spec.Privileged ? [] : null,
            CreationDate = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// <c>--entrypoint</c> set → executable = entrypoint, arguments = <see cref="ContainerSpec.Args"/>
    /// verbatim (Docker's own Entrypoint+Cmd split — cider already keeps them separate on
    /// <see cref="ContainerSpec"/>, unlike Apple's flattened <c>argv</c> the CLI transport's
    /// <c>ArgBuilder</c> builds). Otherwise executable = <c>Args[0]</c>, arguments = the rest. Empty
    /// result on both → <c>invalidArgument</c> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §3.2
    /// item 6: "empty result → invalidArgument").
    /// </summary>
    private static (string Executable, List<string> Arguments) SplitCommand(ContainerSpec spec)
    {
        if (!string.IsNullOrEmpty(spec.Entrypoint))
        {
            return (spec.Entrypoint, [.. spec.Args]);
        }

        if (spec.Args.Count > 0)
        {
            return (spec.Args[0], [.. spec.Args.Skip(1)]);
        }

        throw RuntimeException.InvalidArgument($"cider: container '{spec.RuntimeId}' has no entrypoint or command");
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 63 || !ContainerIdPattern.IsMatch(id))
        {
            throw RuntimeException.InvalidArgument($"cider: invalid container id '{id}'");
        }
    }

    /// <summary>
    /// Numeric <c>uid[:gid]</c> → <see cref="User.OfId"/>; any other non-empty string → <see cref="User.OfRaw"/>;
    /// unset → <c>id{0,0}</c> (root) — task fix direction §1's <c>User</c> rule, minus the "else image
    /// config.user" branch <see cref="ContainerConfigurationBuilder"/>'s own doc comment explains is
    /// already folded into <see cref="ContainerSpec.User"/> by the time this runs. <c>internal</c> so
    /// <see cref="ProcessConfigurationBuilder"/> (task cider-ede.8) can reuse the same parser for
    /// <c>ExecSpec.User</c> — its own fix direction §1 calls this out by name ("same parser as X5").
    /// </summary>
    internal static User BuildUser(string? user)
    {
        if (string.IsNullOrEmpty(user))
        {
            return User.OfId(0, 0);
        }

        if (!NumericUserPattern.IsMatch(user))
        {
            return User.OfRaw(user);
        }

        var parts = user.Split(':', 2);
        var uid = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var gid = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return User.OfId(uid, gid);
    }

    private static List<Rlimit> BuildRlimits(IReadOnlyList<UlimitSpec> ulimits)
    {
        if (ulimits.Count == 0)
        {
            return [];
        }

        var result = new List<Rlimit>(ulimits.Count);
        foreach (var ulimit in ulimits)
        {
            if (!RlimitNames.TryGetValue(ulimit.Name, out var limit))
            {
                continue;
            }

            result.Add(new Rlimit { Limit = limit, Soft = ToUnsigned(ulimit.Soft), Hard = ToUnsigned(ulimit.Hard) });
        }

        return result;
    }

    private static ulong ToUnsigned(long value) => value < 0 ? ulong.MaxValue : (ulong)value;

    /// <summary>
    /// Bind → <c>virtiofs</c> with an absolute host <see cref="Filesystem.Source"/> (task fix
    /// direction §1); Volume → <c>volume</c>, resolved against <paramref name="volumes"/> (populated
    /// by a <c>volumeInspect</c> the caller already ran — see <see cref="BuildContext"/>'s doc
    /// comment), <c>cache</c>/<c>sync</c> defaulted to <c>on</c>/<c>fsync</c> exactly like Apple's own
    /// <c>Filesystem.volume(...)</c> builder (Filesystem.swift, cited in this task's fix direction);
    /// Tmpfs (both <see cref="MountSpec"/> and the separate <see cref="ContainerSpec.Tmpfs"/> list) →
    /// <c>tmpfs</c> with the literal <c>"tmpfs"</c> source string.
    /// </summary>
    private static List<Filesystem> BuildMounts(ContainerSpec spec, IReadOnlyDictionary<string, VolumeConfiguration> volumes)
    {
        var result = new List<Filesystem>(spec.Mounts.Count + spec.Tmpfs.Count);

        foreach (var mount in spec.Mounts)
        {
            var options = mount.ReadOnly ? new List<string> { "ro" } : [];
            switch (mount.Kind)
            {
                case MountKind.Bind:
                    result.Add(new Filesystem
                    {
                        Type = FsType.OfVirtiofs(),
                        Source = AbsolutePath(mount.Source),
                        Destination = mount.Target,
                        Options = options,
                    });
                    break;

                case MountKind.Volume:
                    if (!volumes.TryGetValue(mount.Source, out var volume))
                    {
                        throw RuntimeException.NotFound($"cider: volume '{mount.Source}' does not exist");
                    }

                    result.Add(new Filesystem
                    {
                        Type = FsType.OfVolume(new VolumeFs
                        {
                            Name = volume.Name,
                            Format = volume.Format,
                            Cache = new SingleKeyCase("on"),
                            Sync = new SingleKeyCase("fsync"),
                        }),
                        Source = AbsolutePath(volume.Source),
                        Destination = mount.Target,
                        Options = options,
                    });
                    break;

                case MountKind.Tmpfs:
                    result.Add(new Filesystem
                    {
                        Type = FsType.OfTmpfs(),
                        Source = "tmpfs",
                        Destination = mount.Target,
                        Options = options,
                    });
                    break;

                default:
                    throw RuntimeException.InvalidArgument($"cider: unsupported mount kind '{mount.Kind}'");
            }
        }

        foreach (var tmpfs in spec.Tmpfs)
        {
            result.Add(new Filesystem
            {
                Type = FsType.OfTmpfs(),
                Source = "tmpfs",
                Destination = tmpfs.Target,
                Options = [],
            });
        }

        return result;
    }

    private static string AbsolutePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

    private static List<PublishPort> BuildPublishedPorts(IReadOnlyList<PortSpec> ports)
    {
        if (ports.Count == 0)
        {
            return [];
        }

        var result = new List<PublishPort>(ports.Count);
        foreach (var port in ports)
        {
            result.Add(new PublishPort
            {
                HostAddress = string.IsNullOrEmpty(port.HostIp) ? "0.0.0.0" : port.HostIp,
                HostPort = (ushort)port.HostPort,
                ContainerPort = (ushort)port.ContainerPort,
                Proto = string.IsNullOrEmpty(port.Proto) ? "tcp" : port.Proto.ToLowerInvariant(),
                Count = 1,
            });
        }

        return result;
    }

    /// <summary><c>"hostPath:containerPath"</c> — the same shape <c>Cli.ArgBuilder</c> passes straight
    /// through to <c>--publish-socket</c>. Nothing above the seam populates
    /// <see cref="ContainerSpec.PublishSockets"/> today, so this is exercised only defensively.</summary>
    private static List<PublishSocket> BuildPublishedSockets(IReadOnlyList<string> sockets)
    {
        if (sockets.Count == 0)
        {
            return [];
        }

        var result = new List<PublishSocket>(sockets.Count);
        foreach (var socket in sockets)
        {
            var separator = socket.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == socket.Length - 1)
            {
                continue;
            }

            result.Add(new PublishSocket { HostPath = socket[..separator], ContainerPath = socket[(separator + 1)..] });
        }

        return result;
    }

    /// <summary>
    /// <c>Docker none → []</c>; otherwise one <see cref="AttachmentConfiguration"/> per network, mtu
    /// 1280, with <c>options.hostname</c> = <see cref="ContainerSpec.Hostname"/> when set, else the
    /// CLI's own FQDN rule applied per attachment (<c>Utility.getAttachmentConfigurations</c>,
    /// docs/spikes/xpc/02-apiserver-xpc-protocol.md §3.4: first attachment gets
    /// <c>"&lt;id&gt;.&lt;dnsDomain&gt;."</c>, or <c>"&lt;id&gt;."</c> when the id itself already
    /// contains a dot, or the bare id when there is no domain; every other attachment gets the bare
    /// id). <paramref name="dnsDomain"/> comes from <see cref="SystemDnsDomainResolver"/> (see
    /// <see cref="BuildContext"/>'s doc comment) — <c>null</c> on the common install with no domain
    /// configured, in which case every attachment's default (no explicit hostname) is simply the bare
    /// container id, exactly what the CLI transport already produced before this task.
    /// <see cref="ContainerSpec.Hostname"/> is <c>null</c> (not the resolved default) whenever the
    /// Docker client sent no <c>--hostname</c> — <c>ContainerManager.CreateAsync</c> keeps that
    /// distinction explicit precisely so this FQDN rule can apply — so "set" here really means
    /// "explicitly set", not "resolved to something".
    /// </summary>
    private static List<AttachmentConfiguration> BuildNetworks(ContainerSpec spec, string? dnsDomain)
    {
        if (spec.Networks.Count == 0)
        {
            return [];
        }

        var fqdn = ComputeFqdn(spec.RuntimeId, dnsDomain);
        var result = new List<AttachmentConfiguration>(spec.Networks.Count);
        for (var i = 0; i < spec.Networks.Count; i++)
        {
            var isFirst = i == 0;
            var hostname = !string.IsNullOrEmpty(spec.Hostname)
                ? spec.Hostname
                : isFirst ? fqdn ?? spec.RuntimeId : spec.RuntimeId;

            result.Add(new AttachmentConfiguration
            {
                Network = spec.Networks[i],
                Options = new AttachmentOptions { Hostname = hostname, Mtu = 1280 },
            });
        }

        return result;
    }

    private static string? ComputeFqdn(string id, string? dnsDomain)
    {
        if (id.Contains('.', StringComparison.Ordinal))
        {
            return id + ".";
        }

        return dnsDomain is { Length: > 0 } domain ? $"{id}.{domain}." : null;
    }

    private static DnsConfiguration BuildDns(ContainerSpec spec) => new()
    {
        Nameservers = [.. spec.DnsServers],
        SearchDomains = [.. spec.DnsSearch],
        Options = [.. spec.DnsOptions],
    };

    /// <summary><c>cpus: max(1, round(spec.Cpus))</c>, defaulted to Apple's own 4 when unset;
    /// <c>memoryInBytes: max(200 MiB, spec.MemoryBytes)</c> — the server rejects anything below that
    /// floor (§2.2: "Server rejects memoryInBytes &lt; 200 MiB"); <c>cpuOverhead: 1</c> (task fix
    /// direction §1, verification fixture (f)).</summary>
    private static Resources BuildResources(ContainerSpec spec)
    {
        const ulong memoryFloor = 200UL * 1024 * 1024;
        const ulong defaultMemory = 1024UL * 1024 * 1024;

        var cpus = spec.Cpus is { } requested
            ? Math.Max(1, (int)Math.Round(requested, MidpointRounding.AwayFromZero))
            : 4;

        var memory = spec.MemoryBytes is { } requestedMemory && requestedMemory > 0 ? (ulong)requestedMemory : defaultMemory;

        return new Resources
        {
            Cpus = cpus,
            MemoryInBytes = Math.Max(memoryFloor, memory),
            CpuOverhead = 1,
        };
    }

    /// <summary>Uppercased <c>CAP_*</c> (<c>ALL</c> preserved bare) — Parser.swift's own normalization
    /// rule, cited in this task's fix direction. <c>--privileged</c> adds <c>ALL</c> to
    /// <see cref="ContainerSpec.CapAdd"/> (task fix direction §1; the typed
    /// <see cref="ContainerConfiguration.MaskedPaths"/>/<see cref="ContainerConfiguration.ReadonlyPaths"/>
    /// <c>[]</c> in <see cref="Build"/> is this task's replacement for the CLI transport's
    /// <c>--masked-path NONE</c>/<c>--read-only-path NONE</c> sentinel pair).</summary>
    private static List<string> BuildCapAdd(ContainerSpec spec)
    {
        var result = NormalizeCaps(spec.CapAdd);
        if (spec.Privileged && !result.Contains("ALL", StringComparer.Ordinal))
        {
            result.Add("ALL");
        }

        return result;
    }

    private static List<string> BuildCapDrop(ContainerSpec spec) => NormalizeCaps(spec.CapDrop);

    private static List<string> NormalizeCaps(IReadOnlyList<string> caps)
    {
        var result = new List<string>();
        foreach (var cap in caps)
        {
            var normalized = NormalizeCap(cap);
            if (normalized.Length > 0 && !result.Contains(normalized, StringComparer.Ordinal))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string NormalizeCap(string cap)
    {
        var upper = cap.Trim().ToUpperInvariant();
        if (upper.Length == 0 || string.Equals(upper, "ALL", StringComparison.Ordinal))
        {
            return upper;
        }

        return upper.StartsWith("CAP_", StringComparison.Ordinal) ? upper : "CAP_" + upper;
    }

    /// <summary><c>--platform</c> (Docker <c>os/arch[/variant]</c>) parsed to a target
    /// <see cref="Platform"/>; Apple containers are always Linux, so only the architecture (and an
    /// optional variant) come from <paramref name="platformString"/>. Unset → the host's own platform
    /// (<see cref="Platform.Current"/>), exactly like <c>DefaultPlatform.resolveWithDefaults</c>
    /// (§3.2 item 1). Shared with <see cref="XpcContainerRuntime.CreateContainerAsync"/>, which needs
    /// the same target platform to resolve the image snapshot before <see cref="Build"/> ever runs.</summary>
    internal static Platform ResolveTargetPlatform(string? platformString)
    {
        if (string.IsNullOrEmpty(platformString))
        {
            return Platform.Current;
        }

        var parts = platformString.Split('/');
        var architecture = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : Platform.Current.Architecture;
        var variant = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
        return new Platform { Os = "linux", Architecture = architecture, Variant = variant };
    }

    /// <summary><c>config.rosetta = host arm64 &amp;&amp; target amd64</c> — auto-enabled, never a
    /// client flag on cider's side (§3.2 item 9; the CLI's own <c>--rosetta</c> throws on a non-arm64
    /// host, which is moot here since this condition can only be true on an arm64 host already).</summary>
    private static bool ResolveRosetta(Platform target) =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 &&
        string.Equals(target.Architecture, "amd64", StringComparison.OrdinalIgnoreCase);

    /// <summary>Docker/CLI signal forms (<c>"15"</c>, <c>"TERM"</c>, <c>"SIGTERM"</c>) → the canonical
    /// <c>"SIGxxx"</c> string <c>containerStop</c>/<c>containerKill</c>/<c>ContainerConfiguration.stopSignal</c>
    /// all want (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.7 sample: <c>"signal": "SIGTERM"</c>;
    /// §8.8: <c>signal</c> must be a string). <paramref name="fallback"/> is the bare signal name used
    /// when <paramref name="signal"/> is null/blank.</summary>
    internal static string NormalizeSignal(string? signal, string fallback)
    {
        if (string.IsNullOrWhiteSpace(signal))
        {
            return "SIG" + fallback;
        }

        var value = signal.Trim();
        if (SignalNumbers.TryGetValue(value, out var byNumber))
        {
            return "SIG" + byNumber;
        }

        if (value.StartsWith("SIG", StringComparison.OrdinalIgnoreCase))
        {
            value = value[3..];
        }

        return "SIG" + value.ToUpperInvariant();
    }
}
