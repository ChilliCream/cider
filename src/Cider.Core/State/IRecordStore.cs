namespace Cider.Core.State;

/// <summary>
/// A small keyed record store. Implementations persist synchronously (small JSON) and are
/// thread-safe. See <see cref="JsonFileStore{T}"/> (disk-backed) and <see cref="InMemoryRecordStore{T}"/> (tests).
/// </summary>
public interface IRecordStore<T> where T : class
{
    IReadOnlyCollection<T> GetAll();

    T? Get(string key);

    /// <summary>Persists <paramref name="record"/> under <paramref name="key"/>; overwrites any existing entry.</summary>
    void Upsert(string key, T record);

    bool Delete(string key);

    bool TryGet(string key, out T? record);
}
