using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Time;

namespace Cider.Core.Logs;

/// <summary>One captured chunk of container output.</summary>
/// <param name="Stream">Which standard stream the chunk came from.</param>
/// <param name="Data">The raw bytes.</param>
/// <param name="Time">When the daemon captured it.</param>
public sealed record LogEntry(StdStream Stream, ReadOnlyMemory<byte> Data, DateTimeOffset Time);

/// <summary>The <c>GET /containers/{id}/logs</c> query, in domain terms.</summary>
public sealed record LogReadOptions
{
    /// <summary>
    /// Number of trailing entries to return. <c>null</c> means "unset" (all entries, matching
    /// Docker's <c>tail=all</c>/absent behaviour); <c>0</c> means none, matching Docker's
    /// <c>tail=0</c>; any other non-negative value means the last N entries.
    /// </summary>
    public int? Tail { get; init; }

    /// <summary>Only entries at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only entries at or before this time.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Keep streaming until the writer completes or the caller cancels.</summary>
    public bool Follow { get; init; }

    /// <summary>Include stdout.</summary>
    public bool Stdout { get; init; } = true;

    /// <summary>Include stderr.</summary>
    public bool Stderr { get; init; } = true;
}

/// <summary>Appends captured container output; <see cref="Complete"/> releases everyone following it.</summary>
public interface ILogWriter : IAsyncDisposable
{
    /// <summary>Appends one chunk (split into json-file lines on newlines).</summary>
    ValueTask WriteAsync(StdStream stream, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Signals that no more output will arrive, ending every follower.</summary>
    void Complete();
}

/// <summary>
/// Docker's <c>json-file</c> log driver: one <c>&lt;id&gt;.jsonl</c> file per container holding
/// <c>{"log":"…","stream":"stdout|stderr","time":"RFC3339Nano"}</c> lines, plus an in-memory
/// broadcast so <c>logs --follow</c> does not have to poll the file.
/// </summary>
public sealed partial class LogStore
{
    private const int FollowerCapacity = 4096;

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, ContainerLog> _logs = new(StringComparer.Ordinal);

    /// <summary>Creates the store; <paramref name="logsDir"/> is created if missing.</summary>
    public LogStore(string logsDir, long maxBytesPerContainer)
    {
        ArgumentException.ThrowIfNullOrEmpty(logsDir);
        _directory = logsDir;
        _maxBytes = maxBytesPerContainer > 0 ? maxBytesPerContainer : long.MaxValue;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Path of the capture file for <paramref name="containerId"/>.</summary>
    public string PathFor(string containerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        return Path.Combine(_directory, containerId + ".jsonl");
    }

    /// <summary><c>true</c> when this container has a capture file to read.</summary>
    public bool HasCapture(string containerId) => File.Exists(PathFor(containerId));

    /// <summary>
    /// Bytes currently on disk for this container's capture file, or 0 if it has none. Used for
    /// an honest (if partial — it does not include the container's writable layer, which Apple's
    /// runtime does not report a size for) `SpaceReclaimed` figure on <c>containers/prune</c>.
    /// </summary>
    public long SizeOnDisk(string containerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        var path = PathFor(containerId);
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Opens an appending writer; a previous writer for the same container is superseded.</summary>
    public ILogWriter OpenWriter(string containerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        var log = _logs.GetOrAdd(containerId, _ => new ContainerLog());
        return new Writer(this, log, PathFor(containerId), _maxBytes);
    }

    /// <summary>Reads the capture, then (when following) the live stream.</summary>
    public async IAsyncEnumerable<LogEntry> ReadAsync(
        string containerId,
        LogReadOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentNullException.ThrowIfNull(options);

        var log = _logs.GetOrAdd(containerId, _ => new ContainerLog());
        var path = PathFor(containerId);

        List<LogEntry> history;
        Channel<LogEntry>? follower = null;

        // The writer appends under the same gate, so the snapshot and the subscription cannot race.
        await log.Gate.WaitAsync(ct);
        try
        {
            history = ReadFile(path, options);
            if (options.Follow && !log.Completed)
            {
                follower = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(FollowerCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
                log.Followers.Add(follower);
            }
        }
        finally
        {
            log.Gate.Release();
        }

        try
        {
            foreach (var entry in history)
            {
                yield return entry;
            }

            if (follower is null)
            {
                yield break;
            }

            while (true)
            {
                LogEntry? next;
                try
                {
                    next = await follower.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (ChannelClosedException)
                {
                    yield break;
                }

                if (!Wanted(next, options))
                {
                    if (options.Until is not null && next.Time > options.Until.Value)
                    {
                        yield break;
                    }

                    continue;
                }

                yield return next;
            }
        }
        finally
        {
            if (follower is not null)
            {
                log.Gate.Wait(CancellationToken.None);
                try
                {
                    log.Followers.Remove(follower);
                }
                finally
                {
                    log.Gate.Release();
                }
            }
        }
    }

    /// <summary>Drops the capture file and releases every follower.</summary>
    public void Delete(string containerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);

        if (_logs.TryRemove(containerId, out var log))
        {
            log.CompleteAll();
        }

        var path = PathFor(containerId);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A locked or already-removed file is not worth failing a container removal over.
        }
    }

    private static bool Wanted(LogEntry entry, LogReadOptions options)
    {
        if (entry.Stream == StdStream.Stdout && !options.Stdout)
        {
            return false;
        }

        if (entry.Stream == StdStream.Stderr && !options.Stderr)
        {
            return false;
        }

        if (options.Since is not null && entry.Time < options.Since.Value)
        {
            return false;
        }

        if (options.Until is not null && entry.Time > options.Until.Value)
        {
            return false;
        }

        return true;
    }

    private static List<LogEntry> ReadFile(string path, LogReadOptions options)
    {
        var entries = new List<LogEntry>();
        if (!File.Exists(path))
        {
            return entries;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var entry = Parse(line);
            if (entry is not null && Wanted(entry, options))
            {
                entries.Add(entry);
            }
        }

        // `options.Tail is not null` (rather than `is > 0`) is deliberate: Tail == 0 must also
        // filter down to nothing, matching `docker logs --tail 0`. Only a genuinely unset Tail
        // (null) means "everything" — QueryValues.Tail already collapses "all", absent, and any
        // malformed/negative value to null before this ever sees them, so 0 here is always a
        // real, intentional zero from the caller.
        if (options.Tail is not null && entries.Count > options.Tail.Value)
        {
            entries.RemoveRange(0, entries.Count - options.Tail.Value);
        }

        return entries;
    }

    private static LogEntry? Parse(string line)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(line, LineTypeInfo);
            if (parsed is null)
            {
                return null;
            }

            var stream = string.Equals(parsed.Stream, "stderr", StringComparison.Ordinal)
                ? StdStream.Stderr
                : StdStream.Stdout;
            var time = DockerTime.TryParse(parsed.Time, out var parsedTime) ? parsedTime : DateTimeOffset.UnixEpoch;
            return new LogEntry(stream, Encoding.UTF8.GetBytes(parsed.Log ?? ""), time);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // json-file lines are written and read through the source-generated contract below. The
    // encoder has no [JsonSourceGenerationOptions] counterpart, so it is applied to a copy of the
    // context's options -- and it matters here: log payloads are arbitrary program output and
    // dockerd writes them as raw UTF-8 rather than \uXXXX escapes.
    private static readonly JsonTypeInfo<LogLine> LineTypeInfo = CreateLineTypeInfo();

    private static JsonTypeInfo<LogLine> CreateLineTypeInfo()
    {
        var options = new JsonSerializerOptions(LineJsonContext.Default.Options)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<LogLine>)options.GetTypeInfo(typeof(LogLine));
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(LogLine))]
    private sealed partial class LineJsonContext : JsonSerializerContext;

    private sealed class LogLine
    {
        [JsonPropertyName("log")]
        public string? Log { get; set; }

        [JsonPropertyName("stream")]
        public string? Stream { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }

    private sealed class ContainerLog
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<Channel<LogEntry>> Followers { get; } = [];

        public bool Completed { get; set; }

        public void CompleteAll()
        {
            Gate.Wait(CancellationToken.None);
            try
            {
                Completed = true;
                foreach (var follower in Followers)
                {
                    follower.Writer.TryComplete();
                }

                Followers.Clear();
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    private sealed class Writer : ILogWriter
    {
        private readonly LogStore _store;
        private readonly ContainerLog _log;
        private readonly string _path;
        private readonly long _maxBytes;
        private FileStream? _file;
        private bool _disposed;

        public Writer(LogStore store, ContainerLog log, string path, long maxBytes)
        {
            _store = store;
            _log = log;
            _path = path;
            _maxBytes = maxBytes;

            log.Gate.Wait(CancellationToken.None);
            try
            {
                log.Completed = false;
            }
            finally
            {
                log.Gate.Release();
            }
        }

        public async ValueTask WriteAsync(StdStream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (data.IsEmpty || _disposed)
            {
                return;
            }

            await _log.Gate.WaitAsync(ct);
            try
            {
                // Stamped under the gate: two concurrent stdio pumps could otherwise append a line
                // whose timestamp is older than the line already above it, and both the `until`
                // early-break in ReadAsync and `docker logs --since/--until` assume monotonic time.
                var time = DateTimeOffset.UtcNow;
                if (_file is null)
                {
                    _file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _file.Seek(0, SeekOrigin.End);
                }

                // json-file stores one line per output line, so timestamps line up with what the user sees.
                foreach (var slice in SplitLines(data))
                {
                    if (_file.Length + slice.Length + 64 > _maxBytes)
                    {
                        _file.SetLength(0);
                        _file.Seek(0, SeekOrigin.Begin);
                    }

                    var text = Encoding.UTF8.GetString(slice.Span);
                    var json = JsonSerializer.Serialize(
                        new LogLine { Log = text, Stream = stream == StdStream.Stderr ? "stderr" : "stdout", Time = DockerTime.Format(time) },
                        LineTypeInfo);
                    var bytes = Encoding.UTF8.GetBytes(json + "\n");
                    await _file.WriteAsync(bytes, ct);

                    var entry = new LogEntry(stream, slice.ToArray(), time);
                    foreach (var follower in _log.Followers)
                    {
                        follower.Writer.TryWrite(entry);
                    }
                }

                await _file.FlushAsync(ct);
            }
            finally
            {
                _log.Gate.Release();
            }
        }

        public void Complete() => _log.CompleteAll();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Complete();

            if (_file is not null)
            {
                await _file.DisposeAsync();
                _file = null;
            }

            GC.KeepAlive(_store);
        }

        private static List<ReadOnlyMemory<byte>> SplitLines(ReadOnlyMemory<byte> data)
        {
            var slices = new List<ReadOnlyMemory<byte>>();
            var start = 0;
            var span = data.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] == (byte)'\n')
                {
                    slices.Add(data[start..(i + 1)]);
                    start = i + 1;
                }
            }

            if (start < data.Length)
            {
                slices.Add(data[start..]);
            }

            return slices;
        }
    }
}
