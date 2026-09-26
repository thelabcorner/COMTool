using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Hosts.Illustrator;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorComWriteBridgeTests
{
    // ---------------------------------------------------------------------
    // Runtime-owned allowlist
    // ---------------------------------------------------------------------

    [Fact]
    public void AllowlistContainsTheSingleActiveArtboardNameTarget()
    {
        var target = Assert.Single(
            IllustratorComWriteBridge.Targets).Value;

        Assert.Equal(
            "document.artboard.active.name",
            target.Key);
        Assert.Equal("Name", target.Member);
        Assert.Equal("string", target.ValueType);
        Assert.Equal("value", target.ValueProperty);
        Assert.Equal("name", target.ValueMember);
    }

    [Fact]
    public void UnknownPropertyKeyIsNotResolvable()
    {
        Assert.False(
            IllustratorComWriteBridge.TryGetTarget(
                "document.artboard.active.artboardRect",
                out _));

        Assert.False(
            IllustratorComWriteBridge.TryGetTarget(
                "activeDocument.Name",
                out _));

        Assert.False(
            IllustratorComWriteBridge.TryGetTarget(
                "App.UserInteractionLevel",
                out _));
    }

    // ---------------------------------------------------------------------
    // Setter body construction
    // ---------------------------------------------------------------------

    [Fact]
    public void SetScriptBindsTheFixedComMemberAndNeverConcatenatesRawText()
    {
        var target = IllustratorComWriteBridge.Targets[
            "document.artboard.active.name"];

        // A quote/paren-laden payload must never appear as raw script text:
        // it is carried as JSON string data and parsed with ESON.parse.
        const string hostile = "\"); app.quit(); //";

        var body = IllustratorComWriteBridge.BuildSetScript(
            target,
            JsonSerializer.Serialize(hostile));

        Assert.Contains(
            "__ct_ab.Name=__ct_v;",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "var __ct_v=ESON.parse(",
            body,
            StringComparison.Ordinal);

        // The hostile payload is carried as JSON string data that is parsed
        // by ESON; it never becomes bare executable script because the
        // dangerous quote is backslash-escaped inside the JSON literal.
        Assert.Contains(
            "app.quit(); //",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "=ESON.parse(\"); app.quit();",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SetScriptUsesTheActiveDocumentAndActiveArtboard()
    {
        var target = IllustratorComWriteBridge.Targets[
            "document.artboard.active.name"];

        var body = IllustratorComWriteBridge.BuildSetScript(
            target,
            "\"Board A\"");

        Assert.Contains(
            "var __ct_d=app.activeDocument;",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "artboards.getActiveArtboard()",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "return {kind:'set',name:String(__ct_ab.name)};",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentMutationWrapperGatesOnThePinnedEsonFingerprint()
    {
        var body = IllustratorComWriteBridge.BuildSetScript(
            IllustratorComWriteBridge.Targets[
                "document.artboard.active.name"],
            "\"Board A\"");

        var wrapped =
            IllustratorScriptEval.BuildDocumentMutationWrapper(body);

        Assert.Contains(
            "var ESON=__ct_g&&__ct_g.__comtool_v2_eson;",
            wrapped,
            StringComparison.Ordinal);
        Assert.Contains(
            IllustratorEsonRuntime.ExpectedSha256,
            wrapped,
            StringComparison.Ordinal);
        Assert.Contains(
            """{"__comtoolTransport":"eson_missing"}""",
            wrapped,
            StringComparison.Ordinal);
        Assert.Contains(
            "var __ct_v=ESON.parse(",
            wrapped,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Strict runtime-owned input parsing
    // ---------------------------------------------------------------------

    [Fact]
    public void ParseInputReturnsTheRawValueJsonForTheAllowlistedKey()
    {
        var target = IllustratorComWriteBridge.Targets[
            "document.artboard.active.name"];

        using var document = JsonDocument.Parse(
            """{"property":"document.artboard.active.name","value":"Board A"}""");

        var outcome = IllustratorComWriteBridge.ParseInput(
            document.RootElement,
            target);

        Assert.True(outcome.Ok);
        Assert.Equal("\"Board A\"", outcome.ValueJson);
        Assert.Null(outcome.ErrorKind);
    }

    [Theory]
    [InlineData("""{"property":"activeDocument.Name","value":"x"}""", "unsupported_com_property")]
    [InlineData("""{"property":"","value":"x"}""", "invalid_com_property")]
    [InlineData("""{"property":"document.artboard.active.name"}""", "invalid_com_value")]
    [InlineData("""{"property":"document.artboard.active.name","value":42}""", "invalid_com_value")]
    [InlineData("""{"property":"document.artboard.active.name","value":""}""", "invalid_com_value")]
    [InlineData("""{"property":"document.artboard.active.name","value":"x","path":"ActiveDocument.Name"}""", "unknown_input_field")]
    [InlineData("""[1,2,3]""", "invalid_input")]
    public void ParseInputRejectsNonAllowlistedOrMalformedRequests(
        string inputJson,
        string expectedKind)
    {
        var target = IllustratorComWriteBridge.Targets[
            "document.artboard.active.name"];

        using var document = JsonDocument.Parse(inputJson);

        var outcome = IllustratorComWriteBridge.ParseInput(
            document.RootElement,
            target);

        Assert.False(outcome.Ok);
        Assert.Equal(expectedKind, outcome.ErrorKind);
        Assert.Null(outcome.ValueJson);
    }

    // ---------------------------------------------------------------------
    // Envelope parsing
    // ---------------------------------------------------------------------

    [Fact]
    public void EnvelopeMapsSuccessfulSetToTheNewArtboardName()
    {
        var outcome = IllustratorComWriteEnvelope.Parse(
            """{"ok":true,"kind":"set","document":"Board A","requestId":null}""");

        Assert.Equal("set", outcome.Kind);
        Assert.Equal("Board A", outcome.Name);
        Assert.Null(outcome.Message);
    }

    [Fact]
    public void EnvelopeMapsNoActiveDocumentToThatOutcome()
    {
        var outcome = IllustratorComWriteEnvelope.Parse(
            """{"ok":true,"kind":"no_active_document","document":null,"requestId":null}""");

        Assert.Equal("no_active_document", outcome.Kind);
        Assert.Null(outcome.Name);
    }

    [Fact]
    public void EnvelopeMapsFailureToFailedWithMessage()
    {
        var outcome = IllustratorComWriteEnvelope.Parse(
            """{"ok":false,"kind":"failed","message":"boom","line":-1}""");

        Assert.Equal("failed", outcome.Kind);
        Assert.Equal("boom", outcome.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData("""{"ok":"yes","kind":"set"}""")]
    [InlineData("""{"ok":true,"kind":"set"}""")]
    public void MalformedEnvelopeCannotProveAMutation(string raw)
    {
        var outcome = IllustratorComWriteEnvelope.Parse(raw);

        Assert.Equal("failed", outcome.Kind);
        Assert.NotNull(outcome.Message);
    }

    // ---------------------------------------------------------------------
    // Dispatch fault classification
    // ---------------------------------------------------------------------

    [Fact]
    public void PostDispatchTransportFaultIsSurfacedAsAmbiguous()
    {
        var fault = new HostAdapterException(
            "host_server_fault",
            "lost after dispatch",
            retryable: false,
            ExecutionState.Ambiguous);

        var result = IllustratorComWriteBridge.Dispatch(
            new object(),
            "ignored",
            (_, _, _) => throw fault);

        Assert.Same(fault, result.TransportFault);
        Assert.Null(result.NonAmbiguousFault);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public void PreDispatchFaultIsNotAmbiguous()
    {
        var fault = new HostAdapterException(
            "host_busy",
            "rejected before execution",
            retryable: true,
            ExecutionState.NotStarted);

        var result = IllustratorComWriteBridge.Dispatch(
            new object(),
            "ignored",
            (_, _, _) => throw fault);

        Assert.Null(result.TransportFault);
        Assert.Same(fault, result.NonAmbiguousFault);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public void DispatchSendsTheWrappedSetterThroughTheExecutor()
    {
        string? seenSource = null;
        var mode = -1;

        var result = IllustratorComWriteBridge.Dispatch(
            new object(),
            "var __ct_r=1;",
            (_, source, executionMode) =>
            {
                seenSource = source;
                mode = executionMode;
                return """{"ok":true,"kind":"set","document":"Board A","requestId":null}""";
            });

        Assert.NotNull(seenSource);
        Assert.Equal(
            IllustratorScriptEval.NeverShowDebugger,
            mode);
        Assert.Contains(
            "var __ct_r=1;",
            seenSource!,
            StringComparison.Ordinal);
        Assert.NotNull(result.Outcome);
        Assert.Equal("set", result.Outcome!.Kind);
    }
}
