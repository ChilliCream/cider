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
