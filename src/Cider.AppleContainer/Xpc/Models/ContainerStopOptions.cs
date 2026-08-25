namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `ContainerStopOptions` (`ContainerStopOptions.swift:19-32`), `containerStop`'s mandatory
/// `stopOptions` payload — omitting it entirely yields `invalidArgument "empty StopOptions"`
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2, §8.7, §8.11 gotcha 7). Synthesized Codable:
/// <see cref="TimeoutInSeconds"/> is required (§2.0 rule 11).
/// </summary>
internal sealed class ContainerStopOptions
{
    public required int TimeoutInSeconds { get; init; }

    /// <summary>May be <c>null</c> — the daemon then falls back to
    /// <c>configuration.stopSignal</c>, then <c>"SIGTERM"</c> (`ContainersService.swift:633-636`).
    /// Must be a string, never an int (§8.11 gotcha 6).</summary>
    public string? Signal { get; init; }

    /// <summary>The CLI's own default (§2.2): 5 second timeout, daemon-chosen signal.</summary>
    public static ContainerStopOptions Default { get; } = new() { TimeoutInSeconds = 5, Signal = null };
}
