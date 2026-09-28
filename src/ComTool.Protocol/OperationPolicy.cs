using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationPolicy(
    int? WorkerWatchdogMs = null,
    string? LeaseId = null,
    int? RetryBudgetMs = null)
{
    public const int MinWorkerWatchdogMs = 100;
    public const int MaxWorkerWatchdogMs = 3_600_000;

    /// <summary>
    /// Budget for replay-safe COM retries that are certified rejected before
    /// execution. Zero disables retries while still allowing the initial call.
    /// This budget is independent from the worker watchdog.
    /// </summary>
    public const int MinRetryBudgetMs = 0;
    public const int MaxRetryBudgetMs = 3_600_000;
    public const int DefaultRetryBudgetMs = 2_000;

    public static bool IsValidRetryBudget(int value) =>
        value is >= MinRetryBudgetMs and <= MaxRetryBudgetMs;
}
