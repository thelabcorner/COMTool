using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Knowledge.Tests;

public sealed class IllustratorComKnowledgeServiceTests
{
    [Fact]
    public void DescribeReportsSourceAuthorityWithoutExposingAPath()
    {
        var result = Execute(
            IllustratorComKnowledgeService.DescribeOperation,
            "{}");

        Assert.True(result.Ok, result.Error?.Message);
        var payload = Value(result);
        Assert.Equal(
            "sqlite-index",
            payload.GetProperty("source").GetProperty("kind").GetString());
        Assert.True(
            payload.GetProperty("source")
                .GetProperty("jsonOnDiskMatchesManifest")
                .GetBoolean());
        Assert.Equal(
            "unknown",
            payload.GetProperty("host").GetProperty("version").GetString());
        Assert.False(
            payload.GetProperty("host")
                .GetProperty("versionKnown")
                .GetBoolean());
        Assert.False(
            payload.GetProperty("buildEnvironment")
                .GetProperty("authoritative")
                .GetBoolean());
        Assert.False(payload.TryGetProperty("path", out _));
    }

    [Fact]
    public void SearchFindsApplicationDoScript()
    {
        var result = Execute(
            IllustratorComKnowledgeService.SearchOperation,
            """{"query":"DoScript","limit":10}""");

        Assert.True(result.Ok, result.Error?.Message);
        var items = Value(result).GetProperty("items");
        Assert.Contains(
            items.EnumerateArray(),
            item =>
                item.GetProperty("kind").GetString() == "method" &&
                item.GetProperty("interface").GetString() == "_Application" &&
                item.GetProperty("name").GetString() == "DoScript");
    }

    [Fact]
    public void SymbolReturnsExactCloseSignature()
    {
        var result = Execute(
            IllustratorComKnowledgeService.SymbolOperation,
            """
            {
              "name":"Close",
              "interface":"Document",
              "limit":10
            }
            """);

        Assert.True(result.Ok, result.Error?.Message);
        var method = Assert.Single(
            Value(result)
                .GetProperty("methods")
                .EnumerateArray()
                .ToArray());

        Assert.Equal("Document", method.GetProperty("interface").GetString());
        Assert.Equal("Close", method.GetProperty("name").GetString());
        Assert.Equal("VT_VOID", method.GetProperty("returnType").GetString());

        var parameter = Assert.Single(
            method.GetProperty("parameters").EnumerateArray().ToArray());
        Assert.Equal("Saving", parameter.GetProperty("name").GetString());
        Assert.Equal("VT_VARIANT", parameter.GetProperty("type").GetString());
        Assert.Contains(
            "PARAMFLAG_FOPT",
            parameter.GetProperty("flags").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnumExpandsAllSaveOptions()
    {
        var result = Execute(
            IllustratorComKnowledgeService.EnumOperation,
            """{"name":"AiSaveOptions"}""");

        Assert.True(result.Ok, result.Error?.Message);
        var enumResult = Assert.Single(
            Value(result).GetProperty("enums").EnumerateArray().ToArray());
        var values = enumResult.GetProperty("values").EnumerateArray().ToArray();

        Assert.Equal(3, values.Length);
        Assert.Contains(
            values,
            value =>
                value.GetProperty("name").GetString() ==
                    "aiDoNotSaveChanges" &&
                value.GetProperty("value").GetInt64() == 2);
    }

    [Fact]
    public void PathsMatchesLegacyApplicationToDocumentOracle()
    {
        var result = Execute(
            IllustratorComKnowledgeService.PathsOperation,
            """
            {
              "start":"Application",
              "target":"Document"
            }
            """);

        Assert.True(result.Ok, result.Error?.Message);
        var payload = Value(result);
        Assert.Equal(
            "_Application",
            payload.GetProperty("start").GetString());
        Assert.Equal(
            "Document",
            payload.GetProperty("targetInterface").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("targetMember").ValueKind);

        var step = Assert.Single(
            payload.GetProperty("steps").EnumerateArray().ToArray());
        Assert.Equal("_Application", step.GetProperty("from").GetString());
        Assert.Equal("Document", step.GetProperty("to").GetString());
        Assert.Equal("property", step.GetProperty("kind").GetString());
        Assert.Equal("ActiveDocument", step.GetProperty("member").GetString());
        Assert.Empty(step.GetProperty("parameters").EnumerateArray());
    }

    [Fact]
    public void PathsMatchesLegacyTerminalMemberOracle()
    {
        var result = Execute(
            IllustratorComKnowledgeService.PathsOperation,
            """
            {
              "start":"Application",
              "target":"Document.Close"
            }
            """);

        Assert.True(result.Ok, result.Error?.Message);
        var payload = Value(result);
        Assert.Equal(
            "Document",
            payload.GetProperty("targetInterface").GetString());
        Assert.Equal(
            "Close",
            payload.GetProperty("targetMember").GetString());

        var terminal = payload.GetProperty("terminal");
        var method = Assert.Single(
            terminal.GetProperty("methods").EnumerateArray().ToArray());
        Assert.Equal("Close", method.GetProperty("name").GetString());
        Assert.Equal(
            "VT_VOID",
            method.GetProperty("returnType").GetString());

        var parameter = Assert.Single(
            method.GetProperty("parameters").EnumerateArray().ToArray());
        Assert.Equal("Saving", parameter.GetProperty("name").GetString());
        Assert.Equal("VT_VARIANT", parameter.GetProperty("type").GetString());
        Assert.Contains(
            "PARAMFLAG_FOPT",
            parameter.GetProperty("flags").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PathsEnforcesLegacyDepthBound()
    {
        var result = Execute(
            IllustratorComKnowledgeService.PathsOperation,
            """
            {
              "start":"Application",
              "target":"Document",
              "maxDepth":9
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_knowledge_request", result.Error?.Kind);
    }

    [Fact]
    public void UnknownInputFieldIsRejectedBeforeScanning()
    {
        var result = Execute(
            IllustratorComKnowledgeService.SearchOperation,
            """{"query":"Close","effects":"read_only"}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_knowledge_request", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
    }

    private static OperationResult Execute(
        string operation,
        string inputJson)
    {
        using var input = JsonDocument.Parse(inputJson);
        return IllustratorComKnowledgeService.Execute(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "knowledge-service-test",
                Operation = operation,
                Input = input.RootElement.Clone()
            });
    }

    private static JsonElement Value(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.HasValue);
        return result.Result.Value.Value;
    }
}
