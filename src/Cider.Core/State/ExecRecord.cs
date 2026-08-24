using Cider.Core.DockerApi.Models;

namespace Cider.Core.State;

/// <summary>One exec instance. Docker forgets execs across daemon restarts and so do we (in-memory only).</summary>
public sealed class ExecRecord
{
    /// <summary>Exec id, 64 lowercase hex characters.</summary>
    public required string Id { get; set; }

    /// <summary>Docker id of the container the exec runs in.</summary>
    public required string ContainerId { get; set; }

    /// <summary>The create request as received.</summary>
    public required ExecCreateRequest Request { get; set; }

    /// <summary><c>true</c> between start and exit.</summary>
    public bool Running { get; set; }

    /// <summary>Exit code once the process finished; <c>null</c> before that.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Host-side pid of the exec process, when known.</summary>
    public int Pid { get; set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary><c>true</c> once <c>start</c> was called (Docker refuses a second start).</summary>
    public bool Started { get; set; }
}
