using System.Text.Json;

namespace ComTool.Knowledge.Tests;

public sealed class ComMutationKnowledgeValidatorTests
{
    [Theory]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.Layers[0].Name","value":"Layer 1"}""")]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.Layers[0].Opacity","value":42.5}""")]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.CropStyle","value":1}""")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.ProcessGesture","args":["points.dat"]}""")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.Close","args":[]}""")]
    [InlineData(
        "com.get",
        """{"path":"Version"}""")]
    [InlineData(
        "com.get",
        """{"path":"ActiveDocument.Layers[0].Name"}""")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.Artboards.GetActiveArtboardIndex","args":[]}""")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.Artboards.GetByName","args":["Artboard 1"]}""")]
    public void KnownCompatibleSignaturesValidate(
        string operation,
        string json)
    {
        var result = Validate(operation, json);

        Assert.Equal(
            ComInventoryValidationDisposition.Validated,
            result.Disposition);
        Assert.False(result.Invalid);
    }

    [Theory]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.Layers[0].Name","value":42}""")]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.Layers[0].Opacity","value":true}""")]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.CropStyle","value":999}""")]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument","value":"not-a-document"}""")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.ProcessGesture","args":[]}""")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.ProcessGesture","args":[42]}""")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.Close","args":[1,2]}""")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.Artboards.GetActiveArtboardIndex","args":[1]}""")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.Artboards.GetByName","args":[42]}""")]
    public void KnownContradictionsAreRejectedBeforeDispatch(
        string operation,
        string json)
    {
        var result = Validate(operation, json);

        Assert.Equal(
            ComInventoryValidationDisposition.Invalid,
            result.Disposition);
        Assert.True(result.Invalid);
        Assert.False(string.IsNullOrWhiteSpace(result.Kind));
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.FutureProperty","value":1}""")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.FutureMethod","args":[]}""")]
    [InlineData(
        "com.get",
        """{"path":"ActiveDocument.FutureProperty"}""")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.FutureMethod","args":[]}""")]
    public void MissingInventoryMembersRemainIndeterminate(
        string operation,
        string json)
    {
        var result = Validate(operation, json);

        Assert.Equal(
            ComInventoryValidationDisposition.Indeterminate,
            result.Disposition);
        Assert.False(result.Invalid);
    }

    [Fact]
    public void SharedMutationPathGrammarRejectsIndexedTerminal()
    {
        var result = Validate(
            "com.set",
            """{"path":"ActiveDocument.Layers[0]","value":"x"}""");

        Assert.Equal(
            ComInventoryValidationDisposition.Invalid,
            result.Disposition);
        Assert.Equal("invalid_com_path", result.Kind);
    }

    [Fact]
    public void ReadPathGrammarAllowsIndexedTerminalCollectionItem()
    {
        var result = Validate(
            "com.get",
            """{"path":"ActiveDocument.Layers[0]"}""");

        Assert.Equal(
            ComInventoryValidationDisposition.Validated,
            result.Disposition);
    }

    [Fact]
    public void VariantParameterDoesNotInventAnEnumDomain()
    {
        var result = Validate(
            "com.call",
            """{"path":"ActiveDocument.Close","args":[true]}""");

        Assert.Equal(
            ComInventoryValidationDisposition.Validated,
            result.Disposition);
    }

    private static ComInventoryValidationResult Validate(
        string operation,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return ComMutationKnowledgeValidator.Validate(
            operation,
            document.RootElement);
    }
}
