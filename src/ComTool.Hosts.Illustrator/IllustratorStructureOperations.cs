using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Read-only Illustrator structure surfaces: artboards and layers.
///
/// These are the Wave 4 ("richer Illustrator surfaces") read primitives. They
/// are deliberately read-only and never mutate:
///
/// * They are registered in the runtime-owned catalog as
///   <see cref="MutationClass.ReadOnly"/>, so they neither require a lease nor
///   create a mutation-ledger entry, and they are usable as precondition and
///   postcondition sources.
/// * They address an explicit document selector (<c>name</c>, <c>index</c>, or
///   <c>active</c>) instead of silently retargeting to whatever document is
///   active, exactly like <c>illustrator.document.read</c>.
/// * A selector that does not resolve is a truthful observed state, not a
///   hidden failure: the read reports <c>exists: false</c> so the runtime
///   decides what a failed condition means.
/// * Every item field is read defensively. A single unreadable field degrades
///   to null instead of aborting the whole read.
/// </summary>
internal static class IllustratorStructureOperations
{
    internal const string ArtboardReadOperation =
        "illustrator.artboard.read";

    internal const string LayerReadOperation =
        "illustrator.layer.read";

    private const int MaxArtboardNameChars = 4_096;
    private const int MaxLayerNameChars = 4_096;


    private static readonly IReadOnlySet<string> DocumentSelectorProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "name",
            "index",
            "active"
        };

    public static OperationResult Execute(
        object appObject,
        OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        return request.Operation switch
        {
            ArtboardReadOperation => ReadArtboards(appObject, request),
            LayerReadOperation => ReadLayers(appObject, request),
            _ => Failure(
                request,
                OperationStatus.UnsupportedOperation,
                "unsupported_operation",
                $"Structure surface does not support '{request.Operation}'.",
                ["core.target.capabilities"])
        };
    }

    private static OperationResult ReadArtboards(
        object appObject,
        OperationRequest request)
    {
        if (!TryReadStrictDocumentInput(request, out var inputError))
            return inputError;

        if (!TryResolveDocument(
                appObject,
                request,
                out var document,
                out var resolveError))
        {
            return Success(
                request,
                BuildMissingPayload(
                    "artboards",
                    resolveError));
        }

        var count = ReadArtboardCount(appObject, document!.Index);
        if (count is null)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                "artboard_set_unreadable",
                "The selected document's artboard set could not be read.",
                ["inspect_host_state"]);
        }

        var artboards = new List<object>(count.Value);
        for (var index = 0; index < count.Value; index++)
        {
            artboards.Add(new
            {
                index,
                name = ReadArtboardString(
                    appObject,
                    document.Index,
                    index,
                    "Name"),
                artboardRect = ReadArtboardRect(
                    appObject,
                    document.Index,
                    index),
                rulerOrigin = ReadArtboardPoint(
                    appObject,
                    document.Index,
                    index,
                    "RulerOrigin"),
                rulerPAR = ReadArtboardInt(
                    appObject,
                    document.Index,
                    index,
                    "RulerPAR")
            });
        }

        return Success(
            request,
            JsonSerializer.SerializeToElement(new
            {
                document = new
                {
                    name = document.Name,
                    path = document.Path
                },
                index = document.Index,
                exists = true,
                artboardCount = count.Value,
                artboards,
                ambiguous = false
            }));
    }

    private static OperationResult ReadLayers(
        object appObject,
        OperationRequest request)
    {
        if (!TryReadStrictDocumentInput(request, out var inputError))
            return inputError;

        if (!TryResolveDocument(
                appObject,
                request,
                out var document,
                out var resolveError))
        {
            return Success(
                request,
                BuildMissingPayload(
                    "layers",
                    resolveError));
        }

        var count = ReadLayerCount(appObject, document!.Index);
        if (count is null)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                "layer_set_unreadable",
                "The selected document's layer set could not be read.",
                ["inspect_host_state"]);
        }

        var layers = new List<object>(count.Value);
        for (var index = 0; index < count.Value; index++)
        {
            layers.Add(new
            {
                index,
                name = ReadLayerString(
                    appObject,
                    document.Index,
                    index,
                    "Name"),
                visible = ReadLayerBool(
                    appObject,
                    document.Index,
                    index,
                    "Visible"),
                locked = ReadLayerBool(
                    appObject,
                    document.Index,
                    index,
                    "Locked"),
                isTemplate = ReadLayerBool(
                    appObject,
                    document.Index,
                    index,
                    "IsTemplate"),
                preview = ReadLayerBool(
                    appObject,
                    document.Index,
                    index,
                    "Preview"),
                opacity = ReadLayerDouble(
                    appObject,
                    document.Index,
                    index,
                    "Opacity"),
                itemCount = ReadLayerInt(
                    appObject,
                    document.Index,
                    index,
                    "PathItems.Count")
            });
        }

        return Success(
            request,
            JsonSerializer.SerializeToElement(new
            {
                document = new
                {
                    name = document.Name,
                    path = document.Path
                },
                index = document.Index,
                exists = true,
                layerCount = count.Value,
                layers,
                ambiguous = false
            }));
    }

    // ---------------------------------------------------------------------
    // Structural reads
    // ---------------------------------------------------------------------

    private static int? ReadArtboardCount(
        object appObject,
        int documentIndex)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Artboards.Count");

            return value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var count) &&
                   count >= 0
                ? count
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ReadLayerCount(
        object appObject,
        int documentIndex)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Layers.Count");

            return value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var count) &&
                   count >= 0
                ? count
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadArtboardString(
        object appObject,
        int documentIndex,
        int artboardIndex,
        string property) =>
        ReadString(
            appObject,
            $"Documents[{documentIndex}].Artboards[{artboardIndex}].{property}",
            MaxArtboardNameChars);

    private static string? ReadLayerString(
        object appObject,
        int documentIndex,
        int layerIndex,
        string property) =>
        ReadString(
            appObject,
            $"Documents[{documentIndex}].Layers[{layerIndex}].{property}",
            MaxLayerNameChars);

    private static string? ReadString(
        object appObject,
        string path,
        int maxChars)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(appObject, path);

            if (value.ValueKind != JsonValueKind.String)
                return null;

            var text = value.GetString();
            if (text is null || text.Length > maxChars)
                return null;

            return text;
        }
        catch (Exception)
        {
            // A single unreadable field (for example a host-specific macro or
            // an unsaved document identity) is reported as null instead of
            // aborting the whole structure read.
            return null;
        }
    }

    private static bool? ReadLayerBool(
        object appObject,
        int documentIndex,
        int layerIndex,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Layers[{layerIndex}].{property}");

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

    private static int? ReadLayerInt(
        object appObject,
        int documentIndex,
        int layerIndex,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Layers[{layerIndex}].{property}");

            return value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ReadArtboardInt(
        object appObject,
        int documentIndex,
        int artboardIndex,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Artboards[{artboardIndex}].{property}");

            return value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? ReadLayerDouble(
        object appObject,
        int documentIndex,
        int layerIndex,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Layers[{layerIndex}].{property}");

            return value.ValueKind == JsonValueKind.Number &&
                   value.TryGetDouble(out var number) &&
                   double.IsFinite(number)
                ? number
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<double>? ReadArtboardRect(
        object appObject,
        int documentIndex,
        int artboardIndex)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Artboards[{artboardIndex}].ArtboardRect");

            if (value.ValueKind != JsonValueKind.Array)
                return null;

            var values = new List<double>(4);
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number ||
                    !element.TryGetDouble(out var number) ||
                    !double.IsFinite(number))
                    return null;

                values.Add(number);
            }

            return values.Count == 4
                ? values
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<double>? ReadArtboardPoint(
        object appObject,
        int documentIndex,
        int artboardIndex,
        string property)
    {
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                $"Documents[{documentIndex}].Artboards[{artboardIndex}].{property}");

            if (value.ValueKind != JsonValueKind.Array)
                return null;

            var values = new List<double>(2);
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number ||
                    !element.TryGetDouble(out var number) ||
                    !double.IsFinite(number))
                    return null;

                values.Add(number);
            }

            return values.Count == 2
                ? values
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Document selector

    // ---------------------------------------------------------------------

    private static bool TryReadStrictDocumentInput(
        OperationRequest request,
        out OperationResult error)
    {
        error = null!;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error = InvalidRequest(
                request,
                "invalid_input",
                "Operation input must be a JSON object.");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in request.Input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error = InvalidRequest(
                    request,
                    "invalid_input",
                    $"Duplicate input field '{property.Name}'.");
                return false;
            }

            switch (property.Name)
            {
                case "name" when property.Value.ValueKind == JsonValueKind.String:
                    if (!IsBounded(
                            property.Value.GetString(),
                            MaxDocumentIdChars))
                    {
                        error = InvalidRequest(
                            request,
                            "invalid_document_selector",
                            "'name' must be a non-empty string of at most 4096 characters.");
                        return false;
                    }

                    break;

                case "index" when
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var index) &&
                    index >= 0:
                    break;

                case "active" when property.Value.ValueKind is
                    JsonValueKind.True or JsonValueKind.False:
                    if (property.Value.ValueKind == JsonValueKind.False)
                    {
                        error = InvalidRequest(
                            request,
                            "invalid_document_selector",
                            "'active' may only be true; omit it to address another document by name or index.");
                        return false;
                    }

                    break;

                default:
                    if (!DocumentSelectorProperties.Contains(property.Name))
                    {
                        error = InvalidRequest(
                            request,
                            "invalid_input",
                            $"Unknown input field '{property.Name}'.");
                        return false;
                    }

                    error = InvalidRequest(
                        request,
                        "invalid_document_selector",
                        $"Invalid value for document selector field '{property.Name}'.");
                    return false;
            }
        }

        if (seen.Count == 0)
        {
            error = InvalidRequest(
                request,
                "invalid_document_selector",
                "A document selector is required: 'name', 'index', or 'active'.");
            return false;
        }

        if (seen.Count > 1)
        {
            error = InvalidRequest(
                request,
                "invalid_document_selector",
                "Provide exactly one document selector: 'name', 'index', or 'active'.");
            return false;
        }

        return true;
    }

    private const int MaxDocumentIdChars = 4_096;

    private static bool TryResolveDocument(
        object appObject,
        OperationRequest request,
        out ResolvedDocument? document,
        out string resolveError)
    {
        document = null;
        resolveError = string.Empty;

        int count;
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                "Documents.Count");

            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out count) ||
                count < 0)
            {
                resolveError =
                    "The Illustrator document set could not be read.";
                return false;
            }
        }
        catch (Exception)
        {
            resolveError =
                "The Illustrator document set could not be read.";
            return false;
        }

        var selector = request.Input;

        if (selector.TryGetProperty("active", out var active) &&
            active.ValueKind == JsonValueKind.True)
        {
            if (count == 0)
            {
                resolveError = "No document is open.";
                return false;
            }

            var activeIndex = FindActiveDocument(appObject, count);
            if (activeIndex < 0)
            {
                resolveError =
                    "The active document could not be resolved.";
                return false;
            }

            document = DescribeDocument(appObject, activeIndex);
            return true;
        }

        if (selector.TryGetProperty("index", out var indexElement) &&
            indexElement.ValueKind == JsonValueKind.Number)
        {
            var index = indexElement.GetInt32();
            if (index < 0 || index >= count)
            {
                resolveError =
                    $"Document index {index} is out of range (0..{count - 1}).";
                return false;
            }

            document = DescribeDocument(appObject, index);
            return true;
        }

        if (selector.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String)
        {
            var name = nameElement.GetString();
            for (var index = 0; index < count; index++)
            {
                var candidate = DescribeDocument(appObject, index);
                if (string.Equals(
                        candidate.Name,
                        name,
                        StringComparison.Ordinal))
                {
                    document = candidate;
                    return true;
                }
            }

            resolveError =
                $"No open document is named '{name}'.";
            return false;
        }

        resolveError =
            "A document selector is required: 'name', 'index', or 'active'.";
        return false;
    }

    private static int FindActiveDocument(
        object appObject,
        int count)
    {
        // Document identity is positional, so the active document is found by
        // comparing the host-reported active name against each document. A
        // duplicate name is ambiguous and reported as unresolved rather than
        // guessed.
        string? activeName;
        try
        {
            var value = IllustratorComReadBridge.Get(
                appObject,
                "ActiveDocument.Name");

            activeName = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception)
        {
            return -1;
        }

        if (string.IsNullOrEmpty(activeName))
            return -1;

        var match = -1;
        for (var index = 0; index < count; index++)
        {
            var candidate = DescribeDocument(appObject, index);
            if (!string.Equals(
                    candidate.Name,
                    activeName,
                    StringComparison.Ordinal))
                continue;

            if (match >= 0)
            {
                // Ambiguous active identity (duplicate document names).
                return -1;
            }

            match = index;
        }

        return match;
    }

    private static ResolvedDocument DescribeDocument(
        object appObject,
        int index) =>
        new(
            Index: index,
            Name: ReadDocumentField(appObject, index, "Name"),
            Path: ReadDocumentField(appObject, index, "Path"));

    private static string? ReadDocumentField(
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
            return null;
        }
    }

    private static bool IsBounded(
        string? value,
        int maxChars) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxChars;

    private static JsonElement BuildMissingPayload(
        string collection,
        string reason) =>
        JsonSerializer.SerializeToElement(new
        {
            document = (object?)null,
            exists = false,
            ambiguous = false,
            reason,
            collection
        });

    // ---------------------------------------------------------------------
    // Result helpers
    // ---------------------------------------------------------------------

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
        Failure(
            request,
            OperationStatus.InvalidRequest,
            kind,
            message,
            ["inspect_operation_input"]);

    private static OperationResult Failure(
        OperationRequest request,
        OperationStatus status,
        string kind,
        string message,
        IReadOnlyList<string> suggestedActions) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = status,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = suggestedActions
            }
        };

    private sealed record ResolvedDocument(
        int Index,
        string? Name,
        string? Path);
}
