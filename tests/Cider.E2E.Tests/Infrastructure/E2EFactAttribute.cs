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
