using Cider.Core.Runtime;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Dials a fresh, single-purpose duplex connection to buildkitd for
/// <see cref="SessionBridge.OpenAsync"/> — deliberately a second connection rather than reusing the
/// shared <see cref="BuilderLink"/>, so <see cref="LiteralHeadersRewriteStream"/> only ever has to
/// deal with the one HEADERS frame this one <c>Control/Session</c> call writes, never a frame
/// interleaved from some other concurrent call. Abstracted from <see cref="IContainerRuntime"/>
/// directly so tests can hand out an in-memory pair instead of a real <c>buildctl dial-stdio</c> exec.
/// </summary>
public interface IRawSessionDialer
{
    /// <summary>
    /// Returns a duplex byte stream to buildkitd and whatever owns its lifetime — dispose the owner
    /// (after the stream) once the session bridge that asked for it tears down.
    /// </summary>
    Task<(Stream Duplex, IAsyncDisposable Owner)> DialAsync(CancellationToken cancellationToken);
}

/// <summary>The production <see cref="IRawSessionDialer"/>: one <c>container exec -i buildkit buildctl dial-stdio</c> per call.</summary>
public sealed class RuntimeRawSessionDialer(IContainerRuntime runtime, ILogger<RuntimeRawSessionDialer> logger) : IRawSessionDialer
{
    private readonly IContainerRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly ILogger<RuntimeRawSessionDialer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<(Stream Duplex, IAsyncDisposable Owner)> DialAsync(CancellationToken cancellationToken)
    {
        IContainerProcess process;
        try
        {
            process = await _runtime.DialBuilderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw new BuilderUnavailableException($"cider: cannot dial buildctl in the Apple builder: {ex.Message}", ex);
        }

        if (process.Stdin is not { } stdin)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw new BuilderUnavailableException("cider: the builder dial did not open stdin");
        }

        // IContainerProcess's own contract (see IContainerRuntime.DialBuilderAsync): the caller must
        // keep Stderr drained itself -- an unread pipe can back up and stall the exec entirely, taking
        // stdin/stdout down with it. Mirrors BuilderConnection.DrainStderrAsync exactly.
        _ = DrainStderrAsync(process.Stderr, _logger);

        return (new Tunnel.DuplexStream(process.Stdout, stdin), process);
    }

    private static async Task DrainStderrAsync(Stream? stderr, ILogger logger)
    {
        if (stderr is null)
        {
            return;
        }

        var reader = new StreamReader(stderr);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                logger.LogDebug("buildctl dial-stdio (session bridge): {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
