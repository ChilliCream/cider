using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>Which of the two shapes an XPC failure took (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.3).</summary>
internal enum XpcErrorClass
{
    /// <summary>The reply itself was a libxpc <c>error</c> object, not a dictionary — the
    /// connection was interrupted or invalid, the client-side timeout raced the reply, or (§1.6)
    /// the route does not exist and the server silently dropped the message, which libxpc reports
    /// as "interrupted" on the sync send. No apiserver route handler ever ran.</summary>
    Transport,

    /// <summary>An ordinary reply dictionary carrying <c>{"code","message"}</c> under
    /// <see cref="XpcMessage.ErrorKey"/> — the route ran and the daemon rejected the call.</summary>
    ApiServer,
}

/// <summary>
/// The single exception type every XPC-level failure talking to <c>com.apple.container.apiserver</c>
/// collapses into, whether the failure was <see cref="XpcErrorClass.Transport"/> or
/// <see cref="XpcErrorClass.ApiServer"/>. <see cref="ToRuntimeException"/> is the one place this
/// crosses the <c>IContainerRuntime</c> seam into the daemon-wide <see cref="RuntimeException"/>
/// contract.
/// </summary>
internal sealed class XpcException : Exception
{
    private XpcException(XpcErrorClass errorClass, string? code, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorClass = errorClass;
        Code = code;
    }

    public XpcErrorClass ErrorClass { get; }

    /// <summary>For <see cref="XpcErrorClass.ApiServer"/>, the apiserver's own
    /// <c>ContainerizationError.Code</c> string (§1.3: <c>notFound</c>, <c>exists</c>, …). For
    /// <see cref="XpcErrorClass.Transport"/>, a synthetic tag naming which transport failure this was.</summary>
    public string? Code { get; }

    /// <summary>The sync send returned the <c>XPC_ERROR_CONNECTION_INTERRUPTED</c> sentinel, or (by
    /// §1.6) an unknown route the server silently dropped — libxpc reports both identically.</summary>
    public static XpcException Interrupted(string message) => new(XpcErrorClass.Transport, "interrupted", message);

    /// <summary>The sync send returned the <c>XPC_ERROR_CONNECTION_INVALID</c> sentinel — the
    /// connection can never be used again (as opposed to "interrupted", which a fresh connection
    /// recovers from).</summary>
    public static XpcException Invalid(string message) => new(XpcErrorClass.Transport, "invalidState", message);

    /// <summary>The client-side per-call budget elapsed before a reply arrived (§1.4 — timeouts are
    /// implemented purely client-side, racing a delay against the reply, exactly as the Swift
    /// client does).</summary>
    public static XpcException Timeout(string message) => new(XpcErrorClass.Transport, "timeout", message);

    /// <summary>The daemon ran the route and rejected the call with <c>{code, message}</c>.</summary>
    public static XpcException ApiServer(string code, string message) => new(XpcErrorClass.ApiServer, code, message);

    /// <summary>The error envelope itself failed to parse as JSON — should not happen against a
    /// well-behaved apiserver, but must not crash the client if it does.</summary>
    public static XpcException Malformed(string rawEnvelope) =>
        new(XpcErrorClass.ApiServer, "malformed", $"apiserver returned an unparsable error envelope: {rawEnvelope}");

    /// <summary>
    /// Maps this XPC-level failure to the daemon-wide <see cref="RuntimeException"/> contract.
    /// Nothing above the <c>IContainerRuntime</c> seam may read exception message text
    /// (<c>src/Cider.Core/Runtime/RuntimeException.cs:26-27</c>) — this is the one place the XPC
    /// codes get translated into <see cref="RuntimeErrorKind"/>/<see cref="RuntimeErrorReason"/>.
    /// </summary>
    public RuntimeException ToRuntimeException(string context)
    {
        var message = string.IsNullOrEmpty(context) ? Message : $"{context}: {Message}";
        var kind = XpcErrorMapper.ToRuntimeErrorKind(this);
        var reason = XpcErrorMapper.ToRuntimeErrorReason(this, kind);
        return new RuntimeException(kind, message, this, reason);
    }
}

/// <summary>
/// Decodes the <c>com.apple.container.xpc.error</c> envelope and classifies both error classes
/// into <see cref="RuntimeErrorKind"/>, mirroring <see cref="Cli.CliErrorMapper"/> for the XPC
/// transport. The code→kind table is the task's binding ruling
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.3's exhaustive
/// <c>ContainerizationError.Code</c> list).
/// </summary>
internal static class XpcErrorMapper
{
    /// <summary>Decodes the raw JSON bytes under <see cref="XpcMessage.ErrorKey"/> — always
    /// UTF-8 JSON, never base64 (§1.2 rule 5) — into an <see cref="XpcException"/>.</summary>
    public static XpcException Decode(byte[] envelope)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            var code = doc.RootElement.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            var message = doc.RootElement.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
            return XpcException.ApiServer(code ?? "unknown", message ?? "apiserver returned an error with no message");
        }
        catch (JsonException)
        {
            return XpcException.Malformed(Encoding.UTF8.GetString(envelope));
        }
    }

    /// <summary>apiserver <c>ContainerizationError.Code</c> → <see cref="RuntimeErrorKind"/>.</summary>
    public static RuntimeErrorKind ToRuntimeErrorKind(XpcException ex)
    {
        if (ex.ErrorClass == XpcErrorClass.Transport)
        {
            return ex.Code == "timeout" ? RuntimeErrorKind.Timeout : RuntimeErrorKind.Unavailable;
        }

        return ex.Code switch
        {
            "notFound" => RuntimeErrorKind.NotFound,
            "exists" => RuntimeErrorKind.Conflict,
            "invalidState" => RuntimeErrorKind.Conflict,
            "invalidArgument" => RuntimeErrorKind.InvalidArgument,
            "unsupported" => RuntimeErrorKind.NotSupported,
            "timeout" => RuntimeErrorKind.Timeout,
            "interrupted" => RuntimeErrorKind.Unavailable,
            // cancelled, unknown, internalError, empty, and anything unrecognised.
            _ => RuntimeErrorKind.Internal,
        };
    }

    /// <summary>The one finer cause the task calls out: an <c>invalidState</c> whose message says
    /// the container is not running becomes <see cref="RuntimeErrorReason.ContainerNotRunning"/>,
    /// still a <see cref="RuntimeErrorKind.Conflict"/>.</summary>
    public static RuntimeErrorReason ToRuntimeErrorReason(XpcException ex, RuntimeErrorKind kind) =>
        kind == RuntimeErrorKind.Conflict &&
        ex.ErrorClass == XpcErrorClass.ApiServer &&
        ex.Code == "invalidState" &&
        ex.Message.Contains("not running", StringComparison.OrdinalIgnoreCase)
            ? RuntimeErrorReason.ContainerNotRunning
            : RuntimeErrorReason.None;
}

/// <summary>
/// The two libxpc singleton "error" objects a connection's event handler (and a sync send's reply)
/// can produce — pointer-identity constants exported by libxpc itself, not reference-counted
/// objects a caller creates or owns. Verified live: <c>NativeLibrary.GetExport</c> on
/// <c>libSystem.B.dylib</c> resolves both (see the task's live probe run); the exact same pattern
/// already used for <c>_NSConcreteGlobalBlock</c> in <see cref="XpcBlock"/>.
/// </summary>
internal static class XpcErrorSentinels
{
    /// <summary><c>XPC_ERROR_CONNECTION_INTERRUPTED</c> — the connection can be used again; a
    /// fresh <c>xpc_connection_t</c> reconnects transparently.</summary>
    public static readonly nint ConnectionInterrupted =
        NativeLibrary.GetExport(NativeLibrary.Load(XpcNative.Lib), "_xpc_error_connection_interrupted");

    /// <summary><c>XPC_ERROR_CONNECTION_INVALID</c> — terminal; the connection was cancelled and
    /// will never deliver another reply.</summary>
    public static readonly nint ConnectionInvalid =
        NativeLibrary.GetExport(NativeLibrary.Load(XpcNative.Lib), "_xpc_error_connection_invalid");
}
