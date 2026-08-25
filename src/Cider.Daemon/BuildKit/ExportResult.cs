namespace Cider.Daemon.BuildKit;

/// <summary>
/// What one captured <c>moby.filesync.v1.FileSend/DiffCopy</c> call (see <see cref="FileSendCapture"/>)
/// produced: the tar it wrote and the <c>exporter-md-*</c> metadata the call carried (e.g.
/// <c>image.name</c>). A future caller (T7's Solve handler) claims the file via
/// <see cref="TakeOwnership"/> before doing anything with it — an unclaimed result is deleted when
/// its owning <see cref="SessionBridgeHandle"/> tears down, so a build nobody ever collected the
/// export for does not leak a tar file into <c>TmpDir</c> forever.
/// </summary>
public sealed class ExportResult
{
    private int _owned;

    /// <summary>Absolute path to the captured tar under <c>CiderOptions.TmpDir</c>.</summary>
    public required string TarPath { get; init; }

    /// <summary>The <c>exporter-md-*</c> request metadata, keys with the prefix stripped.</summary>
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    /// <summary>
    /// Claims this result so <see cref="SessionBridgeHandle"/>'s teardown sweep leaves
    /// <see cref="TarPath"/> alone. Returns <see langword="true"/> the first time it is called for
    /// this instance, <see langword="false"/> on every later call (already claimed).
    /// </summary>
    public bool TakeOwnership() => Interlocked.Exchange(ref _owned, 1) == 0;

    /// <summary>
    /// Whether <see cref="TakeOwnership"/> has ever succeeded. Checked, never set, by the teardown
    /// sweep — so cleanup never itself claims a result nobody asked for.
    /// </summary>
    internal bool IsOwned => Volatile.Read(ref _owned) != 0;
}
