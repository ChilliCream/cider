using System.Diagnostics;
using System.Text;
using Cider.AppleContainer.Xpc;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="FollowingFileStream"/> (task cider-ede.9) against real temp files — tail, follow, and
/// truncation-reset, the three behaviours a bare read loop over <c>containerLogs</c>' fd would not
/// give for free (docs/spikes/xpc/03-limitations-audit-1.3.md "Logs merged" row: the runtime
/// <c>O_TRUNC</c>s the file on every container start).
/// </summary>
public sealed class FollowingFileStreamTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cider-following-{Guid.NewGuid():n}.log");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private FollowingFileStream OpenStream(bool follow, int? tail, TimeSpan? pollInterval = null) =>
        new(File.OpenHandle(_path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite), follow, tail, pollInterval);

    [Fact]
    public async Task Tail_3_on_a_10_line_file_yields_the_last_3_lines()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line{i}").ToArray();
        await File.WriteAllTextAsync(_path, string.Join('\n', lines) + "\n");

        await using var stream = OpenStream(follow: false, tail: 3);
        var text = await ReadToEndAsync(stream);

        var got = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["line8", "line9", "line10"], got);
    }

    [Fact]
    public async Task Tail_larger_than_the_file_yields_the_whole_file()
    {
        await File.WriteAllTextAsync(_path, "a\nb\n");

        await using var stream = OpenStream(follow: false, tail: 100);
        var text = await ReadToEndAsync(stream);

        Assert.Equal("a\nb\n", text);
    }

    [Fact]
    public async Task Tail_on_a_file_whose_last_line_has_no_trailing_newline()
    {
        await File.WriteAllTextAsync(_path, "l1\nl2\nl3");

        await using var stream = OpenStream(follow: false, tail: 1);
        var text = await ReadToEndAsync(stream);

        Assert.Equal("l3", text);
    }

    [Fact]
    public async Task Non_following_read_ends_at_EOF()
    {
        await File.WriteAllTextAsync(_path, "one line\n");

        await using var stream = OpenStream(follow: false, tail: null);
        var buffer = new byte[64];
        var first = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal("one line\n", Encoding.UTF8.GetString(buffer, 0, first));

        var second = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Follow_sees_appended_bytes_within_200ms()
    {
        await File.WriteAllTextAsync(_path, "");

        await using var stream = OpenStream(follow: true, tail: null, pollInterval: TimeSpan.FromMilliseconds(20));
        var buffer = new byte[64];
        var readTask = stream.ReadAsync(buffer, CancellationToken.None).AsTask();

        var sw = Stopwatch.StartNew();
        await File.AppendAllTextAsync(_path, "hello\n");

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(readTask, completed);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"took {sw.Elapsed}");

        var read = await readTask;
        Assert.Equal("hello\n", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task Truncation_resets_the_read_position_to_the_start()
    {
        await File.WriteAllTextAsync(_path, "aaaa\n");

        await using var stream = OpenStream(follow: true, tail: null, pollInterval: TimeSpan.FromMilliseconds(20));
        var buffer = new byte[64];

        var firstRead = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal("aaaa\n", Encoding.UTF8.GetString(buffer, 0, firstRead));

        // The runtime O_TRUNCs stdio.log on every container restart — simulate that with a shorter
        // file at the same path.
        await File.WriteAllTextAsync(_path, "bb\n");

        var secondRead = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal("bb\n", Encoding.UTF8.GetString(buffer, 0, secondRead));
    }

    [Fact]
    public async Task Stop_ends_an_in_progress_follow_without_waiting_for_growth()
    {
        await File.WriteAllTextAsync(_path, "");

        await using var stream = OpenStream(follow: true, tail: null, pollInterval: TimeSpan.FromMilliseconds(20));
        var buffer = new byte[64];
        var readTask = stream.ReadAsync(buffer, CancellationToken.None).AsTask();

        await Task.Delay(50);
        stream.Stop();

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(readTask, completed);
        Assert.Equal(0, await readTask);
    }

    [Fact]
    public async Task Dispose_closes_the_underlying_handle()
    {
        await File.WriteAllTextAsync(_path, "x\n");

        var stream = OpenStream(follow: false, tail: null);
        await stream.DisposeAsync();

        // The handle is closed, so a stray post-dispose read hits the same "no more data" path as a
        // real EOF (ReadAsync's IOException/ObjectDisposedException guard) rather than throwing.
        Assert.Equal(0, await stream.ReadAsync(new byte[8], CancellationToken.None));
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, CancellationToken.None);
            if (read <= 0)
            {
                break;
            }

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
