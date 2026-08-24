using System.Net;

namespace Cider.Dns;

/// <summary>An <see cref="IDnsResolver"/> that dispatches to a supplied delegate. Handy for tests and small examples.</summary>
public sealed class DelegateResolver : IDnsResolver
{
    private readonly Func<DnsQuestion, IPEndPoint, CancellationToken, ValueTask<DnsAnswer?>> _resolve;

    public DelegateResolver(Func<DnsQuestion, IPEndPoint, CancellationToken, ValueTask<DnsAnswer?>> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>Convenience constructor for synchronous resolvers.</summary>
    public DelegateResolver(Func<DnsQuestion, IPEndPoint, DnsAnswer?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = (q, c, _) => new ValueTask<DnsAnswer?>(resolve(q, c));
    }

    public ValueTask<DnsAnswer?> ResolveAsync(DnsQuestion question, IPEndPoint client, CancellationToken ct) =>
        _resolve(question, client, ct);
}
