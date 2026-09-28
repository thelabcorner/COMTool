using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ComTool.Hosts.Abstractions;

/// <summary>
/// What a launch request must do when the host family is already running.
/// There is deliberately no "launch if needed" default: a caller either
/// refuses, or it explicitly takes the running generation as-is.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostLaunchExistingInstancePolicy>))]
public enum HostLaunchExistingInstancePolicy
{
    /// <summary>Refuse to act when any running generation is observed.</summary>
    [JsonStringEnumMemberName("fail")]
    Fail,

    /// <summary>
    /// Return the running generation as preexisting, without launching and
    /// without acquiring any ownership authority over it. The serialized token
    /// says "without_ownership" so it can never be read as an ownership
    /// transfer.
    /// </summary>
    [JsonStringEnumMemberName("return_preexisting_without_ownership")]
    ReturnPreexistingWithoutOwnership
}

/// <summary>
/// Explicit launch intent for one host generation.
/// <para>
/// The host family, the COM class to activate, the version guard, the
/// existing-instance policy, the ordered argument vector and the bounded wait
/// are all stated by the caller. Nothing about the target is inferred, and no
/// launch is implied by another operation. Photoshop-shaped and
/// Illustrator-shaped hosts use the same contract; only the concrete values
/// differ.
/// </para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostLaunchSpec
{
    public const int MinLaunchTimeoutMs = 1_000;
    public const int MaxLaunchTimeoutMs = 300_000;
    public const int DefaultLaunchTimeoutMs = 60_000;
    public const int MaxArgumentCount = 16;
    public const int MaxArgumentUtf8Bytes = 1_024;
    public const int MaxProgIdLength = 128;
    public const int MaxHostFamilyLength = 64;
    public const int MaxHostVersionLength = 64;

    /// <summary>Host family, for example <c>illustrator</c> or <c>photoshop</c>.</summary>
    public required string Host { get; init; }

    /// <summary>Explicit COM class identifier requested for activation.</summary>
    public required string ProgId { get; init; }

    /// <summary>
    /// Optional explicit version guard. When set, a launched generation that
    /// reports a different host version is treated as unproven rather than
    /// accepted.
    /// </summary>
    public string? ExpectedHostVersion { get; init; }

    /// <summary>
    /// Bounded wait for the launched generation to become strongly
    /// identifiable. Exceeding it is reported as ambiguity, not as failure to
    /// launch, because the activation may still complete later.
    /// </summary>
    public int LaunchTimeoutMs { get; init; } = DefaultLaunchTimeoutMs;

    public HostLaunchExistingInstancePolicy ExistingInstance { get; init; } =
        HostLaunchExistingInstancePolicy.Fail;

    /// <summary>
    /// Bounded, explicit, ordered launch argument vector. Hosts that must be
    /// started with no arguments send an empty list; arguments are never
    /// defaulted, reordered or dropped from the launch identity.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Stable digest of the complete launch intent, recorded with the durable
    /// attempt so a later reader can prove exactly which class, policy,
    /// timeout and argument vector were requested.
    /// <para>
    /// The canonical encoding concatenates length-prefixed UTF-8 components
    /// (4-byte little-endian byte count, then the bytes) and includes an
    /// explicit argument count followed by every argument in order. Length
    /// prefixing keeps the encoding unambiguous even when a component value
    /// itself contains a separator character, so two different argument
    /// vectors can never collide.
    /// </para>
    /// </summary>
    public string SpecKey
    {
        get
        {
            var canonical = new List<byte>(256);

            AppendComponent(canonical, Host.ToLowerInvariant());
            AppendComponent(canonical, ProgId);
            AppendComponent(canonical, ExpectedHostVersion ?? string.Empty);
            AppendComponent(
                canonical,
                LaunchTimeoutMs.ToString(CultureInfo.InvariantCulture));
            AppendComponent(
                canonical,
                ExistingInstance ==
                    HostLaunchExistingInstancePolicy
                        .ReturnPreexistingWithoutOwnership
                    ? "return_preexisting_without_ownership"
                    : "fail");
            AppendComponent(
                canonical,
                Arguments.Count.ToString(CultureInfo.InvariantCulture));

            foreach (var argument in Arguments)
                AppendComponent(canonical, argument);

            return Convert.ToHexStringLower(
                SHA256.HashData([.. canonical]));
        }
    }

    private static void AppendComponent(List<byte> sink, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        sink.AddRange(BitConverter.GetBytes(bytes.Length));
        sink.AddRange(bytes);
    }
}

public sealed record HostLaunchValidationError(
    string Kind,
    string Message);

/// <summary>
/// Bounded, fail-closed validation for <see cref="HostLaunchSpec"/>. Launch
/// input is untrusted, so every field is length-checked and the host family
/// must be one the runtime was actually configured with.
/// </summary>
public static class HostLaunchSpecValidator
{
    public static HostLaunchValidationError? Validate(
        HostLaunchSpec spec,
        IReadOnlyCollection<string> configuredHostFamilies)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(configuredHostFamilies);

        if (!IsSimpleToken(spec.Host, HostLaunchSpec.MaxHostFamilyLength))
        {
            return new(
                "invalid_host_family",
                "'host' must be a non-empty host family token of at most " +
                $"{HostLaunchSpec.MaxHostFamilyLength} characters.");
        }

        if (!configuredHostFamilies.Contains(
                spec.Host,
                StringComparer.OrdinalIgnoreCase))
        {
            return new(
                "host_family_not_configured",
                $"Host family '{spec.Host}' is not configured on this runtime.");
        }

        if (!IsSimpleToken(spec.ProgId, HostLaunchSpec.MaxProgIdLength) ||
            !spec.ProgId.Contains('.', StringComparison.Ordinal))
        {
            return new(
                "invalid_progid",
                "'progId' must be an explicit dotted COM class of at most " +
                $"{HostLaunchSpec.MaxProgIdLength} characters.");
        }

        if (spec.ExpectedHostVersion is { } version &&
            (string.IsNullOrWhiteSpace(version) ||
             version.Length > HostLaunchSpec.MaxHostVersionLength ||
             ContainsControl(version)))
        {
            return new(
                "invalid_host_version",
                "'expectedHostVersion' must be non-empty, control-free and at " +
                $"most {HostLaunchSpec.MaxHostVersionLength} characters.");
        }

        if (spec.LaunchTimeoutMs is <
            HostLaunchSpec.MinLaunchTimeoutMs or
            > HostLaunchSpec.MaxLaunchTimeoutMs)
        {
            return new(
                "invalid_launch_timeout",
                "'launchTimeoutMs' must be between " +
                $"{HostLaunchSpec.MinLaunchTimeoutMs} and " +
                $"{HostLaunchSpec.MaxLaunchTimeoutMs} ms.");
        }

        if (!Enum.IsDefined(spec.ExistingInstance))
        {
            return new(
                "invalid_launch_existing_instance_policy",
                "'existingInstance' must be 'fail' or " +
                "'return_preexisting_without_ownership'.");
        }

        if (spec.Arguments is null ||
            spec.Arguments.Count > HostLaunchSpec.MaxArgumentCount)
        {
            return new(
                "invalid_launch_arguments",
                "'arguments' must be present and contain at most " +
                $"{HostLaunchSpec.MaxArgumentCount} entries.");
        }

        foreach (var argument in spec.Arguments)
        {
            if (string.IsNullOrEmpty(argument) ||
                Encoding.UTF8.GetByteCount(argument) >
                    HostLaunchSpec.MaxArgumentUtf8Bytes ||
                ContainsControl(argument))
            {
                return new(
                    "invalid_launch_arguments",
                    "Each launch argument must be non-empty, control-free and " +
                    $"at most {HostLaunchSpec.MaxArgumentUtf8Bytes} UTF-8 bytes.");
            }
        }

        return null;
    }

    private static bool IsSimpleToken(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        !ContainsControl(value) &&
        !value.Any(char.IsWhiteSpace);

    private static bool ContainsControl(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return true;
        }

        return false;
    }
}
