namespace Cider.Core.Services;

/// <summary>
/// The slice of <c>ContainerManager</c> that <see cref="SystemManager"/> needs for <c>/info</c>.
/// Defined here (rather than depending on <c>ContainerManager</c> directly) so core-resources can
/// compile independently of core-containers; the orchestrator ensures <c>ContainerManager</c>
/// implements this interface.
/// </summary>
public interface IContainerCounts
{
    /// <summary>Number of containers, optionally filtered to one Docker status (e.g. "running", "exited").</summary>
    int Count(string? status = null);
}
