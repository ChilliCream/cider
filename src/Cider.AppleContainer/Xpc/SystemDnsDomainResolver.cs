using System.Text.Json;
using Cider.AppleContainer.Cli;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Resolves <c>containerSystemConfig.dns.domain</c> (docs/spikes/xpc/02-apiserver-xpc-protocol.md
/// §3.4/§8.11 sample: <c>"dns": { "nameservers": [...], "domain": "test", ... }</c>) — the same value
/// the CLI's own <c>Utility.swift</c> reads as <c>--dns-domain ?? systemConfig.dns.domain</c> for the
/// FQDN rule <see cref="ContainerConfigurationBuilder.Build"/> applies in <c>BuildNetworks</c>.
/// There is no XPC route for this (same non-goal as <see cref="InitImageResolver"/>'s own remarks:
/// three more round trips this task excludes), so this mirrors <see cref="InitImageResolver"/>'s own
/// two-step resolution — a best-effort <c>config.toml</c> <c>[dns]\ndomain = "…"</c> read, else
/// <c>container system property list --format json</c>'s <c>dns.domain</c> — cached for this
/// runtime's lifetime. Unlike the init image, a domain is optional (most installs have none set —
/// confirmed live: <c>"dns":{}</c>), so an unresolved domain is simply <c>null</c>, never a thrown
/// failure.
/// </summary>
internal sealed class SystemDnsDomainResolver
{
    private static readonly string[] ConfigFilePaths =
    [
        Path.Combine(HomeDirectory(), ".config", "container", "config.toml"),
        Path.Combine(HomeDirectory(), "Library", "Application Support", "com.apple.container", "config", "config.toml"),
        "/usr/local/etc/container/config.toml",
    ];

    private readonly ContainerCli _cli;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _resolved;
    private string? _domain;

    public SystemDnsDomainResolver(AppleContainerOptions options, ILogger logger)
    {
        _cli = new ContainerCli(options, logger);
    }

    /// <summary>Returns the cached domain (or <c>null</c> when none is configured), resolving it on
    /// first call. Never throws — a CLI failure or malformed output just means "no domain".</summary>
    public async Task<string?> ResolveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resolved)
            {
                return _domain;
            }

            _domain = await ResolveDomainAsync(ct).ConfigureAwait(false);
            _resolved = true;
            return _domain;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ResolveDomainAsync(CancellationToken ct)
    {
        foreach (var path in ConfigFilePaths)
        {
            if (TryReadDnsDomain(path) is { Length: > 0 } fromConfig)
            {
                return fromConfig;
            }
        }

        return await ReadFromCliAsync(ct).ConfigureAwait(false);
    }

    /// <summary><c>container system property list --format json</c>'s <c>dns.domain</c> — best
    /// effort, matching <see cref="InitImageResolver.ReadFromCliAsync"/>'s own posture except that a
    /// failure here is not fatal (there is no CLI-fallback-of-a-fallback to reach for: a domain is
    /// genuinely optional).</summary>
    private async Task<string?> ReadFromCliAsync(CancellationToken ct)
    {
        try
        {
            var result = await _cli.RunAsync(["system", "property", "list", "--format", "json"], ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return null;
            }

            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.TryGetProperty("dns", out var dns) &&
                dns.TryGetProperty("domain", out var domain) &&
                domain.ValueKind == JsonValueKind.String &&
                domain.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Falls through to null below — malformed CLI output means "no domain resolvable".
        }

        return null;
    }

    /// <summary>Best-effort <c>[dns]\ndomain = "…"</c> read from one TOML file — same single-key scan
    /// as <see cref="InitImageResolver.TryReadVminitImage"/>, not a general TOML parser.</summary>
    private static string? TryReadDnsDomain(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var inDnsSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inDnsSection = string.Equals(line, "[dns]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inDnsSection)
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || !line[..equals].Trim().Equals("domain", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(equals + 1)..].Trim().Trim('"');
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static string HomeDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
