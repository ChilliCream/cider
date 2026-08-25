using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Hand-built Objective-C block literals (ABI from clang's Block-ABI-Apple.rst) for
/// <c>xpc_handler_t</c> — the only block libxpc calls into on the client side of this task
/// (<c>xpc_connection_set_event_handler</c>). Layout verified against the real ABI live: 32-byte
/// literal (<c>isa@0, flags@8, reserved@12, invoke@16, descriptor@24</c>), 16-byte descriptor
/// (<c>{reserved, size}</c>) — see <c>XpcBlockLayoutTests</c> and
/// <c>docs/spikes/xpc-probe/XpcProbe/Xpc.cs</c>.
///
/// Every block this type creates is "global" in the clang sense (<c>BLOCK_IS_GLOBAL</c>):
/// immortal, no captured state in the block struct itself, so <c>Block_copy</c>/
/// <c>Block_release</c> are no-ops on it. Per-connection dispatch is done in managed code instead:
/// libxpc always passes the block's own address as the invoke function's first argument (the
/// implicit <c>self</c>), so a distinct block allocation per connection — sharing one static
/// <c>[UnmanagedCallersOnly]</c> invoke function pointer — is enough to route each event to the
/// right managed callback, keyed by that address in <see cref="s_handlers"/> — the callback
/// receives that same address back as its own first argument, so it can free the block itself
/// once it knows no further call will ever arrive (see <see cref="Free"/>'s doc comment: cancelling
/// a connection whose block was already freed segfaults inside libxpc, confirmed live while
/// building this client — freeing must happen from inside the terminal
/// <c>XPC_ERROR_CONNECTION_INVALID</c> callback, not synchronously after <c>xpc_connection_cancel</c>,
/// because cancellation is asynchronous and that callback can still be in flight).
/// </summary>
internal static unsafe class XpcBlock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public nuint reserved;
        public nuint size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Literal
    {
        public nint isa;
        public int flags;
        public int reserved;
        public nint invoke;
        public Descriptor* descriptor;
    }

    public const int BlockIsGlobal = 1 << 28;

    private static readonly nint s_nsConcreteGlobalBlock =
        NativeLibrary.GetExport(NativeLibrary.Load(XpcNative.Lib), "_NSConcreteGlobalBlock");

    private static readonly ConcurrentDictionary<nint, Action<nint, nint>> s_handlers = new();

    /// <summary>Allocates a new event-handler block whose invocation runs <paramref name="onEvent"/>
    /// on whatever libdispatch worker thread libxpc calls it from, passing the block's own address
    /// first (so the callback can call <see cref="Free"/> on itself once it is safe to — see the
    /// type's own doc comment) and the <c>xpc_object_t</c> event second.</summary>
    public static nint CreateEventHandler(Action<nint, nint> onEvent)
    {
        var block = Allocate((nint)(delegate* unmanaged<nint, nint, void>)&InvokeEventHandler);
        s_handlers[block] = onEvent;
        return block;
    }

    /// <summary>Releases the native memory for a block created by <see cref="CreateEventHandler"/>
    /// and stops routing events to it. Callers must only call this once they are certain the block
    /// will never be invoked again — for a connection's event handler, that means from inside the
    /// terminal <c>XPC_ERROR_CONNECTION_INVALID</c> call itself, never merely after
    /// <c>xpc_connection_cancel</c> (cancellation is asynchronous; freeing eagerly races the
    /// in-flight terminal event and segfaults libxpc).</summary>
    public static void Free(nint block)
    {
        if (block == 0)
        {
            return;
        }

        s_handlers.TryRemove(block, out _);
        var literal = (Literal*)block;
        NativeMemory.Free(literal->descriptor);
        NativeMemory.Free(literal);
    }

    /// <summary>Raw field values of a block created by <see cref="CreateEventHandler"/> —
    /// introspection for <c>XpcBlockLayoutTests</c>, not used by production code.</summary>
    public static (nint Isa, int Flags, int Reserved, nint Invoke, nint Descriptor) Inspect(nint block)
    {
        var literal = (Literal*)block;
        return (literal->isa, literal->flags, literal->reserved, literal->invoke, (nint)literal->descriptor);
    }

    /// <summary>Byte layout constants — <c>XpcBlockLayoutTests</c> checks these against the ABI the
    /// spike measured (isa@0, flags@8, reserved@12, invoke@16, descriptor@24, literal size 32,
    /// descriptor size 16) rather than trusting <c>[StructLayout(Sequential)]</c> blindly.</summary>
    public static (int IsaOffset, int FlagsOffset, int ReservedOffset, int InvokeOffset, int DescriptorOffset,
        int LiteralSize, int DescriptorSize, int GlobalFlag, nint NsConcreteGlobalBlock) Layout => (
        (int)Marshal.OffsetOf<Literal>(nameof(Literal.isa)),
        (int)Marshal.OffsetOf<Literal>(nameof(Literal.flags)),
        (int)Marshal.OffsetOf<Literal>(nameof(Literal.reserved)),
        (int)Marshal.OffsetOf<Literal>(nameof(Literal.invoke)),
        (int)Marshal.OffsetOf<Literal>(nameof(Literal.descriptor)),
        sizeof(Literal),
        sizeof(Descriptor),
        BlockIsGlobal,
        s_nsConcreteGlobalBlock);

    // xpc_handler_t: void (^)(xpc_object_t). Invoked on a libdispatch worker thread — a reverse
    // P/Invoke must never let a managed exception escape back into native code.
    [UnmanagedCallersOnly]
    private static void InvokeEventHandler(nint self, nint xpcObject)
    {
        try
        {
            if (s_handlers.TryGetValue(self, out var handler))
            {
                handler(self, xpcObject);
            }
        }
        catch
        {
            // Swallowed deliberately: see the comment above.
        }
    }

    private static nint Allocate(nint invoke)
    {
        var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
        descriptor->reserved = 0;
        descriptor->size = (nuint)sizeof(Literal);

        var literal = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));
        literal->isa = s_nsConcreteGlobalBlock;
        literal->flags = BlockIsGlobal;
        literal->reserved = 0;
        literal->invoke = invoke;
        literal->descriptor = descriptor;
        return (nint)literal;
    }
}
