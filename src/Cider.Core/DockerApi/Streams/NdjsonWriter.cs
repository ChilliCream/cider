using Cider.Core.DockerApi.Json;

namespace Cider.Core.DockerApi.Streams;

/// <summary>
/// Writes Docker's newline-delimited JSON streams (pull/build/load progress, <c>/events</c>):
/// one compact JSON object per line, flushed after every line. Safe for concurrent writers.
/// </summary>
public sealed class NdjsonWriter : IAsyncDisposable, IDisposable
{
    private static readonly byte[] Newline = "\n"u8.ToArray();

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public NdjsonWriter(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Serializes <paramref name="message"/> with <see cref="DockerJson.Options"/>, appends '\n' and flushes.</summary>
    public async Task WriteAsync<T>(T message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await WriteLineAsync(DockerJson.SerializeToUtf8Bytes(message), ct);
    }

    /// <summary>Writes an already-serialized JSON line.</summary>
    public Task WriteRawAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WriteLineAsync(utf8Json, ct);
    }

    /// <summary>
    /// Emits the JSON and its terminating newline as ONE write, then flushes. Two writes let
    /// Kestrel frame the newline as a chunk of its own, and strict incremental NDJSON parsers
    /// (docker-py's, notably) treat a chunk that does not end a JSON value as a protocol error.
    /// </summary>
    private async Task WriteLineAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken ct)
    {
        var buffer = new byte[utf8Json.Length + Newline.Length];
        utf8Json.CopyTo(buffer);
        buffer[utf8Json.Length] = (byte)'\n';

        await _gate.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(buffer, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
