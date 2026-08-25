using System.Text;
using Cider.AppleContainer.Xpc;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// The envelope decode and the code→kind mapping table — the task's binding ruling, taken from
/// docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.3's exhaustive
/// <c>ContainerizationError.Code</c> list (<c>cancelled, unknown, invalidArgument, timeout,
/// notFound, exists, unsupported, internalError, invalidState, interrupted, empty</c>) and the
/// task description's explicit mapping.
/// </summary>
public class XpcErrorMapperTests
{
    private static byte[] Envelope(string code, string message) =>
        Encoding.UTF8.GetBytes($$"""{"code":"{{code}}","message":"{{message}}"}""");

    [Fact]
    public void Decode_reads_code_and_message()
    {
        var ex = XpcErrorMapper.Decode(Envelope("notFound", "container not found: nope"));

        Assert.Equal(XpcErrorClass.ApiServer, ex.ErrorClass);
        Assert.Equal("notFound", ex.Code);
        Assert.Equal("container not found: nope", ex.Message);
    }

    [Fact]
    public void Decode_survives_malformed_JSON_without_throwing()
    {
        var ex = XpcErrorMapper.Decode(Encoding.UTF8.GetBytes("not json at all"));

        Assert.Equal(XpcErrorClass.ApiServer, ex.ErrorClass);
        Assert.Equal("malformed", ex.Code);
        Assert.Contains("not json at all", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_defaults_a_missing_code_or_message()
    {
        var ex = XpcErrorMapper.Decode(Encoding.UTF8.GetBytes("{}"));

        Assert.Equal("unknown", ex.Code);
        Assert.False(string.IsNullOrEmpty(ex.Message));
    }

    [Theory]
    [InlineData("notFound", RuntimeErrorKind.NotFound)]
    [InlineData("exists", RuntimeErrorKind.Conflict)]
    [InlineData("invalidState", RuntimeErrorKind.Conflict)]
    [InlineData("invalidArgument", RuntimeErrorKind.InvalidArgument)]
    [InlineData("unsupported", RuntimeErrorKind.NotSupported)]
    [InlineData("timeout", RuntimeErrorKind.Timeout)]
    [InlineData("interrupted", RuntimeErrorKind.Unavailable)]
    [InlineData("cancelled", RuntimeErrorKind.Internal)]
    [InlineData("unknown", RuntimeErrorKind.Internal)]
    [InlineData("internalError", RuntimeErrorKind.Internal)]
    [InlineData("empty", RuntimeErrorKind.Internal)]
    public void ApiServer_code_maps_to_the_documented_kind(string code, RuntimeErrorKind expected)
    {
        var ex = XpcException.ApiServer(code, "message");
        Assert.Equal(expected, XpcErrorMapper.ToRuntimeErrorKind(ex));
    }

    [Fact]
    public void InvalidState_with_not_running_in_the_message_is_ContainerNotRunning()
    {
        var ex = XpcException.ApiServer("invalidState", "container adtest1 is not running");

        var kind = XpcErrorMapper.ToRuntimeErrorKind(ex);
        var reason = XpcErrorMapper.ToRuntimeErrorReason(ex, kind);

        Assert.Equal(RuntimeErrorKind.Conflict, kind);
        Assert.Equal(RuntimeErrorReason.ContainerNotRunning, reason);
    }

    [Fact]
    public void InvalidState_without_not_running_has_no_reason()
    {
        var ex = XpcException.ApiServer("invalidState", "container already has a network attached");

        var kind = XpcErrorMapper.ToRuntimeErrorKind(ex);
        var reason = XpcErrorMapper.ToRuntimeErrorReason(ex, kind);

        Assert.Equal(RuntimeErrorKind.Conflict, kind);
        Assert.Equal(RuntimeErrorReason.None, reason);
    }

    [Fact]
    public void Exists_conflict_is_never_ContainerNotRunning_even_if_the_text_matches()
    {
        // Only invalidState carries the reason — `exists` is a different failure entirely.
        var ex = XpcException.ApiServer("exists", "container adtest1 is not running is a weird name");
        var kind = XpcErrorMapper.ToRuntimeErrorKind(ex);
        Assert.Equal(RuntimeErrorReason.None, XpcErrorMapper.ToRuntimeErrorReason(ex, kind));
    }

    [Fact]
    public void Interrupted_transport_error_maps_to_Unavailable()
    {
        var ex = XpcException.Interrupted("Connection interrupted");
        Assert.Equal(RuntimeErrorKind.Unavailable, XpcErrorMapper.ToRuntimeErrorKind(ex));
    }

    [Fact]
    public void Invalid_transport_error_maps_to_Unavailable()
    {
        var ex = XpcException.Invalid("Connection invalid");
        Assert.Equal(RuntimeErrorKind.Unavailable, XpcErrorMapper.ToRuntimeErrorKind(ex));
    }

    [Fact]
    public void Client_side_timeout_maps_to_Timeout()
    {
        var ex = XpcException.Timeout("XPC timeout for request to com.apple.container.apiserver/containerList");
        Assert.Equal(RuntimeErrorKind.Timeout, XpcErrorMapper.ToRuntimeErrorKind(ex));
    }

    [Fact]
    public void ToRuntimeException_prefixes_context_and_never_leaks_the_XPC_code_through_the_seam()
    {
        var ex = XpcException.ApiServer("notFound", "container not found: nope");

        var runtimeEx = ex.ToRuntimeException("delete container nope");

        Assert.Equal(RuntimeErrorKind.NotFound, runtimeEx.Kind);
        Assert.Equal("delete container nope: container not found: nope", runtimeEx.Message);
        Assert.Same(ex, runtimeEx.InnerException);
    }

    [Fact]
    public void ToRuntimeException_with_no_context_uses_the_bare_message()
    {
        var ex = XpcException.ApiServer("notFound", "container not found: nope");
        var runtimeEx = ex.ToRuntimeException(string.Empty);
        Assert.Equal("container not found: nope", runtimeEx.Message);
    }
}
