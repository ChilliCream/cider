using System.Text;
using Cider.Core.DockerApi.Json;

namespace Cider.Core.State;

/// <summary>
/// A record store backed by one JSON file per record (<c>&lt;directory&gt;/&lt;key&gt;.json</c>).
/// Every record is loaded into memory at construction; writes go to a temporary file that is
/// renamed over the target so a crashed daemon never leaves a half-written record behind.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class JsonFileStore<T> : IRecordStore<T>
    where T : class
{
    private const string Extension = ".json";
    private const string EncodedPrefix = "_x";

    private readonly string _directory;
    private readonly Dictionary<string, T> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates (if needed) and loads <paramref name="directory"/>.</summary>
    public JsonFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        System.IO.Directory.CreateDirectory(_directory);
        Load();
    }

    /// <summary>The directory this store persists into.</summary>
    public string Directory => _directory;

    /// <summary>Number of records currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<T> GetAll()
    {
        lock (_gate)
        {
            return _cache.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public T? Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            return _cache.GetValueOrDefault(key);
        }
    }

    /// <inheritdoc />
    public bool TryGet(string key, out T? record)
    {
        record = Get(key);
        return record is not null;
    }

    /// <inheritdoc />
    public void Upsert(string key, T record)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _cache[key] = record;
            WriteFile(key, record);
        }
    }

    /// <inheritdoc />
    public bool Delete(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (_gate)
        {
            var removed = _cache.Remove(key);
            var path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }

            return removed;
        }
    }

    /// <summary>Absolute path of the file backing <paramref name="key"/>.</summary>
    public string PathFor(string key) => Path.Combine(_directory, Encode(key) + Extension);

    private void Load()
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            var key = Decode(Path.GetFileNameWithoutExtension(file));
            if (key is null)
            {
                continue;
            }

            try
            {
                var record = DockerJson.Deserialize<T>(File.ReadAllText(file));
                if (record is not null)
                {
                    _cache[key] = record;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // A corrupt or unreadable record must not stop the daemon; it is simply forgotten.
            }
        }
    }

    private void WriteFile(string key, T record)
    {
        var path = PathFor(key);
        var temp = path + ".tmp";
        var json = DockerJson.Serialize(record);
        File.WriteAllText(temp, json, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    // Keys are ids and Docker names, which are already file-safe; anything else is hex encoded so
    // the mapping stays reversible and can never escape the store directory.
    private static string Encode(string key)
    {
        if (IsSafe(key))
        {
            return key;
        }

        return EncodedPrefix + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(key));
    }

    private static string? Decode(string fileName)
    {
        if (fileName.Length == 0)
        {
            return null;
        }

        if (!fileName.StartsWith(EncodedPrefix, StringComparison.Ordinal))
        {
            return IsSafe(fileName) ? fileName : null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(fileName[EncodedPrefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsSafe(string key)
    {
        if (key.Length == 0 || key.StartsWith(EncodedPrefix, StringComparison.Ordinal) || key[0] == '.')
        {
            return false;
        }

        foreach (var c in key)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
