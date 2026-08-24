using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Cider.Core.Configuration;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Logs;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Daemon.Hosting;
using Cider.Daemon.Routes;
using Cider.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// Integration tests for the daemon-resources route groups (Image/Build/Network/Volume/Stub),
/// hosted in-process on a temporary unix socket with a real <see cref="ImageManager"/>/
/// <see cref="NetworkManager"/>/<see cref="VolumeManager"/> backed by <see cref="FakeContainerRuntime"/>.
/// Self-contained: uses the real <see cref="ErrorMiddleware"/> from daemon-host, but does not depend
/// on <c>Program.cs</c> or any other route group.
/// </summary>
public sealed class ResourceRoutesTests : IAsyncLifetime
{
    // A short, fixed prefix rather than Path.GetTempPath(): macOS's per-user temp dir
    // (/var/folders/.../T/) can itself run 50+ characters, leaving too little headroom under the
    // 104-character sockaddr_un.sun_path limit once a socket file name is appended.
    private readonly string _socketPath = $"/tmp/cider-rr-{Guid.NewGuid():N}"[..24] + ".sock";
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "cider-tests", "resource-routes", Guid.NewGuid().ToString("N"));

    private WebApplication? _app;
    private HttpClient? _client;
    private FakeContainerRuntime? _runtime;
    private ContainerManager? _containers;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        var runtime = new FakeContainerRuntime();
        _runtime = runtime;
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _dataDir };
        var networkStore = new InMemoryRecordStore<NetworkRecord>();
        var volumeStore = new InMemoryRecordStore<VolumeRecord>();

        var images = new ImageManager(runtime, events, options, NullLogger<ImageManager>.Instance);
        var networks = new NetworkManager(runtime, networkStore, events, NullLogger<NetworkManager>.Instance);
        var volumes = new VolumeManager(runtime, volumeStore, events, options, NullLogger<VolumeManager>.Instance);

        // `POST /commit` lives in the image routes but has to resolve the container it snapshots, so
        // this group needs a real ContainerManager over the same fake runtime.
        options.EnsureDirectories();
        var containers = new ContainerManager(
            runtime,
            new InMemoryRecordStore<ContainerRecord>(),
            new LogStore(options.LogsDir, options.LogMaxBytes),
            events,
            new PortAllocator(),
            new RecordingPortPublisher(enabled: false),
            new NameRegistry(),
            new FakeDnsForwarder(),
            images,
            networks,
            volumes,
            options,
            NullLogger<ContainerManager>.Instance);
        await networks.EnsureDefaultAsync(CancellationToken.None);
        _containers = containers;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(_socketPath));

        builder.Services.AddSingleton(images);
        builder.Services.AddSingleton(networks);
        builder.Services.AddSingleton(volumes);
        builder.Services.AddSingleton(containers);

        _app = builder.Build();
        _app.UseMiddleware<ErrorMiddleware>();

        // The real host runs this ahead of the routes; the image routes need the API version it
        // stashes to gate VirtualSize, so the test host mirrors that order.
        _app.UseMiddleware<VersionPrefixMiddleware>();
        _app.UseRouting();
        _app.MapImageRoutes();
        _app.MapBuildRoutes();
        _app.MapNetworkRoutes();
        _app.MapVolumeRoutes();
        _app.MapStubRoutes();

        await _app.StartAsync();

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri("http://cider-tests/") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        TryDelete(_socketPath);
        TryDeleteDirectory(_dataDir);
    }

    [Fact]
    public async Task ImagesJson_ListsSeededAlpineImage()
    {
        var response = await _client!.GetAsync("/images/json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("alpine:latest", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesJson_LegacyFilterParam_MatchesRepositoryName()
    {
        var noMatch = await _client!.GetAsync("/v1.47/images/json?filter=nonesuch");
        var match = await _client.GetAsync("/v1.47/images/json?filter=busybox");
        var tagged = await _client.GetAsync("/v1.47/images/json?filter=busybox%3Alatest");
        var substring = await _client.GetAsync("/v1.47/images/json?filter=box");

        Assert.Empty(await ReadSummariesAsync(noMatch));
        var matched = await ReadSummariesAsync(match);
        Assert.Equal("busybox:latest", Assert.Single(Assert.Single(matched).RepoTags));
        Assert.Single(await ReadSummariesAsync(tagged));

        // dockerd's filter is a name/glob match, never a substring search.
        Assert.Empty(await ReadSummariesAsync(substring));
    }

    [Fact]
    public async Task ImagesJson_LegacyFilterParam_AppliesOnTopOfFilters()
    {
        var response = await _client!.GetAsync(
            "/v1.47/images/json?filter=busybox&filters=%7B%22dangling%22%3A%5B%22true%22%5D%7D");

        Assert.Empty(await ReadSummariesAsync(response));
    }

    [Fact]
    public async Task ImagesJson_VirtualSize_PresentBelow144_OmittedFrom144()
    {
        var legacy = await _client!.GetStringAsync("/v1.43/images/json");
        var modern = await _client.GetStringAsync("/v1.44/images/json");
        var current = await _client.GetStringAsync("/v1.47/images/json");
        var unversioned = await _client.GetStringAsync("/images/json");

        Assert.Contains("\"VirtualSize\":7800000", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualSize", modern, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualSize", current, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualSize", unversioned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesInspect_VirtualSize_PresentBelow144_OmittedFrom144()
    {
        var legacy = await _client!.GetStringAsync("/v1.43/images/alpine:latest/json");
        var modern = await _client.GetStringAsync("/v1.44/images/alpine:latest/json");

        Assert.Contains("\"VirtualSize\":7800000", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualSize", modern, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesJson_SharedSizeRequested_StaysAtTheNotComputedSentinel()
    {
        // Apple's `container` exposes no per-layer byte sizes, so shared-size cannot be computed;
        // the request is accepted and -1 ("not computed") is reported rather than a fabricated
        // number.
        var response = await _client!.GetAsync("/v1.47/images/json?shared-size=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await ReadSummariesAsync(response), summary => Assert.Equal(-1, summary.SharedSize));
    }

    [Fact]
    public async Task ImagesDelete_OmitsTheAbsentMemberInsteadOfWritingNull()
    {
        await _client!.PostAsync("/images/alpine:latest/tag?repo=zq5tmp&tag=probe", content: null);

        var response = await _client.DeleteAsync("/images/zq5tmp:probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("[{\"Untagged\":\"zq5tmp:probe\"}]", body);
    }

    private static async Task<List<ImageSummary>> ReadSummariesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return DockerJson.Deserialize<List<ImageSummary>>(body) ?? [];
    }

    [Fact]
    public async Task ImagesInspect_AcceptsBothShortAndFullyQualifiedNames()
    {
        var byShortName = await _client!.GetAsync("/images/alpine:latest/json");
        var byFullReference = await _client.GetAsync("/images/docker.io/library/alpine:latest/json");

        Assert.Equal(HttpStatusCode.OK, byShortName.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byFullReference.StatusCode);

        var shortBody = await byShortName.Content.ReadAsStringAsync();
        var fullBody = await byFullReference.Content.ReadAsStringAsync();
        Assert.Contains("sha256:", shortBody, StringComparison.Ordinal);
        Assert.Contains("sha256:", fullBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesTagThenDelete_RoutesTrailingSegmentsCorrectly()
    {
        var tagResponse = await _client!.PostAsync("/images/alpine:latest/tag?repo=myrepo%2Fmyimage&tag=v1", content: null);
        Assert.Equal(HttpStatusCode.Created, tagResponse.StatusCode);

        var deleteResponse = await _client.DeleteAsync("/images/myrepo/myimage:v1");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var body = await deleteResponse.Content.ReadAsStringAsync();
        Assert.Contains("Untagged", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesCreate_StreamsNdjsonEndingWithStatusLine()
    {
        var response = await _client!.PostAsync("/images/create?fromImage=alpine&tag=latest", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.Contains("Status:", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesCreate_MissingManifest_Returns404BeforeAnyNdjson()
    {
        _runtime!.PullFailure = RuntimeException.NotFound("pull alpine:doesnotexist99: manifest unknown");

        var response = await _client!.PostAsync("/images/create?fromImage=alpine&tag=doesnotexist99", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"message":"manifest for alpine:doesnotexist99 not found: manifest unknown"}""", body);
    }

    [Fact]
    public async Task ImagesPush_MissingImage_Returns404BeforeAnyNdjson()
    {
        var response = await _client!.PostAsync("/images/no-such-image:latest/push", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"message":"No such image: no-such-image:latest"}""", body);
    }

    [Fact]
    public async Task Commit_ReturnsCreatedWithTheNewImageId()
    {
        var container = await CreateContainerAsync("rr-commit");

        var response = await _client!.PostAsync($"/commit?container={container}&repo=rr-committed&tag=1&comment=hi&author=grj", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var id = DockerJson.Deserialize<IdResponse>(body)?.Id;
        Assert.StartsWith("sha256:", id, StringComparison.Ordinal);

        // The committed image really exists afterwards, under the requested tag.
        var inspect = await _client.GetAsync("/images/rr-committed:1/json");
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        Assert.Contains(id!, await inspect.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_UnknownContainer_Returns404()
    {
        var response = await _client!.PostAsync("/commit?container=rr-nope&repo=x", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Commit_WithoutContainerParameter_Returns400()
    {
        var response = await _client!.PostAsync("/commit?repo=x", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Commit_UnsupportedChange_Returns400()
    {
        var container = await CreateContainerAsync("rr-commit-bad");

        var response = await _client!.PostAsync($"/commit?container={container}&repo=x&changes=RUN%20true", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("RUN", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesCreate_FromSrcDash_ImportsTheTarballAndStreamsTheNewId()
    {
        using var content = new StreamContent(BuildRootFsTar());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        var response = await _client!.PostAsync("/images/create?fromSrc=-&repo=rr-imported&tag=7", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var line = Assert.Single(body.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var id = DockerJson.Deserialize<JsonMessage>(line)?.Status;
        Assert.StartsWith("sha256:", id, StringComparison.Ordinal);

        var inspect = await _client.GetAsync("/images/rr-imported:7/json");
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
    }

    [Fact]
    public async Task ImagesCreate_FromSrcDash_UnsupportedChange_Returns400BeforeAnyNdjson()
    {
        using var content = new StreamContent(BuildRootFsTar());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        var response = await _client!.PostAsync("/images/create?fromSrc=-&repo=rr-bad&changes=RUN%20true", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ImagesCreate_FromSrcUrl_IsStillNotImplemented()
    {
        var response = await _client!.PostAsync("/images/create?fromSrc=https%3A%2F%2Fexample.com%2Frootfs.tar&repo=rr-url", content: null);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    /// <summary>Creates a container through the real manager and returns its Docker id.</summary>
    private async Task<string> CreateContainerAsync(string name)
    {
        var request = new ContainerCreateRequest { Image = "alpine:latest", Cmd = ["sh", "-c", "true"] };
        var created = await _containers!.CreateAsync(request, name, platform: null, CancellationToken.None);
        return created.Id;
    }

    private static MemoryStream BuildRootFsTar()
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "x")
            {
                DataStream = new MemoryStream("hello\n"u8.ToArray()),
            });
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Build_WithRealTarContext_ReturnsAuxAndSuccessfullyBuilt()
    {
        await using var tarContext = await BuildTarContextAsync("FROM alpine\n");
        using var content = new StreamContent(tarContext);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        var response = await _client!.PostAsync("/build?t=myimage%3Atest", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"aux\"", body, StringComparison.Ordinal);
        Assert.Contains("Successfully built", body, StringComparison.Ordinal);

        // The adapter reports raw build output only; the manager alone owns the terminal Docker
        // lines. A regression here means the client saw "Successfully built"/"tagged" twice.
        Assert.Equal(1, CountOccurrences(body, "Successfully built"));
        Assert.Equal(1, CountOccurrences(body, "Successfully tagged"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public async Task Networks_CreateListInspectDelete_AndRejectDuplicatesAndBridgeRemoval()
    {
        var createRequest = new NetworkCreateRequest { Name = "rr-testnet", Driver = "bridge" };
        var createJson = DockerJson.Serialize(createRequest);

        var createResponse = await _client!.PostAsync("/networks/create", new StringContent(createJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = DockerJson.Deserialize<NetworkCreateResponse>(await createResponse.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(created?.Id));

        var listResponse = await _client.GetAsync("/networks");
        Assert.Contains("rr-testnet", await listResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var inspectResponse = await _client.GetAsync("/networks/rr-testnet");
        Assert.Equal(HttpStatusCode.OK, inspectResponse.StatusCode);

        var duplicateResponse = await _client.PostAsync("/networks/create", new StringContent(createJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

        var bridgeDeleteResponse = await _client.DeleteAsync("/networks/bridge");
        Assert.Equal(HttpStatusCode.Forbidden, bridgeDeleteResponse.StatusCode);

        var deleteResponse = await _client.DeleteAsync("/networks/rr-testnet");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var missingResponse = await _client.GetAsync("/networks/rr-testnet");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task NetworkConnect_UnknownContainer_IsNotFound()
    {
        // Connect used to be a flat 501. It is implemented for never-started containers now,
        // so an unknown container is a plain 404 here; the full status-code
        // matrix, including the 501 for a container that has already run, lives in
        // NetworkConnectRoutesTests against a fully wired host.
        var response = await _client!.PostAsync(
            "/networks/bridge/connect",
            new StringContent("""{"Container":"abc"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Volumes_CreateListInspectDelete()
    {
        var createRequest = new VolumeCreateRequest { Name = "rr-testvol" };
        var createJson = DockerJson.Serialize(createRequest);

        var createResponse = await _client!.PostAsync("/volumes/create", new StringContent(createJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await _client.GetAsync("/volumes");
        Assert.Contains("rr-testvol", await listResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var inspectResponse = await _client.GetAsync("/volumes/rr-testvol");
        Assert.Equal(HttpStatusCode.OK, inspectResponse.StatusCode);

        var deleteResponse = await _client.DeleteAsync("/volumes/rr-testvol");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var missingResponse = await _client.GetAsync("/volumes/rr-testvol");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task Swarm_ReturnsNotAcceptable_LikeANonSwarmDaemon()
    {
        var response = await _client!.GetAsync("/swarm");

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not a swarm manager", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_AreNotSwarmManager()
    {
        // Real dockerd routes /services through the same swarm-manager gate as /swarm itself,
        // so a non-swarm node 503s here too — not the generic 501.
        var response = await _client!.GetAsync("/services");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not a swarm manager", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_And_Grpc_ReturnPageNotFound()
    {
        var sessionResponse = await _client!.PostAsync("/session", content: null);
        var grpcResponse = await _client.PostAsync("/grpc", content: null);

        Assert.Equal(HttpStatusCode.NotFound, sessionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, grpcResponse.StatusCode);
        Assert.Contains("page not found", await sessionResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------

    private static async Task<MemoryStream> BuildTarContextAsync(string dockerfileContents)
    {
        var stream = new MemoryStream();
        await using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(dockerfileContents);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(bytes),
            };
            await writer.WriteEntryAsync(entry);
        }

        stream.Position = 0;
        return stream;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
