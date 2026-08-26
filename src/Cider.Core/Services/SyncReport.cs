namespace Cider.Core.Services;

/// <summary>The outcome of one <see cref="StateSynchronizer.SyncAsync"/> pass.</summary>
public sealed class SyncReport
{
    /// <summary>What changed among containers.</summary>
    public SyncResourceReport Containers { get; } = new();

    /// <summary>What changed among networks.</summary>
    public SyncResourceReport Networks { get; } = new();

    /// <summary>What changed among volumes.</summary>
    public SyncResourceReport Volumes { get; } = new();

    /// <summary>
    /// What happened to DNS forwarders this pass (cider-ede.39 — a forwarder resync used to be
    /// entirely invisible in the report): <see cref="SyncResourceReport.Adopted"/> is the networks a
    /// forwarder was confirmed reachable for (started fresh, or already running — <see
    /// cref="Net.IDnsForwarderService.EnsureAsync"/> is idempotent and gives the caller no way to tell
    /// those apart, so both count as "ensured" here), <see cref="SyncResourceReport.Removed"/> is the
    /// networks whose forwarder was stopped because the network record itself was dropped (<see
    /// cref="NetworkManager.ReconcileAsync"/>). <see cref="SyncResourceReport.Updated"/> is never
    /// populated. Deliberately excluded from <see cref="IsEmpty"/>: a forwarder is re-ensured on every
    /// pass that has a running container on its network even when nothing about it actually changed,
    /// so folding it in would make the "nothing to do" summary fire almost never on a daemon with
    /// anything running — the <c>dns:</c> line in the human summary is printed unconditionally instead
    /// (like the other resource lines already are), so it stays visible without corrupting that signal.
    /// </summary>
    public SyncResourceReport Dns { get; } = new();

    /// <summary>
    /// Things the pass could not fix — a resource it could not list, drop or adopt cleanly, or a DNS
    /// forwarder it could not ensure. Never used to swallow a failure that should abort the pass
    /// instead: see <see cref="StateSynchronizer.SyncAsync"/>.
    /// </summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// <c>true</c> when nothing changed and nothing went wrong — the idempotent "second run" outcome.
    /// <see cref="Dns"/> is deliberately not part of this check; see its doc comment.
    /// </summary>
    public bool IsEmpty =>
        Containers.IsEmpty && Networks.IsEmpty && Volumes.IsEmpty && Warnings.Count == 0;
}

/// <summary>Names (or ids, for a record adopted with none of its own) touched for one resource kind.</summary>
public sealed class SyncResourceReport
{
    /// <summary>Records dropped because the engine no longer has the resource behind them.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Records created for a resource the engine has that cider had no record of.</summary>
    public List<string> Adopted { get; } = [];

    /// <summary>Records whose persisted state was corrected to match the engine (e.g. a status fix-up).</summary>
    public List<string> Updated { get; } = [];

    /// <summary><c>true</c> when this resource kind saw no change during the pass.</summary>
    public bool IsEmpty => Removed.Count == 0 && Adopted.Count == 0 && Updated.Count == 0;
}
