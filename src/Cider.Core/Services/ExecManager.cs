using System.Collections.Concurrent;
using System.Threading.Channels;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>Docker's <c>/exec</c> endpoints. Exec instances live in memory only, exactly like Docker's.</summary>
public sealed class ExecManager
{
    private const int PumpBufferSize = 32 * 1024;

    /// <summary>How long the stdio pumps get to drain the process' output after it exited.</summary>
    private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a container started a <see cref="RuntimeErrorReason.ContainerNotRunning"/>
    /// failure from the runtime is treated as a start/exec race (Apple container 1.2.2 has one, see
    /// docs/apple-container-notes.md §12) rather than a genuine "the container really is stopped"
    /// error worth failing fast on.
    /// </summary>
    private static readonly TimeSpan ExecRaceWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ExecRaceBackoff = TimeSpan.FromMilliseconds(300);
    private const int ExecRaceMaxAttempts = 10;

    private readonly IContainerRuntime _runtime;
    private readonly ContainerManager _containers;
    private readonly EventBus _events;
    private readonly ILogger<ExecManager> _logger;
    private readonly ConcurrentDictionary<string, ExecRecord> _execs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IContainerProcess> _processes = new(StringComparer.Ordinal);

    /// <summary>Creates the manager.</summary>
    public ExecManager(IContainerRuntime runtime, ContainerManager containers, EventBus events, ILogger<ExecManager> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary><c>POST /containers/{id}/exec</c>; 409 when the container is not running.</summary>
    public async Task<ExecCreateResponse> CreateAsync(string containerIdOrName, ExecCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var container = await _containers.ResolveAsync(containerIdOrName, ct);
        if (!container.State.Running)
        {
            throw DockerErrors.Conflict($"Container {container.Id} is not running");
        }

        if (request.Cmd.Count == 0)
        {
            throw DockerErrors.BadParameter("No exec command specified");
        }

        var record = new ExecRecord
        {
            Id = DockerId.New(),
            ContainerId = container.Id,
            Request = request,
            Created = DateTimeOffset.UtcNow,
        };

        _execs[record.Id] = record;

        _events.Publish(DockerEvents.Container("exec_create: " + string.Join(' ', request.Cmd), container,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["execID"] = record.Id }));

        return new ExecCreateResponse { Id = record.Id };
    }

    /// <summary><c>POST /exec/{id}/start</c> with attach; 409 when the exec already ran.</summary>
    public async Task<ExecSession> StartAsync(string execId, bool tty, int[]? consoleSize, CancellationToken ct)
    {
        var record = Find(execId);
        var container = await _containers.ResolveAsync(record.ContainerId, ct);

        lock (record)
        {
            if (record.Started)
            {
                throw DockerErrors.Conflict($"Error: Exec command {record.Id} is already running");
            }

            record.Started = true;
        }

        var useTty = tty || record.Request.Tty;
        var process = await StartProcessAsync(record, container, useTty, ct);

        if (consoleSize is { Length: 2 } size && size[0] > 0 && size[1] > 0)
        {
            try
            {
                await process.ResizeAsync(size[1], size[0], ct);
            }
            catch (Exception ex) when (ex is RuntimeException or IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "initial resize of exec {Exec} failed", record.Id);
            }
        }

        var channel = Channel.CreateUnbounded<OutputChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var pumps = new List<Task> { PumpAsync(process.Stdout, StdStream.Stdout, channel, record.Id) };
        if (process.Stderr is { } stderr)
        {
            pumps.Add(PumpAsync(stderr, StdStream.Stderr, channel, record.Id));
        }

        var exited = CompleteAsync(record, container, process, pumps, channel);
        return new ExecSession(useTty, record.Request.AttachStdin, channel.Reader, process, exited);
    }

    /// <summary><c>POST /exec/{id}/start</c> with <c>Detach: true</c>.</summary>
    public async Task StartDetachedAsync(string execId, CancellationToken ct)
    {
        var record = Find(execId);
        var container = await _containers.ResolveAsync(record.ContainerId, ct);

        lock (record)
        {
            if (record.Started)
            {
                throw DockerErrors.Conflict($"Error: Exec command {record.Id} is already running");
            }

            record.Started = true;
        }

        var process = await StartProcessAsync(record, container, record.Request.Tty, ct);

        var channel = Channel.CreateUnbounded<OutputChunk>();
        var pumps = new List<Task> { DrainAsync(process.Stdout) };
        if (process.Stderr is { } stderr)
        {
            pumps.Add(DrainAsync(stderr));
        }

        _ = CompleteAsync(record, container, process, pumps, channel);
    }

    /// <summary><c>GET /exec/{id}/json</c>.</summary>
    public Task<ExecInspectResponse> InspectAsync(string execId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var record = Find(execId);
        var argv = record.Request.Cmd;

        return Task.FromResult(new ExecInspectResponse
        {
            ID = record.Id,
            Running = record.Running,
            ExitCode = record.Running ? null : record.ExitCode,
            ContainerID = record.ContainerId,
            DetachKeys = record.Request.DetachKeys ?? "",
            Pid = record.Pid,
            OpenStdin = record.Request.AttachStdin,
            OpenStdout = record.Request.AttachStdout,
            OpenStderr = record.Request.AttachStderr,
            CanRemove = false,
            ProcessConfig = new ProcessConfig
            {
                Privileged = record.Request.Privileged,
                User = record.Request.User,
                Tty = record.Request.Tty,
                Entrypoint = argv.Count > 0 ? argv[0] : "",
                Arguments = argv.Count > 1 ? [.. argv.Skip(1)] : [],
            },
        });
    }

    /// <summary><c>POST /exec/{id}/resize</c>; succeeds even before the process exists.</summary>
    public async Task ResizeAsync(string execId, int cols, int rows, CancellationToken ct)
    {
        var record = Find(execId);
        if (_processes.TryGetValue(record.Id, out var process))
        {
            try
            {
                await process.ResizeAsync(cols, rows, ct);
            }
            catch (Exception ex) when (ex is RuntimeException or IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "resize of exec {Exec} failed", record.Id);
            }
        }
    }

    /// <summary>Ids of the (still known) execs of one container, for <c>ContainerInspectResponse.ExecIDs</c>.</summary>
    public IReadOnlyList<string> ExecIdsFor(string containerId) =>
        [.. _execs.Values
            .Where(record => string.Equals(record.ContainerId, containerId, StringComparison.Ordinal))
            .Select(record => record.Id)];

    private ExecRecord Find(string execId)
    {
        if (string.IsNullOrEmpty(execId) || !_execs.TryGetValue(execId, out var record))
        {
            throw DockerErrors.NoSuchExec(execId ?? "");
        }

        return record;
    }

    private async Task<IContainerProcess> StartProcessAsync(ExecRecord record, ContainerRecord container, bool tty, CancellationToken ct)
    {
        var spec = new ExecSpec
        {
            Argv = record.Request.Cmd,
            Env = record.Request.Env ?? [],
            WorkingDir = string.IsNullOrEmpty(record.Request.WorkingDir) ? null : record.Request.WorkingDir,
            User = string.IsNullOrEmpty(record.Request.User) ? null : record.Request.User,
            Tty = tty,
            OpenStdin = record.Request.AttachStdin,
            Privileged = record.Request.Privileged,
        };

        var withinStartupWindow = container.State.StartedAt is { } startedAt &&
            DateTimeOffset.UtcNow - startedAt < ExecRaceWindow;

        IContainerProcess process;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                process = await _runtime.ExecAsync(container.RuntimeId, spec, ct);
                break;
            }
            catch (RuntimeException ex) when (withinStartupWindow &&
                attempt < ExecRaceMaxAttempts &&
                ex.IsContainerNotRunning)
            {
                _logger.LogDebug(
                    "exec on container {Container} raced its start (attempt {Attempt}/{Max}): {Message}",
                    container.Id, attempt, ExecRaceMaxAttempts, ex.Message);
                await Task.Delay(ExecRaceBackoff, ct);
            }
            catch (RuntimeException ex)
            {
                record.Started = false;
                throw ContainerManager.Translate(ex);
            }
        }

        record.Running = true;
        record.Pid = process.Pid ?? 0;
        _processes[record.Id] = process;

        _events.Publish(DockerEvents.Container("exec_start: " + string.Join(' ', record.Request.Cmd), container,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["execID"] = record.Id }));

        return process;
    }

    private async Task<int> CompleteAsync(
        ExecRecord record,
        ContainerRecord container,
        IContainerProcess process,
        List<Task> pumps,
        Channel<OutputChunk> channel)
    {
        var exitCode = -1;
        try
        {
            exitCode = await process.Exited;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "waiting for exec {Exec} failed", record.Id);
        }

        try
        {
            await Task.WhenAll(pumps).WaitAsync(PumpDrainTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("stdio pumps of exec {Exec} did not drain within {Timeout}; output may be truncated", record.Id, PumpDrainTimeout);
        }

        _logger.LogDebug("exec {Exec} exited with code {Code}", record.Id, exitCode);

        record.Running = false;
        record.ExitCode = exitCode;
        _processes.TryRemove(record.Id, out _);
        channel.Writer.TryComplete();

        _events.Publish(DockerEvents.Container("exec_die", container, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["execID"] = record.Id,
            ["exitCode"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));

        return exitCode;
    }

    private async Task PumpAsync(Stream source, StdStream stream, Channel<OutputChunk> channel, string execId)
    {
        var buffer = new byte[PumpBufferSize];
        var total = 0L;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0)
                {
                    _logger.LogDebug("exec {Exec} {Stream} reached EOF after {Total} bytes", execId, stream, total);
                    return;
                }

                total += read;
                if (!channel.Writer.TryWrite(new OutputChunk(stream, buffer.AsMemory(0, read).ToArray())))
                {
                    _logger.LogWarning("exec {Exec} dropped {Count} {Stream} bytes: the output channel was already completed", execId, read, stream);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The exec process went away; the exit handler reports the code.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "stdio pump of exec {Exec} failed", execId);
        }
    }

    private static async Task DrainAsync(Stream source)
    {
        var buffer = new byte[PumpBufferSize];
        try
        {
            while (await source.ReadAsync(buffer, CancellationToken.None) > 0)
            {
                // Detached execs still have to be read or the process blocks on a full pipe.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}

/// <summary>An attached exec: its output channel, its stdin and its exit code.</summary>
public sealed class ExecSession : IAsyncDisposable
{
    private readonly IContainerProcess _process;

    internal ExecSession(bool tty, bool openStdin, ChannelReader<OutputChunk> output, IContainerProcess process, Task<int> exited)
    {
        Tty = tty;
        OpenStdin = openStdin;
        Output = output;
        _process = process;
        Exited = exited;
    }

    /// <summary>Whether the exec runs on a pty (the client then gets a raw, unframed stream).</summary>
    public bool Tty { get; }

    /// <summary>Whether the client may write stdin.</summary>
    public bool OpenStdin { get; }

    /// <summary>Output chunks; completes when the exec process exits.</summary>
    public ChannelReader<OutputChunk> Output { get; }

    /// <summary>Completes with the exec's exit code.</summary>
    public Task<int> Exited { get; }

    /// <summary>Writes to the exec's stdin; a no-op when stdin was not attached.</summary>
    public async Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!OpenStdin || data.IsEmpty || _process.Stdin is not { } stdin)
        {
            return;
        }

        await stdin.WriteAsync(data, ct);
        await stdin.FlushAsync(ct);
    }

    /// <summary>Half-closes the exec's stdin.</summary>
    public Task CloseStdinAsync() => _process.CloseStdinAsync();

    /// <summary>Releases the held process.</summary>
    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
