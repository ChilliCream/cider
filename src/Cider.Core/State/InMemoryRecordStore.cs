namespace Cider.Core.State;

/// <summary>An <see cref="IRecordStore{T}"/> backed by a plain in-memory dictionary — used by unit tests (owner: core-resources).</summary>
public sealed class InMemoryRecordStore<T> : IRecordStore<T> where T : class
{
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyCollection<T> GetAll()
    {
        lock (_gate)
        {
            return _items.Values.ToList();
        }
    }

    public T? Get(string key)
    {
        lock (_gate)
        {
            return _items.TryGetValue(key, out var record) ? record : null;
        }
    }

    public void Upsert(string key, T record)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _items[key] = record;
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            return _items.Remove(key);
        }
    }

    public bool TryGet(string key, out T? record)
    {
        lock (_gate)
        {
            return _items.TryGetValue(key, out record);
        }
    }
}
