using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Process;

/// <summary>
/// Reaps <c>container start -a</c> child processes orphaned by a daemon that died without disposing
/// them. The adapter holds one such child per running container to own its stdio
/// and exit code; when the daemon exits cleanly they are disposed, but a hard kill (crash, SIGKILL,
/// a test harness) leaves them running. They then keep their containers alive and hold those
/// containers' networks, and the Apple runtime eventually wedges machine-wide: observed for real on
/// 2026-08-23 as <c>container network create</c> hanging 300+ seconds with no daemon in the path,
/// <c>container stop</c> hanging, and deletes answering "pending operation".
/// </summary>
/// <remarks>
/// <para>
/// The discriminator is the parent pid: when a daemon dies, its held children are re-parented to
/// launchd (ppid 1). A held process whose parent is alive belongs to a live daemon — possibly
/// another instance on this machine — and is never touched. That makes the sweep safe to run from
/// every daemon instance, unconditionally, at startup.
/// </para>
/// <para>
/// Killing the CLI child does not stop its container (verified empirically:
/// docs/apple-container-notes.md §4 "Detach / kill / stop semantics"), so a reaped
/// container keeps running and is adopted by the normal startup reconcile — its exit code is
/// already recorded as unknown for exactly this daemon-died case.
/// </para>
/// </remarks>
internal sealed class OrphanReaper
{
    /// <summary>
    /// Environment marker the launcher stamps on every held child. The sweep requires it in the
    /// <c>ps -axeo</c> row, so a user's own launchd-managed or nohup'd <c>container start -a</c> —
    /// which also has ppid 1 — is never touched. macOS <c>ps</c> only prints the environment of
    /// same-uid processes, which conveniently also scopes the sweep to processes we can kill.
    /// (BSD ps: the env-appending flag is <c>-E</c>; the keyword <c>e</c> inside <c>-axeo</c> is
    /// silently ignored when <c>-o</c> is present — verified live.)
    /// </summary>
    internal const string HeldChildMarker = "CIDER_HELD";

    /// <summary>
    /// Transitional (rename to Cider): the marker stamped by daemons older than the rename. Their
    /// orphans are still on the machine and still need reaping, so the sweep matches it as well.
    /// Stamped on nothing new; delete once no pre-rename daemon can have left a child behind.
    /// </summary>
    internal const string LegacyHeldChildMarker = "APPLE_DEMON_HELD";

    /// <summary>One row of the host process table, as the sweep needs it.</summary>
    internal readonly record struct ProcessRow(int Pid, int ParentPid, string Command);

    private readonly ILogger _logger;
    private readonly string _cliBasename;
    private readonly Func<IReadOnlyList<ProcessRow>> _listProcesses;
    private readonly Action<int> _kill;

    public OrphanReaper(ILogger logger, string? cliPath = null, Func<IReadOnlyList<ProcessRow>>? listProcesses = null, Action<int>? kill = null)
    {
        _logger = logger;
        // The held child's argv[0] is whatever ContainerCliPath the daemon was configured with; a
        // pinned or renamed binary must not silently disable the sweep (review finding).
        _cliBasename = string.IsNullOrEmpty(cliPath) ? "container" : Path.GetFileName(cliPath);
        _listProcesses = listProcesses ?? ListHostProcesses;
        _kill = kill ?? KillHard;
    }

    /// <summary>
    /// Kills every orphaned held child and reports how many. Never throws: a failed sweep must not
    /// keep the daemon from starting — it merely leaves the machine as it already was.
    /// </summary>
    public int ReapOrphanedHeldProcesses()
    {
        var reaped = 0;
        try
        {
            foreach (var row in _listProcesses())
            {
                if (row.ParentPid != 1 || !IsHeldContainerChild(row.Command, _cliBasename) ||
                    !(row.Command.Contains(HeldChildMarker + "=1", StringComparison.Ordinal) ||
                      row.Command.Contains(LegacyHeldChildMarker + "=1", StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    _kill(row.Pid);
                    reaped++;
                    _logger.LogWarning(
                        "reaped orphaned held process {Pid} ({Command}) left by a daemon that died without" +
                        " disposing it; its container keeps running and is picked up by reconcile",
                        row.Pid,
                        ArgvOnly(row.Command));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or AggregateException)
                {
                    _logger.LogDebug(ex, "orphaned held process {Pid} could not be killed (already gone?)", row.Pid);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException or AggregateException)
        {
            _logger.LogWarning(ex, "the orphaned-process sweep could not read the process table; skipping it");
        }

        return reaped;
    }

    /// <summary>
    /// The argv part of a <c>ps -E</c> row, without the appended environment — which contains the
    /// whole inherited env, tokens and all, and must never reach a log line.
    /// </summary>
    internal static string ArgvOnly(string command)
    {
        var cut = System.Text.RegularExpressions.Regex.Match(command, @" [A-Za-z_][A-Za-z0-9_]*=");
        return cut.Success ? command[..cut.Index] : command;
    }

    /// <summary>
    /// <c>true</c> for the child shapes the daemon holds per container: <c>container start -a …</c>
    /// (with or without <c>-i</c>). Deliberately anchored to the start of the argv so an unrelated
    /// process merely mentioning the words (an editor, a grep) is never matched.
    /// </summary>
    internal static bool IsHeldContainerChild(string command, string cliBasename = "container")
    {
        var trimmed = command.TrimStart();

        // ps prints the argv; the binary may be bare or a full path such as /usr/local/bin/container.
        var firstSpace = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace <= 0)
        {
            return false;
        }

        var binary = trimmed[..firstSpace];
        if (!binary.Equals(cliBasename, StringComparison.Ordinal) &&
            !binary.EndsWith("/" + cliBasename, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = trimmed[(firstSpace + 1)..].TrimStart();
        return rest.StartsWith("start -a", StringComparison.Ordinal) ||
            rest.StartsWith("start -i -a", StringComparison.Ordinal) ||
            rest.StartsWith("start --attach", StringComparison.Ordinal);
    }

    private static IReadOnlyList<ProcessRow> ListHostProcesses()
    {
        var psi = new ProcessStartInfo("/bin/ps", "-axE -o pid=,ppid=,command=")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            return [];
        }

        var rows = new List<ProcessRow>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cts.Token).GetAwaiter().GetResult();
            process.WaitForExit(2000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimStart().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 3 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid))
                {
                    rows.Add(new ProcessRow(pid, ppid, parts[2]));
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
                {
                }
            }
        }

        return rows;
    }

    private static void KillHard(int pid)
    {
        // Plain Kill(), not Kill(entireProcessTree: true): a held `container start -a` child is a
        // thin XPC client with no descendants, tree-kill costs a full process-table scan per victim,
        // and Kill(bool) can throw AggregateException (partial tree failure) where Kill() only
        // throws Win32Exception/InvalidOperationException. EPERM on another uid's orphan must skip
        // that row, not abort the sweep (review finding).
        using var victim = System.Diagnostics.Process.GetProcessById(pid);
        victim.Kill();
    }
}
