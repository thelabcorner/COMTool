namespace ComTool.Supervisor;

public sealed record WorkerBrokerOptions
{
    public required string WorkerExecutablePath { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The private worker channel intentionally shares the public 1 MiB bound.
    /// Large successful result payloads cross this same authenticated channel
    /// as bounded broker artifact chunks and are reconstructed by the sole
    /// supervisor before mutation-ledger finalization and artifact-store commit.
    /// </summary>
    public int MaxFrameBytes { get; init; } = 1024 * 1024;

    public bool CreateNoWindow { get; init; } = true;
}
