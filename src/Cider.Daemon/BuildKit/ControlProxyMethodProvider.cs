using Google.Protobuf;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Registers exactly three <c>moby.buildkit.v1.Control</c> methods — <c>Solve</c>, <c>ListWorkers</c>,
/// <c>Session</c> — for <see cref="ControlProxyService"/>, instead of mapping the service through the
/// generated <see cref="Control.BindService(Control.ControlBase)"/>.
/// <para>
/// Binding through the generated binder is deliberately avoided: it maps every <c>Control</c> method,
/// including the ones this proxy does not implement, as explicit routing-Order-0 endpoints answering
/// Unimplemented — which would shadow <see cref="GrpcForwarder.MapGrpcForwarder"/>'s fallback (mapped
/// at the lowest priority) for every method this proxy actually wants to pass through untouched
/// (<c>Status</c>, <c>Info</c>, <c>DiskUsage</c>, <c>Prune</c>, <c>ListenBuildHistory</c>,
/// <c>UpdateBuildHistory</c>, <c>LLBBridge/*</c>, <c>Content/*</c>, <c>TraceService/Export</c>).
/// </para>
/// <para>
/// <see cref="ControlProxyService"/> is therefore a plain class — it does not derive from
/// <see cref="Control.ControlBase"/>, so grpc-dotnet's own <see cref="Control"/>-bound service
/// discovery finds nothing to bind and silently registers zero methods for it; this provider is the
/// only thing that adds any. The three <see cref="Method{TRequest,TResponse}"/> descriptors below are
/// built by hand (the generated ones in <c>ControlGrpc.cs</c> are private <c>static</c> fields on
/// <see cref="Control"/>) but describe the exact same wire shape — same service name, same method
/// name, same protobuf marshalling — so a client speaking real <c>moby.buildkit.v1.Control</c> cannot
/// tell the difference.
/// </para>
/// </summary>
public sealed class ControlProxyMethodProvider : IServiceMethodProvider<ControlProxyService>
{
    /// <inheritdoc />
    public void OnServiceMethodDiscovery(ServiceMethodProviderContext<ControlProxyService> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddUnaryMethod(
            ControlProxyMethods.Solve,
            Array.Empty<object>(),
            (service, request, ctx) => service.Solve(request, ctx));

        context.AddUnaryMethod(
            ControlProxyMethods.ListWorkers,
            Array.Empty<object>(),
            (service, request, ctx) => service.ListWorkers(request, ctx));

        context.AddDuplexStreamingMethod(
            ControlProxyMethods.Session,
            Array.Empty<object>(),
            (service, requestStream, responseStream, ctx) => service.Session(requestStream, responseStream, ctx));
    }
}

/// <summary>
/// Hand-built <see cref="Method{TRequest,TResponse}"/> descriptors matching
/// <c>moby.buildkit.v1.Control</c>'s wire shape for the three methods <see cref="ControlProxyService"/>
/// implements. See <see cref="ControlProxyMethodProvider"/> for why these are not reused from the
/// generated <c>ControlGrpc.cs</c> (they are private there).
/// </summary>
internal static class ControlProxyMethods
{
    private const string ServiceName = "moby.buildkit.v1.Control";

    public static readonly Method<SolveRequest, SolveResponse> Solve = new(
        MethodType.Unary, ServiceName, "Solve",
        CreateMarshaller(SolveRequest.Parser), CreateMarshaller(SolveResponse.Parser));

    public static readonly Method<ListWorkersRequest, ListWorkersResponse> ListWorkers = new(
        MethodType.Unary, ServiceName, "ListWorkers",
        CreateMarshaller(ListWorkersRequest.Parser), CreateMarshaller(ListWorkersResponse.Parser));

    public static readonly Method<BytesMessage, BytesMessage> Session = new(
        MethodType.DuplexStreaming, ServiceName, "Session",
        CreateMarshaller(BytesMessage.Parser), CreateMarshaller(BytesMessage.Parser));

    private static Marshaller<T> CreateMarshaller<T>(MessageParser<T> parser) where T : IMessage<T> =>
        Marshallers.Create<T>(message => message.ToByteArray(), data => parser.ParseFrom(data));
}
