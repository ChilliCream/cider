using Cider.Core.Ids;
using Xunit;

namespace Cider.Tests.Ids;

public class DockerIdTests
{
    [Fact]
    public void New_returns_64_lowercase_hex_characters()
    {
        for (var i = 0; i < 20; i++)
        {
            var id = DockerId.New();

            Assert.Equal(64, id.Length);
            Assert.All(id, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f', $"unexpected character '{c}'"));
            Assert.True(DockerId.IsFullId(id));
        }
    }

    [Fact]
    public void New_ids_are_unique()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(ids.Add(DockerId.New()));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("zzzz", false)]
    public void IsFullId_rejects_non_ids(string? value, bool expected) =>
        Assert.Equal(expected, DockerId.IsFullId(value));

    [Fact]
    public void IsFullId_requires_exactly_64_characters()
    {
        Assert.True(DockerId.IsFullId(new string('a', 64)));
        Assert.False(DockerId.IsFullId(new string('a', 63)));
        Assert.False(DockerId.IsFullId(new string('a', 65)));
        Assert.False(DockerId.IsFullId(new string('g', 64)));
    }

    [Fact]
    public void IsHexPrefix_accepts_1_to_64_hex_characters()
    {
        Assert.True(DockerId.IsHexPrefix("a"));
        Assert.True(DockerId.IsHexPrefix("deadbeef"));
        Assert.True(DockerId.IsHexPrefix(new string('f', 64)));
        Assert.False(DockerId.IsHexPrefix(""));
        Assert.False(DockerId.IsHexPrefix(null));
        Assert.False(DockerId.IsHexPrefix("dead-beef"));
        Assert.False(DockerId.IsHexPrefix(new string('a', 65)));
    }

    [Fact]
    public void Short_truncates_to_12_characters()
    {
        var id = new string('a', 60) + "bcde";

        Assert.Equal(new string('a', 12), DockerId.Short(id));
        Assert.Equal(12, DockerId.Short(id).Length);
        Assert.Equal("abc", DockerId.Short("abc"));
    }
}
