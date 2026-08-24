namespace Cider.Dns;

/// <summary>
/// The result of a resolver deciding it owns a name (as opposed to returning null, meaning
/// "not mine, forward upstream"). Use <see cref="Nxdomain"/> when the name itself is unknown,
/// and <see cref="NoData"/> when the name is known but has nothing for the queried type
/// (e.g. AAAA queried for an A-only name) — the two must not be confused, since NXDOMAIN for
/// a type-mismatch breaks musl/Go dual-stack (A+AAAA) lookups.
/// </summary>
public sealed class DnsAnswer
{
    public IReadOnlyList<DnsRecord> Answers { get; }
    public bool Authoritative { get; }
    public DnsRcode Rcode { get; }

    public DnsAnswer(IReadOnlyList<DnsRecord> answers, bool authoritative = true, DnsRcode rcode = DnsRcode.NoError)
    {
        Answers = answers;
        Authoritative = authoritative;
        Rcode = rcode;
    }

    /// <summary>NXDOMAIN: the name itself does not exist.</summary>
    public static DnsAnswer Nxdomain(bool authoritative = true) =>
        new(Array.Empty<DnsRecord>(), authoritative, DnsRcode.NxDomain);

    /// <summary>NOERROR with zero answers: the name exists but not for the queried type.</summary>
    public static DnsAnswer NoData(bool authoritative = true) =>
        new(Array.Empty<DnsRecord>(), authoritative, DnsRcode.NoError);

    /// <summary>NOERROR with the given answer records.</summary>
    public static DnsAnswer Of(IReadOnlyList<DnsRecord> records, bool authoritative = true) =>
        new(records, authoritative, DnsRcode.NoError);

    /// <summary>NOERROR with the given answer records.</summary>
    public static DnsAnswer Of(params DnsRecord[] records) =>
        new(records, true, DnsRcode.NoError);
}
