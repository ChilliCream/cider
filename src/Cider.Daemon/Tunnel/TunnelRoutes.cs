using System.Globalization;
using Grpc.Core;

namespace Cider.Daemon.Tunnel;

/// <summary>
/// Endpoint-level guard for the tunnel transport: every BuildKit gRPC service the daemon maps is
/// pinned to exactly one <see cref="TunnelKind"/> (the control-plane leg serves
/// <c>moby.buildkit.v1.Control</c>, the session leg serves callbacks back to the CLI), and a call
/// that arrives on the wrong leg — or not over the tunnel at all — must fail the way a real gRPC
/// server answers an unimplemented method (<c>grpc-status: 12</c>, trailers-only), never with
/// dockerd's <c>{"message":...}</c> envelope, which a gRPC client cannot parse.
/// </summary>
public static class TunnelRoutes
{
    private const string GrpcContentTypePrefix = "application/grpc";
    private const string NotAvailableMessage = "cider: not available on this tunnel";

    /// <summary>True when <paramref name="context"/> arrived over <see cref="TunnelTransport"/> rather than a real socket.</summary>
    public static bool IsTunnelRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ITunnelFeature>() is not null;
    }

    /// <summary>
    /// Gates every endpoint under <paramref name="builder"/> behind an active tunnel connection of
    /// exactly <paramref name="kind"/>. A missing or mismatched tunnel answers <c>application/grpc</c>
    /// requests with a trailers-only <c>Unimplemented</c> response (exactly what grpc-dotnet's own
    /// unknown-method fallback would send) and everything else with a plain 404.
    /// </summary>
    public static TBuilder RequireTunnel<TBuilder>(this TBuilder builder, TunnelKind kind)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var feature = http.Features.Get<ITunnelFeature>();
            if (feature is not null && feature.Kind == kind)
            {
                return await next(context);
            }

            if (IsGrpcRequest(http))
            {
                WriteUnavailable(http.Response);
            }
            else
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            }

            return Results.Empty;
        });

        return builder;
    }

    private static bool IsGrpcRequest(HttpContext context) =>
        context.Request.ContentType?.StartsWith(GrpcContentTypePrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Writes a gRPC trailers-only response: status 200, <c>content-type: application/grpc</c>, and
    /// <c>grpc-status</c>/<c>grpc-message</c> set directly on the (not-yet-started) response headers
    /// — exactly what <c>Grpc.AspNetCore.Server</c>'s own internal error helper does, since a HEADERS
    /// frame carrying both is what the gRPC wire protocol calls "Trailers-Only".
    /// </summary>
    private static void WriteUnavailable(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = GrpcContentTypePrefix;
        response.Headers["grpc-status"] = ((int)StatusCode.Unimplemented).ToString(CultureInfo.InvariantCulture);
        response.Headers["grpc-message"] = NotAvailableMessage;
    }
}
