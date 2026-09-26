using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Deterministic unit coverage for the document-lifecycle operation surface.
///
/// Every test drives <see cref="IllustratorDocumentOperations"/> with a
/// recording fake script executor, so the ES3 source the adapter dispatches
/// to Illustrator is asserted exactly. Behaviors that depend on Illustrator's
/// own answers (whether the document set actually changed) cannot be proven
/// here and are asserted as "requires live validation" in the report instead
/// of being faked.
/// </summary>
public sealed class IllustratorDocumentOperationsTests
{
    // =====================================================================
    // Close policy: explicit, mandatory, and never a default
    // =====================================================================

    [Fact]
    public async Task CloseWithoutPolicyIsRejectedAndNeverDispatches()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0}}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_close_policy", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);

        // The invariant that makes accidental discard impossible: nothing was
        // sent to the host at all.
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, app[0].CloseCallCount);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData(" ")]
    [InlineData("Discard")]
    [InlineData("DISCARD")]
    [InlineData("discard ")]
    [InlineData("'discard'")]
    [InlineData("discard\n")]
    public async Task CloseRejectsAnyNonExactPolicyTokenWithoutDispatching(
        string token)
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor();

        var closePolicyJson = token == "null"
            ? "null"
            : JsonSerializer.Serialize(token);

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            "{\"document\":{\"index\":0},\"closePolicy\":" +
            closePolicyJson + "}");

        Assert.False(result.Ok);
        Assert.Equal("invalid_close_policy", result.Error?.Kind);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, app[0].CloseCallCount);
    }

    [Fact]
    public async Task CloseWithExplicitDiscardPolicyDispatchesDoNotSaveChanges()
    {
        var app = DocumentFakeApplication.Create(
            "A.ai",
            saved: false);
        var executor = new RecordingScriptExecutor();
        var target = app[0];

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.True(result.Ok);

        // SaveOptions.DONOTSAVECHANGES is mandatory or Illustrator raises a
        // modal save prompt for a dirty document.
        Assert.Contains(
            "close(2)",
            executor.LastSource,
            StringComparison.Ordinal);

        Assert.Equal(1, target.CloseCallCount);
        Assert.False(target.Saved);

        var payload = Value(result);
        Assert.True(payload.GetProperty("discardedChanges").GetBoolean());
        Assert.Equal(
            "discard",
            payload.GetProperty("closePolicy").GetString());
        Assert.Equal(1, payload.GetProperty("documentCountBefore").GetInt32());
        Assert.Equal(0, payload.GetProperty("documentCountAfter").GetInt32());
    }

    [Fact]
    public async Task CloseWithSavePolicyDispatchesSaveChanges()
    {
        var app = DocumentFakeApplication.Create(
            "A.ai",
            saved: false);
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"save"}""");

        Assert.True(result.Ok);
        Assert.Contains(
            "close(1)",
            executor.LastSource,
            StringComparison.Ordinal);

        var payload = Value(result);
        Assert.False(payload.GetProperty("discardedChanges").GetBoolean());
        Assert.Equal("save", payload.GetProperty("closePolicy").GetString());
    }

    [Fact]
    public async Task RejectIfUnsavedPolicyRefusesDirtyDocumentBeforeDispatch()
    {
        var app = DocumentFakeApplication.Create(
            "A.ai",
            saved: false);
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"reject_if_unsaved"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("document_unsaved_changes", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);

        // The dirty guard is enforced in the adapter, so no close policy can
        // reach a discard implicitly.
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, app[0].CloseCallCount);
    }

    [Fact]
    public async Task RejectIfUnsavedPolicyClosesCleanDocumentWithDoNotSaveChanges()
    {
        var app = DocumentFakeApplication.Create(
            "A.ai",
            saved: true);
        var executor = new RecordingScriptExecutor();
        var target = app[0];

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"reject_if_unsaved"}""");

        Assert.True(result.Ok);
        Assert.Contains(
            "close(2)",
            executor.LastSource,
            StringComparison.Ordinal);
        Assert.Equal(1, target.CloseCallCount);
    }

    // =====================================================================
    // Document identity: operations target a specific document
    // =====================================================================

    [Fact]
    public async Task CloseRejectsActiveDocumentSelector()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"active":true},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal("document_not_found", result.Error?.Kind);
        Assert.Contains(
            "active",
            result.Error?.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task CloseRejectsAmbiguousDocumentName()
    {
        var app = DocumentFakeApplication.Create(
            "Dup.ai",
            "Dup.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"name":"Dup.ai"},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal("document_not_found", result.Error?.Kind);
        Assert.Contains(
            "ambiguous",
            result.Error?.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task CloseByUniqueNameDispatchesTheProvenHostIndex()
    {
        var app = DocumentFakeApplication.Create(
            "First.ai",
            "Target.ai",
            "Third.ai");
        var executor = new RecordingScriptExecutor();
        var first = app[0];
        var second = app[1];
        var third = app[2];

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"name":"Target.ai"},"closePolicy":"discard"}""");

        Assert.True(result.Ok);

        // Zero-based selector index 1 must become 1-based COM item 2.
        Assert.Contains(
            "documents.item(2)",
            executor.LastSource,
            StringComparison.Ordinal);

        Assert.Equal(0, first.CloseCallCount);
        Assert.Equal(1, second.CloseCallCount);
        Assert.Equal(0, third.CloseCallCount);
    }

    [Fact]
    public async Task CloseRejectsOutOfRangeIndex()
    {
        var app = DocumentFakeApplication.Create("Only.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":4},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal("document_not_found", result.Error?.Kind);
        Assert.Contains(
            "out of range",
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task CloseRejectsUnresolvableDocumentIdInsteadOfActingOnAnother()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"documentId":"a1b2c3d4-uuid","closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_document_selector", result.Error?.Kind);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task CloseAcceptsCanonicalDocumentIdSelector()
    {
        var app = DocumentFakeApplication.Create(
            "First.ai",
            "Second.ai");
        var executor = new RecordingScriptExecutor();
        var second = app[1];

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"documentId":"document:index:1","closePolicy":"discard"}""");

        Assert.True(result.Ok);
        Assert.Contains(
            "documents.item(2)",
            executor.LastSource,
            StringComparison.Ordinal);
        Assert.Equal(1, second.CloseCallCount);
    }

    [Fact]
    public async Task MissingSelectorIsRejected()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal("invalid_document_selector", result.Error?.Kind);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task UnknownInputFieldIsRejected()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard","activeDocument":true}""");

        Assert.False(result.Ok);
        Assert.Equal("unknown_input_field", result.Error?.Kind);
        Assert.Equal(0, executor.CallCount);
    }

    // =====================================================================
    // Postcondition verification
    // =====================================================================

    [Fact]
    public async Task CloseWithUnchangedDocumentCountIsAmbiguousAndNotRetryable()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor
        {
            // The host reports success but the document set is unchanged:
            // the mutation outcome cannot be proven.
            DeferDocumentRemoval = true
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.Contains(
            "resolve_incident_explicitly",
            result.Error?.SuggestedActions ?? []);
    }

    [Fact]
    public async Task ClosePostconditionIdentifiesSurvivingDocument()
    {
        var app = DocumentFakeApplication.Create(
            "First.ai",
            "Target.ai");
        var executor = new RecordingScriptExecutor
        {
            // Close "Target.ai" but remove "First.ai" instead.
            MisrouteCloseToName = "First.ai"
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"name":"Target.ai"},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            "document_close_identity_persists",
            result.Error?.Kind);
    }

    [Fact]
    public async Task UnreadableDocumentSetIsReportedAsAmbiguous()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor
        {
            DocumentSetReadableAfterDispatch = false
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            "document_close_result_unreadable",
            result.Error?.Kind);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
    }

    // =====================================================================
    // Failure classification: dispatch faults are never "completed"
    // =====================================================================

    [Fact]
    public async Task TransportFaultAfterDispatchIsAmbiguous()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor
        {
            DispatchFault = new HostAdapterException(
                "com_failure",
                "COM server failed after dispatch.",
                retryable: false,
                ExecutionState.Ambiguous)
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.Equal(
            "document_lifecycle_outcome_ambiguous",
            result.Error?.Kind);
        Assert.Equal(1, executor.CallCount);
        Assert.False(result.Error?.Retryable);
    }

    [Fact]
    public async Task ColdDispatchRejectionIsRetryableBusyWithoutMutation()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor
        {
            DispatchFault = new HostAdapterException(
                "host_busy",
                "Call was rejected before execution.",
                retryable: true,
                ExecutionState.NotStarted)
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.HostBusy, result.Status);
        Assert.Equal(TargetState.Busy, result.TargetState);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.True(result.Error?.Retryable);
        Assert.Equal(0, app[0].CloseCallCount);
    }

    [Fact]
    public async Task ScriptLevelFailureIsReportedAsNotStartedFailure()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor
        {
            ScriptFailureMessage = "The document is locked."
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal("document_close_failed", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.Contains(
            "locked",
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedTransportEnvelopeIsRejectedNotTreatedAsSuccess()
    {
        var app = DocumentFakeApplication.Create("A.ai");
        var executor = new RecordingScriptExecutor
        {
            RawResponseOverride = "not-json-at-all"
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CloseOperation,
            """{"document":{"index":0},"closePolicy":"discard"}""");

        Assert.False(result.Ok);
        Assert.Equal("document_close_failed", result.Error?.Kind);
        Assert.Contains(
            "malformed",
            result.Error?.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Ephemeral fake documents (create / open posture)
    // =====================================================================

    [Fact]
    public async Task CreateDispatchesAddAndReturnsTheNewDocumentIdentity()
    {
        var app = DocumentFakeApplication.Create("Existing.ai");
        var executor = new RecordingScriptExecutor
        {
            DocumentsAddedAfterDispatch = 1
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CreateOperation,
            """{"documentColorSpace":1}""");

        Assert.True(result.Ok);
        Assert.Contains(
            "app.documents.add(1)",
            executor.LastSource,
            StringComparison.Ordinal);

        var payload = Value(result);
        Assert.Equal(1, payload.GetProperty("index").GetInt32());
        Assert.False(payload.GetProperty("ambiguous").GetBoolean());
    }

    [Fact]
    public async Task CreateRejectsInvalidDocumentColorSpace()
    {
        var app = DocumentFakeApplication.Create("Existing.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.CreateOperation,
            """{"documentColorSpace":7}""");

        Assert.False(result.Ok);
        Assert.Equal("invalid_document_color_space", result.Error?.Kind);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task OpenRejectsRelativePathBeforeDispatch()
    {
        var app = DocumentFakeApplication.Create("Existing.ai");
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.OpenOperation,
            """{"path":"artwork/relative.ai"}""");

        Assert.False(result.Ok);
        Assert.Equal("invalid_path", result.Error?.Kind);
        Assert.Contains(
            "absolute",
            result.Error?.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task OpenRejectsAlreadyOpenPathBeforeDispatch()
    {
        var openPath = Path.Combine(
            Path.GetTempPath(),
            "comtool-open-test.ai");

        var app = DocumentFakeApplication.Create(
            "Existing.ai",
            saved: true,
            path: openPath);
        var executor = new RecordingScriptExecutor();

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.OpenOperation,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["path"] = openPath
                }));

        Assert.False(result.Ok);
        Assert.Equal("document_already_open", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task OpenDispatchesWithExplicitOpenOptionsAndNoRetryPolicy()
    {
        var targetPath = Path.Combine(
            Path.GetTempPath(),
            "comtool-open-new.ai");

        var app = DocumentFakeApplication.Create("Existing.ai");
        var executor = new RecordingScriptExecutor
        {
            DocumentsAddedAfterDispatch = 1,
            AddedDocumentPath = targetPath
        };

        var result = await ExecuteAsync(
            app,
            executor,
            IllustratorDocumentOperations.OpenOperation,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["path"] = targetPath,
                    ["requestId"] = "open-run-1"
                }));

        Assert.True(result.Ok);

        // OpenOptions are supplied explicitly, and the open request id is
        // attached to the host document for cross-run attribution.
        Assert.Contains(
            "new OpenOptions()",
            executor.LastSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "open-run-1",
            executor.LastSource,
            StringComparison.Ordinal);

        var payload = Value(result);
        Assert.Equal(
            targetPath,
            payload.GetProperty("document").GetProperty("path").GetString());
    }

    // =====================================================================
    // Postcondition host read (illustrator.document.read)
    // =====================================================================

    [Fact]
    public async Task ReadStateReportsObservedDocumentIdentity()
    {
        var app = DocumentFakeApplication.Create(
            "Target.ai",
            saved: true);

        var result = await ExecuteAsync(
            app,
            new RecordingScriptExecutor(),
            "illustrator.document.read",
            """{"index":0}""");

        Assert.True(
            result.Ok,
            $"status={result.Status} kind={result.Error?.Kind} msg={result.Error?.Message} exec={result.Error?.Execution}");

        var payload = Value(result);
        Assert.True(
            payload.GetProperty("document").GetProperty("saved").GetBoolean());
        Assert.Equal(
            "Target.ai",
            payload.GetProperty("document").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ReadStateReportsMissingDocumentAsObservedState()
    {
        var app = DocumentFakeApplication.Create("Only.ai");

        var result = await ExecuteAsync(
            app,
            new RecordingScriptExecutor(),
            "illustrator.document.read",
            """{"index":9}""");

        // A missing document is a truthful observation, not a hidden failure:
        // the runtime's condition evaluator owns what a false precondition
        // means.
        Assert.True(result.Ok);

        var payload = Value(result);
        Assert.False(payload.GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("document").ValueKind);
    }

    [Fact]
    public async Task ReadStateNeverMutatesTheHost()
    {
        var app = DocumentFakeApplication.Create("Only.ai");

        await ExecuteAsync(
            app,
            new RecordingScriptExecutor(),
            "illustrator.document.read",
            """{"index":0}""");

        Assert.Equal(0, app[0].CloseCallCount);
        Assert.Equal(0, app[0].SaveCallCount);
    }

    // =====================================================================
    // Deterministic ES3 script generation
    // =====================================================================

    [Fact]
    public void GeneratedScriptsAvoidModernJavaScriptSyntax()
    {
        var sources = new[]
        {
            IllustratorDocumentOperations.BuildCreateDocumentScript(
                documentColorSpace: 1,
                width: null,
                height: null),
            IllustratorDocumentOperations.BuildOpenDocumentScript(
                @"C:\tmp\a.ai",
                "req-1"),
            IllustratorDocumentOperations.BuildSaveDocumentScript(0),
            IllustratorDocumentOperations.BuildSaveAsDocumentScript(
                0,
                @"C:\tmp\b.ai"),
            IllustratorDocumentOperations.BuildCloseDocumentScript(
                0,
                IllustratorDocumentOperations.ClosePolicy.Discard)
        };

        foreach (var source in sources)
        {
            Assert.DoesNotContain("let ", source, StringComparison.Ordinal);
            Assert.DoesNotContain("const ", source, StringComparison.Ordinal);
            Assert.DoesNotContain("=>", source, StringComparison.Ordinal);
            Assert.DoesNotContain("`", source, StringComparison.Ordinal);
            Assert.DoesNotContain("class ", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MutationWrapperCarriesEsonGateAndEnvelope()
    {
        var wrapper =
            IllustratorScriptEval.BuildDocumentMutationWrapper(
                "return 'x';");

        Assert.Contains(
            "__comtool_v2_eson",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains(
            "comtoolTransport",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("return 'x';", wrapper, StringComparison.Ordinal);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static async Task<OperationResult> ExecuteAsync(
        DocumentFakeApplication app,
        RecordingScriptExecutor executor,
        string operation,
        string inputJson)
    {
        var identity = Identity();

        await using var session = new IllustratorSession(
            identity,
            app,
            executor.Execute);

        using var input = JsonDocument.Parse(inputJson);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "doc-test",
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Operation = operation,
            Input = input.RootElement.Clone()
        };

        return await session.ExecuteAsync(request);
    }

    private static JsonElement Value(OperationResult result)
    {
        Assert.NotNull(result.Result);
        return Assert.IsType<JsonElement>(result.Result!.Value);
    }

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = IllustratorAdapter.HostName,
            ProcessId = 4321,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-24T17:40:37Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = IllustratorAdapter.CurrentAdapterVersion,
            EndpointIdentity = IllustratorComInterop.ProgId
        };
}

/// <summary>
/// Records the exact ES3 source the adapter dispatches and answers with a
/// deterministic envelope. Mutating actions are applied to the fake document
/// set so postcondition reads observe a realistic result.
/// </summary>
public sealed class RecordingScriptExecutor
{
    private readonly HashSet<string> _injectedSaveRequestIds =
        new(StringComparer.Ordinal);

    public int CallCount { get; private set; }

    public string LastSource { get; private set; } = string.Empty;

    public HostAdapterException? DispatchFault { get; init; }

    public string? RawResponseOverride { get; init; }

    public string? ScriptFailureMessage { get; init; }

    public bool DeferDocumentRemoval { get; init; }

    public bool DocumentSetReadableAfterDispatch { get; init; } = true;

    public int DocumentsAddedAfterDispatch { get; init; }

    public string? AddedDocumentPath { get; init; }

    /// <summary>
    /// When set, the close script is honoured against a different document
    /// name, simulating a mutation that lands on the wrong target.
    /// </summary>
    public string? MisrouteCloseToName { get; init; }

    public string Execute(
        object application,
        string source,
        int executionMode)
    {
        CallCount++;
        LastSource = source;

        if (DispatchFault is not null)
            throw DispatchFault;

        if (RawResponseOverride is not null)
            return RawResponseOverride;

        var app = (DocumentFakeApplication)application;

        if (ScriptFailureMessage is not null)
        {
            return JsonSerializer.Serialize(
                new
                {
                    ok = false,
                    kind = "failed",
                    message = ScriptFailureMessage,
                    line = 1
                });
        }

        if (!DocumentSetReadableAfterDispatch)
        {
            app.SetDocumentsReadable(false);
            return Response("closed", null);
        }

        if (DocumentsAddedAfterDispatch > 0 &&
            !source.Contains("app.open(", StringComparison.Ordinal))
        {
            for (var i = 0; i < DocumentsAddedAfterDispatch; i++)
            {
                app.AddDocument(
                    AddedDocumentPath is null
                        ? $"Untitled-{app.Count + 1}"
                        : AddedDocumentPath);
            }

            return Response(
                "created",
                app[app.Count - 1].Name);
        }

        if (source.Contains("app.open(", StringComparison.Ordinal))
        {
            app.AddDocument(
                AddedDocumentPath
                ?? Path.Combine(Path.GetTempPath(), "opened.ai"));

            // Mirror the host-side SaveRequestId assignment the script
            // performs.
            var requestId = ExtractStringLiteralAfter(
                source,
                ".SaveRequestId=");

            if (requestId is not null)
            {
                app[app.Count - 1].SaveRequestId = requestId;
                _injectedSaveRequestIds.Add(requestId);
            }

            return Response("opened", app[app.Count - 1].Name);
        }

        if (source.Contains(".saveAs(", StringComparison.Ordinal))
        {
            var index = ExtractDocumentsItemIndex(source);
            var path = ExtractFileArgument(source);
            app[index].Path = path ?? app[index].Path;
            app[index].Saved = true;
            return Response("saved", app[index].Name);
        }

        if (source.Contains(".save()", StringComparison.Ordinal))
        {
            var index = ExtractDocumentsItemIndex(source);
            app[index].Saved = true;

            var requestId = ExtractStringLiteralAssignmentOfSaveRequestId(
                source);

            if (requestId is not null)
            {
                app[index].SaveRequestId = requestId;
                _injectedSaveRequestIds.Add(requestId);
            }

            return Response("saved", app[index].Name);
        }

        if (source.Contains(".close(", StringComparison.Ordinal))
        {
            var index = ExtractDocumentsItemIndex(source);
            var document = app[index];

            if (MisrouteCloseToName is not null)
            {
                var wrong = app.Items
                    .ToList()
                    .FindIndex(
                        candidate => string.Equals(
                            candidate.Name,
                            MisrouteCloseToName,
                            StringComparison.Ordinal));

                if (wrong >= 0)
                    document = app[wrong];
            }

            document.CloseCallCount++;

            if (!DeferDocumentRemoval)
                app.RemoveDocument(document);

            return Response("closed", document.Name);
        }

        return Response("completed", null);
    }

    private static string Response(
        string kind,
        string? document) =>
        JsonSerializer.Serialize(
            new
            {
                ok = true,
                kind,
                document,
                requestId = (string?)null
            });

    private static int ExtractDocumentsItemIndex(string source)
    {
        const string marker = "documents.item(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException(
                "Dispatched script did not address a document by item index.");

        start += marker.Length;
        var end = source.IndexOf(')', start);
        if (end < 0)
            throw new InvalidOperationException(
                "Dispatched script had an unterminated item index.");

        return int.Parse(
            source[start..end],
            System.Globalization.CultureInfo.InvariantCulture) - 1;
    }

    private static string? ExtractFileArgument(string source)
    {
        const string marker = "new File(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = source.IndexOf(')', start);
        if (end < 0)
            return null;

        var literal = source[start..end];
        try
        {
            return JsonSerializer.Deserialize<string>(literal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractStringLiteralAfter(
        string source,
        string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        return ReadLiteral(source, start);
    }

    private static string? ExtractStringLiteralAssignmentOfSaveRequestId(
        string source)
    {
        const string marker = "SaveRequestId=";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        return ReadLiteral(source, start);
    }

    private static string? ReadLiteral(string source, int start)
    {
        if (start >= source.Length || source[start] != '"')
            return null;

        for (var end = start + 1; end < source.Length; end++)
        {
            if (source[end] == '\\')
            {
                end++;
                continue;
            }

            if (source[end] != '"')
                continue;

            try
            {
                return JsonSerializer.Deserialize<string>(
                    source[start..(end + 1)]);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>
/// Minimal observable Illustrator application/document surface limited to what
/// the strict COM read bridge and the document-lifecycle adapter touch.
/// </summary>
public sealed class DocumentFakeApplication
{
    public DocumentFakeApplication(
        List<DocumentFakeDocument> documents)
    {
        _documents = documents;
        Documents = new DocumentFakeDocumentsCollection(this);
    }

    private readonly List<DocumentFakeDocument> _documents;

    /// <summary>
    /// Mirrors the Illustrator COM surface: the application exposes a
    /// collection object with Count/Item, not a .NET list.
    /// </summary>
    public DocumentFakeDocumentsCollection Documents { get; }

    public bool DocumentsReadable { get; set; } = true;

    public IReadOnlyList<DocumentFakeDocument> Items => _documents;

    // Convenience aliases used by the tests and the recording executor.
    public DocumentFakeDocument this[int zeroBasedIndex] =>
        _documents[zeroBasedIndex];

    public int Count => _documents.Count;

    public static DocumentFakeApplication Create(
        params string[] names) =>
        Create(names, saved: true, path: null);

    public static DocumentFakeApplication Create(
        string first,
        bool saved,
        string? path = null) =>
        Create([first], saved, path);

    public static DocumentFakeApplication Create(
        string[] names,
        bool saved,
        string? path) =>
        new(
            names
                .Select(
                    (name, index) => new DocumentFakeDocument(
                        name,
                        saved,
                        path ?? Path.Combine(
                            Path.GetTempPath(),
                            $"{index}-{name}")))
                .ToList());

    public void AddDocument(string name) =>
        _documents.Add(
            new DocumentFakeDocument(
                name,
                saved: true,
                Path.Combine(Path.GetTempPath(), name)));

    public void RemoveDocument(DocumentFakeDocument document) =>
        _documents.Remove(document);

    public void SetDocumentsReadable(bool readable) =>
        DocumentsReadable = readable;
}

public sealed class DocumentFakeDocumentsCollection(
    DocumentFakeApplication application)
{
    public int Count =>
        application.DocumentsReadable
            ? application.Count
            : -1;

    /// <summary>
    /// The 1-based collection accessor the strict COM read bridge resolves
    /// for a <c>[index]</c> path segment.
    /// </summary>
    public DocumentFakeDocument Item(int oneBasedIndex) =>
        application[oneBasedIndex - 1];

    public int RawCount => application.Count;
}

public sealed class DocumentFakeDocument
{
    public DocumentFakeDocument(
        string name,
        bool saved,
        string path)
    {
        Name = name;
        Saved = saved;
        Path = path;
    }

    public string Name { get; }

    public bool Saved { get; set; }

    public string? Path { get; set; }

    public string? SaveRequestId { get; set; }

    public int CloseCallCount { get; set; }

    public int SaveCallCount { get; set; }

    /// <summary>
    /// Reproduces the host behavior that reading FullName throws for an
    /// unsaved document; the adapter must degrade to null instead of failing
    /// the whole discovery.
    /// </summary>
    public DocumentFakeFullName FullName =>
        Saved
            ? new DocumentFakeFullName(Path)
            : throw new InvalidOperationException(
                "FullName is unavailable for an unsaved document.");
}

public sealed class DocumentFakeFullName(string? fsName)
{
    public string? FsName { get; } = fsName;
}
