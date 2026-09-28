using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Runtime.Artifacts;

/// <summary>What the offload seam decided to do with a producer's result.</summary>
public enum ArtifactOffloadOutcome
{
    /// <summary>The producer has not opted in. Nothing is offloaded.</summary>
    Disabled,

    /// <summary>No result to consider.</summary>
    NotApplicable,

    /// <summary>Small enough to return inline unchanged.</summary>
    Inline,

    /// <summary>
    /// Large enough that the producer should store it and return a
    /// reference. Still advisory: storing is the producer's explicit choice.
    /// </summary>
    OffloadRecommended
}

/// <summary>
/// The measurement a producer needs before it decides whether to offload.
/// </summary>
public sealed record ArtifactOffloadPlan
{
    public required ArtifactOffloadOutcome Outcome { get; init; }

    /// <summary>
    /// Real UTF-8 byte length of the serialized payload, measured with the
    /// caller's own serializer options so it equals what would be transmitted.
    /// </summary>
    public required long ByteCount { get; init; }

    /// <summary>
    /// Character length of the same payload. Recorded only so the difference is
    /// observable: the legacy tool offloaded on a character count and reported
    /// it as a byte count, which understates non-ASCII and binary content.
    /// </summary>
    public required int CharacterCount { get; init; }

    public required string MediaType { get; init; }

    public required long ThresholdByteCount { get; init; }

    public bool RequiresOffload =>
        Outcome == ArtifactOffloadOutcome.OffloadRecommended;
}

/// <summary>
/// Integration contract for large operation results. The runtime supervisor
/// uses this contract only after a host result is known: the mutation ledger
/// persists the canonical full <see cref="OperationResult"/> first, then the
/// outward response may replace only its payload with a self-describing opaque
/// artifact envelope. Existing artifact envelopes are never recursively
/// offloaded, and the envelope always records the original result kind so the
/// semantic type change is explicit rather than silent.
/// </summary>
public static class ArtifactResultOffload
{
    /// <summary>
    /// Matches the legacy offload threshold, so V2 drops content at the same
    /// size the legacy tool did rather than moving the boundary unnoticed.
    /// </summary>
    public const long DefaultThresholdByteCount = 32 * 1024;

    public const string ResultMediaType = ArtifactMediaTypes.ApplicationJson;

    /// <summary>
    /// Chooses the closest lossless media type for the protocol value. String
    /// values are stored as their actual UTF-8 text rather than JSON string
    /// syntax; every other protocol value keeps canonical JSON bytes.
    /// </summary>
    public static string MediaTypeFor(ProtocolValue? result) =>
        result?.Kind == "string"
            ? ArtifactMediaTypes.TextPlainUtf8
            : ResultMediaType;

    /// <summary>
    /// Measures a producer's result without mutating it. Pass the exact
    /// serializer options the runtime transmits results with; measuring with
    /// different options would report a size that is not the transmitted size.
    /// </summary>
    public static ArtifactOffloadPlan Plan(
        ProtocolValue? result,
        JsonSerializerOptions payloadOptions,
        long thresholdByteCount = DefaultThresholdByteCount,
        bool enabled = false)
    {
        ArgumentNullException.ThrowIfNull(payloadOptions);

        if (result?.Value is not { } value)
        {
            return new ArtifactOffloadPlan
            {
                Outcome = ArtifactOffloadOutcome.NotApplicable,
                ByteCount = 0,
                CharacterCount = 0,
                MediaType = MediaTypeFor(result),
                ThresholdByteCount = thresholdByteCount
            };
        }

        if (thresholdByteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdByteCount),
                thresholdByteCount,
                "Offload threshold must be positive.");
        }

        var utf8 = JsonSerializer.SerializeToUtf8Bytes(value, payloadOptions);

        var outcome = !enabled
            ? ArtifactOffloadOutcome.Disabled
            : utf8.LongLength >= thresholdByteCount
                ? ArtifactOffloadOutcome.OffloadRecommended
                : ArtifactOffloadOutcome.Inline;

        return new ArtifactOffloadPlan
        {
            Outcome = outcome,
            ByteCount = utf8.LongLength,
            CharacterCount = Encoding.UTF8.GetCharCount(utf8),
            MediaType = MediaTypeFor(result),
            ThresholdByteCount = thresholdByteCount
        };
    }

    /// <summary>
    /// Builds the explicit stand-in a producer returns in place of an offloaded
    /// payload.
    /// </summary>
    /// <remarks>
    /// The envelope is distinguishable from any real result by construction:
    /// <c>offloaded</c> is the constant <c>true</c>, and the original result
    /// kind is carried forward so a consumer can tell an object result from a
    /// string result without guessing. No content is echoed inline; a consumer
    /// that wants a preview asks for a bounded range instead.
    /// </remarks>
    public static ProtocolValue Envelope(
        ArtifactDescriptor descriptor,
        string originalResultKind)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (originalResultKind is not (
                "null" or
                "string" or
                "number" or
                "boolean" or
                "array" or
                "object"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalResultKind),
                originalResultKind,
                "Original result kind must be a protocol value kind.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                offloaded = true,
                artifact = new
                {
                    artifactId = descriptor.ArtifactId,
                    sha256 = descriptor.Sha256,
                    byteCount = descriptor.ByteCount,
                    mediaType = descriptor.MediaType,
                    encoding = descriptor.Encoding,
                    createdAt = descriptor.CreatedAt,
                    expiresAt = descriptor.ExpiresAt,
                    revision = descriptor.Revision
                },
                originalResultKind
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        return ProtocolValue.From(payload);
    }
}
