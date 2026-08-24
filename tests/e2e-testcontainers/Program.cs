using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

// A minimal Testcontainers .NET client for cider: it drives DOCKER_HOST exactly the way a
// real test suite would, including (unless TESTCONTAINERS_RYUK_DISABLED=true) the Ryuk resource
// reaper container, which is what exercises the daemon's /var/run/docker.sock bind relay.
Console.WriteLine($"DOCKER_HOST={Environment.GetEnvironmentVariable("DOCKER_HOST")}");
Console.WriteLine($"RYUK_DISABLED={Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED") ?? "(unset)"}");
Console.WriteLine($"RESOLVED_ENDPOINT={TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint}");

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));

try
{
    await using var container = new ContainerBuilder()
        .WithImage("alpine:3.22")
        .WithEntrypoint("/bin/sh", "-c")
        .WithCommand("echo TC_READY; sleep 30")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("TC_READY"))
        .Build();

    await container.StartAsync(timeout.Token);
    Console.WriteLine($"STATE={container.State}");
    Console.WriteLine($"ID={container.Id}");

    var (stdout, stderr) = await container.GetLogsAsync(ct: timeout.Token);
    Console.WriteLine($"LOGS_STDOUT={stdout.Trim()}");
    Console.WriteLine($"LOGS_STDERR={stderr.Trim()}");

    var exec = await container.ExecAsync(["sh", "-c", "echo EXEC_OK"], timeout.Token);
    Console.WriteLine($"EXEC_EXIT={exec.ExitCode} EXEC_OUT={exec.Stdout.Trim()}");

    await container.StopAsync(timeout.Token);
    Console.WriteLine("TESTCONTAINERS_OK");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("TESTCONTAINERS_FAILED");
    Console.WriteLine(ex.ToString());
    return 1;
}
