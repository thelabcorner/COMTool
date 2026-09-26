namespace ComTool.Supervisor;

public sealed record WorkerBrokerOptions
{
    public required string WorkerExecutablePath { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxFrameBytes { get; init; } = 1024 * 1024;

    public bool CreateNoWindow { get; init; } = true;
}
