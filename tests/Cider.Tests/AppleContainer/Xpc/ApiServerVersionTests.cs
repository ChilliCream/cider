using Cider.AppleContainer.Xpc;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

public sealed class ApiServerVersionTests
{
    [Fact]
    public void Parses_a_release_banner()
    {
        var version = ApiServerVersion.Parse("container-apiserver version 1.3.0 (build: release, commit: d6de569)");

        Assert.Equal(new Version(1, 3, 0), version.Semver);
        Assert.Equal("release", version.Build);
        Assert.Equal("d6de569", version.Commit);
        Assert.Equal("container-apiserver version 1.3.0 (build: release, commit: d6de569)", version.RawBanner);
    }

    [Fact]
    public void Parses_a_debug_banner_with_an_unspecified_commit()
    {
        var version = ApiServerVersion.Parse("container-apiserver version 1.2.2 (build: debug, commit: unspecified)");

        Assert.Equal(new Version(1, 2, 2), version.Semver);
        Assert.Equal("debug", version.Build);
        Assert.Equal("unspecified", version.Commit);
    }

    [Fact]
    public void Parses_a_bare_version_with_no_parenthesised_build_or_commit()
    {
        var version = ApiServerVersion.Parse("container-apiserver version 1.2.0");

        Assert.Equal(new Version(1, 2, 0), version.Semver);
        Assert.Equal("", version.Build);
        Assert.Equal("", version.Commit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("container-apiserver is up")]
    [InlineData(null)]
    public void TryParse_fails_on_an_unrecognisable_banner(string? banner)
    {
        Assert.False(ApiServerVersion.TryParse(banner, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void Parse_throws_a_FormatException_on_an_unrecognisable_banner()
    {
        Assert.Throws<FormatException>(() => ApiServerVersion.Parse("nonsense"));
    }

    [Theory]
    [InlineData("1.1.0", true)]
    [InlineData("1.1.9", true)]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.2", false)]
    [InlineData("1.3.0", false)]
    [InlineData("2.0.0", false)]
    public void IsBelowMinimum_gates_on_1_2_0(string semver, bool expected)
    {
        var version = ApiServerVersion.Parse($"container-apiserver version {semver} (build: release, commit: abc1234)");
        Assert.Equal(expected, version.IsBelowMinimum);
    }

    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.3.0", false)]
    [InlineData("1.3.1", true)]
    [InlineData("1.4.0", true)]
    [InlineData("2.0.0", true)]
    public void IsNewerThanTested_gates_on_1_3_0(string semver, bool expected)
    {
        var version = ApiServerVersion.Parse($"container-apiserver version {semver} (build: release, commit: abc1234)");
        Assert.Equal(expected, version.IsNewerThanTested);
    }

    [Fact]
    public void Minimum_and_Tested_match_the_epics_binding_ruling()
    {
        Assert.Equal(new Version(1, 2, 0), ApiServerVersion.Minimum);
        Assert.Equal(new Version(1, 3, 0), ApiServerVersion.Tested);
    }
}
