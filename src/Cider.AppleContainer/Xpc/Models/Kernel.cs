namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `Kernel` (from `Containerization`, `XPCKeys.kernel`), confirmed live from
/// <c>~/Library/Application Support/com.apple.container/containers/*/kernel.json</c>
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2, §8.3, §8.10). Get this from
/// `getDefaultKernel`; do not synthesize it client-side.
/// </summary>
internal sealed class Kernel
{
    /// <summary><c>URL</c> encodes as its <c>absoluteString</c> (§2.0 rule 6) — a percent-encoded
    /// <c>file://</c> URL, e.g.
    /// <c>"file:///Users/michael/Library/Application%20Support/com.apple.container/kernels/vmlinux-6.18.15-186"</c>.</summary>
    public required string Path { get; init; }

    public required Platform Platform { get; init; }

    public required CommandLine CommandLine { get; init; }
}

/// <summary>The `getDefaultKernel` / `installKernel` request's `systemPlatform` payload
/// (`XPCKeys.systemPlatform`) — same two required fields as <see cref="Platform"/> but without a
/// `variant`, matching the live sample at §8.10: <c>{"os":"linux","architecture":"arm64"}</c>.</summary>
internal sealed class SystemPlatform
{
    public required string Os { get; init; }

    public required string Architecture { get; init; }

    /// <summary>The host's own platform, linux-side (`ClientKernel.swift:100-111`'s
    /// arm64→linuxArm / amd64→linuxAmd mapping — same host-architecture rule as
    /// <see cref="Platform.Current"/>).</summary>
    public static SystemPlatform Current { get; } = new()
    {
        Os = "linux",
        Architecture = Platform.Current.Architecture,
    };
}

internal sealed class CommandLine
{
    public required List<string> KernelArgs { get; init; }

    public required List<string> InitArgs { get; init; }
}
