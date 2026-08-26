using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Builds the <see cref="ProcessConfiguration"/> a <c>containerCreateProcess</c> call needs for one
/// <c>docker exec</c> (task cider-ede.8), the .NET equivalent of the Swift CLI's own client-side
/// derivation: "process config is derived CLIENT-SIDE from container.configuration.initProcess:
/// executable = args[0]; arguments = rest; terminal = --tty; environment += --env/--env-file;
/// workingDirectory = --workdir; user/groups from Parser.user"
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §4's <c>container exec -i &lt;id&gt; &lt;cmd&gt;</c>
/// row). Unlike <see cref="ContainerConfigurationBuilder"/> this never reads an image config — the
/// container already exists, so every fallback value comes from its own already-decided
/// <see cref="ProcessConfiguration"/> (<paramref name="container"/> below), not the image. A plain,
/// deterministic function, no XPC call, testable straight from fixtures
/// (<c>tests/Cider.Tests/AppleContainer/Xpc/ProcessConfigurationBuilderTests.cs</c>).
/// </summary>
internal static class ProcessConfigurationBuilder
{
    /// <summary>
    /// Task fix direction §1: <c>executable = argv[0]</c>, <c>arguments = argv[1..]</c>,
    /// <c>environment = container env + spec.Env (last wins per key)</c>, <c>workingDirectory =
    /// spec.WorkingDir ?? container workdir</c>, <c>terminal = spec.Tty</c>, <c>user</c> from
    /// <c>spec.User</c> (same parser as X5 — <see cref="ContainerConfigurationBuilder.BuildUser"/>)
    /// else the container's own <see cref="ProcessConfiguration.User"/>, <c>supplementalGroups</c> and
    /// <c>rlimits</c> both <c>[]</c> — <see cref="ExecSpec"/> has no fields for either, and the wire
    /// type requires both present (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.0 rule 11:
    /// <c>ProcessConfiguration</c> is synthesized Codable, all 8 fields required).
    /// </summary>
    public static ProcessConfiguration Build(ProcessConfiguration container, ExecSpec spec)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Argv.Count == 0)
        {
            throw RuntimeException.InvalidArgument("cider: exec requires a command");
        }

        return new ProcessConfiguration
        {
            Executable = spec.Argv[0],
            Arguments = [.. spec.Argv.Skip(1)],
            Environment = MergeEnvironment(container.Environment, spec.Env),
            WorkingDirectory = string.IsNullOrEmpty(spec.WorkingDir) ? container.WorkingDirectory : spec.WorkingDir,
            Terminal = spec.Tty,
            User = string.IsNullOrEmpty(spec.User) ? container.User : ContainerConfigurationBuilder.BuildUser(spec.User),
            SupplementalGroups = [],
            Rlimits = [],
        };
    }

    /// <summary><c>container env + spec.Env</c>, last-wins per key, order-preserving — the same
    /// merge shape <c>Parser.process</c> uses for the image env + <c>--env-file</c> + <c>--env</c>
    /// chain (§3.2 item 6), applied here to the container's already-resolved
    /// <see cref="ProcessConfiguration.Environment"/> instead of an image config. Entries without an
    /// <c>=</c> are dropped, mirroring <see cref="ContainerConfigurationBuilder.Build"/>'s own filter.</summary>
    private static List<string> MergeEnvironment(IReadOnlyList<string> containerEnv, IReadOnlyList<string> execEnv)
    {
        var order = new List<string>();
        var valueByKey = new Dictionary<string, string>(StringComparer.Ordinal);

        void Apply(IEnumerable<string> entries)
        {
            foreach (var entry in entries)
            {
                var separator = entry.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                var key = entry[..separator];
                if (!valueByKey.ContainsKey(key))
                {
                    order.Add(key);
                }

                valueByKey[key] = entry;
            }
        }

        Apply(containerEnv);
        Apply(execEnv);

        return [.. order.Select(key => valueByKey[key])];
    }
}
