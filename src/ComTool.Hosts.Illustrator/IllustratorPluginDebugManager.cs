using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Typed native plug-in debug facet for the AIPDebug/1 contract.
///
/// The surface is fixed: <c>discover</c>, <c>info</c>, <c>logs</c>,
/// <c>snapshot</c>, <c>stats</c>, <c>clear</c>, <c>break</c>. There is no
/// arbitrary selector passthrough here; product-specific plug-in messaging
/// stays on <see cref="IllustratorPluginMessage"/>.
///
/// Two operations split the facet by effect so the runtime catalog can hold a
/// real mutation class per operation instead of one blurred class for the whole
/// debug surface: <c>plugin.debug.diagnostics</c> is read-only, and
/// <c>plugin.debug.control</c> is an external side effect that requires a
/// target lease and no-replay protection.
///
/// Two transports exist. The COM lane is the host-thread bootstrap and control
/// plane. The direct VectorIPC lane is a transport optimization used only for
/// diagnostics that stay available while Illustrator's host thread is blocked;
/// it is entered only after the runtime has proven endpoint provenance for this
/// exact Illustrator generation, and the native client re-verifies the actual
/// named-pipe peer PID. Nothing here makes the runtime trust a caller-supplied
/// endpoint, an on-disk cache, or its own memo.
/// </summary>
internal sealed class IllustratorPluginDebugManager
{
    public const string DiagnosticsOperation = "plugin.debug.diagnostics";
    public const string ControlOperation = "plugin.debug.control";

    public const string Protocol = "AIPDebug/1";

    internal const int DefaultLogLimit = 100;
    internal const int MinLogLimit = 1;
    internal const int MaxLogLimit = 512;
    internal const int MaxPluginUtf8Bytes = 256;
    internal const int EndpointMaxLength = 80;
    internal const int MaxResponseUtf8Bytes = 512 * 1024;
    internal const string TransportAuto = "auto";
    internal const string TransportCom = "com";
    internal const string TransportVectorIpc = "ipc";

    /// <summary>
    /// The read-only half. <c>discover</c> is included because it only reads
    /// plug-in identity; it is still host-thread-only at execution time.
    /// </summary>
    private static readonly HashSet<string> DiagnosticActions =
        new(StringComparer.Ordinal)
        {
            "discover",
            "info",
            "logs",
            "snapshot",
            "stats"
        };

    /// <summary>The effectful half. Each needs a lease and no-replay.</summary>
    private static readonly HashSet<string> ControlActions =
        new(StringComparer.Ordinal)
        {
            "clear",
            "break"
        };

    private static readonly HashSet<string> Transports =
        new(StringComparer.Ordinal)
        {
            TransportAuto,
            TransportCom,
            TransportVectorIpc
        };

    private readonly HostTargetIdentity _identity;
    private readonly IAipDebugCtlInvoker _ctl;
    private readonly Func<object, string, object?[], object?> _comInvoker;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, AipDebugEndpointProvenance> _provenance =
        new(StringComparer.Ordinal);

    public IllustratorPluginDebugManager(
        HostTargetIdentity identity,
        IAipDebugCtlInvoker ctl,
        Func<object, string, object?[], object?>? comInvoker = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(ctl);

        _identity = identity;
        _ctl = ctl;
        _comInvoker = comInvoker ??
                      ((object target, string method, object?[] args) =>
                          IllustratorComInterop.InvokeMutationMethod(
                              target,
                              method,
                              args));
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public OperationResult Execute(
        OperationRequest request,
        object appObject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appObject);

        PluginDebugRequest parsed;
        try
        {
            parsed = PluginDebugRequest.Parse(request);
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(request, "invalid_plugin_debug_request", ex.Message);
        }

        if (IsControlOperation(request.Operation))
        {
            if (string.IsNullOrWhiteSpace(request.Policy?.LeaseId))
            {
                return InvalidRequest(
                    request,
                    "plugin_debug_lease_required",
                    "plugin.debug.control requires policy.leaseId because clear and " +
                    "break change plug-in debug state.");
            }
        }

        // discover establishes endpoint provenance through the host thread, so
        // it is never eligible for the direct VectorIPC lane.
        var transport = parsed.Action == "discover"
            ? TransportCom
            : parsed.Transport;
        if (parsed.Action == "discover" &&
            transport == TransportVectorIpc)
        {
            return InvalidRequest(
                request,
                "invalid_plugin_debug_transport",
                "discover runs on the host-thread lane and cannot use the direct " +
                "VectorIPC transport.");
        }

        var selector = Protocol + "/" + parsed.Action;
        var input = parsed.Action == "logs"
            ? FormatLogQuery(parsed.After, parsed.Limit)
            : string.Empty;

        string? fallback = null;
        if (transport != TransportCom)
        {
            if (TryGetVerifiedProvenance(
                    parsed,
                    out var provenance,
                    out var refusal))
            {
                return ExecuteVectorIpc(
                    request,
                    appObject,
                    parsed,
                    selector,
                    input,
                    provenance,
                    cancellationToken);
            }

            if (transport == TransportVectorIpc)
            {
                return FailureNotStarted(
                    request,
                    refusal!.Kind,
                    refusal.Message,
                    ["run_plugin_debug_discover", "inspect_target_generation"]);
            }

            if (IsControlOperation(request.Operation))
            {
                // An effectful debug action never silently changes transport
                // after a refused direct attempt; the refusal is reported.
                return FailureNotStarted(
                    request,
                    refusal!.Kind,
                    refusal.Message,
                    ["run_plugin_debug_discover", "inspect_target_generation"]);
            }

            fallback = refusal!.Message;
        }

        return ExecuteCom(
            request,
            appObject,
            parsed,
            selector,
            input,
            TransportCom,
            fallback);
    }

    private OperationResult ExecuteVectorIpc(
        OperationRequest request,
        object appObject,
        PluginDebugRequest parsed,
        string selector,
        string input,
        AipDebugEndpointProvenance provenance,
        CancellationToken cancellationToken)
    {
        var invocation = new AipDebugCtlInvocation(
            provenance.Endpoint,
            parsed.Action,
            provenance.ServerProcessId,
            _identity.ProcessStartedAt.UtcDateTime.ToFileTimeUtc(),
            parsed.After,
            parsed.Action == "logs" ? parsed.Limit : null,
            parsed.TimeoutMs,
            MaxResponseUtf8Bytes);

        AipDebugCtlOutcome outcome;
        try
        {
            outcome = _ctl.Invoke(invocation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HostAdapterException(
                "plugin_debug_ipc_failed",
                $"The AIPDebug vector-IPC client failed: {ex.Message}",
                retryable: false,
                IsControlOperation(request.Operation)
                    ? ExecutionState.Ambiguous
                    : ExecutionState.NotStarted,
                innerException: ex);
        }

        switch (outcome.Completion)
        {
            case AipDebugCtlCompletion.Unavailable:
                InvalidateProvenance(parsed.Plugin);
                if (!IsControlOperation(request.Operation) &&
                    parsed.Transport == TransportAuto)
                {
                    return ExecuteCom(
                        request,
                        appObject,
                        parsed,
                        selector,
                        input,
                        TransportCom,
                        outcome.StandardError.Length == 0
                            ? "The optional AIPDebug VectorIPC helper is unavailable."
                            : outcome.StandardError);
                }

                throw new HostAdapterException(
                    "plugin_debug_ipc_unavailable",
                    outcome.StandardError.Length == 0
                        ? "The optional AIPDebug vector-IPC client is unavailable."
                        : outcome.StandardError,
                    retryable: false,
                    ExecutionState.NotStarted);

            case AipDebugCtlCompletion.Cancelled:
                throw new HostAdapterException(
                    "plugin_debug_ipc_cancelled",
                    "The AIPDebug vector-IPC request was cancelled before it completed.",
                    retryable: true,
                    IsControlOperation(request.Operation)
                        ? ExecutionState.Ambiguous
                        : ExecutionState.NotStarted);

            case AipDebugCtlCompletion.TimedOut:
                InvalidateProvenance(parsed.Plugin);
                throw new HostAdapterException(
                    "plugin_debug_ipc_timeout",
                    $"The AIPDebug vector-IPC request exceeded its {parsed.TimeoutMs} ms " +
                    "bound and the child was terminated.",
                    retryable: !IsControlOperation(request.Operation),
                    IsControlOperation(request.Operation)
                        ? ExecutionState.Ambiguous
                        : ExecutionState.NotStarted);

            case AipDebugCtlCompletion.OutputLimitExceeded:
                InvalidateProvenance(parsed.Plugin);
                throw new HostAdapterException(
                    "plugin_debug_ipc_output_exceeded",
                    $"The AIPDebug vector-IPC response exceeded the {MaxResponseUtf8Bytes} " +
                    "byte inline bound.",
                    retryable: false,
                    IsControlOperation(request.Operation)
                        ? ExecutionState.Ambiguous
                        : ExecutionState.NotStarted);

            case AipDebugCtlCompletion.LaunchFailed:
                InvalidateProvenance(parsed.Plugin);
                throw new HostAdapterException(
                    "plugin_debug_ipc_unavailable",
                    outcome.StandardError.Length == 0
                        ? "The AIPDebug vector-IPC client is unavailable."
                        : outcome.StandardError,
                    retryable: false,
                    ExecutionState.NotStarted);

            case AipDebugCtlCompletion.ProcessFailure:
                InvalidateProvenance(parsed.Plugin);
                throw new HostAdapterException(
                    "plugin_debug_ipc_process_failed",
                    outcome.StandardError.Length == 0
                        ? "The AIPDebug vector-IPC helper failed after it started."
                        : outcome.StandardError,
                    retryable: false,
                    IsControlOperation(request.Operation)
                        ? ExecutionState.Ambiguous
                        : ExecutionState.NotStarted);
        }

        var response = outcome.StandardOutput.Trim();
        var isApplicationError = outcome.ExitCode == AipDebugCtlClient.ExitApplicationError;
        if (outcome.ExitCode is not (
            AipDebugCtlClient.ExitSuccess or
            AipDebugCtlClient.ExitApplicationError))
        {
            InvalidateProvenance(parsed.Plugin);
            throw new HostAdapterException(
                "plugin_debug_ipc_transport_failed",
                BuildTransportDetail(outcome),
                retryable: false,
                IsControlOperation(request.Operation)
                    ? ExecutionState.Ambiguous
                    : ExecutionState.NotStarted);
        }

        if (!TryParseResponse(response, out var payload, out var responseJson))
        {
            InvalidateProvenance(parsed.Plugin);
            return FailureAfterDispatch(
                request,
                "plugin_debug_invalid_response",
                "The AIPDebug vector-IPC client did not return a JSON response.",
                ["inspect_plugin_debug_protocol"]);
        }

        var validation = ValidatePayload(
            parsed,
            payload,
            out var failureKind,
            out var failureMessage);
        if (!validation)
        {
            InvalidateProvenance(parsed.Plugin);
            return FailureAfterDispatch(
                request,
                failureKind,
                failureMessage,
                ["run_plugin_debug_discover", "inspect_target_generation"],
                evidence: BuildProvenanceEvidence(
                    parsed,
                    provenance,
                    outcome.StandardOutput,
                    "vectoripc"));
        }

        return Success(
            request,
            BuildResult(
                parsed,
                selector,
                input,
                payload,
                transport: "vectoripc",
                provenance: provenance,
                fallback: null,
                applicationError: isApplicationError,
                responseBytes: Encoding.UTF8.GetByteCount(response),
                responseSha256: Sha256(response)),
            isApplicationError);
    }

    private OperationResult ExecuteCom(
        OperationRequest request,
        object appObject,
        PluginDebugRequest parsed,
        string selector,
        string input,
        string transport,
        string? fallback)
    {
        object? raw;
        try
        {
            raw = _comInvoker(
                appObject,
                "SendScriptMessage",
                [parsed.Plugin, selector, input]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HostAdapterException(
                "plugin_debug_host_thread_failed",
                $"The AIPDebug host-thread selector '{selector}' failed: {ex.Message}",
                retryable: false,
                IsControlOperation(request.Operation)
                    ? ExecutionState.Ambiguous
                    : ExecutionState.NotStarted,
                innerException: ex);
        }

        var response = raw as string
            ?? Convert.ToString(raw, CultureInfo.InvariantCulture)
            ?? string.Empty;
        var responseBytes = Encoding.UTF8.GetByteCount(response);
        var responseSha256 = Sha256(response);
        if (responseBytes > MaxResponseUtf8Bytes)
        {
            var evidence = JsonSerializer.SerializeToElement(new
            {
                plugin = parsed.Plugin,
                selector,
                responseUtf8Bytes = responseBytes,
                responseSha256
            });

            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = OperationStatus.Failed,
                TargetState = TargetState.Known,
                Error = new ProtocolError
                {
                    Kind = "plugin_debug_response_too_large",
                    Message =
                        $"The plug-in response was {responseBytes} UTF-8 bytes; the " +
                        $"safe inline limit is {MaxResponseUtf8Bytes}.",
                    Retryable = false,
                    Execution = ExecutionState.Completed,
                    SuggestedActions = ["reduce_log_limit", "read_logs_pages"]
                },
                Evidence = [new EvidenceItem("plugin.debug.provenance", evidence)]
            };
        }

        if (!TryParseResponse(response, out var payload, out var responseJson))
        {
            return FailureAfterDispatch(
                request,
                "plugin_debug_invalid_response",
                "The plug-in did not return a JSON AIPDebug response.",
                ["inspect_plugin_debug_protocol"],
                evidence: JsonSerializer.SerializeToElement(new
                {
                    plugin = parsed.Plugin,
                    selector,
                    raw = Truncate(response, 512)
                }));
        }

        var validation = ValidatePayload(
            parsed,
            payload,
            out var failureKind,
            out var failureMessage);
        if (!validation)
        {
            return FailureAfterDispatch(
                request,
                failureKind,
                failureMessage,
                ["run_plugin_debug_discover", "inspect_target_generation"],
                evidence: JsonSerializer.SerializeToElement(new
                {
                    plugin = parsed.Plugin,
                    selector,
                    targetId = _identity.TargetId,
                    expectedProcessId = _identity.ProcessId
                }));
        }

        var applicationError = IsApplicationError(payload);
        var discovered = payload.ValueKind == JsonValueKind.Object
            ? TryReadProvenance(parsed.Plugin, payload)
            : null;
        if (!applicationError && discovered is not null)
            RecordProvenance(discovered);

        return Success(
            request,
            BuildResult(
                parsed,
                selector,
                input,
                payload,
                transport,
                discovered ?? PeekProvenance(parsed.Plugin),
                fallback,
                applicationError,
                responseBytes,
                responseSha256),
            applicationError);
    }

    /// <summary>
    /// Validates one AIPDebug payload against runtime-owned identity. Every
    /// check fails closed: a plug-in that reports another process id, another
    /// plug-in name, or a foreign protocol is not accepted as this target's
    /// debug surface.
    /// </summary>
    private bool ValidatePayload(
        PluginDebugRequest request,
        JsonElement payload,
        out string failureKind,
        out string failureMessage)
    {
        failureKind = string.Empty;
        failureMessage = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            failureKind = "plugin_debug_invalid_response";
            failureMessage = "The AIPDebug response must be a JSON object.";
            return false;
        }

        {
            if (payload.TryGetProperty("protocol", out var protocol) &&
                protocol.ValueKind == JsonValueKind.String &&
                !string.Equals(
                    protocol.GetString(),
                    Protocol,
                    StringComparison.Ordinal))
            {
                failureKind = "plugin_debug_protocol_mismatch";
                failureMessage =
                    $"Expected protocol '{Protocol}' but the plug-in answered " +
                    $"'{Truncate(protocol.GetString() ?? string.Empty, 64)}'.";
                return false;
            }

            if (payload.TryGetProperty("plugin", out var plugin) &&
                plugin.ValueKind == JsonValueKind.String &&
                !string.Equals(
                    plugin.GetString(),
                    request.Plugin,
                    StringComparison.Ordinal))
            {
                failureKind = "plugin_debug_plugin_mismatch";
                failureMessage =
                    "The responding plug-in name does not match the requested plug-in.";
                return false;
            }

            if (payload.TryGetProperty("pid", out var pid) &&
                pid.ValueKind == JsonValueKind.Number)
            {
                if (!pid.TryGetInt32(out var reported) ||
                    reported != _identity.ProcessId)
                {
                    failureKind = "plugin_debug_pid_mismatch";
                    failureMessage =
                        "The AIPDebug server reported process " +
                        $"{Truncate(pid.ToString(), 32)} but this target generation is " +
                        $"process {_identity.ProcessId}.";
                    return false;
                }
            }

            if (payload.TryGetProperty("endpoint", out var endpoint) &&
                endpoint.ValueKind == JsonValueKind.String)
            {
                var value = endpoint.GetString() ?? string.Empty;
                if (!IsValidEndpoint(value))
                {
                    failureKind = "plugin_debug_endpoint_invalid";
                    failureMessage =
                        "The AIPDebug endpoint is not a conservative bounded token.";
                    return false;
                }

                if (!TryReadEndpointProcessId(value, out var endpointProcessId) ||
                    endpointProcessId != _identity.ProcessId)
                {
                    failureKind = "plugin_debug_endpoint_stale";
                    failureMessage =
                        "The AIPDebug endpoint is not bound to this Illustrator " +
                        "process generation.";
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryGetVerifiedProvenance(
        PluginDebugRequest request,
        out AipDebugEndpointProvenance provenance,
        out PluginDebugRefusal? refusal)
    {
        var candidate = PeekProvenance(request.Plugin);
        if (candidate is null)
        {
            provenance = null!;
            refusal = new PluginDebugRefusal(
                "plugin_debug_endpoint_unknown",
                "No runtime-discovered AIPDebug endpoint exists for plug-in '" +
                $"{request.Plugin}' in this worker. Run plugin.debug.diagnostics " +
                "with action 'discover' while the Illustrator host thread is responsive.");
            return false;
        }

        if (!string.Equals(
                candidate.TargetId,
                _identity.TargetId,
                StringComparison.Ordinal))
        {
            InvalidateProvenance(request.Plugin);
            provenance = null!;
            refusal = new PluginDebugRefusal(
                "plugin_debug_endpoint_stale",
                "The recorded AIPDebug endpoint belongs to a different target generation.");
            return false;
        }

        if (candidate.ServerProcessId != _identity.ProcessId)
        {
            InvalidateProvenance(request.Plugin);
            provenance = null!;
            refusal = new PluginDebugRefusal(
                "plugin_debug_pid_stale",
                "The recorded AIPDebug endpoint belongs to process " +
                $"{candidate.ServerProcessId}; this worker is bound to process " +
                $"{_identity.ProcessId}.");
            return false;
        }

        if (!IsValidEndpoint(candidate.Endpoint) ||
            !TryReadEndpointProcessId(candidate.Endpoint, out var embedded) ||
            embedded != _identity.ProcessId)
        {
            InvalidateProvenance(request.Plugin);
            provenance = null!;
            refusal = new PluginDebugRefusal(
                "plugin_debug_endpoint_stale",
                "The recorded AIPDebug endpoint is not bound to this Illustrator " +
                "process generation.");
            return false;
        }

        if (request.Endpoint is not null &&
            !string.Equals(
                request.Endpoint,
                candidate.Endpoint,
                StringComparison.Ordinal))
        {
            provenance = null!;
            refusal = new PluginDebugRefusal(
                "plugin_debug_endpoint_mismatch",
                "The requested AIPDebug endpoint does not match the endpoint this " +
                "runtime discovered for the current target generation.");
            return false;
        }

        provenance = candidate;
        refusal = null;
        return true;
    }

    private AipDebugEndpointProvenance? TryReadProvenance(
        string plugin,
        JsonElement payload)
    {
        if (!payload.TryGetProperty("endpoint", out var endpoint) ||
            endpoint.ValueKind != JsonValueKind.String)
            return null;

        var value = endpoint.GetString() ?? string.Empty;
        if (!IsValidEndpoint(value) ||
            !TryReadEndpointProcessId(value, out var serverProcessId) ||
            serverProcessId != _identity.ProcessId)
            return null;

        return new AipDebugEndpointProvenance(
            plugin,
            value,
            serverProcessId,
            _identity.TargetId,
            ReadOptionalString(payload, "version"),
            ReadOptionalString(payload, "buildId"),
            _utcNow());
    }

    private void RecordProvenance(AipDebugEndpointProvenance provenance)
    {
        lock (_gate)
            _provenance[provenance.Plugin] = provenance;
    }

    private AipDebugEndpointProvenance? PeekProvenance(string plugin)
    {
        lock (_gate)
            return _provenance.TryGetValue(plugin, out var found)
                ? found
                : null;
    }

    private void InvalidateProvenance(string plugin)
    {
        lock (_gate)
            _provenance.Remove(plugin);
    }

    private JsonElement BuildResult(
        PluginDebugRequest request,
        string selector,
        string input,
        JsonElement payload,
        string transport,
        AipDebugEndpointProvenance? provenance,
        string? fallback,
        bool applicationError,
        int responseBytes,
        string responseSha256) =>
        JsonSerializer.SerializeToElement(new
        {
            plugin = request.Plugin,
            action = request.Action,
            selector,
            input,
            transport,
            requestedTransport = request.Transport,
            response = payload.Clone(),
            responseJson = true,
            endpoint = provenance?.Endpoint,
            serverProcessId = provenance?.ServerProcessId,
            serverProcessStartedAt =
                provenance is null
                    ? (DateTimeOffset?)null
                    : (DateTimeOffset?)_identity.ProcessStartedAt,
            endpointProvenance = provenance is null
                ? "absent"
                : "runtime_discovered",
            targetId = _identity.TargetId,
            transportFallback = fallback,
            applicationError,
            responseUtf8Bytes = responseBytes,
            responseSha256
        });

    private static bool TryParseResponse(
        string response,
        out JsonElement payload,
        out bool responseJson)
    {
        payload = default;
        responseJson = false;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            payload = document.RootElement.Clone();
            responseJson = true;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsApplicationError(JsonElement? payload) =>
        payload is { } element &&
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("ok", out var ok) &&
        ok.ValueKind == JsonValueKind.False;

    /// <summary>
    /// Conservative VectorIPC endpoint grammar: a bounded ASCII token. The
    /// value is embedded in a named-pipe name, so anything outside
    /// <c>[A-Za-z0-9._-]</c> is rejected rather than escaped.
    /// </summary>
    internal static bool IsValidEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > EndpointMaxLength)
            return false;

        foreach (var c in endpoint)
        {
            var valid = c is >= 'a' and <= 'z' ||
                        c is >= 'A' and <= 'Z' ||
                        c is >= '0' and <= '9' ||
                        c == '.' || c == '_' || c == '-';
            if (!valid)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the Illustrator process id that AIPDebug embeds in its generated
    /// endpoint (<c>aipdbg-&lt;token&gt;-&lt;hash&gt;-&lt;pid&gt;</c>). The
    /// endpoint therefore carries its own server generation, which is checked
    /// before any transport work and again by the native client's peer-PID
    /// check.
    /// </summary>
    internal static bool TryReadEndpointProcessId(
        string? endpoint,
        out int processId)
    {
        processId = 0;
        if (!IsValidEndpoint(endpoint) || endpoint!.Length <= 1)
            return false;

        var separator = endpoint.LastIndexOf('-');
        if (separator <= 0 || separator == endpoint.Length - 1)
            return false;

        var tail = endpoint.AsSpan(separator + 1);
        if (tail.Length is 0 or > 10)
            return false;

        foreach (var c in tail)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return int.TryParse(
            tail,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out processId) && processId > 0;
    }

    internal static bool IsControlOperation(string operation) =>
        string.Equals(
            operation,
            ControlOperation,
            StringComparison.Ordinal);

    private static string FormatLogQuery(long after, int limit) =>
        "after=" + after.ToString(CultureInfo.InvariantCulture) +
        "&limit=" + limit.ToString(CultureInfo.InvariantCulture);

    private static string BuildTransportDetail(AipDebugCtlOutcome outcome)
    {
        var detail = outcome.StandardError.Trim();
        if (detail.Length == 0)
            detail = outcome.StandardOutput.Trim();
        if (detail.Length == 0)
            detail = "exit " + outcome.ExitCode.ToString(CultureInfo.InvariantCulture);
        return "The AIPDebug vector-IPC transport failed: " +
            Truncate(detail, 400);
    }

    private JsonElement BuildProvenanceEvidence(
        PluginDebugRequest request,
        AipDebugEndpointProvenance provenance,
        string response,
        string transport) =>
        JsonSerializer.SerializeToElement(new
        {
            plugin = request.Plugin,
            action = request.Action,
            transport,
            endpoint = provenance.Endpoint,
            serverProcessId = provenance.ServerProcessId,
            expectedProcessId = _identity.ProcessId,
            expectedProcessStartedAt = _identity.ProcessStartedAt,
            targetId = _identity.TargetId,
            raw = Truncate(response, 512)
        });

    private static string? ReadOptionalString(
        JsonElement payload,
        string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? Truncate(value.GetString() ?? string.Empty, 128)
            : null;

    private static string RawOf(JsonElement? payload) =>
        payload is { } element && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (max <= 0)
            return value;
        return value.Length <= max ? value : value[..max];
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static OperationResult Success(
        OperationRequest request,
        JsonElement result,
        bool applicationError) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = !applicationError,
            Status = applicationError
                ? OperationStatus.Failed
                : OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(result),
            Error = applicationError
                ? new ProtocolError
                {
                    Kind = "plugin_debug_application_error",
                    Message =
                        "The AIPDebug server returned an application-level failure " +
                        "(for example native-debugger-not-attached). The request " +
                        "reached the server and the server refused it.",
                    Retryable = false,
                    Execution = ExecutionState.Completed,
                    SuggestedActions = ["inspect_plugin_debug_response"]
                }
                : null
        };

    private static OperationResult InvalidRequest(
        OperationRequest request,
        string kind,
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
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = ["inspect_operation_input"]
            }
        };

    private static OperationResult FailureNotStarted(
        OperationRequest request,
        string kind,
        string message,
        IReadOnlyList<string> suggestedActions,
        JsonElement? evidence = null) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.Failed,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = suggestedActions
            },
            Evidence = evidence is { } evidenceValue
                ? [new EvidenceItem("plugin.debug.provenance", evidenceValue)]
                : null
        };

    private static OperationResult FailureAfterDispatch(
        OperationRequest request,
        string kind,
        string message,
        IReadOnlyList<string> suggestedActions,
        JsonElement? evidence = null)
    {
        if (!IsControlOperation(request.Operation))
        {
            return FailureNotStarted(
                request,
                kind,
                message,
                suggestedActions,
                evidence);
        }

        throw new HostAdapterException(
            kind,
            message,
            retryable: false,
            ExecutionState.Ambiguous,
            evidence: evidence is { } evidenceValue
                ? [new EvidenceItem(
                    "plugin.debug.provenance",
                    evidenceValue)]
                : null);
    }
}

internal sealed record PluginDebugRefusal(string Kind, string Message);

/// <summary>
/// Runtime-owned endpoint provenance. It is bound to one Illustrator
/// generation, is never read from disk, and is never supplied by a caller.
/// </summary>
internal sealed record AipDebugEndpointProvenance(
    string Plugin,
    string Endpoint,
    int ServerProcessId,
    string TargetId,
    string? PluginVersion,
    string? BuildId,
    DateTimeOffset DiscoveredAtUtc);

/// <summary>
/// One validated plugin-debug request. The allowed action set is a function of
/// the operation, so a request can never escape the effect class the runtime
/// catalog assigned to that operation.
/// </summary>
internal sealed record PluginDebugRequest(
    string Plugin,
    string Action,
    string Transport,
    long After,
    int Limit,
    int TimeoutMs,
    string? Endpoint)
{
    public static PluginDebugRequest Parse(OperationRequest request)
    {
        if (request.Input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                "plugin debug input must be a JSON object.");

        var control = IllustratorPluginDebugManager.IsControlOperation(
            request.Operation);
        var allowed = control
            ? ControlActions
            : DiagnosticActions;

        string? plugin = null;
        string? action = null;
        string? transport = null;
        string? endpoint = null;
        long after = 0;
        int limit = IllustratorPluginDebugManager.DefaultLogLimit;
        var timeoutMs = AipDebugCtlClient.DefaultTimeoutMs;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in request.Input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new ArgumentException(
                    $"Duplicate plugin debug field '{property.Name}'.");

            switch (property.Name)
            {
                case "plugin":
                    plugin = ReadBoundedString(property.Value, "plugin");
                    break;
                case "action":
                    action = ReadBoundedString(property.Value, "action");
                    if (!allowed.Contains(action))
                        throw new ArgumentException(
                            control
                                ? $"Action '{action}' is not a plugin.debug.control action; " +
                                  "expected one of 'clear', 'break'."
                                : $"Action '{action}' is not a plugin.debug.diagnostics " +
                                  "action; expected one of 'discover', 'info', 'logs', " +
                                  "'snapshot', 'stats'.");
                    break;
                case "transport":
                    transport = ReadBoundedString(property.Value, "transport");
                    if (!Transports.Contains(transport))
                        throw new ArgumentException(
                            "transport must be one of 'auto', 'com', 'ipc'.");
                    break;
                case "endpoint":
                    endpoint = ReadBoundedString(property.Value, "endpoint");
                    if (!IsValidEndpointShape(endpoint))
                        throw new ArgumentException(
                            "endpoint must be a bounded conservative ASCII token of at " +
                            $"most {IllustratorPluginDebugManager.EndpointMaxLength} " +
                            "characters [A-Za-z0-9._-].");
                    break;
                case "after":
                    if (!control && action == "logs")
                        after = ReadNonNegativeInt64(property.Value, "after");
                    else
                        throw new ArgumentException(
                            "'after' is only valid for the 'logs' action.");
                    break;
                case "limit":
                    if (!control && action == "logs")
                        limit = ReadBoundedInt32(
                            property.Value,
                            "limit",
                            IllustratorPluginDebugManager.MinLogLimit,
                            IllustratorPluginDebugManager.MaxLogLimit);
                    else
                        throw new ArgumentException(
                            "'limit' is only valid for the 'logs' action.");
                    break;
                case "timeoutMs":
                    timeoutMs = ReadBoundedInt32(
                        property.Value,
                        "timeoutMs",
                        AipDebugCtlClient.MinTimeoutMs,
                        AipDebugCtlClient.MaxTimeoutMs);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown plugin debug field '{property.Name}'.");
            }
        }

        if (plugin is null)
            throw new ArgumentException("'plugin' is required.");
        if (action is null)
            throw new ArgumentException("'action' is required.");

        return new PluginDebugRequest(
            plugin,
            action,
            transport ?? IllustratorPluginDebugManager.TransportAuto,
            after,
            limit,
            timeoutMs,
            endpoint);
    }

    private static bool IsValidEndpointShape(string endpoint) =>
        IllustratorPluginDebugManager.IsValidEndpoint(endpoint) &&
        IllustratorPluginDebugManager.TryReadEndpointProcessId(
            endpoint,
            out _);

    private static string ReadBoundedString(
        JsonElement value,
        string field)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{field}' must be a string.");

        var text = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException($"'{field}' must be non-empty.");

        if (Encoding.UTF8.GetByteCount(text) >
            IllustratorPluginDebugManager.MaxPluginUtf8Bytes)
            throw new ArgumentException(
                $"'{field}' exceeds the " +
                $"{IllustratorPluginDebugManager.MaxPluginUtf8Bytes} UTF-8 byte limit.");

        return text;
    }

    private static long ReadNonNegativeInt64(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) ||
            parsed < 0)
            throw new ArgumentException(
                $"'{field}' must be an integer >= 0.");
        return parsed;
    }

    private static int ReadBoundedInt32(
        JsonElement value,
        string field,
        int min,
        int max)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed) ||
            parsed < min ||
            parsed > max)
            throw new ArgumentException(
                $"'{field}' must be an integer between {min} and {max}.");
        return parsed;
    }

    private static readonly HashSet<string> ControlActions =
        new(StringComparer.Ordinal) { "clear", "break" };

    private static readonly HashSet<string> DiagnosticActions =
        new(StringComparer.Ordinal)
        {
            "discover",
            "info",
            "logs",
            "snapshot",
            "stats"
        };

    private static readonly HashSet<string> Transports =
        new(StringComparer.Ordinal) { "auto", "com", "ipc" };
}
