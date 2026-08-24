using System.Security.Cryptography;

namespace Cider.Core.Ids;

/// <summary>Docker's 64 lowercase hex character object ids (containers, execs, networks, images).</summary>
public static class DockerId
{
    /// <summary>Length of a full Docker id in characters.</summary>
    public const int FullLength = 64;

    /// <summary>Length of the short id Docker prints (<c>docker ps</c>).</summary>
    public const int ShortLength = 12;

    /// <summary>Generates a fresh 64 hex character id from a cryptographic RNG.</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[FullLength / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary><c>true</c> when <paramref name="value"/> is exactly 64 hex characters.</summary>
    public static bool IsFullId(string? value) =>
        value is { Length: FullLength } && IsHex(value);

    /// <summary><c>true</c> when <paramref name="value"/> is 1..64 hex characters (a usable id prefix).</summary>
    public static bool IsHexPrefix(string? value) =>
        value is { Length: > 0 and <= FullLength } && IsHex(value);

    /// <summary>The first 12 characters of <paramref name="id"/> (or all of it when shorter).</summary>
    public static string Short(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length <= ShortLength ? id : id[..ShortLength];
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
