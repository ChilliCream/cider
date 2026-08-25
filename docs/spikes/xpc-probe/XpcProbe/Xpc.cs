// Raw libxpc interop for a .NET 10 process on macOS.
//
// Everything is exported through /usr/lib/libSystem.B.dylib (libxpc + libsystem_blocks are
// part of the shared cache; dlopen of libSystem.B.dylib resolves them).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace XpcProbe;

internal static unsafe partial class Xpc
{
    public const string Lib = "/usr/lib/libSystem.B.dylib";

    // ---- connection ------------------------------------------------------------------------
    // xpc_connection_t xpc_connection_create_mach_service(const char *name, dispatch_queue_t targetq, uint64_t flags);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_connection_create_mach_service(string name, nint targetq, ulong flags);

    // void xpc_connection_set_event_handler(xpc_connection_t connection, xpc_handler_t handler);  // handler is an ObjC block
    [LibraryImport(Lib)]
    public static partial void xpc_connection_set_event_handler(nint connection, nint handlerBlock);

    [LibraryImport(Lib)]
    public static partial void xpc_connection_activate(nint connection);

    [LibraryImport(Lib)]
    public static partial void xpc_connection_resume(nint connection);

    [LibraryImport(Lib)]
    public static partial void xpc_connection_cancel(nint connection);

    [LibraryImport(Lib)]
    public static partial int xpc_connection_get_pid(nint connection);

    // xpc_object_t xpc_connection_send_message_with_reply_sync(xpc_connection_t connection, xpc_object_t message);
    [LibraryImport(Lib)]
    public static partial nint xpc_connection_send_message_with_reply_sync(nint connection, nint message);

    // ---- dictionary ------------------------------------------------------------------------
    // xpc_object_t xpc_dictionary_create(const char *const *keys, const xpc_object_t *values, size_t count);
    [LibraryImport(Lib)]
    public static partial nint xpc_dictionary_create(nint keys, nint values, nuint count);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_string(nint xdict, string key, string value);

    // void xpc_dictionary_set_data(xpc_object_t xdict, const char *key, const void *bytes, size_t length);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_data(nint xdict, string key, byte* bytes, nuint length);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_uint64(nint xdict, string key, ulong value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_int64(nint xdict, string key, long value);

    // C `bool` is 1 byte on arm64 -> U1.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void xpc_dictionary_set_bool(nint xdict, string key, [MarshalAs(UnmanagedType.U1)] bool value);

    // const char *xpc_dictionary_get_string(...)  -- pointer is OWNED BY THE DICTIONARY.
    // Must be declared as nint: a `string` return would make the generated stub free() the buffer.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_dictionary_get_string(nint xdict, string key);

    // const void *xpc_dictionary_get_data(xpc_object_t xdict, const char *key, size_t *length);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte* xpc_dictionary_get_data(nint xdict, string key, out nuint length);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong xpc_dictionary_get_uint64(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial long xpc_dictionary_get_int64(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool xpc_dictionary_get_bool(nint xdict, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint xpc_dictionary_get_value(nint xdict, string key);

    [LibraryImport(Lib)]
    public static partial nuint xpc_dictionary_get_count(nint xdict);

    // bool xpc_dictionary_apply(xpc_object_t xdict, xpc_dictionary_applier_t applier);  // applier is an ObjC block
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool xpc_dictionary_apply(nint xdict, nint applierBlock);

    // ---- objects ---------------------------------------------------------------------------
    [LibraryImport(Lib)]
    public static partial nint xpc_get_type(nint obj);

    [LibraryImport(Lib)]
    public static partial nint xpc_type_get_name(nint type);

    [LibraryImport(Lib)]
    public static partial void xpc_release(nint obj);

    // char *xpc_copy_description(xpc_object_t) -- malloc'd, caller frees.
    [LibraryImport(Lib)]
    public static partial nint xpc_copy_description(nint obj);

    [LibraryImport(Lib)]
    public static partial void free(nint p);

    // ---- helpers ---------------------------------------------------------------------------
    public static string TypeName(nint obj) => Marshal.PtrToStringUTF8(xpc_type_get_name(xpc_get_type(obj))) ?? "?";

    public static string Describe(nint obj)
    {
        nint p = xpc_copy_description(obj);
        try { return Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { free(p); }
    }

    public static string? GetString(nint dict, string key) => Marshal.PtrToStringUTF8(xpc_dictionary_get_string(dict, key));

    public static byte[]? GetData(nint dict, string key)
    {
        byte* p = xpc_dictionary_get_data(dict, key, out nuint len);
        if (p == null) return null;
        return new ReadOnlySpan<byte>(p, checked((int)len)).ToArray();
    }

    public static void SetData(nint dict, string key, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes) xpc_dictionary_set_data(dict, key, p, (nuint)bytes.Length);
    }

    public static void SetJson(nint dict, string key, string json) => SetData(dict, key, Encoding.UTF8.GetBytes(json));
}

/// <summary>
/// Hand-built Objective-C block literal (ABI from clang's Block-ABI-Apple.rst). We only need
/// "global" blocks: no captured state, immortal, so Block_copy/Block_release are no-ops on them.
/// </summary>
internal static unsafe class ObjCBlock
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Descriptor
    {
        public nuint reserved;   // unsigned long
        public nuint size;       // sizeof(Literal)
        // copy/dispose helpers only present when BLOCK_HAS_COPY_DISPOSE; signature only when BLOCK_HAS_SIGNATURE
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Literal
    {
        public nint isa;              // &_NSConcreteGlobalBlock
        public int flags;             // BLOCK_IS_GLOBAL
        public int reserved;
        public nint invoke;           // void (*)(Literal *self, ...args)
        public Descriptor* descriptor;
    }

    public const int BLOCK_IS_GLOBAL = 1 << 28;

    private static readonly nint s_NSConcreteGlobalBlock =
        NativeLibrary.GetExport(NativeLibrary.Load(Xpc.Lib), "_NSConcreteGlobalBlock");

    /// <summary>Allocates an immortal global block whose invoke is an [UnmanagedCallersOnly] function pointer.</summary>
    public static nint CreateGlobal(nint invoke)
    {
        var d = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
        d->reserved = 0;
        d->size = (nuint)sizeof(Literal);

        var b = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));
        b->isa = s_NSConcreteGlobalBlock;
        b->flags = BLOCK_IS_GLOBAL;
        b->reserved = 0;
        b->invoke = invoke;
        b->descriptor = d;
        return (nint)b;
    }

    public static string DescribeLayout() =>
        $"Literal size={sizeof(Literal)} (isa@0,flags@8,reserved@12,invoke@16,descriptor@24), Descriptor size={sizeof(Descriptor)}, " +
        $"_NSConcreteGlobalBlock=0x{s_NSConcreteGlobalBlock:x}";
}

internal sealed class XpcTransportException(string message) : Exception("XPC transport error: " + message);

internal sealed class ApiServerException(string code, string message, string raw)
    : Exception($"apiserver error [{code}]: {message}")
{
    public string Code { get; } = code;
    public string RawJson { get; } = raw;
}

/// <summary>A client connection to com.apple.container.apiserver mirroring Sources/ContainerXPC/XPCClient.swift.</summary>
internal sealed unsafe class ApiServerClient : IDisposable
{
    public const string Service = "com.apple.container.apiserver";
    public const string RouteKey = "com.apple.container.xpc.route";   // XPCMessage.routeKey
    public const string ErrorKey = "com.apple.container.xpc.error";   // XPCMessage.errorKey

    private static readonly nint s_eventBlock =
        ObjCBlock.CreateGlobal((nint)(delegate* unmanaged<nint, nint, void>)&OnConnectionEvent);
    private static readonly nint s_applyBlock =
        ObjCBlock.CreateGlobal((nint)(delegate* unmanaged<nint, nint, nint, byte>)&OnApply);

    public static int EventCount;
    public static string? LastEvent;

    [ThreadStatic] private static List<(string key, string type)>? t_applyResults;

    // xpc_handler_t: void (^)(xpc_object_t). Invoked on a libdispatch worker thread.
    [UnmanagedCallersOnly]
    private static void OnConnectionEvent(nint block, nint obj)
    {
        try
        {
            Interlocked.Increment(ref EventCount);
            LastEvent = $"{Xpc.TypeName(obj)}: {Xpc.Describe(obj)}";
            if (Environment.GetEnvironmentVariable("XPCPROBE_VERBOSE") == "1")
                Console.Error.WriteLine($"[xpc event on tid {Environment.CurrentManagedThreadId}] {LastEvent}");
        }
        catch { /* never let an exception escape a reverse P/Invoke */ }
    }

    // xpc_dictionary_applier_t: bool (^)(const char *key, xpc_object_t value)
    [UnmanagedCallersOnly]
    private static byte OnApply(nint block, nint key, nint value)
    {
        try { t_applyResults?.Add((Marshal.PtrToStringUTF8(key) ?? "", Xpc.TypeName(value))); }
        catch { }
        return 1; // keep iterating
    }

    public static List<(string key, string type)> Keys(nint dict)
    {
        var list = new List<(string, string)>();
        t_applyResults = list;
        try { Xpc.xpc_dictionary_apply(dict, s_applyBlock); }
        finally { t_applyResults = null; }
        return list;
    }

    private nint _conn;

    public ApiServerClient(bool useActivate = true)
    {
        _conn = Xpc.xpc_connection_create_mach_service(Service, 0, 0);
        if (_conn == 0) throw new InvalidOperationException("xpc_connection_create_mach_service returned NULL");
        Xpc.xpc_connection_set_event_handler(_conn, s_eventBlock);
        if (useActivate) Xpc.xpc_connection_activate(_conn);
        else Xpc.xpc_connection_resume(_conn);
    }

    public int RemotePid => Xpc.xpc_connection_get_pid(_conn);

    public static nint NewMessage(string route)
    {
        nint m = Xpc.xpc_dictionary_create(0, 0, 0);
        Xpc.xpc_dictionary_set_string(m, RouteKey, route);
        return m;
    }

    /// <summary>Sends (and releases) the message; returns the reply dictionary, which the caller must xpc_release.</summary>
    public nint Send(nint message)
    {
        nint reply;
        try { reply = Xpc.xpc_connection_send_message_with_reply_sync(_conn, message); }
        finally { Xpc.xpc_release(message); }

        string type = Xpc.TypeName(reply);
        if (type == "error")
        {
            // XPC_ERROR_KEY_DESCRIPTION == "XPCErrorDescription"
            string desc = Xpc.GetString(reply, "XPCErrorDescription") ?? Xpc.Describe(reply);
            Xpc.xpc_release(reply);
            throw new XpcTransportException(desc);
        }
        if (type != "dictionary")
        {
            string d = Xpc.Describe(reply);
            Xpc.xpc_release(reply);
            throw new XpcTransportException($"unexpected reply type {type}: {d}");
        }

        byte[]? err = Xpc.GetData(reply, ErrorKey);
        if (err != null)
        {
            Xpc.xpc_release(reply);
            string raw = Encoding.UTF8.GetString(err);
            try
            {
                using var doc = JsonDocument.Parse(err);
                throw new ApiServerException(
                    doc.RootElement.GetProperty("code").GetString() ?? "?",
                    doc.RootElement.GetProperty("message").GetString() ?? "?",
                    raw);
            }
            catch (JsonException)
            {
                throw new ApiServerException("malformed", raw, raw);
            }
        }
        return reply;
    }

    public void Dispose()
    {
        if (_conn != 0)
        {
            Xpc.xpc_connection_cancel(_conn);
            Xpc.xpc_release(_conn);
            _conn = 0;
        }
    }
}
