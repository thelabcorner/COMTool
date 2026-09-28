using System.Text.Json;
using ComTool.Hosts.Abstractions;

namespace ComTool.Knowledge;

public enum ComInventoryValidationDisposition
{
    Validated,
    Indeterminate,
    Invalid
}

public sealed record ComInventoryValidationResult(
    ComInventoryValidationDisposition Disposition,
    string? Kind = null,
    string? Message = null)
{
    public bool Invalid =>
        Disposition == ComInventoryValidationDisposition.Invalid;

    public static ComInventoryValidationResult Validated() =>
        new(ComInventoryValidationDisposition.Validated);

    public static ComInventoryValidationResult Indeterminate() =>
        new(ComInventoryValidationDisposition.Indeterminate);

    public static ComInventoryValidationResult InvalidResult(
        string kind,
        string message) =>
        new(ComInventoryValidationDisposition.Invalid, kind, message);
}

/// <summary>
/// Conservative pre-dispatch validation for the generic COM surfaces.
/// The embedded inventory is a positive-signature oracle, not a version-complete
/// denylist: unresolved/missing paths remain indeterminate and are allowed to
/// reach the late-bound COM worker. When a path resolves to a known signature,
/// impossible member access, arity, type, or enum inputs are rejected before
/// host dispatch. This never claims that the versionless inventory is complete.
/// </summary>
public static class ComMutationKnowledgeValidator
{
    public const string SetOperation = "com.set";
    public const string CallOperation = "com.call";
    public const string GetOperation = "com.get";
    public const string CallReadOperation = "com.call.read";

    private static readonly Lazy<Inventory> Index = new(
        BuildInventory,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static ComInventoryValidationResult Validate(
        string operation,
        JsonElement input)
    {
        if (operation is not SetOperation and
            not CallOperation and
            not GetOperation and
            not CallReadOperation)
            return ComInventoryValidationResult.Indeterminate();

        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            return ComInventoryValidationResult.Indeterminate();
        }

        var path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ComInventoryValidationResult.Indeterminate();

        ComAutomationPathSegment[] segments;
        try
        {
            segments = operation == GetOperation
                ? ComAutomationPath.ParseRead(path)
                : ComAutomationPath.ParseMutation(path);
        }
        catch (ComAutomationPathException ex)
        {
            return ComInventoryValidationResult.InvalidResult(
                ex.Kind,
                ex.Message);
        }

        var inventory = Index.Value;
        var owner = "_Application";

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            var property = inventory.FindProperty(owner, segment.Name);
            if (property is null)
                return ComInventoryValidationResult.Indeterminate();

            var destination = property.UniqueGetterInterface();
            if (destination is null)
                return ComInventoryValidationResult.Indeterminate();

            owner = destination;

            if (segment.Index is not null)
            {
                var item = inventory.FindProperty(owner, "Item");
                if (item is null)
                    return ComInventoryValidationResult.Indeterminate();

                destination = item.UniqueGetterInterface();
                if (destination is null)
                    return ComInventoryValidationResult.Indeterminate();

                owner = destination;
            }
        }

        return operation switch
        {
            GetOperation =>
                ValidateGet(inventory, owner, segments[^1]),
            SetOperation =>
                ValidateSet(inventory, owner, segments[^1], input),
            CallOperation or CallReadOperation =>
                ValidateCall(inventory, owner, segments[^1], input),
            _ => ComInventoryValidationResult.Indeterminate()
        };
    }

    private static ComInventoryValidationResult ValidateGet(
        Inventory inventory,
        string owner,
        ComAutomationPathSegment terminal)
    {
        var property = inventory.FindProperty(owner, terminal.Name);
        if (property is null)
            return ComInventoryValidationResult.Indeterminate();

        if (property.Getters.Count == 0)
        {
            return ComInventoryValidationResult.InvalidResult(
                "com_inventory_member_not_readable",
                $"Inventory member '{owner}.{terminal.Name}' has no property-get accessor.");
        }

        if (terminal.Index is null)
            return ComInventoryValidationResult.Validated();

        var collectionInterface = property.UniqueGetterInterface();
        if (collectionInterface is null)
            return ComInventoryValidationResult.Indeterminate();

        var item = inventory.FindProperty(collectionInterface, "Item");
        if (item is null)
            return ComInventoryValidationResult.Indeterminate();

        return item.Getters.Count > 0
            ? ComInventoryValidationResult.Validated()
            : ComInventoryValidationResult.InvalidResult(
                "com_inventory_member_not_readable",
                $"Inventory member '{collectionInterface}.Item' has no property-get accessor.");
    }

    private static ComInventoryValidationResult ValidateSet(
        Inventory inventory,
        string owner,
        ComAutomationPathSegment terminal,
        JsonElement input)
    {
        if (!input.TryGetProperty("value", out var value))
            return ComInventoryValidationResult.Indeterminate();

        var property = inventory.FindProperty(owner, terminal.Name);
        if (property is null)
            return ComInventoryValidationResult.Indeterminate();

        if (property.Setters.Count == 0)
        {
            return ComInventoryValidationResult.InvalidResult(
                "com_inventory_member_not_writable",
                $"Inventory member '{owner}.{terminal.Name}' has no property-put accessor.");
        }

        var sawKnownSignature = false;
        var sawIndeterminate = false;
        foreach (var setter in property.Setters)
        {
            if (setter.Parameters.Count != 1)
            {
                sawIndeterminate = true;
                continue;
            }

            sawKnownSignature = true;
            var verdict = inventory.CheckValue(
                value,
                setter.Parameters[0].Type);

            if (verdict == ValueCompatibility.Compatible)
                return ComInventoryValidationResult.Validated();

            if (verdict == ValueCompatibility.Indeterminate)
                sawIndeterminate = true;
        }

        if (sawIndeterminate || !sawKnownSignature)
            return ComInventoryValidationResult.Indeterminate();

        return ComInventoryValidationResult.InvalidResult(
            "com_inventory_type_mismatch",
            $"Value is incompatible with the known inventory setter signature for '{owner}.{terminal.Name}'.");
    }

    private static ComInventoryValidationResult ValidateCall(
        Inventory inventory,
        string owner,
        ComAutomationPathSegment terminal,
        JsonElement input)
    {
        JsonElement[] arguments;
        if (!input.TryGetProperty("args", out var args) ||
            args.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            arguments = [];
        }
        else if (args.ValueKind == JsonValueKind.Array)
        {
            arguments = args.EnumerateArray()
                .Select(static item => item.Clone())
                .ToArray();
        }
        else
        {
            return ComInventoryValidationResult.Indeterminate();
        }

        var methods = inventory.FindMethods(owner, terminal.Name);
        if (methods.Count == 0)
            return ComInventoryValidationResult.Indeterminate();

        var arityMatches = methods
            .Where(method => method.AcceptsArity(arguments.Length))
            .ToArray();

        if (arityMatches.Length == 0)
        {
            var ranges = string.Join(
                ", ",
                methods.Select(static method => method.ArityDisplay)
                    .Distinct(StringComparer.Ordinal));

            return ComInventoryValidationResult.InvalidResult(
                "com_inventory_arity_mismatch",
                $"Argument count {arguments.Length} is incompatible with the known inventory signature for '{owner}.{terminal.Name}' (accepted: {ranges}).");
        }

        var sawIndeterminate = false;
        foreach (var method in arityMatches)
        {
            var incompatible = false;
            var indeterminate = false;

            for (var i = 0; i < arguments.Length; i++)
            {
                if (i >= method.Parameters.Count)
                {
                    indeterminate = true;
                    break;
                }

                var verdict = inventory.CheckValue(
                    arguments[i],
                    method.Parameters[i].Type);
                if (verdict == ValueCompatibility.Incompatible)
                {
                    incompatible = true;
                    break;
                }

                if (verdict == ValueCompatibility.Indeterminate)
                    indeterminate = true;
            }

            if (!incompatible && !indeterminate)
                return ComInventoryValidationResult.Validated();

            if (!incompatible)
                sawIndeterminate = true;
        }

        if (sawIndeterminate)
            return ComInventoryValidationResult.Indeterminate();

        return ComInventoryValidationResult.InvalidResult(
            "com_inventory_type_mismatch",
            $"Arguments are incompatible with every known inventory signature for '{owner}.{terminal.Name}'.");
    }

    private static Inventory BuildInventory()
    {
        var options = new KnowledgeScanOptions
        {
            MaxScanBytes = 16L * 1024 * 1024,
            MaxScannedRecords = 500_000,
            MaxRetainedRecords = 20_000
        };

        using var reader = KnowledgePackReader.OpenEmbedded(options);
        reader.VerifyIntegrity();

        var selected = reader.Select(
            new KnowledgeSelection
            {
                IncludeMethods = true,
                IncludeProperties = true,
                IncludeEnums = true,
                IncludeEnumValues = true
            });

        var members = reader.LoadMembers(
            selected.Methods.Select(static method => method.Id).ToHashSet(),
            selected.Properties.Select(static property => property.Id).ToHashSet(),
            includeAccessorParameters: true);

        if (reader.ScanBudgetExhausted)
        {
            throw new KnowledgePackException(
                "knowledge_scan_budget_exhausted",
                "The COM mutation validation index exceeded its bounded build budget.");
        }

        var properties = new Dictionary<
            string,
            Dictionary<string, PropertyShape>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var property in selected.Properties)
        {
            if (!properties.TryGetValue(property.Interface, out var byName))
            {
                byName = new Dictionary<string, PropertyShape>(
                    StringComparer.OrdinalIgnoreCase);
                properties[property.Interface] = byName;
            }

            members.Accessors.TryGetValue(property.Id, out var accessors);
            var accessorShapes = (accessors ?? [])
                .Select(accessor =>
                    new AccessorShape(
                        accessor,
                        members.AccessorParameters.TryGetValue(
                            accessor.Id,
                            out var parameters)
                            ? parameters
                                .OrderBy(static item => item.Position)
                                .ToArray()
                            : []))
                .ToArray();

            byName[property.Name] = new PropertyShape(
                property,
                accessorShapes);
        }

        var methods = new Dictionary<
            string,
            Dictionary<string, List<MethodShape>>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var method in selected.Methods)
        {
            if (!methods.TryGetValue(method.Interface, out var byName))
            {
                byName = new Dictionary<string, List<MethodShape>>(
                    StringComparer.OrdinalIgnoreCase);
                methods[method.Interface] = byName;
            }

            if (!byName.TryGetValue(method.Name, out var overloads))
            {
                overloads = [];
                byName[method.Name] = overloads;
            }

            overloads.Add(
                new MethodShape(
                    method,
                    members.MethodParameters.TryGetValue(
                        method.Id,
                        out var parameters)
                        ? parameters
                            .OrderBy(static item => item.Position)
                            .ToArray()
                        : []));
        }

        var enumValues = selected.EnumValues
            .Where(static item => item.Value is not null)
            .GroupBy(
                static item => item.EnumName,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static item => item.Value!.Value)
                    .ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        return new Inventory(properties, methods, enumValues);
    }

    private enum ValueCompatibility
    {
        Compatible,
        Indeterminate,
        Incompatible
    }

    private sealed record AccessorShape(
        KnowledgeAccessorInfo Accessor,
        IReadOnlyList<KnowledgeParameterInfo> Parameters);

    private sealed record PropertyShape(
        KnowledgePropertyInfo Property,
        IReadOnlyList<AccessorShape> Accessors)
    {
        public IReadOnlyList<AccessorShape> Getters =>
            Accessors
                .Where(static item =>
                    string.Equals(
                        item.Accessor.InvokeKind,
                        "INVOKE_PROPERTYGET",
                        StringComparison.Ordinal))
                .ToArray();

        public IReadOnlyList<AccessorShape> Setters =>
            Accessors
                .Where(static item =>
                    string.Equals(
                        item.Accessor.InvokeKind,
                        "INVOKE_PROPERTYPUT",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        item.Accessor.InvokeKind,
                        "INVOKE_PROPERTYPUTREF",
                        StringComparison.Ordinal))
                .ToArray();

        public string? UniqueGetterInterface()
        {
            var destinations = Accessors
                .Where(static item =>
                    string.Equals(
                        item.Accessor.InvokeKind,
                        "INVOKE_PROPERTYGET",
                        StringComparison.Ordinal))
                .Select(static item =>
                    ReferencedInterface(item.Accessor.ReturnType))
                .Where(static item => item is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return destinations.Length == 1
                ? destinations[0]
                : null;
        }
    }

    private sealed record MethodShape(
        KnowledgeMethodInfo Method,
        IReadOnlyList<KnowledgeParameterInfo> Parameters)
    {
        public int OptionalCount =>
            Method.OptionalParameterCount ??
            Parameters.Count(static parameter =>
                parameter.Flags?.Contains(
                    "PARAMFLAG_FOPT",
                    StringComparison.OrdinalIgnoreCase) == true);

        public int RequiredCount =>
            Math.Max(0, Parameters.Count - OptionalCount);

        public bool AcceptsArity(int count) =>
            count >= RequiredCount &&
            count <= Parameters.Count;

        public string ArityDisplay =>
            RequiredCount == Parameters.Count
                ? RequiredCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                : $"{RequiredCount}..{Parameters.Count}";
    }

    private sealed class Inventory(
        IReadOnlyDictionary<
            string,
            Dictionary<string, PropertyShape>> properties,
        IReadOnlyDictionary<
            string,
            Dictionary<string, List<MethodShape>>> methods,
        IReadOnlyDictionary<string, HashSet<long>> enumValues)
    {
        public PropertyShape? FindProperty(
            string owner,
            string name) =>
            properties.TryGetValue(owner, out var byName) &&
            byName.TryGetValue(name, out var property)
                ? property
                : null;

        public IReadOnlyList<MethodShape> FindMethods(
            string owner,
            string name) =>
            methods.TryGetValue(owner, out var byName) &&
            byName.TryGetValue(name, out var overloads)
                ? overloads
                : [];

        public ValueCompatibility CheckValue(
            JsonElement value,
            string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return ValueCompatibility.Indeterminate;

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return ValueCompatibility.Indeterminate;

            var normalized = type.Trim();

            if (enumValues.TryGetValue(normalized, out var values))
            {
                if (value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetInt64(out var enumValue))
                {
                    return ValueCompatibility.Incompatible;
                }

                return values.Contains(enumValue)
                    ? ValueCompatibility.Compatible
                    : ValueCompatibility.Incompatible;
            }

            if (normalized.StartsWith(
                    "PTR ",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ValueCompatibility.Incompatible;
            }

            return normalized.ToUpperInvariant() switch
            {
                "VT_VARIANT" => ValueCompatibility.Compatible,
                "VT_BSTR" => value.ValueKind == JsonValueKind.String
                    ? ValueCompatibility.Compatible
                    : ValueCompatibility.Incompatible,
                "VT_BOOL" => value.ValueKind is
                    JsonValueKind.True or JsonValueKind.False
                    ? ValueCompatibility.Compatible
                    : ValueCompatibility.Incompatible,
                "VT_I1" or "VT_UI1" or
                "VT_I2" or "VT_UI2" or
                "VT_I4" or "VT_UI4" or
                "VT_I8" or "VT_UI8" or
                "VT_INT" or "VT_UINT" =>
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out _)
                        ? ValueCompatibility.Compatible
                        : ValueCompatibility.Incompatible,
                "VT_R4" or "VT_R8" or
                "VT_DECIMAL" or "VT_CY" =>
                    value.ValueKind == JsonValueKind.Number
                        ? ValueCompatibility.Compatible
                        : ValueCompatibility.Incompatible,
                "VT_DISPATCH" or "VT_UNKNOWN" =>
                    ValueCompatibility.Indeterminate,
                _ when normalized.Contains(
                    "SAFEARRAY",
                    StringComparison.OrdinalIgnoreCase) =>
                    value.ValueKind == JsonValueKind.Array
                        ? ValueCompatibility.Compatible
                        : ValueCompatibility.Incompatible,
                _ => ValueCompatibility.Indeterminate
            };
        }
    }

    private static string? ReferencedInterface(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return null;

        const string prefix = "PTR ";
        if (!returnType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var name = returnType[prefix.Length..].Trim();
        return name.Length == 0 ||
               name.Contains("SAFEARRAY", StringComparison.OrdinalIgnoreCase)
            ? null
            : name;
    }
}
