namespace ComTool.Supervisor;

public sealed record TargetHostTerminationResult(
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    bool KillIssued,
    bool AlreadyExited,
    bool ExitObserved,
    bool WorkerAbortIssued);