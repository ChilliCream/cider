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
                (cols, rows, resizeCt) => ResizeProcessAsync(runtimeId, cols, rows, resizeCt),
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
    /// <c>containerResize{id, processIdentifier:id, width, height}</c> (§8's route table) — best
    /// effort, exactly like <c>CliProcess.ResizeAsync</c>: a resize routinely races the end of a
    /// session, so a failure here is logged at Debug and swallowed rather than surfacing to whatever
    /// endpoint is forwarding a client's <c>SIGWINCH</c>.
    /// </summary>
    private async Task ResizeProcessAsync(string runtimeId, int cols, int rows, CancellationToken ct)
    {
        try
        {
            using var request = new XpcMessage("containerResize");
            request.SetString("id", runtimeId);
            request.SetString("processIdentifier", runtimeId);
            request.SetUInt64("width", (ulong)Math.Clamp(cols, 1, int.MaxValue));
            request.SetUInt64("height", (ulong)Math.Clamp(rows, 1, int.MaxValue));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            _logger.LogDebug(ex, "containerResize for {Id} failed", runtimeId);
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
}
