using System.Security.Cryptography;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Mints and validates opaque artifact handles.
/// </summary>
/// <remarks>
/// The handle is 128 bits of cryptographic randomness in a fixed character
/// set. It is deliberately NOT derived from the content, from a timestamp, or
/// from a path segment, and it is never used to build a filesystem path — the
/// store maps a validated id to a sidecar record and the sidecar names the
/// content address. Traversal and enumeration are therefore structurally
/// impossible rather than filtered after the fact.
/// </remarks>
public static class ArtifactId
{
    public const string Prefix = "art_";
    public const int HexLength = 32;

    public static string NewId()
    {
        Span<byte> entropy = stackalloc byte[HexLength / 2];
        RandomNumberGenerator.Fill(entropy);
        return Prefix + Convert.ToHexString(entropy).ToLowerInvariant();
    }

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Prefix.Length + HexLength)
            return false;

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        for (var index = Prefix.Length; index < value.Length; index++)
        {
            var c = value[index];
            var isHex =
                c is >= '0' and <= '9' ||
                c is >= 'a' and <= 'f';
            if (!isHex)
                return false;
        }

        return true;
    }

    public static string Require(string? value) =>
        IsValid(value)
            ? value!
            : throw new ArtifactStoreException(
                "artifact_id_invalid",
                "Artifact id must be 'art_' followed by exactly " +
                $"{HexLength} lowercase hex characters.");
}
