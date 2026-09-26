using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Supervisor;

internal static class Program
{
    private const string IllustratorProgId = "Illustrator.Application";
    private const int LeaseTtlMs = 300_000;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    [STAThread]
    private static int Main(string[] args)
    {
        object? ownerApp = null;
        RuntimeSupervisor? runtime = null;
        TargetRef? target = null;
        string? leaseId = null;
        string? createdDocumentName = null;
        OwnedProcessIdentity? ownedProcess = null;
        var stateDirectory = string.Empty;
        var steps = new List<object>();
        string? cleanupWarning = null;

        try
        {
            var workerPath = RequireOption(args, "--worker");
            stateDirectory =
                Option(args, "--state-dir")
                ?? Path.Combine(
                    Path.GetTempPath(),
                    "comtool-v2-live-conformance",
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

            workerPath = Path.GetFullPath(workerPath);
            stateDirectory = Path.GetFullPath(stateDirectory);

            if (!File.Exists(workerPath))
                throw new FileNotFoundException(
                    "Conformance worker executable was not found.",
                    workerPath);

            var preexisting = GetIllustratorProcesses();
            try
            {
                if (preexisting.Count != 0)
                {
                    Write(new
                    {
                        ok = false,
                        kind = "preexisting_illustrator_detected",
                        message =
                            "Owned-host live conformance refuses to start while Illustrator is already running.",
                        processIds = preexisting
                            .Select(static process => process.Id)
                            .ToArray()
                    });
                    return 3;
                }
            }
            finally
            {
                DisposeAll(preexisting);
            }

            ownerApp = LaunchOwnedIllustrator();
            dynamic app = ownerApp;
            var version = Convert.ToString(app.Version) ?? string.Empty;
            var documentsAtLaunch = Convert.ToInt32(app.Documents.Count);
            if (documentsAtLaunch != 0)
            {
                throw new InvalidOperationException(
                    $"Owned Illustrator launch unexpectedly contained {documentsAtLaunch} document(s).");
            }

            ownedProcess = WaitForSingleIllustratorProcess(
                TimeSpan.FromSeconds(30));
            steps.Add(new
            {
                step = "owned-host-launch",
                ok = true,
                version,
                processId = ownedProcess.ProcessId,
                processStartedAt = ownedProcess.ProcessStartedAt,
                documents = documentsAtLaunch
            });

            runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath = workerPath,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    CommandTimeout = TimeSpan.FromSeconds(60)
                },
                stateDirectory);

            var targets = Execute(
                runtime,
                Request(
                    "live-targets",
                    target: null,
                    "core.targets.list",
                    JsonElementOf(new { refresh = true })));
            RequireOk(targets, "target discovery");

            var targetValue = RequireValue(targets);
            if (targetValue.ValueKind != JsonValueKind.Array ||
                targetValue.GetArrayLength() != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one live Illustrator target, got {targetValue.GetArrayLength()}.");
            }

            var targetEntry = targetValue[0];
            var identity = targetEntry.GetProperty("identity");
            var discoveredPid = identity.GetProperty("processId").GetInt32();
            if (discoveredPid != ownedProcess.ProcessId)
            {
                throw new InvalidOperationException(
                    $"Runtime discovered Illustrator PID {discoveredPid}, but the harness owns PID {ownedProcess.ProcessId}.");
            }

            var targetElement = targetEntry.GetProperty("target");
            target = new TargetRef(
                targetElement.GetProperty("host").GetString()
                    ?? throw new InvalidOperationException("Target host is missing."),
                targetElement.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("Target id is missing."),
                targetElement.TryGetProperty("generation", out var generation) &&
                generation.ValueKind == JsonValueKind.Number
                    ? generation.GetInt64()
                    : null);

            steps.Add(new
            {
                step = "runtime-discovery-owned-pid",
                ok = true,
                targetId = target.Id,
                processId = discoveredPid
            });

            var lease = Execute(
                runtime,
                Request(
                    "live-lease-acquire",
                    target,
                    "core.target.lease.acquire",
                    JsonElementOf(new { ttlMs = LeaseTtlMs })));
            RequireOk(lease, "lease acquisition");
            leaseId = RequireValue(lease)
                .GetProperty("leaseId")
                .GetString()
                ?? throw new InvalidOperationException(
                    "Lease acquisition returned no lease id.");

            steps.Add(new
            {
                step = "exclusive-target-lease",
                ok = true,
                ttlMs = LeaseTtlMs
            });

            var bootstrap = Execute(
                runtime,
                Request(
                    "live-eson-bootstrap",
                    target,
                    "script.eval",
                    JsonElementOf(new
                    {
                        kind = "expression",
                        source = "1+1"
                    }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId)));
            RequireOk(bootstrap, "ESON bootstrap");

            var create = Execute(
                runtime,
                Request(
                    "live-document-create",
                    target,
                    "illustrator.document.create",
                    JsonElementOf(new
                    {
                        documentColorSpace = 1,
                        width = 256,
                        height = 256
                    }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId)));
            RequireOk(create, "disposable document creation");
            var createValue = RequireValue(create);
            createdDocumentName =
                createValue.GetProperty("document")
                    .GetProperty("name")
                    .GetString()
                ?? throw new InvalidOperationException(
                    "Document creation returned no document name.");

            steps.Add(new
            {
                step = "disposable-document-create",
                ok = true,
                documentName = createdDocumentName
            });

            var selectorInput =
                JsonElementOf(new { name = createdDocumentName });

            var documentRead = Execute(
                runtime,
                Request(
                    "live-document-read",
                    target,
                    "illustrator.document.read",
                    selectorInput));
            RequireOk(documentRead, "document read");
            RequireBoolean(
                RequireValue(documentRead),
                "exists",
                expected: true,
                "document read");

            var artboards = Execute(
                runtime,
                Request(
                    "live-artboards-read",
                    target,
                    "illustrator.artboard.read",
                    selectorInput));
            RequireOk(artboards, "artboard structure read");
            var artboardValue = RequireValue(artboards);
            RequireBoolean(
                artboardValue,
                "exists",
                expected: true,
                "artboard structure read");
            if (artboardValue.GetProperty("artboardCount").GetInt32() < 1)
                throw new InvalidOperationException(
                    "Disposable document has no readable artboard.");

            var layers = Execute(
                runtime,
                Request(
                    "live-layers-read",
                    target,
                    "illustrator.layer.read",
                    selectorInput));
            RequireOk(layers, "layer structure read");
            var layerValue = RequireValue(layers);
            RequireBoolean(
                layerValue,
                "exists",
                expected: true,
                "layer structure read");
            if (layerValue.GetProperty("layerCount").GetInt32() < 1)
                throw new InvalidOperationException(
                    "Disposable document has no readable layer.");

            steps.Add(new
            {
                step = "wave4-structure-reads",
                ok = true,
                artboardCount =
                    artboardValue.GetProperty("artboardCount").GetInt32(),
                layerCount =
                    layerValue.GetProperty("layerCount").GetInt32()
            });

            var controlledName =
                "COMTOOL_V2_CONTROLLED_" +
                Guid.NewGuid().ToString("N")[..12];
            var activeDocumentCondition = EqualsCondition(
                "active-document-is-disposable",
                "com.get",
                JsonElementOf(new { path = "ActiveDocument.Name" }),
                ProtocolValue.FromString(createdDocumentName));
            var controlledPostcondition = EqualsCondition(
                "controlled-artboard-name-landed",
                "com.get",
                JsonElementOf(
                    new { path = "ActiveDocument.Artboards[0].Name" }),
                ProtocolValue.FromString(controlledName));

            var controlledMutation = Execute(
                runtime,
                Request(
                    "live-controlled-artboard-name",
                    target,
                    "illustrator.artboard.setName",
                    JsonElementOf(new
                    {
                        property =
                            "document.artboard.active.name",
                        value = controlledName
                    }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId),
                    preconditions: [activeDocumentCondition],
                    postconditions: [controlledPostcondition]));
            RequireOk(
                controlledMutation,
                "controlled artboard-name mutation");

            var artboardsAfterControlled = Execute(
                runtime,
                Request(
                    "live-artboards-read-controlled",
                    target,
                    "illustrator.artboard.read",
                    selectorInput));
            RequireOk(
                artboardsAfterControlled,
                "post-mutation artboard read");
            var observedControlledName =
                RequireValue(artboardsAfterControlled)
                    .GetProperty("artboards")[0]
                    .GetProperty("name")
                    .GetString();
            if (!string.Equals(
                    observedControlledName,
                    controlledName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Controlled artboard-name mutation was not observed by the structure read.");
            }

            steps.Add(new
            {
                step = "controlled-mutation-with-conditions",
                ok = true,
                artboardName = controlledName
            });

            var ambiguousName =
                "COMTOOL_V2_RECONCILE_" +
                Guid.NewGuid().ToString("N")[..12];
            var ambiguousRequestId =
                "live-ambiguous-artboard-name";
            var ambiguousPostcondition = EqualsCondition(
                "ambiguous-artboard-name-landed",
                "com.get",
                JsonElementOf(
                    new { path = "ActiveDocument.Artboards[0].Name" }),
                ProtocolValue.FromString(ambiguousName));
            var ambiguousSource =
                "app.activeDocument.artboards[0].name=" +
                JsonSerializer.Serialize(ambiguousName) +
                ";var __ct_t=(new Date()).getTime();" +
                "while(((new Date()).getTime()-__ct_t)<2000){};";

            var ambiguous = Execute(
                runtime,
                Request(
                    ambiguousRequestId,
                    target,
                    "script.eval",
                    JsonElementOf(new
                    {
                        kind = "code",
                        source = ambiguousSource,
                        effects = "idempotent_write"
                    }),
                    policy: new OperationPolicy(
                        WorkerWatchdogMs: 500,
                        LeaseId: leaseId),
                    preconditions: [activeDocumentCondition],
                    postconditions: [ambiguousPostcondition]));

            if (ambiguous.Ok ||
                ambiguous.TargetState !=
                    TargetState.ReconciliationRequired)
            {
                throw new InvalidOperationException(
                    "Deliberate watchdog mutation did not enter reconciliation_required.");
            }

            Thread.Sleep(2500);

            var stateProbe = Execute(
                runtime,
                Request(
                    "live-state-after-ambiguity",
                    target,
                    "core.target.capabilities",
                    JsonElementOf(new { }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId)));
            RequireOk(stateProbe, "target state after ambiguity");
            var runtimeState = RequireValue(stateProbe)
                .GetProperty("runtimeState");
            var revision = runtimeState
                .GetProperty("revision")
                .GetInt64();

            var reconcile = Execute(
                runtime,
                Request(
                    "live-mutation-reconcile",
                    target,
                    "core.target.mutation.reconcile",
                    JsonElementOf(new
                    {
                        incidentRequestId = ambiguousRequestId,
                        expectedRevision = revision
                    }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId)));
            RequireOk(
                reconcile,
                "condition-bound mutation reconciliation");
            if (reconcile.TargetState != TargetState.Known)
            {
                throw new InvalidOperationException(
                    $"Mutation reconciliation completed with target state '{reconcile.TargetState}'.");
            }

            var artboardsAfterReconcile = Execute(
                runtime,
                Request(
                    "live-artboards-read-reconciled",
                    target,
                    "illustrator.artboard.read",
                    selectorInput));
            RequireOk(
                artboardsAfterReconcile,
                "reconciled artboard read");
            var observedReconciledName =
                RequireValue(artboardsAfterReconcile)
                    .GetProperty("artboards")[0]
                    .GetProperty("name")
                    .GetString();
            if (!string.Equals(
                    observedReconciledName,
                    ambiguousName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Reconciled mutation postcondition was not observed on the disposable document.");
            }

            steps.Add(new
            {
                step = "ambiguous-mutation-condition-reconciliation",
                ok = true,
                incidentRequestId = ambiguousRequestId,
                revision,
                artboardName = ambiguousName,
                ambiguousErrorKind = ambiguous.Error?.Kind
            });

            var close = Execute(
                runtime,
                Request(
                    "live-document-close",
                    target,
                    "illustrator.document.close",
                    JsonElementOf(new
                    {
                        document = new
                        {
                            name = createdDocumentName
                        },
                        closePolicy = "discard"
                    }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId),
                    preconditions: [activeDocumentCondition]));
            RequireOk(close, "disposable document close");
            createdDocumentName = null;

            steps.Add(new
            {
                step = "disposable-document-close",
                ok = true,
                closePolicy = "discard"
            });

            var release = Execute(
                runtime,
                Request(
                    "live-lease-release",
                    target,
                    "core.target.lease.release",
                    JsonElementOf(new { }),
                    policy: new OperationPolicy(
                        LeaseId: leaseId)));
            RequireOk(release, "lease release");
            leaseId = null;

            if (!OwnedProcessStillMatches(ownedProcess))
            {
                throw new InvalidOperationException(
                    "Owned Illustrator process identity changed before restart validation.");
            }

            var documentsBeforeRestart =
                Convert.ToInt32(app.Documents.Count);
            if (documentsBeforeRestart != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to restart owned Illustrator because {documentsBeforeRestart} document(s) remain open.");
            }

            var firstOwnedProcess = ownedProcess;
            var firstTargetId = target.Id
                ?? throw new InvalidOperationException(
                    "Initial target id disappeared before restart validation.");

            app.Quit();
            WaitForProcessExit(
                firstOwnedProcess.ProcessId,
                TimeSpan.FromSeconds(15));
            ReleaseCom(ownerApp);
            ownerApp = null;

            steps.Add(new
            {
                step = "owned-host-first-generation-exit",
                ok = true,
                processId = firstOwnedProcess.ProcessId,
                targetId = firstTargetId
            });

            ownerApp = LaunchOwnedIllustrator();
            app = ownerApp;
            var restartedVersion =
                Convert.ToString(app.Version) ?? string.Empty;
            var restartedDocuments =
                Convert.ToInt32(app.Documents.Count);
            if (restartedDocuments != 0)
            {
                throw new InvalidOperationException(
                    $"Relaunched owned Illustrator unexpectedly contained {restartedDocuments} document(s).");
            }

            var secondOwnedProcess = WaitForSingleIllustratorProcess(
                TimeSpan.FromSeconds(30));
            ownedProcess = secondOwnedProcess;

            var refreshedTargets = Execute(
                runtime,
                Request(
                    "live-targets-after-host-restart",
                    target: null,
                    "core.targets.list",
                    JsonElementOf(new { refresh = true })));
            RequireOk(
                refreshedTargets,
                "target refresh after Illustrator restart");

            var refreshedValue = RequireValue(refreshedTargets);
            if (refreshedValue.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Target refresh after Illustrator restart did not return an array.");
            }

            JsonElement? oldTargetEntry = null;
            JsonElement? newTargetEntry = null;
            foreach (var entry in refreshedValue.EnumerateArray())
            {
                var entryTarget = entry.GetProperty("target");
                var entryId = entryTarget.GetProperty("id").GetString();
                if (string.Equals(
                        entryId,
                        firstTargetId,
                        StringComparison.Ordinal))
                {
                    oldTargetEntry = entry.Clone();
                }

                var entryIdentity = entry.GetProperty("identity");
                if (entry.GetProperty("running").GetBoolean() &&
                    entryIdentity.GetProperty("processId").GetInt32() ==
                        secondOwnedProcess.ProcessId)
                {
                    newTargetEntry = entry.Clone();
                }
            }

            if (oldTargetEntry is null ||
                oldTargetEntry.Value.GetProperty("running").GetBoolean())
            {
                throw new InvalidOperationException(
                    "The first Illustrator target generation was not retained as stale after restart.");
            }

            if (newTargetEntry is null)
            {
                throw new InvalidOperationException(
                    "Runtime did not discover the relaunched Illustrator process generation.");
            }

            var newTargetElement =
                newTargetEntry.Value.GetProperty("target");
            var newTargetId =
                newTargetElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException(
                    "Relaunched Illustrator target id is missing.");
            if (string.Equals(
                    newTargetId,
                    firstTargetId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Illustrator restart reused the previous strong target identity.");
            }

            var restartedTarget = new TargetRef(
                newTargetElement.GetProperty("host").GetString()
                    ?? throw new InvalidOperationException(
                        "Relaunched target host is missing."),
                newTargetId,
                newTargetElement.TryGetProperty(
                        "generation",
                        out var restartedGeneration) &&
                    restartedGeneration.ValueKind == JsonValueKind.Number
                    ? restartedGeneration.GetInt64()
                    : null);

            var restartedStatus = Execute(
                runtime,
                Request(
                    "live-status-after-host-restart",
                    restartedTarget,
                    "core.target.status",
                    JsonElementOf(new { })));
            RequireOk(
                restartedStatus,
                "status against relaunched Illustrator generation");

            steps.Add(new
            {
                step = "owned-host-restart-reconnect",
                ok = true,
                version = restartedVersion,
                previousProcessId = firstOwnedProcess.ProcessId,
                newProcessId = secondOwnedProcess.ProcessId,
                previousTargetId = firstTargetId,
                newTargetId,
                oldTargetRunning = false,
                newTargetRunning = true
            });

            runtime.DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            runtime = null;

            if (!OwnedProcessStillMatches(secondOwnedProcess))
            {
                throw new InvalidOperationException(
                    "Relaunched owned Illustrator process identity changed before final shutdown.");
            }

            var documentsBeforeQuit =
                Convert.ToInt32(app.Documents.Count);
            if (documentsBeforeQuit != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to quit relaunched owned Illustrator because {documentsBeforeQuit} document(s) remain open.");
            }

            app.Quit();
            WaitForProcessExit(
                secondOwnedProcess.ProcessId,
                TimeSpan.FromSeconds(15));

            steps.Add(new
            {
                step = "owned-host-clean-shutdown",
                ok = true,
                processId = secondOwnedProcess.ProcessId
            });

            Write(new
            {
                ok = true,
                host = "illustrator",
                version,
                ownedProcess,
                stateDirectory,
                steps
            });
            return 0;
        }
        catch (Exception ex)
        {
            if (runtime is not null)
            {
                try
                {
                    if (target is not null &&
                        !string.IsNullOrWhiteSpace(leaseId))
                    {
                        _ = Execute(
                            runtime,
                            Request(
                                "live-cleanup-lease-release",
                                target,
                                "core.target.lease.release",
                                JsonElementOf(new { }),
                                policy: new OperationPolicy(
                                    LeaseId: leaseId)));
                    }
                }
                catch
                {
                }

                try
                {
                    runtime.DisposeAsync()
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                }
            }

            cleanupWarning = TryCleanupOwnedHost(
                ownerApp,
                ownedProcess,
                createdDocumentName);

            Write(new
            {
                ok = false,
                error = new
                {
                    kind = ex.GetType().Name,
                    ex.Message,
                    hResult = ex.HResult,
                    hResultHex =
                        $"0x{unchecked((uint)ex.HResult):X8}"
                },
                stateDirectory,
                cleanupWarning,
                steps
            });
            return 1;
        }
        finally
        {
            ReleaseCom(ownerApp);
        }
    }

    private static object LaunchOwnedIllustrator()
    {
        var type = Type.GetTypeFromProgID(
            IllustratorProgId,
            throwOnError: true)
            ?? throw new InvalidOperationException(
                $"Could not resolve COM ProgID '{IllustratorProgId}'.");

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException(
                "COM activation returned null.");
    }

    private static OwnedProcessIdentity WaitForSingleIllustratorProcess(
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var processes = GetIllustratorProcesses();
            try
            {
                if (processes.Count == 1)
                {
                    var process = processes[0];
                    process.Refresh();
                    return new OwnedProcessIdentity(
                        process.Id,
                        new DateTimeOffset(process.StartTime));
                }

                if (processes.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"COM activation produced {processes.Count} Illustrator processes; ownership is ambiguous.");
                }
            }
            finally
            {
                DisposeAll(processes);
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException(
            "Illustrator process did not appear after COM activation.");
    }

    private static List<Process> GetIllustratorProcesses() =>
        Process.GetProcessesByName("Illustrator").ToList();

    private static bool OwnedProcessStillMatches(
        OwnedProcessIdentity identity)
    {
        try
        {
            using var process =
                Process.GetProcessById(identity.ProcessId);
            process.Refresh();
            return new DateTimeOffset(process.StartTime) ==
                   identity.ProcessStartedAt;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForProcessExit(
        int processId,
        TimeSpan timeout)
    {
        try
        {
            using var process =
                Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                throw new TimeoutException(
                    "Owned Illustrator did not exit after Application.Quit().");
            }
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
    }

    private static string? TryCleanupOwnedHost(
        object? ownerApp,
        OwnedProcessIdentity? identity,
        string? createdDocumentName)
    {
        if (ownerApp is null || identity is null)
            return null;

        if (!OwnedProcessStillMatches(identity))
        {
            return
                "Owned Illustrator process identity changed; cleanup was intentionally skipped.";
        }

        try
        {
            dynamic app = ownerApp;

            if (!string.IsNullOrWhiteSpace(createdDocumentName))
            {
                var count = Convert.ToInt32(app.Documents.Count);
                for (var index = count; index >= 1; index--)
                {
                    dynamic document = app.Documents[index];
                    var name =
                        Convert.ToString(document.Name)
                        ?? string.Empty;
                    if (string.Equals(
                            name,
                            createdDocumentName,
                            StringComparison.Ordinal))
                    {
                        document.Close(2);
                        break;
                    }
                }
            }

            var remaining =
                Convert.ToInt32(app.Documents.Count);
            if (remaining != 0)
            {
                return
                    $"Cleanup left {remaining} document(s) open; Illustrator was intentionally not quit.";
            }

            app.Quit();
            WaitForProcessExit(
                identity.ProcessId,
                TimeSpan.FromSeconds(15));
            return null;
        }
        catch (Exception ex)
        {
            return
                $"Owned-host cleanup failed and was not forced: {ex.Message}";
        }
    }

    private static OperationResult Execute(
        RuntimeSupervisor runtime,
        OperationRequest request) =>
        runtime.ExecuteAsync(request)
            .GetAwaiter()
            .GetResult();

    private static OperationRequest Request(
        string id,
        TargetRef? target,
        string operation,
        JsonElement input,
        OperationPolicy? policy = null,
        IReadOnlyList<OperationCondition>? preconditions = null,
        IReadOnlyList<OperationCondition>? postconditions = null) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Target = target,
            Operation = operation,
            Input = input,
            Policy = policy,
            Preconditions = preconditions,
            Postconditions = postconditions
        };

    private static OperationCondition EqualsCondition(
        string id,
        string operation,
        JsonElement input,
        ProtocolValue expected) =>
        new()
        {
            Id = id,
            Source = new OperationConditionSource
            {
                Operation = operation,
                Input = input
            },
            Predicate = new OperationConditionPredicate
            {
                Kind = "equals",
                Expected = expected
            }
        };

    private static JsonElement JsonElementOf<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Json);

    private static JsonElement RequireValue(
        OperationResult result) =>
        result.Result?.Value
        ?? throw new InvalidOperationException(
            $"Operation '{result.Operation}' returned no value.");

    private static void RequireOk(
        OperationResult result,
        string context)
    {
        if (result.Ok)
            return;

        throw new InvalidOperationException(
            $"{context} failed: {result.Error?.Kind ?? result.Status.ToString()}: {result.Error?.Message ?? "no message"}");
    }

    private static void RequireBoolean(
        JsonElement value,
        string property,
        bool expected,
        string context)
    {
        if (!value.TryGetProperty(property, out var observed) ||
            observed.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False) ||
            observed.GetBoolean() != expected)
        {
            throw new InvalidOperationException(
                $"{context} did not report {property}={expected.ToString().ToLowerInvariant()}.");
        }
    }

    private static string RequireOption(
        string[] args,
        string name) =>
        Option(args, name)
        ?? throw new ArgumentException(
            $"{name} is required.");

    private static string? Option(
        string[] args,
        string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(
                    args[index],
                    name,
                    StringComparison.Ordinal))
                continue;

            if (index + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException(
                    $"{name} requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }

    private static void DisposeAll(
        IEnumerable<Process> processes)
    {
        foreach (var process in processes)
            process.Dispose();
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }

    private static void Write(object value) =>
        Console.Out.WriteLine(
            JsonSerializer.Serialize(value, Json));

    private sealed record OwnedProcessIdentity(
        int ProcessId,
        DateTimeOffset ProcessStartedAt);
}