using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorStructureOperationsTests
{
    [Fact]
    public void ArtboardReadByIndexReturnsArtboardsWithoutMutating()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.artboard.read",
            """{"index":0}""");

        Assert.True(result.Ok, result.Error?.Message);
        var payload = ReadPayload(result);
        Assert.True(payload.GetProperty("exists").GetBoolean());
        Assert.Equal(2, payload.GetProperty("artboardCount").GetInt32());

        var first = payload.GetProperty("artboards")[0];
        Assert.Equal("Artboard 1", first.GetProperty("name").GetString());
        Assert.Equal(4, first.GetProperty("artboardRect").GetArrayLength());
        Assert.Equal(100, first.GetProperty("artboardRect")[2].GetDouble());
    }

    [Fact]
    public void LayerReadByIndexReturnsLayerFlags()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.layer.read",
            """{"index":0}""");

        Assert.True(result.Ok, result.Error?.Message);
        var payload = ReadPayload(result);
        Assert.True(payload.GetProperty("exists").GetBoolean());
        Assert.Equal(2, payload.GetProperty("layerCount").GetInt32());

        var first = payload.GetProperty("layers")[0];
        Assert.Equal("Layer 1", first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("visible").GetBoolean());
        Assert.False(first.GetProperty("locked").GetBoolean());
        Assert.Equal(100, first.GetProperty("opacity").GetDouble());
        Assert.Equal(0, first.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public void StructureReadResolvesDocumentByName()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.layer.read",
            """{"name":"Second.ai"}""");

        Assert.True(result.Ok, result.Error?.Message);
        var payload = ReadPayload(result);
        Assert.Equal(1, payload.GetProperty("index").GetInt32());
        Assert.Equal(
            "Second.ai",
            payload.GetProperty("document").GetProperty("name").GetString());
    }

    [Fact]
    public void StructureReadReportsMissingDocumentAsObservedState()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.artboard.read",
            """{"name":"DoesNotExist.ai"}""");

        Assert.True(result.Ok, result.Error?.Message);
        var payload = ReadPayload(result);
        Assert.False(payload.GetProperty("exists").GetBoolean());
        Assert.Contains(
            "DoesNotExist.ai",
            payload.GetProperty("reason").GetString());
    }

    [Fact]
    public void StructureReadRejectsUnknownFields()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.layer.read",
            """{"index":0,"unexpected":true}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_input", result.Error?.Kind);
    }

    [Fact]
    public void StructureReadRejectsMultipleSelectors()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.artboard.read",
            """{"index":0,"active":true}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public void StructureReadRejectsOutOfRangeIndex()
    {
        var root = FakeApplication.Create();

        var result = Execute(
            root,
            "illustrator.layer.read",
            """{"index":99}""");

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(ReadPayload(result).GetProperty("exists").GetBoolean());
    }

    private static OperationResult Execute(
        object root,
        string operation,
        string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "structure-test",
            Operation = operation,
            Input = document.RootElement.Clone()
        };

        return IllustratorStructureOperations.Execute(root, request);
    }

    private static JsonElement ReadPayload(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.HasValue);
        return result.Result.Value.Value;
    }
}
