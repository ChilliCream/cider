using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// A safe handle around any <c>xpc_object_t</c>. libxpc objects are reference counted, and a
/// double release — or a P/Invoke racing a finalizer that already released — corrupts the process,
/// which is the entire reason this is a <see cref="SafeHandle"/> and not a bare <c>nint</c>: it
/// guarantees <c>xpc_release</c> runs exactly once, even across a finalizer race, and pins the
/// handle alive for the duration of any native call made through <see cref="Use{T}"/>.
/// <see cref="XpcDictionary"/> is the one concrete subclass this task needs.
/// </summary>
internal class XpcObject : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>For handles that will be attached with <see cref="SafeHandle.SetHandle"/> later
    /// (not used directly by this task, but required by the <see cref="SafeHandle"/> contract).</summary>
    protected XpcObject()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Wraps an <c>xpc_object_t</c> this instance now owns a reference to (typically one
    /// returned by a libxpc "create" call, or a reply handed to the caller with +1 ownership).</summary>
    public XpcObject(nint handle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        XpcNative.xpc_release(handle);
        return true;
    }

    /// <summary>The libxpc type name (<c>"dictionary"</c>, <c>"string"</c>, <c>"error"</c>, …).</summary>
    public string TypeName => Use(TypeNameOf);

    /// <summary>libxpc's own debug description of the object.</summary>
    public string Describe() => Use(DescribeOf);

    /// <summary>Runs <paramref name="fn"/> with the raw handle, holding a reference-count pin for
    /// its duration so the underlying object cannot be finalized mid-call.</summary>
    public T Use<T>(Func<nint, T> fn)
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            return fn(DangerousGetHandle());
        }
        finally
        {
            if (added)
            {
                DangerousRelease();
            }
        }
    }

    /// <inheritdoc cref="Use{T}"/>
    public void Use(Action<nint> fn) => Use<object?>(h =>
    {
        fn(h);
        return null;
    });

    /// <summary>Reads the type name of a raw, not-yet-wrapped <c>xpc_object_t</c> — for the one
    /// place (<c>XpcClient.SendSync</c>) that must classify a reply before deciding whether to keep
    /// or release it, i.e. before it can be handed to a safe-handle constructor.</summary>
    public static string TypeNameOf(nint obj) =>
        Marshal.PtrToStringUTF8(XpcNative.xpc_type_get_name(XpcNative.xpc_get_type(obj))) ?? "?";

    /// <inheritdoc cref="TypeNameOf"/>
    public static string DescribeOf(nint obj)
    {
        var p = XpcNative.xpc_copy_description(obj);
        if (p == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUTF8(p) ?? string.Empty;
        }
        finally
        {
            XpcNative.free(p);
        }
    }
}
