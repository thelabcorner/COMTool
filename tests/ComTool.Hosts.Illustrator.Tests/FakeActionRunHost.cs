using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Disposable fake action host. It contacts nothing, mutates nothing, and
/// can only fail in the ways the real COM surface reports failures, so no
/// assertion can pass by inferring dispatch from elapsed time.
/// </summary>
internal sealed class FakeActionRunHost : IActionRunHost
{
    private readonly Queue<bool> _actionIsRunning = new();

    /// <summary>Used once the scripted queue is exhausted.</summary>
    public bool DefaultActionIsRunning { get; set; }

    public int UserInteractionLevel { get; set; } = 2;

    public List<string> Calls { get; } = [];

    public List<(string Name, string ActionSet, bool Dialogs)> Dispatches { get; } = [];

    public int ActionIsRunningReads { get; private set; }

    public int UserInteractionLevelReads { get; private set; }

    public HostAdapterException? ReadActionIsRunningFailure { get; set; }

    public HostAdapterException? ReadUserInteractionLevelFailure { get; set; }

    public HostAdapterException? DispatchFailure { get; set; }

    public void Script(params bool[] actionIsRunning)
    {
        foreach (var value in actionIsRunning)
            _actionIsRunning.Enqueue(value);
    }

    public bool ReadActionIsRunning()
    {
        Calls.Add("read:ActionIsRunning");
        ActionIsRunningReads++;

        if (ReadActionIsRunningFailure is { } failure)
            throw failure;

        return _actionIsRunning.Count > 0
            ? _actionIsRunning.Dequeue()
            : DefaultActionIsRunning;
    }

    public int ReadUserInteractionLevel()
    {
        Calls.Add("read:UserInteractionLevel");
        UserInteractionLevelReads++;

        if (ReadUserInteractionLevelFailure is { } failure)
            throw failure;

        return UserInteractionLevel;
    }

    public void DoScript(string name, string actionSet, bool dialogs)
    {
        Calls.Add("DoScript");
        Dispatches.Add((name, actionSet, dialogs));

        if (DispatchFailure is { } failure)
            throw failure;
    }

    public static HostAdapterException RejectedBeforeExecution() =>
        new(
            "host_busy",
            "The host rejected the call before execution (RPC_E_CALL_REJECTED).",
            retryable: true,
            ExecutionState.NotStarted,
            unchecked((int)0x80010001));

    public static HostAdapterException AmbiguousAfterDispatch() =>
        new(
            "com_failure",
            "The transport failed after the call was accepted.",
            retryable: false,
            ExecutionState.Ambiguous,
            unchecked((int)0x80010105));
}

/// <summary>
/// Virtual-time clock. <see cref="Wait"/> advances the clock instead of
/// sleeping, so watchdog expiry is deterministic and no assertion depends on
/// real elapsed time.
/// </summary>
internal sealed class FakeActionRunClock : IActionRunClock
{
    public TimeSpan Elapsed { get; private set; }

    public List<TimeSpan> Waits { get; } = [];

    public void Wait(TimeSpan duration)
    {
        Waits.Add(duration);
        Elapsed += duration;
    }
}

internal static class ActionRunRequests
{
    public static OperationRequest Create(
        string inputJson,
        int? workerWatchdogMs = null)
    {
        using var document = System.Text.Json.JsonDocument.Parse(inputJson);
        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "op-action-1",
            Operation = IllustratorActionRun.Operation,
            Target = new TargetRef("illustrator", "illustrator-30.1", 0),
            Input = document.RootElement.Clone(),
            Policy = workerWatchdogMs is { } watchdogMs
                ? new OperationPolicy(WorkerWatchdogMs: watchdogMs)
                : null
        };
    }
}
