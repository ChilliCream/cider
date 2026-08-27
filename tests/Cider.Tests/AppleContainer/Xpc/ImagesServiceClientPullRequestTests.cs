using System.Text;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// Wire-message assertions for <see cref="ImagesServiceClient.BuildImagePullRequest"/> (cider-ede.43)
/// — the field-by-field contract against Apple's own <c>ClientImage.pull</c> (apple/container 1.3.0,
/// <c>ClientImage.swift:253-272</c>). The load-bearing one: <c>maxConcurrentDownloads</c> must be
/// present and nonzero. The server reads an absent key as 0
/// (<c>ImagesServiceHarness.swift:51</c>, <c>message.int64</c>) and hands it to Containerization's
/// <c>ImportOperation.fetchAll</c>, which starts <c>0..&lt;maxConcurrentDownloads</c> download tasks
/// (<c>ImageStore+Import.swift:126</c>) — 0 downloads nothing, commits an empty ingest plus the index
/// entry (the machine-wide dangling reference), and fails the follow-up <c>imageUnpack</c> at the
/// index digest with zero durable bytes written. No live apiserver needed:
/// <c>xpc_dictionary_*</c> is a pure in-process object model (same rationale as
/// <c>XpcDictionaryTests</c>).
/// </summary>
public class ImagesServiceClientPullRequestTests
{
    [Fact]
    public void Pull_request_carries_nonzero_maxConcurrentDownloads()
    {
        using var request = ImagesServiceClient.BuildImagePullRequest("docker.io/library/redis:8.6", null, null);

        Assert.True(request.ContainsKey("maxConcurrentDownloads"));
        Assert.Equal(ImagesServiceClient.MaxConcurrentDownloads, request.GetInt64("maxConcurrentDownloads"));
        Assert.True(request.GetInt64("maxConcurrentDownloads") > 0);
    }

    [Fact]
    public void Pull_request_carries_route_reference_and_insecure_flag()
    {
        using var request = ImagesServiceClient.BuildImagePullRequest("docker.io/library/redis:8.6", null, null);

        Assert.Equal("imagePull", request.GetString(XpcMessage.RouteKey));
        Assert.Equal("docker.io/library/redis:8.6", request.GetString("imageReference"));
        Assert.True(request.ContainsKey("insecureFlag"));
        Assert.False(request.GetBool("insecureFlag"));
    }

    [Fact]
    public void Pull_request_omits_ociPlatform_and_endpoint_when_not_given()
    {
        // ociPlatform is optional on the wire, exactly as Apple's CLI sends it: with no --platform
        // flag, DefaultPlatform.resolve returns nil and the key is absent (DefaultPlatform.swift:75-93
        // at 1.3.0) — the server then pulls every platform the index carries.
        using var request = ImagesServiceClient.BuildImagePullRequest("docker.io/library/redis:8.6", null, null);

        Assert.False(request.ContainsKey("ociPlatform"));
        Assert.False(request.ContainsKey("progressUpdateEndpoint"));
    }

    [Fact]
    public void Pull_request_serializes_ociPlatform_when_given()
    {
        var platform = new Platform { Os = "linux", Architecture = "arm64", Variant = "v8" };
        using var request = ImagesServiceClient.BuildImagePullRequest("docker.io/library/redis:8.6", platform, null);

        var bytes = request.GetData("ociPlatform");
        Assert.NotNull(bytes);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"os\":\"linux\"", json);
        Assert.Contains("\"architecture\":\"arm64\"", json);
        Assert.Contains("\"variant\":\"v8\"", json);
    }
}
