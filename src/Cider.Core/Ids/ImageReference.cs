using System.Diagnostics.CodeAnalysis;
using Cider.Core.DockerApi;

namespace Cider.Core.Ids;

/// <summary>
/// A parsed Docker image reference. <see cref="Parse"/> keeps the reference exactly as written;
/// <see cref="Normalize"/> applies Docker's defaults (<c>docker.io</c>, <c>library/</c>, <c>:latest</c>)
/// and <see cref="Familiar"/> renders the short display form Docker prints.
/// </summary>
public sealed record ImageReference
{
    /// <summary>Docker's implicit registry.</summary>
    public const string DefaultDomain = "docker.io";

    /// <summary>The registry Docker's own clients talk to for <see cref="DefaultDomain"/>.</summary>
    public const string LegacyDefaultDomain = "index.docker.io";

    /// <summary>The namespace Docker Hub official images live in.</summary>
    public const string OfficialRepositoryPrefix = "library/";

    /// <summary>Docker's implicit tag.</summary>
    public const string DefaultTag = "latest";

    /// <summary>Registry host (with optional port), or <c>null</c> when the reference omitted it.</summary>
    public string? Domain { get; init; }

    /// <summary>Repository path without the domain, e.g. <c>library/alpine</c> or <c>foo/bar</c>.</summary>
    public string Path { get; init; } = "";

    /// <summary>Tag, or <c>null</c> when the reference omitted it.</summary>
    public string? Tag { get; init; }

    /// <summary>Digest (<c>sha256:…</c>), or <c>null</c>.</summary>
    public string? Digest { get; init; }

    /// <summary>Domain + path, without tag or digest.</summary>
    public string Name => Domain is null ? Path : $"{Domain}/{Path}";

    /// <summary>Parses a reference; throws a 400 <see cref="DockerApiException"/> for malformed input.</summary>
    public static ImageReference Parse(string reference)
    {
        if (!TryParse(reference, out var parsed))
        {
            throw DockerErrors.BadParameter($"invalid reference format: {reference}");
        }

        return parsed;
    }

    /// <summary>Parses a reference; returns <c>false</c> for malformed input.</summary>
    public static bool TryParse(string? reference, [NotNullWhen(true)] out ImageReference? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var remainder = reference.Trim();

        // A bare digest ("sha256:<64hex>") is a legal reference for inspect/rm.
        if (remainder.StartsWith("sha256:", StringComparison.Ordinal) &&
            DockerId.IsFullId(remainder["sha256:".Length..]))
        {
            result = new ImageReference { Digest = remainder };
            return true;
        }

        string? digest = null;
        var at = remainder.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = remainder[(at + 1)..];
            remainder = remainder[..at];
            if (digest.Length == 0 || remainder.Length == 0)
            {
                return false;
            }
        }

        string? tag = null;
        var lastColon = remainder.LastIndexOf(':');
        var lastSlash = remainder.LastIndexOf('/');
        if (lastColon > lastSlash)
        {
            tag = remainder[(lastColon + 1)..];
            remainder = remainder[..lastColon];
            if (tag.Length == 0 || remainder.Length == 0)
            {
                return false;
            }
        }

        string? domain = null;
        var path = remainder;
        var firstSlash = remainder.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash > 0)
        {
            var head = remainder[..firstSlash];
            if (head.Contains('.', StringComparison.Ordinal) ||
                head.Contains(':', StringComparison.Ordinal) ||
                string.Equals(head, "localhost", StringComparison.Ordinal))
            {
                domain = head;
                path = remainder[(firstSlash + 1)..];
            }
        }

        if (path.Length == 0)
        {
            return false;
        }

        result = new ImageReference { Domain = domain, Path = path, Tag = tag, Digest = digest };
        return true;
    }

    /// <summary>Applies Docker's defaults: no domain → <c>docker.io</c>, single-component Hub path → <c>library/</c>, no tag/digest → <c>:latest</c>.</summary>
    public ImageReference Normalize()
    {
        var domain = Domain ?? DefaultDomain;
        if (string.Equals(domain, LegacyDefaultDomain, StringComparison.Ordinal))
        {
            domain = DefaultDomain;
        }

        var path = Path;
        if (string.Equals(domain, DefaultDomain, StringComparison.Ordinal) &&
            path.Length > 0 &&
            !path.Contains('/', StringComparison.Ordinal))
        {
            path = OfficialRepositoryPrefix + path;
        }

        var tag = Tag;
        if (tag is null && Digest is null)
        {
            tag = DefaultTag;
        }

        return this with { Domain = domain, Path = path, Tag = tag };
    }

    /// <summary>The short form Docker displays: <c>alpine:latest</c>, <c>foo/bar:1</c>, <c>ghcr.io/foo/bar:1</c>.</summary>
    public string Familiar()
    {
        var normalized = Normalize();
        if (normalized.Path.Length == 0)
        {
            return normalized.Digest ?? "";
        }

        string name;
        if (string.Equals(normalized.Domain, DefaultDomain, StringComparison.Ordinal))
        {
            name = normalized.Path.StartsWith(OfficialRepositoryPrefix, StringComparison.Ordinal)
                ? normalized.Path[OfficialRepositoryPrefix.Length..]
                : normalized.Path;
        }
        else
        {
            name = $"{normalized.Domain}/{normalized.Path}";
        }

        return Render(name, normalized.Tag, normalized.Digest);
    }

    /// <summary>The fully qualified form: <c>[domain/]path[:tag][@digest]</c>.</summary>
    public override string ToString()
    {
        if (Path.Length == 0)
        {
            return Digest ?? "";
        }

        return Render(Name, Tag, Digest);
    }

    private static string Render(string name, string? tag, string? digest)
    {
        var suffix = tag is null ? "" : $":{tag}";
        var digestSuffix = digest is null ? "" : $"@{digest}";
        return $"{name}{suffix}{digestSuffix}";
    }
}
