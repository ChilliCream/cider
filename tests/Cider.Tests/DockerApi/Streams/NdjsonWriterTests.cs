using System.Text;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Xunit;

namespace Cider.Tests.DockerApi.Streams;

public class NdjsonWriterTests
{
    [Fact]
    public async Task Writes_one_json_object_per_line()
    {
        using var buffer = new MemoryStream();
        await using (var writer = new NdjsonWriter(buffer))
        {
            await writer.WriteAsync(new JsonMessage { Status = "Pulling from library/alpine", Id = "latest" });
            await writer.WriteAsync(new JsonMessage { Status = "Status: Downloaded newer image for alpine:latest" });
        }

        var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("""{"status":"Pulling from library/alpine","id":"latest"}""", lines[0]);
        Assert.Equal("""{"status":"Status: Downloaded newer image for alpine:latest"}""", lines[1]);
    }

    [Fact]
    public async Task Concurrent_writes_do_not_interleave()
    {
        using var buffer = new MemoryStream();
        await using var writer = new NdjsonWriter(buffer);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            writer.WriteAsync(new JsonMessage { Status = "line", Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture) })));

        var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(50, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("""{"status":"line","id":""", line, StringComparison.Ordinal));
    }
}
