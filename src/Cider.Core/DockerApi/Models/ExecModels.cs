using System.Text.Json.Serialization;

namespace Cider.Core.DockerApi.Models;

/// <summary>Body of <c>POST /containers/{id}/exec</c>.</summary>
public sealed class ExecCreateRequest
{
    public bool AttachStdin { get; set; }
    public bool AttachStdout { get; set; }
    public bool AttachStderr { get; set; }
    public List<int>? ConsoleSize { get; set; }
    public string? DetachKeys { get; set; }
    public bool Tty { get; set; }
    public List<string>? Env { get; set; }
    public List<string> Cmd { get; set; } = [];
    public bool Privileged { get; set; }
    public string? User { get; set; }
    public string? WorkingDir { get; set; }
}

/// <summary><c>POST /containers/{id}/exec</c> response.</summary>
public sealed class ExecCreateResponse
{
    public string Id { get; set; } = "";
}

/// <summary>Body of <c>POST /exec/{id}/start</c>.</summary>
public sealed class ExecStartRequest
{
    public bool Detach { get; set; }
    public bool Tty { get; set; }
    public List<int>? ConsoleSize { get; set; }
}

/// <summary><c>GET /exec/{id}/json</c> — note Docker's all-caps <c>ID</c>.</summary>
public sealed class ExecInspectResponse
{
    [JsonPropertyName("ID")]
    public string ID { get; set; } = "";

    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public ProcessConfig ProcessConfig { get; set; } = new();
    public bool OpenStdin { get; set; }
    public bool OpenStderr { get; set; }
    public bool OpenStdout { get; set; }
    public bool CanRemove { get; set; }
    public string ContainerID { get; set; } = "";
    public string DetachKeys { get; set; } = "";
    public int Pid { get; set; }
}

/// <summary><c>ExecInspectResponse.ProcessConfig</c> — lowercase keys in Docker.</summary>
public sealed class ProcessConfig
{
    [JsonPropertyName("privileged")]
    public bool Privileged { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("tty")]
    public bool Tty { get; set; }

    [JsonPropertyName("entrypoint")]
    public string Entrypoint { get; set; } = "";

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = [];
}
