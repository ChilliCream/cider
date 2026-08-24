namespace Cider.Core.DockerApi;

/// <summary>
/// Validation for <c>HostConfig.LogConfig.Type</c> on <c>POST /containers/create</c>.
/// </summary>
/// <remarks>
/// <para>
/// The daemon captures logs one way only — <c>LogStore</c>, in dockerd's <c>json-file</c> shape — and
/// says so in <c>GET /info</c> (<c>LoggingDriver: "json-file"</c>, <c>Plugins.Log: ["json-file"]</c>).
/// Until this check existed, <c>create</c> disagreed with <c>/info</c>: any driver name at all was
/// accepted with a 201 and echoed back on inspect, so a client that asked for <c>syslog</c> or for
/// <c>none</c> believed it had got what it asked for while every line went on being written to the
/// json-file store.
/// </para>
/// <para>
/// Two different answers, because the client's mistake is a different one in each case:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A name dockerd does not know either is a typo, and gets dockerd's own 400 —
/// <c>logger: no log driver named '%s' is registered</c>, verbatim from
/// <c>moby/daemon/logger/factory.go</c>'s <c>ValidateLogOpts</c>, which dockerd also raises at
/// create time.
/// </description></item>
/// <item><description>
/// A name dockerd does know but this daemon cannot honour is not a typo — it is a capability we do
/// not have, so it gets a 501 in cider's own voice rather than a borrowed message that would
/// falsely claim the driver does not exist. This mirrors how <c>NetworkManager</c> already answers an
/// unsupported network driver.
/// </description></item>
/// </list>
/// <para>
/// <c>none</c> deliberately falls in the second group. Accepting it would mean telling a client that
/// asked for logging to be off that it is off while the store keeps every line — the silent lie this
/// check exists to remove.
/// </para>
/// </remarks>
public static class LogDrivers
{
    /// <summary>The only driver this daemon implements, and the one <c>/info</c> advertises.</summary>
    public const string Default = "json-file";

    /// <summary>
    /// dockerd's built-in log drivers (<c>moby/daemon/logger/*</c>). Only used to tell "you asked for
    /// a driver that does not exist" apart from "you asked for one cider does not implement".
    /// </summary>
    private static readonly HashSet<string> KnownToDocker = new(StringComparer.Ordinal)
    {
        "json-file", "local", "none", "journald", "syslog", "gelf", "fluentd",
        "awslogs", "splunk", "etwlogs", "gcplogs",
    };

    /// <summary>Throws unless <paramref name="type"/> is a driver this daemon can actually honour.</summary>
    public static void Validate(string type)
    {
        if (string.IsNullOrEmpty(type) || string.Equals(type, Default, StringComparison.Ordinal))
        {
            return;
        }

        throw KnownToDocker.Contains(type)
            ? DockerErrors.NotImplemented(
                $"cider: logging driver '{type}' is not supported; only '{Default}' is (see GET /info Plugins.Log)")
            : DockerErrors.NoSuchLogDriver(type);
    }
}
