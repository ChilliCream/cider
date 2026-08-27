namespace Cider.AppleContainer;

/// <summary>
/// Daemon-wide gate against cider's own content-store sweep racing its own image writes
/// (cider-ede.31 fix direction §3). Since cider-ede.41 removed the prune-path sweep on both
/// transports, the only sweep cider can still run is Apple's own in-binary sweep-on-every-delete of
/// the `container image delete` CLI (<c>ImageDelete.swift</c>, confirmed live —
/// <c>container image delete --help</c> carries no flag to skip it) both walk the *whole* content
/// store and delete every blob it considers unreferenced — run by the CLI transport's every
/// delete, and by the XPC transport's apiserver-unavailable delete fallback. A pull/load that has written blobs but
/// not yet committed its index entry looks exactly like garbage to that sweep — this gate keeps a
/// sweep from ever starting while one of those writes is still in flight in this daemon, and blocks
/// new writes from starting once a sweep has begun. It does not, and is not meant to, coordinate
/// with a `container` CLI invocation running outside cider's own process — fix direction §3 is
/// explicit that this is out of scope ("just stop cider from corrupting the store by itself").
/// </summary>
/// <remarks>
/// <para>
/// One instance per runtime — <c>AppleContainerRuntime</c> and <c>XpcContainerRuntime</c> each keep
/// their own, and the two do not coordinate with each other (see each type's own doc comment for why
/// that is fine for the writes/sweeps each one actually performs). What matters for correctness is
/// that every write and every sweep *a given transport can run* goes through that same transport's one
/// instance — cider-ede.31's correction closed the one gap where that was not yet true: an
/// XPC-transport build used to delegate straight to the CLI runtime's <c>BuildImageAsync</c> with no
/// XPC-side gate entry first, so it was invisible to the XPC transport's own sweep
/// even though it commits new content the same way a pull/load does.
/// <c>XpcContainerRuntime.BuildImageAsync</c> now enters its own instance before delegating, so every
/// write path on both transports is covered by the sweep that could actually race it.
/// </para>
/// <para>
/// Classic two-semaphore readers/writer lock, roles named for what they mean here rather than the
/// generic reader/writer vocabulary: <see cref="EnterImageWriteAsync"/> ("shared" — pulls/loads run
/// freely alongside each other) and <see cref="EnterSweepAsync"/> ("exclusive" — a sweep waits for
/// every in-flight write to finish, then blocks new ones until it completes). Not fair to a queued
/// sweep under sustained write load — an unbroken stream of writes can in principle starve it
/// indefinitely — but a sweep here is rare (the CLI transport's delete, or the XPC transport's
/// unavailable-apiserver delete fallback — never a prune, since cider-ede.41), not a hot path; starving it only delays reclaiming space, and never corrupts
/// anything, which is the one property this type exists to guarantee.
/// </para>
/// </remarks>
internal sealed class BlobSweepGate
{
    private readonly SemaphoreSlim _accounting = new(1, 1);
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private int _writers;

    /// <summary>Enter as an in-flight image write (pull/load, and any CLI-fallback equivalent) —
    /// runs concurrently with every other writer, but never while a sweep holds the gate.</summary>
    public async Task<IAsyncDisposable> EnterImageWriteAsync(CancellationToken ct)
    {
        await _accounting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writers == 0)
            {
                // First writer of a (possibly concurrent) group: holds the exclusive slot for as
                // long as any writer in the group is in flight, so a sweep that arrives has to wait.
                await _exclusive.WaitAsync(ct).ConfigureAwait(false);
            }

            _writers++;
        }
        finally
        {
            _accounting.Release();
        }

        return new WriteScope(this);
    }

    private async Task ExitImageWriteAsync()
    {
        await _accounting.WaitAsync().ConfigureAwait(false);
        try
        {
            _writers--;
            if (_writers == 0)
            {
                _exclusive.Release();
            }
        }
        finally
        {
            _accounting.Release();
        }
    }

    /// <summary>Enter as a store-wide sweep — waits for every in-flight write to finish, then holds
    /// the gate exclusively (blocking new writes) until disposed.</summary>
    public async Task<IAsyncDisposable> EnterSweepAsync(CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        return new SweepScope(this);
    }

    private void ExitSweep() => _exclusive.Release();

    private sealed class WriteScope(BlobSweepGate gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(gate.ExitImageWriteAsync());
    }

    private sealed class SweepScope(BlobSweepGate gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.ExitSweep();
            return ValueTask.CompletedTask;
        }
    }
}
