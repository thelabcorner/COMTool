using System.Collections.Frozen;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// The bounded media-type vocabulary the store will record.
/// </summary>
/// <remarks>
/// A closed set is deliberate. Media type is client-visible metadata, so an
/// open vocabulary would let a caller smuggle a header-like or
/// path-like string into a descriptor that is echoed back across the
/// protocol. Anything outside the set is rejected with a truthful error
/// rather than silently coerced.
/// </remarks>
public static class ArtifactMediaTypes
{
    public const string ApplicationJson = "application/json";
    public const string ApplicationNdjson = "application/x-ndjson";
    public const string ApplicationOctetStream = "application/octet-stream";
    public const string TextPlainUtf8 = "text/plain; charset=utf-8";
    public const string TextCsvUtf8 = "text/csv; charset=utf-8";

    public const string Utf8 = "utf-8";
    public const string Binary = "binary";

    private static readonly FrozenSet<string> Supported = new[]
    {
        ApplicationJson,
        ApplicationNdjson,
        ApplicationOctetStream,
        TextPlainUtf8,
        TextCsvUtf8
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsSupported(string? mediaType) =>
        mediaType is not null && Supported.Contains(mediaType);

    public static string RequireMediaType(string? mediaType) =>
        IsSupported(mediaType)
            ? mediaType!
            : throw new ArtifactStoreException(
                "artifact_media_type_unsupported",
                $"Unsupported artifact media type. Supported: " +
                $"{string.Join(", ", Supported.Order(StringComparer.Ordinal))}.");

    public static string RequireEncoding(string? encoding) =>
        encoding is Utf8 or Binary
            ? encoding
            : throw new ArtifactStoreException(
                "artifact_encoding_unsupported",
                "Unsupported artifact encoding. Supported: " +
                $"{Utf8}, {Binary}.");

    /// <summary>
    /// The encoding a media type implies when the producer does not state one.
    /// </summary>
    public static string DefaultEncodingFor(string mediaType) =>
        mediaType == ApplicationOctetStream ? Binary : Utf8;
}
