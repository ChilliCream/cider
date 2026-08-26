using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// cider-ede.35 closing-audit fix (finding 1): Apple's <c>container create</c> has no flag to ask
/// for zero network attachments — omitting <c>--network</c> entirely attaches the default network
/// instead of none (<see cref="ArgBuilder.Create"/> simply emits no <c>--network</c> pair for an
/// empty <see cref="ContainerSpec.Networks"/> list, exactly the CLI's own gap). So a zero-network
/// spec must never reach the CLI create path at all — <see cref="AppleContainerRuntime"/> must
/// refuse it before ever building argv or shelling out.
/// </summary>
public sealed class AppleContainerRuntimeNoNetworkGuardTests
{
    [Fact]
    public async Task CreateContainerAsync_WithNoNetworks_ThrowsWithoutInvokingTheCli()
    {
        var cli = new NeverCalledCli();
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var error = await Assert.ThrowsAsync<RuntimeException>(() =>
            runtime.CreateContainerAsync(
                new ContainerSpec { RuntimeId = "web", Image = "alpine:3.22", Networks = [] },
                CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.NotSupported, error.Kind);
        Assert.Contains("network mode 'none'", error.Message, StringComparison.Ordinal);
        Assert.False(cli.WasCalled, "the CLI must never be invoked for a zero-network create");
    }

    [Fact]
    public async Task CreateContainerAsync_WithANetwork_StillReachesTheCli()
    {
        var cli = new NeverCalledCli { AllowCreate = true };
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        await runtime.CreateContainerAsync(
            new ContainerSpec { RuntimeId = "web", Image = "alpine:3.22", Networks = ["default"] },
            CancellationToken.None);

        Assert.True(cli.WasCalled);
    }

    /// <summary>Fails any test that reaches it unless <see cref="AllowCreate"/> opts in — proof that
    /// the zero-network guard fires before any argv is built or any process is started.</summary>
    private sealed class NeverCalledCli : ContainerCli
    {
        public NeverCalledCli() : base(new AppleContainerOptions(), NullLogger.Instance)
        {
        }

        public bool WasCalled { get; private set; }

        public bool AllowCreate { get; set; }

        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            WasCalled = true;
            if (AllowCreate && args.Count > 0 && args[0] == "create")
            {
                return Task.FromResult(new CliResult(0, "web\n", ""));
            }

            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }
}
