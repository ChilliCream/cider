using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <see cref="XpcContainerRuntime.OpenLogsAsync"/> over XPC (task cider-ede.9). Replaces the
/// <c>container logs [-f] [-n]</c> stream-until-dispose child process
/// (docs/spikes/xpc/01-cider-runtime-map.md §2 <c>OpenLogsAsync</c> row) with the apiserver's own
/// <c>containerLogs</c> fd, so <c>docker logs</c> also works for containers cider did not start
/// (docs/spikes/xpc/03-limitations-audit-1.3.md "Logs merged for containers the daemon did not
/// start" row) without spawning a subprocess.
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    /// <summary>How often the follow-mode stop-watcher (<see cref="WatchForContainerStop"/>)
    /// re-checks the container's running state — independent of <see cref="FollowingFileStream"/>'s
    /// own 100 ms growth-poll interval, since a <c>containerList</c> round trip is far more expensive
    /// than a local file-length check.</summary>
    private static readonly TimeSpan LogFollowStatePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// <c>containerLogs {id}</c> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.10) →
    /// <c>xpc_array_dup_fd(logs, 0)</c> — fd 0 is <c>stdio.log</c>, the merged stdout+stderr text file
    /// every container writes to whether or not a client is attached and regardless of who started it
    /// (audit's "Logs merged" row); fd 1 (<c>vminitd.log</c>, the boot log) is out of scope for this
    /// task. No client-side timeout on the call itself — <c>containerLogs</c> is in the apiserver's
    /// own "no timeout" list (§1.4 table: <c>ContainerClient.swift:151</c> et al. call
    /// <c>responseTimeout: nil</c>). The daemon-side labelling stays "stdout" unchanged
    /// (<c>ContainerManager.LogsAsync</c>, <c>ContainerManager.Logs.cs</c>) — the file itself carries
    /// no stream separation to preserve. <see cref="FollowingFileStream"/> does the
    /// tail/follow/truncation work; when <paramref name="follow"/>, <see cref="WatchForContainerStop"/>
    /// also starts, since the merged file itself never signals "the writer is gone" the way a real log
    /// driver's pipe would.
    /// Falls back to the CLI transport on <see cref="RuntimeErrorKind.Unavailable"/> — the same
    /// Fallback rule as every other member (fix direction §4).
    /// </summary>
    public Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        try
        {
            using var request = new XpcMessage("containerLogs");
            request.SetString("id", runtimeId);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);

            var fd = reply.DupArrayFd("logs", 0);
            var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            var stream = new FollowingFileStream(handle, follow, tail);

            if (follow)
            {
                WatchForContainerStop(runtimeId, stream, ct);
            }

            return (Stream)stream;
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerLogs", ex);
            return await _cliFallback.OpenLogsAsync(runtimeId, follow, tail, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"logs {runtimeId}");
        }
    });

    /// <summary>
    /// Fire-and-forget background watcher, started only while <paramref name="stream"/> is following:
    /// polls <see cref="InspectContainerAsync"/> (the same <c>containerList{ids:[id]}</c> call
    /// <c>docker inspect</c> uses) every <see cref="LogFollowStatePollInterval"/> and calls
    /// <see cref="FollowingFileStream.Stop"/> the moment the container is no longer
    /// <see cref="RuntimeContainerState.Running"/> — real dockerd's log driver would get an EOF from
    /// its writer when the container process exits, so <c>docker logs -f</c> returns; Apple's plain
    /// merged file has no such signal, so this supplies it. Self-terminates on
    /// <paramref name="ct"/> (the caller's own cancellation — the request-aborted token for the HTTP
    /// case) so it never outlives the log request it was opened for. Any failure while polling
    /// (apiserver down and the CLI fallback also failing, say) is logged at Debug and simply stops the
    /// watcher — a watcher that cannot tell the container's state must not guess "stopped" and cut a
    /// still-live stream short.
    /// </summary>
    private void WatchForContainerStop(string runtimeId, FollowingFileStream stream, CancellationToken ct) =>
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(LogFollowStatePollInterval, ct).ConfigureAwait(false);

                        var container = await InspectContainerAsync(runtimeId, ct).ConfigureAwait(false);
                        if (container is null || container.State != RuntimeContainerState.Running)
                        {
                            stream.Stop();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "log-follow stop-watcher for {Id} stopped watching", runtimeId);
                }
            },
            CancellationToken.None);
}
