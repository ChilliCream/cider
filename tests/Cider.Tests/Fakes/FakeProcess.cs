using System.IO.Pipelines;
using System.Text;
using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

/// <summary>
/// A fake <see cref="IContainerProcess"/> with <see cref="Pipe"/>-backed stdio and a tiny shell
/// interpreter, enough for the command lines the container tests use:
/// <c>sh -c "echo out; echo err 1>&amp;2; exit 3"</c>, <c>cat</c>, <c>sleep N</c>, <c>exit N</c>,
/// <c>env</c>, <c>true</c>, <c>false</c>.
/// </summary>
public sealed class FakeProcess : IContainerProcess
{
    private static int _nextPid = 1000;

    private readonly Pipe _stdoutPipe = new();
    private readonly Pipe? _stderrPipe;
    private readonly Pipe? _stdinPipe;
    private readonly Stream _stdoutStream;
    private readonly Stream? _stderrStream;
    private readonly Stream? _stdinStream;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly IReadOnlyList<string> _argv;
    private readonly IReadOnlyList<string> _env;
    private int _killCode = 137;
    private int _disposed;

    /// <summary>Starts the fake process immediately, like the engine would.</summary>
    public FakeProcess(IReadOnlyList<string> argv, IReadOnlyList<string> env, bool tty, bool openStdin)
    {
        _argv = argv ?? [];
        _env = env ?? [];
        HasTty = tty;
        Pid = Interlocked.Increment(ref _nextPid);

        _stdoutStream = _stdoutPipe.Reader.AsStream();
        if (!tty)
        {
            _stderrPipe = new Pipe();
            _stderrStream = _stderrPipe.Reader.AsStream();
        }

        if (openStdin)
        {
            _stdinPipe = new Pipe();
            _stdinStream = _stdinPipe.Writer.AsStream();
        }

        Run = Task.Run(RunAsync);
    }

    /// <summary>The interpreter task; tests can await it to know the process finished writing.</summary>
    public Task Run { get; }

    /// <summary>The last <c>(cols, rows)</c> the daemon asked for.</summary>
    public (int Cols, int Rows)? LastResize { get; private set; }

    /// <summary>The signals the daemon delivered, in order.</summary>
    public List<string> Signals { get; } = [];

    /// <inheritdoc />
    public int? Pid { get; }

    /// <inheritdoc />
    public bool HasTty { get; }

    /// <inheritdoc />
    public Stream? Stdin => _stdinStream;

    /// <inheritdoc />
    public Stream Stdout => _stdoutStream;

    /// <inheritdoc />
    public Stream? Stderr => _stderrStream;

    /// <inheritdoc />
    public Task<int> Exited => _exited.Task;

    /// <inheritdoc />
    public async Task CloseStdinAsync()
    {
        if (_stdinPipe is not null)
        {
            await _stdinPipe.Writer.CompleteAsync();
        }
    }

    /// <inheritdoc />
    public Task ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        LastResize = (cols, rows);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task KillAsync(string signal, CancellationToken ct)
    {
        lock (Signals)
        {
            Signals.Add(signal);
        }

        _killCode = signal switch
        {
            "SIGTERM" or "TERM" or "15" => 143,
            "SIGINT" or "INT" or "2" => 130,
            "SIGQUIT" or "QUIT" or "3" => 131,
            _ => 137,
        };

        await _cts.CancelAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_exited.Task.IsCompleted)
        {
            await _cts.CancelAsync();
        }

        try
        {
            await Run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var exitCode = 0;
        try
        {
            exitCode = await ExecuteAsync(_argv, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            exitCode = _killCode;
        }
        catch (Exception)
        {
            exitCode = 1;
        }

        await _stdoutPipe.Writer.CompleteAsync();
        if (_stderrPipe is not null)
        {
            await _stderrPipe.Writer.CompleteAsync();
        }

        _exited.TrySetResult(exitCode);
    }

    private async Task<int> ExecuteAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (argv.Count == 0)
        {
            return 0;
        }

        var command = Path.GetFileName(argv[0]);
        if (command is "sh" or "bash" or "ash")
        {
            var dashC = -1;
            for (var i = 1; i < argv.Count; i++)
            {
                if (argv[i] == "-c")
                {
                    dashC = i;
                    break;
                }
            }

            if (dashC >= 0 && dashC + 1 < argv.Count)
            {
                return await RunScriptAsync(argv[dashC + 1], ct);
            }

            return 0;
        }

        return await RunCommandAsync([.. argv], ct);
    }

    private async Task<int> RunScriptAsync(string script, CancellationToken ct)
    {
        var exitCode = 0;
        foreach (var statement in SplitStatements(script))
        {
            ct.ThrowIfCancellationRequested();

            var tokens = Tokenize(statement);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (tokens[0] == "exit")
            {
                return tokens.Count > 1 && int.TryParse(tokens[1], out var code) ? code : 0;
            }

            exitCode = await RunCommandAsync(tokens, ct);
        }

        return exitCode;
    }

    private async Task<int> RunCommandAsync(List<string> tokens, CancellationToken ct)
    {
        var toStderr = false;
        var arguments = new List<string>();
        foreach (var token in tokens)
        {
            if (token is "1>&2" or ">&2" or "2>&1")
            {
                toStderr = token is not "2>&1";
                continue;
            }

            arguments.Add(token);
        }

        if (arguments.Count == 0)
        {
            return 0;
        }

        switch (arguments[0])
        {
            case "echo":
                await WriteAsync(string.Join(' ', arguments.Skip(1)) + "\n", toStderr, ct);
                return 0;

            case "printf":
                await WriteAsync(string.Join(' ', arguments.Skip(1)).Replace("\\n", "\n", StringComparison.Ordinal), toStderr, ct);
                return 0;

            case "sleep":
            {
                var seconds = arguments.Count > 1 && double.TryParse(arguments[1], out var value) ? value : 0;
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return 0;
            }

            case "exit":
                return arguments.Count > 1 && int.TryParse(arguments[1], out var exitCode) ? exitCode : 0;

            case "cat":
                await CatAsync(ct);
                return 0;

            case "env":
                foreach (var entry in _env)
                {
                    await WriteAsync(entry + "\n", toStderr, ct);
                }

                return 0;

            case "true":
                return 0;

            case "false":
                return 1;

            case "hostname":
                await WriteAsync("fake\n", toStderr, ct);
                return 0;

            case "ps":
                await WriteAsync("PID   USER     TIME  COMMAND\n    1 root      0:00 /bin/sh\n", toStderr, ct);
                return 0;

            default:
                await WriteAsync($"sh: {arguments[0]}: not found\n", toStderr: true, ct);
                return 127;
        }
    }

    private async Task CatAsync(CancellationToken ct)
    {
        if (_stdinPipe is null)
        {
            return;
        }

        while (true)
        {
            var result = await _stdinPipe.Reader.ReadAsync(ct);
            foreach (var segment in result.Buffer)
            {
                await _stdoutPipe.Writer.WriteAsync(segment, ct);
                await _stdoutPipe.Writer.FlushAsync(ct);
            }

            _stdinPipe.Reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }
    }

    private async Task WriteAsync(string text, bool toStderr, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var writer = toStderr && _stderrPipe is not null ? _stderrPipe.Writer : _stdoutPipe.Writer;
        await writer.WriteAsync(bytes, ct);
        await writer.FlushAsync(ct);
    }

    private static List<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                current.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    current.Append(c);
                    break;

                case ';' or '\n':
                    statements.Add(current.ToString());
                    current.Clear();
                    break;

                case '&' when i + 1 < script.Length && script[i + 1] == '&':
                    statements.Add(current.ToString());
                    current.Clear();
                    i++;
                    break;

                default:
                    current.Append(c);
                    break;
            }
        }

        statements.Add(current.ToString());
        return statements;
    }

    private static List<string> Tokenize(string statement)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var any = false;

        foreach (var c in statement)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || any)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0 || any)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
