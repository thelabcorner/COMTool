using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Explicit, generic Illustrator menu-command execution over
/// <c>Application.ExecuteMenuCommand(MenuCommandString)</c>.
///
/// This is the first-class replacement for the legacy tool's *fallback*
/// behavior, and it deliberately does not reproduce it:
/// <list type="bullet">
/// <item>No route selection. The caller names one exact command string and
/// this adapter dispatches exactly that string to exactly that COM method.
/// There is no scoring, no "try JSX, then action, then menu" cascade, and no
/// silent retarget to a different route when the dispatch fails.</item>
/// <item>No caller-declared effects. The runtime registers this operation as
/// a fixed <see cref="MutationClass.ExternalSideEffect"/>, so a caller cannot
/// weaken the ledger, lease, and no-replay treatment by asserting
/// <c>read_only</c> the way <c>script.eval</c> allows.</item>
/// <item>No replay after a possible dispatch. <see cref="Execute"/> never
/// re-invokes the host. A call that may have been accepted by Illustrator is
/// reported ambiguous so the runtime opens a durable incident and the caller
/// reconciles explicitly.</item>
/// <item>No effect claims. <c>ExecuteMenuCommand</c> returns void, so a
/// completed dispatch proves only that the host accepted the call. A
/// successful result therefore reports <c>effectVerified: false</c> and
/// points at operation postconditions; proving what the command changed is
/// the caller's explicit job, not this adapter's guess.</item>
/// </list>
///
/// Command strings are version-sensitive and mostly undocumented (see
/// <c>references/actions-events-and-plugins.md</c> in the parent skill), so
/// the request is a closed single-field object and every rejected shape is a
/// pre-dispatch <c>not_started</c> invalid request. The exact validated
/// string is the only host input, and the only field echoed into diagnostics.
///
/// The bounded timeout for a blocking host call is the caller-supplied worker
/// watchdog owned by the supervisor, not a thread abandoned here: an
/// in-flight COM call cannot be cancelled, so abandoning it would create a
/// second, unobservable execution path. A watchdog timeout is not
/// cancellation proof, and the supervisor already reports a timed-out
/// non-read-only operation as reconciliation-required.
/// </summary>
internal static class IllustratorMenuCommand
{
    internal const string ExecuteOperation = "illustrator.menu.execute";

    internal const string CommandProperty = "command";

    /// <summary>
    /// Bounded command-string length. Real documented strings (for example
    /// <c>selectall</c>, <c>pasteFront</c>, <c>expandStyle</c>,
    /// <c>Live Pathfinder Divide</c>) are far shorter; the bound exists so a
    /// caller cannot smuggle a payload through the only accepted field.
    /// </summary>
    internal const int MaxCommandChars = 256;

    /// <summary>
    /// Rejected defense-in-depth punctuation. The command string is passed to
    /// the host as data and is never embedded in a script, a path, or markup
    /// here, so this is not claimed as an injection barrier. It exists so the
    /// single accepted field cannot change meaning in any future transport
    /// or log that does embed it.
    /// </summary>
    private static readonly char[] RejectedCharacters =
    [
        '"',
        '\\',
        '<',
        '>',
        '`',
        '$'
    ];

    private static readonly string[] ReconcileActions =
    [
        "inspect_mutation_ledger",
        "apply_operation_specific_postconditions",
        "resolve_incident_explicitly"
    ];

    public static OperationResult Execute(
        object appObject,
        OperationRequest request,
        Func<object, string, object?[], object?>? mutationInvoker = null,
        string? hostVersion = null)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        MenuCommandRequest parsed;
        try
        {
            parsed = ParseRequest(request.Input);
        }
        catch (ArgumentException ex)
        {
            // Every parse failure happens before any host call, so it is
            // provably not started and never becomes an incident.
            return InvalidRequest(request, ex.Message);
        }

        var invoke =
            mutationInvoker ??
            ((object target, string method, object?[] args) =>
                IllustratorComInterop.InvokeMutationMethod(
                    target,
                    method,
                    args));

        try
        {
            // Exactly one dispatch, exactly this method, exactly this string.
            invoke(appObject, "ExecuteMenuCommand", [parsed.Command]);
        }
        catch (HostAdapterException ex)
        {
            return DispatchFailure(request, parsed, ex, hostVersion);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            command = parsed.Command,
            dispatched = true,
            effectVerified = false,
            mutationClass = "external_side_effect",
            verifyWith = "operation_postconditions"
        });

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            // A completed menu command may have changed document, selection,
            // or view state. Reporting Known would understate that.
            TargetState = TargetState.KnownChanged,
            Result = ProtocolValue.From(payload)
        };
    }

    public static MenuCommandRequest ParseRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Operation input must be a JSON object with a single 'command' string.");
        }

        string? command = null;

        foreach (var property in input.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    CommandProperty,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unknown input field '{property.Name}'. " +
                    $"This operation accepts only '{CommandProperty}'; menu routing, " +
                    "fallbacks, and extra parameters are not configurable here.");
            }

            if (command is not null)
            {
                throw new ArgumentException(
                    $"Duplicate input field '{CommandProperty}'.");
            }

            command = ReadCommand(property.Value);
        }

        if (command is null)
        {
            throw new ArgumentException(
                $"'{CommandProperty}' is required and must be the exact " +
                "Illustrator menu command string to execute.");
        }

        return new MenuCommandRequest(command);
    }

    private static string ReadCommand(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                $"'{CommandProperty}' must be a string.");
        }

        var value = element.GetString() ?? string.Empty;

        if (value.Length == 0)
        {
            throw new ArgumentException(
                $"'{CommandProperty}' must not be empty.");
        }

        if (value.Length > MaxCommandChars)
        {
            throw new ArgumentException(
                $"'{CommandProperty}' exceeds the {MaxCommandChars} character limit.");
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            // Rejected, never trimmed: a padded string is a caller bug, and
            // silently coercing it could dispatch a different command than
            // the one that was reviewed.
            throw new ArgumentException(
                $"'{CommandProperty}' must not start or end with whitespace; " +
                "the exact string is dispatched, so it is rejected instead of trimmed.");
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    $"'{CommandProperty}' must not contain control characters.");
            }

            if (Array.IndexOf(RejectedCharacters, character) >= 0)
            {
                throw new ArgumentException(
                    $"'{CommandProperty}' must not contain the character " +
                    $"'{(int)character:X2}'.");
            }
        }

        return value;
    }

    private static OperationResult DispatchFailure(
        OperationRequest request,
        MenuCommandRequest parsed,
        HostAdapterException failure,
        string? hostVersion)
    {
        var ambiguous = failure.Execution == ExecutionState.Ambiguous;

        var status = ambiguous
            ? OperationStatus.ReconciliationRequired
            : failure.Retryable
                ? OperationStatus.HostBusy
                : OperationStatus.Failed;

        var targetState = ambiguous
            ? TargetState.ReconciliationRequired
            : failure.Retryable
                ? TargetState.Busy
                : TargetState.Known;

        var kind = ambiguous
            ? "menu_command_outcome_ambiguous"
            : failure.Retryable
                ? "host_busy"
                : failure.Kind;

        var message = ambiguous
            ? $"Illustrator may have accepted menu command '{parsed.Command}' " +
              "before the transport failed. The dispatch is not retried: " +
              "inspect the mutation ledger and reconcile the target explicitly."
            : failure.Message;

        var suggestedActions = ambiguous
            ? ReconcileActions
            : failure.Retryable
                // Provably rejected before execution: nothing ran, so a
                // bounded retry of the same string is safe.
                ? ["retry_within_budget"]
                : [
                    // An unavailable string is a host-version fact, not a bug
                    // in the request, and the only honest fix is a different
                    // explicit command or a different explicit route.
                    "verify_menu_command_string_for_host_version",
                    "use_a_typed_operation_instead_of_a_menu_command",
                    "inspect_host_state"
                ];

        var evidence = JsonSerializer.SerializeToElement(new
        {
            command = parsed.Command,
            hostVersion,
            dispatch = ambiguous ? "possibly_executed" : "not_executed",
            failureKind = failure.Kind,
            hresult = failure.HResultCode,
            hresultHex = failure.HResultCode is null
                ? null
                : $"0x{unchecked((uint)failure.HResultCode.Value):X8}"
        });

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = status,
            TargetState = targetState,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = failure.Retryable,
                Execution = failure.Execution,
                HResult = failure.HResultCode,
                HResultHex = failure.HResultCode is null
                    ? null
                    : $"0x{unchecked((uint)failure.HResultCode.Value):X8}",
                SuggestedActions = suggestedActions
            },
            Evidence =
            [
                new EvidenceItem("menu_command.dispatch", evidence)
            ]
        };
    }

    private static OperationResult InvalidRequest(
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
                Kind = "invalid_menu_command",
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = ["inspect_operation_input"]
            }
        };
}

internal sealed record MenuCommandRequest(string Command);
