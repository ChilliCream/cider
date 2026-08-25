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
    /// Things the pass could not fix — a resource it could not list, drop or adopt cleanly, or a DNS
    /// forwarder it could not ensure. Never used to swallow a failure that should abort the pass
    /// instead: see <see cref="StateSynchronizer.SyncAsync"/>.
    /// </summary>
    public List<string> Warnings { get; } = [];

    /// <summary><c>true</c> when nothing changed and nothing went wrong — the idempotent "second run" outcome.</summary>
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
