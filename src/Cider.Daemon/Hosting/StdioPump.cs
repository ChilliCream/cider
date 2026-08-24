using System.Threading.Channels;
using Cider.Core.Services;

namespace Cider.Daemon.Hosting;

/// <summary>
/// The bidirectional stdio pump shared by the exec hijack (connection level) and the attach
/// upgrade (HTTP level): output chunks go out raw (pty) or stdcopy-framed, whatever the client
/// sends goes into stdin, and a half-close from the client half-closes the process' stdin.
/// </summary>
internal static class StdioPump
{
    private const int StdinBufferSize = 32 * 1024;

    /// <summary>
    /// Runs until the output channel completes (the process exited and its output was drained) or
    /// the connection breaks. Never throws for ordinary client disconnects.
    /// </summary>
    public static async Task RunAsync(
        Stream? clientInput,
        Stream clientOutput,
        bool tty,
        ChannelReader<OutputChunk> output,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? writeStdin,
        Func<Task>? closeStdin,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientOutput);
        ArgumentNullException.ThrowIfNull(output);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var stdinTask = clientInput is not null && writeStdin is not null
            ? PumpStdinAsync(clientInput, writeStdin, closeStdin, logger, linked.Token)
            : Task.CompletedTask;

        try
        {
            await foreach (var chunk in output.ReadAllAsync(ct))
            {
                await DockerResults.WriteChunkAsync(clientOutput, chunk.Stream, chunk.Data, tty, ct);
            }
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            logger.LogDebug(ex, "stdio client disconnected while writing output");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "stdio output pump failed");
        }
        finally
        {
            await linked.CancelAsync();
            try
            {
                await stdinTask;
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                // Expected once the connection is torn down.
            }
        }
    }

    private static async Task PumpStdinAsync(
        Stream clientInput,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeStdin,
        Func<Task>? closeStdin,
        ILogger logger,
        CancellationToken ct)
    {
        var buffer = new byte[StdinBufferSize];
        var total = 0L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await clientInput.ReadAsync(buffer, ct);
                if (read <= 0)
                {
                    // The client half-closed (docker's CloseWrite): stdin ends, output keeps flowing.
                    logger.LogDebug("stdio client half-closed stdin after {Total} bytes", total);
                    if (closeStdin is not null)
                    {
                        await closeStdin();
                    }

                    return;
                }

                total += read;
                logger.LogTrace("stdio client sent {Count} stdin bytes ({Total} total)", read, total);
                await writeStdin(buffer.AsMemory(0, read), ct);
            }
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            logger.LogDebug("stdio client disconnected while reading stdin");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "stdio stdin pump ended");
        }
    }

    /// <summary><c>true</c> for the exception kinds an ordinary client disconnect produces.</summary>
    public static bool IsDisconnect(Exception ex) =>
        ex is IOException or ObjectDisposedException or OperationCanceledException
            or Microsoft.AspNetCore.Connections.ConnectionResetException
            or Microsoft.AspNetCore.Connections.ConnectionAbortedException;
}
