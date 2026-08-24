using Cider.Core.Configuration;

namespace Cider.Core.State;

/// <summary>
/// The daemon's stable per-engine UUID, exposed on <c>/info</c> as <c>ID</c>. Real dockerd persists
/// this at <c>&lt;DataDir&gt;/engine-id</c>, generating it once on first start and reading it back on
/// every start after; cider does the same under the same file name so the id survives daemon
/// restarts (Testcontainers and friends key caches on it).
/// </summary>
public sealed class EngineId
{
    /// <summary>Name of the file inside <c>DataDir</c> that holds the id.</summary>
    public const string FileName = "engine-id";

    /// <summary>The stable id: read from disk, or generated and persisted on first use.</summary>
    public string Value { get; }

    public EngineId(CiderOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).DataDir)
    {
    }

    public EngineId(string dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        Value = LoadOrCreate(dataDir);
    }

    private static string LoadOrCreate(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);

        try
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _))
            {
                return existing;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var created = Guid.NewGuid().ToString();
        TryPersist(dataDir, path, created);
        return created;
    }

    // A daemon that cannot write its data directory must still start up and report an id; the
    // freshly generated one is simply not stable across restarts in that case.
    private static void TryPersist(string dataDir, string path, string value)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, value);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
