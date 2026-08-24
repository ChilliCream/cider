using Cider.Daemon.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cider.Tests.Daemon;

public sealed class VersionPrefixMiddlewareTests
{
    [Theory]
    [InlineData("/v1.47/containers/json", "/containers/json", "1.47")]
    [InlineData("/v1.24/_ping", "/_ping", "1.24")]
    [InlineData("/v2.0/info", "/info", "2.0")]
    [InlineData("/v1.47", "/", "1.47")]
    [InlineData("/v1.47/", "/", "1.47")]
    [InlineData("/v1/version", "/version", "1")]
    public void Strips_the_api_version_prefix(string path, string expected, string version)
    {
        Assert.True(VersionPrefixMiddleware.TryStrip(path, out var stripped, out var parsed));
        Assert.Equal(expected, stripped);
        Assert.Equal(version, parsed);
    }

    [Theory]
    [InlineData("/v1.43/images/json", false)]
    [InlineData("/v1.44/images/json", true)]
    [InlineData("/v1.47/images/json", true)]
    [InlineData("/v2.0/images/json", true)]
    [InlineData("/v1/images/json", false)]
    // An unversioned request counts as the newest API, like dockerd's own version middleware.
    [InlineData("/images/json", true)]
    public async Task Exposes_the_requested_version_to_handlers(string path, bool atLeast144)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var seen = false;
        var middleware = new VersionPrefixMiddleware(ctx =>
        {
            seen = true;
            Assert.Equal(atLeast144, VersionPrefixMiddleware.IsAtLeast(ctx, 1, 44));
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(seen);
    }

    [Theory]
    [InlineData("/containers/json")]
    [InlineData("/version")]
    [InlineData("/volumes/v1.47")]
    [InlineData("/vault/secrets")]
    [InlineData("")]
    [InlineData("/")]
    public void Leaves_unprefixed_paths_alone(string path)
    {
        Assert.False(VersionPrefixMiddleware.TryStrip(path, out var stripped, out _));
        Assert.Equal(path, stripped);
    }
}
