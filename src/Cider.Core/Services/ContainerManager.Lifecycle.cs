using System.Globalization;
using System.Net;
using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Ids;
using Cider.Core.Restart;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    private const int PumpBufferSize = 32 * 1024;

    /// <summary>How long the exit handler waits for the stdio pumps to drain before giving up.</summary>
    public TimeSpan PumpDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often <see cref="AwaitStartupAndRegisterNetworkNamesAsync"/> re-inspects after start.</summary>
    public TimeSpan NetworkPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Hard cap on how long the post-start network poll keeps retrying at all (including the
    /// part that runs after <see cref="StartAsync"/> has already returned to its caller).</summary>
    public TimeSpan NetworkPollBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Once the detached post-start network registration (see
    /// <see cref="AwaitStartupAndRegisterNetworkNamesAsync"/>, launched from <see cref="StartAsync"/>
    /// but no longer part of its return path — cider-ede.26) has the runtime's confirmation that the
    /// container is running, how much longer it keeps polling for an address before giving up early
    /// and leaving the rest to <see cref="StatePoller"/>. Shorter than <see cref="NetworkPollBudget"/>
    /// so a container that is running but whose address is stuck doesn't poll as long as one that's
    /// still booting.</summary>
    public TimeSpan StartReturnBudget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary><c>POST /containers/{id}/start</c>; 304 when the container already runs.</summary>
    public async Task StartAsync(string idOrName, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        await handle.Gate.WaitAsync(ct);
        try
        {
            if (string.Equals(record.State.Status, "running", StringComparison.Ordinal) && handle.Process is not null)
            {
                throw DockerErrors.NotModified();
            }

            // Files `docker cp`'d in before the first start are mounted into the container here, so
            // they are in place the moment its entrypoint runs — Apple `container cp` cannot write
            // into a container that is not running, and copying them in afterwards is too late for
            // an image that reads them at once (see TryMountStagedArchivesAsync).
            var mountedBatches = await TryMountStagedArchivesAsync(record, ct);

            IContainerProcess process;
            try
            {
                process = await _runtime.StartContainerAsync(
                    record.RuntimeId,
                    new StartOptions { AttachStdin = record.Request.OpenStdin },
                    ct);
            }
            catch (RuntimeException ex)
            {
                record.State.Error = ex.Message;
                Persist(record);

                // The engine has no idea what this id is any more: someone ran `container delete`
                // (or `rm -f`) directly, or Apple's services restarted and lost it (ARCHITECTURE
                // §6/§9). `docker rm` is the only way out of that, so the 404 says so instead of
                // repeating the runtime's bare "container not found".
                throw ex.Kind == RuntimeErrorKind.NotFound
                    ? DockerErrors.NotFound(
                        $"container {record.Name} no longer exists in Apple container (removed outside cider); " +
                        $"run 'docker rm {record.Name}' to drop it")
                    : Translate(ex);
            }

            // Only now that the start has succeeded are the mounted batches marked: the marker turns
            // off the copy fallback for them for good, and a failed start would have left them
            // mounted on an engine container that the next start re-creates from the record alone.
            await MarkStagedBatchesMounted(mountedBatches, record.RuntimeId);

            handle.Process = process;
            handle.Tty = process.HasTty;
            handle.LogWriter = _logs.OpenWriter(record.Id);
            handle.Pumps.Clear();
            handle.Pumps.Add(PumpAsync(handle, process.Stdout, StdStream.Stdout));

            // Kept only to recognize a vanished container the started process reports on its own
            // stderr rather than the start call throwing (a warm tty cache — AppleContainerRuntime's
            // `_ttyByContainer` — can let `container start -a` spawn against a runtime id Apple has
            // already dropped; cider-msj). Discarded once HandleExitAsync has classified it.
            StringBuilder? stderrTail = null;
            if (process.Stderr is { } stderr)
            {
                stderrTail = new StringBuilder();
                handle.Pumps.Add(PumpAsync(handle, stderr, StdStream.Stderr, stderrTail));
            }

            record.State.Status = "running";
            record.State.StartedAt = DateTimeOffset.UtcNow;
            record.State.FinishedAt = null;
            record.State.ExitCode = 0;
            record.State.Error = null;
            record.State.Pid = process.Pid ?? 0;
            record.UserStopped = false;
            if (record.Healthcheck is { Test.Count: > 0 } && !IsHealthcheckDisabled(record.Healthcheck))
            {
                record.State.Health = new HealthState { Status = "starting" };
            }

            Persist(record);

            await BindAttachmentsAsync(handle);
            handle.ExitHandling = Task.Run(() => HandleExitAsync(record.Id, handle, process, stderrTail), CancellationToken.None);

            // Bind every published TCP host listener now, before the container's address (or even
            // its "running" status) is known: cider-ede.18. Published ports used to stay unbound
            // until AwaitStartupAndRegisterNetworkNamesAsync below found an address, so every
            // connection attempt during the VM boot it polls through (~3.5 s, plus up to
            // StartReturnBudget past that) got a bare "connection refused" instead of anything
            // queuing. The listener is already accepting by the time that wait even starts now;
            // TcpPortForwarder holds each accepted connection until EnsurePublishedPortsAsync
            // resolves the backend address (the detached follow-up below, once it is found — or a
            // later poller/refresh tick, if it is not, in time) instead of failing it.
            await EnsurePublishedPortsAsync(record, ct);

            // Everything `docker cp`'d into the container while it was not running goes in here,
            // with the gate still held and before this call returns, so the client that started it
            // cannot observe it running without the files it handed over before the start.
            // Aspire/DCP injects its development certificates exactly that way, and Apple
            // `container cp` refuses a container that is not running.
            await FlushStagedArchivesAsync(record, ct);

            Publish(record, "start");
            RaiseStateChanged(record, "start");

            // cider-ede.26: network name registration/DNS (and the address-aware port republish
            // that follows once an address is found) are the one piece of start's old work that
            // has nothing to do with the answer the caller is waiting on — Docker semantics say
            // `start` returns once the process is running, not once every side effect has settled
            // (cider-ede.18's own criterion: `docker start` returns in <= 200 ms on XPC, excluding
            // VM boot). This used to run inline here, so a container whose address Apple was slow
            // to attach — the very case AwaitStartupAndRegisterNetworkNamesAsync polls through —
            // kept the caller waiting for up to StartReturnBudget on top of the VM boot it had
            // already waited out. Detached on purpose, the same way HandleExitAsync's auto-remove
            // below is: CancellationToken.None throughout because the request's own `ct` is scoped
            // to the call that has already returned to its caller by the time this runs, and must
            // not cancel a poll that legitimately continues past it. StatePoller's
            // RefreshNetworkInfoAsync/EnsurePublishedPortsAsync (PollOnceAsync) are the safety net
            // once this task's own budget (NetworkPollBudget/StartReturnBudget) runs out first.
            _ = Task.Run(async () =>
            {
                try
                {
                    await AwaitStartupAndRegisterNetworkNamesAsync(record, process, CancellationToken.None);
                    await EnsurePublishedPortsAsync(record, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "post-start network registration failed for container {Container}", record.Id);
                }
            }, CancellationToken.None);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    /// <summary><c>POST /containers/{id}/stop</c>; 304 when the container is not running.</summary>
    public async Task StopAsync(string idOrName, int? timeoutSeconds, string? signal, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        await handle.Gate.WaitAsync(ct);
        try
        {
            if (!record.State.Running)
            {
                throw DockerErrors.NotModified();
            }

            record.UserStopped = true;
            Persist(record);

            try
            {
                await _runtime.StopContainerAsync(record.RuntimeId, timeoutSeconds ?? record.StopTimeout, signal ?? record.StopSignal, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }
        }
        finally
        {
            handle.Gate.Release();
        }

        await WaitForExitHandlingAsync(handle, timeoutSeconds);
        MarkStoppedWithoutHandle(handle, record);
        UnpublishPorts(record.Id);
        Publish(record, "stop");
    }

    /// <summary>
    /// Marks a container the daemon does not hold the stdio of (reconciled after a daemon restart,
    /// or created outside the daemon) as exited. Without this the record stays <c>running</c> until
    /// the state poller catches up seconds later, so <c>docker stop</c> would return while
    /// <c>docker inspect</c> still says the container is running.
    /// </summary>
    private void MarkStoppedWithoutHandle(ContainerHandle handle, ContainerRecord record)
    {
        if (handle.ExitHandling is not null || !record.State.Running)
        {
            return;
        }

        record.State.Status = "exited";
        record.State.FinishedAt ??= DateTimeOffset.UtcNow;
        record.State.ExitCode = 0;
        record.State.Error = "exit code unknown (daemon restarted)";
        Persist(record);
    }

    /// <summary><c>POST /containers/{id}/kill</c>; 409 when the container is not running.</summary>
    public async Task KillAsync(string idOrName, string? signal, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);
        var effective = string.IsNullOrEmpty(signal) ? "SIGKILL" : NormalizeSignal(signal);

        await handle.Gate.WaitAsync(ct);
        try
        {
            if (!record.State.Running)
            {
                throw DockerErrors.Conflict($"Cannot kill container: {idOrName}: Container {record.Id} is not running");
            }

            if (IsTerminating(effective))
            {
                record.UserStopped = true;
                Persist(record);
            }

            try
            {
                await _runtime.KillContainerAsync(record.RuntimeId, effective, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }
        }
        finally
        {
            handle.Gate.Release();
        }

        Publish(record, "kill", new Dictionary<string, string>(StringComparer.Ordinal) { ["signal"] = effective });
    }

    /// <summary><c>POST /containers/{id}/restart</c>.</summary>
    public async Task RestartAsync(string idOrName, int? timeoutSeconds, CancellationToken ct)
    {
        var record = Resolve(idOrName);

        try
        {
            await StopAsync(record.Id, timeoutSeconds, signal: null, ct);
        }
        catch (DockerApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotModified)
        {
            // Already stopped: a restart just starts it.
        }

        await StartAsync(record.Id, ct);
        Publish(record, "restart");
    }

    /// <summary><c>DELETE /containers/{id}</c>; 409 for a running container without <c>force</c>.</summary>
    public async Task RemoveAsync(string idOrName, bool force, bool removeVolumes, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        var wasRunning = record.State.Running;
        if (wasRunning && !force)
        {
            throw DockerErrors.ContainerRunning(record.Name);
        }

        if (wasRunning)
        {
            record.UserStopped = true;
            Persist(record);
            try
            {
                await _runtime.KillContainerAsync(record.RuntimeId, "SIGKILL", ct);
            }
            catch (RuntimeException ex)
            {
                _logger.LogDebug(ex, "killing container {Container} before removal failed", record.Id);
            }

            await WaitForExitHandlingAsync(handle, timeoutSeconds: 10);
        }

        await handle.Gate.WaitAsync(ct);
        try
        {
            record.State.Status = "removing";
            Persist(record);

            try
            {
                await _runtime.RemoveContainerAsync(record.RuntimeId, force, ct);
            }
            catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
            {
                // Already gone on the engine side; the record still has to go.
            }
            catch (RuntimeException ex)
            {
                record.State.Status = wasRunning ? "running" : "exited";
                Persist(record);
                throw Translate(ex);
            }

            if (handle.Process is { } process)
            {
                await process.DisposeAsync();
                handle.Process = null;
            }

            _names.Unregister(record.Id);
            UnpublishPorts(record.Id);
            ReleasePorts(record);
            _logs.Delete(record.Id);
            DropStagedArchives(record.Id);
            _store.Delete(record.Id);
            _handles.TryRemove(record.Id, out _);
            handle.Removed.TrySetResult(record.State.ExitCode);
            CompleteAttachments(handle);
        }
        finally
        {
            handle.Gate.Release();
        }

        if (removeVolumes)
        {
            await RemoveAnonymousVolumesAsync(record, ct);
        }

        Publish(record, "destroy");
        RaiseStateChanged(record, "destroy");
    }

    /// <summary>
    /// Called by <see cref="StatePoller"/> once a record's runtime container has been missing from
    /// <c>container ls -a</c> for two consecutive polls: it was removed outside cider (someone ran
    /// <c>container delete</c>/<c>rm -f</c> directly, or Apple's services restarted and lost it —
    /// ARCHITECTURE §6/§9). This is the record-side half of <see cref="RemoveAsync"/> without the
    /// <c>container delete</c> call — there is nothing left on the engine to delete — and it takes
    /// the same per-container gate <see cref="RemoveAsync"/> does, so it cannot race a create/remove
    /// already in flight. Anonymous volumes are kept, exactly like <c>docker rm</c> without
    /// <c>-v</c>.
    /// </summary>
    /// <returns><c>true</c> if the record was actually dropped; <c>false</c> if this call bailed out
    /// because a start, remove or re-create raced in first, leaving the record untouched.</returns>
    internal async Task<bool> ForgetVanishedAsync(ContainerRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var handle = GetHandle(record.Id);

        await handle.Gate.WaitAsync(ct);
        try
        {
            // A start, remove or re-create may have raced in while this call waited for the gate
            // (or the caller's own miss count is stale): bail out and let the next poll re-evaluate
            // rather than drop a record that is no longer the one the poller looked at.
            if (_store.Get(record.Id) is not { } current ||
                !string.Equals(current.RuntimeId, record.RuntimeId, StringComparison.Ordinal) ||
                handle.Process is not null)
            {
                return false;
            }

            var wasRunning = current.State.Running;

            // Mirrors what RemoveAsync does before a running container's "die": without this,
            // RestartSupervisor would see the "die" below, treat it as a container to restart, and
            // resurrect the very record this call is meant to drop (Persist inside MarkRestarting
            // would re-add it to the store).
            current.UserStopped = true;

            _names.Unregister(current.Id);
            UnpublishPorts(current.Id);
            ReleasePorts(current);
            _logs.Delete(current.Id);
            DropStagedArchives(current.Id);
            _store.Delete(current.Id);
            _handles.TryRemove(current.Id, out _);
            handle.Removed.TrySetResult(current.State.ExitCode);
            CompleteAttachments(handle);

            if (wasRunning)
            {
                Publish(current, "die", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exitCode"] = current.State.ExitCode.ToString(CultureInfo.InvariantCulture),
                });
                RaiseStateChanged(current, "die");
            }

            Publish(current, "destroy");
            RaiseStateChanged(current, "destroy");

            return true;
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    /// <summary><c>POST /containers/{id}/wait?condition=</c>.</summary>
    public async Task<ContainerWaitResponse> WaitAsync(string idOrName, string condition, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);
        var wanted = string.IsNullOrEmpty(condition) ? "not-running" : condition;

        switch (wanted)
        {
            case "removed":
                {
                    var code = await handle.Removed.Task.WaitAsync(ct);
                    return new ContainerWaitResponse { StatusCode = code };
                }

            case "next-exit":
                {
                    // Capture the pending exit before awaiting: `docker run` waits before it starts.
                    var pending = handle.NextExit.Task;
                    var code = await pending.WaitAsync(ct);
                    return Response(code);
                }

            default:
                {
                    if (!record.State.Running)
                    {
                        return Response(record.State.ExitCode);
                    }

                    var pending = handle.NextExit.Task;
                    var code = await pending.WaitAsync(ct);
                    return Response(code);
                }
        }

        ContainerWaitResponse Response(int code)
        {
            var error = string.IsNullOrEmpty(record.State.Error)
                ? null
                : new ContainerWaitExitError { Message = record.State.Error };
            return new ContainerWaitResponse { StatusCode = code, Error = error };
        }
    }

    /// <summary><c>POST /containers/{id}/resize</c>; succeeds even when nothing is running yet.</summary>
    public async Task ResizeAsync(string idOrName, int cols, int rows, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        if (handle.Process is { } process)
        {
            try
            {
                await process.ResizeAsync(cols, rows, ct);
            }
            catch (Exception ex) when (ex is RuntimeException or IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "resize of container {Container} failed", record.Id);
            }
        }

        Publish(record, "resize", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["height"] = rows.ToString(CultureInfo.InvariantCulture),
            ["width"] = cols.ToString(CultureInfo.InvariantCulture),
        });
    }

    /// <summary><c>POST /containers/{id}/rename</c>.</summary>
    public Task RenameAsync(string idOrName, string newName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var record = Resolve(idOrName);
        var trimmed = (newName ?? "").TrimStart('/');
        if (!Names.IsValidDockerName(trimmed))
        {
            throw DockerErrors.BadParameter(
                $"Invalid container name ({trimmed}), only [a-zA-Z0-9][a-zA-Z0-9_.-] are allowed");
        }

        lock (_nameGate)
        {
            var existing = FindByName(trimmed);
            if (existing is not null && !string.Equals(existing.Id, record.Id, StringComparison.Ordinal))
            {
                throw DockerErrors.ContainerNameConflict(trimmed, existing.Id);
            }

            var oldName = record.Name;
            record.Name = trimmed;
            Persist(record);
            Publish(record, "rename", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["oldName"] = oldName,
            });
        }

        return Task.CompletedTask;
    }

    /// <summary><c>POST /containers/{id}/update</c>: only the restart policy can be changed here.</summary>
    public Task UpdateAsync(string idOrName, ContainerUpdateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var record = Resolve(idOrName);

        if (request.Memory != 0 || request.NanoCpus != 0 || request.CpuShares != 0 ||
            request.CpuQuota != 0 || request.CpuPeriod != 0 || request.PidsLimit is > 0)
        {
            throw DockerErrors.NotImplemented("cider: updating container resources is not supported by Apple container");
        }

        if (request.RestartPolicy is { } policy)
        {
            record.RestartPolicy = policy;
            (record.Request.HostConfig ??= new HostConfig()).RestartPolicy = policy;
            Persist(record);
            Publish(record, "update");
        }

        return Task.CompletedTask;
    }

    /// <summary><c>POST /containers/prune</c>: removes stopped containers.</summary>

    /// <summary>Bound on how much of the stderr tail <see cref="HandleExitAsync"/> keeps around to
    /// classify a vanished container against; the runtime's own error line is always near the end.</summary>
    private const int StderrTailCapBytes = 4 * 1024;

    private async Task PumpAsync(ContainerHandle handle, Stream source, StdStream stream, StringBuilder? stderrTail = null)
    {
        var buffer = new byte[PumpBufferSize];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0)
                {
                    return;
                }

                var chunk = buffer.AsMemory(0, read).ToArray();

                var writer = handle.LogWriter;
                if (writer is not null)
                {
                    await writer.WriteAsync(stream, chunk, CancellationToken.None);
                }

                Broadcast(handle, stream, chunk);

                if (stderrTail is not null)
                {
                    AppendCappedTail(stderrTail, chunk);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The process went away underneath us; the exit handler takes it from here.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "log pump for container {Container} failed", handle.Id);
        }
    }

    private static void AppendCappedTail(StringBuilder tail, byte[] chunk)
    {
        tail.Append(Encoding.UTF8.GetString(chunk));
        if (tail.Length > StderrTailCapBytes)
        {
            tail.Remove(0, tail.Length - StderrTailCapBytes);
        }
    }

    /// <summary>
    /// A small echo of <c>CliErrorMapper.NotFoundMarkers</c> (Cider.AppleContainer) for the started
    /// process's own stderr — Cider.Core cannot reference that internal type, since AppleContainer
    /// depends on Core, not the other way around. Scoped to blobs that also mention "container" so
    /// an application's own "404 not found" logging does not trip it.
    /// </summary>
    private static bool LooksLikeVanishedContainer(string? stderrTail)
    {
        if (string.IsNullOrWhiteSpace(stderrTail))
        {
            return false;
        }

        var text = stderrTail.ToLowerInvariant();
        if (!text.Contains("container", StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains("not found", StringComparison.Ordinal) ||
            text.Contains("no such", StringComparison.Ordinal) ||
            text.Contains("does not exist", StringComparison.Ordinal);
    }

    /// <summary>
    /// The actual terminal decision for a container the stderr heuristic only flagged as suspect:
    /// asks the runtime itself (the same <see cref="IContainerRuntime.InspectContainerAsync"/> call
    /// <see cref="StatePoller"/>'s reconciliation loop relies on to detect a dropped container,
    /// which returns <c>null</c> when the engine no longer knows the id) rather than trusting
    /// application output. Any runtime error is swallowed — a flaky inspect must never itself block
    /// exit handling nor get treated as confirmation.
    /// </summary>
    private async Task<bool> IsConfirmedVanishedAsync(string runtimeId)
    {
        try
        {
            return await _runtime.InspectContainerAsync(runtimeId, CancellationToken.None) is null;
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "confirming vanished container {RuntimeId} failed", runtimeId);
            return false;
        }
    }

    /// <summary>
    /// Completes <paramref name="handle"/>'s pending <c>NextExit</c> waiter (the <c>next-exit</c>
    /// and default <c>not-running</c> `docker wait` conditions) with <paramref name="exitCode"/>
    /// and swaps in a fresh TaskCompletionSource for the following run. This is the one path both
    /// <see cref="HandleExitAsync"/> (a process the daemon itself started and held) and
    /// <see cref="StatePoller"/> (a container cider only adopted, with no held process ever able to
    /// drive <see cref="HandleExitAsync"/>) go through when a record's state transitions to exited
    /// -- exit completion is a property of that transition, not of who happened to observe it
    /// (cider-ede.33), so a future third caller cannot reintroduce the gap where the record says
    /// exited but a `docker wait` is left blocked forever. <c>TrySetResult</c> is a no-op if the
    /// TCS this call captured a reference to is already completed -- the other caller having raced
    /// in first for the very same exit, or a later start already having swapped in the next run's
    /// TCS before this call got here -- so this can never resolve a stale wait with a different
    /// run's exit code.
    /// </summary>
    private static void CompleteExitWait(ContainerHandle handle, int exitCode)
    {
        var pending = handle.NextExit;
        handle.NextExit = ContainerHandle.NewExit();
        pending.TrySetResult(exitCode);
    }

    private async Task HandleExitAsync(string id, ContainerHandle handle, IContainerProcess process, StringBuilder? stderrTail = null)
    {
        var exitCode = -1;
        try
        {
            exitCode = await process.Exited;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "waiting for container {Container} to exit failed", id);
        }

        try
        {
            if (handle.Pumps.Count > 0)
            {
                await Task.WhenAll(handle.Pumps).WaitAsync(PumpDrainTimeout);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("stdio pumps of container {Container} did not drain in time", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "stdio pumps of container {Container} failed", id);
        }

        if (handle.LogWriter is { } writer)
        {
            handle.LogWriter = null;
            try
            {
                await writer.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "closing the log writer of container {Container} failed", id);
            }
        }

        var record = _store.Get(id);
        if (record is not null)
        {
            record.State.Status = "exited";
            record.State.ExitCode = exitCode;
            record.State.FinishedAt = DateTimeOffset.UtcNow;
            record.State.Pid = 0;

            // A warm tty cache (AppleContainerRuntime._ttyByContainer) can let `container start -a`
            // spawn even though Apple's own container table has already dropped the runtime id: the
            // start call never throws, so RestartSupervisor never sees the NotFound it otherwise
            // catches. The attached process's own stderr is only ever a cheap pre-filter for this —
            // it is the application's own output (Broadcast writes the same bytes to the container
            // log), so an ordinary "no such container" logged by the app itself must never be trusted
            // on its own. Stamp the same marker MarkVanished uses only once the runtime itself
            // confirms the id is gone, so OnStateChanged recognizes this and gives up instead of
            // rescheduling into a tight loop (cider-msj).
            if (exitCode != 0 && LooksLikeVanishedContainer(stderrTail?.ToString()) &&
                await IsConfirmedVanishedAsync(record.RuntimeId))
            {
                record.State.Error = RestartSupervisor.VanishedError;
            }

            if (record.State.Health is { } health)
            {
                health.Status = "unhealthy";
            }

            Persist(record);
        }

        handle.Process = null;
        CompleteAttachments(handle);
        CompleteExitWait(handle, exitCode);

        if (record is null)
        {
            return;
        }

        _names.Unregister(record.Id);
        UnpublishPorts(record.Id);
        Publish(record, "die", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture),
        });
        RaiseStateChanged(record, "die");

        if (record.AutoRemove)
        {
            // Detached on purpose: RemoveAsync takes the same gate a stop may still be holding.
            _ = Task.Run(async () =>
            {
                try
                {
                    await RemoveAsync(record.Id, force: true, removeVolumes: true, CancellationToken.None);
                }
                catch (Exception ex) when (ex is DockerApiException or RuntimeException)
                {
                    _logger.LogDebug(ex, "auto-removing container {Container} failed", record.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "auto-removing container {Container} failed", record.Id);
                }
            });
        }
    }

    private async Task WaitForExitHandlingAsync(ContainerHandle handle, int? timeoutSeconds)
    {
        var exitHandling = handle.ExitHandling;
        if (exitHandling is null)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(timeoutSeconds ?? 10, 1)) + TimeSpan.FromSeconds(10);
        try
        {
            await exitHandling.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("container {Container} did not finish exiting within {Timeout}", handle.Id, timeout);
        }
    }

    private static bool IsTerminating(string signal) =>
        signal is "SIGKILL" or "SIGTERM" or "SIGINT" or "SIGQUIT" or "SIGSTOP" or "KILL" or "TERM" or "INT" or "QUIT";

    private static string NormalizeSignal(string signal)
    {
        var trimmed = signal.Trim();
        if (trimmed.Length == 0)
        {
            return "SIGKILL";
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return trimmed;
        }

        return trimmed.StartsWith("SIG", StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToUpperInvariant()
            : "SIG" + trimmed.ToUpperInvariant();
    }

    private static bool IsHealthcheckDisabled(HealthConfig config) =>
        config.Test.Count > 0 && string.Equals(config.Test[0], "NONE", StringComparison.Ordinal);
}
