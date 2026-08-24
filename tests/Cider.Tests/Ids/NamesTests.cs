using System.Text.RegularExpressions;
using Cider.Core.Ids;
using Xunit;

namespace Cider.Tests.Ids;

public class NamesTests
{
    [Theory]
    [InlineData("web", true)]
    [InlineData("app-web-1", true)]
    [InlineData("/app-web-1", true)]
    [InlineData("a.b_c-d", true)]
    [InlineData("", false)]
    [InlineData("-web", false)]
    [InlineData(".web", false)]
    [InlineData("we b", false)]
    [InlineData("web/1", false)]
    public void IsValidDockerName(string name, bool expected) =>
        Assert.Equal(expected, Names.IsValidDockerName(name));

    [Theory]
    [InlineData("web", true)]
    [InlineData("a", true)]
    [InlineData("app-web-1", true)]
    [InlineData("A.b_c-d", true)]
    [InlineData("", false)]
    [InlineData("-web", false)]
    [InlineData("web:1", false)]
    [InlineData("web/1", false)]
    public void IsValidAppleContainerId(string id, bool expected) =>
        Assert.Equal(expected, Names.IsValidAppleContainerId(id));

    [Fact]
    public void IsValidAppleContainerId_caps_at_63_characters()
    {
        Assert.True(Names.IsValidAppleContainerId(new string('a', 63)));
        Assert.False(Names.IsValidAppleContainerId(new string('a', 64)));
    }

    [Fact]
    public void GenerateRandomName_is_adjective_underscore_surname()
    {
        var pattern = new Regex("^[a-z]+_[a-z]+$", RegexOptions.CultureInvariant);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 200; i++)
        {
            var name = Names.GenerateRandomName();

            Assert.Matches(pattern, name);
            Assert.NotEqual("boring_wozniak", name);
            Assert.True(Names.IsValidDockerName(name));
            Assert.True(Names.IsValidAppleContainerId(name));
            seen.Add(name);
        }

        Assert.True(seen.Count > 50, $"expected varied names, got {seen.Count} distinct");
    }
}
