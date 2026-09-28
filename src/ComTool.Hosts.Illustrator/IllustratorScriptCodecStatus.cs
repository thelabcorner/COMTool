using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Read-only observability for the optional shared ESON script codec. This
/// operation never installs or mutates the target; it only runs the fixed
/// presence probe already used by script execution.
/// </summary>
internal static class IllustratorScriptCodecStatus
{
    public const string Operation = "script.codec.status";
    private const string CodecName = "eson";

    public static OperationResult Execute(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string>? scriptExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Input.ValueKind is not (
                JsonValueKind.Object or
                JsonValueKind.Null or
                JsonValueKind.Undefined))
        {
            return Invalid(
                request,
                "script.codec.status input must be an empty JSON object.");
        }

        if (request.Input.ValueKind == JsonValueKind.Object &&
            request.Input.EnumerateObject().Any())
        {
            return Invalid(
                request,
                "script.codec.status input must not contain fields.");
        }

        // Force embedded provenance verification before talking to the host.
        _ = IllustratorEsonRuntime.Source;

        var executeScript =
            scriptExecutor ??
            ((object application, string source, int executionMode) =>
                IllustratorComInterop.ExecuteJavaScript(
                    application,
                    source,
                    executionMode));

        string probe;
        try
        {
            probe = executeScript(
                appObject,
                IllustratorScriptEval.EsonPresenceProbeSource,
                IllustratorScriptEval.NeverShowDebugger);
        }
        catch (HostAdapterException ex)
        {
            return Failure(request, ex);
        }

        var installed = string.Equals(
            probe,
            IllustratorScriptEval.EsonReadyToken,
            StringComparison.Ordinal);

        var payload = JsonSerializer.SerializeToElement(new
        {
            codec = CodecName,
            optional = true,
            expectedSha256 = IllustratorEsonRuntime.ExpectedSha256,
            embeddedResource = IllustratorEsonRuntime.ResourceName,
            embeddedVerified = true,
            installed,
            installedSha256Matches = installed,
            presenceProbe = "fixed_read_only",
            executionMode = IllustratorScriptEval.NeverShowDebugger,
            fallback = "bootstrap_on_demand",
            bootstrapRequired = !installed
        });

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(payload)
        };
    }

    private static OperationResult Invalid(
        OperationRequest request,
        string message) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.InvalidRequest,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = "invalid_script_codec_status_request",
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted
            }
        };

    private static OperationResult Failure(
        OperationRequest request,
        HostAdapterException ex) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = ex.Retryable
                ? OperationStatus.HostBusy
                : OperationStatus.Failed,
            TargetState = ex.Retryable
                ? TargetState.Busy
                : TargetState.Known,
            Error = ex.ToProtocolError(
                ex.Retryable
                    ? "retry_within_budget"
                    : "inspect_script_codec_state"),
            Evidence = ex.Evidence
        };
}
