using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Knowledge;

/// <summary>
/// Runtime-facing, target-independent query surface over the immutable embedded
/// Illustrator COM knowledge pack. The pack is verified once per process; each
/// query then uses a fresh bounded reader so no mutable cursor is shared between
/// concurrent agent requests.
/// </summary>
public static class IllustratorComKnowledgeService
{
    public const string DescribeOperation = "knowledge.describe";
    public const string SearchOperation = "knowledge.search";
    public const string SymbolOperation = "knowledge.symbol";
    public const string EnumOperation = "knowledge.enum";
    public const string PathsOperation = "knowledge.paths";

    private const int MaxQueryChars = 128;
    private static readonly JsonSerializerOptions PayloadJson =
        new(JsonSerializerDefaults.Web);

    private static readonly Lazy<KnowledgePackHeader> VerifiedHeader =
        new(
            static () =>
            {
                using var reader = KnowledgePackReader.OpenEmbedded();
                reader.VerifyIntegrity();
                return reader.Header;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static OperationResult Execute(
        OperationRequest request,
        double totalMs = 0)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            _ = VerifiedHeader.Value;

            return request.Operation switch
            {
                DescribeOperation => Describe(request, totalMs),
                SearchOperation => Search(request, totalMs),
                SymbolOperation => Symbol(request, totalMs),
                EnumOperation => Enum(request, totalMs),
                PathsOperation => Paths(request, totalMs),
                _ => Failure(
                    request,
                    OperationStatus.UnsupportedOperation,
                    "unsupported_operation",
                    $"Knowledge operation '{request.Operation}' is not supported.",
                    totalMs)
            };
        }
        catch (ArgumentException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                "invalid_knowledge_request",
                ex.Message,
                totalMs);
        }
        catch (KnowledgePackException ex)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                ex.Kind,
                ex.Message,
                totalMs,
                ex.Retryable,
                ex.SuggestedActions);
        }
    }

    private static OperationResult Describe(
        OperationRequest request,
        double totalMs)
    {
        EnsureFields(request.Input, allowNull: true);

        var header = VerifiedHeader.Value;
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                format = header.Format,
                version = header.Version,
                bodyBytes = header.BodyBytes,
                bodySha256 = header.BodySha256,
                source = new
                {
                    kind = header.Source.Kind,
                    database = header.Source.Database,
                    databaseSha256 = header.Source.DatabaseSha256,
                    json = header.Source.Json,
                    jsonSha256 = header.Source.JsonSha256,
                    jsonOnDiskSha256Observed =
                        header.Source.JsonOnDiskSha256Observed,
                    jsonOnDiskMatchesManifest =
                        header.Source.JsonOnDiskMatchesManifest,
                    manifest = header.Source.Manifest,
                    exportGeneratedAtUtc =
                        header.Source.ExportGeneratedAtUtc,
                    interfaceKinds = header.Source.InterfaceKinds,
                    sourceOutgoingInterfacesFlagged =
                        header.Source.SourceOutgoingInterfacesFlagged
                },
                host = new
                {
                    family = header.Host.Family,
                    version = header.Host.Version,
                    versionKnown = header.Host.VersionKnown,
                    versionSource = header.Host.VersionSource
                },
                buildEnvironment = new
                {
                    illustratorProduct =
                        header.BuildEnvironment.IllustratorProduct,
                    illustratorProductVersion =
                        header.BuildEnvironment.IllustratorProductVersion,
                    authoritative =
                        header.BuildEnvironment.Authoritative
                },
                counts = header.Counts,
                limits = new
                {
                    defaultLimit =
                        KnowledgeScanOptions.Default.DefaultLimit,
                    maxLimit = KnowledgeScanOptions.Default.MaxLimit,
                    maxScanBytes =
                        KnowledgeScanOptions.Default.MaxScanBytes,
                    maxScannedRecords =
                        KnowledgeScanOptions.Default.MaxScannedRecords,
                    maxRetainedRecords =
                        KnowledgeScanOptions.Default.MaxRetainedRecords
                }
            },
            PayloadJson);

        return Success(request, payload, totalMs);
    }

    private static OperationResult Search(
        OperationRequest request,
        double totalMs)
    {
        var fields = EnsureFields(
            request.Input,
            "query",
            "limit");
        var query = RequiredString(fields, "query");
        var limit = ReadLimit(fields);

        using var reader = KnowledgePackReader.OpenEmbedded();
        var selected = reader.Select(
            new KnowledgeSelection
            {
                IncludeInterfaces = true,
                IncludeMethods = true,
                IncludeProperties = true,
                IncludeEnums = true,
                IncludeEnumValues = true
            });

        var items = new List<object>(limit);

        void Add(object item)
        {
            if (items.Count < limit)
                items.Add(item);
        }

        foreach (var item in selected.Interfaces)
        {
            if (Contains(item.Name, query) ||
                Contains(item.Kind, query))
            {
                Add(new
                {
                    kind = "interface",
                    name = item.Name,
                    interfaceKind = item.Kind,
                    guid = item.Guid
                });
            }
        }

        foreach (var item in selected.Methods)
        {
            if (Contains(item.Name, query) ||
                Contains(item.Interface, query) ||
                Contains(item.ReturnType, query))
            {
                Add(new
                {
                    kind = "method",
                    @interface = item.Interface,
                    name = item.Name,
                    dispid = item.Dispid,
                    invokeKind = item.InvokeKind,
                    returnType = item.ReturnType,
                    optionalParameterCount =
                        item.OptionalParameterCount
                });
            }
        }

        foreach (var item in selected.Properties)
        {
            if (Contains(item.Name, query) ||
                Contains(item.Interface, query))
            {
                Add(new
                {
                    kind = "property",
                    @interface = item.Interface,
                    name = item.Name,
                    dispid = item.Dispid
                });
            }
        }

        foreach (var item in selected.Enums)
        {
            if (Contains(item, query))
                Add(new { kind = "enum", name = item });
        }

        foreach (var item in selected.EnumValues)
        {
            if (Contains(item.EnumName, query) ||
                Contains(item.Name, query))
            {
                Add(new
                {
                    kind = "enum_value",
                    enumName = item.EnumName,
                    name = item.Name,
                    value = item.Value
                });
            }
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                query,
                count = items.Count,
                limit,
                items,
                scan = Scan(reader, selected.RetentionCapReached)
            },
            PayloadJson);

        return Success(request, payload, totalMs);
    }

    private static OperationResult Symbol(
        OperationRequest request,
        double totalMs)
    {
        var fields = EnsureFields(
            request.Input,
            "name",
            "interface",
            "limit");
        var name = RequiredString(fields, "name");
        var interfaceName = OptionalString(fields, "interface");
        var limit = ReadLimit(fields);

        using var reader = KnowledgePackReader.OpenEmbedded();
        var selected = reader.Select(
            new KnowledgeSelection
            {
                SymbolName = name,
                InterfaceName = interfaceName,
                IncludeMethods = true,
                IncludeProperties = true
            });

        var methods = selected.Methods.Take(limit).ToArray();
        var remaining = Math.Max(0, limit - methods.Length);
        var properties = selected.Properties.Take(remaining).ToArray();

        var members = reader.LoadMembers(
            methods.Select(static item => item.Id).ToHashSet(),
            properties.Select(static item => item.Id).ToHashSet(),
            includeAccessorParameters: true);

        var methodPayload = methods.Select(method => new
        {
            id = method.Id,
            @interface = method.Interface,
            name = method.Name,
            dispid = method.Dispid,
            invokeKind = method.InvokeKind,
            returnType = method.ReturnType,
            optionalParameterCount = method.OptionalParameterCount,
            parameters = members.MethodParameters.TryGetValue(
                method.Id,
                out var parameters)
                    ? parameters
                        .OrderBy(static item => item.Position)
                        .ToArray()
                    : []
        }).ToArray();

        var propertyPayload = properties.Select(property => new
        {
            id = property.Id,
            @interface = property.Interface,
            name = property.Name,
            dispid = property.Dispid,
            accessors = members.Accessors.TryGetValue(
                property.Id,
                out var accessors)
                    ? accessors.Select(accessor => new
                    {
                        id = accessor.Id,
                        invokeKind = accessor.InvokeKind,
                        returnType = accessor.ReturnType,
                        parameters =
                            members.AccessorParameters.TryGetValue(
                                accessor.Id,
                                out var parameters)
                                ? parameters
                                    .OrderBy(
                                        static item => item.Position)
                                    .ToArray()
                                : []
                    }).ToArray()
                    : []
        }).ToArray();

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                name,
                @interface = interfaceName,
                count = methodPayload.Length + propertyPayload.Length,
                limit,
                methods = methodPayload,
                properties = propertyPayload,
                scan = Scan(reader, selected.RetentionCapReached)
            },
            PayloadJson);

        return Success(request, payload, totalMs);
    }

    private static OperationResult Enum(
        OperationRequest request,
        double totalMs)
    {
        var fields = EnsureFields(request.Input, "name", "limit");
        var name = RequiredString(fields, "name");
        var limit = ReadLimit(fields);

        using var reader = KnowledgePackReader.OpenEmbedded();
        var selected = reader.Select(
            new KnowledgeSelection
            {
                IncludeEnums = true,
                IncludeEnumValues = true
            });

        var matchingEnumNames = selected.Enums
            .Where(item => EqualsName(item, name))
            .Concat(
                selected.EnumValues
                    .Where(item => EqualsName(item.Name, name))
                    .Select(static item => item.EnumName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        var enumPayload = matchingEnumNames.Select(enumName => new
        {
            name = enumName,
            values = selected.EnumValues
                .Where(item => EqualsName(item.EnumName, enumName))
                .OrderBy(static item => item.Name, StringComparer.Ordinal)
                .ToArray()
        }).ToArray();

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                query = name,
                count = enumPayload.Length,
                limit,
                enums = enumPayload,
                scan = Scan(reader, selected.RetentionCapReached)
            },
            PayloadJson);

        return Success(request, payload, totalMs);
    }


    private sealed record NavigationEdge(
        string From,
        string To,
        string Kind,
        string Member,
        int OwnerId,
        int? AccessorId,
        string? ReturnType);

    private static OperationResult Paths(
        OperationRequest request,
        double totalMs)
    {
        var fields = EnsureFields(
            request.Input,
            "start",
            "target",
            "maxDepth");
        var startText = RequiredString(fields, "start");
        var targetText = RequiredString(fields, "target");
        var maxDepth = ReadMaxDepth(fields);

        using var reader = KnowledgePackReader.OpenEmbedded();
        var selected = reader.Select(
            new KnowledgeSelection
            {
                IncludeInterfaces = true,
                IncludeMethods = true,
                IncludeProperties = true
            });

        if (selected.RetentionCapReached)
        {
            throw new KnowledgePackException(
                "knowledge_scan_budget_exhausted",
                "The navigation graph exceeded the retained-record budget.",
                suggestedActions: ["retry_with_default_knowledge_limits"]);
        }

        var implemented = reader.LoadImplementedInterfaces();
        var interfaces = selected.Interfaces.ToDictionary(
            static item => item.Name,
            StringComparer.OrdinalIgnoreCase);

        var implementations = implemented
            .GroupBy(
                static item => item.Coclass,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static item =>
                        item.Flags?.Contains(
                            "FDEFAULT",
                            StringComparison.OrdinalIgnoreCase) == true)
                    .ThenBy(
                        static item => item.Interface,
                        StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        string? Canonical(string name)
        {
            if (name.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(
                    "application",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Equals(
                    "illustrator.application",
                    StringComparison.OrdinalIgnoreCase))
            {
                name = "Application";
            }

            if (!interfaces.TryGetValue(name, out var info))
                return null;

            if (string.Equals(
                    info.Kind,
                    "COCLASS",
                    StringComparison.OrdinalIgnoreCase) &&
                implementations.TryGetValue(info.Name, out var impls) &&
                impls.Length > 0)
            {
                return impls[0].Interface;
            }

            return info.Name;
        }

        string? Referenced(string? returnType)
        {
            if (string.IsNullOrWhiteSpace(returnType))
                return null;

            var cleaned = returnType.Trim();
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var prefix in new[] { "PTR ", "SAFEARRAY ", "ARRAY " })
                {
                    if (!cleaned.StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    cleaned = cleaned[prefix.Length..].Trim();
                    changed = true;
                }
            }

            cleaned = cleaned.Trim('[', ']', ' ');
            return Canonical(cleaned);
        }

        var start = Canonical(startText);
        if (start is null)
        {
            throw new ArgumentException(
                $"Unknown start interface/coclass '{startText}'.");
        }

        var targetOwnerText = targetText;
        string? targetMember = null;
        var separator = targetText.LastIndexOf('.');
        if (separator > 0 && separator < targetText.Length - 1)
        {
            targetOwnerText = targetText[..separator];
            targetMember = targetText[(separator + 1)..];
        }

        var targetInterface = Canonical(targetOwnerText);
        if (targetInterface is null)
        {
            var memberName = targetMember ?? targetOwnerText;
            var candidateOwners = selected.Methods
                .Where(item => EqualsName(item.Name, memberName))
                .Select(item => Canonical(item.Interface))
                .Concat(
                    selected.Properties
                        .Where(item => EqualsName(item.Name, memberName))
                        .Select(item => Canonical(item.Interface)))
                .Where(static item => item is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray();

            if (candidateOwners.Length != 1)
            {
                var candidates = candidateOwners.Length == 0
                    ? "(none)"
                    : string.Join(", ", candidateOwners);
                throw new ArgumentException(
                    $"Cannot resolve target '{targetText}'. Candidate owners: " +
                    candidates + ".");
            }

            targetInterface = candidateOwners[0];
            targetMember ??= targetOwnerText;
        }

        var propertyIds = selected.Properties
            .Select(static item => item.Id)
            .ToHashSet();
        var graphMembers = reader.LoadMembers(
            new HashSet<int>(),
            propertyIds,
            includeAccessorParameters: false);

        if (reader.ScanBudgetExhausted)
        {
            throw new KnowledgePackException(
                "knowledge_scan_budget_exhausted",
                "The navigation graph exceeded the knowledge scan budget.",
                suggestedActions: ["request_a_narrower_path"]);
        }

        var edgesByOwner = new Dictionary<
            string,
            List<NavigationEdge>>(
                StringComparer.OrdinalIgnoreCase);

        void AddEdge(NavigationEdge edge)
        {
            if (!edgesByOwner.TryGetValue(edge.From, out var list))
            {
                list = [];
                edgesByOwner[edge.From] = list;
            }

            list.Add(edge);
        }

        foreach (var property in selected.Properties
                     .OrderBy(static item => item.Interface, StringComparer.Ordinal)
                     .ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            var owner = Canonical(property.Interface);
            if (owner is null ||
                !graphMembers.Accessors.TryGetValue(
                    property.Id,
                    out var accessors))
            {
                continue;
            }

            foreach (var accessor in accessors
                         .Where(static item =>
                             string.Equals(
                                 item.InvokeKind,
                                 "INVOKE_PROPERTYGET",
                                 StringComparison.Ordinal))
                         .OrderBy(static item => item.Id))
            {
                var destination = Referenced(accessor.ReturnType);
                if (destination is null)
                    continue;

                AddEdge(new NavigationEdge(
                    owner,
                    destination,
                    "property",
                    property.Name,
                    property.Id,
                    accessor.Id,
                    accessor.ReturnType));
            }
        }

        foreach (var method in selected.Methods
                     .OrderBy(static item => item.Interface, StringComparer.Ordinal)
                     .ThenBy(static item => item.Name, StringComparer.Ordinal))
        {
            var owner = Canonical(method.Interface);
            var destination = Referenced(method.ReturnType);
            if (owner is null || destination is null)
                continue;

            AddEdge(new NavigationEdge(
                owner,
                destination,
                "method",
                method.Name,
                method.Id,
                AccessorId: null,
                method.ReturnType));
        }

        var queue = new Queue<(string Owner, List<NavigationEdge> Path)>();
        queue.Enqueue((start, []));
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            start
        };
        List<NavigationEdge>? found = null;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(
                    current.Owner,
                    targetInterface,
                    StringComparison.OrdinalIgnoreCase))
            {
                found = current.Path;
                break;
            }

            if (current.Path.Count >= maxDepth ||
                !edgesByOwner.TryGetValue(current.Owner, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (!seen.Add(edge.To))
                    continue;

                var next = new List<NavigationEdge>(
                    current.Path.Count + 1);
                next.AddRange(current.Path);
                next.Add(edge);
                queue.Enqueue((edge.To, next));
            }
        }

        if (found is null)
        {
            throw new ArgumentException(
                $"No object-model path found from '{start}' to " +
                $"'{targetInterface}' within depth {maxDepth}.");
        }

        using var detailReader = KnowledgePackReader.OpenEmbedded();
        var details = detailReader.LoadMembers(
            found
                .Where(static edge => edge.Kind == "method")
                .Select(static edge => edge.OwnerId)
                .ToHashSet(),
            found
                .Where(static edge => edge.Kind == "property")
                .Select(static edge => edge.OwnerId)
                .ToHashSet(),
            includeAccessorParameters: true);

        object ProjectStep(NavigationEdge edge)
        {
            IReadOnlyList<KnowledgeParameterInfo> parameters;
            if (edge.Kind == "method")
            {
                parameters =
                    details.MethodParameters.TryGetValue(
                        edge.OwnerId,
                        out var methodParameters)
                        ? methodParameters
                            .OrderBy(static item => item.Position)
                            .ToArray()
                        : [];
            }
            else if (edge.AccessorId is { } accessorId)
            {
                parameters =
                    details.AccessorParameters.TryGetValue(
                        accessorId,
                        out var accessorParameters)
                        ? accessorParameters
                            .OrderBy(static item => item.Position)
                            .ToArray()
                        : [];
            }
            else
            {
                parameters = [];
            }

            return new
            {
                from = edge.From,
                to = edge.To,
                kind = edge.Kind,
                member = edge.Member,
                returnType = edge.ReturnType,
                parameters,
                signature = FormatNavigationSignature(
                    edge,
                    parameters)
            };
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                start,
                targetInterface,
                targetMember,
                maxDepth,
                steps = found.Select(ProjectStep).ToArray(),
                terminal = targetMember is null
                    ? null
                    : BuildTerminal(
                        targetInterface,
                        targetMember),
                note =
                    "Paths describe type navigation, not guaranteed runtime " +
                    "availability. Indexed properties and methods may require " +
                    "parameters and object state.",
                scan = Scan(reader, selected.RetentionCapReached)
            },
            PayloadJson);

        return Success(request, payload, totalMs);
    }

    private static object BuildTerminal(
        string interfaceName,
        string memberName)
    {
        using var reader = KnowledgePackReader.OpenEmbedded();
        var selected = reader.Select(
            new KnowledgeSelection
            {
                InterfaceName = interfaceName,
                SymbolName = memberName,
                IncludeMethods = true,
                IncludeProperties = true
            });
        var details = reader.LoadMembers(
            selected.Methods
                .Select(static item => item.Id)
                .ToHashSet(),
            selected.Properties
                .Select(static item => item.Id)
                .ToHashSet(),
            includeAccessorParameters: true);

        return new
        {
            query = memberName,
            interfaceFilter = interfaceName,
            methods = selected.Methods.Select(method => new
            {
                id = method.Id,
                name = method.Name,
                returnType = method.ReturnType,
                invokeKind = method.InvokeKind,
                parameters =
                    details.MethodParameters.TryGetValue(
                        method.Id,
                        out var parameters)
                        ? parameters
                            .OrderBy(static item => item.Position)
                            .ToArray()
                        : []
            }).ToArray(),
            properties = selected.Properties.Select(property => new
            {
                id = property.Id,
                name = property.Name,
                accessors =
                    details.Accessors.TryGetValue(
                        property.Id,
                        out var accessors)
                        ? accessors.Select(accessor => new
                        {
                            id = accessor.Id,
                            invokeKind = accessor.InvokeKind,
                            returnType = accessor.ReturnType,
                            parameters =
                                details.AccessorParameters.TryGetValue(
                                    accessor.Id,
                                    out var parameters)
                                    ? parameters
                                        .OrderBy(
                                            static item => item.Position)
                                        .ToArray()
                                    : []
                        }).ToArray()
                        : []
            }).ToArray()
        };
    }

    private static string FormatNavigationSignature(
        NavigationEdge edge,
        IReadOnlyList<KnowledgeParameterInfo> parameters)
    {
        var parameterText = string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var type = parameter.Type ?? "VT_VARIANT";
                var name = parameter.Name ?? $"arg{parameter.Position}";
                var optional =
                    parameter.Flags?.Contains(
                        "PARAMFLAG_FOPT",
                        StringComparison.OrdinalIgnoreCase) == true
                        ? "?"
                        : string.Empty;
                return $"{type} {name}{optional}";
            }));

        return edge.Kind == "method"
            ? $"{edge.From}.{edge.Member}({parameterText}): " +
              $"{edge.ReturnType ?? "VT_VARIANT"}"
            : $"{edge.From}.{edge.Member}" +
              (parameterText.Length == 0
                  ? string.Empty
                  : $"({parameterText})") +
              $": {edge.ReturnType ?? "VT_VARIANT"}";
    }

    private static int ReadMaxDepth(
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        const int defaultDepth = 8;
        const int maxDepth = 8;

        if (!fields.TryGetValue("maxDepth", out var element))
            return defaultDepth;

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value) ||
            value < 1 ||
            value > maxDepth)
        {
            throw new ArgumentException(
                $"'maxDepth' must be an integer between 1 and {maxDepth}.");
        }

        return value;
    }

    private static Dictionary<string, JsonElement> EnsureFields(
        JsonElement input,
        params string[] allowed) =>
        EnsureFields(input, allowNull: false, allowed);

    private static Dictionary<string, JsonElement> EnsureFields(
        JsonElement input,
        bool allowNull,
        params string[] allowed)
    {
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (allowNull)
                return new(StringComparer.Ordinal);

            throw new ArgumentException(
                "Knowledge operation input must be a JSON object.");
        }

        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Knowledge operation input must be a JSON object.");
        }

        var accepted = allowed.ToHashSet(StringComparer.Ordinal);
        var fields = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);

        foreach (var property in input.EnumerateObject())
        {
            if (!accepted.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"Unknown knowledge input field '{property.Name}'.");
            }

            if (!fields.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new ArgumentException(
                    $"Duplicate knowledge input field '{property.Name}'.");
            }
        }

        return fields;
    }

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = OptionalString(fields, name);
        if (value is null)
            throw new ArgumentException($"'{name}' is required.");

        return value;
    }

    private static string? OptionalString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var element))
            return null;

        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{name}' must be a string.");

        var value = element.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxQueryChars)
        {
            throw new ArgumentException(
                $"'{name}' must be 1-{MaxQueryChars} characters.");
        }

        return value;
    }

    private static int ReadLimit(
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("limit", out var element))
            return KnowledgeScanOptions.Default.DefaultLimit;

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value))
        {
            throw new ArgumentException("'limit' must be an integer.");
        }

        return KnowledgeScanOptions.Default.ResolveLimit(value);
    }

    private static bool Contains(string? value, string query) =>
        value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool EqualsName(string? value, string query) =>
        string.Equals(value, query, StringComparison.OrdinalIgnoreCase);

    private static object Scan(
        KnowledgePackReader reader,
        bool retentionCapReached) =>
        new
        {
            bytesScanned = reader.BytesScanned,
            recordsScanned = reader.RecordsScanned,
            recordsRetained = reader.RecordsRetained,
            scanBudgetExhausted = reader.ScanBudgetExhausted,
            retentionCapReached
        };

    private static OperationResult Success(
        OperationRequest request,
        JsonElement payload,
        double totalMs) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(payload),
            Timing = new OperationTiming(TotalMs: totalMs)
        };

    private static OperationResult Failure(
        OperationRequest request,
        OperationStatus status,
        string kind,
        string message,
        double totalMs,
        bool retryable = false,
        IReadOnlyList<string>? suggestedActions = null) =>
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
                Retryable = retryable,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = suggestedActions
            },
            Timing = new OperationTiming(TotalMs: totalMs)
        };
}
