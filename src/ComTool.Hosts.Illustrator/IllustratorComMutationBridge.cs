using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Runtime-owned general COM write/invoke surface behind <c>com.set</c> and
/// <c>com.call</c>.
///
/// This reaches the legacy bridge's <c>set</c>/<c>call</c> reach without its
/// semantics. Legacy dispatched both through the same retrying transport used
/// for reads, so a lost or failed response could silently replay a mutation
/// and neither entry point carried first-class safety metadata. Here both
/// operations are fixed external side effects, require an exclusive target
/// lease, and are never retried once the host may have accepted the dispatch.
///
/// Classification is fixed per operation, never per member, and that is the
/// point: this bridge cannot certify itself. A caller cannot reach a read-only
/// class, drop the lease requirement, or narrow the mutation class through an
/// argument, a member name, or a declared <c>effects</c> hint — the input
/// parser refuses unknown fields outright, and the catalog entry is
/// <see cref="MutationResolutionMode.Fixed"/> so caller input is never
/// consulted. Proof that one member is a pure query belongs to
/// <c>com.call.read</c>'s explicit allowlist or to a future narrow typed
/// operation, never to this bridge.
///
/// The bridge performs no ExtendScript dispatch and has no fallback path to
/// <c>script.eval</c>. A path, member, or value it cannot represent is
/// refused as a request error rather than smuggled into script text, which
/// would bypass both the typed argument domain and the mutation dispatch
/// contract.
/// </summary>
internal static class IllustratorComMutationBridge
{
    internal const string SetOperation = "com.set";
    internal const string CallOperation = "com.call";

    /// <summary>
    /// Bounded input limits. General COM paths, values, and argument lists are
    /// caller-authored, so every dimension a caller can grow is capped rather
    /// than trusted.
    /// </summary>
    internal const int MaxPathLength =
        ComAutomationPath.MaxPathLength;
    internal const int MaxPathSegments =
        ComAutomationPath.MaxPathSegments;
    internal const int MaxCallArguments = 32;
    internal const int MaxStringLength = 8192;
    internal const int MaxValueDepth = 4;
    internal const int MaxArrayElements = 256;
    internal const int MaxResultDepth = 4;

    private static readonly string[] SetInputFields = ["path", "value"];
    private static readonly string[] CallInputFields = ["path", "args"];

    /// <summary>
    /// The only mutation class either generic operation can carry. A general
    /// property put is not assumed idempotent merely because repeating a scalar
    /// write often looks idempotent, and an arbitrary method call is never
    /// assumed observational.
    /// </summary>
    internal static MutationClass GenericMutationClass =>
        MutationClass.ExternalSideEffect;

    /// <summary>
    /// Validated property-put request. Parsing touches no COM object, so every
    /// rejection here is provably <see cref="ExecutionState.NotStarted"/>.
    /// </summary>
    internal sealed record SetPlan(
        ComAutomationPathSegment[] Segments,
        string Member,
        string Path,
        object? ComValue,
        JsonElement Value);

    /// <summary>
    /// Validated method-invocation request, with arguments already converted
    /// into their COM argument domain.
    /// </summary>
    internal sealed record CallPlan(
        ComAutomationPathSegment[] Segments,
        string Member,
        string Path,
        object?[] Args);

    internal sealed record ComMutationError(string Kind, string Message);

    internal static SetPlan? ParseSetInput(
        JsonElement input,
        out ComMutationError? error)
    {
        error = null;

        if (!TryReadStrictInput(input, SetInputFields, out error))
            return null;

        if (!TryGetRequiredString(input, "path", out var path, out error))
            return null;

        if (!TryParsePath(path, out var segments, out error))
            return null;

        if (!input.TryGetProperty("value", out var value))
        {
            error = new(
                "invalid_com_value",
                "'value' is required.");
            return null;
        }

        var elementBudget = MaxArrayElements;

        if (!TryConvertValue(
                value,
                "value",
                depth: 0,
                ref elementBudget,
                out var comValue,
                out error))
            return null;

        return new SetPlan(
            segments,
            segments[^1].Name,
            path,
            comValue,
            value.Clone());
    }

    internal static CallPlan? ParseCallInput(
        JsonElement input,
        out ComMutationError? error)
    {
        error = null;

        if (!TryReadStrictInput(input, CallInputFields, out error))
            return null;

        if (!TryGetRequiredString(input, "path", out var path, out error))
            return null;

        if (!TryParsePath(path, out var segments, out error))
            return null;

        var args = Array.Empty<object?>();

        if (input.TryGetProperty("args", out var argsElement) &&
            argsElement.ValueKind is not JsonValueKind.Null and
                not JsonValueKind.Undefined)
        {
            if (argsElement.ValueKind != JsonValueKind.Array)
            {
                error = new(
                    "invalid_com_args",
                    "'args' must be a JSON array.");
                return null;
            }

            var count = argsElement.GetArrayLength();

            if (count > MaxCallArguments)
            {
                error = new(
                    "com_args_too_many",
                    $"COM calls are limited to {MaxCallArguments} arguments; {count} were supplied.");
                return null;
            }

            args = new object?[count];
            var elementBudget = MaxArrayElements;
            var position = 0;

            foreach (var argument in argsElement.EnumerateArray())
            {
                if (!TryConvertValue(
                        argument,
                        $"args[{position}]",
                        depth: 0,
                        ref elementBudget,
                        out args[position],
                        out error))
                    return null;

                position++;
            }
        }

        return new CallPlan(segments, segments[^1].Name, path, args);
    }

    /// <summary>
    /// Walks the caller's path to the owning object and performs exactly one
    /// property put. Every transient COM object obtained along the way is
    /// released when the walk finishes, and the bridge root is never released.
    /// </summary>
    internal static void ApplySet(object root, SetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(plan);

        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);

        try
        {
            var parent = ResolveParent(
                root,
                plan.Segments,
                plan.Path,
                owned);

            IllustratorComInterop.PutMutationProperty(
                parent,
                plan.Member,
                plan.ComValue);
        }
        finally
        {
            ReleaseAll(owned);
        }
    }

    /// <summary>
    /// Walks the caller's path to the owning object and performs exactly one
    /// method invocation, then projects the result into the plain value
    /// domain.
    /// </summary>
    internal static object? InvokeCall(object root, CallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(plan);

        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);

        try
        {
            var parent = ResolveParent(
                root,
                plan.Segments,
                plan.Path,
                owned);

            var result = IllustratorComInterop.InvokeMutationMethod(
                parent,
                plan.Member,
                plan.Args);

            return ProjectResult(result);
        }
        finally
        {
            ReleaseAll(owned);
        }
    }

    /// <summary>
    /// Projects a COM return value into the plain JSON value domain.
    ///
    /// A returned COM object is described by its type name only. Reading
    /// summary properties would mean extra COM dispatches after a mutation
    /// dispatch, widening the window in which a transport failure is
    /// ambiguous, so the mutation surface never does it.
    /// </summary>
    internal static object? ProjectResult(object? value, int depth = 0)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
                return value;
        }

        if (value is DateTime dateTime)
            return dateTime.ToString("O", CultureInfo.InvariantCulture);

        if (value is Array array)
        {
            if (depth >= MaxResultDepth)
            {
                return new Dictionary<string, object?>
                {
                    ["_comType"] = typeof(Array).FullName,
                    ["_length"] = array.Length
                };
            }

            var lower = array.GetLowerBound(0);
            var limit = Math.Min(array.Length, MaxArrayElements);
            var items = new List<object?>(limit);

            for (var i = 0; i < limit; i++)
                items.Add(ProjectResult(array.GetValue(lower + i), depth + 1));

            return items;
        }

        if (Marshal.IsComObject(value))
        {
            try
            {
                return new Dictionary<string, object?>
                {
                    ["_comType"] = value.GetType().FullName
                        ?? value.GetType().Name
                };
            }
            finally
            {
                IllustratorComInterop.Release(value);
            }
        }

        if (value is Enum)
            return value.ToString();

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static object ResolveParent(
        object root,
        IReadOnlyList<ComAutomationPathSegment> segments,
        string path,
        HashSet<object> owned)
    {
        // Traversal reads only. Every step is a property read or a collection
        // Item lookup, so the read retry budget is safe here: nothing in this
        // loop can mutate the target.
        var current = root;

        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            var next = LateGet(current, segment.Name, path);

            if (next is null)
            {
                throw new HostAdapterException(
                    "com_null_intermediate",
                    $"COM path '{path}' resolved '{segment.Name}' to null.",
                    retryable: false,
                    ExecutionState.Started);
            }

            if (segment.Index is { } index)
            {
                var indexed = LateItem(next, index, path);

                if (indexed is null)
                {
                    throw new HostAdapterException(
                        "com_null_indexed_item",
                        $"COM path '{path}' index [{index}] resolved to null.",
                        retryable: false,
                        ExecutionState.Started);
                }

                Track(owned, indexed, root);
                next = indexed;
            }

            Track(owned, next, root);
            current = next;
        }

        return current;
    }

    private static void Track(
        HashSet<object> owned,
        object value,
        object root)
    {
        if (Marshal.IsComObject(value) &&
            !ReferenceEquals(value, root))
        {
            owned.Add(value);
        }
    }

    private static void ReleaseAll(HashSet<object> owned)
    {
        foreach (var value in owned)
            IllustratorComInterop.Release(value);

        owned.Clear();
    }

    private static object? LateGet(
        object target,
        string member,
        string path) =>
        IllustratorComInterop.RetryRead(
            () => InvokeMemberCore(
                target,
                member,
                BindingFlags.GetProperty,
                [],
                path));

    private static object? LateItem(
        object target,
        int index,
        string path) =>
        InvokeMemberCore(
            target,
            "Item",
            BindingFlags.InvokeMethod,
            [index + 1],
            path);

    private static object? InvokeMemberCore(
        object target,
        string member,
        BindingFlags flags,
        object?[] args,
        string path)
    {
        try
        {
            return target.GetType().InvokeMember(
                member,
                flags | BindingFlags.OptionalParamBinding,
                binder: null,
                target,
                args,
                modifiers: null,
                culture: CultureInfo.InvariantCulture,
                namedParameters: null);
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is COMException comException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(comException)
                .Throw();

            throw;
        }
        catch (Exception ex)
            when (ex is MissingMemberException or
                MissingMethodException or
                TargetException or
                ArgumentException)
        {
            throw new HostAdapterException(
                "com_member_not_found",
                $"COM path '{path}' could not resolve member '{member}': {ex.Message}",
                retryable: false,
                ExecutionState.Started,
                ex.HResult,
                ex);
        }
    }

    private static bool TryReadStrictInput(
        JsonElement input,
        IReadOnlyList<string> allowedFields,
        out ComMutationError? error)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            error = new(
                "invalid_input",
                "Operation input must be a JSON object.");
            return false;
        }

        foreach (var field in input.EnumerateObject())
        {
            if (!allowedFields.Contains(field.Name))
            {
                // An unrecognized field is refused rather than ignored. This is
                // what stops a caller smuggling a mutation hint such as
                // "effects" into a fixed-classification operation.
                error = new(
                    "unknown_input_field",
                    $"Unknown input field '{field.Name}'.");
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement input,
        string field,
        out string value,
        out ComMutationError? error)
    {
        if (!input.TryGetProperty(field, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = string.Empty;
            error = new(
                "invalid_com_path",
                $"'{field}' must be a non-empty string.");
            return false;
        }

        value = element.GetString() ?? string.Empty;
        error = null;
        return true;
    }

    private static bool TryParsePath(
        string path,
        out ComAutomationPathSegment[] segments,
        out ComMutationError? error)
    {
        try
        {
            segments = ComAutomationPath.ParseMutation(path);
            error = null;
            return true;
        }
        catch (ComAutomationPathException ex)
        {
            segments = [];
            error = new(ex.Kind, ex.Message);
            return false;
        }
    }

    private static bool TryConvertValue(
        JsonElement value,
        string pointer,
        int depth,
        ref int elementBudget,
        out object? converted,
        out ComMutationError? error)
    {
        converted = null;
        error = null;

        if (depth > MaxValueDepth)
        {
            error = new(
                "com_value_too_deep",
                $"COM value '{pointer}' exceeds the runtime nesting bound of {MaxValueDepth}.");
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;

            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;

                if (text.Length > MaxStringLength)
                {
                    error = new(
                        "com_value_too_large",
                        $"COM value '{pointer}' exceeds the runtime string bound of {MaxStringLength} characters.");
                    return false;
                }

                converted = text;
                return true;

            case JsonValueKind.True:
                converted = true;
                return true;

            case JsonValueKind.False:
                converted = false;
                return true;

            case JsonValueKind.Number:
                if (value.TryGetInt32(out var int32Value))
                {
                    converted = int32Value;
                    return true;
                }

                if (value.TryGetInt64(out var int64Value))
                {
                    converted = int64Value;
                    return true;
                }

                var doubleValue = value.GetDouble();

                if (double.IsFinite(doubleValue))
                {
                    converted = doubleValue;
                    return true;
                }

                error = new(
                    "invalid_com_value",
                    $"COM value '{pointer}' must be a finite number.");
                return false;

            case JsonValueKind.Array:
                var arrayLength = value.GetArrayLength();
                if (arrayLength > elementBudget)
                {
                    error = new(
                        "com_value_too_large",
                        $"COM value '{pointer}' exceeds the runtime element bound of {MaxArrayElements}.");
                    return false;
                }

                elementBudget -= arrayLength;
                var items = new List<object?>(arrayLength);

                foreach (var item in value.EnumerateArray())
                {
                    if (!TryConvertValue(
                            item,
                            $"{pointer}[{items.Count}]",
                            depth + 1,
                            ref elementBudget,
                            out var itemValue,
                            out error))
                        return false;

                    items.Add(itemValue);
                }

                converted = items.ToArray();
                return true;

            default:
                // JSON objects are refused outright. Accepting them would mean
                // guessing at a named-argument or COM-object marshaling
                // contract the type library does not state, and an
                // object-shaped value is the obvious smuggling route for a
                // caller-authored handle.
                error = new(
                    "invalid_com_value",
                    $"COM value '{pointer}' must be null, a string, a boolean, a number, or an array of those; JSON objects are not a COM argument.");
                return false;
        }
    }
}
