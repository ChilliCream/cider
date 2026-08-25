namespace Cider.Core.Runtime;

/// <summary>
/// The Apple builder VM as <c>container builder status</c> reports it: a single container named
/// <c>buildkit</c>, running the fixed <c>ghcr.io/apple/container-builder-shim/builder</c> image.
/// </summary>
public sealed record BuilderStatus
{
    /// <summary>The builder's container name/runtimeId — always <c>buildkit</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The (fixed, CLI-chosen) builder image reference.</summary>
    public string Image { get; init; } = "";

    public bool Running { get; init; }

    /// <summary>The builder VM's address (<c>ip/prefix</c>), when running.</summary>
    public string? Address { get; init; }

    public int? Cpus { get; init; }

    public long? MemoryBytes { get; init; }
}
