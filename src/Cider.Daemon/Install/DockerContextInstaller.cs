namespace Cider.Daemon.Install;

/// <summary>Creates/updates/removes a `docker context` pointing at the cider socket.</summary>
public static class DockerContextInstaller
{
    public static async Task<InstallResult> EnsureAsync(string contextName, string socketPath, bool setCurrent, TextWriter log, CancellationToken ct)
    {
        var steps = new List<string>();

        if (!ProcessRunner.ExistsOnPath("docker"))
        {
            const string notFoundMessage = "docker CLI not found; skipped context";
            steps.Add(notFoundMessage);
            log.WriteLine(notFoundMessage);
            return new InstallResult(false, notFoundMessage, steps);
        }

        var hostArg = $"host=unix://{socketPath}";

        var inspect = await ProcessRunner.RunAsync("docker", ["context", "inspect", contextName], TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        var exists = inspect.Succeeded;

        if (exists)
        {
            var update = await ProcessRunner.RunAsync("docker", ["context", "update", contextName, "--docker", hostArg], TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
            steps.Add($"docker context update {contextName} --docker {hostArg} (exit {update.ExitCode})");
            log.WriteLine(steps[^1]);
            if (!update.Succeeded)
            {
                var failMessage = $"Failed to update docker context '{contextName}': {update.StdErr.Trim()}";
                return new InstallResult(false, failMessage, steps);
            }
        }
        else
        {
            var create = await ProcessRunner.RunAsync(
                "docker",
                ["context", "create", contextName, "--docker", hostArg, "--description", "cider (Apple container)"],
                TimeSpan.FromSeconds(10),
                ct: ct).ConfigureAwait(false);
            steps.Add($"docker context create {contextName} --docker {hostArg} (exit {create.ExitCode})");
            log.WriteLine(steps[^1]);
            if (!create.Succeeded)
            {
                var failMessage = $"Failed to create docker context '{contextName}': {create.StdErr.Trim()}";
                return new InstallResult(false, failMessage, steps);
            }
        }

        if (setCurrent)
        {
            var use = await ProcessRunner.RunAsync("docker", ["context", "use", contextName], TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
            steps.Add($"docker context use {contextName} (exit {use.ExitCode})");
            log.WriteLine(steps[^1]);
        }

        var message = $"Docker context '{contextName}' ready. Run `docker context use {contextName}` to select it.";
        return new InstallResult(true, message, steps);
    }

    public static async Task<InstallResult> RemoveAsync(string contextName, TextWriter log, CancellationToken ct)
    {
        var steps = new List<string>();

        if (!ProcessRunner.ExistsOnPath("docker"))
        {
            const string message = "docker CLI not found; skipped context removal";
            steps.Add(message);
            log.WriteLine(message);
            return new InstallResult(true, message, steps);
        }

        var remove = await ProcessRunner.RunAsync("docker", ["context", "rm", "-f", contextName], TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        steps.Add($"docker context rm -f {contextName} (exit {remove.ExitCode})");
        log.WriteLine(steps[^1]);

        return new InstallResult(true, $"Docker context '{contextName}' removed (best effort).", steps);
    }
}
