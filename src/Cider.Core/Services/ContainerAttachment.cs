using System.Threading.Channels;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Runtime;

namespace Cider.Core.Services;

/// <summary>One chunk of output on its way to an attached client.</summary>
/// <param name="Stream">Which standard stream the chunk came from.</param>
/// <param name="Data">The raw bytes.</param>
public readonly record struct OutputChunk(StdStream Stream, ReadOnlyMemory<byte> Data);

/// <summary>The <c>POST /containers/{id}/attach</c> query, in domain terms.</summary>
public sealed record AttachOptions
{
    /// <summary>Forward the client's stdin into the container.</summary>
    public bool Stdin { get; init; }

    /// <summary>Deliver stdout.</summary>
    public bool Stdout { get; init; } = true;

    /// <summary>Deliver stderr.</summary>
    public bool Stderr { get; init; } = true;

    /// <summary>Replay the captured log before the live stream.</summary>
    public bool Logs { get; init; }

    /// <summary>Deliver live output (when <c>false</c> only the replay is delivered).</summary>
    public bool Stream { get; init; } = true;

    /// <summary>The client's detach key sequence (passed through; the daemon does not interpret it).</summary>
    public string? DetachKeys { get; init; }
}

/// <summary>
/// A live attachment to a container's stdio. It can be created before the container starts — the
/// manager binds it to the process as soon as one exists.
/// </summary>
public sealed class ContainerAttachment : IAsyncDisposable
{
    private readonly Channel<OutputChunk> _channel;
    private readonly Func<IContainerProcess?> _process;
    private readonly Action<ContainerAttachment> _onDispose;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Serialises stdin so the pre-start buffer is replayed ahead of any later write.</summary>
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private readonly List<byte[]> _pendingStdin = [];
    private bool _stdinBound;
    private bool _closePending;
    private int _disposed;

    internal ContainerAttachment(
        bool tty,
        AttachOptions options,
        Channel<OutputChunk> channel,
        Func<IContainerProcess?> process,
        Action<ContainerAttachment> onDispose)
    {
        Tty = tty;
        Options = options;
        _channel = channel;
        _process = process;
        _onDispose = onDispose;
    }

    /// <summary>Whether the container runs on a pty (the client then gets a raw, unframed stream).</summary>
    public bool Tty { get; internal set; }

    /// <summary>Output chunks; the reader completes once the container process has exited.</summary>
    public ChannelReader<OutputChunk> Output => _channel.Reader;

    /// <summary>Completes when the container process exits.</summary>
    public Task Exited => _exited.Task;

    internal AttachOptions Options { get; }

    internal ChannelWriter<OutputChunk> Writer => _channel.Writer;

    /// <summary>
    /// Writes to the container's stdin; a no-op when stdin was not attached. An attachment made
    /// before the container starts has no process yet — `docker run -i` attaches first and its
    /// stdin copier runs immediately, so a piped run writes (and often half-closes) before the
    /// start request even lands. That input is held until <see cref="BindStdinAsync"/> replays it,
    /// the way dockerd's stdin fifo holds it.
    /// </summary>
    public async Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!Options.Stdin || data.IsEmpty)
        {
            return;
        }

        await _stdinGate.WaitAsync(ct);
        try
        {
            if (!_stdinBound)
            {
                _pendingStdin.Add(data.ToArray());
                return;
            }

            if (_process() is not { Stdin: { } stdin })
            {
                return;
            }

            await stdin.WriteAsync(data, ct);
            await stdin.FlushAsync(ct);
        }
        finally
        {
            _stdinGate.Release();
        }
    }

    /// <summary>
    /// Half-closes the container's stdin (Docker's <c>CloseWrite</c>). Before the container starts
    /// the half-close is recorded and applied by <see cref="BindStdinAsync"/>, so a client that
    /// pipes its input and closes before the start still ends the container's stdin.
    /// </summary>
    public async Task CloseStdinAsync()
    {
        await _stdinGate.WaitAsync();
        try
        {
            if (!_stdinBound)
            {
                _closePending = true;
                return;
            }

            if (_process() is { } process)
            {
                await process.CloseStdinAsync();
            }
        }
        finally
        {
            _stdinGate.Release();
        }
    }

    /// <summary>Detaches; the container keeps running.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
            _onDispose(this);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hands the attachment its process: everything the client wrote before the container started
    /// is replayed in order, and a half-close it already asked for is applied. Called once, either
    /// when the attachment is made on an already-running container or when the start binds it.
    /// </summary>
    internal async Task BindStdinAsync()
    {
        if (!Options.Stdin)
        {
            _stdinBound = true;
            return;
        }

        await _stdinGate.WaitAsync();
        try
        {
            if (_stdinBound)
            {
                return;
            }

            _stdinBound = true;
            if (_process() is not { } process)
            {
                return;
            }

            if (process.Stdin is { } stdin)
            {
                foreach (var chunk in _pendingStdin)
                {
                    await stdin.WriteAsync(chunk, CancellationToken.None);
                }

                await stdin.FlushAsync(CancellationToken.None);
            }

            _pendingStdin.Clear();

            if (_closePending)
            {
                await process.CloseStdinAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The container went away between the start and the replay.
        }
        finally
        {
            _stdinGate.Release();
        }
    }

    internal void SignalExit()
    {
        _exited.TrySetResult();
        _channel.Writer.TryComplete();
    }
}
