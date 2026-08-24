namespace Cider.Tests.Fakes;

/// <summary>
/// Test hooks that simulate the timing quirks of Apple container 1.2.2 the daemon has to work
/// around (docs/apple-container-notes.md §12): <c>container inspect</c> reporting no network
/// attachments for a second or two after start, and <c>container exec</c> rejecting with
/// "is not running" for a similar window even though the init process is already held.
/// </summary>
public sealed partial class FakeContainerRuntime
{
    private readonly Dictionary<string, int> _pendingEmptyNetworkInspects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingExecNotRunningFailures = new(StringComparer.Ordinal);

    /// <summary>
    /// Test hook: the next <paramref name="count"/> calls to <see cref="InspectContainerAsync"/> for
    /// <paramref name="runtimeId"/> report no network attachments at all, even though the container
    /// is running and has an address — simulating Apple's <c>status.networks</c> lag right after
    /// <c>container start</c>.
    /// </summary>
    public void DelayNetworkAttachment(string runtimeId, int count)
    {
        lock (_sync)
        {
            _pendingEmptyNetworkInspects[runtimeId] = count;
        }
    }

    /// <summary>
    /// Test hook: the next <paramref name="count"/> calls to <see cref="ExecAsync"/> against
    /// <paramref name="runtimeId"/> fail with the typed
    /// <c>RuntimeErrorReason.ContainerNotRunning</c> condition, worded deliberately unlike Apple's
    /// CLI (see <c>NotRunning</c> in FakeContainerRuntime.Containers.cs) — simulating Apple's
    /// <c>container exec</c> rejecting for a moment after start even though the container is
    /// already held as running.
    /// </summary>
    public void FailExecUntilRunning(string runtimeId, int count)
    {
        lock (_sync)
        {
            _pendingExecNotRunningFailures[runtimeId] = count;
        }
    }

    // Callers already hold `_sync` (Monitor.Enter is reentrant on the same thread), so these just
    // decrement in place and report whether this particular call should still be flaky.

    private bool ShouldDelayNetworkAttachment(string runtimeId)
    {
        lock (_sync)
        {
            if (!_pendingEmptyNetworkInspects.TryGetValue(runtimeId, out var remaining) || remaining <= 0)
            {
                return false;
            }

            _pendingEmptyNetworkInspects[runtimeId] = remaining - 1;
            return true;
        }
    }

    private bool ShouldFailExecAsNotRunning(string runtimeId)
    {
        lock (_sync)
        {
            if (!_pendingExecNotRunningFailures.TryGetValue(runtimeId, out var remaining) || remaining <= 0)
            {
                return false;
            }

            _pendingExecNotRunningFailures[runtimeId] = remaining - 1;
            return true;
        }
    }
}
