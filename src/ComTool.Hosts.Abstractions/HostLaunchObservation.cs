using System.Text.Json.Serialization;

namespace ComTool.Hosts.Abstractions;

/// <summary>
/// How a host generation came into existence relative to one launch attempt.
/// <para>
/// <see cref="Unproven"/> exists so an undecidable activation outcome is
/// reported truthfully instead of being rounded to "launched" or "preexisting".
/// Neither <see cref="Unproven"/> nor
/// <see cref="PreexistingWithoutOwnership"/> ever confers ownership.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostLaunchOwnership>))]
public enum HostLaunchOwnership
{
    /// <summary>
    /// The generation already existed. This runtime did not start it and holds
    /// no authority over it.
    /// </summary>
    [JsonStringEnumMemberName("preexisting_without_ownership")]
    PreexistingWithoutOwnership,

    /// <summary>The generation was created by this launch dispatch.</summary>
    [JsonStringEnumMemberName("launched_by_runtime")]
    LaunchedByRuntime,

    /// <summary>The activation outcome could not be attributed either way.</summary>
    [JsonStringEnumMemberName("unproven")]
    Unproven
}

/// <summary>
/// What the host side reports back after a launch dispatch. This is the
/// worker-facing result contract; the runtime never learns ownership by
/// observing a process id alone.
/// </summary>
public sealed record HostLaunchObservation
{
    /// <summary>Host family the launch was requested for.</summary>
    public required string Host { get; init; }

    /// <summary>COM class the launch actually acted on.</summary>
    public required string ProgId { get; init; }

    public required HostLaunchOwnership Ownership { get; init; }

    /// <summary>
    /// Strong identity of the resulting generation. Required for
    /// <see cref="HostLaunchOwnership.LaunchedByRuntime"/>; may be absent for
    /// an unproven outcome.
    /// </summary>
    public HostTargetIdentity? Identity { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>HRESULT of the preexisting-instance attach attempt, if any.</summary>
    public int? AttachHResult { get; init; }

    /// <summary>HRESULT of the class activation, if activation was attempted.</summary>
    public int? ActivationHResult { get; init; }

    /// <summary>Machine-readable reason the outcome could not be attributed.</summary>
    public string? AmbiguityKind { get; init; }

    public string? AmbiguityMessage { get; init; }

    /// <summary>
    /// Open document count observed in the resulting generation, or -1 when
    /// unknown. Unknown is deliberately not zero: an explicit quit decision
    /// must refuse to treat an unreported count as "no documents".
    /// </summary>
    public int OpenDocumentCount { get; init; } = -1;
}
