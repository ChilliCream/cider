namespace Cider.Daemon.Install;

/// <summary>Options controlling how the cider daemon is installed as a launchd agent.</summary>
public sealed record InstallOptions(
    string ExecutablePath,
    string SocketPath,
    string DataDir,
    string? LogLevel,
    bool CreateDockerContext = true,
    bool SystemSocketSymlink = false,
    string Label = "com.chillicream.cider.daemon",
    string ContextName = "cider",
    bool SystemSocketForce = false);

/// <summary>Outcome of an install/uninstall/context operation, with the human-readable steps taken.</summary>
public sealed record InstallResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>Current state of the launchd agent as reported by <c>launchctl print</c>.</summary>
public sealed record ServiceStatus(bool Installed, bool Running, int? Pid, string? PlistPath, string? LastExitStatus);
