using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Hosts.Illustrator;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Focused proof for the general COM write/invoke surface: strict bounded input
/// handling, the typed JSON-to-COM argument domain, and the mutation dispatch
/// contract that keeps a general put/call from ever being replayed or treated
/// as a read.
/// </summary>
public sealed class IllustratorComMutationBridgeTests
{
    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerException = unchecked((int)0x80010105);

    [Fact]
    public void GenericSurfaceIsFixedExternalSideEffect()
    {
        // The only classification the generic bridge can carry. A general
        // property put is not idempotent merely because repeating a scalar
        // write often looks idempotent.
        Assert.Equal(
            MutationClass.ExternalSideEffect,
            IllustratorComMutationBridge.GenericMutationClass);
    }

    [Theory]
    [InlineData("com.set")]
    [InlineData("com.call")]
    public void GenericSurfaceIsDistinctFromTheReadOnlyCallSurface(
        string operation)
    {
        // com.call.read stays the lease-free, allowlisted, read-only surface.
        // The generic surface must not be a second name for it.
        Assert.NotEqual("com.call.read", operation);
        Assert.NotEqual("com.get", operation);
    }

    [Fact]
    public void SetInputRejectsCallerSuppliedEffectsHint()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(
                """{"path":"Items[0].Name","value":"x","effects":"read_only"}"""),
            out var error);

        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Equal("unknown_input_field", error!.Kind);
    }

    [Fact]
    public void CallInputRejectsCallerSuppliedEffectsHint()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input(
                """{"path":"Items[0].GetByName","args":[],"effects":"read_only"}"""),
            out var error);

        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Equal("unknown_input_field", error!.Kind);
    }

    [Fact]
    public void CallInputGivesNoReadOnlyStatusToAnAllowlistedReadMethod()
    {
        // GetActiveArtboardIndex is on com.call.read's allowlist. Accepting it
        // here proves only that the generic bridge has no read allowlist to
        // consult; the operation's class stays the fixed external side effect
        // above, so nothing about a known-read member escapes that policy.
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Items[0].GetActiveArtboardIndex","args":[]}"""),
            out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal("GetActiveArtboardIndex", plan!.Member);
        Assert.Equal(
            MutationClass.ExternalSideEffect,
            IllustratorComMutationBridge.GenericMutationClass);
    }

    [Fact]
    public void SetInputRejectsUnknownField()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"Name","value":"x","extra":1}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("unknown_input_field", error!.Kind);
    }

    [Fact]
    public void CallInputRejectsUnknownField()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Name","extra":1}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("unknown_input_field", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsNonObjectInput()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""["Name","x"]"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_input", error!.Kind);
    }

    [Fact]
    public void SetInputRequiresPath()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"value":"x"}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Fact]
    public void SetInputRequiresValue()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"Name"}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_value", error!.Kind);
    }

    [Fact]
    public void SetPlanSplitsDottedPathAndKeepsFinalMember()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"Items[1].Name","value":"Board A"}"""),
            out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal("Name", plan!.Member);
        Assert.Equal("Items[1].Name", plan.Path);
        Assert.Equal(2, plan.Segments.Length);
        Assert.Equal("Items", plan.Segments[0].Name);
        Assert.Equal(1, plan.Segments[0].Index);
        Assert.Null(plan.Segments[1].Index);
    }

    [Theory]
    [InlineData("""{"path":"Items[0].Name[0]","value":"x"}""")]
    [InlineData("""{"path":"Name[0]","value":"x"}""")]
    public void SetInputRejectsIndexOnFinalSegment(string json)
    {
        // Legacy refused an indexed final segment for set as well; the index
        // has no meaning for a property put and must not be reinterpreted.
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(json),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Fact]
    public void CallInputRejectsIndexOnFinalSegment()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Items[0].GetByName[2]","args":[]}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Theory]
    [InlineData("Items..Name")]
    [InlineData("Items.")]
    [InlineData(".")]
    public void SetInputRejectsEmptyPathSegment(string path)
    {
        // Stricter than legacy, which silently collapsed empty segments and
        // could therefore resolve a path the caller did not write.
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(JsonSerializer.Serialize(new
            {
                path,
                value = "x"
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Theory]
    [InlineData("Items[0].na me")]
    [InlineData("Items[0].9lives")]
    [InlineData("Items[0].na*me")]
    [InlineData("Items[0].")]
    public void SetInputRejectsInvalidSegmentSyntax(string path)
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(JsonSerializer.Serialize(new
            {
                path,
                value = "x"
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsNegativeIndex()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"Items[-1].Name","value":"x"}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsIndexThatWouldOverflowOneBasedTranslation()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"Items[2147483647].Name","value":"x"}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_path", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsPathLongerThanRuntimeBound()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(JsonSerializer.Serialize(new
            {
                path = new string('a', 513),
                value = "x"
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("com_path_too_long", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsPathDeeperThanSegmentBound()
    {
        var path = string.Join(
            '.',
            Enumerable.Repeat(
                "Item",
                IllustratorComMutationBridge.MaxPathSegments + 1));

        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(JsonSerializer.Serialize(new
            {
                path,
                value = "x"
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("com_path_too_deep", error!.Kind);
    }

    [Fact]
    public void SetInputConvertsTypedValueDomain()
    {
        Assert.Null(ConvertedComValue("""{"path":"Name","value":null}"""));
        Assert.Equal(true, ConvertedComValue("""{"path":"Name","value":true}"""));
        Assert.Equal(false, ConvertedComValue("""{"path":"Name","value":false}"""));
        Assert.Equal(7, ConvertedComValue("""{"path":"Name","value":7}"""));
        Assert.Equal(
            2147483648L,
            ConvertedComValue("""{"path":"Name","value":2147483648}"""));
        Assert.Equal(1.5d, ConvertedComValue("""{"path":"Name","value":1.5}"""));
        Assert.Equal("x", ConvertedComValue("""{"path":"Name","value":"x"}"""));
    }

    [Fact]
    public void SetInputConvertsArrayValueToClrArray()
    {
        var value = Assert.IsType<object?[]>(
            ConvertedComValue("""{"path":"RulerOrigin","value":[0,1.5,"a",null,true]}"""));

        Assert.Equal(5, value.Length);
        Assert.Equal(0, value[0]);
        Assert.Equal(1.5d, value[1]);
        Assert.Equal("a", value[2]);
        Assert.Null(value[3]);
        Assert.Equal(true, value[4]);
    }

    [Fact]
    public void SetInputRejectsJsonObjectValue()
    {
        // Refusing objects outright keeps named-argument and COM-handle
        // marshaling out of the generic surface entirely.
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"Name","value":{"handle":1}}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_value", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsOversizedStringValue()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(JsonSerializer.Serialize(new
            {
                path = "Name",
                value = new string(
                    'x',
                    IllustratorComMutationBridge.MaxStringLength + 1)
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("com_value_too_large", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsValueNestedBeyondRuntimeBound()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input("""{"path":"RulerOrigin","value":[[[[[[1]]]]]]}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("com_value_too_deep", error!.Kind);
    }

    [Fact]
    public void SetInputRejectsOversizedArrayValue()
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(JsonSerializer.Serialize(new
            {
                path = "RulerOrigin",
                value = Enumerable
                    .Range(0, IllustratorComMutationBridge.MaxArrayElements + 1)
                    .ToArray()
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("com_value_too_large", error!.Kind);
    }

    [Fact]
    public void CallInputDefaultsToEmptyArgumentList()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Items[0].Save"}"""),
            out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Empty(plan!.Args);
    }

    [Fact]
    public void CallInputRejectsNonArrayArgs()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Items[0].GetByName","args":"Layer 7"}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_args", error!.Kind);
    }

    [Fact]
    public void CallInputPreservesArgumentOrderAndTypes()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Items[0].Move","args":[1,"a",true,null,[2]]}"""),
            out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal(5, plan!.Args.Length);
        Assert.Equal(1, plan.Args[0]);
        Assert.Equal("a", plan.Args[1]);
        Assert.Equal(true, plan.Args[2]);
        Assert.Null(plan.Args[3]);
        Assert.Equal(new object?[] { 2 }, plan.Args[4]);
    }

    [Fact]
    public void CallInputRejectsTooManyArguments()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input(JsonSerializer.Serialize(new
            {
                path = "Items[0].Move",
                args = Enumerable
                    .Range(0, IllustratorComMutationBridge.MaxCallArguments + 1)
                    .ToArray()
            })),
            out var error);

        Assert.Null(plan);
        Assert.Equal("com_args_too_many", error!.Kind);
    }

    [Fact]
    public void CallInputRejectsJsonObjectArgument()
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input("""{"path":"Items[0].Move","args":[{"x":1}]}"""),
            out var error);

        Assert.Null(plan);
        Assert.Equal("invalid_com_value", error!.Kind);
    }

    [Fact]
    public void ApplySetWritesTheResolvedProperty()
    {
        var root = new FakeRoot();
        var plan = SetPlan("""{"path":"Items[1].Name","value":"Board A"}""");

        RunSta(() => IllustratorComMutationBridge.ApplySet(root, plan));

        Assert.Equal(2, root.Items.LastRequestedItemIndex);
        Assert.Equal("Board A", root.Items.Items[1].Name);
    }

    [Fact]
    public void ApplySetWritesDirectPropertyWithoutIndexing()
    {
        var root = new FakeRoot();
        var plan = SetPlan("""{"path":"Items[0].Name","value":"Solo"}""");

        RunSta(() => IllustratorComMutationBridge.ApplySet(root, plan));

        Assert.Equal(1, root.Items.LastRequestedItemIndex);
        Assert.Equal("Solo", root.Items.Items[0].Name);
    }

    [Fact]
    public void ApplySetTreatsHostRejectionAsNotStartedAndRetryable()
    {
        // A definitely-rejected call cannot have mutated, so it stays
        // retryable and never enters the ambiguous path.
        var root = new FakeRoot
        {
            Rejecting = new FakeRejectingTarget()
        };

        var plan = SetPlan("""{"path":"Rejecting.Name","value":"x"}""");

        var error = Assert.Throws<HostAdapterException>(
            () => RunSta(
                () => IllustratorComMutationBridge.ApplySet(root, plan)));

        Assert.Equal("host_busy", error.Kind);
        Assert.True(error.Retryable);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
    }

    [Fact]
    public void ApplySetTreatsAcceptedDispatchFailureAsAmbiguous()
    {
        // Once the host may have accepted the put, a transport failure cannot
        // prove whether the document changed. It must never look retryable.
        var root = new FakeRoot
        {
            Ambiguous = new FakeAmbiguousTarget()
        };

        var plan = SetPlan("""{"path":"Ambiguous.Name","value":"x"}""");

        var error = Assert.Throws<HostAdapterException>(
            () => RunSta(
                () => IllustratorComMutationBridge.ApplySet(root, plan)));

        Assert.Equal("host_server_fault", error.Kind);
        Assert.False(error.Retryable);
        Assert.Equal(ExecutionState.Ambiguous, error.Execution);
    }

    [Fact]
    public void ApplySetTreatsUnknownMemberAsNotStartedSignatureMismatch()
    {
        var root = new FakeRoot();
        var plan = SetPlan("""{"path":"Missing.Name","value":"x"}""");

        var error = Assert.Throws<HostAdapterException>(
            () => RunSta(
                () => IllustratorComMutationBridge.ApplySet(root, plan)));

        Assert.Equal("com_member_not_found", error.Kind);
        Assert.Equal(ExecutionState.Started, error.Execution);
    }

    [Fact]
    public void InvokeCallInvokesTheResolvedMethod()
    {
        var root = new FakeRoot();
        var plan = CallPlan(
            """{"path":"Items[0].GetByName","args":["Layer 7"]}""");

        var result = RunSta(
            () => IllustratorComMutationBridge.InvokeCall(root, plan));

        Assert.Equal("found:Layer 7", result);
        Assert.Equal(1, root.Items.LastRequestedItemIndex);
    }

    [Fact]
    public void InvokeCallProjectsVoidResultAsNull()
    {
        var root = new FakeRoot();
        var plan = CallPlan("""{"path":"Items[0].Save","args":[]}""");

        var result = RunSta(
            () => IllustratorComMutationBridge.InvokeCall(root, plan));

        Assert.Null(result);
        Assert.Equal(1, root.Items.Items[0].SaveCallCount);
    }

    [Fact]
    public void InvokeCallProjectsNumericResult()
    {
        var root = new FakeRoot();
        var plan = CallPlan(
            """{"path":"Items[0].GetActiveArtboardIndex","args":[]}""");

        var result = RunSta(
            () => IllustratorComMutationBridge.InvokeCall(root, plan));

        Assert.Equal(2, result);
    }

    [Fact]
    public void InvokeCallTreatsHostRejectionAsNotStartedAndRetryable()
    {
        var root = new FakeRoot
        {
            Rejecting = new FakeRejectingTarget()
        };

        var plan = CallPlan("""{"path":"Rejecting.Flush","args":[]}""");

        var error = Assert.Throws<HostAdapterException>(
            () => RunSta(
                () => IllustratorComMutationBridge.InvokeCall(root, plan)));

        Assert.Equal("host_busy", error.Kind);
        Assert.True(error.Retryable);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
    }

    [Fact]
    public void InvokeCallTreatsAcceptedDispatchFailureAsAmbiguous()
    {
        var root = new FakeRoot
        {
            Ambiguous = new FakeAmbiguousTarget()
        };

        var plan = CallPlan("""{"path":"Ambiguous.Flush","args":[]}""");

        var error = Assert.Throws<HostAdapterException>(
            () => RunSta(
                () => IllustratorComMutationBridge.InvokeCall(root, plan)));

        Assert.Equal("host_server_fault", error.Kind);
        Assert.False(error.Retryable);
        Assert.Equal(ExecutionState.Ambiguous, error.Execution);
    }

    [Fact]
    public void ProjectResultDescribesDateTimeAsRoundTripString()
    {
        var moment = new DateTime(2026, 9, 26, 7, 30, 0, DateTimeKind.Utc);

        Assert.Equal(
            moment.ToString("O"),
            IllustratorComMutationBridge.ProjectResult(moment));
    }

    [Fact]
    public void ProjectResultBoundsNestedArrayExpansion()
    {
        object? value = new object?[]
        {
            new object?[]
            {
                new object?[]
                {
                    new object?[]
                    {
                        new object?[] { 1 }
                    }
                }
            }
        };

        for (var i = 0; i < 3; i++)
            value = new object?[] { value };

        object? projected =
            IllustratorComMutationBridge.ProjectResult(value);

        for (var depth = 0;
             depth < IllustratorComMutationBridge.MaxResultDepth;
             depth++)
        {
            var items = Assert.IsType<List<object?>>(projected);
            projected = Assert.Single(items);
        }

        var summary =
            Assert.IsType<Dictionary<string, object?>>(projected);
        Assert.Equal(typeof(Array).FullName, summary["_comType"]);
        Assert.Equal(1, summary["_length"]);
    }

    [Fact]
    public void ProjectResultKeepsScalarAndEnumValuesTyped()
    {
        Assert.Equal("x", IllustratorComMutationBridge.ProjectResult("x"));
        Assert.Equal(3, IllustratorComMutationBridge.ProjectResult(3));
        Assert.Equal(1.5d, IllustratorComMutationBridge.ProjectResult(1.5d));
        Assert.Equal(
            true,
            IllustratorComMutationBridge.ProjectResult(true));
        Assert.Equal(
            DayOfWeek.Monday.ToString(),
            IllustratorComMutationBridge.ProjectResult(DayOfWeek.Monday));
        Assert.Null(IllustratorComMutationBridge.ProjectResult(null));
    }

    private static object? ConvertedComValue(string json)
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(json),
            out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        return plan!.ComValue;
    }

    private static IllustratorComMutationBridge.SetPlan SetPlan(
        string json)
    {
        var plan = IllustratorComMutationBridge.ParseSetInput(
            Input(json),
            out var error);

        Assert.Null(error);
        return plan!;
    }

    private static IllustratorComMutationBridge.CallPlan CallPlan(
        string json)
    {
        var plan = IllustratorComMutationBridge.ParseCallInput(
            Input(json),
            out var error);

        Assert.Null(error);
        return plan!;
    }

    private static JsonElement Input(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// COM mutation dispatch requires an STA apartment. The xunit runner uses
    /// thread-pool threads, so the dispatch assertions run on a dedicated STA
    /// thread rather than asserting against a mocked transport.
    /// </summary>
    private static void RunSta(Action action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure =
            null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure =
                    System.Runtime.ExceptionServices
                        .ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test thread did not finish.");

        failure?.Throw();
    }

    private static T RunSta<T>(Func<T> action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure =
            null;
        T? result = default;
        var completed = false;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
                completed = true;
            }
            catch (Exception ex)
            {
                failure =
                    System.Runtime.ExceptionServices
                        .ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test thread did not finish.");

        failure?.Throw();
        if (!completed)
            throw new InvalidOperationException(
                "STA test function did not complete.");

        return result!;
    }

    private sealed class FakeRoot
    {
        public FakeCollection Items { get; } = new(
            new FakeTarget(),
            new FakeTarget());

        public FakeRejectingTarget? Rejecting { get; init; }

        public FakeAmbiguousTarget? Ambiguous { get; init; }
    }

    private sealed class FakeCollection
    {
        public FakeCollection(params FakeTarget[] items)
        {
            Items = items;
        }

        public FakeTarget[] Items { get; }

        public int? LastRequestedItemIndex { get; private set; }

        public object Item(int index)
        {
            LastRequestedItemIndex = index;
            return Items[index - 1];
        }
    }

    private sealed class FakeTarget
    {
        public string? Name { get; set; }

        public int SaveCallCount { get; private set; }

        public int GetActiveArtboardIndex() => 2;

        public string GetByName(string name) => $"found:{name}";

        public void Save() => SaveCallCount++;
    }

    private sealed class FakeRejectingTarget
    {
        public string Name
        {
            get => string.Empty;
            set => throw new COMException(
                "Call was rejected by callee.",
                RpcECallRejected);
        }

        public void Flush() =>
            throw new COMException(
                "Call was rejected by callee.",
                RpcECallRejected);
    }

    private sealed class FakeAmbiguousTarget
    {
        public string Name
        {
            get => string.Empty;
            set => throw new COMException(
                "Server exception.",
                RpcEServerException);
        }

        public void Flush() =>
            throw new COMException(
                "Server exception.",
                RpcEServerException);
    }
}
