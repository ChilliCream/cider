using System.Net;

namespace Cider.Dns;

/// <summary>
/// Answers (or declines) a single DNS question. Returning null means "not mine" — the server
/// forwards the raw query to upstream resolvers. Returning a <see cref="DnsAnswer"/> means this
/// resolver is authoritative for the name; use <see cref="DnsAnswer.Nxdomain"/> for an unknown
/// name and <see cref="DnsAnswer.NoData"/> for a known name with no records of the queried type.
/// </summary>
public interface IDnsResolver
{
    ValueTask<DnsAnswer?> ResolveAsync(DnsQuestion question, IPEndPoint client, CancellationToken ct);
}
