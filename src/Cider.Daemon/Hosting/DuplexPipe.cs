using System.IO.Pipelines;

namespace Cider.Daemon.Hosting;

/// <summary>An <see cref="IDuplexPipe"/> built from an independent reader and writer.</summary>
internal sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    /// <inheritdoc />
    public PipeReader Input { get; } = input;

    /// <inheritdoc />
    public PipeWriter Output { get; } = output;
}
