using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<TargetState>))]
public enum TargetState
{
    [JsonStringEnumMemberName("known")] Known,
    [JsonStringEnumMemberName("known_changed")] KnownChanged,
    [JsonStringEnumMemberName("busy")] Busy,
    [JsonStringEnumMemberName("reconciliation_required")] ReconciliationRequired,
    [JsonStringEnumMemberName("unavailable")] Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationStatus>))]
public enum OperationStatus
{
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("invalid_request")] InvalidRequest,
    [JsonStringEnumMemberName("unsupported_operation")] UnsupportedOperation,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("host_busy")] HostBusy,
    [JsonStringEnumMemberName("target_unavailable")] TargetUnavailable,
    [JsonStringEnumMemberName("reconciliation_required")] ReconciliationRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionState>))]
public enum ExecutionState
{
    [JsonStringEnumMemberName("not_started")] NotStarted,
    [JsonStringEnumMemberName("started")] Started,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("ambiguous")] Ambiguous
}

[JsonConverter(typeof(JsonStringEnumConverter<MutationClass>))]
public enum MutationClass
{
    [JsonStringEnumMemberName("read_only")] ReadOnly,
    [JsonStringEnumMemberName("idempotent_write")] IdempotentWrite,
    [JsonStringEnumMemberName("conditional_write")] ConditionalWrite,
    [JsonStringEnumMemberName("non_idempotent_write")] NonIdempotentWrite,
    [JsonStringEnumMemberName("document_lifecycle")] DocumentLifecycle,
    [JsonStringEnumMemberName("external_side_effect")] ExternalSideEffect,
    [JsonStringEnumMemberName("unknown")] Unknown
}
