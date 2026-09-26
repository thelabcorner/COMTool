using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorSessionScriptEvalTests
{
    [Fact]
    public async Task ScriptErrorWithNumericLineDoesNotEscapeThroughDynamicBinding()
    {
        var identity = Identity();
        const string response =
            """{"ok":false,"name":"Error","message":"boom","line":5}""";

        await using var session = new IllustratorSession(
            identity,
            new object(),
            (_, source, executionMode) =>
            {
                Assert.False(string.IsNullOrWhiteSpace(source));
                Assert.Equal(1, executionMode);
                return response;
            });

        using var input = JsonDocument.Parse(
            """{"kind":"code","source":"return 1;","effects":"unknown"}""");

        var result = await session.ExecuteAsync(
            Request(identity, input.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal("script_error", result.Error?.Kind);
        Assert.Equal(
            ExecutionState.Ambiguous,
            result.Error?.Execution);
        Assert.Contains(
            "wrapper line 5",
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptObjectEnvelopePreservesNestedJsonTypes()
    {
        var identity = Identity();
        const string response =
            """{"ok":true,"result":{"s":"true","n":2,"b":true,"a":[1,null,"x"]}}""";

        await using var session = new IllustratorSession(
            identity,
            new object(),
            (_, source, executionMode) =>
            {
                Assert.False(string.IsNullOrWhiteSpace(source));
                Assert.Equal(1, executionMode);
                return response;
            });

        using var input = JsonDocument.Parse(
            """{"kind":"code","source":"return {};","effects":"unknown"}""");

        var result = await session.ExecuteAsync(
            Request(identity, input.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.Equal("object", result.Result?.Kind);

        var value = Assert.IsType<JsonElement>(
            result.Result?.Value);

        Assert.Equal("true", value.GetProperty("s").GetString());
        Assert.Equal(2, value.GetProperty("n").GetInt32());
        Assert.True(value.GetProperty("b").GetBoolean());

        var array = value.GetProperty("a");
        Assert.Equal(3, array.GetArrayLength());
        Assert.Equal(1, array[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, array[1].ValueKind);
        Assert.Equal("x", array[2].GetString());
    }

    [Fact]
    public async Task DirectSessionRejectsConditionBearingRequest()
    {
        var identity = Identity();
        var scriptCalled = false;

        await using var session = new IllustratorSession(
            identity,
            new object(),
            (_, _, _) =>
            {
                scriptCalled = true;
                return """{"ok":true,"result":1}""";
            });

        using var input = JsonDocument.Parse(
            """{"kind":"code","source":"return 1;","effects":"unknown"}""");
        using var conditionInput = JsonDocument.Parse(
            """{"path":"Version"}""");

        var request = Request(
            identity,
            input.RootElement.Clone()) with
        {
            Preconditions =
            [
                new OperationCondition
                {
                    Id = "guard",
                    Source = new OperationConditionSource
                    {
                        Operation = "com.get",
                        Input =
                            conditionInput.RootElement.Clone()
                    },
                    Predicate =
                        new OperationConditionPredicate
                        {
                            Kind = "truthy"
                        }
                }
            ]
        };

        var result = await session.ExecuteAsync(request);

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.InvalidRequest,
            result.Status);
        Assert.Equal(
            "conditions_require_runtime_supervisor",
            result.Error?.Kind);
        Assert.False(scriptCalled);
    }

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = IllustratorAdapter.HostName,
            ProcessId = 1234,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-24T17:40:37Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = IllustratorAdapter.CurrentAdapterVersion,
            EndpointIdentity = IllustratorComInterop.ProgId
        };

    private static OperationRequest Request(
        HostTargetIdentity identity,
        JsonElement input) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "session-eval-test",
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Operation = "script.eval",
            Input = input
        };
}
