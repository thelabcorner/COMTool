using System.Text.Json;
using ComTool.Runtime;

namespace ComTool.Supervisor;

public sealed record MutationIncidentResolution(
    string IncidentRequestId,
    string Resolution,
    string Rationale,
    DateTimeOffset ResolvedAt,
    TargetStateSnapshot State,
    JsonElement? Evidence);
