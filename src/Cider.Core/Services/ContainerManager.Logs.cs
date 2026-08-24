using System.Runtime.CompilerServices;
using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Logs;
using Cider.Core.Runtime;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    /// <summary>
    /// <c>GET /containers/{id}/logs</c>: our own capture when there is one, otherwise the engine's
    /// merged log stream (which has no stream separation, so everything arrives as stdout).
    /// </summary>
    public async IAsyncEnumerable<LogEntry> LogsAsync(
        string idOrName,
        LogReadOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var record = Resolve(idOrName);

        if (_logs.HasCapture(record.Id))
        {
            await foreach (var entry in _logs.ReadAsync(record.Id, options, ct))
            {
                yield return entry;
            }

            yield break;
        }

        if (!options.Stdout && !options.Stderr)
        {
            yield break;
        }

        Stream stream;
        try
        {
            stream = await _runtime.OpenLogsAsync(record.RuntimeId, options.Follow, options.Tail, ct);
        }
        catch (RuntimeException ex)
        {
            throw Translate(ex);
        }

        await using (stream)
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, ct);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    yield break;
                }

                if (read <= 0)
                {
                    yield break;
                }

                yield return new LogEntry(StdStream.Stdout, buffer.AsMemory(0, read).ToArray(), DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary><c>GET /containers/{id}/top</c>: runs <c>ps</c> inside the container, best effort.</summary>
    public async Task<ContainerTopResponse> TopAsync(string idOrName, string? psArgs, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        if (!record.State.Running)
        {
            throw DockerErrors.Conflict($"Container {record.Id} is not running");
        }

        var argv = new List<string> { "ps" };
        if (!string.IsNullOrWhiteSpace(psArgs))
        {
            argv.AddRange(psArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            argv.Add("-ef");
        }

        string output;
        try
        {
            await using var process = await _runtime.ExecAsync(
                record.RuntimeId,
                new ExecSpec { Argv = argv },
                ct);

            using var reader = new StreamReader(process.Stdout, Encoding.UTF8);
            output = await reader.ReadToEndAsync(ct);
            await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (RuntimeException ex)
        {
            throw Translate(ex);
        }
        catch (TimeoutException)
        {
            throw DockerErrors.Internal("cider: `ps` inside the container timed out");
        }

        return ParseTop(output);
    }

    internal static ContainerTopResponse ParseTop(string output)
    {
        var response = new ContainerTopResponse();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            response.Titles = ["UID", "PID", "PPID", "C", "STIME", "TTY", "TIME", "CMD"];
            return response;
        }

        response.Titles = [.. lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        var columns = response.Titles.Count;

        for (var i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            if (fields.Length > columns && columns > 0)
            {
                // The last column (the command) keeps its spaces.
                var head = fields[..(columns - 1)].ToList();
                head.Add(string.Join(' ', fields[(columns - 1)..]));
                response.Processes.Add(head);
            }
            else
            {
                response.Processes.Add([.. fields]);
            }
        }

        return response;
    }
}
