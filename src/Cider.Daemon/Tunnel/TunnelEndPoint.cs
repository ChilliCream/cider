using System.Net;

namespace Cider.Daemon.Tunnel;

/// <summary>
/// Marker <see cref="EndPoint"/> Kestrel binds to for the in-process tunnel (see
/// <see cref="TunnelTransport"/>): no socket exists behind it — connections arrive only through
/// <see cref="TunnelTransport.ServeAsync(Stream, TunnelKind, string?, IDictionary{string, string[]}?, CancellationToken)"/>
/// or its <see cref="Microsoft.AspNetCore.Connections.ConnectionContext"/> overload.
/// </summary>
public sealed class TunnelEndPoint : EndPoint
{
    /// <inheritdoc />
    public override string ToString() => "cider-tunnel";
}
