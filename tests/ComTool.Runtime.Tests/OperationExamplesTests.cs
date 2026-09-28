using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Examples;

namespace ComTool.Runtime.Tests;

public sealed class OperationExamplesTests
{
    [Fact]
    public void CheckedInCorpusValidatesAgainstLiveCatalog()
    {
        var examples = OperationExamplesRegistry.All;

        Assert.Equal(23, examples.Count);
        Assert.Equal(
            examples.Count,
            examples.Select(static example => example.Id)
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var example in examples)
        {
            var definition =
                BuiltInOperations.Catalog.GetRequired(example.Operation);
            var request =
                OperationExampleRequests.Build(definition, example);

            Assert.Equal(example.Operation, request.Operation);
            Assert.Equal(
                definition.RequiresTarget,
                request.Target is not null);
            Assert.Equal(
                definition.RequiresLease,
                request.Policy?.LeaseId is not null);
        }
    }

    [Fact]
    public void ScriptEvalTemplateDerivesTargetAndLeaseFromCatalog()
    {
        var example = Assert.Single(
            OperationExamplesRegistry.All,
            item => item.Id == "script-eval-expression");
        var definition =
            BuiltInOperations.Catalog.GetRequired(example.Operation);

        var request = OperationExampleRequests.Build(
            definition,
            example);

        Assert.NotNull(request.Target);
        Assert.Equal(
            OperationExampleRequests.PlaceholderTargetId,
            request.Target!.Id);
        Assert.NotNull(request.Policy);
        Assert.Equal(
            OperationExampleRequests.PlaceholderLeaseId,
            request.Policy!.LeaseId);
    }

    [Fact]
    public void QueryFiltersByTagsAndProjectsDerivedSafety()
    {
        using var input = JsonDocument.Parse(
            """{"tags":["script"],"limit":32}""");
        var result = OperationExamplesRegistry.Execute(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "examples-query-test",
                Operation = OperationExamplesRegistry.OperationName,
                Input = input.RootElement.Clone()
            });

        Assert.True(result.Ok, result.Error?.Message);
        var payload = result.Result!.Value!.Value;
        var examples =
            payload.GetProperty("examples").EnumerateArray().ToArray();

        Assert.NotEmpty(examples);
        Assert.All(
            examples,
            item =>
            {
                var tags = item.GetProperty("tags")
                    .EnumerateArray()
                    .Select(static tag => tag.GetString())
                    .ToArray();
                Assert.Contains("script", tags);
            });

        var scriptEval = Assert.Single(
            examples,
            item =>
                item.GetProperty("operation").GetString() ==
                "script.eval");
        var safety = scriptEval.GetProperty("safety");
        Assert.True(safety.GetProperty("requiresTarget").GetBoolean());
        Assert.True(safety.GetProperty("requiresLease").GetBoolean());
        Assert.Equal(
            "unknown",
            safety.GetProperty("mutationClass").GetString());
        Assert.Equal(
            "declared_or_unknown",
            safety.GetProperty("mutationResolution").GetString());
    }

    [Fact]
    public void UnknownQueryFieldIsRejectedWithoutExecution()
    {
        using var input = JsonDocument.Parse(
            """{"operation":"script.eval","execute":true}""");
        var result = OperationExamplesRegistry.Execute(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "examples-invalid-query",
                Operation = OperationExamplesRegistry.OperationName,
                Input = input.RootElement.Clone()
            });

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_examples_query", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
    }
}
