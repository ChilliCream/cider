using System.Globalization;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.State;
using Cider.Core.Time;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    // Real dockerd's default masked/read-only proc & sys paths for a non-privileged container
    // (moby/moby's oci/defaults.go); a privileged container gets neither list.
    private static readonly string[] DefaultMaskedPaths =
    [
        "/proc/asound",
        "/proc/acpi",
        "/proc/interrupts",
        "/proc/kcore",
        "/proc/keys",
        "/proc/latency_stats",
        "/proc/timer_list",
        "/proc/timer_stats",
        "/proc/sched_debug",
        "/proc/scsi",
        "/sys/firmware",
        "/sys/devices/virtual/powercap",
    ];

    private static readonly string[] DefaultReadonlyPaths =
    [
        "/proc/bus",
        "/proc/fs",
        "/proc/irq",
        "/proc/sys",
        "/proc/sysrq-trigger",
    ];

    /// <summary><c>GET /containers/json</c>.</summary>
    public Task<IReadOnlyList<ContainerSummary>> ListAsync(bool all, int? limit, bool size, Filters filters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        filters ??= Filters.Empty;

        var now = DateTimeOffset.UtcNow;
        var matching = new List<ContainerRecord>();

        foreach (var record in _store.GetAll())
        {
            if (!all && !record.State.Running)
            {
                continue;
            }

            if (!Matches(record, filters))
            {
                continue;
            }

            matching.Add(record);
        }

        matching.Sort((left, right) => right.Created.CompareTo(left.Created));

        if (limit is > 0 && matching.Count > limit.Value)
        {
            matching = matching.GetRange(0, limit.Value);
        }

        IReadOnlyList<ContainerSummary> summaries = [.. matching.Select(record => ToSummary(record, now, size))];
        return Task.FromResult(summaries);
    }

    /// <summary><c>GET /containers/{id}/json</c>.</summary>
    public Task<ContainerInspectResponse> InspectAsync(string idOrName, bool size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var record = Resolve(idOrName);
        var config = record.Request;
        var hostConfig = BuildInspectHostConfig(record.Request.HostConfig);

        var response = new ContainerInspectResponse
        {
            Id = record.Id,
            Created = DockerTime.Format(record.Created),
            Path = record.Path,
            Args = [.. record.Args],
            State = BuildInspectState(record),
            Image = record.ImageId,
            ResolvConfPath = "",
            HostnamePath = "",
            HostsPath = "",
            LogPath = record.LogPath,
            Name = "/" + record.Name,
            RestartCount = record.RestartCount,
            Driver = "apple-container",
            Platform = "linux",
            ExecIDs = [],
            HostConfig = hostConfig,
            GraphDriver = new GraphDriverData { Name = "apple-container" },
            Mounts = record.Mounts,
            Config = config,
            NetworkSettings = BuildNetworkSettings(record),
        };

        if (size)
        {
            response.SizeRw = 0;
            response.SizeRootFs = 0;
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Builds the <c>HostConfig</c> inspect echoes back: the stored one, plus the fields real
    /// dockerd derives at container creation but that cider has no independent source for
    /// (the cgroup/IPC namespace mode, the runtime name, and the masked/read-only proc &amp; sys
    /// paths). Never overwrites a value the client set explicitly.
    /// </summary>
    /// <remarks>
    /// The defaults go onto a copy, never onto <paramref name="stored"/>: <c>CreateAsync</c> keeps
    /// the client's <c>HostConfig</c> instance in the record, so filling them in place would turn a
    /// read-only inspect into a write and persist synthesized values into the state file as if the
    /// client had sent them. A shallow copy is enough — nothing below mutates a collection, the
    /// path lists are freshly built.
    /// </remarks>
    private static HostConfig BuildInspectHostConfig(HostConfig? stored)
    {
        var hostConfig = stored?.ShallowCopy() ?? new HostConfig();

        if (string.IsNullOrEmpty(hostConfig.CgroupnsMode))
        {
            hostConfig.CgroupnsMode = "private";
        }

        if (string.IsNullOrEmpty(hostConfig.IpcMode))
        {
            hostConfig.IpcMode = "private";
        }

        if (string.IsNullOrEmpty(hostConfig.Runtime))
        {
            hostConfig.Runtime = "apple-container";
        }

        hostConfig.MaskedPaths ??= hostConfig.Privileged ? [] : [.. DefaultMaskedPaths];
        hostConfig.ReadonlyPaths ??= hostConfig.Privileged ? [] : [.. DefaultReadonlyPaths];

        return hostConfig;
    }

    private ContainerInspectState BuildInspectState(ContainerRecord record)
    {
        var state = new ContainerInspectState
        {
            Status = record.State.Status,
            Running = string.Equals(record.State.Status, "running", StringComparison.Ordinal),
            Paused = string.Equals(record.State.Status, "paused", StringComparison.Ordinal),
            Restarting = string.Equals(record.State.Status, "restarting", StringComparison.Ordinal),
            Dead = string.Equals(record.State.Status, "dead", StringComparison.Ordinal),
            Pid = record.State.Pid,
            ExitCode = record.State.ExitCode,
            Error = record.State.Error ?? "",
            StartedAt = DockerTime.FormatOrZero(record.State.StartedAt),
            FinishedAt = DockerTime.FormatOrZero(record.State.FinishedAt),
        };

        if (record.State.Health is { } health)
        {
            state.Health = new DockerApi.Models.Health
            {
                Status = health.Status,
                FailingStreak = health.FailingStreak,
                Log = [.. health.Log],
            };
        }

        return state;
    }

    private NetworkSettings BuildNetworkSettings(ContainerRecord record)
    {
        var settings = new NetworkSettings
        {
            Networks = record.Networks,
        };

        foreach (var key in record.Request.ExposedPorts.Keys)
        {
            settings.Ports[NormalizePortKey(key)] = null;
        }

        foreach (var (key, bindings) in record.Ports)
        {
            settings.Ports[key] = bindings;
        }

        var first = record.Networks.Values.FirstOrDefault();
        if (first is not null)
        {
            settings.IPAddress = first.IPAddress;
            settings.Gateway = first.Gateway;
            settings.MacAddress = first.MacAddress ?? "";
            settings.EndpointID = first.EndpointID;
            settings.GlobalIPv6Address = first.GlobalIPv6Address;
            settings.GlobalIPv6PrefixLen = first.GlobalIPv6PrefixLen;
            settings.IPv6Gateway = first.IPv6Gateway;
        }

        return settings;
    }

    private ContainerSummary ToSummary(ContainerRecord record, DateTimeOffset now, bool size)
    {
        var summary = new ContainerSummary
        {
            Id = record.Id,
            Names = ["/" + record.Name],
            Image = record.ImageRef.Length > 0 ? record.ImageRef : record.Request.Image,
            ImageID = record.ImageId,
            Command = FormatCommand(record),
            Created = record.Created.ToUnixTimeSeconds(),
            State = record.State.Status,
            Status = FormatStatus(record, now),
            Labels = new Dictionary<string, string>(record.Request.Labels, StringComparer.Ordinal),
            HostConfig = new SummaryHostConfig
            {
                NetworkMode = string.IsNullOrEmpty(record.Request.HostConfig?.NetworkMode)
                    ? "bridge"
                    : record.Request.HostConfig.NetworkMode,
            },
            NetworkSettings = new NetworkSettingsSummary { Networks = record.Networks },
            Mounts = record.Mounts,
        };

        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, bindings) in record.Ports)
        {
            bound.Add(key);
            var (containerPort, proto) = SplitPortKey(key);
            foreach (var binding in bindings)
            {
                int? publicPort = int.TryParse(binding.HostPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
                summary.Ports.Add(new Port
                {
                    IP = string.IsNullOrEmpty(binding.HostIp) ? "0.0.0.0" : binding.HostIp,
                    PrivatePort = containerPort,
                    PublicPort = publicPort,
                    Type = proto,
                });
            }
        }

        foreach (var key in record.Request.ExposedPorts.Keys)
        {
            var normalized = NormalizePortKey(key);
            if (bound.Contains(normalized))
            {
                continue;
            }

            var (containerPort, proto) = SplitPortKey(normalized);
            summary.Ports.Add(new Port { PrivatePort = containerPort, Type = proto });
        }

        if (size)
        {
            summary.SizeRw = 0;
            summary.SizeRootFs = 0;
        }

        return summary;
    }

    private static string FormatCommand(ContainerRecord record)
    {
        var argv = new List<string>();
        if (!string.IsNullOrEmpty(record.Path))
        {
            argv.Add(record.Path);
        }

        argv.AddRange(record.Args);
        return string.Join(' ', argv);
    }

    /// <summary>Docker's <c>STATUS</c> column text.</summary>
    public static string FormatStatus(ContainerRecord record, DateTimeOffset now)
    {
        var state = record.State;
        switch (state.Status)
        {
            case "created":
                return "Created";

            case "running":
                {
                    var uptime = HumanDuration(now - (state.StartedAt ?? now));
                    var health = state.Health?.Status;
                    return health switch
                    {
                        "healthy" => $"Up {uptime} (healthy)",
                        "unhealthy" => $"Up {uptime} (unhealthy)",
                        "starting" => $"Up {uptime} (health: starting)",
                        _ => $"Up {uptime}",
                    };
                }

            case "paused":
                return $"Up {HumanDuration(now - (state.StartedAt ?? now))} (Paused)";

            case "restarting":
                return $"Restarting ({state.ExitCode}) {HumanDuration(now - (state.FinishedAt ?? now))} ago";

            case "removing":
                return "Removal In Progress";

            case "dead":
                return "Dead";

            default:
                return $"Exited ({state.ExitCode}) {HumanDuration(now - (state.FinishedAt ?? now))} ago";
        }
    }

    /// <summary>Go's <c>units.HumanDuration</c>, which Docker uses for every relative timestamp.</summary>
    public static string HumanDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var seconds = (int)duration.TotalSeconds;
        if (seconds < 1)
        {
            return "Less than a second";
        }

        if (seconds == 1)
        {
            return "1 second";
        }

        if (seconds < 60)
        {
            return $"{seconds} seconds";
        }

        var minutes = (int)duration.TotalMinutes;
        if (minutes == 1)
        {
            return "About a minute";
        }

        if (minutes < 60)
        {
            return $"{minutes} minutes";
        }

        var hours = (int)(duration.TotalHours + 0.5);
        if (hours == 1)
        {
            return "About an hour";
        }

        if (hours < 48)
        {
            return $"{hours} hours";
        }

        if (hours < 24 * 7 * 2)
        {
            return $"{hours / 24} days";
        }

        if (hours < 24 * 30 * 2)
        {
            return $"{hours / 24 / 7} weeks";
        }

        if (hours < 24 * 365 * 2)
        {
            return $"{hours / 24 / 30} months";
        }

        return $"{(int)duration.TotalHours / 24 / 365} years";
    }

    private bool Matches(ContainerRecord record, Filters filters)
    {
        if (filters.IsEmpty)
        {
            return true;
        }

        if (!filters.MatchId(record.Id) || !filters.MatchName(record.Name) ||
            !filters.MatchesLabels(record.Request.Labels))
        {
            return false;
        }

        if (!filters.MatchExact("status", record.State.Status))
        {
            return false;
        }

        if (filters.Contains("health") &&
            !filters.MatchExact("health", record.State.Health?.Status ?? "none"))
        {
            return false;
        }

        if (filters.Contains("exited") &&
            !filters.MatchExact("exited", record.State.ExitCode.ToString(CultureInfo.InvariantCulture)))
        {
            return false;
        }

        if (filters.Contains("ancestor") &&
            !filters.MatchAny("ancestor", candidate =>
                string.Equals(candidate, record.ImageRef, StringComparison.Ordinal) ||
                string.Equals(candidate, record.Request.Image, StringComparison.Ordinal) ||
                record.ImageId.StartsWith(candidate, StringComparison.Ordinal) ||
                record.ImageId.Replace("sha256:", "", StringComparison.Ordinal).StartsWith(candidate, StringComparison.Ordinal)))
        {
            return false;
        }

        if (filters.Contains("network") &&
            !filters.MatchAny("network", candidate => record.Networks.ContainsKey(candidate)))
        {
            return false;
        }

        if (filters.Contains("volume") &&
            !filters.MatchAny("volume", candidate =>
                record.Mounts.Any(mount =>
                    string.Equals(mount.Name, candidate, StringComparison.Ordinal) ||
                    string.Equals(mount.Destination, candidate, StringComparison.Ordinal))))
        {
            return false;
        }

        if (filters.TryGetSingle("before", out var before))
        {
            var other = _store.GetAll().FirstOrDefault(r =>
                string.Equals(r.Id, before, StringComparison.Ordinal) ||
                string.Equals(r.Name, before.TrimStart('/'), StringComparison.Ordinal));
            if (other is null || record.Created >= other.Created)
            {
                return false;
            }
        }

        if (filters.TryGetSingle("since", out var since))
        {
            var other = _store.GetAll().FirstOrDefault(r =>
                string.Equals(r.Id, since, StringComparison.Ordinal) ||
                string.Equals(r.Name, since.TrimStart('/'), StringComparison.Ordinal));
            if (other is null || record.Created <= other.Created)
            {
                return false;
            }
        }

        return true;
    }
}
