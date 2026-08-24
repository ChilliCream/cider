using System.Text.Json.Serialization;
using Cider.Core.DockerApi.Models;

namespace Cider.Core.State;

/// <summary>Everything the daemon remembers about one container it created.</summary>
public sealed class ContainerRecord
{
    /// <summary>Docker id, 64 lowercase hex characters.</summary>
    public required string Id { get; set; }

    /// <summary>Docker name without the leading slash.</summary>
    public required string Name { get; set; }

    /// <summary>The Apple <c>container</c> id this record drives.</summary>
    public required string RuntimeId { get; set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>The create request as received, with image and daemon defaults applied.</summary>
    public required ContainerCreateRequest Request { get; set; }

    /// <summary>Image content id (<c>sha256:…</c>).</summary>
    public string ImageId { get; set; } = "";

    /// <summary>Familiar image reference the container was created from (e.g. <c>alpine:latest</c>).</summary>
    public string ImageRef { get; set; } = "";

    /// <summary>Platform the container was created for: the requested one, else the image's.</summary>
    public string? Platform { get; set; }

    /// <summary>
    /// The <c>?platform=</c> of the create request, <c>null</c> when the client sent none. Unlike
    /// <see cref="Platform"/> (which falls back to the platform the image resolved to) this is what
    /// a re-create has to pass, so it repeats exactly the <c>--platform</c> the original create
    /// used. Missing from state files written before this field existed, which reads back as
    /// <c>null</c> — the same as a create without <c>?platform=</c>.
    /// </summary>
    public string? RequestedPlatform { get; set; }

    /// <summary>argv[0] of the resolved command line.</summary>
    public string Path { get; set; } = "";

    /// <summary>argv[1..] of the resolved command line.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Resolved entrypoint (may be empty).</summary>
    public List<string> Entrypoint { get; set; } = [];

    /// <summary>Resolved command (may be empty).</summary>
    public List<string> Cmd { get; set; } = [];

    /// <summary>Lifecycle state.</summary>
    public ContainerState State { get; set; } = new();

    /// <summary>Allocated host port bindings, keyed <c>"80/tcp"</c>.</summary>
    public Dictionary<string, List<PortBinding>> Ports { get; set; } = [];

    /// <summary>Docker network name to endpoint settings; the address fields are filled on start.</summary>
    public Dictionary<string, EndpointSettings> Networks { get; set; } = [];

    /// <summary>Resolved mounts, in Docker's inspect shape.</summary>
    public List<MountPoint> Mounts { get; set; } = [];

    /// <summary>Path of the captured json-file log.</summary>
    public string LogPath { get; set; } = "";

    /// <summary>How often the restart policy restarted this container.</summary>
    public int RestartCount { get; set; }

    /// <summary>Whether the container is removed as soon as it exits.</summary>
    public bool AutoRemove { get; set; }

    /// <summary><c>false</c> for containers discovered on the engine that cider did not create.</summary>
    public bool Managed { get; set; } = true;

    /// <summary>Effective stop signal (image ∘ request).</summary>
    public string? StopSignal { get; set; }

    /// <summary>Effective stop timeout in seconds.</summary>
    public int? StopTimeout { get; set; }

    /// <summary>Effective healthcheck (image ∘ request).</summary>
    public HealthConfig? Healthcheck { get; set; }

    /// <summary>Restart policy as requested.</summary>
    public RestartPolicy RestartPolicy { get; set; } = new();

    /// <summary>The last stop was requested by the user, so the restart policy must not fire.</summary>
    public bool UserStopped { get; set; }

    /// <summary>Names of anonymous volumes created for this container (removed with <c>-v</c>).</summary>
    public List<string> AnonymousVolumes { get; set; } = [];
}

/// <summary>The lifecycle state of a container, mirroring Docker's <c>State</c> strings.</summary>
public sealed class ContainerState
{
    /// <summary>created | running | paused | restarting | removing | exited | dead.</summary>
    public string Status { get; set; } = "created";

    /// <summary><c>true</c> while the container is running or restarting.</summary>
    [JsonIgnore]
    public bool Running => Status is "running" or "restarting";

    /// <summary>Exit code of the last run.</summary>
    public int ExitCode { get; set; }

    /// <summary>Failure detail Docker shows in <c>State.Error</c>.</summary>
    public string? Error { get; set; }

    /// <summary>When the container last started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the container last exited.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Host-side pid of the held runtime process, when known.</summary>
    public int Pid { get; set; }

    /// <summary>Healthcheck state, when the container has a healthcheck.</summary>
    public HealthState? Health { get; set; }
}

/// <summary>Healthcheck state of one container.</summary>
public sealed class HealthState
{
    /// <summary>starting | healthy | unhealthy.</summary>
    public string Status { get; set; } = "starting";

    /// <summary>Consecutive failures since the last success.</summary>
    public int FailingStreak { get; set; }

    /// <summary>The last few probe results (Docker keeps five).</summary>
    public List<HealthcheckResult> Log { get; set; } = [];
}
