namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `ContainerCreateOptions` (`ContainerCreateOptions.swift:17-28`), `containerCreate`'s optional
/// `containerOptions` payload — defaults to <c>{"autoRemove": false}</c> when omitted
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.1, §8.3). Synthesized Codable:
/// <see cref="AutoRemove"/> is required (§2.0 rule 11).
/// </summary>
internal sealed class ContainerCreateOptions
{
    public required bool AutoRemove { get; init; }

    /// <summary>Supplying this skips the image unpack (§8.3).</summary>
    public Filesystem? RootFsOverride { get; init; }

    public static ContainerCreateOptions Default { get; } = new() { AutoRemove = false };
}
