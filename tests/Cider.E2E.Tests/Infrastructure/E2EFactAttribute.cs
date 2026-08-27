using Xunit;

namespace Cider.E2E.Tests.Infrastructure;

/// <summary>A fact that only runs with <c>CIDER_E2E=1</c>; otherwise it reports as skipped.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    /// <summary>Applies the skip reason when the opt-in environment variable is missing.</summary>
    public E2EFactAttribute()
    {
        if (DaemonFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// An E2E fact that also needs <c>CIDER_E2E_XPROC_RACE=1</c>: cider-ede.41's cross-process
/// store-corruption experiment. It spawns two throwaway daemon <em>processes</em> and runs a
/// sustained (default 15-minute) pull-vs-prune race whose prune side sweeps the one machine-wide
/// shared Apple store — deliberate, clean-baseline-verified store-wide prunes that the rest of the
/// suite's own environment rules forbid running casually (see <see cref="ImageStoreRaceFixture"/>'s
/// remarks on why the intra-daemon race rejected the prune path). Never part of a default suite run.
/// </summary>
public sealed class CrossProcessRaceFactAttribute : FactAttribute
{
    /// <summary>Applies the skip reason unless the cross-process race experiment is opted into.</summary>
    public CrossProcessRaceFactAttribute()
    {
        if (DaemonFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("CIDER_E2E_XPROC_RACE"), "1", StringComparison.Ordinal))
        {
            Skip = "set CIDER_E2E_XPROC_RACE=1 to run cider-ede.41's cross-process store race " +
                "(sustained; prunes the shared machine-wide Apple store from a throwaway daemon)";
        }
    }
}

/// <summary>
/// An E2E fact that also needs <c>CIDER_PORT_PUBLISHING=apple</c>: it characterizes what
/// Apple <c>container</c>'s own published-port forwarder does, which the default <c>proxy</c> mode
/// deliberately bypasses.
/// </summary>
public sealed class AppleModePortFactAttribute : FactAttribute
{
    /// <summary>Applies the skip reason when the suite is not running in <c>apple</c> mode.</summary>
    public AppleModePortFactAttribute()
    {
        if (DaemonFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
        else if (!DaemonFixture.AppleModePorts)
        {
            Skip = "set CIDER_PORT_PUBLISHING=apple to characterize Apple's own port forwarder";
        }
    }
}

/// <summary>
/// An E2E fact that only means anything under XPC (<c>CIDER_RUNTIME_TRANSPORT=xpc</c>): a latency
/// characterization of the fast path <c>auto</c> falls back to the CLI for. Skipped — with a message
/// naming the env var, per cider-ede.15's fix direction — under <c>cli</c> or the unpinned <c>auto</c>
/// default, since the latter's actual transport is a runtime decision (<see cref="DaemonFixture.Transport"/>
/// cannot see it) this attribute must not guess at.
/// </summary>
public sealed class XpcOnlyFactAttribute : FactAttribute
{
    /// <summary>Applies the skip reason unless the suite explicitly requested XPC.</summary>
    public XpcOnlyFactAttribute()
    {
        if (DaemonFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
        else if (!DaemonFixture.XpcTransport)
        {
            Skip = "set CIDER_RUNTIME_TRANSPORT=xpc to run this XPC-only fast-path latency characterization";
        }
    }
}

/// <summary>
/// An E2E fact that also needs <c>CIDER_E2E_LARGE=1</c>: it moves a genuinely large (default
/// 200 MiB) build context through the real Apple builder VM, which is slow and whose outcome is
/// evidence for a follow-up task rather than something worth paying for on every run.
/// </summary>
public sealed class LargeContextFactAttribute : FactAttribute
{
    /// <summary>Applies the skip reason unless the suite is enabled and opted into the large-context run.</summary>
    public LargeContextFactAttribute()
    {
        if (DaemonFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("CIDER_E2E_LARGE"), "1", StringComparison.Ordinal))
        {
            Skip = "set CIDER_E2E_LARGE=1 to run the large build-context characterization (slow; feeds cider-ger.15)";
        }
    }
}
