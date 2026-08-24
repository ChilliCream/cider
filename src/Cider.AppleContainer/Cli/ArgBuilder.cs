using System.Globalization;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Cli;

/// <summary>Turns runtime specs into <c>container</c> argument vectors (ARCHITECTURE.md §9).</summary>
internal static class ArgBuilder
{
    private const long MiB = 1024 * 1024;

    /// <summary>Numeric signals Docker clients send, mapped to the names Apple's CLI accepts.</summary>
    private static readonly Dictionary<string, string> SignalNames = new(StringComparer.Ordinal)
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

    /// <summary><c>container create …</c> — never <c>--rm</c>, never <c>-d</c>.</summary>
    public static List<string> Create(ContainerSpec spec)
    {
        var args = new List<string> { "create", "--name", spec.RuntimeId };

        foreach (var env in spec.Env)
        {
            args.Add("-e");
            args.Add(env);
        }

        if (!string.IsNullOrEmpty(spec.WorkingDir))
        {
            args.Add("-w");
            args.Add(spec.WorkingDir);
        }

        if (!string.IsNullOrEmpty(spec.User))
        {
            args.Add("-u");
            args.Add(spec.User);
        }

        if (spec.Tty)
        {
            args.Add("-t");
        }

        if (spec.OpenStdin)
        {
            args.Add("-i");
        }

        foreach (var (key, value) in spec.Labels)
        {
            args.Add("-l");
            args.Add($"{key}={value}");
        }

        foreach (var mount in spec.Mounts)
        {
            switch (mount.Kind)
            {
                case MountKind.Bind:
                case MountKind.Volume:
                    args.Add("-v");
                    args.Add(mount.ReadOnly
                        ? $"{mount.Source}:{mount.Target}:ro"
                        : $"{mount.Source}:{mount.Target}");
                    break;
                case MountKind.Tmpfs:
                    args.Add("--mount");
                    args.Add(mount.ReadOnly
                        ? $"type=tmpfs,target={mount.Target},readonly"
                        : $"type=tmpfs,target={mount.Target}");
                    break;
                default:
                    throw RuntimeException.InvalidArgument($"unsupported mount kind '{mount.Kind}'");
            }
        }

        // `--tmpfs <path>` has no size option on 1.2.2; TmpfsSpec.SizeBytes is ignored.
        foreach (var tmpfs in spec.Tmpfs)
        {
            args.Add("--tmpfs");
            args.Add(tmpfs.Target);
        }

        foreach (var port in spec.Ports)
        {
            args.Add("-p");
            args.Add(FormatPort(port));
        }

        foreach (var network in spec.Networks)
        {
            args.Add("--network");
            args.Add(network);
        }

        foreach (var dns in spec.DnsServers)
        {
            args.Add("--dns");
            args.Add(dns);
        }

        foreach (var search in spec.DnsSearch)
        {
            args.Add("--dns-search");
            args.Add(search);
        }

        foreach (var option in spec.DnsOptions)
        {
            args.Add("--dns-option");
            args.Add(option);
        }

        if (spec.Cpus is > 0)
        {
            args.Add("-c");
            args.Add(Math.Max(1, (int)Math.Round(spec.Cpus.Value, MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture));
        }

        if (spec.MemoryBytes is > 0)
        {
            args.Add("-m");
            args.Add(FormatMebibytes(spec.MemoryBytes.Value));
        }

        foreach (var cap in spec.CapAdd)
        {
            args.Add("--cap-add");
            args.Add(cap);
        }

        foreach (var cap in spec.CapDrop)
        {
            args.Add("--cap-drop");
            args.Add(cap);
        }

        if (spec.Privileged)
        {
            args.Add("--cap-add");
            args.Add("ALL");
            args.Add("--masked-path");
            args.Add("NONE");
            args.Add("--read-only-path");
            args.Add("NONE");
        }

        if (!string.IsNullOrEmpty(spec.Platform))
        {
            args.Add("--platform");
            args.Add(spec.Platform);
        }

        if (spec.ReadOnlyRootfs)
        {
            args.Add("--read-only");
        }

        if (spec.ShmSizeBytes is > 0)
        {
            args.Add("--shm-size");
            args.Add(FormatMebibytes(spec.ShmSizeBytes.Value));
        }

        if (spec.Init)
        {
            args.Add("--init");
        }

        foreach (var ulimit in spec.Ulimits)
        {
            args.Add("--ulimit");
            args.Add($"{ulimit.Name}={ulimit.Soft}:{ulimit.Hard}");
        }

        foreach (var socket in spec.PublishSockets)
        {
            args.Add("--publish-socket");
            args.Add(socket);
        }

        if (!string.IsNullOrEmpty(spec.Entrypoint))
        {
            args.Add("--entrypoint");
            args.Add(spec.Entrypoint);
        }

        args.Add(spec.Image);
        args.AddRange(spec.Args);
        return args;
    }

    /// <summary><c>container start -a [-i] &lt;id&gt;</c> — held, never detached.</summary>
    public static List<string> Start(string runtimeId, bool attachStdin)
    {
        var args = new List<string> { "start", "-a" };
        if (attachStdin)
        {
            args.Add("-i");
        }

        args.Add(runtimeId);
        return args;
    }

    /// <summary><c>container exec …</c>.</summary>
    public static List<string> Exec(string runtimeId, ExecSpec spec)
    {
        var args = new List<string> { "exec" };

        if (spec.OpenStdin)
        {
            args.Add("-i");
        }

        if (spec.Tty)
        {
            args.Add("-t");
        }

        foreach (var env in spec.Env)
        {
            args.Add("-e");
            args.Add(env);
        }

        if (!string.IsNullOrEmpty(spec.WorkingDir))
        {
            args.Add("-w");
            args.Add(spec.WorkingDir);
        }

        if (!string.IsNullOrEmpty(spec.User))
        {
            args.Add("-u");
            args.Add(spec.User);
        }

        args.Add(runtimeId);
        args.AddRange(spec.Argv);
        return args;
    }

    /// <summary><c>container build --progress plain …</c>.</summary>
    public static List<string> Build(BuildSpec spec, IReadOnlyList<string> tags)
    {
        var args = new List<string> { "build", "--progress", "plain" };

        var dockerfile = ResolveDockerfile(spec);
        if (dockerfile is not null)
        {
            args.Add("-f");
            args.Add(dockerfile);
        }

        foreach (var tag in tags)
        {
            args.Add("-t");
            args.Add(tag);
        }

        foreach (var (key, value) in spec.BuildArgs)
        {
            args.Add("--build-arg");
            args.Add($"{key}={value}");
        }

        foreach (var (key, value) in spec.Labels)
        {
            args.Add("-l");
            args.Add($"{key}={value}");
        }

        if (!string.IsNullOrEmpty(spec.Target))
        {
            args.Add("--target");
            args.Add(spec.Target);
        }

        foreach (var platform in spec.Platforms)
        {
            args.Add("--platform");
            args.Add(platform);
        }

        if (spec.NoCache)
        {
            args.Add("--no-cache");
        }

        if (spec.Pull)
        {
            args.Add("--pull");
        }

        if (spec.Quiet)
        {
            args.Add("-q");
        }

        if (spec.Cpus is > 0)
        {
            args.Add("-c");
            args.Add(spec.Cpus.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.MemoryBytes is > 0)
        {
            args.Add("-m");
            args.Add(FormatMebibytes(spec.MemoryBytes.Value));
        }

        args.Add(spec.ContextDir);
        return args;
    }

    /// <summary><c>container network create …</c>.</summary>
    public static List<string> CreateNetwork(NetworkSpec spec)
    {
        var args = new List<string> { "network", "create" };

        if (spec.Internal)
        {
            args.Add("--internal");
        }

        foreach (var (key, value) in spec.Labels)
        {
            args.Add("--label");
            args.Add($"{key}={value}");
        }

        foreach (var (key, value) in spec.Options)
        {
            args.Add("--option");
            args.Add($"{key}={value}");
        }

        if (!string.IsNullOrEmpty(spec.Subnet))
        {
            args.Add("--subnet");
            args.Add(spec.Subnet);
        }

        if (!string.IsNullOrEmpty(spec.SubnetV6))
        {
            args.Add("--subnet-v6");
            args.Add(spec.SubnetV6);
        }

        args.Add(spec.Name);
        return args;
    }

    /// <summary><c>container volume create …</c>.</summary>
    public static List<string> CreateVolume(VolumeSpec spec)
    {
        var args = new List<string> { "volume", "create" };

        foreach (var (key, value) in spec.Labels)
        {
            args.Add("--label");
            args.Add($"{key}={value}");
        }

        foreach (var (key, value) in spec.Options)
        {
            args.Add("--opt");
            args.Add($"{key}={value}");
        }

        if (spec.SizeBytes is > 0)
        {
            args.Add("-s");
            args.Add(spec.SizeBytes.Value.ToString(CultureInfo.InvariantCulture));
        }

        args.Add(spec.Name);
        return args;
    }

    /// <summary><c>[ip:]host:container[/proto]</c>.</summary>
    public static string FormatPort(PortSpec port)
    {
        var head = string.IsNullOrEmpty(port.HostIp) || port.HostIp == "0.0.0.0"
            ? $"{port.HostPort}:{port.ContainerPort}"
            : $"{port.HostIp}:{port.HostPort}:{port.ContainerPort}";

        var proto = string.IsNullOrEmpty(port.Proto) ? "tcp" : port.Proto.ToLowerInvariant();
        return proto == "tcp" ? head : $"{head}/{proto}";
    }

    /// <summary>Apple's memory flags take 1 MiB granularity with a suffix; round up.</summary>
    public static string FormatMebibytes(long bytes)
    {
        var mebibytes = (bytes + MiB - 1) / MiB;
        if (mebibytes < 1)
        {
            mebibytes = 1;
        }

        return $"{mebibytes.ToString(CultureInfo.InvariantCulture)}M";
    }

    /// <summary>Absolute path of the Dockerfile, or <c>null</c> when the default should be used.</summary>
    public static string? ResolveDockerfile(BuildSpec spec)
    {
        if (string.IsNullOrEmpty(spec.Dockerfile))
        {
            return null;
        }

        return Path.IsPathRooted(spec.Dockerfile)
            ? spec.Dockerfile
            : Path.Combine(spec.ContextDir, spec.Dockerfile);
    }

    /// <summary>Normalizes a Docker signal name (<c>SIGTERM</c>, <c>15</c>) to what Apple's CLI expects (<c>TERM</c>).</summary>
    public static string NormalizeSignal(string? signal)
    {
        if (string.IsNullOrWhiteSpace(signal))
        {
            return "TERM";
        }

        var value = signal.Trim();
        if (SignalNames.TryGetValue(value, out var byNumber))
        {
            return byNumber;
        }

        if (value.StartsWith("SIG", StringComparison.OrdinalIgnoreCase) && value.Length > 3)
        {
            value = value[3..];
        }

        return value.ToUpperInvariant();
    }
}
