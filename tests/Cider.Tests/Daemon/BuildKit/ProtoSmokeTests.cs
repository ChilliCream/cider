using Google.Protobuf;
using Moby.Buildkit.V1;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// Smoke tests for the vendored BuildKit protos (see ../../../../../protos and
/// src/Cider.Daemon/BuildKit/Protos/README.md): proves the <c>Grpc.Tools</c> codegen produced
/// message types that round-trip through the wire, not just that they compile.
/// </summary>
public sealed class ProtoSmokeTests
{
    [Fact]
    public void SolveRequest_with_one_exporter_round_trips_through_the_wire()
    {
        var request = new SolveRequest
        {
            Ref = "smoke-test-ref",
            Exporters =
            {
                new Exporter
                {
                    Type = "moby",
                    Attrs = { ["name"] = "a:1" },
                },
            },
        };

        var bytes = request.ToByteArray();
        var parsed = SolveRequest.Parser.ParseFrom(bytes);

        Assert.Equal(request.Ref, parsed.Ref);
        Assert.Equal(request, parsed);
        var exporter = Assert.Single(parsed.Exporters);
        Assert.Equal("moby", exporter.Type);
        Assert.Equal("a:1", exporter.Attrs["name"]);
    }
}
