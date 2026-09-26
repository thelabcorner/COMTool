using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationPolicy(
    int? QueueTimeoutMs = null,
    int? RetryBudgetMs = null,
    int? OperationSoftTimeoutMs = null,
    int? WorkerWatchdogMs = null,
    bool AllowReplayAfterAmbiguous = false,
    string? LeaseId = null);
