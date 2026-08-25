using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

/// <summary>
/// The Apple builder VM half of the fake engine (owner: apple-builder): scriptable
/// <c>container builder status</c>, call counters for start/dial, and an in-memory duplex process
/// for <see cref="DialBuilderAsync"/> — mirroring <c>AppleContainerRuntime</c>'s builder seam without
/// a real <c>container</c> binary.
/// </summary>
public sealed partial class FakeContainerRuntime
{
    /// <summary>What <see cref="GetBuilderStatusAsync"/> returns; <c>null</c> (the default) means
    /// "no builder has ever been started", exactly like the real runtime.</summary>
    public BuilderStatus? BuilderStatus { get; set; }

    /// <summary>How many times <see cref="StartBuilderAsync"/> was called.</summary>
    public int StartBuilderCalls { get; private set; }

    /// <summary>The <c>(cpus, memoryBytes)</c> the last <see cref="StartBuilderAsync"/> call carried.</summary>
    public (int? Cpus, long? MemoryBytes)? LastStartBuilderArgs { get; private set; }

    /// <summary>How many times <see cref="DialBuilderAsync"/> was called.</summary>
    public int DialBuilderCalls { get; private set; }

    /// <summary>Test hook: fails the next <see cref="StartBuilderAsync"/> with this error.</summary>
    public RuntimeException? StartBuilderFailure { get; set; }

    /// <summary>Test hook: fails the next <see cref="DialBuilderAsync"/> with this error.</summary>
    public RuntimeException? DialBuilderFailure { get; set; }

    /// <summary>Every exec process handed out by <see cref="DialBuilderAsync"/>, in order.</summary>
    public List<FakeProcess> BuilderDials { get; } = [];

    /// <inheritdoc />
    public Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct)
    {
        Record("GetBuilderStatusAsync");
        lock (_sync)
        {
            return Task.FromResult(BuilderStatus);
        }
    }

    /// <inheritdoc />
    public Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct)
    {
        Record($"StartBuilderAsync:{cpus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}:{memoryBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");

        lock (_sync)
        {
            StartBuilderCalls++;
            LastStartBuilderArgs = (cpus, memoryBytes);

            if (StartBuilderFailure is { } failure)
            {
                StartBuilderFailure = null;
                throw failure;
            }

            var current = BuilderStatus;
            BuilderStatus = new BuilderStatus
            {
                Name = "buildkit",
                Image = current?.Image is { Length: > 0 } image ? image : "ghcr.io/apple/container-builder-shim/builder:0.13.1",
                Running = true,
                Address = current?.Address ?? "192.168.64.7/24",
                Cpus = cpus ?? current?.Cpus,
                MemoryBytes = memoryBytes ?? current?.MemoryBytes,
            };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IContainerProcess> DialBuilderAsync(CancellationToken ct)
    {
        Record("DialBuilderAsync");

        lock (_sync)
        {
            DialBuilderCalls++;

            if (DialBuilderFailure is { } failure)
            {
                DialBuilderFailure = null;
                throw failure;
            }
        }

        var process = new FakeProcess(["buildctl", "dial-stdio"], [], tty: false, openStdin: true);
        lock (_sync)
        {
            BuilderDials.Add(process);
        }

        return Task.FromResult<IContainerProcess>(process);
    }
}
