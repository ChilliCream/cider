namespace Cider.Daemon.Tunnel;

/// <summary>
/// Which leg of the BuildKit tunnel a connection is: the control-plane connection BuildKit dials
/// through the hijacked <c>POST /grpc</c> (<c>moby.buildkit.v1.Control</c> and friends), or a CLI
/// session connection dialed through <c>POST /session</c> (filesync, auth callbacks back to the
/// client that started the build).
/// </summary>
public enum TunnelKind
{
    /// <summary>The BuildKit control-plane connection.</summary>
    Control,

    /// <summary>A CLI session connection.</summary>
    Session,
}

/// <summary>
/// Marks a connection (and every <see cref="Microsoft.AspNetCore.Http.HttpContext"/> Kestrel builds
/// on top of it) as arriving over <see cref="TunnelTransport"/> rather than a real socket. Set on
/// the connection's own <c>Features</c> collection by <see cref="TunnelConnectionContext"/>, so it
/// surfaces on <c>HttpContext.Features</c> too — Kestrel falls back to the connection's feature
/// collection for anything the HTTP layer does not override.
/// </summary>
public interface ITunnelFeature
{
    /// <summary>Which leg of the tunnel this connection is.</summary>
    TunnelKind Kind { get; }

    /// <summary>The CLI session id this connection belongs to, when one is known.</summary>
    string? SessionId { get; }

    /// <summary>Arbitrary metadata carried over from the hijack that established this connection (e.g. auth headers).</summary>
    IDictionary<string, string[]> Meta { get; }
}
