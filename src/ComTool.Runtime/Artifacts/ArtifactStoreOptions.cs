namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Bounds and clock for one artifact store. Every knob here exists to keep
/// disk, memory, and wall-clock cost of the store predictable.
/// </summary>
public sealed record ArtifactStoreOptions
{
    public const long DefaultMaxArtifactByteCount = 64L * 1024 * 1024;

    /// <summary>
    /// COM Tool V2 runtime state root, i.e. <c>RuntimeStateLayout.Root</c>.
    /// The store creates its own versioned subtree beneath it and never writes
    /// anywhere else in the state root.
    /// </summary>
    public required string StateRoot { get; init; }

    /// <summary>
    /// Single source of time for expiry decisions. Injectable so retention
    /// behavior is deterministic under test instead of wall-clock dependent.
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } =
        static () => DateTimeOffset.UtcNow;

    public TimeSpan DefaultTimeToLive { get; init; } =
        TimeSpan.FromHours(24);

    /// <summary>
    /// Ceiling on caller-requested retention. Bounds how long the store can be
    /// made to retain content by any single request.
    /// </summary>
    public TimeSpan MaxTimeToLive { get; init; } =
        TimeSpan.FromDays(7);

    /// <summary>
    /// Largest artifact accepted by <c>Put</c>. Bounds store growth from one
    /// producer.
    /// </summary>
    public long MaxArtifactByteCount { get; init; } =
        DefaultMaxArtifactByteCount;

    /// <summary>
    /// Largest single read materialized into memory, including a whole-artifact
    /// read. Bounds client memory. Larger content is reachable through
    /// <c>Open</c> or through explicit ranges.
    /// </summary>
    public long MaxReadByteCount { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// Maximum records examined, and content files reclaimed, by one
    /// <c>DeleteExpired</c> or <c>Sweep</c> call. Bounds cleanup latency.
    /// Sweep reports <c>Incomplete</c> when it stops at this limit.
    /// </summary>
    public int SweepBatchLimit { get; init; } = 256;

    /// <summary>
    /// Age after which a store-owned <c>incoming/*.part</c> file is considered
    /// abandoned by a crashed producer and may be reclaimed by <c>Sweep</c>.
    /// The delay deliberately avoids racing an in-flight <c>Put</c>, whose
    /// staging write occurs before the commit gate is acquired.
    /// </summary>
    public TimeSpan StaleIncomingMaxAge { get; init; } =
        TimeSpan.FromHours(24);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StateRoot);

        if (Clock is null)
            throw new ArgumentNullException(
                nameof(Clock),
                "Artifact store requires a clock.");

        if (DefaultTimeToLive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(DefaultTimeToLive),
                DefaultTimeToLive,
                "Default retention must be positive.");

        if (MaxTimeToLive < DefaultTimeToLive)
            throw new ArgumentOutOfRangeException(
                nameof(MaxTimeToLive),
                MaxTimeToLive,
                "Maximum retention must be at least the default retention.");

        if (MaxArtifactByteCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxArtifactByteCount),
                MaxArtifactByteCount,
                "Artifact size bound must be positive.");

        if (MaxReadByteCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxReadByteCount),
                MaxReadByteCount,
                "Read size bound must be positive.");

        if (SweepBatchLimit <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(SweepBatchLimit),
                SweepBatchLimit,
                "Sweep batch limit must be positive.");

        if (StaleIncomingMaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(StaleIncomingMaxAge),
                StaleIncomingMaxAge,
                "Stale incoming retention must be positive.");
    }
}
