namespace Cider.AppleContainer.Xpc;

/// <summary>
/// One row of the fallback policy: an <see cref="Cider.Core.Runtime.IContainerRuntime"/> member that
/// <see cref="XpcContainerRuntime"/> hands straight to its CLI fallback, and why.
/// </summary>
/// <param name="Member">The <see cref="Cider.Core.Runtime.IContainerRuntime"/> method name, exactly as
/// it appears in the interface (matches the identifiers <c>cider status</c> and the startup log
/// print).</param>
/// <param name="Reason">Why this member never reaches the apiserver — see each entry in
/// <see cref="FallbackMatrix.Unconditional"/>/<see cref="FallbackMatrix.NetworkCreate"/> for the
/// specific rationale.</param>
public readonly record struct FallbackMatrixEntry(string Member, string Reason);

/// <summary>
/// The fallback policy task cider-ede.14 asked to make explicit and observable, replacing the prose
/// <c>// FALLBACK</c> block <see cref="XpcContainerRuntime"/> used to carry alone. This is not the
/// list of every member that *can* fall back — nearly every ported read/write path also falls back to
/// <see cref="XpcContainerRuntime"/>'s <c>_cliFallback</c> on a transient
/// <see cref="Cider.Core.Runtime.RuntimeErrorKind.Unavailable"/> (see <c>XpcContainerRuntime.GuardAsync</c>/
/// <c>WarnFallback</c>) — that is the ordinary "apiserver hiccuped" case and is not policy, it is
/// resilience. This matrix is the smaller, structural list: members that <em>never even attempt</em>
/// XPC, either always (<see cref="Unconditional"/>) or on this host (<see cref="NetworkCreate"/>, gated
/// by <see cref="RuntimeCapabilities.NetworkCreate"/>). <see cref="ActiveMembers"/> is what
/// <c>cider status</c> (<c>Program.StatusAsync</c>) and <see cref="XpcContainerRuntime"/>'s own startup
/// Information log print.
/// </summary>
public static class FallbackMatrix
{
    /// <summary>
    /// Members that delegate straight to the CLI runtime regardless of host or apiserver version — see
    /// the <c>// ---- FALLBACK ----</c> region at the bottom of <c>XpcContainerRuntime.cs</c> and
    /// <c>XpcContainerRuntime.Builder.cs</c> for the code.
    /// </summary>
    public static readonly IReadOnlyList<FallbackMatrixEntry> Unconditional =
    [
        new(
            "BuildImageAsync",
            "classic builder — task cider-ede.5's own non-goal; still enters the blob-sweep gate as a " +
            "write first (cider-ede.31 correction)"),
        new(
            "LoginAsync",
            "registry login stores credentials the images service itself reads back — fix direction §2"),
        new(
            "StartBuilderAsync",
            "builder VM start stays on the CLI — task cider-ede.13's own non-goal"),
    ];

    /// <summary>
    /// <c>CreateNetworkAsync</c> — conditional, not unconditional: the <c>networkCreate</c> route is
    /// registered only on macOS 26+ (<c>APIServer+Start.swift:351-355</c>); below that the route does
    /// not exist and <see cref="RuntimeCapabilities.NetworkCreate"/> is <c>false</c>, so
    /// <c>XpcContainerRuntime.Resources.cs</c> delegates without even attempting XPC. On macOS 26+ it
    /// is XPC-first like any other ported member (and drops out of <see cref="ActiveMembers"/>) —
    /// it still falls back on a live <c>Unavailable</c>, same as every other ported path, which is the
    /// ordinary resilience case this matrix does not enumerate.
    /// </summary>
    public static readonly FallbackMatrixEntry NetworkCreate = new(
        "CreateNetworkAsync",
        "networkCreate is registered only on macOS 26+ (RuntimeCapabilities.NetworkCreate is false on this host)");

    /// <summary>
    /// The fallback member names actually in effect for <paramref name="capabilities"/> — the
    /// <see cref="Unconditional"/> three, plus <see cref="NetworkCreate"/> when this host's apiserver
    /// does not register the route. Meaningless (and never called) when
    /// <see cref="RuntimeCapabilities.Transport"/> is <see cref="RuntimeTransportKind.Cli"/> — in that
    /// configuration every member falls back, not just these.
    /// </summary>
    public static IReadOnlyList<string> ActiveMembers(RuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capabilities.NetworkCreate
            ? [.. Unconditional.Select(e => e.Member)]
            : [.. Unconditional.Select(e => e.Member), NetworkCreate.Member];
    }
}
