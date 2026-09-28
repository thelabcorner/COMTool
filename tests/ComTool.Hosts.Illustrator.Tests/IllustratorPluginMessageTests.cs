using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorPluginMessageTests
{
    [Fact]
    public void ParseRequestAcceptsBoundedDataContract()
    {
        using var input = JsonDocument.Parse(
            """{"plugin":"ExamplePlugin","selector":"rpc/v1","input":"{\"x\":1}"}""");

        var request = IllustratorPluginMessage.ParseRequest(
            input.RootElement);

        Assert.Equal("ExamplePlugin", request.Plugin);
        Assert.Equal("rpc/v1", request.Selector);
        Assert.Equal("""{"x":1}""", request.Input);
    }

    [Theory]
    [InlineData("""{"plugin":"ExamplePlugin","selector":"rpc/v1"}""")]
    [InlineData("""{"plugin":"","selector":"rpc/v1","input":""}""")]
    [InlineData("""{"plugin":"ExamplePlugin","selector":"","input":""}""")]
    [InlineData("""{"plugin":"ExamplePlugin","selector":"rpc/v1","input":"","extra":true}""")]
    public void ParseRequestRejectsIncompleteOrOpenEndedContracts(
        string json)
    {
        using var input = JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(
            () => IllustratorPluginMessage.ParseRequest(
                input.RootElement));
    }

    [Fact]
    public void ParseRequestBoundsInputByUtf8Bytes()
    {
        var oversized = new string(
            '\u00E9',
            (IllustratorPluginMessage.MaxInputUtf8Bytes / 2) + 1);
        using var input = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                plugin = "ExamplePlugin",
                selector = "rpc/v1",
                input = oversized
            }));

        var error = Assert.Throws<ArgumentException>(
            () => IllustratorPluginMessage.ParseRequest(
                input.RootElement));

        Assert.Contains(
            "UTF-8 bytes",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteUsesSendScriptMessageAndReturnsPayloadProvenance()
    {
        var request = new PluginMessageRequest(
            "ExamplePlugin",
            "rpc/v1",
            """{"x":1}""");
        object?[]? observedArgs = null;
        string? observedMethod = null;

        var outcome = IllustratorPluginMessage.Execute(
            new object(),
            request,
            (_, method, args) =>
            {
                observedMethod = method;
                observedArgs = args;
                return """{"ok":true,"result":42}""";
            });

        Assert.Equal("SendScriptMessage", observedMethod);
        Assert.NotNull(observedArgs);
        Assert.Equal("ExamplePlugin", observedArgs[0]);
        Assert.Equal("rpc/v1", observedArgs[1]);
        Assert.Equal("""{"x":1}""", observedArgs[2]);

        Assert.Equal(
            Encoding.UTF8.GetByteCount(request.Input),
            outcome.InputUtf8Bytes);
        Assert.Equal(
            Sha256(request.Input),
            outcome.InputSha256);
        Assert.Equal(
            """{"ok":true,"result":42}""",
            outcome.Response);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(outcome.Response),
            outcome.ResponseUtf8Bytes);
        Assert.Equal(
            Sha256(outcome.Response),
            outcome.ResponseSha256);
    }

    [Fact]
    public void ExecutePreservesCompletedOversizeResponseProvenance()
    {
        var request = new PluginMessageRequest(
            "ExamplePlugin",
            "rpc/v1",
            "request");
        var response = new string(
            'x',
            IllustratorPluginMessage.MaxResponseUtf8Bytes + 1);

        var error =
            Assert.Throws<PluginMessageResponseTooLargeException>(
                () => IllustratorPluginMessage.Execute(
                    new object(),
                    request,
                    (_, _, _) => response));

        Assert.Equal(
            Encoding.UTF8.GetByteCount(response),
            error.ResponseUtf8Bytes);
        Assert.Equal(
            Sha256(response),
            error.ResponseSha256);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(request.Input),
            error.InputUtf8Bytes);
        Assert.Equal(
            Sha256(request.Input),
            error.InputSha256);
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
