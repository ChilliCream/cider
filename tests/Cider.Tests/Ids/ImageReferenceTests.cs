using Cider.Core.DockerApi;
using Cider.Core.Ids;
using Xunit;

namespace Cider.Tests.Ids;

public class ImageReferenceTests
{
    [Theory]
    // input                                    normalized                              familiar
    [InlineData("alpine", "docker.io/library/alpine:latest", "alpine:latest")]
    [InlineData("alpine:3.19", "docker.io/library/alpine:3.19", "alpine:3.19")]
    [InlineData("library/alpine", "docker.io/library/alpine:latest", "alpine:latest")]
    [InlineData("docker.io/library/alpine", "docker.io/library/alpine:latest", "alpine:latest")]
    [InlineData("index.docker.io/library/alpine:latest", "docker.io/library/alpine:latest", "alpine:latest")]
    [InlineData("foo/bar", "docker.io/foo/bar:latest", "foo/bar:latest")]
    [InlineData("docker.io/foo/bar:2", "docker.io/foo/bar:2", "foo/bar:2")]
    [InlineData("ghcr.io/foo/bar:1", "ghcr.io/foo/bar:1", "ghcr.io/foo/bar:1")]
    [InlineData("ghcr.io/foo/bar", "ghcr.io/foo/bar:latest", "ghcr.io/foo/bar:latest")]
    [InlineData("localhost:5000/x", "localhost:5000/x:latest", "localhost:5000/x:latest")]
    [InlineData("registry.example.com:5000/a/b:tag", "registry.example.com:5000/a/b:tag", "registry.example.com:5000/a/b:tag")]
    [InlineData("hello-world", "docker.io/library/hello-world:latest", "hello-world:latest")]
    public void Normalization_table(string input, string normalized, string familiar)
    {
        var reference = ImageReference.Parse(input);

        Assert.Equal(normalized, reference.Normalize().ToString());
        Assert.Equal(familiar, reference.Familiar());
    }

    [Fact]
    public void Parse_keeps_the_reference_exactly_as_written()
    {
        var reference = ImageReference.Parse("alpine");

        Assert.Null(reference.Domain);
        Assert.Equal("alpine", reference.Path);
        Assert.Null(reference.Tag);
        Assert.Null(reference.Digest);
        Assert.Equal("alpine", reference.ToString());
    }

    [Fact]
    public void Parses_domain_path_tag_and_digest()
    {
        var digest = "sha256:" + new string('a', 64);
        var reference = ImageReference.Parse($"ghcr.io/foo/bar:1@{digest}");

        Assert.Equal("ghcr.io", reference.Domain);
        Assert.Equal("foo/bar", reference.Path);
        Assert.Equal("1", reference.Tag);
        Assert.Equal(digest, reference.Digest);
        Assert.Equal("ghcr.io/foo/bar", reference.Name);
    }

    [Fact]
    public void A_digest_suppresses_the_implicit_latest_tag()
    {
        var digest = "sha256:" + new string('b', 64);
        var reference = ImageReference.Parse($"alpine@{digest}");

        Assert.Null(reference.Normalize().Tag);
        Assert.Equal($"docker.io/library/alpine@{digest}", reference.Normalize().ToString());
        Assert.Equal($"alpine@{digest}", reference.Familiar());
    }

    [Fact]
    public void A_bare_digest_is_a_valid_reference()
    {
        var digest = "sha256:" + new string('c', 64);
        var reference = ImageReference.Parse(digest);

        Assert.Equal(digest, reference.Digest);
        Assert.Equal("", reference.Path);
        Assert.Equal(digest, reference.ToString());
        Assert.Equal(digest, reference.Familiar());
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = ImageReference.Parse("alpine").Normalize();
        var twice = once.Normalize();

        Assert.Equal(once.ToString(), twice.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alpine:")]
    [InlineData("alpine@")]
    [InlineData("@sha256:abc")]
    public void Invalid_references_are_rejected(string input)
    {
        Assert.False(ImageReference.TryParse(input, out _));
        Assert.Throws<DockerApiException>(() => ImageReference.Parse(input));
    }
}
