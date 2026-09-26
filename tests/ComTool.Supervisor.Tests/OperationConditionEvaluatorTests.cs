using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor.Tests;

public sealed class OperationConditionEvaluatorTests
{
    [Fact]
    public void EqualsUsesDeepJsonValueEquality()
    {
        var predicate = new OperationConditionPredicate
        {
            Kind = "equals",
            Expected = Value(
                """{"a":1,"nested":{"x":true,"y":[1,2]}}""")
        };

        var actual = Value(
            """{"nested":{"y":[1,2],"x":true},"a":1}""");

        Assert.True(
            OperationConditionEvaluator.EvaluatePredicate(
                predicate,
                actual));
    }

    [Fact]
    public void EqualsPreservesJsonTypeIdentity()
    {
        var predicate = new OperationConditionPredicate
        {
            Kind = "equals",
            Expected = Value("\"1\"")
        };

        Assert.False(
            OperationConditionEvaluator.EvaluatePredicate(
                predicate,
                Value("1")));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("-0.0", false)]
    [InlineData("1", true)]
    [InlineData("-2.5", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"x\"", true)]
    [InlineData("[]", true)]
    [InlineData("{}", true)]
    public void TruthyHasPortableProtocolSemantics(
        string json,
        bool expected)
    {
        var predicate = new OperationConditionPredicate
        {
            Kind = "truthy"
        };

        Assert.Equal(
            expected,
            OperationConditionEvaluator.EvaluatePredicate(
                predicate,
                Value(json)));
    }

    [Fact]
    public void NullPredicatesAreExact()
    {
        Assert.True(
            OperationConditionEvaluator.EvaluatePredicate(
                new OperationConditionPredicate
                {
                    Kind = "is_null"
                },
                Value("null")));

        Assert.True(
            OperationConditionEvaluator.EvaluatePredicate(
                new OperationConditionPredicate
                {
                    Kind = "not_null"
                },
                Value("\"null\"")));
    }

    [Fact]
    public void ScriptEvalCannotBeUsedAsConditionSource()
    {
        var target = Target(
            "script.eval",
            "com.get");

        var condition = Condition(
            "guard",
            "script.eval",
            """{"kind":"expression","source":"1"}""",
            "truthy");

        var error =
            Assert.Throws<OperationConditionPolicyException>(
                () => OperationConditionEvaluator.ValidateSources(
                    [condition],
                    target,
                    "precondition"));

        Assert.Equal(
            "condition_source_not_read_only",
            error.Kind);
    }

    [Fact]
    public void ConditionSourceMustBeAdvertisedByTarget()
    {
        var target = Target(
            "core.target.status");

        var condition = Condition(
            "guard",
            "com.get",
            """{"path":"Version"}""",
            "truthy");

        var error =
            Assert.Throws<OperationConditionPolicyException>(
                () => OperationConditionEvaluator.ValidateSources(
                    [condition],
                    target,
                    "precondition"));

        Assert.Equal(
            "condition_source_not_advertised",
            error.Kind);
    }

    [Fact]
    public void FixedReadOnlyAdvertisedHostOperationIsAccepted()
    {
        var target = Target(
            "com.get");

        OperationConditionEvaluator.ValidateSources(
            [
                Condition(
                    "version",
                    "com.get",
                    """{"path":"Version"}""",
                    "truthy")
            ],
            target,
            "precondition");
    }

    private static OperationCondition Condition(
        string id,
        string operation,
        string inputJson,
        string predicate)
    {
        using var input =
            JsonDocument.Parse(inputJson);

        return new OperationCondition
        {
            Id = id,
            Source = new OperationConditionSource
            {
                Operation = operation,
                Input = input.RootElement.Clone()
            },
            Predicate =
                new OperationConditionPredicate
                {
                    Kind = predicate
                }
        };
    }

    private static ProtocolValue Value(
        string json)
    {
        using var document =
            JsonDocument.Parse(json);

        return ProtocolValue.From(
            document.RootElement);
    }

    private static HostTargetDescriptor Target(
        params string[] capabilities)
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 1234,
            ProcessStartedAt =
                DateTimeOffset.Parse(
                    "2026-09-24T17:40:37Z"),
            ExecutablePath =
                @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "test",
            EndpointIdentity =
                "Illustrator.Application"
        };

        return new HostTargetDescriptor
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Running = true,
            Capabilities = capabilities
                .Select(
                    name => new CapabilityDescriptor
                    {
                        Name = name,
                        Version = "1",
                        MutationClass =
                            name == "script.eval"
                                ? MutationClass.Unknown
                                : MutationClass.ReadOnly,
                        Supported = true,
                        Host = "illustrator"
                    })
                .ToArray()
        };
    }
}
