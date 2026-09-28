using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorMenuCommandTests
{
    [Fact]
    public void ExecuteDispatchesTheExactCommandStringOnce()
    {
        var calls = new List<(string Method, object?[] Args)>();

        var result = Execute(
            """{"command":"expandStyle"}""",
            (_, method, args) =>
            {
                calls.Add((method, args));
                return null;
            });

        Assert.True(result.Ok, result.Error?.Message);
        var dispatch = Assert.Single(calls);
        Assert.Equal("ExecuteMenuCommand", dispatch.Method);
        Assert.Equal("expandStyle", Assert.Single(dispatch.Args));
    }

    [Fact]
    public void SuccessfulDispatchReportsDispatchWithoutClaimingAnEffect()
    {
        var result = Execute(
            """{"command":"Live Pathfinder Divide"}""",
            (_, _, _) => null);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(TargetState.KnownChanged, result.TargetState);
        Assert.Null(result.Error);

        var payload = ReadPayload(result);
        Assert.Equal("Live Pathfinder Divide", payload.GetProperty("command").GetString());
        Assert.True(payload.GetProperty("dispatched").GetBoolean());
        Assert.False(payload.GetProperty("effectVerified").GetBoolean());
        Assert.Equal(
            "external_side_effect",
            payload.GetProperty("mutationClass").GetString());
        Assert.Equal(
            "operation_postconditions",
            payload.GetProperty("verifyWith").GetString());
    }

    [Fact]
    public void NonAsciiCommandStringsArePreservedForLocalizedHosts()
    {
        string? observed = null;

        var result = Execute(
            JsonSerializer.Serialize(new { command = "Tout sélectionner" }),
            (_, _, args) =>
            {
                observed = (string?)args[0];
                return null;
            });

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("Tout sélectionner", observed);
    }

    [Theory]
    [InlineData("""{"command":"selectall"}""", true)]
    [InlineData("""{"command":" copy"}""", false)]
    [InlineData("""{"command":"copy "}""", false)]
    [InlineData("""{"command":" "}""", false)]
    [InlineData("""{"command":""}""", false)]
    [InlineData("""{"command":"copy\r\ndelete"}""", false)]
    [InlineData("""{"command":"copy\";app.quit();"}""", false)]
    [InlineData("""{"command":"copy\\path"}""", false)]
    [InlineData("""{"command":123}""", false)]
    [InlineData("""{"command":null}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("""{"cmd":"copy"}""", false)]
    [InlineData("""{"command":"copy","fallback":"script.eval"}""", false)]
    [InlineData("\"copy\"", false)]
    public void RejectedShapesFailBeforeAnyHostCall(string json, bool expectedAccepted)
    {
        var dispatched = false;

        var result = Execute(
            json,
            (_, _, _) =>
            {
                dispatched = true;
                return null;
            });

        if (expectedAccepted)
        {
            Assert.True(result.Ok, result.Error?.Message);
            return;
        }

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_menu_command", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(TargetState.Known, result.TargetState);
        Assert.False(dispatched);
    }

    [Fact]
    public void DuplicateCommandFieldIsRejected()
    {
        using var input = JsonDocument.Parse(
            """{"command":"copy","command":"pasteFront"}""");

        var error = Assert.Throws<ArgumentException>(
            () => IllustratorMenuCommand.ParseRequest(
                input.RootElement));

        Assert.Contains("Duplicate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongCommandIsRejected()
    {
        var oversized = new string(
            'a',
            IllustratorMenuCommand.MaxCommandChars + 1);

        var result = Execute(
            JsonSerializer.Serialize(new { command = oversized }),
            (_, _, _) => throw new InvalidOperationException(
                "must not dispatch"));

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Contains(
            $"{IllustratorMenuCommand.MaxCommandChars} character limit",
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CommandAtTheLengthLimitIsAccepted()
    {
        var atLimit = new string('a', IllustratorMenuCommand.MaxCommandChars);

        var result = Execute(
            JsonSerializer.Serialize(new { command = atLimit }),
            (_, _, args) => args[0]);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(atLimit, ReadPayload(result).GetProperty("command").GetString());
    }

    [Fact]
    public void PostDispatchTransportFailureIsAmbiguousAndNeverRetried()
    {
        var invocations = 0;

        var result = Execute(
            """{"command":"preview"}""",
            (_, _, _) =>
            {
                invocations++;
                throw new HostAdapterException(
                    "com_failure",
                    "The remote procedure call failed.",
                    retryable: false,
                    ExecutionState.Ambiguous,
                    unchecked((int)0x800706BE));
            },
            "30.6.0");

        Assert.Equal(1, invocations);
        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "menu_command_outcome_ambiguous",
            result.Error?.Kind);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.Equal("0x800706BE", result.Error?.HResultHex);
        Assert.Contains(
            "inspect_mutation_ledger",
            result.Error?.SuggestedActions ?? []);

        var evidence = Assert.Single(result.Evidence!);
        Assert.Equal("menu_command.dispatch", evidence.Kind);
        Assert.Equal(
            "preview",
            evidence.Value.GetProperty("command").GetString());
        Assert.Equal(
            "30.6.0",
            evidence.Value.GetProperty("hostVersion").GetString());
        Assert.Equal(
            "possibly_executed",
            evidence.Value.GetProperty("dispatch").GetString());
    }

    [Fact]
    public void PreDispatchRejectionIsRetryableBusyAndProvesNothingRan()
    {
        var result = Execute(
            """{"command":"copy"}""",
            (_, _, _) => throw new HostAdapterException(
                "host_busy",
                "The object invoked has disconnected from its clients.",
                retryable: true,
                ExecutionState.NotStarted,
                unchecked((int)0x8001010A)));

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.HostBusy, result.Status);
        Assert.Equal(TargetState.Busy, result.TargetState);
        Assert.Equal("host_busy", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.True(result.Error?.Retryable);
        Assert.Equal(["retry_within_budget"], result.Error?.SuggestedActions);
        Assert.Equal(
            "not_executed",
            Assert.Single(result.Evidence!).Value
                .GetProperty("dispatch").GetString());
    }

    [Fact]
    public void SignatureFailureIsNotStartedAndSuggestsATypedRoute()
    {
        var result = Execute(
            """{"command":"copy"}""",
            (_, _, _) => throw new HostAdapterException(
                "com_signature_mismatch",
                "Missing method ExecuteMenuCommand.",
                retryable: false,
                ExecutionState.NotStarted));

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(TargetState.Known, result.TargetState);
        Assert.Equal("com_signature_mismatch", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.Contains(
            "use_a_typed_operation_instead_of_a_menu_command",
            result.Error?.SuggestedActions ?? []);
    }

    [Fact]
    public void FailedDispatchNeverFallsBackToAnotherRoute()
    {
        var invocations = 0;

        var result = Execute(
            """{"command":"noSuchCommand"}""",
            (_, _, _) =>
            {
                invocations++;
                throw new HostAdapterException(
                    "com_failure",
                    "Command is not available.",
                    retryable: false,
                    ExecutionState.Ambiguous);
            });

        Assert.Equal(1, invocations);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
    }

    [Fact]
    public void CapabilityIsTruthfulAndConsistentlyMutating()
    {
        var capability = Assert.Single(
            IllustratorOperations.Capabilities,
            entry =>
                entry.Name == IllustratorMenuCommand.ExecuteOperation);

        Assert.Equal("illustrator.menu.execute", capability.Name);
        Assert.Equal(MutationClass.ExternalSideEffect, capability.MutationClass);
        Assert.True(capability.Supported);
        Assert.Equal("illustrator", capability.Host);
    }

    [Fact]
    public async Task SessionRoutesTheOperationWithoutUsingTheScriptExecutor()
    {
        var identity = Identity();
        var scriptExecutorCalled = false;

        await using var session = new IllustratorSession(
            identity,
            new object(),
            (_, _, _) =>
            {
                scriptExecutorCalled = true;
                return "{}";
            });

        var result = await ExecuteOnSessionAsync(
            session,
            identity,
            """{"command":" copy"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.False(scriptExecutorCalled);
    }

    private static OperationResult Execute(
        string inputJson,
        Func<object, string, object?[], object?> invoker,
        string? hostVersion = null)
    {
        using var input = JsonDocument.Parse(inputJson);

        return IllustratorMenuCommand.Execute(
            new object(),
            Request(input.RootElement.Clone()),
            invoker,
            hostVersion);
    }

    private static async Task<OperationResult> ExecuteOnSessionAsync(
        IllustratorSession session,
        HostTargetIdentity identity,
        string inputJson)
    {
        using var input = JsonDocument.Parse(inputJson);

        // The session resolves the real COM application object. A request that
        // fails validation must never reach it, so the fake host object is
        // sufficient and the test stays host-free.
        return await session.ExecuteAsync(
            Request(input.RootElement.Clone()) with
            {
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0)
            });
    }

    private static OperationRequest Request(JsonElement input) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "menu-command-test",
            Operation = IllustratorMenuCommand.ExecuteOperation,
            Input = input
        };

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = IllustratorAdapter.HostName,
            ProcessId = 4242,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-26T10:00:00Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = IllustratorAdapter.CurrentAdapterVersion,
            EndpointIdentity = IllustratorComInterop.ProgId
        };

    private static JsonElement ReadPayload(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.HasValue);
        return result.Result.Value.Value;
    }
}
