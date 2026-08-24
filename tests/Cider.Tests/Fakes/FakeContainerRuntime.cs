using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IContainerRuntime"/> test double. Partial across:
/// this file (declaration + call log, owner: core-containers convention) and
/// <c>.Containers.Placeholder.cs</c> (owner: core-resources, placeholder until core-containers
/// supplies the real container-side implementation), <c>.Images.cs</c>, <c>.Networks.cs</c>,
/// <c>.Volumes.cs</c> (owner: core-resources).
/// </summary>
public sealed partial class FakeContainerRuntime : IContainerRuntime
{
    /// <summary>Every call made against this fake, in order, for test assertions.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Replaces the exec process a test gets — e.g. with one on a real pty.</summary>
    public Func<ExecSpec, IContainerProcess>? ExecFactory { get; set; }

    private readonly object _sync = new();

    private void Record(string call)
    {
        lock (_sync)
        {
            Calls.Add(call);
        }
    }
}
