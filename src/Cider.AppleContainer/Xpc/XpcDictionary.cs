using System.Runtime.InteropServices;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// A flat XPC dictionary — the only message shape apiserver's <c>ContainerXPC</c> protocol uses,
/// on both the request and the reply side
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2, §1.3). Typed accessors here are thin,
/// direct wrappers over the matching <c>xpc_dictionary_*</c> pair; the route key and higher-level
/// framing live in <see cref="XpcMessage"/>, which wraps one of these.
/// </summary>
internal sealed class XpcDictionary : XpcObject
{
    /// <summary>Creates a new, empty dictionary — the start of an outbound request.</summary>
    public XpcDictionary()
        : base(XpcNative.xpc_dictionary_create(0, 0, 0))
    {
    }

    /// <summary>Wraps a dictionary this instance now owns — typically a reply.</summary>
    public XpcDictionary(nint handle, bool ownsHandle = true)
        : base(handle, ownsHandle)
    {
    }

    public void SetString(string key, string value) => Use(h => XpcNative.xpc_dictionary_set_string(h, key, value));

    /// <summary><c>null</c> when the key is absent.</summary>
    public string? GetString(string key) => Use(h => Marshal.PtrToStringUTF8(XpcNative.xpc_dictionary_get_string(h, key)));

    public unsafe void SetData(string key, ReadOnlySpan<byte> value)
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            var handle = DangerousGetHandle();
            fixed (byte* p = value)
            {
                XpcNative.xpc_dictionary_set_data(handle, key, p, (nuint)value.Length);
            }
        }
        finally
        {
            if (added)
            {
                DangerousRelease();
            }
        }
    }

    /// <summary><c>null</c> when the key is absent. The bytes are copied out immediately, so the
    /// result stays valid after this dictionary is released — unlike the underlying
    /// <c>xpc_dictionary_get_data</c> pointer, which the dictionary owns.</summary>
    public unsafe byte[]? GetData(string key) => Use<byte[]?>(h =>
    {
        var p = XpcNative.xpc_dictionary_get_data(h, key, out var length);
        if (p != null)
        {
            return new ReadOnlySpan<byte>(p, checked((int)length)).ToArray();
        }

        // A NULL pointer is ambiguous on its own: libxpc returns it both for "key absent" and for
        // "key present, a zero-length data value" (there is no buffer to point a 0-byte allocation
        // at) — confirmed live while building this client. Disambiguate via the key's presence
        // rather than mistake real empty data for absence.
        return XpcNative.xpc_dictionary_get_value(h, key) != 0 ? [] : null;
    });

    public void SetBool(string key, bool value) => Use(h => XpcNative.xpc_dictionary_set_bool(h, key, value));

    public bool GetBool(string key) => Use(h => XpcNative.xpc_dictionary_get_bool(h, key));

    public void SetUInt64(string key, ulong value) => Use(h => XpcNative.xpc_dictionary_set_uint64(h, key, value));

    public ulong GetUInt64(string key) => Use(h => XpcNative.xpc_dictionary_get_uint64(h, key));

    public void SetInt64(string key, long value) => Use(h => XpcNative.xpc_dictionary_set_int64(h, key, value));

    public long GetInt64(string key) => Use(h => XpcNative.xpc_dictionary_get_int64(h, key));

    /// <summary>Sets an XPC <c>date</c> value — nanoseconds since the Unix epoch, libxpc's own
    /// convention (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2), not to be confused with the
    /// seconds-since-2001 <c>Date</c> encoding inside JSON payloads (§2.0.2). A .NET tick is
    /// exactly 100 ns, so the conversion is exact.</summary>
    public void SetDate(string key, DateTimeOffset value)
    {
        var ticksSinceEpoch = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        Use(h => XpcNative.xpc_dictionary_set_date(h, key, ticksSinceEpoch * 100));
    }

    public DateTimeOffset GetDate(string key)
    {
        var nanosecondsSinceEpoch = Use(h => XpcNative.xpc_dictionary_get_date(h, key));
        var ticks = DateTimeOffset.UnixEpoch.UtcTicks + nanosecondsSinceEpoch / 100;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public void SetFd(string key, int fd) => Use(h => XpcNative.xpc_dictionary_set_fd(h, key, fd));

    /// <summary>Duplicates the fd the peer placed at <paramref name="key"/> into this process.</summary>
    public int DupFd(string key) => Use(h => XpcNative.xpc_dictionary_dup_fd(h, key));

    /// <summary><c>&amp;_xpc_type_array</c> — the pointer-identity singleton <c>xpc_get_type</c>
    /// returns for an array object, exported by libxpc the same way as the two error sentinels in
    /// <see cref="XpcErrorSentinels"/>. <see cref="DupArrayFd"/> compares against this before ever
    /// calling an <c>xpc_array_*</c> accessor (cider-ede.34).</summary>
    private static readonly nint ArrayType =
        NativeLibrary.GetExport(NativeLibrary.Load(XpcNative.Lib), "_xpc_type_array");

    /// <summary><c>&amp;_xpc_type_fd</c> — same pattern as <see cref="ArrayType"/>, for the element
    /// <see cref="DupArrayFd"/> is about to dup. Verified live (task's probe run): an in-range array
    /// element that is not itself an fd is <b>also</b> libxpc API misuse for
    /// <c>xpc_array_dup_fd</c> — unlike <c>xpc_dictionary_dup_fd</c>, which is documented to return
    /// <c>-1</c> for a wrong-type value, the array accessor aborts. So the element's type must be
    /// checked here too, before the call, not inferred from a negative return afterwards.</summary>
    private static readonly nint FdType =
        NativeLibrary.GetExport(NativeLibrary.Load(XpcNative.Lib), "_xpc_type_fd");

    /// <summary>Duplicates the fd at <paramref name="index"/> of the xpc array stored at
    /// <paramref name="key"/> into this process — <c>containerLogs</c>'s two-fd <c>logs</c> array
    /// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.10: <c>xpc_array_dup_fd(logs, 0)</c> for
    /// <c>stdio.log</c>, <c>(logs, 1)</c> for <c>vminitd.log</c>). The array is dictionary-owned —
    /// <see cref="Use{T}"/> pins this dictionary alive for the duration of the call, which keeps the
    /// array valid too.
    ///
    /// <para><b>cider-ede.34:</b> the keyed <c>xpc_dictionary_get_*</c> accessors above
    /// (<see cref="GetString"/>, <see cref="GetBool"/>, <see cref="GetUInt64"/>, etc.) are all
    /// libxpc's "forgiving" family — documented to return a default/absent value when the key is
    /// missing or holds the wrong type, never to abort. The <c>xpc_array_*</c> family is not: an
    /// index that is out of range, or a call against a non-array object, is libxpc API misuse and
    /// aborts the whole process. This is the only <c>xpc_array_*</c> call site in the codebase
    /// (audited class-wide for this task; <see cref="DupFd"/> and every other accessor here go
    /// through the forgiving dictionary path and need no change), so every precondition
    /// <c>xpc_array_dup_fd</c> assumes is checked here first, in .NET, where a mismatch can be
    /// thrown instead of aborting.</para>
    ///
    /// <para>A protocol-shape surprise is deliberately surfaced as a plain
    /// <see cref="InvalidOperationException"/> — the same type the pre-existing "key absent" guard
    /// below already used — rather than as an <see cref="XpcException"/>. The caller's
    /// <c>XpcException</c>-only <c>catch</c> degrades to the CLI transport on transport/availability
    /// failures (§ Fallback rule); a shape our own client-side assumptions did not expect is a bug
    /// worth surfacing (as an Internal <see cref="Cider.Core.Runtime.RuntimeException"/> via
    /// <c>GuardAsync</c>), not one to mask behind a silent fallback.</para>
    /// </summary>
    public int DupArrayFd(string key, int index) => Use(h =>
    {
        var array = XpcNative.xpc_dictionary_get_value(h, key);
        if (array == 0)
        {
            throw new InvalidOperationException($"xpc reply carried no '{key}' array");
        }

        var type = XpcNative.xpc_get_type(array);
        if (type != ArrayType)
        {
            var typeName = Marshal.PtrToStringUTF8(XpcNative.xpc_type_get_name(type)) ?? "?";
            throw new InvalidOperationException($"xpc reply's '{key}' was a {typeName}, not an array");
        }

        var count = XpcNative.xpc_array_get_count(array);
        if (index < 0 || (nuint)index >= count)
        {
            throw new InvalidOperationException(
                $"xpc reply's '{key}' array has {count} element(s); index {index} is out of range");
        }

        // xpc_array_get_value is itself an xpc_array_* accessor, so it shares the same "in range
        // and on an array" precondition already established above — safe to call now.
        var element = XpcNative.xpc_array_get_value(array, (nuint)index);
        var elementType = XpcNative.xpc_get_type(element);
        if (elementType != FdType)
        {
            var elementTypeName = Marshal.PtrToStringUTF8(XpcNative.xpc_type_get_name(elementType)) ?? "?";
            throw new InvalidOperationException(
                $"xpc reply's '{key}'[{index}] was a {elementTypeName}, not an fd");
        }

        var fd = XpcNative.xpc_array_dup_fd(array, (nuint)index);
        if (fd < 0)
        {
            // Defence in depth: every precondition libxpc documents is already checked above, so
            // this should be unreachable — but a negative fd must never be wrapped in a
            // SafeFileHandle (SafeHandleZeroOrMinusOneIsInvalid tolerates -1 as "invalid", not as
            // "throw"), so refuse it explicitly rather than hand the caller a handle that looks real.
            throw new InvalidOperationException($"xpc reply's '{key}'[{index}] did not hold a duplicable fd");
        }

        return fd;
    });

    /// <summary>Stores a raw <c>xpc_object_t</c> value (an endpoint, a nested array/dictionary, …)
    /// under <paramref name="key"/> without taking ownership away from <paramref name="value"/>.</summary>
    public void SetValue(string key, XpcObject value) =>
        Use(h => value.Use(v => XpcNative.xpc_dictionary_set_value(h, key, v)));

    public bool ContainsKey(string key) => Use(h => XpcNative.xpc_dictionary_get_value(h, key) != 0);

    public nuint Count => Use(XpcNative.xpc_dictionary_get_count);
}
