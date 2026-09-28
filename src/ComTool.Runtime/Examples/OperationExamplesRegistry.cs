using System.Text;
using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Runtime.Examples;

/// <summary>
/// Checked-in, machine-readable operation examples. This registry is data-only:
/// it never executes an example. Every example is validated against the live
/// catalog and the production request-envelope validator before it can be
/// projected to a caller.
/// </summary>
public static class OperationExamplesRegistry
{
    public const string OperationName = "core.operation.examples";

    private static readonly JsonSerializerOptions PayloadJson =
        new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<OperationExample> Examples =
        BuildAndValidate();

    public static OperationResult Execute(
        OperationRequest request,
        double totalMs = 0)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var query = ParseQuery(request.Input);
            var matches = Query(query).ToArray();

            var payload = JsonSerializer.SerializeToElement(
                new
                {
                    query = new
                    {
                        operation = query.Operation,
                        tags = query.Tags,
                        words = query.Words,
                        limit = query.Limit
                    },
                    count = matches.Length,
                    totalExamples = Examples.Count,
                    coveredOperations = Examples
                        .Select(static example => example.Operation)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    catalogOperations =
                        BuiltInOperations.Catalog.Definitions.Count,
                    examples = matches.Select(Project).ToArray()
                },
                PayloadJson);

            return new OperationResult
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
        }
        catch (OperationExamplesException ex)
        {
            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = OperationStatus.InvalidRequest,
                TargetState = TargetState.Known,
                Error = new ProtocolError
                {
                    Kind = ex.Kind,
                    Message = ex.Message,
                    Retryable = false,
                    Execution = ExecutionState.NotStarted,
                    SuggestedActions = ["inspect_examples_query"]
                },
                Timing = new OperationTiming(TotalMs: totalMs)
            };
        }
    }

    public static IReadOnlyList<OperationExample> All => Examples;

    private static IEnumerable<OperationExample> Query(
        OperationExamplesQuery query)
    {
        query.Validate();

        return Examples
            .Where(example =>
                query.Operation is null ||
                string.Equals(
                    example.Operation,
                    query.Operation,
                    StringComparison.Ordinal))
            .Where(example =>
                query.Tags.All(tag =>
                    example.Tags.Contains(
                        tag,
                        StringComparer.OrdinalIgnoreCase)))
            .Where(example =>
            {
                if (query.Words.Count == 0)
                    return true;

                var text = Searchable(example);
                return query.Words.All(word =>
                    text.Contains(
                        word,
                        StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(
                static example => example.Operation,
                StringComparer.Ordinal)
            .ThenBy(
                static example => example.Id,
                StringComparer.Ordinal)
            .Take(query.Limit);
    }

    private static object Project(OperationExample example)
    {
        var definition =
            BuiltInOperations.Catalog.GetRequired(example.Operation);

        return new
        {
            id = example.Id,
            operation = example.Operation,
            title = example.Title,
            summary = example.Summary,
            tags = example.Tags,
            prerequisites = example.Prerequisites,
            notes = example.Notes,
            safety = new
            {
                mutationClass = definition.MutationClass,
                requiresTarget = definition.RequiresTarget,
                executionScope =
                    definition.Scope == OperationExecutionScope.Runtime
                        ? "runtime"
                        : "host",
                host = definition.Host,
                requiresLease = definition.RequiresLease,
                mutationResolution =
                    definition.MutationResolution ==
                        MutationResolutionMode.Fixed
                        ? "fixed"
                        : "declared_or_unknown"
            },
            request = OperationExampleRequests.Template(
                definition,
                example)
        };
    }

    private static OperationExamplesQuery ParseQuery(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Null or
            JsonValueKind.Undefined)
        {
            return new OperationExamplesQuery();
        }

        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new OperationExamplesException(
                "invalid_examples_query",
                "core.operation.examples input must be a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? operation = null;
        IReadOnlyList<string> tags = [];
        IReadOnlyList<string> words = [];
        var limit = OperationExamplesQuery.DefaultLimit;

        foreach (var property in input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new OperationExamplesException(
                    "invalid_examples_query",
                    $"Duplicate examples query field '{property.Name}'.");
            }

            switch (property.Name)
            {
                case "operation":
                    operation = ReadString(
                        property.Value,
                        "operation",
                        normalizeLower: false);
                    break;

                case "tags":
                    tags = ReadStrings(
                        property.Value,
                        "tags",
                        normalizeLower: true);
                    break;

                case "words":
                    words = ReadStrings(
                        property.Value,
                        "words",
                        normalizeLower: true);
                    break;

                case "limit":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out limit))
                    {
                        throw new OperationExamplesException(
                            "invalid_examples_query_limit",
                            "'limit' must be an integer.");
                    }
                    break;

                default:
                    throw new OperationExamplesException(
                        "invalid_examples_query",
                        $"Unknown examples query field '{property.Name}'.");
            }
        }

        var query = new OperationExamplesQuery
        {
            Operation = operation,
            Tags = tags,
            Words = words,
            Limit = limit
        };
        query.Validate();
        return query;
    }

    private static IReadOnlyList<string> ReadStrings(
        JsonElement value,
        string field,
        bool normalizeLower)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new OperationExamplesException(
                "invalid_examples_query_filter",
                $"'{field}' must be an array of strings.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            result.Add(
                ReadString(
                    item,
                    field,
                    normalizeLower));
        }

        return result;
    }

    private static string ReadString(
        JsonElement value,
        string field,
        bool normalizeLower)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new OperationExamplesException(
                "invalid_examples_query_filter",
                $"'{field}' must contain strings.");
        }

        var text = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length > OperationExamplesQuery.MaxFilterLength)
        {
            throw new OperationExamplesException(
                "invalid_examples_query_filter",
                $"'{field}' values must be 1-" +
                $"{OperationExamplesQuery.MaxFilterLength} characters.");
        }

        return normalizeLower
            ? text.ToLowerInvariant()
            : text;
    }

    private static string Searchable(OperationExample example)
    {
        var builder = new StringBuilder();
        builder.Append(example.Operation).Append(' ')
            .Append(example.Id).Append(' ')
            .Append(example.Title).Append(' ')
            .Append(example.Summary);

        foreach (var tag in example.Tags)
            builder.Append(' ').Append(tag);

        foreach (var note in example.Notes)
            builder.Append(' ').Append(note);

        return builder.ToString();
    }

    private static IReadOnlyList<OperationExample> BuildAndValidate()
    {
        var examples = BuildExamples();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var example in examples)
        {
            if (!ids.Add(example.Id))
            {
                throw new OperationExamplesException(
                    "duplicate_example_id",
                    $"Duplicate example id '{example.Id}'.");
            }

            if (!BuiltInOperations.Catalog.TryGet(
                    example.Operation,
                    out var definition))
            {
                throw new OperationExamplesException(
                    "example_operation_not_registered",
                    $"Example '{example.Id}' references unregistered " +
                    $"operation '{example.Operation}'.");
            }

            if (example.Input.ValueKind != JsonValueKind.Object)
            {
                throw new OperationExamplesException(
                    "invalid_example_input",
                    $"Example '{example.Id}' input must be a JSON object.");
            }

            if (example.Tags.Count > OperationExamplesQuery.MaxTags ||
                example.Tags.Any(tag =>
                    string.IsNullOrWhiteSpace(tag) ||
                    tag.Length > OperationExamplesQuery.MaxFilterLength ||
                    !string.Equals(
                        tag,
                        tag.ToLowerInvariant(),
                        StringComparison.Ordinal)))
            {
                throw new OperationExamplesException(
                    "invalid_example_tags",
                    $"Example '{example.Id}' has invalid tags.");
            }

            foreach (var prerequisite in example.Prerequisites)
            {
                if (!BuiltInOperations.Catalog.TryGet(
                        prerequisite.Operation,
                        out _))
                {
                    throw new OperationExamplesException(
                        "example_prerequisite_not_registered",
                        $"Example '{example.Id}' prerequisite " +
                        $"'{prerequisite.Operation}' is not registered.");
                }
            }

            _ = OperationExampleRequests.Build(definition, example);
        }

        return examples;
    }

    private static IReadOnlyList<OperationExample> BuildExamples() =>
    [
        Example(
            "catalog-list",
            "core.operations.list",
            "List the authoritative operation catalog",
            "Discover current operation names and runtime-owned safety metadata.",
            "{}",
            ["discovery", "catalog"]),

        Example(
            "catalog-describe",
            "core.operation.describe",
            "Describe one registered operation",
            "Read target, lease, scope, and mutation policy for one operation.",
            """{"name":"script.eval"}""",
            ["discovery", "catalog"]),

        Example(
            "examples-search",
            OperationName,
            "Find script-related examples",
            "Query this machine-readable example registry without executing anything.",
            """{"tags":["script"],"limit":8}""",
            ["discovery", "examples"]),

        Example(
            "artifact-describe",
            "core.artifact.describe",
            "Describe one runtime artifact",
            "Read opaque artifact metadata without exposing runtime filesystem paths.",
            """{"artifactId":"art_0123456789abcdef0123456789abcdef"}""",
            ["artifact", "read"],
            notes:
            [
                "Replace the placeholder artifact id with an id returned by an opted-in producer result."
            ]),

        Example(
            "artifact-read-range",
            "core.artifact.read",
            "Read a bounded artifact range",
            "Retrieve an explicit byte range as base64 while staying below transport frame limits.",
            """
            {
              "artifactId":"art_0123456789abcdef0123456789abcdef",
              "offset":0,
              "length":65536
            }
            """,
            ["artifact", "read"],
            notes:
            [
                "Whole reads above 512 KiB are refused; request explicit bounded ranges instead."
            ]),

        Example(
            "knowledge-search",
            "knowledge.search",
            "Search the Illustrator COM inventory",
            "Find COM methods, properties, interfaces, and enum values from the embedded inventory.",
            """{"query":"DoScript","limit":10}""",
            ["knowledge", "com"]),

        Example(
            "knowledge-symbol-close",
            "knowledge.symbol",
            "Inspect Document.Close",
            "Read the exact COM signature and parameter domain before invoking a method.",
            """{"interface":"Document","name":"Close","limit":10}""",
            ["knowledge", "signature", "com"]),

        Example(
            "knowledge-path-document-close",
            "knowledge.paths",
            "Find the COM navigation path to Document.Close",
            "Resolve the shortest typed object-model route from Application to a target interface or member.",
            """
            {
              "start":"Application",
              "target":"Document.Close",
              "maxDepth":8
            }
            """,
            ["knowledge", "path", "com"],
            notes:
            [
                "Paths describe type navigation, not guaranteed runtime object availability."
            ]),

        Example(
            "script-validate-expression",
            "script.validate",
            "Preflight an ES3 expression",
            "Run advisory static analysis before spending a host call.",
            """{"kind":"expression","source":"6*7"}""",
            ["script", "validation"]),

        Example(
            "target-attach",
            "core.target.attach",
            "Attach the exact discovered generation",
            "Explicitly bind one strong running target without acquiring ownership.",
            "{}",
            ["target", "lifecycle", "read"],
            prerequisites:
            [
                new("core.targets.list", "Discover and select the exact strong target generation first.")
            ],
            notes:
            [
                "Use the request target field returned by core.targets.list; attach never substitutes a restarted generation."
            ]),

        Example(
            "target-launch",
            "core.target.launch",
            "Launch Illustrator explicitly",
            "Activate one host generation through the authenticated STA launch worker with durable ownership provenance.",
            """
            {
              "host":"illustrator",
              "progId":"Illustrator.Application",
              "expectedHostVersion":null,
              "launchTimeoutMs":60000,
              "existingInstance":"fail",
              "arguments":[]
            }
            """,
            ["target", "lifecycle", "mutation"],
            notes:
            [
                "Launch is never implicit and is never silently retried after an ambiguous dispatch.",
                "Use return_preexisting_without_ownership only when explicitly willing to bind an already-running generation without ownership."
            ]),

        Example(
            "target-status",
            "core.target.status",
            "Read target status",
            "Read host and active-document status for one discovered target.",
            "{}",
            ["target", "read"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first.")
            ]),

        Example(
            "com-get-version",
            "com.get",
            "Read Illustrator version through COM",
            "Read one strict dotted COM property path without mutation.",
            """{"path":"Version"}""",
            ["com", "read"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first.")
            ]),

        Example(
            "script-eval-expression",
            "script.eval",
            "Evaluate one ExtendScript expression",
            "Execute a small expression through the canonical host worker path.",
            """{"kind":"expression","source":"6*7","effects":"unknown"}""",
            ["script", "execution"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first."),
                new("core.target.lease.acquire", "script.eval requires an exclusive target lease.")
            ],
            notes:
            [
                "Arbitrary script cannot self-certify as read-only; the example keeps effects unknown."
            ]),

        Example(
            "layer-opacity-set",
            "illustrator.layer.setOpacity",
            "Set one explicitly selected layer opacity",
            "Use the fixed typed mutation surface rather than a caller-supplied COM path.",
            """
            {
              "document":{"name":"Poster.ai"},
              "layer":{"uuid":"replace-with-layer-uuid"},
              "value":72.5
            }
            """,
            ["illustrator", "layer", "mutation"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running Illustrator generation first."),
                new("core.target.lease.acquire", "Typed layer mutations require an exclusive target lease.")
            ],
            notes:
            [
                "Exactly one document selector and exactly one layer selector are required.",
                "Name selectors that match more than one document or layer fail before mutation instead of silently retargeting."
            ]),

        Example(
            "artboard-rect-set",
            "illustrator.artboard.setRect",
            "Set one explicitly selected artboard rectangle",
            "Set [left, top, right, bottom] through a fixed typed property contract.",
            """
            {
              "document":{"index":0},
              "artboard":{"index":0},
              "value":[0,792,612,0]
            }
            """,
            ["illustrator", "artboard", "mutation"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running Illustrator generation first."),
                new("core.target.lease.acquire", "Typed artboard mutations require an exclusive target lease.")
            ],
            notes:
            [
                "Coordinates must be finite; right must exceed left and top must exceed bottom."
            ]),

        Example(
            "action-run",
            "illustrator.action.run",
            "Run a named Illustrator action",
            "Dispatch one explicit action/action-set pair and wait within the caller-derived bounded budget.",
            """{"name":"Export for Web","actionSet":"My Actions","dialogs":false}""",
            ["action", "mutation"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first."),
                new("core.target.lease.acquire", "Action execution is an external side effect.")
            ],
            notes:
            [
                "Replace the example action and action-set names with names that exist in the target Illustrator installation.",
                "A completed dispatch does not replace operation-specific postcondition checks."
            ]),

        Example(
            "menu-copy",
            "illustrator.menu.execute",
            "Dispatch one Illustrator menu command",
            "Execute exactly one menu command string without fallback routing.",
            """{"command":"copy"}""",
            ["menu", "mutation"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first."),
                new("core.target.lease.acquire", "Menu commands are fixed external side effects.")
            ],
            notes:
            [
                "Menu command strings are version-sensitive; prefer a typed operation when one exists."
            ]),

        Example(
            "plugin-message",
            "plugin.message",
            "Send a bounded plug-in message",
            "Send data to a named Illustrator plug-in and receive SHA-provenanced response bytes.",
            """{"plugin":"ExamplePlugin","selector":"rpc/v1","input":"{\"x\":1}"}""",
            ["plugin", "mutation"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first."),
                new("core.target.lease.acquire", "Plug-in messaging is an external side effect.")
            ],
            notes:
            [
                "Replace ExamplePlugin and selector with the installed plug-in's documented contract."
            ]),

        Example(
            "plugin-debug-discover",
            "plugin.debug.diagnostics",
            "Discover AIPDebug endpoint provenance",
            "Use the host-thread diagnostic lane to bind AIPDebug endpoint provenance to the exact Illustrator generation.",
            """{"plugin":"AIPDebug","action":"discover","transport":"com"}""",
            ["plugin", "debug", "read"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first.")
            ],
            notes:
            [
                "Requires the AIPDebug plug-in; diagnostics themselves do not require a target lease."
            ]),

        Example(
            "plugin-debug-direct-info",
            "plugin.debug.diagnostics",
            "Read AIPDebug diagnostics through verified VectorIPC",
            "After host-thread discovery, use the packaged native helper against the exact runtime-owned endpoint and Illustrator PID.",
            """
            {
              "plugin":"AIPDebug",
              "action":"info",
              "transport":"ipc"
            }
            """,
            ["plugin", "debug", "read", "vectoripc"],
            prerequisites:
            [
                new("plugin.debug.diagnostics", "Run action 'discover' with transport 'com' first so the worker owns fresh endpoint provenance.")
            ],
            notes:
            [
                "The caller does not choose the native server: the worker binds the endpoint to the strong target generation and aipdebugctl re-verifies the named-pipe peer PID."
            ]),

        Example(
            "debugger-status",
            "debug.session.status",
            "Inspect persistent debugger session state",
            "Observe debugger ownership and provenance without opening or rebinding a session.",
            "{}",
            ["script", "debug", "read"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact running target generation first.")
            ]),

        Example(
            "watch-com-property",
            "watch.condition",
            "Watch a read-only COM condition",
            "Poll a registered read-only source under one strong target generation and a bounded deadline.",
            """
            {
              "source":{
                "operation":"com.get",
                "input":{"path":"ActiveDocument.Saved"}
              },
              "predicate":{"kind":"equals","expected":true},
              "timeoutMs":30000,
              "pollIntervalMs":500
            }
            """,
            ["watch", "com", "read"],
            prerequisites:
            [
                new("core.targets.list", "Choose the exact target generation the watch must remain pinned to.")
            ])
    ];

    private static OperationExample Example(
        string id,
        string operation,
        string title,
        string summary,
        string input,
        IReadOnlyList<string> tags,
        IReadOnlyList<OperationExampleReference>? prerequisites = null,
        IReadOnlyList<string>? notes = null) =>
        new()
        {
            Id = id,
            Operation = operation,
            Title = title,
            Summary = summary,
            Input = OperationExampleInput.Parse(input),
            Tags = tags,
            Prerequisites = prerequisites ?? [],
            Notes = notes ?? []
        };
}
