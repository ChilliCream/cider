namespace Cider.Core.Ids;

/// <summary>
/// Bridges Docker identities (64-hex id + name) and Apple <c>container</c> ids.
/// The Docker name is reused verbatim when the Apple CLI accepts it, otherwise the
/// container's short id is used (decision log, 2026-08-21).
/// </summary>
public static class ContainerIdentity
{
    /// <summary>Label carrying the Docker 64-hex id on every Apple object we create.</summary>
    public const string IdLabel = "com.chillicream.cider.id";

    /// <summary>Label carrying the Docker name on every Apple object we create.</summary>
    public const string NameLabel = "com.chillicream.cider.name";

    /// <summary>Label marking objects that were not created through Cider (<c>false</c>).</summary>
    public const string ManagedLabel = "com.chillicream.cider.managed";

    /// <summary>
    /// Transitional (rename to Cider): the pre-rename <see cref="IdLabel"/>. Apple objects created by
    /// an older daemon still carry it, so it is read and never written. This constant and the
    /// fallbacks that use it may be deleted once no <c>com.apple-demon.*</c> labelled objects remain.
    /// </summary>
    public const string LegacyIdLabel = "com.apple-demon.id";

    /// <summary>Transitional (rename to Cider): pre-rename <see cref="NameLabel"/>; read, never written.</summary>
    public const string LegacyNameLabel = "com.apple-demon.name";

    /// <summary>Transitional (rename to Cider): pre-rename <see cref="ManagedLabel"/>; read, never written.</summary>
    public const string LegacyManagedLabel = "com.apple-demon.managed";

    /// <summary>
    /// Reads a Cider label, falling back to its pre-rename <c>com.apple-demon.*</c> key. Every label
    /// read goes through this so a daemon started after the rename still recognises the Apple
    /// containers, networks and volumes an older daemon created. Transitional: the fallback may be
    /// dropped once no old-labelled objects remain.
    /// </summary>
    public static bool TryReadLabel(
        IReadOnlyDictionary<string, string>? labels,
        string key,
        string legacyKey,
        out string value)
    {
        if (labels is not null && (labels.TryGetValue(key, out var found) || labels.TryGetValue(legacyKey, out found)))
        {
            value = found;
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>Picks the Apple runtime id for a container: its Docker name when usable, else the short id.</summary>
    public static string ResolveRuntimeId(string dockerId, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(dockerId);

        var candidate = name?.TrimStart('/');
        if (!string.IsNullOrEmpty(candidate) && Names.IsValidAppleContainerId(candidate))
        {
            return candidate;
        }

        return DockerId.Short(dockerId);
    }

    /// <summary>The labels Cider stamps on every Apple object, merged over the user's labels.</summary>
    public static Dictionary<string, string> BuildLabels(
        string dockerId,
        string name,
        IReadOnlyDictionary<string, string>? userLabels = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userLabels is not null)
        {
            foreach (var (key, value) in userLabels)
            {
                labels[key] = value;
            }
        }

        labels[IdLabel] = dockerId;
        labels[NameLabel] = name.TrimStart('/');
        return labels;
    }

    /// <summary>Reads the Docker id back from an Apple object's labels; <c>null</c> when the object is not ours.</summary>
    public static string? ReadDockerId(IReadOnlyDictionary<string, string>? labels) =>
        TryReadLabel(labels, IdLabel, LegacyIdLabel, out var id) && DockerId.IsFullId(id) ? id : null;

    /// <summary>Reads the Docker name back from an Apple object's labels.</summary>
    public static string? ReadDockerName(IReadOnlyDictionary<string, string>? labels) =>
        TryReadLabel(labels, NameLabel, LegacyNameLabel, out var name) && name.Length > 0 ? name : null;
}
