using System.Runtime.InteropServices;
using Cider.AppleContainer.Xpc;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// The Objective-C block literal <see cref="XpcBlock"/> hand-builds must match clang's real ABI
/// exactly, or <c>xpc_connection_set_event_handler</c> reads garbage out of it. This is the layout
/// the spike measured live against a real <c>xpc_connection_t</c>
/// (docs/spikes/xpc-probe/XpcProbe/Xpc.cs, docs/spikes/xpc/04-dotnet-xpc-probe-report.md) and this
/// task re-verified the same way while building the production client.
/// </summary>
public class XpcBlockLayoutTests
{
    [Fact]
    public void Literal_layout_matches_the_clang_block_ABI()
    {
        var layout = XpcBlock.Layout;

        Assert.Equal(0, layout.IsaOffset);
        Assert.Equal(8, layout.FlagsOffset);
        Assert.Equal(12, layout.ReservedOffset);
        Assert.Equal(16, layout.InvokeOffset);
        Assert.Equal(24, layout.DescriptorOffset);
        Assert.Equal(32, layout.LiteralSize);
        Assert.Equal(16, layout.DescriptorSize);
        Assert.Equal(1 << 28, layout.GlobalFlag);
        Assert.NotEqual(0, layout.NsConcreteGlobalBlock);
    }

    [Fact]
    public void CreateEventHandler_populates_isa_flags_invoke_and_a_32_byte_descriptor()
    {
        var block = XpcBlock.CreateEventHandler((_, _) => { });
        try
        {
            var (isa, flags, reserved, invoke, descriptor) = XpcBlock.Inspect(block);

            Assert.Equal(XpcBlock.Layout.NsConcreteGlobalBlock, isa);
            Assert.Equal(XpcBlock.BlockIsGlobal, flags);
            Assert.Equal(0, reserved);
            Assert.NotEqual(0, invoke);
            Assert.NotEqual(0, descriptor);
        }
        finally
        {
            XpcBlock.Free(block);
        }
    }

    [Fact]
    public async Task CreateEventHandler_routes_a_call_through_self_to_the_right_managed_delegate()
    {
        // Every block shares one invoke function pointer; dispatch to the right managed callback
        // depends entirely on looking up the block's own address ("self"), which is what this
        // proves — two blocks, two independently-firing handlers, each also handed its own
        // address back (which XpcClient/XpcListener rely on to self-free on the terminal event).
        var tcs1 = new TaskCompletionSource<(nint Self, nint Obj)>();
        var tcs2 = new TaskCompletionSource<(nint Self, nint Obj)>();
        var block1 = XpcBlock.CreateEventHandler((self, obj) => tcs1.TrySetResult((self, obj)));
        var block2 = XpcBlock.CreateEventHandler((self, obj) => tcs2.TrySetResult((self, obj)));
        try
        {
            InvokeForTest(block2, 0x2222);
            InvokeForTest(block1, 0x1111);

            var result1 = await tcs1.Task;
            var result2 = await tcs2.Task;
            Assert.Equal((block1, (nint)0x1111), result1);
            Assert.Equal((block2, (nint)0x2222), result2);
        }
        finally
        {
            XpcBlock.Free(block1);
            XpcBlock.Free(block2);
        }
    }

    [Fact]
    public void Free_stops_routing_and_does_not_throw_on_a_second_call()
    {
        var received = false;
        var block = XpcBlock.CreateEventHandler((_, _) => received = true);
        XpcBlock.Free(block);

        // A block invocation after Free must not resurrect routing (the handler dictionary entry
        // is gone) and Free itself must be idempotent-safe for a NULL/zero block.
        XpcBlock.Free(0);
        Assert.False(received);
    }

    /// <summary><c>xpc_handler_t</c>'s shape: <c>void (^)(self, xpc_object_t)</c>.</summary>
    private delegate void EventHandlerInvoke(nint self, nint xpcObject);

    /// <summary>Invokes a block exactly the way libxpc would: through its own <c>invoke</c> function
    /// pointer, passing the block's own address as <c>self</c>. Uses
    /// <see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}(nint)"/> rather than a raw
    /// function pointer so this test project needs no <c>AllowUnsafeBlocks</c> of its own.</summary>
    private static void InvokeForTest(nint block, nint arg)
    {
        var invoke = XpcBlock.Inspect(block).Invoke;
        var fn = Marshal.GetDelegateForFunctionPointer<EventHandlerInvoke>(invoke);
        fn(block, arg);
    }
}
