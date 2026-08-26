using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <c>StartContainerAsync</c>/<c>WaitContainerAsync</c> over XPC (task cider-ede.7): the daemon-owned
/// pipe stdio + real exit code replacement for the held <c>container start -a</c> child
/// (docs/spikes/xpc/01-cider-runtime-map.md §3.2, docs/spikes/xpc/03-limitations-audit-1.3.md's
/// "Ownership of the container's lifetime moves from 'a subprocess cider must not lose' to 'a socket
/// cider can reconnect'" row). Wire sequence (docs/spikes/xpc/02-apiserver-xpc-protocol.md §4's
/// <c>container start -a</c> row, §8.4-§8.6): <c>containerBootstrap{id, stdin?, stdout?, stderr?,
/// dynamicEnv?}</c> (this boots the VM and creates the init process but does not start it) →
/// <c>containerStartProcess{id, processIdentifier=id}</c> → the returned <see cref="XpcContainerProcess"/>'s
/// <see cref="IContainerProcess.Exited"/> is a <c>containerWait{id, processIdentifier=id}</c> issued
/// immediately on its own dedicated connection (<see cref="XpcCallOptions.LongRunning"/>). No
/// <c>CIDER_HELD</c> marker, no <see cref="Process.OrphanReaper"/> involvement — those exist for the
/// CLI-fallback path only, and the startup orphan sweep (<see cref="EnsureReadyAsync"/>) keeps running
/// unconditionally to catch containers a prior CLI-transport run left behind.
/// <see cref="ExecAsync"/> (task cider-ede.8) reuses the very same <see cref="XpcContainerProcess"/>
/// shell for a second, independent process on the same container — a
/// <c>containerCreateProcess</c>/<c>containerStartProcess</c>/<c>containerWait</c> triple keyed by a
/// freshly generated <c>processIdentifier</c> instead of the container id, exactly the distinction
/// <see cref="KillContainerAsync"/>'s own doc comment draws ("processIdentifier is the container id
/// itself: this always targets the init process, never an exec").
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    /// <summary>
    /// Resolves whether the container has a TTY (<c>InspectContainerAsync</c>'s own
    /// <c>containerList</c> — <see cref="RuntimeContainer.Tty"/>, mapped from
    /// <c>initProcess.terminal</c>), opens the stdio pipes §3.6 calls for (stdin only when attaching,
    /// stderr only when <c>!tty</c> — with a TTY, stderr is merged into stdout server-side), and runs
    /// the bootstrap/start-process pair. Any client-side precondition failure or apiserver
    /// <see cref="RuntimeErrorKind.Unavailable"/> before <c>containerBootstrap</c> completes falls
    /// back to <see cref="_cliFallback"/> whole (the Fallback rule, fix direction §4) — nothing has
    /// been created on the apiserver side yet, so there is nothing to leave half-done. Once
    /// <c>containerBootstrap</c> has succeeded, a failure is a real answer: it is mapped and thrown,
    /// after a best-effort <c>containerStop</c> cleanup mirroring <c>ContainerStart.swift:107</c>
    /// (fix direction §5) — falling back to the CLI at that point would start a second process
    /// against the container the apiserver already bootstrapped.
    /// </summary>
    public Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentNullException.ThrowIfNull(options);

        var container = await InspectContainerAsync(runtimeId, ct).ConfigureAwait(false)
            ?? throw RuntimeException.NotFound($"container not found: {runtimeId}");
        var tty = container.Tty;
        var attachStdin = options.AttachStdin || tty;

        HostPipe? stdinPipe = null;
        HostPipe? stdoutPipe = null;
        HostPipe? stderrPipe = null;
        var handedOff = false;

        try
        {
            stdoutPipe = HostPipe.Create();
            if (attachStdin)
            {
                stdinPipe = HostPipe.Create();
            }

            if (!tty)
            {
                stderrPipe = HostPipe.Create();
            }

            try
            {
                using var bootstrap = new XpcMessage("containerBootstrap");
                bootstrap.SetString("id", runtimeId);
                if (stdinPipe is { } sp)
                {
                    // stdin: the daemon writes, the guest reads — the READ end is the one that
                    // crosses to the guest (fix direction §1).
                    bootstrap.SetFd("stdin", sp.ReadFd);
                }

                // stdout: the guest writes, the daemon reads — the WRITE end crosses to the guest.
                bootstrap.SetFd("stdout", stdoutPipe.WriteFd);
                if (stderrPipe is { } ep)
                {
                    bootstrap.SetFd("stderr", ep.WriteFd);
                }

                var dynamicEnv = BuildDynamicEnv();
                if (dynamicEnv is not null)
                {
                    bootstrap.SetData("dynamicEnv", dynamicEnv);
                }

                // containerBootstrap is in the apiserver's own "no timeout" list (§1.4), on the
                // shared connection — it does not block indefinitely the way containerWait does.
                using var bootstrapReply = await _apiserver.SendAsync(bootstrap, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
            }
            catch (XpcException ex) when (IsUnavailable(ex))
            {
                // containerBootstrap itself failed, so the guest never saw any of these fds — the
                // finally below disposes them (handedOff stays false) exactly as it would for any
                // other failure before a process object exists.
                WarnFallback("containerBootstrap", ex);
                return await _cliFallback.StartContainerAsync(runtimeId, options, ct).ConfigureAwait(false);
            }
            catch (XpcException ex)
            {
                throw ex.ToRuntimeException($"start {runtimeId}");
            }

            // The guest now owns whichever ends it was handed (xpc_dictionary_set_fd dups the
            // descriptor into the message rather than taking it over) — our own copies of those far
            // ends serve no further purpose and must be closed (fix direction §1).
            stdinPipe?.CloseReadFd();
            stdoutPipe.CloseWriteFd();
            stderrPipe?.CloseWriteFd();

            try
            {
                using var startProcess = new XpcMessage("containerStartProcess");
                startProcess.SetString("id", runtimeId);
                startProcess.SetString("processIdentifier", runtimeId);
                using var startReply = await _apiserver.SendAsync(startProcess, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
            }
            catch (XpcException ex)
            {
                await TryStopAfterFailedStartAsync(runtimeId).ConfigureAwait(false);
                throw ex.ToRuntimeException($"start {runtimeId}");
            }

            var process = new XpcContainerProcess(
                tty,
                stdinPipe?.DetachWriteStream(),
                stdoutPipe.DetachReadStream(),
                stderrPipe?.DetachReadStream(),
                waitCt => WaitContainerAsync(runtimeId, waitCt),
                (cols, rows, resizeCt) => ResizeProcessAsync(runtimeId, runtimeId, cols, rows, resizeCt),
                (signal, killCt) => KillProcessBestEffortAsync(runtimeId, signal, killCt),
                _logger);

            handedOff = true;
            return (IContainerProcess)process;
        }
        finally
        {
            if (!handedOff)
            {
                stdinPipe?.Dispose();
                stdoutPipe?.Dispose();
                stderrPipe?.Dispose();
            }
        }
    });

    /// <summary>
    /// <c>containerWait{id, processIdentifier:id}</c> (§8.6) — no client-side timeout, on its own
    /// dedicated connection (<see cref="XpcCallOptions.LongRunning"/>): this blocks until the process
    /// exits, which for a long-running container can be hours, and must never be torn down by a
    /// per-call timeout the way a normal shared-connection call would be (<see cref="XpcCallOptions.DedicatedConnection"/>'s
    /// own doc comment). <c>notFound</c>/<c>invalidState</c> (fix direction §3) answers <c>null</c> —
    /// "the transport cannot wait" for this particular call, e.g. the process was never bootstrapped
    /// or was already reaped — the same "exit code unknown" contract <see cref="ContainerManager"/>'s
    /// reconcile keeps for that case. Falls back to <see cref="_cliFallback"/> (which itself always
    /// answers <c>null</c> — there is no CLI equivalent) on apiserver
    /// <see cref="RuntimeErrorKind.Unavailable"/>, matching every other member's Fallback rule.
    /// </summary>
    public Task<(int ExitCode, DateTimeOffset ExitedAt)?> WaitContainerAsync(string runtimeId, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        try
        {
            using var request = new XpcMessage("containerWait");
            request.SetString("id", runtimeId);
            request.SetString("processIdentifier", runtimeId);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.LongRunning, ct).ConfigureAwait(false);

            var exitCode = checked((int)reply.GetInt64("exitCode"));
            var exitedAt = reply.GetDate("exitedAt");
            return ((int ExitCode, DateTimeOffset ExitedAt)?)(exitCode, exitedAt);
        }
        catch (XpcException ex) when (XpcErrorMapper.ToRuntimeErrorKind(ex) is RuntimeErrorKind.NotFound or RuntimeErrorKind.Conflict)
        {
            return null;
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerWait", ex);
            return await _cliFallback.WaitContainerAsync(runtimeId, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"wait {runtimeId}");
        }
    });

    /// <summary>
    /// <c>containerResize{id, processIdentifier, width, height}</c> (§8's route table) — best
    /// effort, exactly like <c>CliProcess.ResizeAsync</c>: a resize routinely races the end of a
    /// session, so a failure here is logged at Debug and swallowed rather than surfacing to whatever
    /// endpoint is forwarding a client's <c>SIGWINCH</c>. <paramref name="processIdentifier"/> is
    /// <paramref name="runtimeId"/> itself for the container's own init process
    /// (<see cref="StartContainerAsync"/>) or a per-exec uuid for an <c>ExecAsync</c> process (task
    /// cider-ede.8).
    /// </summary>
    private async Task ResizeProcessAsync(string runtimeId, string processIdentifier, int cols, int rows, CancellationToken ct)
    {
        try
        {
            using var request = new XpcMessage("containerResize");
            request.SetString("id", runtimeId);
            request.SetString("processIdentifier", processIdentifier);
            request.SetUInt64("width", (ulong)Math.Clamp(cols, 1, int.MaxValue));
            request.SetUInt64("height", (ulong)Math.Clamp(rows, 1, int.MaxValue));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            _logger.LogDebug(ex, "containerResize for {Id}/{ProcessIdentifier} failed", runtimeId, processIdentifier);
        }
    }

    /// <summary>Best-effort wrapper around the already-ported <see cref="KillContainerAsync"/> (which
    /// always targets the init process — <c>processIdentifier == id</c>, its own doc comment) for
    /// <see cref="XpcContainerProcess.KillAsync"/>'s injected delegate: swallows the
    /// <see cref="RuntimeException"/> <see cref="GuardAsync{T}(System.Func{System.Threading.Tasks.Task{T}})"/>
    /// would otherwise throw, matching <see cref="IContainerProcess.KillAsync"/>'s "best-effort"
    /// contract.</summary>
    private async Task KillProcessBestEffortAsync(string runtimeId, string signal, CancellationToken ct)
    {
        try
        {
            await KillContainerAsync(runtimeId, signal, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "containerKill({Signal}) for {Id} failed", signal, runtimeId);
        }
    }

    /// <summary>Mirrors <c>ContainerStart.swift:107</c> (fix direction §5): on any failure after a
    /// successful <c>containerBootstrap</c>, stop the half-started container rather than leaving it
    /// bootstrapped-but-never-run. Reuses the already-ported <see cref="StopContainerAsync"/>
    /// (including its own CLI fallback — the CLI's <c>container stop</c> reaches the very same
    /// apiserver, so falling back here is still a real stop, not a different mechanism). Best effort:
    /// a further failure is logged at Debug, never allowed to shadow the original error that
    /// triggered this cleanup.</summary>
    private async Task TryStopAfterFailedStartAsync(string runtimeId)
    {
        try
        {
            await StopContainerAsync(runtimeId, 0, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "cleanup containerStop after a failed start for {Id} also failed", runtimeId);
        }
    }

    /// <summary><c>dynamicEnv</c> (§8.4): <c>SSH_AUTH_SOCK</c> forwarded from the daemon's own host
    /// environment when set, exactly like the Swift CLI's own <c>start -a</c>
    /// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §4: "dynamicEnv = {"SSH_AUTH_SOCK": …} if set in
    /// the host env"). <c>null</c> (the key is omitted entirely) when it is not set — an empty
    /// dictionary is never sent.</summary>
    private static byte[]? BuildDynamicEnv()
    {
        var sshAuthSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        return string.IsNullOrEmpty(sshAuthSock)
            ? null
            : XpcJson.SerializeToUtf8Bytes(new Dictionary<string, string> { ["SSH_AUTH_SOCK"] = sshAuthSock });
    }

    /// <summary>
    /// <c>docker exec</c>/<c>HealthMonitor</c> probes/<c>docker top</c>/<c>buildctl dial-stdio</c>
    /// (task cider-ede.8) — replaces the CLI transport's held <c>container exec</c> child and its
    /// stderr-text-keyed "is not running" retry (docs/spikes/xpc/01-cider-runtime-map.md §2
    /// <c>ExecAsync</c>, §3.3) with the apiserver's own three-call sequence
    /// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §4's <c>container exec -i &lt;id&gt; &lt;cmd&gt;</c>
    /// row): one <c>containerList</c> (via <see cref="FetchContainerSnapshotAsync"/>) to read
    /// <c>configuration.initProcess</c>, then <c>containerCreateProcess</c> → <c>containerStartProcess</c>
    /// keyed by a freshly generated <c>processIdentifier</c> (never the container id — that would
    /// collide with the init process). Pipes follow the same X6/X7 rule stdio §3.6 states: <c>stdin</c>
    /// only when <see cref="ExecSpec.OpenStdin"/>, no <c>stderr</c> pipe when the exec itself is a tty
    /// (server-side merge into stdout). <see cref="ExecSpec.Privileged"/> has no wire field (fix
    /// direction §4) — ignored, logged at Debug, exactly like the CLI transport
    /// (<c>AppleContainerRuntime.ExecAsync</c>).
    /// Start-race handling (fix direction §3): an <c>invalidState</c> "not running" from
    /// <c>containerCreateProcess</c>/<c>containerStartProcess</c> right after the container started is
    /// not special-cased here at all — <see cref="XpcException.ToRuntimeException"/> already turns it
    /// into <see cref="RuntimeErrorReason.ContainerNotRunning"/> (<see cref="XpcErrorMapper.ToRuntimeErrorReason"/>),
    /// which is exactly the signal <c>ExecManager</c>'s own retry keys on (<c>ExecManager.cs:246-254</c>,
    /// <c>ex.IsContainerNotRunning</c>) — no CLI-specific probe/retry loop is needed or ported.
    /// Falls back to <see cref="_cliFallback"/> whole on apiserver <see cref="RuntimeErrorKind.Unavailable"/>
    /// at either the inspect or the <c>containerCreateProcess</c> step — nothing has been created yet
    /// in both cases, mirroring <see cref="StartContainerAsync"/>'s own Fallback rule. Once
    /// <c>containerCreateProcess</c> has succeeded, a <c>containerStartProcess</c> failure is a real
    /// answer, mapped and thrown — falling back at that point would leave an orphaned, never-started
    /// process object on the apiserver as well as starting a second one via the CLI.
    /// </summary>
    public Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Privileged)
        {
            _logger.LogDebug("ignoring privileged exec on {Id}: no such field on the apiserver wire", runtimeId);
        }

        ContainerSnapshot? snapshot;
        try
        {
            snapshot = await FetchContainerSnapshotAsync(runtimeId, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerList", ex);
            return await _cliFallback.ExecAsync(runtimeId, spec, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"exec {runtimeId}");
        }

        if (snapshot is null)
        {
            throw RuntimeException.NotFound($"container not found: {runtimeId}");
        }

        var processConfig = ProcessConfigurationBuilder.Build(snapshot.Configuration.InitProcess, spec);
        var tty = processConfig.Terminal;
        var processIdentifier = Guid.NewGuid().ToString();

        HostPipe? stdinPipe = null;
        HostPipe? stdoutPipe = null;
        HostPipe? stderrPipe = null;
        var handedOff = false;

        try
        {
            stdoutPipe = HostPipe.Create();
            if (spec.OpenStdin)
            {
                stdinPipe = HostPipe.Create();
            }

            if (!tty)
            {
                stderrPipe = HostPipe.Create();
            }

            try
            {
                using var create = new XpcMessage("containerCreateProcess");
                create.SetString("id", runtimeId);
                create.SetString("processIdentifier", processIdentifier);
                create.SetData("processConfig", XpcJson.SerializeToUtf8Bytes(processConfig));
                if (stdinPipe is { } sp)
                {
                    create.SetFd("stdin", sp.ReadFd);
                }

                create.SetFd("stdout", stdoutPipe.WriteFd);
                if (stderrPipe is { } ep)
                {
                    create.SetFd("stderr", ep.WriteFd);
                }

                using var createReply = await _apiserver.SendAsync(create, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
            }
            catch (XpcException ex) when (IsUnavailable(ex))
            {
                // Mirrors StartContainerAsync: nothing has been created on the apiserver side yet, so
                // the guest never saw any of these fds — the finally below disposes them.
                WarnFallback("containerCreateProcess", ex);
                return await _cliFallback.ExecAsync(runtimeId, spec, ct).ConfigureAwait(false);
            }
            catch (XpcException ex)
            {
                throw ex.ToRuntimeException($"exec {runtimeId}");
            }

            // The guest now owns whichever ends it was handed — see StartContainerAsync's identical
            // comment on why our own copies must be closed here.
            stdinPipe?.CloseReadFd();
            stdoutPipe.CloseWriteFd();
            stderrPipe?.CloseWriteFd();

            try
            {
                using var start = new XpcMessage("containerStartProcess");
                start.SetString("id", runtimeId);
                start.SetString("processIdentifier", processIdentifier);
                using var startReply = await _apiserver.SendAsync(start, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
            }
            catch (XpcException ex)
            {
                throw ex.ToRuntimeException($"exec {runtimeId}");
            }

            var process = new XpcContainerProcess(
                tty,
                stdinPipe?.DetachWriteStream(),
                stdoutPipe.DetachReadStream(),
                stderrPipe?.DetachReadStream(),
                waitCt => WaitProcessAsync(runtimeId, processIdentifier, waitCt),
                (cols, rows, resizeCt) => ResizeProcessAsync(runtimeId, processIdentifier, cols, rows, resizeCt),
                (signal, killCt) => KillExecProcessBestEffortAsync(runtimeId, processIdentifier, signal, killCt),
                _logger);

            handedOff = true;
            return (IContainerProcess)process;
        }
        finally
        {
            if (!handedOff)
            {
                stdinPipe?.Dispose();
                stdoutPipe?.Dispose();
                stderrPipe?.Dispose();
            }
        }
    });

    /// <summary><c>containerList{ids:[runtimeId]}</c> (§8.2), returning the raw
    /// <see cref="ContainerSnapshot"/> rather than the mapped <see cref="RuntimeContainer"/> the
    /// public <see cref="InspectContainerAsync"/> answers — <see cref="ExecAsync"/> needs
    /// <c>configuration.initProcess</c> in full (in particular its wire <see cref="User"/>, which
    /// <see cref="RuntimeContainer"/> does not carry) to seed <see cref="ProcessConfigurationBuilder.Build"/>.
    /// <c>null</c> when the container does not exist; throws <see cref="XpcException"/> straight
    /// through on any transport/apiserver failure — <see cref="ExecAsync"/> classifies those itself
    /// (Unavailable → fall back whole; anything else → mapped and thrown).</summary>
    private async Task<ContainerSnapshot?> FetchContainerSnapshotAsync(string runtimeId, CancellationToken ct)
    {
        using var request = new XpcMessage("containerList");
        var filters = new ContainerListFilters { Ids = [runtimeId], Labels = [] };
        request.SetData("listFilters", XpcJson.SerializeToUtf8Bytes(filters));
        using var reply = await _apiserver.SendAsync(request, XpcCallOptions.List, ct).ConfigureAwait(false);

        var bytes = reply.GetData("containers");
        var snapshots = bytes is null ? [] : XpcJson.Deserialize<List<ContainerSnapshot>>(bytes);
        return snapshots.Count > 0 ? snapshots[0] : null;
    }

    /// <summary>
    /// <c>containerWait{id, processIdentifier}</c> for an exec process (task cider-ede.8) — the exact
    /// same call and "cannot wait → null" contract as the public <see cref="WaitContainerAsync"/>
    /// (its own doc comment), except keyed by the exec's own <paramref name="processIdentifier"/>
    /// rather than the container id, and with no CLI fallback: the CLI transport never created this
    /// process (it has its own, separate <c>container exec</c> child), so there is nothing for it to
    /// wait on. Never throws — matches <see cref="IContainerProcess.Exited"/>'s "never throws; -1 when
    /// unknown" contract via <see cref="XpcContainerProcess.RunWaitAsync"/>, which is the only caller.
    /// </summary>
    private async Task<(int ExitCode, DateTimeOffset ExitedAt)?> WaitProcessAsync(string runtimeId, string processIdentifier, CancellationToken ct)
    {
        try
        {
            using var request = new XpcMessage("containerWait");
            request.SetString("id", runtimeId);
            request.SetString("processIdentifier", processIdentifier);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.LongRunning, ct).ConfigureAwait(false);

            var exitCode = checked((int)reply.GetInt64("exitCode"));
            var exitedAt = reply.GetDate("exitedAt");
            return ((int ExitCode, DateTimeOffset ExitedAt)?)(exitCode, exitedAt);
        }
        catch (XpcException ex) when (XpcErrorMapper.ToRuntimeErrorKind(ex) is RuntimeErrorKind.NotFound or RuntimeErrorKind.Conflict)
        {
            return null;
        }
        catch (XpcException ex)
        {
            _logger.LogDebug(ex, "containerWait for {Id}/{ProcessIdentifier} failed; exit code will be reported as unknown", runtimeId, processIdentifier);
            return null;
        }
    }

    /// <summary>Best-effort <c>containerKill{id, processIdentifier, signal}</c> for an exec process
    /// (task cider-ede.8) — the exec counterpart of <see cref="KillProcessBestEffortAsync"/>, but a
    /// direct XPC call rather than a wrapper around <see cref="KillContainerAsync"/>: that method's
    /// own doc comment is explicit that its <c>processIdentifier</c> is always the container id
    /// ("never an exec"), so an exec kill needs its own call keyed by <paramref name="processIdentifier"/>.
    /// <paramref name="signal"/> is normalized the same way <see cref="KillContainerAsync"/> normalizes
    /// it (<see cref="ContainerConfigurationBuilder.NormalizeSignal"/>). Swallows every failure —
    /// <see cref="XpcContainerProcess.KillAsync"/>'s own "best-effort" contract.</summary>
    private async Task KillExecProcessBestEffortAsync(string runtimeId, string processIdentifier, string signal, CancellationToken ct)
    {
        try
        {
            using var request = new XpcMessage("containerKill");
            request.SetString("id", runtimeId);
            request.SetString("processIdentifier", processIdentifier);
            request.SetString("signal", ContainerConfigurationBuilder.NormalizeSignal(signal, "KILL"));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            _logger.LogDebug(ex, "containerKill({Signal}) for {Id}/{ProcessIdentifier} failed", signal, runtimeId, processIdentifier);
        }
    }
}
