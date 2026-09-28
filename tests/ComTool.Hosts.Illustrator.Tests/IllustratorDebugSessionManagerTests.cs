using System.Text.Json;
using System.Xml.Linq;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorDebugSessionManagerTests
{
    [Fact]
    public void EvalCommandPreservesArbitrarySourceIncludingCDataTerminator()
    {
        using var input = JsonDocument.Parse(
            """
            {
              "sessionId": "dbg-test",
              "command": "eval",
              "source": "var x = ']]>';\n1 + 1;",
              "debugLevel": 1,
              "file": "file:///C:/tmp/a&b.jsx",
              "reset": false
            }
            """);

        var xml = IllustratorDebugSessionManager.BuildCommandXml(
            input.RootElement,
            "eval",
            "main",
            8000);

        var document = XDocument.Parse(xml);
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("eval", root.Name.LocalName);
        Assert.Equal("main", (string?)root.Attribute("engine"));
        Assert.Equal("1", (string?)root.Attribute("debug"));
        Assert.Equal("8000", (string?)root.Attribute("timeout"));
        Assert.Equal("file:///C:/tmp/a&b.jsx", (string?)root.Attribute("file"));
        Assert.Equal("false", (string?)root.Attribute("reset"));
        Assert.Equal("var x = ']]>';\n1 + 1;", root.Element("source")?.Value);
    }

    [Fact]
    public void BreakpointCommandEscapesDataAndRetainsTypedFields()
    {
        using var input = JsonDocument.Parse(
            """
            {
              "sessionId": "dbg-test",
              "command": "set-breakpoints",
              "flags": 1024,
              "breakpoints": [
                {
                  "file": "file:///C:/tmp/a&b.jsx",
                  "line": 12,
                  "enabled": true,
                  "condition": "x < 4 && y > 1",
                  "hits": 2,
                  "count": 3
                }
              ]
            }
            """);

        var xml = IllustratorDebugSessionManager.BuildCommandXml(
            input.RootElement,
            "set-breakpoints",
            "main",
            5000);

        var root = XDocument.Parse(xml).Root!;
        var breakpoint = Assert.Single(root.Elements("breakpoint"));
        Assert.Equal("main", (string?)root.Attribute("engine"));
        Assert.Equal("1024", (string?)root.Attribute("flags"));
        Assert.Equal("file:///C:/tmp/a&b.jsx", (string?)breakpoint.Attribute("file"));
        Assert.Equal("12", (string?)breakpoint.Attribute("line"));
        Assert.Equal("true", (string?)breakpoint.Attribute("enabled"));
        Assert.Equal("2", (string?)breakpoint.Attribute("hits"));
        Assert.Equal("3", (string?)breakpoint.Attribute("count"));
        Assert.Equal("x < 4 && y > 1", breakpoint.Value);
    }

    [Fact]
    public void CommandContractRejectsUnknownFieldsAndUnknownCommands()
    {
        using var unknownField = JsonDocument.Parse(
            """
            {
              "sessionId": "dbg-test",
              "command": "get-breakpoints",
              "surprise": true
            }
            """);

        Assert.Throws<ArgumentException>(
            () => IllustratorDebugSessionManager.BuildCommandXml(
                unknownField.RootElement,
                "get-breakpoints",
                "main",
                5000));

        using var unknownCommand = JsonDocument.Parse(
            """
            {
              "sessionId": "dbg-test",
              "command": "arbitrary-xml"
            }
            """);

        Assert.Throws<ArgumentException>(
            () => IllustratorDebugSessionManager.BuildCommandXml(
                unknownCommand.RootElement,
                "arbitrary-xml",
                "main",
                5000));
    }

    [Fact]
    public void DebuggerCapabilitiesAreTruthfulAndConservativelyMutating()
    {
        var support = IllustratorDebuggerSupport.Probe();

        foreach (var name in new[]
                 {
                     IllustratorDebugSessionManager.OpenOperation,
                     IllustratorDebugSessionManager.CommandOperation,
                     IllustratorDebugSessionManager.CloseOperation
                 })
        {
            var capability = Assert.Single(
                IllustratorOperations.Capabilities,
                entry => entry.Name == name);

            Assert.Equal(
                MutationClass.ExternalSideEffect,
                capability.MutationClass);
            Assert.Equal(support.Supported, capability.Supported);
            Assert.Equal("illustrator", capability.Host);
        }
    }

    [Fact]
    public void DebuggerBridgeIsEmbeddedIntoHostAssembly()
    {
        using var stream = typeof(IllustratorSession)
            .Assembly
            .GetManifestResourceStream(
                "ComTool.Hosts.Illustrator.Assets.esd-debugger-bridge.mjs");

        Assert.NotNull(stream);
        Assert.True(stream!.Length > 1000);
    }

    [Fact]
    public void NormalizedEvalResponseExposesTypedNumberWithoutDiscardingEventIdentity()
    {
        using var exchange = JsonDocument.Parse(
            """
            {
              "ok": true,
              "events": [
                {
                  "reason": 3,
                  "serialNumber": 4,
                  "resultSerial": 3,
                  "bodyTag": "evalresult",
                  "body": "<evalresult engine=\"main\"><value type=\"number\"><![CDATA[42]]></value><profiling/></evalresult>"
                }
              ]
            }
            """);

        var response =
            IllustratorDebugSessionManager.NormalizeDebuggerResponse(
                "eval",
                exchange.RootElement);

        Assert.True(response.HasValue);
        var root = response.Value;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal(3, root.GetProperty("eventReason").GetInt32());
        Assert.Equal(4, root.GetProperty("eventSerial").GetInt32());

        var normalized = root.GetProperty("normalized");
        Assert.Equal(
            "evalresult",
            normalized.GetProperty("tag").GetString());
        Assert.Equal(
            "number",
            normalized.GetProperty("valueType").GetString());
        Assert.Equal(
            42,
            normalized.GetProperty("value").GetInt64());
        Assert.Equal(
            "main",
            normalized
                .GetProperty("tree")
                .GetProperty("attributes")
                .GetProperty("engine")
                .GetString());
    }

    [Fact]
    public void NormalizedEvalResponsePrefersBreakWhenExecutionSuspends()
    {
        using var exchange = JsonDocument.Parse(
            """
            {
              "ok": true,
              "events": [
                {
                  "reason": 3,
                  "serialNumber": 8,
                  "bodyTag": "break",
                  "body": "<break engine=\"main\" file=\"file:///probe.jsx\" line=\"7\"/>"
                },
                {
                  "reason": 5,
                  "serialNumber": 8,
                  "bodyTag": null,
                  "body": ""
                }
              ]
            }
            """);

        var response =
            IllustratorDebugSessionManager.NormalizeDebuggerResponse(
                "eval",
                exchange.RootElement);

        Assert.True(response.HasValue);
        var normalized =
            response.Value.GetProperty("normalized");
        Assert.Equal(
            "break",
            normalized.GetProperty("tag").GetString());
        Assert.Equal(
            "7",
            normalized
                .GetProperty("tree")
                .GetProperty("attributes")
                .GetProperty("line")
                .GetString());
    }

    [Fact]
    public void MalformedDebuggerXmlDegradesNormalizationWithoutThrowing()
    {
        using var exchange = JsonDocument.Parse(
            """
            {
              "ok": true,
              "events": [
                {
                  "reason": 3,
                  "serialNumber": 9,
                  "bodyTag": "evalresult",
                  "body": "<evalresult><value type=\"number\">42"
                }
              ]
            }
            """);

        var response =
            IllustratorDebugSessionManager.NormalizeDebuggerResponse(
                "eval",
                exchange.RootElement);

        Assert.True(response.HasValue);
        Assert.False(
            response.Value
                .GetProperty("available")
                .GetBoolean());
        Assert.Equal(
            "evalresult",
            response.Value
                .GetProperty("bodyTag")
                .GetString());
        Assert.True(
            response.Value
                .GetProperty("parseError")
                .GetString()!
                .Length > 0);
    }
}
