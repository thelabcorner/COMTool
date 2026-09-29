using System.Diagnostics;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

internal sealed class IllustratorSession : IHostSession
{
    private const int DocumentReadExecutionMode = 0;

    private const string IllustratorDocumentReadOperation =
        "illustrator.document.read";

    private static readonly HashSet<string> DocumentOperationNames =
        new(StringComparer.Ordinal)
        {
            IllustratorDocumentOperations.CreateOperation,
            IllustratorDocumentOperations.OpenOperation,
            IllustratorDocumentOperations.SaveOperation,
            IllustratorDocumentOperations.SaveAsOperation,
            IllustratorDocumentOperations.CloseOperation
        };

    private static readonly HashSet<string> StructureOperationNames =
        new(StringComparer.Ordinal)
        {
            IllustratorStructureOperations.ArtboardReadOperation,
            IllustratorStructureOperations.LayerReadOperation
        };

    private static readonly HashSet<string> ActionOperationNames =
        new(StringComparer.Ordinal)
        {
            IllustratorActionRun.Operation
        };

    private object? _appObject;
    private readonly Func<object, string, int, string> _scriptExecutor;
    private readonly IllustratorDebugSessionManager _debugger;
    private readonly IllustratorPluginDebugManager _pluginDebug;

    public IllustratorSession(
        HostTargetIdentity identity,
        object appObject,
        Func<object, string, int, string>? scriptExecutor = null)
    {
        Identity = identity;
        _appObject = appObject;
        _scriptExecutor =
            scriptExecutor ??
            ((object application, string source, int executionMode) =>
                IllustratorComInterop.ExecuteJavaScript(
                    application,
                    source,
                    executionMode));
        _debugger =
            new IllustratorDebugSessionManager(identity);
        _pluginDebug =
            new IllustratorPluginDebugManager(
                identity,
                new AipDebugCtlClient());
    }

    public HostTargetIdentity Identity { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities =>
        IllustratorOperations.Capabilities;

    public ValueTask<OperationResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(request);

        if (request.Preconditions is { Count: > 0 } ||
            request.Postconditions is { Count: > 0 })
        {
            return ValueTask.FromResult(
                InvalidRequest(
                    request,
                    "conditions_require_runtime_supervisor",
                    "Preconditions and postconditions require runtime-supervisor orchestration."));
        }

        if (request.Policy?.RetryBudgetMs is { } retryBudgetMs &&
            !OperationPolicy.IsValidRetryBudget(retryBudgetMs))
        {
            return ValueTask.FromResult(
                InvalidRequest(
                    request,
                    "invalid_retry_budget",
                    $"{nameof(OperationPolicy.RetryBudgetMs)} must be between " +
                    $"{OperationPolicy.MinRetryBudgetMs} and " +
                    $"{OperationPolicy.MaxRetryBudgetMs} ms."));
        }

        using var retryBudgetScope =
            IllustratorComInterop.PushRetryBudget(
                request.Policy?.RetryBudgetMs);

        var started = Stopwatch.StartNew();
        try
        {
            var result = request.Operation switch
            {
                "core.target.status" => Status(request),
                "core.target.snapshot" => Snapshot(request),
                "com.get" => ComGet(request),
                "com.call.read" => ComCallRead(request),
                IllustratorComMutationBridge.SetOperation => ComSet(request),
                IllustratorComMutationBridge.CallOperation => ComCall(request),
                IllustratorPluginMessage.Operation => PluginMessage(request),
                IllustratorPluginDebugManager.DiagnosticsOperation or
                IllustratorPluginDebugManager.ControlOperation =>
                    _pluginDebug.Execute(
                        request,
                        GetApp(),
                        cancellationToken),
                IllustratorMenuCommand.ExecuteOperation => MenuCommand(request),
                IllustratorDebugSessionManager.OpenOperation or
                IllustratorDebugSessionManager.StatusOperation or
                IllustratorDebugSessionManager.CommandOperation or
                IllustratorDebugSessionManager.CloseOperation =>
                    _debugger.Execute(request),
                IllustratorScriptCodecStatus.Operation =>
                    IllustratorScriptCodecStatus.Execute(
                        GetApp(),
                        request,
                        _scriptExecutor),
                "script.eval" => ScriptEval(request),
                IllustratorScriptRunFile.Operation => ScriptRunFile(request),
                "illustrator.artboard.setName" => ArtboardSetName(request),
                _ when ActionOperationNames.Contains(
                    request.Operation) => IllustratorActionRun.Execute(
                        GetApp(),
                        request,
                        cancellationToken),
                _ when StructureOperationNames.Contains(
                    request.Operation) => IllustratorStructureOperations.Execute(
                        GetApp(),
                        request),
                _ when IllustratorTypedMutationOperations.OperationNames.Contains(
                    request.Operation) => IllustratorTypedMutationOperations.Execute(
                        GetApp(),
                        request),
                IllustratorDocumentReadOperation => IllustratorDocumentOperations.ReadState(
                    GetApp(),
                    request),
                _ when DocumentOperationNames.Contains(
                    request.Operation) => IllustratorDocumentOperations.Execute(
                        GetApp(),
                        request,
                        _scriptExecutor),
                _ => Unsupported(request)
            };

            started.Stop();
            return ValueTask.FromResult(result with
            {
                Timing = new OperationTiming(
                    ExecuteMs: started.Elapsed.TotalMilliseconds,
                    TotalMs: started.Elapsed.TotalMilliseconds)
            });
        }
        catch (HostAdapterException ex)
        {
            started.Stop();
            return ValueTask.FromResult(new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = ex.Execution == ExecutionState.Ambiguous
                    ? OperationStatus.ReconciliationRequired
                    : ex.Retryable
                        ? OperationStatus.HostBusy
                        : OperationStatus.Failed,
                TargetState = ex.Execution == ExecutionState.Ambiguous
                    ? TargetState.ReconciliationRequired
                    : ex.Retryable
                        ? TargetState.Busy
                        : TargetState.Known,
                Error = ex.ToProtocolError(
                    ex.Retryable ? "retry_within_budget" : "inspect_host_state"),
                // Purely additive. Every status, target state, kind, message,
                // retryability, and execution value above is still derived
                // from the exception alone; a host that attaches no evidence
                // serializes byte-for-byte as before.
                Evidence = ex.Evidence,
                Timing = new OperationTiming(
                    ExecuteMs: started.Elapsed.TotalMilliseconds,
                    TotalMs: started.Elapsed.TotalMilliseconds)
            });
        }
    }

    public ValueTask<ReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            dynamic app = GetApp();
            var version = IllustratorComInterop.RetryRead(
                () => Convert.ToString(app.Version) ?? string.Empty);

            var evidence = JsonSerializer.SerializeToElement(
                new { version, identity = Identity.TargetId });

            return ValueTask.FromResult(new ReconciliationResult(
                Reconciled: true,
                TargetState.Known,
                Evidence: [new EvidenceItem("host.heartbeat", evidence)]));
        }
        catch (HostAdapterException ex)
        {
            return ValueTask.FromResult(new ReconciliationResult(
                Reconciled: false,
                TargetState.Unavailable,
                Error: ex.ToProtocolError("reconnect_target")));
        }
    }

    public ValueTask DisposeAsync()
    {
        _debugger.Dispose();
        var app = Interlocked.Exchange(ref _appObject, null);
        IllustratorComInterop.Release(app);
        return ValueTask.CompletedTask;
    }

    private OperationResult Status(OperationRequest request)
    {
        dynamic app = GetApp();

        var payload = JsonSerializer.SerializeToElement(new
        {
            name = ReadString(() => app.Name),
            version = ReadString(() => app.Version),
            buildNumber = ReadString(() => app.BuildNumber),
            scriptingVersion = ReadString(() => app.ScriptingVersion),
            locale = ReadString(() => app.Locale),
            actionIsRunning = ReadBool(() => app.ActionIsRunning),
            userInteractionLevel = ReadInt(() => app.UserInteractionLevel),
            coordinateSystem = ReadInt(() => app.CoordinateSystem),
            freeMemory = ReadOptionalInt(() => app.FreeMemory),
            documentsCount = ReadRequiredCollectionCount(() => app.Documents)
        });

        return Success(request, ProtocolValue.From(payload));
    }

    private OperationResult Snapshot(OperationRequest request)
    {
        dynamic app = GetApp();
        var documentCount = ReadRequiredCollectionCount(() => app.Documents);

        var application = new
        {
            name = ReadOptionalString(() => app.Name),
            version = ReadOptionalString(() => app.Version),
            buildNumber = ReadOptionalString(() => app.BuildNumber),
            coordinateSystem = ReadOptionalInt(() => app.CoordinateSystem),
            documentsCount = documentCount
        };

        object? documentObject = null;
        try
        {
            object? activeDocument = null;
            if (documentCount > 0)
            {
                activeDocument = IllustratorComInterop.RetryRead<object?>(
                    () => app.ActiveDocument);
                documentObject = activeDocument;
            }

            if (activeDocument is null)
            {
                var emptyPayload = JsonSerializer.SerializeToElement(new
                {
                    documentsCount = documentCount,
                    application,
                    activeDocument = (object?)null,
                    selection = Array.Empty<object>()
                });

                return Success(request, ProtocolValue.From(emptyPayload));
            }

            dynamic document = activeDocument;
            ArtboardsFingerprint artboards = ReadArtboards(document);
            IReadOnlyList<object> selection = ReadSelection(document);

            var activeDocumentPayload = new
            {
                name = ReadOptionalString(() => document.Name),
                path = ReadOptionalString(() => document.Path),
                fullName = ReadOptionalString(() => document.FullName),
                saved = ReadOptionalBool(() => document.Saved),
                width = ReadOptionalDouble(() => document.Width),
                height = ReadOptionalDouble(() => document.Height),
                colorSpace = ReadOptionalInt(() => document.DocumentColorSpace),
                layersCount = ReadCollectionCount(() => document.Layers),
                artboardsCount = artboards.Count,
                activeArtboardIndex = artboards.ActiveIndex,
                pageItemsCount = ReadCollectionCount(() => document.PageItems),
                pathItemsCount = ReadCollectionCount(() => document.PathItems),
                selectionCount = selection.Count,
                rulerOrigin = ReadOptionalDoubleArray(() => document.RulerOrigin)
            };

            var payload = JsonSerializer.SerializeToElement(new
            {
                documentsCount = documentCount,
                application,
                activeDocument = activeDocumentPayload,
                selection
            });

            return Success(request, ProtocolValue.From(payload));
        }
        finally
        {
            IllustratorComInterop.Release(documentObject);
        }
    }

    private OperationResult ComGet(OperationRequest request)
    {
        if (!TryReadStrictInput(
                request,
                allowedProperties: ["path"],
                out var inputError))
        {
            return inputError;
        }

        if (!TryGetRequiredString(
                request.Input,
                "path",
                out var path,
                out var pathError))
        {
            return InvalidRequest(
                request,
                "invalid_com_path",
                pathError);
        }

        try
        {
            var payload = IllustratorComReadBridge.Get(
                GetApp(),
                path);

            return Success(
                request,
                ProtocolValue.From(payload));
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(
                request,
                "invalid_com_path",
                ex.Message);
        }
    }

    private OperationResult ComCallRead(OperationRequest request)
    {
        if (!TryReadStrictInput(
                request,
                allowedProperties: ["path", "args"],
                out var inputError))
        {
            return inputError;
        }

        if (!TryGetRequiredString(
                request.Input,
                "path",
                out var path,
                out var pathError))
        {
            return InvalidRequest(
                request,
                "invalid_com_call",
                pathError);
        }

        var args = request.Input.TryGetProperty(
                "args",
                out var argsElement)
            ? argsElement
            : default;

        if (args.ValueKind != JsonValueKind.Undefined &&
            args.ValueKind != JsonValueKind.Null &&
            args.ValueKind != JsonValueKind.Array)
        {
            return InvalidRequest(
                request,
                "invalid_com_call",
                "'args' must be a JSON array.");
        }

        try
        {
            var payload = IllustratorComReadBridge.CallRead(
                GetApp(),
                path,
                args);

            return Success(
                request,
                ProtocolValue.From(payload));
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(
                request,
                "invalid_com_call",
                ex.Message);
        }
    }

    private OperationResult ComSet(OperationRequest request)
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            request.Input,
            out var error);

        if (plan is null)
            return InvalidRequest(request, error!.Kind, error.Message);

        IllustratorComMutationBridge.ApplySet(GetApp(), plan);

        // The dispatched value is echoed, not read back. A verification read
        // would be a second COM dispatch after a mutation dispatch, which
        // widens the ambiguity window, so this result must not be read as
        // proof that the host now holds the value.
        var payload = JsonSerializer.SerializeToElement(new
        {
            property = plan.Member,
            path = plan.Path,
            value = plan.Value,
            mutationClass = "external_side_effect"
        });

        return Success(request, ProtocolValue.From(payload));
    }

    private OperationResult ComCall(OperationRequest request)
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            request.Input,
            out var error);

        if (plan is null)
            return InvalidRequest(request, error!.Kind, error.Message);

        var result = IllustratorComMutationBridge.InvokeCall(
            GetApp(),
            plan);

        var payload = JsonSerializer.SerializeToElement(new
        {
            method = plan.Member,
            path = plan.Path,
            argumentCount = plan.Args.Length,
            mutationClass = "external_side_effect",
            result
        });

        return Success(request, ProtocolValue.From(payload));
    }

    private OperationResult ArtboardSetName(OperationRequest request)
    {
        const string propertyKey =
            "document.artboard.active.name";

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "property",
            "value"
        };

        if (!TryReadStrictInput(request, allowed, out var inputError))
            return inputError;

        if (!IllustratorComWriteBridge.TryGetTarget(
                propertyKey,
                out var target))
        {
            // Runtime-owned invariant: the registered operation must have a
            // registered write target. This can only happen if the catalog
            // and the allowlist drift, which is a runtime defect.
            throw new InvalidOperationException(
                $"Write target '{propertyKey}' is not registered.");
        }

        if (!TryGetRequiredString(
                request.Input,
                "property",
                out var property,
                out var propertyError))
        {
            return InvalidRequest(
                request,
                "invalid_com_property",
                propertyError);
        }

        if (!string.Equals(
                property,
                target.Key,
                StringComparison.Ordinal))
        {
            return InvalidRequest(
                request,
                "unsupported_com_property",
                $"'property' must be '{target.Key}'.");
        }

        if (!request.Input.TryGetProperty(
                "value",
                out var valueElement))
        {
            return InvalidRequest(
                request,
                "invalid_com_value",
                "'value' is required.");
        }

        if (valueElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(
                valueElement.GetString()))
        {
            return InvalidRequest(
                request,
                "invalid_com_value",
                "'value' must be a non-empty string.");
        }

        if (!IsEsonInstalled())
        {
            // The fixed setter encodes its value through ESON. Without the
            // canonical runtime the mutation cannot be dispatched at all, so
            // this fails before dispatch (not_started), not ambiguously.
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
                    Kind = "eson_bootstrap_required",
                    Message =
                        "The canonical ESON runtime is not installed in the host; the fixed property put cannot be dispatched.",
                    Retryable = false,
                    Execution = ExecutionState.NotStarted,
                    SuggestedActions =
                    [
                        "eval_any_script_to_bootstrap_eson",
                        "retry_operation"
                    ]
                }
            };
        }

        var body = IllustratorComWriteBridge.BuildSetScript(
            target,
            valueElement.GetRawText());

        var dispatch = IllustratorComWriteBridge.Dispatch(
            GetApp(),
            body,
            _scriptExecutor);

        if (dispatch.TransportFault is { } transportFault)
            throw transportFault;

        if (dispatch.NonAmbiguousFault is { } nonAmbiguousFault)
            throw nonAmbiguousFault;

        var outcome = dispatch.Outcome
            ?? throw new HostAdapterException(
                "com_set_transport_invalid",
                IllustratorComWriteEnvelope.MalformedMessage,
                retryable: false,
                ExecutionState.Ambiguous);

        if (outcome.Kind == "set")
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                property = target.Key,
                name = outcome.Name,
                mutationClass = "idempotent_write"
            });

            return Success(
                request,
                ProtocolValue.From(payload));
        }

        return outcome.Kind switch
        {
            "no_active_document" or "no_active_artboard" =>
                InvalidRequest(
                    request,
                    outcome.Kind,
                    outcome.Kind == "no_active_document"
                        ? "The target has no active document to mutate."
                        : "The active document has no active artboard to mutate."),

            _ => new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = OperationStatus.ReconciliationRequired,
                TargetState = TargetState.ReconciliationRequired,
                Error = new ProtocolError
                {
                    Kind = "com_set_outcome_ambiguous",
                    Message = outcome.Message
                        ?? "The fixed property put did not report a provable outcome.",
                    Retryable = false,
                    Execution = ExecutionState.Ambiguous,
                    SuggestedActions =
                    [
                        "inspect_mutation_ledger",
                        "apply_operation_specific_postconditions",
                        "resolve_incident_explicitly"
                    ]
                }
            }
        };
    }

    private OperationResult ScriptRunFile(OperationRequest request)
    {
        ScriptRunFileRequest parsed;
        try
        {
            parsed = IllustratorScriptRunFile.ParseRequest(
                request.Input);
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(
                request,
                "invalid_script_run_file",
                ex.Message);
        }

        var outcome = IllustratorScriptRunFile.Execute(
            GetApp(),
            parsed,
            _scriptExecutor);

        if (outcome.Ok)
        {
            return Success(
                request,
                outcome.Value
                ?? throw new InvalidOperationException(
                    "Successful script-file execution produced no protocol value."))
                with
                {
                    Evidence =
                    [
                        new EvidenceItem(
                            "script.result",
                            JsonSerializer.SerializeToElement(new
                            {
                                mode = parsed.ResultMode,
                                present = outcome.ResultPresent
                            }))
                    ]
                };
        }

        var scriptError = outcome.Error
            ?? new ScriptEvalError(
                "Error",
                "Unknown ExtendScript file error.",
                null,
                null);

        var location = scriptError.SourceLine is not null
            ? $" (file line {scriptError.SourceLine.Value})"
            : string.Empty;

        var evidencePayload = JsonSerializer.SerializeToElement(new
        {
            path = parsed.Path,
            expectedSha256 = parsed.ExpectedSha256,
            scriptError.Name,
            scriptError.Message,
            fileLine = scriptError.SourceLine
        });

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.ReconciliationRequired,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = "script_file_error",
                Message =
                    $"{scriptError.Name}: {scriptError.Message}{location}",
                Retryable = false,
                Execution = ExecutionState.Ambiguous,
                SuggestedActions =
                [
                    "inspect_script_file_error",
                    "reconcile_target_before_mutation"
                ]
            },
            Evidence =
            [
                new EvidenceItem(
                    "script.file.error",
                    evidencePayload)
            ]
        };
    }

    private bool IsEsonInstalled()
    {
        try
        {
            var raw = _scriptExecutor(
                GetApp(),
                IllustratorScriptEval.EsonPresenceProbeSource,
                IllustratorScriptEval.NeverShowDebugger);

            return string.Equals(
                raw,
                IllustratorScriptEval.EsonReadyToken,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is HostAdapterException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    private OperationResult PluginMessage(OperationRequest request)
    {
        PluginMessageRequest parsed;
        try
        {
            parsed = IllustratorPluginMessage.ParseRequest(request.Input);
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(
                request,
                "invalid_plugin_message",
                ex.Message);
        }

        PluginMessageOutcome outcome;
        try
        {
            outcome = IllustratorPluginMessage.Execute(
                GetApp(),
                parsed);
        }
        catch (PluginMessageResponseTooLargeException ex)
        {
            var evidence = JsonSerializer.SerializeToElement(new
            {
                plugin = parsed.Plugin,
                selector = parsed.Selector,
                inputUtf8Bytes = ex.InputUtf8Bytes,
                inputSha256 = ex.InputSha256,
                responseUtf8Bytes = ex.ResponseUtf8Bytes,
                responseSha256 = ex.ResponseSha256
            });

            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = OperationStatus.Failed,
                TargetState = TargetState.KnownChanged,
                Error = new ProtocolError
                {
                    Kind = "plugin_response_too_large",
                    Message = ex.Message,
                    Retryable = false,
                    Execution = ExecutionState.Completed,
                    SuggestedActions =
                    [
                        "reduce_plugin_response",
                        "return_artifact_reference"
                    ]
                },
                Evidence =
                [
                    new EvidenceItem(
                        "plugin.message.provenance",
                        evidence)
                ]
            };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            plugin = parsed.Plugin,
            selector = parsed.Selector,
            response = outcome.Response,
            inputUtf8Bytes = outcome.InputUtf8Bytes,
            inputSha256 = outcome.InputSha256,
            responseUtf8Bytes = outcome.ResponseUtf8Bytes,
            responseSha256 = outcome.ResponseSha256
        });

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.KnownChanged,
            Result = ProtocolValue.From(payload)
        };
    }

    private OperationResult MenuCommand(OperationRequest request) =>
        IllustratorMenuCommand.Execute(
            GetApp(),
            request,
            hostVersion: Identity.HostVersion);

    private OperationResult ScriptEval(OperationRequest request)
    {
        ScriptEvalRequest parsed;
        try
        {
            parsed = IllustratorScriptEval.ParseRequest(request.Input);
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(
                request,
                "invalid_script_eval",
                ex.Message);
        }

        var outcome = IllustratorScriptEval.Execute(
            GetApp(),
            parsed,
            _scriptExecutor);

        if (outcome.Ok)
        {
            return Success(
                request,
                outcome.Value
                ?? throw new InvalidOperationException(
                    "Successful script evaluation produced no protocol value."))
                with
                {
                    Evidence =
                    [
                        new EvidenceItem(
                            "script.result",
                            JsonSerializer.SerializeToElement(new
                            {
                                mode = parsed.ResultMode,
                                present = outcome.ResultPresent
                            }))
                    ]
                };
        }

        var scriptError = outcome.Error
            ?? new ScriptEvalError(
                "Error",
                "Unknown ExtendScript error.",
                null,
                null);

        var location = scriptError.SourceLine is not null
            ? $" (source line {scriptError.SourceLine.Value})"
            : scriptError.WrapperLine is not null
                ? $" (wrapper line {scriptError.WrapperLine.Value})"
                : string.Empty;

        var evidencePayload = JsonSerializer.SerializeToElement(new
        {
            scriptError.Name,
            scriptError.Message,
            scriptError.WrapperLine,
            scriptError.SourceLine
        });

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.ReconciliationRequired,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = "script_error",
                Message =
                    $"{scriptError.Name}: {scriptError.Message}{location}",
                Retryable = false,
                Execution = ExecutionState.Ambiguous,
                SuggestedActions =
                [
                    "inspect_script_error",
                    "reconcile_target_before_mutation"
                ]
            },
            Evidence =
            [
                new EvidenceItem(
                    "script.error",
                    evidencePayload)
            ]
        };
    }

    private static ArtboardsFingerprint ReadArtboards(dynamic document)
    {
        object? artboardsObject = null;
        try
        {
            artboardsObject = ReadOptionalObject(() => document.Artboards);
            if (artboardsObject is null)
                return new ArtboardsFingerprint(null, null);

            var count = ReadCollectionCountValue(artboardsObject);
            if (artboardsObject is Array)
                return new ArtboardsFingerprint(count, null);

            dynamic artboards = artboardsObject;
            return new ArtboardsFingerprint(
                count,
                ReadOptionalInt(() => artboards.GetActiveArtboardIndex()));
        }
        finally
        {
            IllustratorComInterop.Release(artboardsObject);
        }
    }

    private static IReadOnlyList<object> ReadSelection(dynamic document)
    {
        object? selectionValue;
        try
        {
            selectionValue = IllustratorComInterop.RetryRead<object?>(
                () => document.Selection);
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return [];
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return [];
        }

        if (selectionValue is null)
            return [];

        var result = new List<object>();

        if (selectionValue is Array array)
        {
            foreach (var rawItem in array)
            {
                if (rawItem is null)
                    continue;

                try
                {
                    result.Add(ReadSelectedItem(rawItem));
                }
                finally
                {
                    IllustratorComInterop.Release(rawItem);
                }
            }

            return result;
        }

        try
        {
            result.Add(ReadSelectedItem(selectionValue));
            return result;
        }
        finally
        {
            IllustratorComInterop.Release(selectionValue);
        }
    }

    private static object ReadSelectedItem(object itemObject)
    {
        dynamic item = itemObject;
        var pageItemType = ReadOptionalInt(() => item.PageItemType);

        return new
        {
            pageItemType,
            pageItemTypeName = PageItemTypeName(pageItemType),
            uuid = ReadOptionalString(() => item.Uuid),
            name = ReadOptionalString(() => item.Name),
            locked = ReadOptionalBool(() => item.Locked),
            hidden = ReadOptionalBool(() => item.Hidden),
            geometricBounds = ReadOptionalDoubleArray(() => item.GeometricBounds),
            visibleBounds = ReadOptionalDoubleArray(() => item.VisibleBounds)
        };
    }

    private static int? ReadCollectionCount(Func<object?> collectionGetter)
    {
        object? collectionObject = null;
        try
        {
            collectionObject = ReadOptionalObject(collectionGetter);
            return ReadCollectionCountValue(collectionObject);
        }
        finally
        {
            IllustratorComInterop.Release(collectionObject);
        }
    }

    private static int ReadRequiredCollectionCount(Func<object?> collectionGetter)
    {
        object? collectionObject = null;
        try
        {
            collectionObject = IllustratorComInterop.RetryRead(collectionGetter);
            var count = ReadCollectionCountValue(collectionObject);
            if (count is not null)
                return count.Value;

            throw new HostAdapterException(
                "collection_count_unavailable",
                "Illustrator collection did not expose a numeric count.",
                retryable: false,
                ExecutionState.Started);
        }
        finally
        {
            IllustratorComInterop.Release(collectionObject);
        }
    }

    internal static int? ReadCollectionCountValue(object? collectionObject)
    {
        if (collectionObject is null)
            return null;

        if (collectionObject is Array array)
            return array.Length;

        dynamic collection = collectionObject;
        return ReadOptionalInt(() => collection.Count);
    }

    private static object? ReadOptionalObject(Func<object?> getter)
    {
        try
        {
            return IllustratorComInterop.RetryRead(getter);
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return null;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static string? ReadOptionalString(Func<object?> getter)
    {
        try
        {
            return IllustratorComInterop.RetryRead(
                () => Convert.ToString(getter()));
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return null;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static int? ReadOptionalInt(Func<object?> getter)
    {
        try
        {
            return IllustratorComInterop.RetryRead(
                () => Convert.ToInt32(getter()));
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return null;
        }
        catch (Exception ex) when (
            ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                InvalidCastException or
                FormatException or
                OverflowException)
        {
            return null;
        }
    }

    private static bool? ReadOptionalBool(Func<object?> getter)
    {
        try
        {
            return IllustratorComInterop.RetryRead(
                () => Convert.ToBoolean(getter()));
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return null;
        }
        catch (Exception ex) when (
            ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                InvalidCastException or
                FormatException)
        {
            return null;
        }
    }

    private static double? ReadOptionalDouble(Func<object?> getter)
    {
        try
        {
            return IllustratorComInterop.RetryRead(
                () => Convert.ToDouble(getter()));
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return null;
        }
        catch (Exception ex) when (
            ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                InvalidCastException or
                FormatException or
                OverflowException)
        {
            return null;
        }
    }

    private static double[]? ReadOptionalDoubleArray(Func<object?> getter)
    {
        object? raw;
        try
        {
            raw = IllustratorComInterop.RetryRead(getter);
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            return null;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }

        try
        {
            if (raw is not Array array)
                return null;

            var result = new double[array.Length];
            for (var i = 0; i < array.Length; i++)
            {
                var value = array.GetValue(i);
                if (value is null)
                    return null;

                result[i] = Convert.ToDouble(value);
            }

            return result;
        }
        catch (Exception ex) when (
            ex is InvalidCastException or
                FormatException or
                OverflowException)
        {
            return null;
        }
        finally
        {
            IllustratorComInterop.Release(raw);
        }
    }

    private static string? PageItemTypeName(int? pageItemType) =>
        pageItemType switch
        {
            1 => "compound_path",
            2 => "graph",
            3 => "group",
            4 => "mesh",
            5 => "path",
            6 => "placed",
            7 => "plugin",
            8 => "raster",
            9 => "symbol",
            10 => "text_frame",
            11 => "legacy_text",
            12 => "non_native",
            13 => "embedded",
            14 => "symmetry_repeat",
            15 => "radial_repeat",
            16 => "grid_repeat",
            _ => null
        };

    private static bool TryReadStrictInput(
        OperationRequest request,
        IReadOnlyCollection<string> allowedProperties,
        out OperationResult error)
    {
        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error = InvalidRequest(
                request,
                "invalid_input",
                "Operation input must be a JSON object.");
            return false;
        }

        foreach (var property in request.Input.EnumerateObject())
        {
            if (allowedProperties.Contains(property.Name))
                continue;

            error = InvalidRequest(
                request,
                "unknown_input_field",
                $"Unknown input field '{property.Name}'.");
            return false;
        }

        error = null!;
        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement input,
        string propertyName,
        out string value,
        out string error)
    {
        if (!input.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            value = string.Empty;
            error = $"'{propertyName}' must be a non-empty string.";
            return false;
        }

        value = element.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"'{propertyName}' must be a non-empty string.";
            return false;
        }

        error = string.Empty;
        return true;
    }

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

    private OperationResult Unsupported(OperationRequest request) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.UnsupportedOperation,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = "unsupported_operation",
                Message = $"Illustrator adapter does not support '{request.Operation}'.",
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = ["core.target.capabilities"]
            }
        };

    private OperationResult Success(
        OperationRequest request,
        ProtocolValue value) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = value
        };

    private void ValidateTarget(OperationRequest request)
    {
        if (request.Target is null)
            throw new HostAdapterException(
                "target_required",
                "Illustrator operations require an explicit target.",
                retryable: false,
                ExecutionState.NotStarted);

        if (!string.Equals(request.Target.Host, IllustratorAdapter.HostName, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(request.Target.Id) &&
             !string.Equals(request.Target.Id, Identity.TargetId, StringComparison.Ordinal)))
        {
            throw new HostAdapterException(
                "target_mismatch",
                "Request target does not match this Illustrator worker.",
                retryable: false,
                ExecutionState.NotStarted);
        }
    }

    private object GetApp() =>
        _appObject ?? throw new ObjectDisposedException(nameof(IllustratorSession));

    private static string? ReadString(Func<object?> getter) =>
        IllustratorComInterop.RetryRead(() => Convert.ToString(getter()));

    private static int ReadInt(Func<object?> getter) =>
        IllustratorComInterop.RetryRead(() => Convert.ToInt32(getter()));

    private static bool ReadBool(Func<object?> getter) =>
        IllustratorComInterop.RetryRead(() => Convert.ToBoolean(getter()));

    private sealed record ArtboardsFingerprint(
        int? Count,
        int? ActiveIndex);
}
