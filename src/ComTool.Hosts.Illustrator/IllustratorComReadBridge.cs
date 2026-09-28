using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using Microsoft.CSharp.RuntimeBinder;
using RuntimeBinder = Microsoft.CSharp.RuntimeBinder.Binder;

namespace ComTool.Hosts.Illustrator;

internal static class IllustratorComReadBridge
{
    private static readonly ConcurrentDictionary<string, Func<object, object?>> Getters =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<CallKey, Func<object, object?[], object?>> Invokers =
        new();

    private static readonly HashSet<string> ReadOnlyMethods =
        new(StringComparer.Ordinal)
        {
            "GetActiveArtboardIndex",
            "GetNumSelectedArtboard",
            "GetByName",
            "GetPageItemFromUuid",
            "IsViewClippedToArtboards"
        };

    public static JsonElement Get(object root, string path)
    {
        var segments = ComAutomationPath.ParseRead(path);
        object current = root;
        var ownsCurrent = false;

        try
        {
            foreach (var segment in segments)
            {
                var next = LateGet(current, segment.Name);
                ReleaseIfOwnedAndDifferent(current, ownsCurrent, next);

                current = next
                    ?? throw new HostAdapterException(
                        "com_null_intermediate",
                        $"COM path '{path}' resolved '{segment.Name}' to null.",
                        retryable: false,
                        ExecutionState.Started);

                ownsCurrent = IsOwnedTransient(current, root);

                if (segment.Index is not null)
                {
                    var indexed = LateCall(
                        current,
                        "Item",
                        [segment.Index.Value + 1]);

                    ReleaseIfOwnedAndDifferent(
                        current,
                        ownsCurrent,
                        indexed);

                    current = indexed
                        ?? throw new HostAdapterException(
                            "com_null_indexed_item",
                            $"COM path '{path}' index [{segment.Index.Value}] resolved to null.",
                            retryable: false,
                            ExecutionState.Started);

                    ownsCurrent = IsOwnedTransient(current, root);
                }
            }

            var plain = ToPlain(current, root, depth: 0);
            ownsCurrent = false; // ToPlain owns/released transient final COM values.
            return JsonSerializer.SerializeToElement(plain);
        }
        finally
        {
            ReleaseIfOwned(current, ownsCurrent);
        }
    }

    public static JsonElement CallRead(
        object root,
        string path,
        JsonElement argsElement)
    {
        var segments = ComAutomationPath.ParseRead(path);
        var final = segments[^1];

        if (final.Index is not null)
            throw new ArgumentException(
                "Read-call target cannot include an index on the method segment.",
                nameof(path));

        if (!ReadOnlyMethods.Contains(final.Name))
        {
            throw new ArgumentException(
                $"COM method '{final.Name}' is not in the runtime-owned read-only allowlist.",
                nameof(path));
        }

        var args = ParseArguments(argsElement);

        object current = root;
        var ownsCurrent = false;
        var retainedParents = new List<object>();

        try
        {
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                var next = LateGet(current, segment.Name);

                RetainIfOwnedAndDifferent(
                    retainedParents,
                    current,
                    ownsCurrent,
                    next);

                current = next
                    ?? throw new HostAdapterException(
                        "com_null_intermediate",
                        $"COM path '{path}' resolved '{segment.Name}' to null.",
                        retryable: false,
                        ExecutionState.Started);

                ownsCurrent = IsOwnedTransient(current, root);

                if (segment.Index is not null)
                {
                    var indexed = LateCall(
                        current,
                        "Item",
                        [segment.Index.Value + 1]);

                    RetainIfOwnedAndDifferent(
                        retainedParents,
                        current,
                        ownsCurrent,
                        indexed);

                    current = indexed
                        ?? throw new HostAdapterException(
                            "com_null_indexed_item",
                            $"COM path '{path}' index [{segment.Index.Value}] resolved to null.",
                            retryable: false,
                            ExecutionState.Started);

                    ownsCurrent = IsOwnedTransient(current, root);
                }
            }

            var result = LateCall(current, final.Name, args);
            var plain = ToPlain(result, root, depth: 0);
            return JsonSerializer.SerializeToElement(plain);
        }
        finally
        {
            ReleaseOperationChain(
                current,
                ownsCurrent,
                retainedParents);
        }
    }

    private static object? LateGet(object target, string member)
    {
        try
        {
            var getter = Getters.GetOrAdd(member, BuildGetter);
            return IllustratorComInterop.RetryRead(
                () => getter(target));
        }
        catch (RuntimeBinderException ex)
        {
            throw MemberFailure(member, ex);
        }
    }

    private static object? LateCall(
        object target,
        string member,
        object?[] args)
    {
        try
        {
            var invoker = Invokers.GetOrAdd(
                new CallKey(member, args.Length),
                BuildInvoker);

            return IllustratorComInterop.RetryRead(
                () => invoker(target, args));
        }
        catch (RuntimeBinderException ex)
        {
            throw MemberFailure(member, ex);
        }
    }

    private static Func<object, object?> BuildGetter(string member)
    {
        var target = Expression.Parameter(typeof(object), "target");

        var binder = RuntimeBinder.GetMember(
            CSharpBinderFlags.None,
            member,
            typeof(IllustratorComReadBridge),
            [
                CSharpArgumentInfo.Create(
                    CSharpArgumentInfoFlags.None,
                    name: null)
            ]);

        var body = Expression.Dynamic(
            binder,
            typeof(object),
            target);

        return Expression
            .Lambda<Func<object, object?>>(body, target)
            .Compile();
    }

    private static Func<object, object?[], object?> BuildInvoker(CallKey key)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var args = Expression.Parameter(typeof(object[]), "args");

        var dynamicArguments = new List<Expression>(key.ArgumentCount + 1)
        {
            target
        };

        var argumentInfo = new List<CSharpArgumentInfo>(key.ArgumentCount + 1)
        {
            CSharpArgumentInfo.Create(
                CSharpArgumentInfoFlags.None,
                name: null)
        };

        for (var i = 0; i < key.ArgumentCount; i++)
        {
            dynamicArguments.Add(
                Expression.ArrayIndex(args, Expression.Constant(i)));

            argumentInfo.Add(
                CSharpArgumentInfo.Create(
                    CSharpArgumentInfoFlags.None,
                    name: null));
        }

        var binder = RuntimeBinder.InvokeMember(
            CSharpBinderFlags.None,
            key.Member,
            typeArguments: null,
            typeof(IllustratorComReadBridge),
            argumentInfo);

        var body = Expression.Dynamic(
            binder,
            typeof(object),
            dynamicArguments);

        return Expression
            .Lambda<Func<object, object?[], object?>>(
                body,
                target,
                args)
            .Compile();
    }

    private static object?[] ParseArguments(JsonElement argsElement)
    {
        if (argsElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (argsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException(
                "'args' must be a JSON array.");

        return argsElement
            .EnumerateArray()
            .Select(JsonToClr)
            .ToArray();
    }

    private static object? JsonToClr(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var int32) => int32,
            JsonValueKind.Number when value.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value
                .EnumerateArray()
                .Select(JsonToClr)
                .ToArray(),
            _ => throw new ArgumentException(
                "COM read-call arguments support only JSON null, strings, booleans, numbers, and arrays.")
        };

    private static object? ToPlain(
        object? value,
        object root,
        int depth)
    {
        if (value is null ||
            value is string ||
            value is bool ||
            value is byte ||
            value is sbyte ||
            value is short ||
            value is ushort ||
            value is int ||
            value is uint ||
            value is long ||
            value is ulong ||
            value is float ||
            value is double ||
            value is decimal)
        {
            return value;
        }

        if (value is DateTime dateTime)
            return dateTime.ToString("O");

        if (value is Array array)
        {
            var result = new object?[array.Length];
            var lower = array.GetLowerBound(0);

            for (var i = 0; i < array.Length; i++)
                result[i] = ToPlain(array.GetValue(lower + i), root, depth + 1);

            return result;
        }

        if (!Marshal.IsComObject(value))
            return Convert.ToString(value);

        if (ReferenceEquals(value, root))
        {
            return new Dictionary<string, object?>
            {
                ["_comType"] = "application",
                ["borrowedRoot"] = true
            };
        }

        try
        {
            if (depth >= 3)
            {
                return new Dictionary<string, object?>
                {
                    ["_comType"] = value.GetType().FullName ?? value.GetType().Name
                };
            }

            var summary = new Dictionary<string, object?>
            {
                ["_comType"] = value.GetType().FullName ?? value.GetType().Name
            };

            TryAddPrimitive(summary, value, "Name", "name");
            TryAddPrimitive(summary, value, "Count", "count");
            TryAddPrimitive(summary, value, "Length", "length");
            TryAddPrimitive(summary, value, "Version", "version");
            TryAddPrimitive(summary, value, "Uuid", "uuid");
            TryAddPrimitive(summary, value, "PageItemType", "pageItemType");

            return summary;
        }
        finally
        {
            IllustratorComInterop.Release(value);
        }
    }

    private static void TryAddPrimitive(
        IDictionary<string, object?> summary,
        object target,
        string member,
        string key)
    {
        try
        {
            var value = LateGet(target, member);

            if (value is null ||
                value is string ||
                value is bool ||
                value is byte ||
                value is sbyte ||
                value is short ||
                value is ushort ||
                value is int ||
                value is uint ||
                value is long ||
                value is ulong ||
                value is float ||
                value is double ||
                value is decimal)
            {
                summary[key] = value;
                return;
            }

            if (Marshal.IsComObject(value))
                IllustratorComInterop.Release(value);
        }
        catch (HostAdapterException ex) when (!ex.Retryable)
        {
            // Common summary properties are opportunistic only.
        }
    }


    private static bool IsOwnedTransient(
        object value,
        object root) =>
        Marshal.IsComObject(value) &&
        !ReferenceEquals(value, root);

    private static void ReleaseIfOwned(
        object value,
        bool owned)
    {
        if (owned)
            IllustratorComInterop.Release(value);
    }

    private static void ReleaseIfOwnedAndDifferent(
        object value,
        bool owned,
        object? next)
    {
        if (owned && !ReferenceEquals(value, next))
            IllustratorComInterop.Release(value);
    }

    private static void RetainIfOwnedAndDifferent(
        ICollection<object> retained,
        object value,
        bool owned,
        object? next)
    {
        if (!owned ||
            ReferenceEquals(value, next) ||
            retained.Any(existing => ReferenceEquals(existing, value)))
            return;

        retained.Add(value);
    }

    private static void ReleaseOperationChain(
        object current,
        bool ownsCurrent,
        IReadOnlyList<object> retained)
    {
        var released = new HashSet<object>(
            ReferenceEqualityComparer.Instance);

        if (ownsCurrent && released.Add(current))
            IllustratorComInterop.Release(current);

        for (var i = retained.Count - 1; i >= 0; i--)
        {
            var value = retained[i];
            if (released.Add(value))
                IllustratorComInterop.Release(value);
        }
    }

    private static HostAdapterException MemberFailure(
        string member,
        Exception inner) =>
        new(
            "com_member_not_found",
            $"COM member '{member}' could not be resolved: {inner.Message}",
            retryable: false,
            ExecutionState.Started,
            inner.HResult,
            inner);

    private readonly record struct CallKey(
        string Member,
        int ArgumentCount);
}
