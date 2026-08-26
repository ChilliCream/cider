using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// The Apple builder VM over XPC (task cider-ede.13). The builder is just the container named
/// <c>buildkit</c> (<c>AppleContainerRuntime.Builder.cs</c>'s own doc comment on its labels), so status
/// is a plain <c>containerList{ids:["buildkit"]}</c> snapshot mapped to <see cref="BuilderStatus"/>
/// (<see cref="ToBuilderStatus"/>), reusing <see cref="FetchContainerSnapshotAsync"/> (task cider-ede.8,
/// <c>XpcContainerRuntime.Process.cs</c>) — the exact same route <see cref="InspectContainerAsync"/>
/// uses for any other container. The dial itself needs no new wire call at all: it is
/// <see cref="ExecAsync"/> (task cider-ede.8) with the identical <see cref="ExecSpec"/> the CLI
/// transport's own <c>AppleContainerRuntime.DialBuilderAsync</c> passes — a
/// <c>containerCreateProcess</c>/<c>containerStartProcess</c> pair over daemon-owned pipes (X7),
/// no child process and no PTY, in place of the CLI's held <c>container exec -i buildkit buildctl
/// dial-stdio</c> subprocess. Starting the builder (<c>container builder start</c>) composes the shim
/// image pull and the VM's own config — nothing an apiserver route replaces (this task's non-goals) —
/// so <see cref="StartBuilderAsync"/> keeps delegating to <c>_cliFallback</c> outright, same as before
/// this task.
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    /// <summary>
    /// <c>containerList{ids:["buildkit"], labels:[]}</c> — the same shape
    /// <see cref="InspectContainerAsync"/> uses for any other container id, via the already-ported
    /// <see cref="FetchContainerSnapshotAsync"/> — mapped to <see cref="BuilderStatus"/> by
    /// <see cref="ToBuilderStatus"/>. <c>null</c> when no such container exists yet: "no builder has
    /// ever been started on this machine", the same contract the CLI transport's <c>container builder
    /// status</c> gives on a NotFound/Conflict-shaped stderr (<c>AppleContainerRuntime.GetBuilderStatusAsync</c>).
    /// Falls back to <c>_cliFallback</c> whole on apiserver <see cref="RuntimeErrorKind.Unavailable"/>,
    /// matching every other read member's Fallback rule.
    /// </summary>
    public Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "containerList",
            async () =>
            {
                var snapshot = await FetchContainerSnapshotAsync("buildkit", ct).ConfigureAwait(false);
                return snapshot is null ? null : ToBuilderStatus(snapshot);
            },
            () => _cliFallback.GetBuilderStatusAsync(ct)));

    /// <summary>
    /// <c>container builder start</c> composes the shim image pull and the builder VM's own config —
    /// nothing an apiserver route replaces (this task's non-goals) — so this stays delegated to the
    /// CLI transport outright, exactly like the members still listed in the <c>// FALLBACK</c> block at
    /// the bottom of <c>XpcContainerRuntime.cs</c>.
    /// </summary>
    public Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct) =>
        _cliFallback.StartBuilderAsync(cpus, memoryBytes, ct);

    /// <summary>
    /// <c>container exec -i buildkit buildctl dial-stdio</c>'s XPC replacement: reuses
    /// <see cref="ExecAsync"/> (task cider-ede.8) verbatim with the exact <see cref="ExecSpec"/> the CLI
    /// transport's own <c>AppleContainerRuntime.DialBuilderAsync</c> passes to its own
    /// <c>ExecAsync</c> — <see cref="ExecSpec.OpenStdin"/> true (buildctl reads its protocol on stdin),
    /// <see cref="ExecSpec.Tty"/> false (a raw duplex byte pipe, never a terminal — §3.6's stdio rule
    /// keeps a real stderr pipe open rather than merging it into stdout). No wire call of its own:
    /// every apiserver call, the Fallback rule, and the returned <see cref="IContainerProcess"/>'s
    /// contract (caller drains stderr, must not half-close stdin early — <c>IContainerRuntime.cs</c>'s
    /// own doc comment on this member) all come from <see cref="ExecAsync"/> unchanged.
    /// </summary>
    public Task<IContainerProcess> DialBuilderAsync(CancellationToken ct) => ExecAsync(
        "buildkit",
        new ExecSpec { Argv = ["buildctl", "dial-stdio"], OpenStdin = true, Tty = false },
        ct);

    /// <summary>
    /// <see cref="ContainerSnapshot"/> → <see cref="BuilderStatus"/>: the configuration id is the
    /// builder's name (always <c>buildkit</c>), the image reference its (fixed, CLI-chosen) image, the
    /// snapshot status its running flag, the configuration's resources its cpus/memory, and the first
    /// network attachment's IPv4 address (already <c>ip/prefix</c> on the wire, §2.2 — the identical
    /// shape <c>AppleContainerRuntime.ParseBuilderStatus</c> reads out of the CLI's <c>ip/prefix</c>
    /// column) its address — the same fields callers see over either transport.
    /// </summary>
    internal static BuilderStatus ToBuilderStatus(ContainerSnapshot snapshot)
    {
        var configuration = snapshot.Configuration;
        var resources = configuration.Resources ?? new Resources();
        var address = snapshot.Networks.Count > 0 ? snapshot.Networks[0].Ipv4Address : null;

        return new BuilderStatus
        {
            Name = configuration.Id,
            Image = configuration.Image.Reference,
            Running = snapshot.Status == RuntimeStatus.Running,
            Address = string.IsNullOrEmpty(address) ? null : address,
            Cpus = resources.Cpus,
            MemoryBytes = (long)resources.MemoryInBytes,
        };
    }
}
