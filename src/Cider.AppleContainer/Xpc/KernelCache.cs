using System.Text.Json;
using Cider.AppleContainer.Xpc.Models;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <c>getDefaultKernel</c>, cached for the daemon's lifetime (task cider-ede.6 fix direction §2:
/// "cached for the daemon lifetime, invalidated on Unavailable") — <c>containerCreate</c> cannot be
/// built without a real <see cref="Kernel"/> blob (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.3:
/// "get this from getDefaultKernel, do not synthesize it"), and the CLI itself only re-resolves it
/// per invocation because it is a fresh process each time — a long-lived daemon does not need to pay
/// that round trip on every <c>docker create</c>. One instance is owned by <see cref="XpcContainerRuntime"/>
/// and shared across every create for the life of that runtime.
/// </summary>
internal sealed class KernelCache(XpcClient apiserver)
{
    private readonly Lock _lock = new();
    private Kernel? _cached;

    /// <summary>Returns the cached kernel, fetching it once on first use. Never caches a failed
    /// fetch — a <see cref="XpcException"/> (including an apiserver-unavailable one) always
    /// propagates to the caller and leaves the cache exactly as it was, so the very next call tries
    /// again instead of ever serving a poisoned "unavailable" result.</summary>
    public async Task<Kernel> GetAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cached is { } already)
            {
                return already;
            }
        }

        using var request = new XpcMessage("getDefaultKernel");
        request.SetData("systemPlatform", XpcJson.SerializeToUtf8Bytes(SystemPlatform.Current));
        using var reply = await apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

        var bytes = reply.GetData("kernel") ?? throw new JsonException("getDefaultKernel reply carried no kernel");
        var kernel = XpcJson.Deserialize<Kernel>(bytes);

        lock (_lock)
        {
            // A concurrent caller may have already populated this; keep whichever landed first so
            // every caller in flight at once agrees on one Kernel instance.
            _cached ??= kernel;
            return _cached;
        }
    }

    /// <summary>Drops the cached kernel — a spot for a future caller to force a re-fetch (e.g. after
    /// observing the apiserver restart) without waiting for process restart. Not wired to anything
    /// yet in this task: <see cref="GetAsync"/> already never caches a failure, which covers the
    /// "invalidated on Unavailable" requirement without needing an external trigger.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }
}
