using Cider.AppleContainer.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// A daemon that dies without disposing its held <c>container start -a</c>
/// children leaves them running under launchd (ppid 1), where they keep containers alive, hold
/// networks, and eventually wedge the Apple runtime machine-wide. The startup sweep kills exactly
/// those — and nothing else.
/// </summary>
public class OrphanReaperTests
{
    private static OrphanReaper.ProcessRow Row(int pid, int ppid, string command) => new(pid, ppid, command);

    private const string M = " CIDER_HELD=1"; // ps -axeo appends the environment to the row

    private static (OrphanReaper Reaper, List<int> Killed) Create(params OrphanReaper.ProcessRow[] rows)
    {
        var killed = new List<int>();
        var reaper = new OrphanReaper(NullLogger.Instance, cliPath: null, () => rows, killed.Add);
        return (reaper, killed);
    }

    [Fact]
    public void An_orphaned_held_child_is_killed()
    {
        var (reaper, killed) = Create(
            Row(4711, 1, "container start -a wizardly_gates" + M),
            Row(4712, 1, "/usr/local/bin/container start -a amazing_swirles" + M),
            Row(4713, 1, "container start -a -i with_stdin" + M));

        Assert.Equal(3, reaper.ReapOrphanedHeldProcesses());
        Assert.Equal([4711, 4712, 4713], killed);
    }

    /// <summary>
    /// Transitional (rename to Cider): a child held by a daemon from before the rename carries the
    /// old marker, and is exactly the orphan the sweep exists to kill.
    /// </summary>
    [Fact]
    public void An_orphan_left_by_a_pre_rename_daemon_is_still_killed()
    {
        var (reaper, killed) = Create(
            Row(4711, 1, "container start -a wizardly_gates APPLE_DEMON_HELD=1"));

        Assert.Equal(1, reaper.ReapOrphanedHeldProcesses());
        Assert.Equal([4711], killed);
    }

    [Fact]
    public void A_held_child_with_a_live_parent_belongs_to_a_running_daemon_and_is_spared()
    {
        var (reaper, killed) = Create(
            Row(4711, 8842, "container start -a owned_by_a_live_daemon" + M));

        Assert.Equal(0, reaper.ReapOrphanedHeldProcesses());
        Assert.Empty(killed);
    }

    [Fact]
    public void Unrelated_processes_are_never_matched_even_when_orphaned()
    {
        var (reaper, killed) = Create(
            // Apple's own infrastructure and other CLI verbs must survive a sweep.
            Row(101, 1, "/usr/local/bin/container-apiserver start"),
            Row(102, 1, "/usr/local/libexec/container/plugins/container-runtime-linux/bin/container-runtime-linux start --root x --uuid y"),
            Row(103, 1, "container exec -i -t c1 sh"),
            Row(104, 1, "container logs -f c1"),
            Row(105, 1, "container system start --enable-kernel-install"),
            // Things that merely mention the words.
            Row(106, 1, "vim notes-about-container start -a.md"),
            Row(107, 1, "grep container start -a somefile"),
            // A user's OWN launchd-managed or nohup'd attach: ppid 1, right argv, but no marker —
            // the daemon has no business killing it (review finding).
            Row(108, 1, "container start -a users_own_launch_agent"),
            Row(109, 1, "/usr/local/bin/container start -a users_nohup PATH=/usr/bin HOME=/Users/x"));

        Assert.Equal(0, reaper.ReapOrphanedHeldProcesses());
        Assert.Empty(killed);
    }

    [Fact]
    public void A_kill_that_fails_does_not_abort_the_sweep()
    {
        var rows = new[]
        {
            Row(1000, 1, "container start -a first" + M),
            Row(1001, 1, "container start -a second" + M),
            Row(1002, 1, "container start -a third" + M),
        };
        var killed = new List<int>();
        var reaper = new OrphanReaper(
            NullLogger.Instance,
            cliPath: null,
            () => rows,
            pid =>
            {
                if (pid == 1000)
                {
                    throw new InvalidOperationException("already exited");
                }

                if (pid == 1001)
                {
                    // A tree-kill wraps EPERM as AggregateException; the sweep must skip the row,
                    // not abort (the review's major finding).
                    throw new AggregateException(new System.ComponentModel.Win32Exception(1));
                }

                killed.Add(pid);
            });

        Assert.Equal(1, reaper.ReapOrphanedHeldProcesses());
        Assert.Equal([1002], killed);
    }

    [Fact]
    public void A_process_table_that_cannot_be_read_is_a_no_op_not_a_startup_failure()
    {
        var reaper = new OrphanReaper(
            NullLogger.Instance,
            cliPath: null,
            () => throw new IOException("ps unavailable"),
            _ => Assert.Fail("nothing must be killed when the table is unreadable"));

        Assert.Equal(0, reaper.ReapOrphanedHeldProcesses());
    }

    [Theory]
    [InlineData("container start -a c1", true)]
    [InlineData("/usr/local/bin/container start -a c1", true)]
    [InlineData("container start -a -i c1", true)] // ArgBuilder.Start order: start -a [-i] <id>
    [InlineData("container start --attach c1", true)]
    [InlineData("container start c1", false)] // detached start: exits on its own, nothing held
    [InlineData("container stop c1", false)]
    [InlineData("container-apiserver start", false)]
    [InlineData("mycontainer start -a c1", false)]
    [InlineData("container", false)]
    [InlineData("", false)]
    public void Only_the_held_start_shapes_match(string command, bool expected) =>
        Assert.Equal(expected, OrphanReaper.IsHeldContainerChild(command));

    [Fact]
    public void The_logged_command_never_contains_the_environment()
    {
        // ps -E appends the full inherited environment — tokens included — to the row; only the
        // argv may reach a log line.
        Assert.Equal(
            "container start -a c1",
            OrphanReaper.ArgvOnly("container start -a c1 CLAUDE_CODE_MESSAGING_TOKEN=s3cr3t PATH=/usr/bin CIDER_HELD=1"));
        Assert.Equal("container start -a c1", OrphanReaper.ArgvOnly("container start -a c1"));
    }

    [Fact]
    public void A_configured_cli_basename_is_honoured()
    {
        Assert.True(OrphanReaper.IsHeldContainerChild("/opt/apple/bin/container-1.2.2 start -a c1", "container-1.2.2"));
        Assert.False(OrphanReaper.IsHeldContainerChild("/opt/apple/bin/container-1.2.2 start -a c1")); // default basename: no match
        Assert.False(OrphanReaper.IsHeldContainerChild("container start -a c1", "container-1.2.2"));
    }
}
