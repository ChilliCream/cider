using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// Every stderr text observed on 1.2.2 (docs/apple-container-notes.md §12) and the kind it must map to.
/// The CLI exits with 1 for every failure, so text is the only signal.
/// </summary>
public class CliErrorMapperTests
{
    [Theory]
    // not found
    [InlineData("Error: container not found: nope", RuntimeErrorKind.NotFound)]
    [InlineData("Error: image not found: doesnotexist:1", RuntimeErrorKind.NotFound)]
    [InlineData("Error: get failed: container nonexistent not found", RuntimeErrorKind.NotFound)]
    [InlineData("Error: No such container: nope", RuntimeErrorKind.NotFound)]
    [InlineData("Error: volume does not exist", RuntimeErrorKind.NotFound)]
    [InlineData("Error: HTTP request to https://registry-1.docker.io/v2/library/alpine/manifests/x failed with response: 404 Not Found. Reason: Unknown", RuntimeErrorKind.NotFound)]
    [InlineData("Error: HTTP request to https://registry-1.docker.io/v2/doesnotexist/xyz/manifests/1 failed with response: 401 Unauthorized. Reason: Unknown, no credentials found for host registry-1.docker.io", RuntimeErrorKind.NotFound)]
    // conflicts
    [InlineData("Error: container already exists: adtest2", RuntimeErrorKind.Conflict)]
    [InlineData("Error: container adtest1 is not running", RuntimeErrorKind.Conflict)]
    [InlineData("Error: internalError: \"failed to delete container\" (cause: \"invalidState: \"container adtest2 is running and can not be deleted\"\")", RuntimeErrorKind.Conflict)]
    [InlineData("Error: failed to copy from container adtest2 (cause: \"invalidState: \"container adtest2 is not running\"\")", RuntimeErrorKind.Conflict)]
    [InlineData("failed to delete network: [\"id\": adtest-net, \"error\": invalidState: \"cannot delete subnet adtest-net with referring containers: adtest8, adtest7\"]", RuntimeErrorKind.Conflict)]
    [InlineData("failed to delete volume: [\"id\": adtest-vol, \"error\": invalidArgument: \"volume 'adtest-vol' is currently in use and cannot be accessed by another container, or deleted\"]", RuntimeErrorKind.Conflict)]
    // not found by elimination: the delete messages carry no cause at all
    [InlineData("Error: failed to delete one or more networks: [\"nope\"]", RuntimeErrorKind.NotFound)]
    [InlineData("Error: failed to delete one or more volumes: [\"nope\"]", RuntimeErrorKind.NotFound)]
    // bad input
    [InlineData("Error: Unknown option '--frobnicate'", RuntimeErrorKind.InvalidArgument)]
    [InlineData("Error: invalidArgument: \"cpus must be positive\"", RuntimeErrorKind.InvalidArgument)]
    [InlineData("Error: Missing value for '--name'", RuntimeErrorKind.InvalidArgument)]
    // unsupported
    [InlineData("Error: unsupported operation: pause", RuntimeErrorKind.NotSupported)]
    [InlineData("Error: this feature is not supported", RuntimeErrorKind.NotSupported)]
    // service down
    [InlineData("Error: apiserver is not running", RuntimeErrorKind.Unavailable)]
    [InlineData("Error: XPC connection error", RuntimeErrorKind.Unavailable)]
    [InlineData("Error: Plugin 'container-x' failed.\n\n- If system services are not running, start them with: container system start", RuntimeErrorKind.Unavailable)]
    // everything else
    [InlineData("Error: unknown: \"failed to solve: process \"/bin/sh -c false\" did not complete successfully: exit code: 1\"", RuntimeErrorKind.Internal)]
    [InlineData("", RuntimeErrorKind.Internal)]
    public void Stderr_maps_to_the_expected_kind(string stderr, RuntimeErrorKind expected) =>
        Assert.Equal(expected, CliErrorMapper.Classify(stderr));

    [Fact]
    public void Message_is_the_last_line_without_the_error_prefix()
    {
        Assert.Equal("container not found: nope", CliErrorMapper.ExtractMessage("Error: container not found: nope\n"));

        Assert.Equal(
            "delete failed for one or more networks: [\"adtest-net\"]",
            CliErrorMapper.ExtractMessage(
                "failed to delete network: [\"id\": adtest-net]\nError: delete failed for one or more networks: [\"adtest-net\"]\n"));
    }

    [Fact]
    public void Message_falls_back_to_stdout_then_to_a_default()
    {
        Assert.Equal("something on stdout", CliErrorMapper.ExtractMessage("   ", "something on stdout"));
        Assert.Equal("container CLI failed", CliErrorMapper.ExtractMessage(null, null));
    }

    [Fact]
    public void Failures_become_runtime_exceptions_with_context()
    {
        var result = new CliResult(1, "", "Error: container not found: nope");
        var exception = CliErrorMapper.ToException(result, "inspect nope");

        Assert.Equal(RuntimeErrorKind.NotFound, exception.Kind);
        Assert.Equal("inspect nope: container not found: nope", exception.Message);
    }

    // The wording is classified into a typed reason exactly once, here at the
    // boundary. Everything above the IContainerRuntime seam branches on the reason, never the text.
    [Theory]
    [InlineData("Error: container adtest1 is not running")]
    [InlineData("Error: failed to copy from container adtest2 (cause: \"invalidState: \"container adtest2 is not running\"\")")]
    [InlineData("Error: failed to copy into container op3pg (cause: \"invalidState: \"container op3pg is not running\"\")")]
    public void A_container_that_is_not_running_gets_the_typed_reason(string stderr)
    {
        var exception = CliErrorMapper.ToException(new CliResult(1, "", stderr), "exec c1");

        Assert.Equal(RuntimeErrorKind.Conflict, exception.Kind);
        Assert.True(exception.IsContainerNotRunning);
    }

    [Fact]
    public void The_reason_survives_the_stdout_fallback()
    {
        // Some CLI failures write to stdout with an empty stderr; ToException classifies kind and
        // reason from the SAME fallback text, and this pins it (review finding).
        var exception = CliErrorMapper.ToException(
            new CliResult(1, "Error: container adtest1 is not running", ""), "exec c1");

        Assert.Equal(RuntimeErrorKind.Conflict, exception.Kind);
        Assert.True(exception.IsContainerNotRunning);
    }

    [Theory]
    // The runtime itself being down is Unavailable, not a stopped container — the phrase overlaps.
    [InlineData("Error: apiserver is not running")]
    [InlineData("Error: Plugin 'container-x' failed.\n\n- If system services are not running, start them with: container system start")]
    // An unrelated conflict must not pick up the reason either.
    [InlineData("Error: container already exists: adtest2")]
    public void Other_failures_do_not_get_the_not_running_reason(string stderr)
    {
        var exception = CliErrorMapper.ToException(new CliResult(1, "", stderr), "exec c1");

        Assert.False(exception.IsContainerNotRunning);
    }

    // swift-argument-parser's usage banner is the LAST thing on stderr, so the
    // plain last-line rule handed Docker clients `network create x: See 'container network create
    // --help' for more information.` with a 500 — a banner instead of a cause, and a server status
    // for a client input error. Shape taken from the docker-py IPv6 network failures.
    private const string UsageBannerStderr =
        "Error: The value 'fd00::/64' is invalid for '--subnet <subnet>'\n" +
        "Usage: container network create [--subnet <subnet>] [--label <label>]\n" +
        "                               <name>\n" +
        "  See 'container network create --help' for more information.\n";

    [Fact]
    public void A_usage_banner_is_an_argument_rejection_reported_by_its_cause()
    {
        Assert.Equal(RuntimeErrorKind.InvalidArgument, CliErrorMapper.Classify(UsageBannerStderr));

        var exception = CliErrorMapper.ToException(
            new CliResult(1, "", UsageBannerStderr), "network create dockerpytest_f70baec");

        // 400 (InvalidArgument), and the message names what was rejected instead of the banner.
        Assert.Equal(RuntimeErrorKind.InvalidArgument, exception.Kind);
        Assert.Equal(
            "network create dockerpytest_f70baec: The value 'fd00::/64' is invalid for '--subnet <subnet>'",
            exception.Message);
        Assert.DoesNotContain("--help", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_banner_with_nothing_else_still_does_not_reach_the_client()
    {
        const string BannerOnly =
            "Usage: container network create <name>\n  See 'container network create --help' for more information.\n";

        Assert.Equal("the container CLI rejected the arguments", CliErrorMapper.ExtractMessage(BannerOnly));
        Assert.Equal(RuntimeErrorKind.InvalidArgument, CliErrorMapper.Classify(BannerOnly));
    }

    [Fact]
    public void Successful_results_do_not_throw() =>
        ContainerCli.ThrowIfFailed(new CliResult(0, "ok", ""), "test");
}
