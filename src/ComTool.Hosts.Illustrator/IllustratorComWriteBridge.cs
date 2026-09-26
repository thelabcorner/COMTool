using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Runtime-owned, narrowly allowlisted COM property mutation surface.
///
/// This is deliberately NOT a generic COM setter. Callers may only name a
/// property key from <see cref="Targets"/>; the caller never authors a COM
/// member name, a dotted path, an index, or a raw dispatch call. Every key is
/// bound to a fixed runtime-owned semantic: one strict dotted path, one exact
/// CLR value type, one declared object selector, and one mutation class.
///
/// The bridge performs no COM dispatch itself. It only:
///
/// * validates the caller's JSON input against the runtime catalog,
/// * authors the ExtendScript setter body for the active artboard,
/// * dispatches through the canonical document-mutation wrapper, and
/// * parses the transport envelope into a fixed outcome.
///
/// The setter body is runtime-authored and contains exactly one fixed member
/// write. The canonical ESON runtime is assumed already installed on
/// <c>$.global</c>; the caller (the session shim) is responsible for the
/// read-only presence check and the ESON bootstrap, which are host-lifecycle
/// concerns owned elsewhere. This keeps the value itself carried as JSON data
/// and parsed by ESON rather than concatenated into script text.
///
/// The runtime supervisor owns leases, write-ahead journaling, idempotency, and
/// ambiguity recovery; this bridge never bypasses that path. The mutating
/// dispatch is a single host call whose success is never re-checked by the
/// bridge, so an ambiguous transport failure cannot be mistaken for a
/// completed mutation.
/// </summary>
internal static class IllustratorComWriteBridge
{
    internal const string ActiveArtboardNameProperty =
        "document.artboard.active.name";

    /// <summary>
    /// The canonical ESON ready token, duplicated here so the write bridge is
    /// self-contained and does not depend on the script-runtime file's
    /// accessibility surface.
    /// </summary>
    internal const string EsonReadyToken = "comtool-eson-ready";

    /// <summary>
    /// One runtime-owned property-put target. The setter body is fixed; only
    /// the value data varies, and it is emitted through ESON.
    /// </summary>
    internal sealed record WriteTarget(
        string Key,
        string DocumentSelectorProperty,
        string Member,
        string ValueType,
        string ValueProperty,
        string ValueMember);

    /// <summary>
    /// The runtime-owned allowlist. Adding a target is a runtime code change,
    /// never a caller capability.
    /// </summary>
    internal static IReadOnlyDictionary<string, WriteTarget> Targets { get; } =
        new Dictionary<string, WriteTarget>(StringComparer.Ordinal)
        {
            [ActiveArtboardNameProperty] = new WriteTarget(
                Key: ActiveArtboardNameProperty,
                DocumentSelectorProperty: "active",
                Member: "Name",
                ValueType: "string",
                ValueProperty: "value",
                ValueMember: "name")
        };

    internal static bool TryGetTarget(
        string key,
        out WriteTarget target)
    {
        if (key is null)
        {
            target = null!;
            return false;
        }

        return Targets.TryGetValue(key, out target!);
    }

    /// <summary>
    /// Builds the ES3 body for a fixed setter. All literal value data is
    /// emitted through <c>ESON.stringify</c>/<c>ESON.parse</c> so the exact
    /// JSON value reaches Illustrator as data, never as script text.
    /// </summary>
    internal static string BuildSetScript(
        WriteTarget target,
        string valueJson)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(valueJson);

        return
            """
            var __ct_d=app.activeDocument;
            if(!__ct_d){return {kind:'no_active_document'};}
            var __ct_ab=__ct_d.artboards.getActiveArtboard();
            if(!__ct_ab){return {kind:'no_active_artboard'};}
            var __ct_v=ESON.parse(__CT_VALUE__);
            __ct_ab.__CT_MEMBER__=__ct_v;
            return {kind:'set',name:String(__ct_ab.name)};
            """
            .Replace("__CT_VALUE__", valueJson, StringComparison.Ordinal)
            .Replace("__CT_MEMBER__", target.Member, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strict, runtime-owned input parsing. Returns a fixed outcome rather than
    /// throwing so the session shim can map it to a protocol error without
    /// owning the catalog.
    /// </summary>
    internal static ComWriteParseOutcome ParseInput(
        JsonElement input,
        WriteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (input.ValueKind != JsonValueKind.Object)
        {
            return ComWriteParseOutcome.Invalid(
                "invalid_input",
                "Operation input must be a JSON object.");
        }

        foreach (var property in input.EnumerateObject())
        {
            if (property.NameEquals("property") ||
                property.NameEquals("value"))
                continue;

            return ComWriteParseOutcome.Invalid(
                "unknown_input_field",
                $"Unknown input field '{property.Name}'.");
        }

        if (!input.TryGetProperty("property", out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(
                propertyElement.GetString()))
        {
            return ComWriteParseOutcome.Invalid(
                "invalid_com_property",
                "'property' must be a non-empty string.");
        }

        if (!string.Equals(
                propertyElement.GetString(),
                target.Key,
                StringComparison.Ordinal))
        {
            return ComWriteParseOutcome.Invalid(
                "unsupported_com_property",
                $"'property' must be '{target.Key}'.");
        }

        if (!input.TryGetProperty("value", out var valueElement))
        {
            return ComWriteParseOutcome.Invalid(
                "invalid_com_value",
                "'value' is required.");
        }

        if (valueElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(
                valueElement.GetString()))
        {
            return ComWriteParseOutcome.Invalid(
                "invalid_com_value",
                "'value' must be a non-empty string.");
        }

        return ComWriteParseOutcome.Valid(
            valueElement.GetRawText());
    }

    /// <summary>
    /// Executes a fixed setter through the canonical document-mutation wrapper
    /// and returns the parsed transport outcome. A post-dispatch transport
    /// failure is surfaced as an ambiguous host fault; a pre-dispatch failure
    /// is surfaced as a non-ambiguous fault.
    /// </summary>
    internal static WriteDispatchResult Dispatch(
        object appObject,
        string body,
        Func<object, string, int, string> executeScript)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(executeScript);

        string raw;
        try
        {
            raw = executeScript(
                appObject,
                IllustratorScriptEval.BuildDocumentMutationWrapper(body),
                IllustratorScriptEval.NeverShowDebugger);
        }
        catch (HostAdapterException ex)
        {
            return new WriteDispatchResult(
                Outcome: null,
                TransportFault: ex.Execution == ExecutionState.Ambiguous
                    ? ex
                    : null,
                NonAmbiguousFault: ex.Execution != ExecutionState.Ambiguous
                    ? ex
                    : null);
        }

        return new WriteDispatchResult(
            IllustratorComWriteEnvelope.Parse(raw),
            TransportFault: null,
            NonAmbiguousFault: null);
    }
}

/// <summary>
/// Validated input for a fixed property put.
/// </summary>
internal sealed record ComWriteParseOutcome(
    bool Ok,
    string? ValueJson,
    string? ErrorKind,
    string? ErrorMessage)
{
    public static ComWriteParseOutcome Valid(string valueJson) =>
        new(true, valueJson, null, null);

    public static ComWriteParseOutcome Invalid(
        string kind,
        string message) =>
        new(false, null, kind, message);
}

/// <summary>
/// Parsed transport envelope for a fixed property put.
/// </summary>
internal sealed record ComWriteOutcome(
    string Kind,
    string? Name,
    string? Message);

internal sealed record WriteDispatchResult(
    ComWriteOutcome? Outcome,
    HostAdapterException? TransportFault,
    HostAdapterException? NonAmbiguousFault);

internal static class IllustratorComWriteEnvelope
{
    internal const string MalformedMessage =
        "The COM write operation returned a malformed transport envelope.";

    internal static ComWriteOutcome Parse(string raw)
    {
        // The document-mutation wrapper reuses the `document` slot for the
        // fixed setter's outcome string. A missing document slot means the
        // body returned no structured outcome, which cannot prove a mutation.
        var outcome = IllustratorDocumentOperations.ParseMutationEnvelope(
            raw,
            allowMissingDocument: false);

        if (outcome.Kind == "failed")
        {
            return new ComWriteOutcome(
                "failed",
                Name: null,
                Message: outcome.Message ?? MalformedMessage);
        }

        return new ComWriteOutcome(
            outcome.Kind == "completed"
                ? "set"
                : outcome.Kind,
            Name: outcome.Document,
            Message: null);
    }
}
