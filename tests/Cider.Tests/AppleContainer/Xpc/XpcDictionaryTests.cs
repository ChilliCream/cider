using System.Runtime.InteropServices;
using System.Text;
using Cider.AppleContainer.Xpc;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// Round trips every XPC value type the task's file scope calls for
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2's value-type table). None of this needs a
/// live apiserver: <c>xpc_dictionary_*</c> is a pure in-process object model.
/// </summary>
public class XpcDictionaryTests
{
    [Fact]
    public void String_round_trips()
    {
        using var dict = new XpcDictionary();
        dict.SetString("k", "hello world");
        Assert.Equal("hello world", dict.GetString("k"));
    }

    [Fact]
    public void String_absent_key_returns_null()
    {
        using var dict = new XpcDictionary();
        Assert.Null(dict.GetString("missing"));
    }

    [Fact]
    public void Data_round_trips_raw_bytes_including_JSON()
    {
        using var dict = new XpcDictionary();
        var json = Encoding.UTF8.GetBytes("{\"ids\":[],\"labels\":{}}");
        dict.SetData("listFilters", json);
        Assert.Equal(json, dict.GetData("listFilters"));
    }

    [Fact]
    public void Data_absent_key_returns_null()
    {
        using var dict = new XpcDictionary();
        Assert.Null(dict.GetData("missing"));
    }

    [Fact]
    public void Data_round_trips_empty_bytes()
    {
        using var dict = new XpcDictionary();
        dict.SetData("empty", ReadOnlySpan<byte>.Empty);
        var result = dict.GetData("empty");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bool_round_trips(bool value)
    {
        using var dict = new XpcDictionary();
        dict.SetBool("k", value);
        Assert.Equal(value, dict.GetBool("k"));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(42UL)]
    public void UInt64_round_trips(ulong value)
    {
        using var dict = new XpcDictionary();
        dict.SetUInt64("k", value);
        Assert.Equal(value, dict.GetUInt64("k"));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void Int64_round_trips(long value)
    {
        using var dict = new XpcDictionary();
        dict.SetInt64("k", value);
        Assert.Equal(value, dict.GetInt64("k"));
    }

    [Fact]
    public void Date_round_trips_to_100ns_tick_precision()
    {
        using var dict = new XpcDictionary();
        // Not on a whole second, to prove the ns-precision conversion (not just second rounding).
        var value = new DateTimeOffset(2026, 8, 25, 12, 34, 56, 789, TimeSpan.Zero).AddTicks(1234567);
        dict.SetDate("k", value);
        Assert.Equal(value, dict.GetDate("k"));
    }

    [Fact]
    public void Date_round_trips_the_Unix_epoch()
    {
        using var dict = new XpcDictionary();
        dict.SetDate("k", DateTimeOffset.UnixEpoch);
        Assert.Equal(DateTimeOffset.UnixEpoch, dict.GetDate("k"));
    }

    [Fact]
    public void Fd_round_trips_via_dup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xpc-fd-{Guid.NewGuid():N}");
        using var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var fd = (int)file.SafeFileHandle.DangerousGetHandle();
            using var dict = new XpcDictionary();
            dict.SetFd("k", fd);

            var dup = dict.DupFd("k");
            try
            {
                Assert.True(dup >= 0);
                Assert.NotEqual(fd, dup); // a genuine duplicate, not the same descriptor number
            }
            finally
            {
                if (dup >= 0)
                {
                    Cider.AppleContainer.Native.Libc.Close(dup);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ContainsKey_distinguishes_absent_from_present()
    {
        using var dict = new XpcDictionary();
        Assert.False(dict.ContainsKey("k"));
        dict.SetBool("k", false); // present, even though the value itself is falsy
        Assert.True(dict.ContainsKey("k"));
    }

    [Fact]
    public void Count_reflects_the_number_of_keys()
    {
        using var dict = new XpcDictionary();
        Assert.Equal((nuint)0, dict.Count);
        dict.SetString("a", "1");
        dict.SetString("b", "2");
        Assert.Equal((nuint)2, dict.Count);
    }

    // ---- DupArrayFd (cider-ede.34) -------------------------------------------------------------
    //
    // libxpc treats a type or range mismatch on an xpc_array_* accessor as API misuse and aborts
    // the calling process — these all run in-process and assert a thrown InvalidOperationException;
    // the test host surviving to report the assertion result *is* the proof the guard works.

    [Fact]
    public void DupArrayFd_throws_when_the_value_is_not_an_array()
    {
        using var dict = new XpcDictionary();
        dict.SetString("logs", "not-an-array");

        var ex = Assert.Throws<InvalidOperationException>(() => dict.DupArrayFd("logs", 0));
        Assert.Contains("logs", ex.Message);
        Assert.Contains("string", ex.Message);
    }

    [Fact]
    public void DupArrayFd_throws_when_the_key_is_absent()
    {
        using var dict = new XpcDictionary();

        var ex = Assert.Throws<InvalidOperationException>(() => dict.DupArrayFd("logs", 0));
        Assert.Contains("logs", ex.Message);
    }

    [Fact]
    public void DupArrayFd_throws_when_the_array_is_empty()
    {
        using var dict = new XpcDictionary();
        using var array = new XpcObject(XpcNative.xpc_array_create(0, 0));
        dict.SetValue("logs", array);

        var ex = Assert.Throws<InvalidOperationException>(() => dict.DupArrayFd("logs", 0));
        Assert.Contains("logs", ex.Message);
        Assert.Contains("0 element", ex.Message);
    }

    [Fact]
    public void DupArrayFd_throws_when_the_index_is_out_of_range_on_a_non_empty_array()
    {
        using var dict = new XpcDictionary();
        var (array, file, _, path) = BuildFdArray();
        using (array)
        using (file)
        {
            try
            {
                dict.SetValue("logs", array);

                var ex = Assert.Throws<InvalidOperationException>(() => dict.DupArrayFd("logs", 5));
                Assert.Contains("logs", ex.Message);
                Assert.Contains("1 element", ex.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void DupArrayFd_throws_when_the_element_at_index_is_not_an_fd()
    {
        using var dict = new XpcDictionary();
        using var valueDict = new XpcDictionary();
        valueDict.SetString("s", "x");
        var stringValue = valueDict.Use(h => XpcNative.xpc_dictionary_get_value(h, "s"));

        using var array = new XpcObject(BuildArray(stringValue));
        dict.SetValue("logs", array);

        var ex = Assert.Throws<InvalidOperationException>(() => dict.DupArrayFd("logs", 0));
        Assert.Contains("logs", ex.Message);
    }

    [Fact]
    public void DupArrayFd_round_trips_a_real_fd_when_the_shape_is_correct()
    {
        using var dict = new XpcDictionary();
        var (array, file, fd, path) = BuildFdArray();
        using (array)
        using (file)
        {
            try
            {
                dict.SetValue("logs", array);

                var dup = dict.DupArrayFd("logs", 0);
                try
                {
                    Assert.True(dup >= 0);
                    Assert.NotEqual(fd, dup); // a genuine duplicate, not the same descriptor number
                }
                finally
                {
                    if (dup >= 0)
                    {
                        Cider.AppleContainer.Native.Libc.Close(dup);
                    }
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Builds a one-element xpc array whose element 0 is a real, duplicable fd — mirrors
    /// <c>containerLogs</c>'s <c>logs</c> array shape. <c>xpc_fd_create</c> duplicates <c>Fd</c>
    /// internally, so closing <c>File</c> afterwards does not invalidate the array's own copy. The
    /// caller owns and must dispose <c>Array</c>/<c>File</c> and delete <c>Path</c>.</summary>
    private static (XpcObject Array, FileStream File, int Fd, string Path) BuildFdArray()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xpc-array-fd-{Guid.NewGuid():N}");
        var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var fd = (int)file.SafeFileHandle.DangerousGetHandle();

        using var fdObject = new XpcObject(XpcNative.xpc_fd_create(fd));
        var array = fdObject.Use(fdHandle => new XpcObject(BuildArray(fdHandle)));
        return (array, file, fd, path);
    }

    /// <summary>Builds an xpc array pre-populated with <paramref name="elements"/> via
    /// <c>xpc_array_create</c>'s own <c>objects</c>/<c>count</c> constructor arguments — deliberately
    /// <b>not</b> via <c>xpc_array_set_value</c> appending onto an empty array (index == count), which
    /// live-testing while building this task's guard (cider-ede.34) showed libxpc also treats as API
    /// misuse and aborts on, same as an out-of-range read. <c>xpc_array_create</c> retains its own
    /// reference to each element; the caller keeps whatever reference it already held on
    /// <paramref name="elements"/> and remains responsible for releasing it.</summary>
    private static nint BuildArray(params nint[] elements)
    {
        var size = elements.Length * nint.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < elements.Length; i++)
            {
                Marshal.WriteIntPtr(buffer, i * nint.Size, elements[i]);
            }

            return XpcNative.xpc_array_create(buffer, (nuint)elements.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void SetValue_stores_a_nested_xpc_object_without_taking_its_ownership()
    {
        using var inner = new XpcDictionary();
        inner.SetString("nested", "yes");

        using var outer = new XpcDictionary();
        outer.SetValue("child", inner);

        // `inner` must still be usable afterwards — SetValue must not release it.
        Assert.Equal("yes", inner.GetString("nested"));
        Assert.True(outer.ContainsKey("child"));
    }
}
