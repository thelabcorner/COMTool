using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorScriptEvalTests
{
    [Fact]
    public void ParseRequestPreservesStructuredArguments()
    {
        using var input = JsonDocument.Parse(
            """
            {
              "kind": "code",
              "source": "return arguments;",
              "effects": "unknown",
              "args": [1, "two", {"x": true}]
            }
            """);

        var request = IllustratorScriptEval.ParseRequest(
            input.RootElement);

        Assert.Equal("code", request.Kind);
        Assert.Equal("return arguments;", request.Source);
        Assert.Equal("""[1, "two", {"x": true}]""", request.ArgsJson);
    }

    [Fact]
    public void ParseRequestRejectsReadOnlySelfCertification()
    {
        using var input = JsonDocument.Parse(
            """
            {
              "kind": "expression",
              "source": "app.version",
              "effects": "read_only"
            }
            """);

        var error = Assert.Throws<ArgumentException>(
            () => IllustratorScriptEval.ParseRequest(
                input.RootElement));

        Assert.Contains(
            "cannot declare itself read-only",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRequestRejectsUnknownFields()
    {
        using var input = JsonDocument.Parse(
            """
            {
              "kind": "expression",
              "source": "1 + 1",
              "mystery": true
            }
            """);

        Assert.Throws<ArgumentException>(
            () => IllustratorScriptEval.ParseRequest(
                input.RootElement));
    }

    [Fact]
    public void ParseRequestRejectsUnknownResultMode()
    {
        using var input = JsonDocument.Parse(
            """{"kind":"code","source":"return 1;","resultMode":"implicit"}""");

        var error = Assert.Throws<ArgumentException>(
            () => IllustratorScriptEval.ParseRequest(
                input.RootElement));

        Assert.Contains(
            "'resultMode' must be 'capture' or 'discard'",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WrapperParsesArgumentsWithSharedEsonAndEmbedsUserSource()
    {
        var request = new ScriptEvalRequest(
            "expression",
            "arguments[0] + arguments[1]",
            "[2,3]",
            "capture");

        var wrapper = IllustratorScriptEval.BuildWrapper(request);

        Assert.Contains(
            "var __ct_args=ESON.parse(\"[2,3]\");",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "}).apply(null,__ct_args);",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "arguments[0] + arguments[1]",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.True(wrapper.UserSourceStartLine > 1);
        Assert.Equal(1, wrapper.UserSourceLineCount);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("discard")]
    public void ParseRequestAcceptsExplicitResultMode(string resultMode)
    {
        using var input = JsonDocument.Parse(
            $$"""{"kind":"code","source":"return null;","resultMode":"{{resultMode}}"}""");

        var request = IllustratorScriptEval.ParseRequest(input.RootElement);

        Assert.Equal(resultMode, request.ResultMode);
    }

    [Fact]
    public void ParseRequestDefaultsResultModeToCapture()
    {
        using var input = JsonDocument.Parse(
            """{"kind":"code","source":"return null;"}""");

        var request = IllustratorScriptEval.ParseRequest(input.RootElement);

        Assert.Equal("capture", request.ResultMode);
    }

    [Fact]
    public void CaptureEnvelopeDistinguishesMissingValueFromExplicitNull()
    {
        var wrapper = new ScriptWrapper("ignored", 1, 1);

        var missing = IllustratorScriptEval.ParseEnvelope(
            """{"ok":true,"result":null,"resultPresent":false}""",
            wrapper);
        var explicitNull = IllustratorScriptEval.ParseEnvelope(
            """{"ok":true,"result":null,"resultPresent":true}""",
            wrapper);

        Assert.False(missing.ResultPresent);
        Assert.True(explicitNull.ResultPresent);
        Assert.Equal("null", missing.Value?.Kind);
        Assert.Equal("null", explicitNull.Value?.Kind);
    }

    [Fact]
    public void DiscardModeDoesNotSerializeReturnedValue()
    {
        var wrapper = IllustratorScriptEval.BuildWrapper(
            new ScriptEvalRequest(
                "code",
                "return {cyclic:true};",
                "[]",
                "discard"));

        Assert.Contains(
            "var __ct_result_present=false;",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__ct_validate_json(__ct_result,[],0);",
            wrapper.Source[
                wrapper.Source.IndexOf(
                    "}).apply(null,__ct_args);",
                    StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void WrapperUsesNamespacedEsonFingerprintWithoutEmbeddingRuntime()
    {
        var wrapper = IllustratorScriptEval.BuildWrapper(
            new ScriptEvalRequest(
                "code",
                "return {value:true};",
                "[]",
                "capture"));

        Assert.Contains(
            "var __ct_g=$.global;",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var ESON=__ct_g&&__ct_g.__comtool_v2_eson;",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "__comtool_v2_eson_sha256",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            IllustratorEsonRuntime.ExpectedSha256,
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__CT_ESON_SHA__",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "return ESON.stringify({ok:true,result:__ct_result,resultPresent:__ct_result_present});",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            """{"__comtoolTransport":"eson_missing"}""",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var ESON_JSON2",
            wrapper.Source,
            StringComparison.Ordinal);
        Assert.True(
            wrapper.Source.Length <
            IllustratorEsonRuntime.Source.Length);
    }

    [Fact]
    public void EsonInstallerPromotesCanonicalRuntimeToNamespacedEngineState()
    {
        var installer =
            IllustratorScriptEval.BuildEsonInstallerSource();

        Assert.Contains(
            "var ESON_JSON2",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "__ct_g.__comtool_v2_eson=ESON;",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "__ct_g.__comtool_v2_eson_sha256='" +
            IllustratorEsonRuntime.ExpectedSha256 +
            "';",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__ct_g.JSON.parse=",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__ct_g.JSON.stringify=",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "comtool-eson-ready",
            installer,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"__comtoolTransport":"eson_missing"}""", true)]
    [InlineData("""{"ok":true,"result":"eson_missing"}""", false)]
    [InlineData("", false)]
    public void EsonMissingSentinelIsExact(
        string raw,
        bool expected)
    {
        Assert.Equal(
            expected,
            IllustratorScriptEval.IsEsonMissingEnvelope(raw));
    }

    [Fact]
    public void EmbeddedEsonRuntimeMatchesPinnedProvenance()
    {
        var source = IllustratorEsonRuntime.Source;

        Assert.False(string.IsNullOrWhiteSpace(source));
        Assert.Contains(
            "ESON.stringify",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__toCommonJS",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__copyProps",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__export",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Object[\"defineProperty\"]",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Object[\"getOwnPropertyDescriptor\"]",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Object[\"getOwnPropertyNames\"]",
            source,
            StringComparison.Ordinal);
        var actualSha256 = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        Assert.Equal(
            IllustratorEsonRuntime.ExpectedSha256,
            actualSha256);
    }

    [Theory]
    [InlineData(IllustratorScriptEval.EsonReadyToken, true)]
    [InlineData("comtool-eson-missing", false)]
    public void CodecStatusProbesPinnedRuntimeWithoutBootstrapping(
        string probeResult,
        bool expectedInstalled)
    {
        using var input = JsonDocument.Parse("{}");
        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "codec-status-test",
            Operation = IllustratorScriptCodecStatus.Operation,
            Input = input.RootElement.Clone()
        };

        var calls = 0;
        string? observedSource = null;
        int? observedMode = null;

        var result = IllustratorScriptCodecStatus.Execute(
            new object(),
            request,
            (_, source, mode) =>
            {
                calls++;
                observedSource = source;
                observedMode = mode;
                return probeResult;
            });

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, calls);
        Assert.Equal(
            IllustratorScriptEval.EsonPresenceProbeSource,
            observedSource);
        Assert.Equal(
            IllustratorScriptEval.NeverShowDebugger,
            observedMode);
        Assert.DoesNotContain(
            "var ESON_JSON2",
            observedSource,
            StringComparison.Ordinal);

        var payload = result.Result!.Value!.Value;
        Assert.Equal("eson", payload.GetProperty("codec").GetString());
        Assert.True(payload.GetProperty("optional").GetBoolean());
        Assert.Equal(
            IllustratorEsonRuntime.ExpectedSha256,
            payload.GetProperty("expectedSha256").GetString());
        Assert.True(
            payload.GetProperty("embeddedVerified").GetBoolean());
        Assert.Equal(
            expectedInstalled,
            payload.GetProperty("installed").GetBoolean());
        Assert.Equal(
            expectedInstalled,
            payload.GetProperty("installedSha256Matches").GetBoolean());
        Assert.Equal(
            !expectedInstalled,
            payload.GetProperty("bootstrapRequired").GetBoolean());
        Assert.Equal(
            "bootstrap_on_demand",
            payload.GetProperty("fallback").GetString());
    }

    [Fact]
    public void CodecStatusRejectsUnknownInputWithoutProbing()
    {
        using var input = JsonDocument.Parse("""{"install":true}""");
        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "codec-status-invalid",
            Operation = IllustratorScriptCodecStatus.Operation,
            Input = input.RootElement.Clone()
        };
        var calls = 0;

        var result = IllustratorScriptCodecStatus.Execute(
            new object(),
            request,
            (_, _, _) =>
            {
                calls++;
                return IllustratorScriptEval.EsonReadyToken;
            });

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("\"true\"", "string")]
    [InlineData("true", "boolean")]
    [InlineData("123", "number")]
    [InlineData("null", "null")]
    [InlineData("[1,2]", "array")]
    [InlineData("{\"x\":1}", "object")]
    public void SuccessfulEnvelopePreservesProtocolType(
        string resultJson,
        string expectedKind)
    {
        var wrapper = new ScriptWrapper(
            "ignored",
            UserSourceStartLine: 10,
            UserSourceLineCount: 2);

        var outcome = IllustratorScriptEval.ParseEnvelope(
            $"{{\"ok\":true,\"result\":{resultJson}}}",
            wrapper);

        Assert.True(outcome.Ok);
        Assert.NotNull(outcome.Value);
        Assert.Equal(expectedKind, outcome.Value!.Kind);
    }

    [Fact]
    public void ErrorEnvelopeMapsWrapperLineToSourceLine()
    {
        var wrapper = new ScriptWrapper(
            "ignored",
            UserSourceStartLine: 20,
            UserSourceLineCount: 4);

        var outcome = IllustratorScriptEval.ParseEnvelope(
            """
            {"ok":false,"name":"Error","message":"boom","line":22}
            """,
            wrapper);

        Assert.False(outcome.Ok);
        Assert.Equal("Error", outcome.Error?.Name);
        Assert.Equal("boom", outcome.Error?.Message);
        Assert.Equal(22, outcome.Error?.WrapperLine);
        Assert.Equal(3, outcome.Error?.SourceLine);
    }

    [Fact]
    public void InvalidTransportEnvelopeIsAmbiguous()
    {
        var wrapper = new ScriptWrapper(
            "ignored",
            UserSourceStartLine: 1,
            UserSourceLineCount: 1);

        var error = Assert.Throws<HostAdapterException>(
            () => IllustratorScriptEval.ParseEnvelope(
                "not-json",
                wrapper));

        Assert.Equal(
            "script_transport_invalid",
            error.Kind);
        Assert.Equal(
            ExecutionState.Ambiguous,
            error.Execution);
    }
}
