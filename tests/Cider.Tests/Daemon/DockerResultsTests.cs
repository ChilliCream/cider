using System.Text;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Daemon.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cider.Tests.Daemon;

public sealed class DockerResultsTests
{
    /// <summary>An <see cref="IResult"/> needs logging services to execute.</summary>
    private static DefaultHttpContext NewContext(MemoryStream body)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = body },
        };
    }

    [Fact]
    public async Task Json_writes_pascal_case_with_the_json_content_type()
    {
        var body = new MemoryStream();
        var context = NewContext(body);

        await DockerResults.Json(new ContainerCreateResponse { Id = "abc" }).ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("""{"Id":"abc","Warnings":[]}""", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Error_renders_dockers_message_envelope()
    {
        var body = new MemoryStream();
        var context = NewContext(body);

        await DockerResults.Error(Cider.Core.DockerApi.DockerErrors.NoSuchContainer("nope")).ExecuteAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("""{"message":"No such container: nope"}""", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task BeginNdjson_deferStart_writes_nothing_until_the_first_message()
    {
        var body = new MemoryStream();
        var context = NewContext(body);

        var writer = await DockerResults.BeginNdjsonAsync(context.Response, CancellationToken.None, deferStart: true);

        // The headers are prepared but nothing has actually gone out yet — a handler that throws
        // here instead of writing can still turn into a normal (non-200) Docker error response.
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(0, body.Length);

        await writer.WriteAsync(new JsonMessage { Status = "hi" });

        Assert.True(body.Length > 0);
    }

    [Fact]
    public void Stream_content_type_follows_the_tty_flag()
    {
        Assert.Equal("application/vnd.docker.raw-stream", DockerResults.StreamContentType(true));
        Assert.Equal("application/vnd.docker.multiplexed-stream", DockerResults.StreamContentType(false));
    }

    [Fact]
    public async Task WriteChunk_frames_non_tty_output()
    {
        using var stream = new MemoryStream();
        await DockerResults.WriteChunkAsync(stream, StdStream.Stderr, "hi\n"u8.ToArray(), tty: false, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(11, bytes.Length);
        Assert.Equal(2, bytes[0]);
        Assert.Equal(3, bytes[7]);
        Assert.Equal("hi\n", Encoding.UTF8.GetString(bytes, 8, 3));
    }

    [Fact]
    public async Task WriteChunk_leaves_tty_output_raw()
    {
        using var stream = new MemoryStream();
        await DockerResults.WriteChunkAsync(stream, StdStream.Stdout, "hi\n"u8.ToArray(), tty: true, CancellationToken.None);

        Assert.Equal("hi\n", Encoding.UTF8.GetString(stream.ToArray()));
    }
}
