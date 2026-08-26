using Cider.AppleContainer.Xpc;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// cider-ede.14: the per-member fallback matrix is now a documented, testable policy
/// (<see cref="FallbackMatrix"/>) instead of only a prose comment — this is what <c>cider status</c>
/// (<c>Program.StatusAsync</c>) and <see cref="XpcContainerRuntime"/>'s own startup Information log
/// both read from.
/// </summary>
public sealed class FallbackMatrixTests
{
    [Fact]
    public void ActiveMembers_OnAHostWithNetworkCreate_ListsExactlyTheThreeUnconditionalMembers()
    {
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc, NetworkCreate = true };

        var members = FallbackMatrix.ActiveMembers(capabilities);

        Assert.Equal(["BuildImageAsync", "LoginAsync", "StartBuilderAsync"], members);
    }

    [Fact]
    public void ActiveMembers_OnAHostWithoutNetworkCreate_AlsoListsCreateNetworkAsync()
    {
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc, NetworkCreate = false };

        var members = FallbackMatrix.ActiveMembers(capabilities);

        Assert.Equal(["BuildImageAsync", "LoginAsync", "StartBuilderAsync", "CreateNetworkAsync"], members);
    }

    [Fact]
    public void ActiveMembers_Throws_WhenCapabilitiesIsNull() =>
        Assert.Throws<ArgumentNullException>(() => FallbackMatrix.ActiveMembers(null!));

    [Fact]
    public void Unconditional_EveryEntry_HasANonEmptyReason()
    {
        foreach (var entry in FallbackMatrix.Unconditional)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Member));
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        }

        Assert.False(string.IsNullOrWhiteSpace(FallbackMatrix.NetworkCreate.Member));
        Assert.False(string.IsNullOrWhiteSpace(FallbackMatrix.NetworkCreate.Reason));
    }
}
