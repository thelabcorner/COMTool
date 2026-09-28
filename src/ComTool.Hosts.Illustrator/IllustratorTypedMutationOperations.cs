using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Narrow, runtime-owned Illustrator mutations with fixed contracts.
/// Callers select an exact document/object and a fixed property operation; they
/// never supply a COM path or member name.
/// </summary>
internal static class IllustratorTypedMutationOperations
{
    internal const string LayerSetNameOperation = "illustrator.layer.setName";
    internal const string LayerSetVisibleOperation = "illustrator.layer.setVisible";
    internal const string LayerSetLockedOperation = "illustrator.layer.setLocked";
    internal const string LayerSetOpacityOperation = "illustrator.layer.setOpacity";
    internal const string ArtboardSetRectOperation = "illustrator.artboard.setRect";

    internal static readonly IReadOnlySet<string> OperationNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            LayerSetNameOperation,
            LayerSetVisibleOperation,
            LayerSetLockedOperation,
            LayerSetOpacityOperation,
            ArtboardSetRectOperation
        };

    private const int MaxNameChars = 4_096;
    private const double MaxCoordinateMagnitude = 1_000_000_000d;

    public static OperationResult Execute(object appObject, OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        if (!OperationNames.Contains(request.Operation))
            return Failure(
                request,
                OperationStatus.UnsupportedOperation,
                "unsupported_operation",
                $"Typed Illustrator mutation surface does not support '{request.Operation}'.",
                ExecutionState.NotStarted,
                ["core.target.capabilities"]);

        if (request.Input.ValueKind != JsonValueKind.Object)
            return InvalidRequest(
                request,
                "invalid_input",
                "Operation input must be a JSON object.");

        return request.Operation == ArtboardSetRectOperation
            ? ExecuteArtboardSetRect(appObject, request)
            : ExecuteLayerMutation(appObject, request);
    }

    private static OperationResult ExecuteLayerMutation(
        object appObject,
        OperationRequest request)
    {
        if (!TryReadTopLevel(
                request,
                "layer",
                out var documentElement,
                out var layerElement,
                out var valueElement,
                out var inputError))
            return inputError!;

        if (!TryParseDocumentSelector(
                documentElement,
                out var documentSelector,
                out var selectorError))
            return InvalidRequest(
                request,
                "invalid_document_selector",
                selectorError!);

        if (!TryParseLayerSelector(
                layerElement,
                out var layerSelector,
                out selectorError))
            return InvalidRequest(
                request,
                "invalid_layer_selector",
                selectorError!);

        var documentResolution =
            ResolveDocument(appObject, documentSelector);
        if (documentResolution.Value is null)
            return Failure(
                request,
                OperationStatus.Failed,
                documentResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "document_selector_ambiguous"
                    : "document_not_found",
                documentResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "The document name selector matches more than one open Illustrator document."
                    : "The selected Illustrator document does not exist.",
                ExecutionState.NotStarted,
                ["refresh_document_state"]);
        var document = documentResolution.Value;

        var layerResolution =
            ResolveLayer(appObject, document.Index, layerSelector);
        if (layerResolution.Value is null)
            return Failure(
                request,
                OperationStatus.Failed,
                layerResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "layer_selector_ambiguous"
                    : "layer_not_found",
                layerResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "The layer name selector matches more than one layer in the selected Illustrator document."
                    : "The selected Illustrator layer does not exist.",
                ExecutionState.NotStarted,
                ["illustrator.layer.read"]);
        var layer = layerResolution.Value;

        if (!SelectionStillMatches(
                appObject,
                document,
                layer))
        {
            return Failure(
                request,
                OperationStatus.Failed,
                "typed_mutation_selector_changed",
                "The selected Illustrator document or layer changed after selector resolution and before the property put.",
                ExecutionState.NotStarted,
                ["illustrator.layer.read", "refresh_document_state"]);
        }

        string member;
        object comValue;
        object responseValue;

        switch (request.Operation)
        {
            case LayerSetNameOperation:
                if (valueElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(valueElement.GetString()) ||
                    valueElement.GetString()!.Length > MaxNameChars)
                    return InvalidRequest(
                        request,
                        "invalid_layer_name",
                        $"'value' must be a non-empty string of at most {MaxNameChars} characters.");

                member = "Name";
                comValue = valueElement.GetString()!;
                responseValue = comValue;
                break;

            case LayerSetVisibleOperation:
                if (valueElement.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False))
                    return InvalidRequest(
                        request,
                        "invalid_layer_visible",
                        "'value' must be a boolean.");

                member = "Visible";
                comValue = valueElement.GetBoolean();
                responseValue = comValue;
                break;

            case LayerSetLockedOperation:
                if (valueElement.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False))
                    return InvalidRequest(
                        request,
                        "invalid_layer_locked",
                        "'value' must be a boolean.");

                member = "Locked";
                comValue = valueElement.GetBoolean();
                responseValue = comValue;
                break;

            case LayerSetOpacityOperation:
                if (valueElement.ValueKind != JsonValueKind.Number ||
                    !valueElement.TryGetDouble(out var opacity) ||
                    !double.IsFinite(opacity) ||
                    opacity is < 0d or > 100d)
                    return InvalidRequest(
                        request,
                        "invalid_layer_opacity",
                        "'value' must be a finite number from 0 through 100.");

                member = "Opacity";
                comValue = opacity;
                responseValue = opacity;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unexpected layer operation '{request.Operation}'.");
        }

        var path =
            $"Documents[{document.Index}].Layers[{layer.Index}].{member}";
        ApplyFixedSet(appObject, path, member, comValue, valueElement);

        return Success(
            request,
            JsonSerializer.SerializeToElement(new
            {
                document = new
                {
                    index = document.Index,
                    name = document.Name,
                    path = document.Path
                },
                layer = new
                {
                    index = layer.Index,
                    name = request.Operation == LayerSetNameOperation
                        ? (string)responseValue
                        : layer.Name
                },
                property = member,
                value = responseValue,
                mutationClass = "idempotent_write",
                ambiguous = false
            }));
    }

    private static OperationResult ExecuteArtboardSetRect(
        object appObject,
        OperationRequest request)
    {
        if (!TryReadTopLevel(
                request,
                "artboard",
                out var documentElement,
                out var artboardElement,
                out var valueElement,
                out var inputError))
            return inputError!;

        if (!TryParseDocumentSelector(
                documentElement,
                out var documentSelector,
                out var selectorError))
            return InvalidRequest(
                request,
                "invalid_document_selector",
                selectorError!);

        if (!TryParseArtboardSelector(
                artboardElement,
                out var artboardSelector,
                out selectorError))
            return InvalidRequest(
                request,
                "invalid_artboard_selector",
                selectorError!);

        if (!TryReadRect(valueElement, out var rect, out var rectError))
            return InvalidRequest(
                request,
                "invalid_artboard_rect",
                rectError!);

        var documentResolution =
            ResolveDocument(appObject, documentSelector);
        if (documentResolution.Value is null)
            return Failure(
                request,
                OperationStatus.Failed,
                documentResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "document_selector_ambiguous"
                    : "document_not_found",
                documentResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "The document name selector matches more than one open Illustrator document."
                    : "The selected Illustrator document does not exist.",
                ExecutionState.NotStarted,
                ["refresh_document_state"]);
        var document = documentResolution.Value;

        var artboardResolution = ResolveArtboard(
            appObject,
            document.Index,
            artboardSelector);
        if (artboardResolution.Value is null)
            return Failure(
                request,
                OperationStatus.Failed,
                artboardResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "artboard_selector_ambiguous"
                    : "artboard_not_found",
                artboardResolution.Status ==
                    SelectorResolutionStatus.Ambiguous
                    ? "The artboard name selector matches more than one artboard in the selected Illustrator document."
                    : "The selected Illustrator artboard does not exist.",
                ExecutionState.NotStarted,
                ["illustrator.artboard.read"]);
        var artboard = artboardResolution.Value;

        if (!SelectionStillMatches(
                appObject,
                document,
                artboard))
        {
            return Failure(
                request,
                OperationStatus.Failed,
                "typed_mutation_selector_changed",
                "The selected Illustrator document or artboard changed after selector resolution and before the property put.",
                ExecutionState.NotStarted,
                ["illustrator.artboard.read", "refresh_document_state"]);
        }

        var path =
            $"Documents[{document.Index}].Artboards[{artboard.Index}].ArtboardRect";
        ApplyFixedSet(
            appObject,
            path,
            "ArtboardRect",
            rect,
            valueElement);

        return Success(
            request,
            JsonSerializer.SerializeToElement(new
            {
                document = new
                {
                    index = document.Index,
                    name = document.Name,
                    path = document.Path
                },
                artboard = new
                {
                    index = artboard.Index,
                    name = artboard.Name
                },
                property = "ArtboardRect",
                value = rect,
                mutationClass = "idempotent_write",
                ambiguous = false
            }));
    }

    private static void ApplyFixedSet(
        object appObject,
        string path,
        string member,
        object comValue,
        JsonElement valueElement)
    {
        var parsed = IllustratorComMutationBridge.ParseSetInput(
            JsonSerializer.SerializeToElement(new
            {
                path,
                value = 0
            }),
            out var error);

        if (parsed is null)
            throw new InvalidOperationException(
                $"Runtime-owned typed COM path '{path}' is invalid: {error?.Message}");

        var plan = new IllustratorComMutationBridge.SetPlan(
            parsed.Segments,
            member,
            path,
            comValue,
            valueElement.Clone());

        IllustratorComMutationBridge.ApplySet(appObject, plan);
    }

    private static bool TryReadTopLevel(
        OperationRequest request,
        string targetField,
        out JsonElement document,
        out JsonElement target,
        out JsonElement value,
        out OperationResult? error)
    {
        document = default;
        target = default;
        value = default;
        error = null;

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

            if (property.Name != "document" &&
                property.Name != "value" &&
                !string.Equals(
                    property.Name,
                    targetField,
                    StringComparison.Ordinal))
            {
                error = InvalidRequest(
                    request,
                    "invalid_input",
                    $"Unknown input field '{property.Name}'.");
                return false;
            }
        }

        if (!request.Input.TryGetProperty("document", out document) ||
            document.ValueKind != JsonValueKind.Object)
        {
            error = InvalidRequest(
                request,
                "invalid_document_selector",
                "'document' must be a JSON object.");
            return false;
        }

        if (!request.Input.TryGetProperty(targetField, out target) ||
            target.ValueKind != JsonValueKind.Object)
        {
            error = InvalidRequest(
                request,
                $"invalid_{targetField}_selector",
                $"'{targetField}' must be a JSON object.");
            return false;
        }

        if (!request.Input.TryGetProperty("value", out value))
        {
            error = InvalidRequest(
                request,
                "invalid_value",
                "'value' is required.");
            return false;
        }

        return true;
    }

    private static bool TryParseDocumentSelector(
        JsonElement element,
        out DocumentSelector selector,
        out string? error)
    {
        selector = default;
        error = null;

        if (!TryRequireOneSelector(
                element,
                ["name", "index"],
                "document",
                out var name,
                out var value,
                out error))
            return false;

        switch (name)
        {
            case "name":
                if (!TryReadBoundedString(
                        value,
                        MaxNameChars,
                        out var documentName))
                {
                    error =
                        $"'document.name' must be a non-empty string of at most {MaxNameChars} characters.";
                    return false;
                }

                selector = new DocumentSelector(
                    documentName,
                    null);
                return true;

            case "index":
                if (!TryReadNonNegativeIndex(value, out var documentIndex))
                {
                    error =
                        "'document.index' must be a non-negative integer.";
                    return false;
                }

                selector = new DocumentSelector(
                    null,
                    documentIndex);
                return true;

            default:
                throw new InvalidOperationException();
        }
    }

    private static bool TryParseLayerSelector(
        JsonElement element,
        out LayerSelector selector,
        out string? error)
    {
        selector = default;
        error = null;

        if (!TryRequireOneSelector(
                element,
                ["index", "name"],
                "layer",
                out var name,
                out var value,
                out error))
            return false;

        switch (name)
        {
            case "index":
                if (!TryReadNonNegativeIndex(value, out var index))
                {
                    error =
                        "'layer.index' must be a non-negative integer.";
                    return false;
                }

                selector = new LayerSelector(index, null);
                return true;

            case "name":
                if (!TryReadBoundedString(
                        value,
                        MaxNameChars,
                        out var layerName))
                {
                    error =
                        $"'layer.name' must be a non-empty string of at most {MaxNameChars} characters.";
                    return false;
                }

                selector = new LayerSelector(null, layerName);
                return true;

            default:
                throw new InvalidOperationException();
        }
    }

    private static bool TryParseArtboardSelector(
        JsonElement element,
        out ArtboardSelector selector,
        out string? error)
    {
        selector = default;
        error = null;

        if (!TryRequireOneSelector(
                element,
                ["index", "name"],
                "artboard",
                out var name,
                out var value,
                out error))
            return false;

        switch (name)
        {
            case "index":
                if (!TryReadNonNegativeIndex(value, out var index))
                {
                    error =
                        "'artboard.index' must be a non-negative integer.";
                    return false;
                }

                selector = new ArtboardSelector(index, null);
                return true;

            case "name":
                if (!TryReadBoundedString(
                        value,
                        MaxNameChars,
                        out var artboardName))
                {
                    error =
                        $"'artboard.name' must be a non-empty string of at most {MaxNameChars} characters.";
                    return false;
                }

                selector = new ArtboardSelector(null, artboardName);
                return true;

            default:
                throw new InvalidOperationException();
        }
    }

    private static bool TryRequireOneSelector(
        JsonElement element,
        string[] allowed,
        string label,
        out string? selectedName,
        out JsonElement selectedValue,
        out string? error)
    {
        selectedName = null;
        selectedValue = default;
        error = null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate '{label}.{property.Name}' selector field.";
                return false;
            }

            if (!allowed.Contains(property.Name))
            {
                error =
                    $"'{label}' contains unknown selector field '{property.Name}'.";
                return false;
            }

            if (selectedName is not null)
            {
                error =
                    $"Provide exactly one '{label}' selector.";
                return false;
            }

            selectedName = property.Name;
            selectedValue = property.Value.Clone();
        }

        if (selectedName is null)
        {
            error =
                $"Provide exactly one '{label}' selector.";
            return false;
        }

        return true;
    }

    private static SelectorResolution<ResolvedDocument> ResolveDocument(
        object appObject,
        DocumentSelector selector)
    {
        var count = ReadRequiredInt(
            appObject,
            "Documents.Count");

        if (selector.Index is { } index)
            return index < count
                ? SelectorResolution<ResolvedDocument>.Found(
                    DescribeDocument(appObject, index))
                : SelectorResolution<ResolvedDocument>.NotFound();

        return selector.Name is null
            ? SelectorResolution<ResolvedDocument>.NotFound()
            : ResolveDocumentByName(
                appObject,
                count,
                selector.Name);
    }

    private static SelectorResolution<ResolvedDocument> ResolveDocumentByName(
        object appObject,
        int count,
        string name)
    {
        ResolvedDocument? match = null;

        for (var index = 0; index < count; index++)
        {
            var candidate = DescribeDocument(appObject, index);
            if (!string.Equals(
                    candidate.Name,
                    name,
                    StringComparison.Ordinal))
                continue;

            if (match is not null)
                return SelectorResolution<ResolvedDocument>.Ambiguous();

            match = candidate;
        }

        return match is null
            ? SelectorResolution<ResolvedDocument>.NotFound()
            : SelectorResolution<ResolvedDocument>.Found(match);
    }

    private static SelectorResolution<ResolvedLayer> ResolveLayer(
        object appObject,
        int documentIndex,
        LayerSelector selector)
    {
        var count = ReadRequiredInt(
            appObject,
            $"Documents[{documentIndex}].Layers.Count");

        if (selector.Index is { } index)
            return index < count
                ? SelectorResolution<ResolvedLayer>.Found(
                    DescribeLayer(appObject, documentIndex, index))
                : SelectorResolution<ResolvedLayer>.NotFound();

        ResolvedLayer? match = null;
        for (var candidateIndex = 0; candidateIndex < count; candidateIndex++)
        {
            var candidate = DescribeLayer(
                appObject,
                documentIndex,
                candidateIndex);

            var matches = string.Equals(
                candidate.Name,
                selector.Name,
                StringComparison.Ordinal);

            if (!matches)
                continue;

            if (match is not null)
                return SelectorResolution<ResolvedLayer>.Ambiguous();

            match = candidate;
        }

        return match is null
            ? SelectorResolution<ResolvedLayer>.NotFound()
            : SelectorResolution<ResolvedLayer>.Found(match);
    }

    private static SelectorResolution<ResolvedArtboard> ResolveArtboard(
        object appObject,
        int documentIndex,
        ArtboardSelector selector)
    {
        var count = ReadRequiredInt(
            appObject,
            $"Documents[{documentIndex}].Artboards.Count");

        if (selector.Index is { } index)
            return index < count
                ? SelectorResolution<ResolvedArtboard>.Found(
                    DescribeArtboard(
                        appObject,
                        documentIndex,
                        index))
                : SelectorResolution<ResolvedArtboard>.NotFound();

        ResolvedArtboard? match = null;
        for (var candidateIndex = 0; candidateIndex < count; candidateIndex++)
        {
            var candidate = DescribeArtboard(
                appObject,
                documentIndex,
                candidateIndex);

            if (!string.Equals(
                    candidate.Name,
                    selector.Name,
                    StringComparison.Ordinal))
                continue;

            if (match is not null)
                return SelectorResolution<ResolvedArtboard>.Ambiguous();

            match = candidate;
        }

        return match is null
            ? SelectorResolution<ResolvedArtboard>.NotFound()
            : SelectorResolution<ResolvedArtboard>.Found(match);
    }

    private static ResolvedDocument DescribeDocument(
        object appObject,
        int index) =>
        new(
            index,
            ReadOptionalString(
                appObject,
                $"Documents[{index}].Name"),
            ReadOptionalString(
                appObject,
                $"Documents[{index}].Path"));

    private static ResolvedLayer DescribeLayer(
        object appObject,
        int documentIndex,
        int index) =>
        new(
            index,
            ReadOptionalString(
                appObject,
                $"Documents[{documentIndex}].Layers[{index}].Name"));

    private static ResolvedArtboard DescribeArtboard(
        object appObject,
        int documentIndex,
        int index) =>
        new(
            index,
            ReadOptionalString(
                appObject,
                $"Documents[{documentIndex}].Artboards[{index}].Name"));

    private static bool SelectionStillMatches(
        object appObject,
        ResolvedDocument document,
        ResolvedLayer layer)
    {
        if (!DocumentStillMatches(appObject, document))
            return false;

        var count = ReadRequiredInt(
            appObject,
            $"Documents[{document.Index}].Layers.Count");
        return layer.Index < count &&
               DescribeLayer(
                   appObject,
                   document.Index,
                   layer.Index) == layer;
    }

    private static bool SelectionStillMatches(
        object appObject,
        ResolvedDocument document,
        ResolvedArtboard artboard)
    {
        if (!DocumentStillMatches(appObject, document))
            return false;

        var count = ReadRequiredInt(
            appObject,
            $"Documents[{document.Index}].Artboards.Count");
        return artboard.Index < count &&
               DescribeArtboard(
                   appObject,
                   document.Index,
                   artboard.Index) == artboard;
    }

    private static bool DocumentStillMatches(
        object appObject,
        ResolvedDocument document)
    {
        var count = ReadRequiredInt(
            appObject,
            "Documents.Count");
        return document.Index < count &&
               DescribeDocument(
                   appObject,
                   document.Index) == document;
    }

    private static int ReadRequiredInt(
        object appObject,
        string path)
    {
        try
        {
            var value =
                IllustratorComReadBridge.Get(appObject, path);
            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number) &&
                number >= 0)
                return number;
        }
        catch (Exception ex)
        {
            throw new HostAdapterException(
                "typed_mutation_selector_read_failed",
                $"Could not read '{path}' before mutation: {ex.Message}",
                retryable: false,
                ExecutionState.NotStarted,
                innerException: ex);
        }

        throw new HostAdapterException(
            "typed_mutation_selector_read_failed",
            $"Could not read a valid non-negative integer from '{path}' before mutation.",
            retryable: false,
            ExecutionState.NotStarted);
    }

    private static string? ReadOptionalString(
        object appObject,
        string path)
    {
        try
        {
            var value =
                IllustratorComReadBridge.Get(appObject, path);
            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex)
        {
            throw new HostAdapterException(
                "typed_mutation_selector_read_failed",
                $"Could not read '{path}' before mutation: {ex.Message}",
                retryable: false,
                ExecutionState.NotStarted,
                innerException: ex);
        }
    }

    private static bool TryReadRect(
        JsonElement element,
        out double[] rect,
        out string? error)
    {
        rect = [];
        error = null;

        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() != 4)
        {
            error =
                "'value' must be [left, top, right, bottom] with exactly four numbers.";
            return false;
        }

        rect = new double[4];
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number ||
                !item.TryGetDouble(out var number) ||
                !double.IsFinite(number) ||
                Math.Abs(number) > MaxCoordinateMagnitude)
            {
                error =
                    $"'value[{index}]' must be a finite coordinate with magnitude <= {MaxCoordinateMagnitude}.";
                return false;
            }

            rect[index++] = number;
        }

        if (rect[2] <= rect[0])
        {
            error =
                "Artboard right must be greater than left.";
            return false;
        }

        if (rect[1] <= rect[3])
        {
            error =
                "Artboard top must be greater than bottom in Illustrator document coordinates.";
            return false;
        }

        return true;
    }

    private static bool TryReadBoundedString(
        JsonElement element,
        int maxChars,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        var candidate = element.GetString();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Length > maxChars)
            return false;

        value = candidate;
        return true;
    }

    private static bool TryReadNonNegativeIndex(
        JsonElement element,
        out int value)
    {
        value = -1;
        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value) &&
               value >= 0;
    }

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
            ExecutionState.NotStarted,
            ["inspect_operation_input"]);

    private static OperationResult Failure(
        OperationRequest request,
        OperationStatus status,
        string kind,
        string message,
        ExecutionState execution,
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
                Execution = execution,
                SuggestedActions = suggestedActions
            }
        };

    private readonly record struct DocumentSelector(
        string? Name,
        int? Index);

    private readonly record struct LayerSelector(
        int? Index,
        string? Name);

    private readonly record struct ArtboardSelector(
        int? Index,
        string? Name);

    private enum SelectorResolutionStatus
    {
        Found,
        NotFound,
        Ambiguous
    }

    private readonly record struct SelectorResolution<T>(
        T? Value,
        SelectorResolutionStatus Status)
        where T : class
    {
        public static SelectorResolution<T> Found(T value) =>
            new(value, SelectorResolutionStatus.Found);

        public static SelectorResolution<T> NotFound() =>
            new(null, SelectorResolutionStatus.NotFound);

        public static SelectorResolution<T> Ambiguous() =>
            new(null, SelectorResolutionStatus.Ambiguous);
    }

    private sealed record ResolvedDocument(
        int Index,
        string? Name,
        string? Path);

    private sealed record ResolvedLayer(
        int Index,
        string? Name);

    private sealed record ResolvedArtboard(
        int Index,
        string? Name);
}
