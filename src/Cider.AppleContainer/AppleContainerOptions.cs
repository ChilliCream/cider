namespace Cider.AppleContainer;

/// <summary>Knobs for <see cref="AppleContainerRuntime"/> (CONTRACTS §H).</summary>
public sealed class AppleContainerOptions
{
    /// <summary>Path to the Apple <c>container</c> CLI, or just its name when it is on PATH.</summary>
    public string CliPath { get; set; } = "container";

    /// <summary>Timeout applied to ordinary (non-streaming) CLI invocations.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout applied to the small resource operations a Docker client expects to be instant —
    /// network and volume create/delete. dockerd answers <c>POST /networks/create</c> in
    /// milliseconds and Apple's CLI does the same on a healthy runtime, so all the general
    /// <see cref="CommandTimeout"/> buys these calls is a five-minute stall when the runtime is
    /// wedged, which every client above us reads as a dead daemon. 30 s is two
    /// orders of magnitude of headroom over the healthy case and still inside docker-py's and
    /// compose's own 60 s HTTP read timeout, so the client sees our error envelope rather than its
    /// own connection timeout.
    /// </summary>
    public TimeSpan ResourceTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout applied to pull/push/build/save/load.</summary>
    public TimeSpan PullTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The overall ceiling for one <c>container cp</c> invocation, in either direction.
    /// Apple's <c>cp</c> reports no progress of any kind — confirmed against
    /// 1.2.2's own <c>container copy --help</c>, which has no verbose/bytes/percent option — so
    /// there is no signal to build genuine idle-during-a-transfer detection on for however long a
    /// legitimately large payload takes. This mirrors <see cref="PullTimeout"/>, which makes the
    /// same "large payload, no progress signal, be generous" call for pull/push/build/save/load: a
    /// copy that IS moving bytes must never be killed, so the bound here exists only to eventually
    /// surface a genuinely wedged runtime as a daemon-authored error instead of hanging forever.
    /// See also <see cref="CopyIdleGrace"/>, which catches the specific hang this ticket was filed
    /// for — a nonexistent source path — far sooner than this ceiling ever would.
    /// </summary>
    public TimeSpan CopyTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long <c>container cp &lt;container&gt;:&lt;path&gt; …</c> waits, after starting, to see the
    /// first byte land at the local destination before concluding the source path does not exist and
    /// the CLI has wedged rather than started a transfer (reproduced live: Apple
    /// `container cp` of a nonexistent guest path hangs indefinitely — still wedged after 90+ s, not
    /// merely slow — and leaves the container's own exec/rm channel wedged behind it too).
    /// A copy that already produced so much as one byte or directory entry is left alone entirely
    /// for the rest of <see cref="CopyTimeout"/>: this only ever distinguishes "nothing has happened
    /// yet" from "something is happening," never second-guesses a transfer once one is under way, so
    /// a real transfer — however slow — is never killed. `container cp` starts writing to a running
    /// container's already-live filesystem within a couple of seconds in the healthy case (no image
    /// fetch or VM boot involved, unlike `run`), so 10s is generous headroom over that while being a
    /// small fraction of the old five-minute default that a caller of <c>docker cp</c> would have
    /// read as a stalled daemon. Applies only to the container→host direction
    /// (<c>CopyFromContainerAsync</c>): the host→container source is always a path the daemon staged
    /// itself, never a client-supplied one, so it cannot be missing and there is nothing local to
    /// watch grow on the container side.
    /// </summary>
    public TimeSpan CopyIdleGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Directory for the temporary tarballs used by export/save/load.</summary>
    public string TmpDir { get; set; } = Path.GetTempPath();
}
