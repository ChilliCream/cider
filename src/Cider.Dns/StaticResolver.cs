using System.Net;
using System.Net.Sockets;

namespace Cider.Dns;

/// <summary>
/// An <see cref="IDnsResolver"/> backed by an exact-match name → address table (e.g. container
/// names, `*.docker.internal`-style host aliases). Lookups are case-insensitive; a trailing dot
/// is ignored. A name present in the table but with no address of the queried family answers
/// NOERROR/0 answers (<see cref="DnsAnswer.NoData"/>), never NXDOMAIN — required for musl/Go
/// dual-stack (A+AAAA) lookups to succeed. A name absent from the table returns null ("not
/// mine"), so the server forwards it upstream instead of treating it as NXDOMAIN.
/// </summary>
public sealed class StaticResolver : IDnsResolver
{
    private readonly Dictionary<string, List<IPAddress>> _entries = new(StringComparer.OrdinalIgnoreCase);

    public StaticResolver()
    {
    }

    public StaticResolver(IEnumerable<KeyValuePair<string, IReadOnlyList<IPAddress>>> entries)
    {
        foreach (var kvp in entries) Add(kvp.Key, kvp.Value);
    }

    public StaticResolver Add(string name, params IPAddress[] addresses) => Add(name, (IReadOnlyList<IPAddress>)addresses);

    public StaticResolver Add(string name, IReadOnlyList<IPAddress> addresses)
    {
        var key = Normalize(name);
        if (!_entries.TryGetValue(key, out var list))
        {
            list = new List<IPAddress>();
            _entries[key] = list;
        }
        list.AddRange(addresses);
        return this;
    }

    public bool Remove(string name) => _entries.Remove(Normalize(name));

    public ValueTask<DnsAnswer?> ResolveAsync(DnsQuestion question, IPEndPoint client, CancellationToken ct)
    {
        var key = Normalize(question.Name);
        if (!_entries.TryGetValue(key, out var addresses))
            return new ValueTask<DnsAnswer?>((DnsAnswer?)null); // not mine -> caller forwards upstream

        DnsAnswer answer = question.Type switch
        {
            DnsRecordType.A => BuildAnswer(question.Name, addresses, AddressFamily.InterNetwork,
                a => DnsRecord.CreateA(question.Name, a)),
            DnsRecordType.Aaaa => BuildAnswer(question.Name, addresses, AddressFamily.InterNetworkV6,
                a => DnsRecord.CreateAaaa(question.Name, a)),
            _ => DnsAnswer.NoData(),
        };

        return new ValueTask<DnsAnswer?>(answer);
    }

    private static DnsAnswer BuildAnswer(string name, List<IPAddress> addresses, AddressFamily family, Func<IPAddress, DnsRecord> makeRecord)
    {
        var matching = addresses.Where(a => a.AddressFamily == family).Select(makeRecord).ToList();
        // Known name, but nothing of this family: NOERROR/0 answers, not NXDOMAIN.
        return matching.Count == 0 ? DnsAnswer.NoData() : DnsAnswer.Of(matching);
    }

    private static string Normalize(string name) => name.EndsWith('.') ? name[..^1] : name;
}
