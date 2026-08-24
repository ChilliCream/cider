using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #7 — <c>docker compose</c> up/ps/logs/down over the daemon, with service-name DNS.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ComposeTests(DaemonFixture daemon)
{
    private const string ComposeFile = """
        services:
          web:
            image: alpine:3.22
            expose:
              - "8080"
            command:
              - sh
              - -c
              - "while true; do { printf 'HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi'; sleep 1; } | nc -l -p 8080 >/dev/null; done"
          client:
            image: alpine:3.22
            depends_on:
              - web
            command:
              - sh
              - -c
              - "for i in $$(seq 1 30); do wget -q -T 3 -O - http://web:8080/ && echo && break; sleep 2; done"
        """;

    [E2EFact]
    public async Task Compose_up_ps_logs_and_down_work_end_to_end()
    {
        var project = "e2e" + Guid.NewGuid().ToString("n")[..8];
        var directory = Path.Combine(daemon.ScratchDir, project);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "docker-compose.yml"), ComposeFile);

        try
        {
            var up = await daemon.DockerAsync(
                ["compose", "-p", project, "up", "-d"],
                timeout: TimeSpan.FromMinutes(6),
                workingDirectory: directory);
            Assert.True(up.Ok, up.ToString());

            var ps = await daemon.DockerAsync(
                ["compose", "-p", project, "ps", "-a", "--format", "{{.Service}}"],
                timeout: TimeSpan.FromMinutes(2),
                workingDirectory: directory);
            Assert.True(ps.Ok, ps.ToString());
            Assert.Contains("web", ps.Stdout, StringComparison.Ordinal);
            Assert.Contains("client", ps.Stdout, StringComparison.Ordinal);

            // The client resolves the `web` service name through the daemon's DNS forwarder and
            // fetches from it; its log therefore ends up carrying the served body.
            var logs = "";
            var reached = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var result = await daemon.DockerAsync(
                        ["compose", "-p", project, "logs", "client"],
                        timeout: TimeSpan.FromMinutes(2),
                        workingDirectory: directory);
                    logs = result.Stdout + result.Stderr;
                    return logs.Contains("hi", StringComparison.Ordinal);
                },
                TimeSpan.FromMinutes(2),
                TimeSpan.FromSeconds(3));

            Assert.True(reached, "the client service never reached web:8080; logs were:\n" + logs);
        }
        finally
        {
            var down = await daemon.DockerAsync(
                ["compose", "-p", project, "down", "-v", "--remove-orphans"],
                timeout: TimeSpan.FromMinutes(5),
                workingDirectory: directory);
            Assert.True(down.Ok, down.ToString());
        }

        var remaining = await daemon.DockerAsync("ps", "-a", "--filter", "label=com.docker.compose.project=" + project, "--format", "{{.Names}}");
        Assert.True(remaining.Ok, remaining.ToString());
        Assert.Equal("", remaining.Stdout.Trim());

        var networks = await daemon.DockerAsync("network", "ls", "--format", "{{.Name}}");
        Assert.DoesNotContain(project + "_default", networks.Stdout, StringComparison.Ordinal);
    }
}
