using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="ProcessConfigurationBuilder.Build"/> (task cider-ede.8) as a pure function over
/// fixtures — no XPC, no live apiserver. Covers the task's own verification-section cases: env merge
/// last-wins, user parsing (both the explicit <c>spec.User</c> and the "fall back to the container's
/// own user" branch), the tty flag, and working-directory fallback.
/// </summary>
public class ProcessConfigurationBuilderTests
{
    private static ProcessConfiguration ContainerInitProcess(
        IReadOnlyList<string>? env = null, string workingDirectory = "/app", User? user = null) => new()
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "app"],
            Environment = env is null ? ["PATH=/usr/bin", "APP_ENV=prod"] : [.. env],
            WorkingDirectory = workingDirectory,
            Terminal = false,
            User = user ?? User.OfId(1000, 1000),
            SupplementalGroups = [],
            Rlimits = [],
        };

    private static ExecSpec PlainSpec(IReadOnlyList<string>? argv = null) => new()
    {
        Argv = argv ?? ["ps", "-ef"],
    };

    // ---- executable / arguments -------------------------------------------------------------------

    [Fact]
    public void Build_splits_argv_into_executable_and_arguments()
    {
        var config = ProcessConfigurationBuilder.Build(ContainerInitProcess(), PlainSpec(["sh", "-c", "echo hi"]));

        Assert.Equal("sh", config.Executable);
        Assert.Equal(["-c", "echo hi"], config.Arguments);
    }

    [Fact]
    public void Build_a_single_argv_entry_has_no_arguments()
    {
        var config = ProcessConfigurationBuilder.Build(ContainerInitProcess(), PlainSpec(["bash"]));

        Assert.Equal("bash", config.Executable);
        Assert.Empty(config.Arguments);
    }

    [Fact]
    public void Build_throws_invalid_argument_on_an_empty_argv()
    {
        var ex = Assert.Throws<RuntimeException>(() =>
            ProcessConfigurationBuilder.Build(ContainerInitProcess(), PlainSpec([])));

        Assert.Equal(RuntimeErrorKind.InvalidArgument, ex.Kind);
    }

    // ---- environment: container env + spec.Env, last wins per key ---------------------------------

    [Fact]
    public void Build_merges_spec_env_over_container_env_last_wins_by_key()
    {
        var container = ContainerInitProcess(env: ["PATH=/usr/bin", "APP_ENV=prod"]);
        var spec = new ExecSpec { Argv = ["ps"], Env = ["APP_ENV=debug", "EXTRA=1"] };

        var config = ProcessConfigurationBuilder.Build(container, spec);

        // Key order preserved: PATH first (container-only), then APP_ENV (overridden, original
        // position), then EXTRA (new, appended).
        Assert.Equal(["PATH=/usr/bin", "APP_ENV=debug", "EXTRA=1"], config.Environment);
    }

    [Fact]
    public void Build_drops_env_entries_without_an_equals_sign()
    {
        var container = ContainerInitProcess(env: ["PATH=/usr/bin"]);
        var spec = new ExecSpec { Argv = ["ps"], Env = ["GARBAGE", "OK=1"] };

        var config = ProcessConfigurationBuilder.Build(container, spec);

        Assert.Equal(["PATH=/usr/bin", "OK=1"], config.Environment);
    }

    [Fact]
    public void Build_with_no_spec_env_keeps_the_container_env_verbatim()
    {
        var container = ContainerInitProcess(env: ["A=1", "B=2"]);

        var config = ProcessConfigurationBuilder.Build(container, PlainSpec());

        Assert.Equal(["A=1", "B=2"], config.Environment);
    }

    // ---- working directory: spec.WorkingDir ?? container workdir ----------------------------------

    [Fact]
    public void Build_uses_spec_working_dir_when_set()
    {
        var spec = new ExecSpec { Argv = ["ps"], WorkingDir = "/tmp" };

        var config = ProcessConfigurationBuilder.Build(ContainerInitProcess(workingDirectory: "/app"), spec);

        Assert.Equal("/tmp", config.WorkingDirectory);
    }

    [Fact]
    public void Build_falls_back_to_the_container_working_dir_when_unset()
    {
        var config = ProcessConfigurationBuilder.Build(ContainerInitProcess(workingDirectory: "/app"), PlainSpec());

        Assert.Equal("/app", config.WorkingDirectory);
    }

    // ---- tty flag -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_terminal_mirrors_spec_tty(bool tty)
    {
        var config = ProcessConfigurationBuilder.Build(ContainerInitProcess(), new ExecSpec { Argv = ["sh"], Tty = tty });

        Assert.Equal(tty, config.Terminal);
    }

    // ---- user: spec.User (same parser as ContainerConfigurationBuilder) else the container's user ---

    [Fact]
    public void Build_falls_back_to_the_container_user_when_spec_user_is_unset()
    {
        var container = ContainerInitProcess(user: User.OfId(1000, 1000));

        var config = ProcessConfigurationBuilder.Build(container, PlainSpec());

        Assert.NotNull(config.User.Id);
        Assert.Equal(1000, config.User.Id!.Uid);
        Assert.Equal(1000, config.User.Id!.Gid);
    }

    [Fact]
    public void Build_parses_a_numeric_spec_user_as_uid_gid()
    {
        var container = ContainerInitProcess(user: User.OfId(1000, 1000));
        var spec = new ExecSpec { Argv = ["sh"], User = "0:0" };

        var config = ProcessConfigurationBuilder.Build(container, spec);

        Assert.NotNull(config.User.Id);
        Assert.Equal(0, config.User.Id!.Uid);
        Assert.Equal(0, config.User.Id!.Gid);
    }

    [Fact]
    public void Build_parses_a_non_numeric_spec_user_as_raw()
    {
        var container = ContainerInitProcess(user: User.OfId(1000, 1000));
        var spec = new ExecSpec { Argv = ["sh"], User = "www-data" };

        var config = ProcessConfigurationBuilder.Build(container, spec);

        Assert.Null(config.User.Id);
        Assert.NotNull(config.User.Raw);
        Assert.Equal("www-data", config.User.Raw!.UserString);
    }

    // ---- supplementalGroups / rlimits: always empty (ExecSpec has no such fields) -------------------

    [Fact]
    public void Build_supplemental_groups_and_rlimits_are_always_empty()
    {
        var config = ProcessConfigurationBuilder.Build(ContainerInitProcess(), PlainSpec());

        Assert.Empty(config.SupplementalGroups);
        Assert.Empty(config.Rlimits);
    }
}
