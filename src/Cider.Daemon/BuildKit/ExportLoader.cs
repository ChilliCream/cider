using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Services;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Turns one captured docker-exporter tar (<see cref="FileSendCapture"/>'s output, routed to
/// <see cref="ControlProxyService.Solve"/> through <see cref="SessionBridgeHandle.ExportFor"/>) into
/// a loaded cider image: <see cref="ImageManager.LoadImagesAsync"/> the tar, make sure every tag
/// <see cref="SolveRewriter"/> asked for actually resolves (a plain load already should — the tar's
/// own <c>manifest.json</c> carries every name that went into the exporter's <c>name</c> attr — but
/// a fallback <see cref="ImageManager.TagAsync"/> covers a load that only produced the digest), and
/// publish the same <c>build</c> event <c>ImageManager.BuildAsync</c> does for a classic build.
/// </summary>
public sealed class ExportLoader
{
    private readonly ImageManager _images;
    private readonly EventBus _events;
    private readonly ILogger<ExportLoader> _logger;

    public ExportLoader(ImageManager images, EventBus events, ILogger<ExportLoader> logger)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Loads <paramref name="tarPath"/> and resolves <paramref name="exporter"/>'s image id.
    /// Deletes <paramref name="tarPath"/> once loading finishes, success or failure.
    /// </summary>
    public async Task<LoadedImage> LoadAsync(string tarPath, RewrittenExporter exporter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tarPath);
        ArgumentNullException.ThrowIfNull(exporter);

        try
        {
            IReadOnlyList<string> loaded;
            await using (var file = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            {
                loaded = await _images.LoadImagesAsync(file, progress: null, ct).ConfigureAwait(false);
            }

            var allNames = exporter.SyntheticTag is null
                ? exporter.Tags
                : (IReadOnlyList<string>)[.. exporter.Tags, exporter.SyntheticTag];

            string? imageId = null;
            foreach (var name in allNames)
            {
                imageId = await EnsureTaggedAsync(name, loaded, ct).ConfigureAwait(false) ?? imageId;
            }

            if (imageId is null && loaded.Count > 0)
            {
                imageId = await InspectIdAsync(loaded[0], ct).ConfigureAwait(false);
            }

            if (imageId is null)
            {
                throw new InvalidOperationException("cider: the docker exporter's tar produced no loadable image");
            }

            _events.Publish(DockerEvents.Image("build", imageId, exporter.Tags.FirstOrDefault()));
            return new LoadedImage { ImageId = imageId, Tags = exporter.Tags };
        }
        finally
        {
            TryDelete(tarPath);
        }
    }

    /// <summary>
    /// Resolves <paramref name="name"/>'s image id, tagging it from a loaded reference first if the
    /// tar's own load did not already produce that exact reference.
    /// </summary>
    private async Task<string?> EnsureTaggedAsync(string name, IReadOnlyList<string> loaded, CancellationToken ct)
    {
        var id = await InspectIdAsync(name, ct).ConfigureAwait(false);
        if (id is not null)
        {
            return id;
        }

        var source = loaded.FirstOrDefault();
        if (source is null)
        {
            return null;
        }

        var parsed = ImageReference.Parse(name);
        try
        {
            await _images.TagAsync(source, parsed.Name, parsed.Tag, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            _logger.LogWarning(ex, "could not tag {Name} from loaded reference {Source}", name, source);
            return null;
        }

        return await InspectIdAsync(name, ct).ConfigureAwait(false);
    }

    private async Task<string?> InspectIdAsync(string reference, CancellationToken ct)
    {
        try
        {
            var inspected = await _images.InspectAsync(reference, ct).ConfigureAwait(false);
            return inspected.Id;
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "could not delete export tar {TarPath}", path);
        }
    }
}

/// <summary>What <see cref="ExportLoader.LoadAsync"/> resolved for one rewritten exporter.</summary>
public sealed class LoadedImage
{
    /// <summary>Cider's own image id (<c>sha256:…</c>).</summary>
    public required string ImageId { get; init; }

    /// <summary>The caller's own normalized tags — excludes the synthetic tag, if any.</summary>
    public required IReadOnlyList<string> Tags { get; init; }
}
