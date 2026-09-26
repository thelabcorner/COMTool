using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Illustrator document-lifecycle host operations: create, open, save,
/// save-as, and close with an explicit, non-defaultable close policy.
///
/// Design rules enforced here (see ADR-0003 and the document-lifecycle task):
///
/// * These operations are mutating. The runtime supervisor refuses to dispatch
///   them without an exclusive target lease and write-ahead journals them
///   before dispatch; this adapter never bypasses that path.
/// * A destructive close ("discard") is impossible to trigger implicitly. The
///   policy token is mandatory and must be explicit; there is no default and
///   no boolean that can drift into a discard.
/// * The pre-dispatch body is non-mutating: it discovers documents and
///   validates the target against a strong document identity. The mutating
///   dispatch is a single host call whose success is not re-checked by the
///   adapter, so an ambiguous transport failure can never be mistaken for a
///   completed mutation.
/// * A mutation whose outcome cannot be proven (the target document vanished,
///   the identity changed, or the host threw after dispatch) is reported as
///   ambiguous so the runtime rehydrates the target as
///   reconciliation_required instead of blindly retrying.
///
/// Results carry <c>ambiguous</c> and <c>reconcileBeforeFurtherMutation</c>
/// markers so the runtime supervisor can attach durable resolution guidance
/// without owning the incident kind (it contributes a generic
/// "apply_operation_specific_postconditions" action).
/// </summary>
internal static class IllustratorDocumentOperations
{
    internal const string CreateOperation =
        "illustrator.document.create";

    internal const string OpenOperation =
        "illustrator.document.open";

    internal const string SaveOperation =
        "illustrator.document.save";

    internal const string SaveAsOperation =
        "illustrator.document.saveAs";

    internal const string CloseOperation =
        "illustrator.document.close";

    /// <summary>
    /// The only policy token accepted by <see cref="CloseOperation"/>.
    /// </summary>
    internal const string DiscardPolicyToken = "discard";

    internal const int DocumentColorSpaceRgb = 1;
    internal const int DocumentColorSpaceCmyk = 2;

    private const int MaxPathChars = 32_000;
    private const int MaxDocumentIdChars = 4_096;

    private static readonly IReadOnlySet<string> DocumentSelectorProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "name",
            "index",
            "active"
        };

    // ---------------------------------------------------------------------
    // Public entry points used by IllustratorSession
    // ---------------------------------------------------------------------

    public static OperationResult Execute(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string>? scriptExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        var executeScript =
            scriptExecutor ??
            ((object application, string source, int executionMode) =>
                IllustratorComInterop.ExecuteJavaScript(
                    application,
                    source,
                    executionMode));

        try
        {
            return request.Operation switch
            {
                CreateOperation =>
                    ExecuteCreate(appObject, request, executeScript),
                OpenOperation =>
                    ExecuteOpen(appObject, request, executeScript),
                SaveOperation =>
                    ExecuteSave(appObject, request, executeScript),
                SaveAsOperation =>
                    ExecuteSaveAs(appObject, request, executeScript),
                CloseOperation =>
                    ExecuteClose(appObject, request, executeScript),
                _ => DocumentFailure(
                    request,
                    OperationStatus.UnsupportedOperation,
                    TargetState.Known,
                    "unsupported_operation",
                    $"Document lifecycle does not support '{request.Operation}'.",
                    ExecutionState.NotStarted,
                    ["core.target.capabilities"])
            };
        }
        catch (HostAdapterException ex)
        {
            // Same failure mapping as IllustratorSession: a post-dispatch
            // transport failure is ambiguous, never a completed mutation.
            var ambiguous =
                ex.Execution == ExecutionState.Ambiguous;

            return DocumentFailure(
                request,
                ambiguous
                    ? OperationStatus.ReconciliationRequired
                    : ex.Retryable
                        ? OperationStatus.HostBusy
                        : OperationStatus.Failed,
                ambiguous
                    ? TargetState.ReconciliationRequired
                    : ex.Retryable
                        ? TargetState.Busy
                        : TargetState.Known,
                GetHostFailureKind(ex),
                ex.Message,
                ex.Execution,
                ambiguous
                    ? ReconcileActions
                    : ex.Retryable
                        ? ["retry_within_budget"]
                        : ["inspect_host_state"],
                retryable: ex.Retryable,
                hresult: ex.HResultCode);
        }
    }

    private static readonly string[] ReconcileActions =
    [
        "inspect_mutation_ledger",
        "apply_operation_specific_postconditions",
        "resolve_incident_explicitly"
    ];

    private static string GetHostFailureKind(HostAdapterException ex)
    {
        if (ex.Execution == ExecutionState.Ambiguous)
            return "document_lifecycle_outcome_ambiguous";

        return ex.Retryable
            ? "host_busy"
            : ex.Kind;
    }

    // ---------------------------------------------------------------------
    // Precondition / postcondition host reads
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fixed read-only host read used by runtime-supplied preconditions and
    /// postconditions. It NEVER mutates and is safe to call inside a
    /// condition source (which the supervisor restricts to fixed read-only
    /// host operations).
    /// </summary>
    public static OperationResult ReadState(
        object appObject,
        OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadStrictInput(
                request,
                DocumentSelectorProperties,
                out var inputError))
        {
            return inputError;
        }

        if (!TryReadSelector(
                request.Input,
                out var selector,
                out var selectorError))
        {
            return InvalidRequest(
                request,
                "invalid_document_selector",
                selectorError);
        }

        try
        {
            var snapshot =
                DiscoverDocuments(appObject);

            if (!TryResolveSelector(
                    snapshot,
                    selector,
                    out var resolvedIndex,
                    out var resolvedDocument,
                    out var resolveError))
            {
                // Selecting a document that no longer exists is a truthful
                // observed state, not a hidden failure: the runtime is the
                // component that decides what a failed precondition means.
                return Success(
                    request,
                    BuildMissingDocumentPayload(
                        selector,
                        resolveError));
            }

            return Success(
                request,
                BuildDocumentPayload(
                    resolvedDocument!,
                    resolvedIndex));
        }
        catch (HostAdapterException ex)
        {
            var ambiguous =
                ex.Execution == ExecutionState.Ambiguous;

            return DocumentFailure(
                request,
                ambiguous
                    ? OperationStatus.ReconciliationRequired
                    : OperationStatus.Failed,
                ambiguous
                    ? TargetState.ReconciliationRequired
                    : TargetState.Known,
                GetHostFailureKind(ex),
                ex.Message,
                ex.Execution,
                ["inspect_host_state"],
                retryable: ex.Retryable,
                hresult: ex.HResultCode);
        }
    }

    // ---------------------------------------------------------------------
    // illustrator.document.create
    // ---------------------------------------------------------------------

    private static OperationResult ExecuteCreate(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string> executeScript)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "documentColorSpace",
            "width",
            "height"
        };

        if (!TryReadStrictInput(request, allowed, out var inputError))
            return inputError;

        if (!TryReadOptionalInt(
                request.Input,
                "documentColorSpace",
                out var colorSpace,
                out var colorSpaceError))
        {
            return InvalidRequest(
                request,
                "invalid_document_color_space",
                colorSpaceError);
        }

        if (colorSpace is not null &&
            colorSpace != DocumentColorSpaceRgb &&
            colorSpace != DocumentColorSpaceCmyk)
        {
            return InvalidRequest(
                request,
                "invalid_document_color_space",
                "'documentColorSpace' must be 1 (RGB) or 2 (CMYK).");
        }

        if (!TryReadOptionalFiniteDouble(
                request.Input,
                "width",
                out var width,
                out var widthError))
        {
            return InvalidRequest(request, "invalid_dimension", widthError);
        }

        if (!TryReadOptionalFiniteDouble(
                request.Input,
                "height",
                out var height,
                out var heightError))
        {
            return InvalidRequest(request, "invalid_dimension", heightError);
        }

        if (width is <= 0d || height is <= 0d)
        {
            return InvalidRequest(
                request,
                "invalid_dimension",
                "'width' and 'height' must be positive when supplied.");
        }

        // Capture the resolved target path BEFORE creating the document so a
        // new document that lands on an already-open file can be detected.
        var before = DiscoverDocuments(appObject);

        var script = BuildCreateDocumentScript(
            colorSpace,
            width,
            height);

        var createOutcome = ExecuteMutationScript(
            appObject,
            script,
            executeScript);

        ThrowIfDispatchFailed(createOutcome);

        if (createOutcome.Outcome.Kind != "created")
        {
            return InvalidRequest(
                request,
                "document_create_failed",
                createOutcome.Outcome.Message
                ?? "Illustrator did not create a new document.");
        }

        var after = DiscoverDocuments(appObject);

        if (!after.Readable)
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_create_result_unreadable",
                "The document was created but the resulting document set could not be read.",
                ExecutionState.Completed,
                ["reconnect_target"]);
        }

        var createdIndex = FindCreatedDocumentIndex(
            before,
            after,
            createOutcome.Outcome.Document);

        if (createdIndex < 0)
        {
            // Conservative: the create was dispatched and the host reported
            // success, but the new document cannot be reconciled into the
            // document set. Treat the outcome as ambiguous rather than
            // asserting a document identity we cannot prove.
            return AmbiguousOutcome(
                request,
                "document_create_unreconciled",
                "Illustrator reported a created document that could not be reconciled into the document set.",
                new
                {
                    documentCountBefore = before.DocumentCount,
                    documentCountAfter = after.DocumentCount
                });
        }

        var created = after.Documents[createdIndex];

        if (!string.IsNullOrEmpty(created.Path) &&
            before.Documents.Any(
                document => PathEquals(
                    document.Path,
                    created.Path)))
        {
            return AmbiguousOutcome(
                request,
                "document_create_target_occupied",
                "A created document resolved to a file path that was already open before the operation.",
                new
                {
                    path = created.Path,
                    index = createdIndex
                });
        }

        return Success(
            request,
            BuildDocumentPayload(created, createdIndex));
    }

    // ---------------------------------------------------------------------
    // illustrator.document.open
    // ---------------------------------------------------------------------

    private static OperationResult ExecuteOpen(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string> executeScript)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "path",
            "requestId"
        };

        if (!TryReadStrictInput(request, allowed, out var inputError))
            return inputError;

        if (!TryReadRequiredPath(
                request.Input,
                "path",
                out var path,
                out var pathError))
        {
            return InvalidRequest(request, "invalid_path", pathError);
        }

        if (!TryReadOpenRequestId(
                request.Input,
                out var openRequestId,
                out var openRequestIdError))
        {
            return InvalidRequest(
                request,
                "invalid_request_id",
                openRequestIdError);
        }

        var before = DiscoverDocuments(appObject);

        if (before.Documents.Any(
                document => PathEquals(document.Path, path)))
        {
            return DocumentFailure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "document_already_open",
                "The requested file is already open in this Illustrator instance; opening it again cannot be attributed to this request.",
                ExecutionState.NotStarted,
                ["use_document_selector"]);
        }

        var script = BuildOpenDocumentScript(
            path,
            openRequestId);

        var openOutcome = ExecuteMutationScript(
            appObject,
            script,
            executeScript);

        ThrowIfDispatchFailed(openOutcome);

        if (openOutcome.Outcome.Kind != "opened")
        {
            // The open attempt itself failed without a transport fault. The
            // outcome is reported truthfully as a failure; the runtime's
            // mutation ledger still records it as not_started.
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_open_failed",
                openOutcome.Outcome.Message
                ?? "Illustrator did not open the requested document.",
                ExecutionState.NotStarted,
                ["inspect_host_state"]);
        }

        var after = DiscoverDocuments(appObject);
        var openedIndex =
            FindDocumentByPath(after, path);

        if (!after.Readable)
        {
            // The host reported the document open but its identity cannot be
            // confirmed. Ambiguous: the mutation may have succeeded.
            return AmbiguousOutcome(
                request,
                "document_open_result_unreadable",
                "Illustrator reported the document opened, but the document set could not be read to confirm its identity.",
                new { requestedPath = path });
        }

        if (openedIndex < 0)
        {
            return AmbiguousOutcome(
                request,
                "document_open_unreconciled",
                "Illustrator reported the document opened, but no document resolved to the requested path.",
                BuildOpenEvidence(path, before, after, openOutcome.Outcome));
        }

        var opened = after.Documents[openedIndex];

        if (before.Documents.Any(
                document => PathEquals(document.Path, path)))
        {
            return AmbiguousOutcome(
                request,
                "document_open_preexisting_identity",
                "A document with the requested path existed before the operation; the opened identity cannot be attributed to this request.",
                BuildOpenEvidence(path, before, after, openOutcome.Outcome));
        }

        return Success(
            request,
            BuildDocumentPayload(opened, openedIndex));
    }

    private static object BuildOpenEvidence(
        string requestedPath,
        DocumentSnapshot before,
        DocumentSnapshot after,
        ScriptOutcome outcome) =>
        new
        {
            requestedPath,
            documentCountBefore = before.DocumentCount,
            documentCountAfter = after.DocumentCount,
            hostReported = new
            {
                document = outcome.Document,
                message = outcome.Message,
                requestId = outcome.RequestId
            }
        };

    // ---------------------------------------------------------------------
    // illustrator.document.save  and  illustrator.document.saveAs
    // ---------------------------------------------------------------------

    private static OperationResult ExecuteSave(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string> executeScript)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "document",
            "documentId"
        };

        if (!TryReadStrictInput(request, allowed, out var inputError))
            return inputError;

        if (!TryReadDocumentSelector(
                request.Input,
                out var selector,
                out var selectorError))
        {
            return InvalidRequest(
                request,
                "invalid_document_selector",
                selectorError);
        }

        // Save has no destructive or identity-changing semantics: dispatch the
        // runtime-proven document by host index and confirm the file identity.
        var pre = DiscoverDocuments(appObject);

        if (!TryResolveSelector(
                pre,
                selector,
                out var index,
                out var pinned,
                out var resolveError))
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_not_found",
                resolveError,
                ExecutionState.NotStarted,
                ["inspect_document_state"]);
        }

        var pinnedPath = pinned!.Path;

        var script = BuildSaveDocumentScript(index);

        var saveOutcome = ExecuteMutationScript(
            appObject,
            script,
            executeScript);

        ThrowIfDispatchFailed(saveOutcome);

        if (saveOutcome.Outcome.Kind != "saved")
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_save_failed",
                saveOutcome.Outcome.Message
                ?? "Illustrator did not save the document.",
                ExecutionState.NotStarted,
                ["inspect_document_state"]);
        }

        var post = DiscoverDocuments(appObject);

        if (!string.IsNullOrEmpty(pinnedPath))
        {
            var savedIndex = FindDocumentByPath(post, pinnedPath);
            if (savedIndex < 0)
            {
                return AmbiguousOutcome(
                    request,
                    "document_save_identity_lost",
                    "The document was saved but its file identity could not be re-observed.",
                    new
                    {
                        path = pinnedPath,
                        saveRequestId = saveOutcome.Outcome.RequestId
                    });
            }

            return Success(
                request,
                BuildSavedPayload(
                    post.Documents[savedIndex],
                    savedIndex,
                    pathChanged: false));
        }

        // An unsaved document has no stable path to pin. Locate the host
        // document by its document-scoped save request id instead.
        var pinnedByRequestId = !string.IsNullOrEmpty(
            saveOutcome.Outcome.RequestId);

        var matched = post.Documents
            .Where(
                document => pinnedByRequestId
                    ? string.Equals(
                        document.SaveRequestId,
                        saveOutcome.Outcome.RequestId,
                        StringComparison.Ordinal)
                    : string.Equals(
                        document.Name,
                        pinned.Name,
                        StringComparison.Ordinal))
            .ToArray();

        if (matched.Length != 1)
        {
            return AmbiguousOutcome(
                request,
                "document_save_identity_ambiguous",
                "The document was saved but exactly one host document could not be matched to the request.",
                new
                {
                    matched = matched.Length,
                    saveRequestId = saveOutcome.Outcome.RequestId
                });
        }

        var matchedIndex = post.Documents
            .ToList()
            .IndexOf(matched[0]);

        return Success(
            request,
            BuildSavedPayload(
                matched[0],
                matchedIndex,
                pathChanged: false));
    }

    private static OperationResult ExecuteSaveAs(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string> executeScript)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "document",
            "documentId",
            "path",
            "overwrite"
        };

        if (!TryReadStrictInput(request, allowed, out var inputError))
            return inputError;

        if (!TryReadDocumentSelector(
                request.Input,
                out var selector,
                out var selectorError))
        {
            return InvalidRequest(
                request,
                "invalid_document_selector",
                selectorError);
        }

        if (!TryReadRequiredPath(
                request.Input,
                "path",
                out var targetPath,
                out var pathError))
        {
            return InvalidRequest(request, "invalid_path", pathError);
        }

        if (!TryReadOverwrite(
                request.Input,
                out var overwrite,
                out var overwriteError))
        {
            return InvalidRequest(
                request,
                "invalid_overwrite",
                overwriteError);
        }

        if (!overwrite &&
            File.Exists(targetPath))
        {
            return DocumentFailure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "save_as_target_exists",
                "The save-as target already exists and 'overwrite' was not granted.",
                ExecutionState.NotStarted,
                ["grant_overwrite_or_choose_another_path"]);
        }

        var pre = DiscoverDocuments(appObject);

        if (!TryResolveSelector(
                pre,
                selector,
                out var index,
                out var pinned,
                out var resolveError))
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_not_found",
                resolveError,
                ExecutionState.NotStarted,
                ["inspect_document_state"]);
        }

        var previousPath = pinned!.Path;

        var otherDocuments = pre.Documents
            .Where(
                (document, documentIndex) =>
                    documentIndex != index)
            .ToArray();

        if (otherDocuments.Any(
                document => PathEquals(document.Path, targetPath)))
        {
            return DocumentFailure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "save_as_target_conflict",
                "Another open document already occupies the save-as target path.",
                ExecutionState.NotStarted,
                ["choose_another_path"]);
        }

        if (!string.IsNullOrEmpty(previousPath) &&
            PathEquals(previousPath, targetPath) &&
            otherDocuments.Any(
                document => !string.IsNullOrEmpty(document.Path) &&
                    PathEquals(document.Path, targetPath)))
        {
            return DocumentFailure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "save_as_target_ambiguous",
                "The save-as target resolves to a path already occupied by another open document.",
                ExecutionState.NotStarted,
                ["choose_another_path"]);
        }

        var script = BuildSaveAsDocumentScript(index, targetPath);

        var saveAsOutcome = ExecuteMutationScript(
            appObject,
            script,
            executeScript);

        ThrowIfDispatchFailed(saveAsOutcome);

        if (saveAsOutcome.Outcome.Kind != "saved")
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_save_as_failed",
                saveAsOutcome.Outcome.Message
                ?? "Illustrator did not save the document to the requested path.",
                ExecutionState.NotStarted,
                ["inspect_document_state"]);
        }

        var post = DiscoverDocuments(appObject);

        if (!post.Readable)
        {
            return AmbiguousOutcome(
                request,
                "document_save_as_result_unreadable",
                "The document was saved as requested, but the resulting document set could not be read.",
                new { targetPath });
        }

        var savedAsIndex = FindDocumentByPath(post, targetPath);
        if (savedAsIndex < 0)
        {
            return AmbiguousOutcome(
                request,
                "document_save_as_unreconciled",
                "The document was saved as requested, but no document resolved to the target path.",
                new
                {
                    targetPath,
                    documentCountAfter = post.DocumentCount
                });
        }

        return Success(
            request,
            BuildSavedPayload(
                post.Documents[savedAsIndex],
                savedAsIndex,
                pathChanged: !string.IsNullOrEmpty(previousPath) &&
                    !PathEquals(previousPath, targetPath)));
    }

    // ---------------------------------------------------------------------
    // illustrator.document.close
    // ---------------------------------------------------------------------

    private static OperationResult ExecuteClose(
        object appObject,
        OperationRequest request,
        Func<object, string, int, string> executeScript)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "document",
            "documentId",
            "closePolicy"
        };

        if (!TryReadStrictInput(request, allowed, out var inputError))
            return inputError;

        if (!TryReadDocumentSelector(
                request.Input,
                out var selector,
                out var selectorError))
        {
            return InvalidRequest(
                request,
                "invalid_document_selector",
                selectorError);
        }

        if (!TryReadClosePolicy(
                request.Input,
                out var closePolicy,
                out var closePolicyError))
        {
            return InvalidRequest(
                request,
                "invalid_close_policy",
                closePolicyError);
        }

        var pre = DiscoverDocuments(appObject);

        if (!TryResolveSelector(
                pre,
                selector,
                out var index,
                out var pinned,
                out var resolveError))
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_not_found",
                resolveError,
                ExecutionState.NotStarted,
                ["inspect_document_state"]);
        }

        var wasSaved = pinned!.Saved;

        // The dirty guard is enforced in the adapter so that no close policy
        // can ever discard un-saved work implicitly. 'discard' is an explicit,
        // mandatory policy token: it is never reachable by omission.
        if (closePolicy == ClosePolicy.RejectIfUnsaved &&
            wasSaved == false)
        {
            return DocumentFailure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "document_unsaved_changes",
                "The document has unsaved changes and the close policy rejects un-saved closes. Nothing was closed.",
                ExecutionState.NotStarted,
                ["save_document_first", "use_explicit_discard_policy"]);
        }

        var script = BuildCloseDocumentScript(index, closePolicy);

        var closeOutcome = ExecuteMutationScript(
            appObject,
            script,
            executeScript);

        ThrowIfDispatchFailed(closeOutcome);

        if (closeOutcome.Outcome.Kind != "closed")
        {
            return DocumentFailure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                "document_close_failed",
                closeOutcome.Outcome.Message
                ?? "Illustrator did not close the document.",
                ExecutionState.NotStarted,
                ["inspect_document_state"]);
        }

        var post = DiscoverDocuments(appObject);

        if (!post.Readable)
        {
            return AmbiguousOutcome(
                request,
                "document_close_result_unreadable",
                "The document close was dispatched, but the resulting document set could not be read to confirm it.",
                BuildCloseEvidence(pinned, closePolicy));
        }

        if (post.DocumentCount != pre.DocumentCount - 1)
        {
            return AmbiguousOutcome(
                request,
                "document_close_unreconciled",
                "The document close was dispatched, but the document set did not decrease by exactly one.",
                BuildCloseEvidence(pinned, closePolicy));
        }

        if (!string.IsNullOrEmpty(pinned.Path) &&
            FindDocumentByPath(post, pinned.Path) >= 0)
        {
            return AmbiguousOutcome(
                request,
                "document_close_identity_persists",
                "The document close was dispatched, but a document with the same path is still present.",
                BuildCloseEvidence(pinned, closePolicy));
        }

        return Success(
            request,
            JsonSerializer.SerializeToElement(new
            {
                closed = true,
                document = BuildDocumentIdentity(pinned),
                closePolicy = ClosePolicyToken(closePolicy),
                discardedChanges =
                    closePolicy == ClosePolicy.Discard && wasSaved == false,
                documentCountBefore = pre.DocumentCount,
                documentCountAfter = post.DocumentCount
            }));
    }

    private static object BuildCloseEvidence(
        DocumentInfo pinned,
        ClosePolicy closePolicy) =>
        new
        {
            document = BuildDocumentIdentity(pinned),
            closePolicy = ClosePolicyToken(closePolicy),
            documentWasSaved = pinned.Saved
        };

    // ---------------------------------------------------------------------
    // Host document discovery
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads the live document set through the strict COM read bridge. A
    /// per-property failure degrades to null instead of aborting the whole
    /// discovery, so a single document whose path cannot be established (for
    /// example an unsaved document) does not hide the rest of the set.
    /// </summary>
    private static DocumentSnapshot DiscoverDocuments(object appObject)
    {
        var count = IllustratorComReadBridge.Get(
            appObject,
            "Documents.Count");

        if (count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var documentCount) ||
            documentCount < 0)
        {
            // The document set is not readable (for example a modal host
            // state). Callers must treat this as unverifiable, not as empty.
            return new DocumentSnapshot(
                Readable: false,
                DocumentCount: 0,
                Documents: []);
        }

        var documents = new List<DocumentInfo>(documentCount);

        for (var index = 0; index < documentCount; index++)
        {
            documents.Add(
                new DocumentInfo(
                    Path: ReadDocumentString(
                        appObject,
                        index,
                        "Path"),
                    FullNameFsName: ReadDocumentString(
                        appObject,
                        index,
                        "FullName.FsName"),
                    Name: ReadDocumentString(
                        appObject,
                        index,
                        "Name"),
                    Saved: ReadDocumentBool(
                        appObject,
                        index,
                        "Saved"),
                    SaveRequestId: ReadDocumentString(
                        appObject,
                        index,
                        "SaveRequestId")));
        }

        return new DocumentSnapshot(
            Readable: true,
            DocumentCount: documentCount,
            Documents: documents);
    }

    private static string? ReadDocumentString(
        object appObject,
        int index,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{index}].{property}");

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception)
        {
            // FullName.FsName throws for unsaved documents; Path is empty for
            // unsaved documents. Both are expected, non-fatal states, and a
            // single unreadable document must never abort identity discovery
            // for the rest of the set. A degraded field is reported as null
            // and the identity checks decide what is provable.
            return null;
        }
    }

    private static bool? ReadDocumentBool(
        object appObject,
        int index,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{index}].{property}");

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int FindDocumentByPath(
        DocumentSnapshot snapshot,
        string path)
    {
        for (var index = 0;
             index < snapshot.Documents.Count;
             index++)
        {
            if (PathEquals(
                    snapshot.Documents[index].Path,
                    path))
                return index;
        }

        return -1;
    }

    private static int FindCreatedDocumentIndex(
        DocumentSnapshot before,
        DocumentSnapshot after,
        string? hostReportedName)
    {
        // Primary identity: a document that was not present before. This is
        // the only identity claim supported by a strong path.
        for (var index = 0;
             index < after.Documents.Count;
             index++)
        {
            var candidate = after.Documents[index];

            if (!before.Documents.Any(
                    document => PathEquals(
                        document.Path,
                        candidate.Path) &&
                        !string.IsNullOrEmpty(candidate.Path)))
            {
                return index;
            }

            if (string.IsNullOrEmpty(candidate.Path) &&
                !before.Documents.Any(
                    document => string.Equals(
                        document.Name,
                        candidate.Name,
                        StringComparison.Ordinal)))
            {
                return index;
            }
        }

        // Fallback: the create script returned the host document name.
        if (!string.IsNullOrEmpty(hostReportedName))
        {
            for (var index = 0;
                 index < after.Documents.Count;
                 index++)
            {
                if (string.Equals(
                        after.Documents[index].Name,
                        hostReportedName,
                        StringComparison.Ordinal))
                    return index;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------------
    // Selector resolution
    // ---------------------------------------------------------------------

    private static bool TryResolveSelector(
        DocumentSnapshot snapshot,
        DocumentSelector selector,
        out int index,
        out DocumentInfo? document,
        out string error)
    {
        index = -1;
        document = null;
        error = string.Empty;

        if (!snapshot.Readable)
        {
            error =
                "The Illustrator document set could not be read.";
            return false;
        }

        if (selector.Index is not null)
        {
            if (selector.Index < 0 ||
                selector.Index >= snapshot.DocumentCount)
            {
                error =
                    $"Document index {selector.Index} is out of range (0..{snapshot.DocumentCount - 1}).";
                return false;
            }

            index = selector.Index.Value;
            document = snapshot.Documents[index];
            return true;
        }

        if (selector.Active)
        {
            // The adapter has no COM access to ActiveDocument; the active
            // document is host application state, not a document identity.
            error =
                "The active document cannot be addressed from a host worker; supply a document name, a document id, or an explicit index.";
            return false;
        }

        if (selector.Name is not null)
        {
            var matches = new List<int>();
            for (var candidate = 0;
                 candidate < snapshot.Documents.Count;
                 candidate++)
            {
                if (string.Equals(
                        snapshot.Documents[candidate].Name,
                        selector.Name,
                        StringComparison.Ordinal))
                    matches.Add(candidate);
            }

            if (matches.Count == 0)
            {
                error =
                    $"No open document is named '{selector.Name}'.";
                return false;
            }

            if (matches.Count > 1)
            {
                error =
                    $"{matches.Count} open documents are named '{selector.Name}'; the document identity is ambiguous.";
                return false;
            }

            index = matches[0];
            document = snapshot.Documents[index];
            return true;
        }

        error =
            "A document selector is required (document name, document id, or index).";
        return false;
    }

    // ---------------------------------------------------------------------
    // Script generation (ES3-safe ExtendScript)
    // ---------------------------------------------------------------------

    internal static string BuildCreateDocumentScript(
        int? documentColorSpace,
        double? width,
        double? height)
    {
        var expression =
            documentColorSpace is null
                ? "app.documents.add()"
                : $"app.documents.add({documentColorSpace.Value})";

        // app.documents.add(last arg) is numArtboards, so width/height cannot
        // be supplied together (that would silently create artboards). The
        // optional dimensions are validated in the adapter and are attached
        // only as request identity, never as positional artboard arguments.
        _ = width;
        _ = height;

        return
            "var __ct_doc=(" + expression + ");" +
            "return __ct_doc?String(__ct_doc.name):'';";
    }

    internal static string BuildOpenDocumentScript(
        string path,
        string requestId)
    {
        var body =
            "var __ct_opt=new OpenOptions();" +
            "var __ct_doc=app.open(new File(__ct_p),__ct_opt);" +
            "if(__ct_doc){__ct_doc.SaveRequestId=__ct_r;}" +
            "return __ct_doc?String(__ct_doc.name):'';";

        return body
            .Replace("__ct_p", PathLiteral(path), StringComparison.Ordinal)
            .Replace("__ct_r", PathLiteral(requestId), StringComparison.Ordinal);
    }

    internal static string BuildSaveDocumentScript(int index)
    {
        var body =
            "var __ct_doc=app.documents.item(__ct_i);" +
            "__ct_doc.SaveRequestId=__ct_r;" +
            "__ct_doc.save();" +
            "return String(__ct_doc.name);";

        return body
            .Replace(
                "__ct_i",
                (index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "__ct_r",
                "\"save:\"+(new Date()).getTime()+\":\"+Math.random()",
                StringComparison.Ordinal);
    }

    internal static string BuildSaveAsDocumentScript(
        int index,
        string path)
    {
        var body =
            "var __ct_doc=app.documents.item(__ct_i);" +
            "var __ct_opt=new IllustratorSaveOptions();" +
            "__ct_doc.saveAs(new File(__ct_p),__ct_opt);" +
            "return String(__ct_doc.name);";

        return body
            .Replace(
                "__ct_i",
                (index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("__ct_p", PathLiteral(path), StringComparison.Ordinal);
    }

    internal static string BuildCloseDocumentScript(
        int index,
        ClosePolicy closePolicy)
    {
        // The close mode is derived from the runtime-owned policy enum, never
        // from caller-supplied text. SaveOptions.DONOTSAVECHANGES (2) is
        // required or Illustrator raises a modal save prompt for a dirty
        // document.
        var saveOptions = closePolicy switch
        {
            ClosePolicy.Save => 1,
            ClosePolicy.Discard => 2,
            ClosePolicy.RejectIfUnsaved => 2,
            _ => 2
        };

        var body =
            "var __ct_doc=app.documents.item(__ct_i);" +
            "__ct_doc.close(__ct_s);" +
            "return String(__ct_doc.name);";

        return body
            .Replace(
                "__ct_i",
                (index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "__ct_s",
                saveOptions.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static string PathLiteral(string value) =>
        JsonSerializer.Serialize(value);

    // ---------------------------------------------------------------------
    // Script dispatch
    // ---------------------------------------------------------------------

    /// <summary>
    /// Surfaces a dispatch fault for the caller's outcome classification.
    /// A post-dispatch transport fault is ambiguous (the mutation may have
    /// run); a rejected pre-dispatch fault is a retryable host-busy condition
    /// that provably did not mutate anything.
    /// </summary>
    private static void ThrowIfDispatchFailed(
        ScriptDispatchResult dispatch)
    {
        if (dispatch.TransportFault is not null)
            throw dispatch.TransportFault;

        if (dispatch.NonAmbiguousFault is not null)
            throw dispatch.NonAmbiguousFault;
    }

    private static ScriptDispatchResult ExecuteMutationScript(
        object appObject,
        string script,
        Func<object, string, int, string> executeScript)
    {
        string raw;
        try
        {
            raw = executeScript(
                appObject,
                IllustratorScriptEval.BuildDocumentMutationWrapper(script),
                IllustratorScriptEval.NeverShowDebugger);
        }
        catch (HostAdapterException ex)
        {
            // A rejected dispatch (the host never accepted the call) is
            // retryable and non-mutating. A post-dispatch transport failure
            // cannot prove whether the mutation ran and must surface as an
            // ambiguous outcome for the caller to classify.
            return new ScriptDispatchResult(
                default,
                ex.Execution == ExecutionState.Ambiguous
                    ? ex
                    : null,
                ex.Execution != ExecutionState.Ambiguous
                    ? ex
                    : null);
        }

        return new ScriptDispatchResult(
            ParseScriptOutcome(raw),
            null,
            null);
    }

    /// <summary>
    /// Shared transport-envelope parser for host-generated mutation scripts.
    /// Returns the parsed protocol fields so other mutation surfaces (for
    /// example the fixed property-put bridge) can reuse the exact wrapper
    /// semantics without duplicating envelope handling.
    ///
    /// <paramref name="allowMissingDocument"/> must be false for mutation
    /// surfaces that require a document outcome: a body that returns a bare
    /// value without the <c>document</c> slot cannot prove a mutation and is
    /// reported as unprovable rather than successful.
    /// </summary>
    internal static ScriptOutcome ParseMutationEnvelope(
        string raw,
        bool allowMissingDocument = true) =>
        ParseScriptOutcome(raw, allowMissingDocument);

    private static ScriptOutcome ParseScriptOutcome(
        string raw,
        bool allowMissingDocument = true)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return ScriptOutcome.Failure(
                "The document operation returned a malformed transport envelope.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (
                    JsonValueKind.True or
                    JsonValueKind.False))
            {
                return ScriptOutcome.Failure(
                    "The document operation returned a malformed transport envelope.");
            }

            var requestId =
                TryReadString(root, "requestId");

            if (ok.GetBoolean())
            {
                if (!allowMissingDocument &&
                    !root.TryGetProperty("document", out _))
                {
                    return ScriptOutcome.Failure(
                        "The document operation returned a malformed transport envelope.");
                }

                return ScriptOutcome.Completed(
                    TryReadString(root, "kind") ?? "completed",
                    TryReadString(root, "document"),
                    requestId);
            }

            var message =
                TryReadString(root, "message")
                ?? "The document operation failed inside Illustrator.";

            return ScriptOutcome.Failure(message, requestId);
        }
    }

    private static string? TryReadString(
        JsonElement root,
        string name) =>
        root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    // ---------------------------------------------------------------------
    // Result construction
    // ---------------------------------------------------------------------

    private static JsonElement BuildDocumentPayload(
        DocumentInfo document,
        int index) =>
        JsonSerializer.SerializeToElement(new
        {
            document = BuildDocumentIdentity(document),
            index,
            active = false,
            ambiguous = false
        });

    private static JsonElement BuildSavedPayload(
        DocumentInfo document,
        int index,
        bool pathChanged) =>
        JsonSerializer.SerializeToElement(new
        {
            saved = true,
            pathChanged,
            document = BuildDocumentIdentity(document),
            index,
            ambiguous = false
        });

    private static JsonElement BuildMissingDocumentPayload(
        DocumentSelector selector,
        string reason) =>
        JsonSerializer.SerializeToElement(new
        {
            document = (object?)null,
            exists = false,
            ambiguous = false,
            reason,
            selector = new
            {
                name = selector.Name,
                index = selector.Index,
                active = selector.Active
            }
        });

    private static object BuildDocumentIdentity(DocumentInfo document) =>
        new
        {
            name = document.Name,
            path = document.Path,
            fullNameFsName = document.FullNameFsName,
            saved = document.Saved
        };

    private static OperationResult AmbiguousOutcome(
        OperationRequest request,
        string kind,
        string message,
        object evidence) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.ReconciliationRequired,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.Ambiguous,
                SuggestedActions = ReconcileActions
            },
            Evidence =
            [
                new EvidenceItem(
                    "mutation.outcome_ambiguous",
                    JsonSerializer.SerializeToElement(evidence))
            ]
        };

    private static OperationResult Success(
        OperationRequest request,
        JsonElement payload) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(payload)
        };

    private static OperationResult InvalidRequest(
        OperationRequest request,
        string kind,
        string message) =>
        DocumentFailure(
            request,
            OperationStatus.InvalidRequest,
            TargetState.Known,
            kind,
            message,
            ExecutionState.NotStarted,
            ["inspect_operation_input"]);

    private static OperationResult DocumentFailure(
        OperationRequest request,
        OperationStatus status,
        TargetState targetState,
        string kind,
        string message,
        ExecutionState execution,
        IReadOnlyList<string> suggestedActions,
        bool retryable = false,
        int? hresult = null) =>
        new()
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
                Retryable = retryable,
                Execution = execution,
                HResult = hresult,
                HResultHex = hresult is null
                    ? null
                    : $"0x{unchecked((uint)hresult.Value):X8}",
                SuggestedActions = suggestedActions
            }
        };

    // ---------------------------------------------------------------------
    // Input parsing
    // ---------------------------------------------------------------------

    private static bool TryReadStrictInput(
        OperationRequest request,
        IReadOnlySet<string> allowedProperties,
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

    private static bool TryReadDocumentSelector(
        JsonElement input,
        out DocumentSelector selector,
        out string error)
    {
        selector = default;
        error = string.Empty;

        string? name = null;
        int? index = null;
        var active = false;

        if (input.TryGetProperty("document", out var documentElement) &&
            documentElement.ValueKind != JsonValueKind.Null)
        {
            if (documentElement.ValueKind != JsonValueKind.Object)
            {
                error = "'document' must be a JSON object.";
                return false;
            }

            foreach (var property in documentElement.EnumerateObject())
            {
                if (!DocumentSelectorProperties.Contains(property.Name))
                {
                    error =
                        $"'document' contains unknown field '{property.Name}'.";
                    return false;
                }
            }

            if (documentElement.TryGetProperty(
                    "name",
                    out var nameElement))
            {
                if (nameElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(
                        nameElement.GetString()))
                {
                    error =
                        "'document.name' must be a non-empty string.";
                    return false;
                }

                name = nameElement.GetString();
            }

            if (documentElement.TryGetProperty(
                    "index",
                    out var indexElement))
            {
                if (indexElement.ValueKind != JsonValueKind.Number ||
                    !indexElement.TryGetInt32(out var parsedIndex) ||
                    parsedIndex < 0)
                {
                    error =
                        "'document.index' must be a non-negative integer.";
                    return false;
                }

                index = parsedIndex;
            }

            if (documentElement.TryGetProperty(
                    "active",
                    out var activeElement))
            {
                if (activeElement.ValueKind is not (
                        JsonValueKind.True or
                        JsonValueKind.False))
                {
                    error = "'document.active' must be a boolean.";
                    return false;
                }

                active = activeElement.GetBoolean();
            }
        }

        if (input.TryGetProperty("documentId", out var documentIdElement) &&
            documentIdElement.ValueKind != JsonValueKind.Null)
        {
            if (documentIdElement.ValueKind != JsonValueKind.String)
            {
                error = "'documentId' must be a string.";
                return false;
            }

            var documentId = documentIdElement.GetString();
            if (string.IsNullOrWhiteSpace(documentId) ||
                documentId.Length > MaxDocumentIdChars)
            {
                error =
                    $"'documentId' must be a non-empty string of at most {MaxDocumentIdChars} characters.";
                return false;
            }

            if (TryParseDocumentId(
                    documentId!,
                    out var parsedName,
                    out var parsedIndex))
            {
                name ??= parsedName;
                index ??= parsedIndex;
            }
            else
            {
                // A document identity that is not a supported selector form
                // (a UUID, for example) cannot be read back from the COM
                // surface. Reject it rather than silently acting on a
                // different document.
                error =
                    "'documentId' is not a resolvable document identity in this adapter; supply document.name or document.index.";
                return false;
            }
        }

        if (name is null && index is null && !active)
        {
            error =
                "A document selector is required ('document.name', 'document.index', 'document.active', or 'documentId').";
            return false;
        }

        selector = new DocumentSelector(name, index, active);
        return true;
    }

    private static bool TryReadSelector(
        JsonElement input,
        out DocumentSelector selector,
        out string error)
    {
        selector = default;
        error = string.Empty;

        string? name = null;
        int? index = null;
        var active = false;

        if (input.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind != JsonValueKind.Null)
        {
            if (nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                error = "'name' must be a non-empty string.";
                return false;
            }

            name = nameElement.GetString();
        }

        if (input.TryGetProperty("index", out var indexElement) &&
            indexElement.ValueKind != JsonValueKind.Null)
        {
            if (indexElement.ValueKind != JsonValueKind.Number ||
                !indexElement.TryGetInt32(out var parsedIndex) ||
                parsedIndex < 0)
            {
                error = "'index' must be a non-negative integer.";
                return false;
            }

            index = parsedIndex;
        }

        if (input.TryGetProperty("active", out var activeElement) &&
            activeElement.ValueKind != JsonValueKind.Null)
        {
            if (activeElement.ValueKind is not (
                    JsonValueKind.True or
                    JsonValueKind.False))
            {
                error = "'active' must be a boolean.";
                return false;
            }

            active = activeElement.GetBoolean();
        }

        if (name is null && index is null && !active)
        {
            error =
                "A document selector is required ('name', 'index', or 'active').";
            return false;
        }

        selector = new DocumentSelector(name, index, active);
        return true;
    }

    private static bool TryParseDocumentId(
        string documentId,
        out string? name,
        out int? index)
    {
        name = null;
        index = null;

        const string nameMarker = "document:name:";
        const string indexMarker = "document:index:";

        if (documentId.StartsWith(
                nameMarker,
                StringComparison.Ordinal))
        {
            name = documentId[nameMarker.Length..];
            return name.Length > 0;
        }

        if (documentId.StartsWith(
                indexMarker,
                StringComparison.Ordinal))
        {
            return int.TryParse(
                documentId[indexMarker.Length..],
                out var parsedIndex) &&
                parsedIndex >= 0 &&
                (index = parsedIndex) >= 0;
        }

        return false;
    }

    private static bool TryReadClosePolicy(
        JsonElement input,
        out ClosePolicy closePolicy,
        out string error)
    {
        closePolicy = ClosePolicy.RejectIfUnsaved;
        error = string.Empty;

        // Mandatory and explicit: a missing token is a hard invalid request,
        // never a default. This is the invariant that makes accidental
        // discard impossible.
        if (!input.TryGetProperty(
                "closePolicy",
                out var policyElement) ||
            policyElement.ValueKind is
                JsonValueKind.Null or
                JsonValueKind.Undefined)
        {
            error =
                "'closePolicy' is required and must be one of 'save', 'discard', or 'reject_if_unsaved'. There is no default; 'discard' must be requested explicitly.";
            return false;
        }

        if (policyElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in policyElement.EnumerateObject())
            {
                if (!string.Equals(
                        property.Name,
                        "mode",
                        StringComparison.Ordinal))
                {
                    error =
                        $"'closePolicy' contains unknown field '{property.Name}'.";
                    return false;
                }
            }

            if (!policyElement.TryGetProperty(
                    "mode",
                    out var modeElement))
            {
                error = "'closePolicy.mode' is required.";
                return false;
            }

            return TryReadClosePolicyToken(
                modeElement,
                out closePolicy,
                out error);
        }

        return TryReadClosePolicyToken(
            policyElement,
            out closePolicy,
            out error);
    }

    private static bool TryReadClosePolicyToken(
        JsonElement element,
        out ClosePolicy closePolicy,
        out string error)
    {
        closePolicy = ClosePolicy.RejectIfUnsaved;
        error = string.Empty;

        if (element.ValueKind != JsonValueKind.String)
        {
            error =
                "'closePolicy' must be a string or an object with a 'mode' string.";
            return false;
        }

        var token = element.GetString();

        // 'discard' is the only destructive token and is matched exactly: no
        // whitespace, no case folding. A near-miss is rejected rather than
        // coerced, so nothing can drift into a discard.
        if (string.Equals(
                token,
                DiscardPolicyToken,
                StringComparison.Ordinal))
        {
            closePolicy = ClosePolicy.Discard;
            return true;
        }

        if (string.Equals(token, "save", StringComparison.Ordinal))
        {
            closePolicy = ClosePolicy.Save;
            return true;
        }

        if (string.Equals(
                token,
                "reject_if_unsaved",
                StringComparison.Ordinal))
        {
            closePolicy = ClosePolicy.RejectIfUnsaved;
            return true;
        }

        error =
            $"'closePolicy' value '{token ?? "<null>"}' is not supported; expected 'save', 'discard', or 'reject_if_unsaved'.";
        return false;
    }

    private static string ClosePolicyToken(ClosePolicy closePolicy) =>
        closePolicy switch
        {
            ClosePolicy.Save => "save",
            ClosePolicy.Discard => "discard",
            _ => "reject_if_unsaved"
        };

    private static bool TryReadRequiredPath(
        JsonElement input,
        string propertyName,
        out string path,
        out string error)
    {
        path = string.Empty;
        error = string.Empty;

        if (!input.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            error = $"'{propertyName}' must be a non-empty string.";
            return false;
        }

        var value = element.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"'{propertyName}' must be a non-empty string.";
            return false;
        }

        if (value.Length > MaxPathChars)
        {
            error =
                $"'{propertyName}' exceeds the {MaxPathChars} character limit.";
            return false;
        }

        if (value.IndexOf('\0') >= 0)
        {
            error = $"'{propertyName}' must not contain NUL characters.";
            return false;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            // Relative paths resolve against Folder.current inside
            // Illustrator, which makes the target ambiguous. Require an
            // absolute path.
            error =
                $"'{propertyName}' must be an absolute path; relative paths resolve against Illustrator's current folder and cannot be verified.";
            return false;
        }

        path = value;
        return true;
    }

    private static bool TryReadOpenRequestId(
        JsonElement input,
        out string requestId,
        out string error)
    {
        requestId = string.Empty;
        error = string.Empty;

        if (!input.TryGetProperty(
                "requestId",
                out var element))
        {
            // Optional; a caller-stable request id strengthens attribution
            // between a dispatched open and a later run.
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = "'requestId' must be a string.";
            return false;
        }

        var value = element.GetString() ?? string.Empty;
        if (value.Length == 0 ||
            value.Length > 256 ||
            value.Any(char.IsControl))
        {
            error =
                "'requestId' must be 1..256 characters without control characters.";
            return false;
        }

        requestId = value;
        return true;
    }

    private static bool TryReadOverwrite(
        JsonElement input,
        out bool overwrite,
        out string error)
    {
        overwrite = false;
        error = string.Empty;

        if (!input.TryGetProperty("overwrite", out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return true;

        if (element.ValueKind is not (
                JsonValueKind.True or
                JsonValueKind.False))
        {
            error = "'overwrite' must be a boolean.";
            return false;
        }

        // Explicit by construction: 'overwrite' must be the boolean true. A
        // stronger form than a truthy string is used here on purpose.
        overwrite = element.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool TryReadOptionalInt(
        JsonElement input,
        string propertyName,
        out int? value,
        out string error)
    {
        value = null;
        error = string.Empty;

        if (!input.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return true;

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var parsed))
        {
            error = $"'{propertyName}' must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadOptionalFiniteDouble(
        JsonElement input,
        string propertyName,
        out double? value,
        out string error)
    {
        value = null;
        error = string.Empty;

        if (!input.TryGetProperty(
                propertyName,
                out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return true;

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out var parsed) ||
            double.IsNaN(parsed) ||
            double.IsInfinity(parsed))
        {
            error =
                $"'{propertyName}' must be a finite number.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool PathEquals(
        string? left,
        string? right)
    {
        if (string.IsNullOrEmpty(left) ||
            string.IsNullOrEmpty(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return string.Equals(
                left,
                right,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------------
    // Nested types
    // ---------------------------------------------------------------------

    internal enum ClosePolicy
    {
        Save = 0,
        Discard = 1,
        RejectIfUnsaved = 2
    }

    private readonly record struct DocumentSelector(
        string? Name,
        int? Index,
        bool Active);

    private sealed record DocumentInfo(
        string? Path,
        string? FullNameFsName,
        string? Name,
        bool? Saved,
        string? SaveRequestId);

    private sealed record DocumentSnapshot(
        bool Readable,
        int DocumentCount,
        IReadOnlyList<DocumentInfo> Documents);

    private readonly record struct ScriptDispatchResult(
        ScriptOutcome Outcome,
        HostAdapterException? TransportFault,
        HostAdapterException? NonAmbiguousFault);

    internal readonly record struct ScriptOutcome(
        string Kind,
        string? Document,
        string? Message,
        string? RequestId)
    {
        public static ScriptOutcome Completed(
            string kind,
            string? document,
            string? requestId) =>
            new(kind, document, null, requestId);

        public static ScriptOutcome Failure(
            string message,
            string? requestId = null) =>
            new("failed", null, message, requestId);
    }
}
