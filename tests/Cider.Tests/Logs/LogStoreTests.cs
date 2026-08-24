using System.Text;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Logs;
using Xunit;

namespace Cider.Tests.Logs;

public sealed class LogStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cider-tests", Guid.NewGuid().ToString("n")[..12]);

    [Fact]
    public async Task Written_entries_are_read_back_in_order_with_their_stream()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using (var writer = store.OpenWriter("c1"))
        {
            await writer.WriteAsync(StdStream.Stdout, Utf8("out\n"), default);
            await writer.WriteAsync(StdStream.Stderr, Utf8("err\n"), default);
        }

        var entries = await ReadAsync(store, "c1", new LogReadOptions());

        Assert.Equal(2, entries.Count);
        Assert.Equal(StdStream.Stdout, entries[0].Stream);
        Assert.Equal("out\n", Text(entries[0]));
        Assert.Equal(StdStream.Stderr, entries[1].Stream);
        Assert.Equal("err\n", Text(entries[1]));
        Assert.True(File.Exists(store.PathFor("c1")));
    }

    [Fact]
    public async Task Chunks_are_split_into_one_entry_per_line()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using (var writer = store.OpenWriter("c1"))
        {
            await writer.WriteAsync(StdStream.Stdout, Utf8("one\ntwo\nthree"), default);
        }

        var entries = await ReadAsync(store, "c1", new LogReadOptions());

        Assert.Equal(["one\n", "two\n", "three"], entries.Select(Text));
    }

    [Fact]
    public async Task Tail_returns_the_last_entries_and_stream_selection_filters()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using (var writer = store.OpenWriter("c1"))
        {
            await writer.WriteAsync(StdStream.Stdout, Utf8("1\n2\n3\n4\n"), default);
            await writer.WriteAsync(StdStream.Stderr, Utf8("e\n"), default);
        }

        var tail = await ReadAsync(store, "c1", new LogReadOptions { Tail = 2 });
        Assert.Equal(["4\n", "e\n"], tail.Select(Text));

        var stdoutOnly = await ReadAsync(store, "c1", new LogReadOptions { Stderr = false });
        Assert.Equal(["1\n", "2\n", "3\n", "4\n"], stdoutOnly.Select(Text));

        var stderrOnly = await ReadAsync(store, "c1", new LogReadOptions { Stdout = false });
        Assert.Equal(["e\n"], stderrOnly.Select(Text));
    }

    [Fact]
    public async Task Tail_zero_returns_nothing_while_unset_tail_returns_everything()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using (var writer = store.OpenWriter("c1"))
        {
            await writer.WriteAsync(StdStream.Stdout, Utf8("1\n2\n3\n"), default);
        }

        // tail=0 (Docker's `docker logs --tail 0`): no lines at all, not "unset".
        var tailZero = await ReadAsync(store, "c1", new LogReadOptions { Tail = 0 });
        Assert.Empty(tailZero);

        // Tail left unset — what QueryValues.Tail returns for an absent query string, "tail=all",
        // or a malformed/negative value — means everything, same as no filter at all.
        var tailUnset = await ReadAsync(store, "c1", new LogReadOptions { Tail = null });
        Assert.Equal(["1\n", "2\n", "3\n"], tailUnset.Select(Text));

        // tail=1: the boundary just above zero still behaves as "last N".
        var tailOne = await ReadAsync(store, "c1", new LogReadOptions { Tail = 1 });
        Assert.Equal(["3\n"], tailOne.Select(Text));
    }

    [Fact]
    public async Task Since_and_until_bound_the_range()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using var writer = store.OpenWriter("c1");

        await writer.WriteAsync(StdStream.Stdout, Utf8("early\n"), default);
        await Task.Delay(30);
        var cut = DateTimeOffset.UtcNow;
        await Task.Delay(30);
        await writer.WriteAsync(StdStream.Stdout, Utf8("late\n"), default);
        writer.Complete();

        var since = await ReadAsync(store, "c1", new LogReadOptions { Since = cut });
        Assert.Equal(["late\n"], since.Select(Text));

        var until = await ReadAsync(store, "c1", new LogReadOptions { Until = cut });
        Assert.Equal(["early\n"], until.Select(Text));
    }

    [Fact]
    public async Task Follow_streams_new_entries_and_ends_on_Complete()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        var writer = store.OpenWriter("c1");
        await writer.WriteAsync(StdStream.Stdout, Utf8("first\n"), default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entries = new List<LogEntry>();
        var reader = Task.Run(async () =>
        {
            await foreach (var entry in store.ReadAsync("c1", new LogReadOptions { Follow = true }, cts.Token))
            {
                lock (entries)
                {
                    entries.Add(entry);
                }
            }
        });

        await WaitUntil(() => Count(entries) == 1);
        await writer.WriteAsync(StdStream.Stdout, Utf8("second\n"), default);
        await WaitUntil(() => Count(entries) == 2);

        writer.Complete();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first\n", "second\n"], entries.Select(Text));
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task Follow_on_a_completed_container_returns_immediately()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using (var writer = store.OpenWriter("c1"))
        {
            await writer.WriteAsync(StdStream.Stdout, Utf8("done\n"), default);
            writer.Complete();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entries = await ReadAsync(store, "c1", new LogReadOptions { Follow = true }, cts.Token);

        Assert.Equal(["done\n"], entries.Select(Text));
    }

    [Fact]
    public async Task The_file_is_truncated_when_it_exceeds_the_cap()
    {
        var store = new LogStore(_directory, 256);
        await using (var writer = store.OpenWriter("c1"))
        {
            for (var i = 0; i < 50; i++)
            {
                await writer.WriteAsync(StdStream.Stdout, Utf8($"line {i}\n"), default);
            }
        }

        Assert.True(new FileInfo(store.PathFor("c1")).Length <= 256);
        var entries = await ReadAsync(store, "c1", new LogReadOptions());
        Assert.NotEmpty(entries);
        Assert.Equal("line 49\n", Text(entries[^1]));
    }

    [Fact]
    public async Task Delete_removes_the_capture()
    {
        var store = new LogStore(_directory, 1024 * 1024);
        await using (var writer = store.OpenWriter("c1"))
        {
            await writer.WriteAsync(StdStream.Stdout, Utf8("x\n"), default);
        }

        Assert.True(store.HasCapture("c1"));
        store.Delete("c1");
        Assert.False(store.HasCapture("c1"));
        Assert.Empty(await ReadAsync(store, "c1", new LogReadOptions()));
    }

    private static async Task<List<LogEntry>> ReadAsync(LogStore store, string id, LogReadOptions options, CancellationToken ct = default)
    {
        var entries = new List<LogEntry>();
        await foreach (var entry in store.ReadAsync(id, options, ct))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static int Count(List<LogEntry> entries)
    {
        lock (entries)
        {
            return entries.Count;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !condition())
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private static ReadOnlyMemory<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(LogEntry entry) => Encoding.UTF8.GetString(entry.Data.Span);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
