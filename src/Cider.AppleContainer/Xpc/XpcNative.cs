using System.Runtime.InteropServices;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Raw libxpc P/Invoke surface for a .NET process on macOS. Every symbol resolves from
/// <see cref="Lib"/>: libxpc and libsystem_blocks are part of the dyld shared cache, so there is
/// no on-disk <c>libSystem.B.dylib</c> to point at, only the shared-cache export — the same path
/// still resolves through <c>dlopen</c>/<see cref="System.Runtime.InteropServices.NativeLibrary"/>.
///
/// This is the exact surface the spike proved (docs/spikes/xpc-probe/XpcProbe/Xpc.cs,
/// docs/spikes/xpc/04-dotnet-xpc-probe-report.md) plus the handful of calls that surface needs to
/// grow into: date get/set (nanoseconds since the Unix epoch — apiserver's own convention, see
/// docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2), fd get/set for <c>containerDial</c>/
/// <c>containerLogs</c>, and the anonymous-listener/endpoint pair the progress channel needs
/// (§5, implemented as a stub here — see <c>XpcListener.cs</c>).
///
/// <c>xpc_dictionary_get_string</c> is declared to return <c>nint</c>, not <c>string</c>: the
/// pointer is owned by the dictionary, and a <c>string</c>-typed return would have the generated
/// marshalling stub <c>free()</c> memory it does not own.
/// </summary>
internal static unsafe partial class XpcNative
{
    public const string Lib = "/usr/lib/libSystem.B.dylib";

    // ---- connection --------------------------------------------------------------------------

    /// <summary><c>xpc_connection_t xpc_connection_create(const char *name, dispatch_queue_t targetq)</c>.
    /// <c>name == NULL</c> makes an anonymous, unadvertised connection — the listener side of the
    /// progress-endpoint pattern (§5).</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_connection_create(string? name, nint targetQueue);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_connection_create_mach_service(string name, nint targetQueue, ulong flags);

    /// <summary>Turns a previously received <c>xpc_endpoint_t</c> back into a connection.</summary>
    [LibraryImport(Lib)]
    public static partial nint xpc_connection_create_from_endpoint(nint endpoint);

    /// <summary><c>void xpc_connection_set_event_handler(xpc_connection_t, xpc_handler_t)</c> —
    /// <c>handlerBlock</c> is an Objective-C block built by <see cref="XpcBlock"/>. Must be called
    /// before <see cref="xpc_connection_activate"/>.</summary>
    [LibraryImport(Lib)]
    public static partial void xpc_connection_set_event_handler(nint connection, nint handlerBlock);

    [LibraryImport(Lib)]
    public static partial void xpc_connection_activate(nint connection);

    [LibraryImport(Lib)]
    public static partial void xpc_connection_cancel(nint connection);

    /// <summary><c>xpc_object_t xpc_connection_send_message_with_reply_sync(xpc_connection_t, xpc_object_t)</c>.
    /// Blocks the calling thread until a reply (or a transport error object) arrives; the caller
    /// owns the returned object and must <see cref="xpc_release"/> it. Does not consume
    /// <c>message</c> — the caller must release that too.</summary>
    [LibraryImport(Lib)]
    public static partial nint xpc_connection_send_message_with_reply_sync(nint connection, nint message);

    // ---- endpoint ------------------------------------------------------------------------------

    /// <summary><c>xpc_object_t xpc_endpoint_create(xpc_connection_t connection)</c> — wraps a
    /// (listener) connection into a value that can be sent to a peer, letting it connect back.</summary>
    [LibraryImport(Lib)]
    public static partial nint xpc_endpoint_create(nint connection);

    // ---- dictionary ----------------------------------------------------------------------------

    [LibraryImport(Lib)]
    public static partial nint xpc_dictionary_create(nint keys, nint values, nuint count);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_value(nint xdict, string key, nint value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_dictionary_get_value(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_string(nint xdict, string key, string value);

    /// <summary>Dictionary-owned memory — see the type's own doc comment.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_dictionary_get_string(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_data(nint xdict, string key, byte* bytes, nuint length);

    /// <summary>Dictionary-owned memory, valid only while <c>xdict</c> is alive.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte* xpc_dictionary_get_data(nint xdict, string key, out nuint length);

    // C `bool` is 1 byte on arm64 -> U1.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_bool(nint xdict, string key, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool xpc_dictionary_get_bool(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_uint64(nint xdict, string key, ulong value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong xpc_dictionary_get_uint64(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_int64(nint xdict, string key, long value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial long xpc_dictionary_get_int64(nint xdict, string key);

    /// <summary>XPC <c>date</c> = Int64 nanoseconds since the Unix epoch — verified live against
    /// com.apple.container.apiserver (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2). Distinct
    /// from the <c>Date</c> fields inside JSON <c>data</c> payloads, which are seconds since
    /// 2001-01-01 (§2.0.2) — that conversion belongs to the JSON layer, not here.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_date(nint xdict, string key, long nanosecondsSinceUnixEpoch);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial long xpc_dictionary_get_date(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_fd(nint xdict, string key, int fd);

    /// <summary>Duplicates the fd the peer put at <paramref name="key"/> into this process; the
    /// dictionary keeps its own copy (there is no plain <c>xpc_dictionary_get_fd</c>).</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int xpc_dictionary_dup_fd(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint xpc_dictionary_get_count(nint xdict);

    // ---- array (fd arrays — containerLogs' two log fds) -----------------------------------------

    [LibraryImport(Lib)]
    public static partial nint xpc_array_create(nint objects, nuint count);

    [LibraryImport(Lib)]
    public static partial void xpc_array_set_value(nint xarray, nuint index, nint value);

    [LibraryImport(Lib)]
    public static partial nint xpc_array_get_value(nint xarray, nuint index);

    [LibraryImport(Lib)]
    public static partial nuint xpc_array_get_count(nint xarray);

    /// <summary>Duplicates the fd at <paramref name="index"/> into this process.</summary>
    [LibraryImport(Lib)]
    public static partial int xpc_array_dup_fd(nint xarray, nuint index);

    // ---- fd ---------------------------------------------------------------------------------

    /// <summary>Wraps a local fd as an <c>xpc_object_t</c> so it can be placed in a dictionary/array
    /// (the send side of <c>containerBootstrap</c>'s stdin/stdout/stderr keys).</summary>
    [LibraryImport(Lib)]
    public static partial nint xpc_fd_create(int fd);

    // ---- objects ------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    public static partial nint xpc_get_type(nint obj);

    [LibraryImport(Lib)]
    public static partial nint xpc_type_get_name(nint type);

    /// <summary><c>xpc_object_t xpc_retain(xpc_object_t)</c> — bumps the reference count and
    /// returns the same object. Used to pin a connection alive across a blocking sync send that the
    /// caller-side timeout logic may abandon while <see cref="XpcClient.Dispose"/> concurrently
    /// releases the client's own reference (see <c>XpcClient.SendSync</c>'s doc comment).</summary>
    [LibraryImport(Lib)]
    public static partial nint xpc_retain(nint obj);

    [LibraryImport(Lib)]
    public static partial void xpc_release(nint obj);

    /// <summary><c>char *xpc_copy_description(xpc_object_t)</c> — malloc'd, caller frees via <see cref="free"/>.</summary>
    [LibraryImport(Lib)]
    public static partial nint xpc_copy_description(nint obj);

    [LibraryImport(Lib)]
    public static partial void free(nint p);
}
