namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `ContainerListFilters` (`ContainerListFilters.swift:19-39`), the `containerList` request's
/// `listFilters` payload (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2, §8.2). Synthesized
/// Codable: <see cref="Ids"/> and <see cref="Labels"/> are required on decode — an empty
/// <c>{}</c> payload fails (§2.0 rule 11, §8.11 gotcha 8). Send <c>[]</c>/<c>{}</c> for "no filter",
/// or omit the whole `listFilters` key for `.all`.
/// </summary>
internal sealed class ContainerListFilters
{
    public required List<string> Ids { get; init; }

    /// <summary><c>RuntimeStatus</c>, plain string, optional.</summary>
    public RuntimeStatus? Status { get; init; }

    /// <summary>Values are regexes compiled server-side (`ContainersService.swift:181-190`).</summary>
    public required Dictionary<string, string> Labels { get; init; }

    /// <summary>The "no filter" payload the client always sends when it wants every container.</summary>
    public static ContainerListFilters All { get; } = new() { Ids = [], Labels = [] };
}
