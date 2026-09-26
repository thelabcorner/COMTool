using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorScriptRunFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "comtool-v2-runfile-tests",
        Guid.NewGuid().ToString("N"));

    public IllustratorScriptRunFileTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ParseRequiresAbsoluteLocalScriptPathAndExactSha256()
    {
        foreach (var json in new[]
                 {
                     """{"path":"relative.jsx","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
                     """{"path":"\\\\server\\share\\probe.jsx","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
                     """{"path":"C:\\probe.txt","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
                     """{"path":"C:\\probe.jsx"}""",
                     """{"path":"C:\\probe.jsx","expectedSha256":"not-a-hash"}""",
                     """{"path":"C:\\probe.jsx","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","args":{"x":1}}""",
                     """{"path":"C:\\probe.jsx","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","effects":"read_only"}""",
                     """{"path":"C:\\probe.jsx","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","unexpected":true}"""
                 })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<ArgumentException>(
                () => IllustratorScriptRunFile.ParseRequest(
                    document.RootElement));
        }
    }

    [Fact]
    public void ParseAcceptsJsxbinAndNormalizesHashCase()
    {
        var path = Path.Combine(_root, "probe.jsxbin");
        using var document = JsonDocument.Parse(
            $$"""
            {
              "path": {{JsonSerializer.Serialize(path)}},
              "expectedSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "effects": "unknown",
              "args": [1, "two", {"x":true}]
            }
            """);

        var parsed = IllustratorScriptRunFile.ParseRequest(
            document.RootElement);

        Assert.Equal(Path.GetFullPath(path), parsed.Path);
        Assert.Equal(
            new string('a', 64),
            parsed.ExpectedSha256);
        Assert.Equal(
            """[1, "two", {"x":true}]""".Replace(" ", string.Empty),
            parsed.ArgsJson.Replace(" ", string.Empty));
    }

    [Fact]
    public void EvalFileSourceUsesNamespacedArgsAndRestoresGlobalInFinally()
    {
        var source = IllustratorScriptRunFile.BuildEvalFileSource(
            @"C:\Temp Folder\quoted ' probe.jsx");

        Assert.Contains(
            "$.evalFile(new File(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            JsonSerializer.Serialize(
                "C:/Temp Folder/quoted ' probe.jsx"),
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            IllustratorScriptRunFile.ArgsGlobalName,
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "finally{",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete __ct_rf_g." +
            IllustratorScriptRunFile.ArgsGlobalName,
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$.global.__com_args",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteUsesEvalFileRouteAndPreservesTypedResult()
    {
        var path = WriteScript(
            "typed.jsx",
            "#target illustrator\n$.global.__comtool_v2_runfile_args;");
        var request = RequestFor(path);
        string? observedSource = null;
        int? observedMode = null;

        var outcome = IllustratorScriptRunFile.Execute(
            new object(),
            request,
            (_, source, executionMode) =>
            {
                observedSource = source;
                observedMode = executionMode;
                return
                    """{"ok":true,"result":{"s":"true","n":2,"b":true,"a":[1,null]}}""";
            });

        Assert.True(outcome.Ok);
        Assert.Equal(
            IllustratorScriptEval.NeverShowDebugger,
            observedMode);
        Assert.NotNull(observedSource);
        Assert.Contains(
            "$.evalFile(new File(",
            observedSource!,
            StringComparison.Ordinal);
        Assert.Contains(
            IllustratorScriptRunFile.ArgsGlobalName,
            observedSource!,
            StringComparison.Ordinal);

        var value = Assert.IsType<JsonElement>(
            outcome.Value?.Value);
        Assert.Equal("true", value.GetProperty("s").GetString());
        Assert.Equal(2, value.GetProperty("n").GetInt32());
        Assert.True(value.GetProperty("b").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            value.GetProperty("a")[1].ValueKind);
    }

    [Fact]
    public void HashMismatchFailsBeforeAnyIllustratorDispatch()
    {
        var path = WriteScript("mismatch.jsx", "return 1;");
        var parsed = new ScriptRunFileRequest(
            path,
            new string('0', 64),
            "[]");
        var dispatchCount = 0;

        var error = Assert.Throws<HostAdapterException>(
            () => IllustratorScriptRunFile.Execute(
                new object(),
                parsed,
                (_, _, _) =>
                {
                    dispatchCount++;
                    return """{"ok":true,"result":1}""";
                }));

        Assert.Equal(0, dispatchCount);
        Assert.Equal("script_file_hash_mismatch", error.Kind);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void VerifiedFileCannotBeReplacedWhileEvalFileDispatchIsInFlight()
    {
        var path = WriteScript("locked.jsx", "return 1;");
        var request = RequestFor(path);
        var overwriteBlocked = false;

        var outcome = IllustratorScriptRunFile.Execute(
            new object(),
            request,
            (_, _, _) =>
            {
                try
                {
                    File.WriteAllText(
                        path,
                        "return 999;",
                        Encoding.UTF8);
                }
                catch (IOException)
                {
                    overwriteBlocked = true;
                }

                return """{"ok":true,"result":1}""";
            });

        Assert.True(outcome.Ok);
        Assert.True(
            overwriteBlocked,
            "The verified file was writable/replacable during evalFile dispatch.");
        Assert.Equal(
            "return 1;",
            File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void EvalFileErrorsReportFileLineInsteadOfGeneratedWrapperLine()
    {
        var path = WriteScript(
            "error.jsx",
            "#target illustrator\nthrow new Error('boom');");
        var request = RequestFor(path);

        var outcome = IllustratorScriptRunFile.Execute(
            new object(),
            request,
            (_, _, _) =>
                """{"ok":false,"name":"Error","message":"boom","line":2}""");

        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Error!.WrapperLine);
        Assert.Equal(2, outcome.Error.SourceLine);
        Assert.Equal("boom", outcome.Error.Message);
    }

    [Fact]
    public async Task SessionRoutesRunFileAndReturnsAmbiguousScriptFileError()
    {
        var identity = Identity();
        var path = WriteScript(
            "session-error.jsx",
            "#target illustrator\nthrow new Error('boom');");
        var parsed = RequestFor(path);
        using var input = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                path = parsed.Path,
                expectedSha256 = parsed.ExpectedSha256,
                effects = "unknown",
                args = Array.Empty<object>()
            }));

        await using var session = new IllustratorSession(
            identity,
            new object(),
            (_, _, _) =>
                """{"ok":false,"name":"Error","message":"boom","line":2}""");

        var result = await session.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "runfile-session-error",
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0),
                Operation = IllustratorScriptRunFile.Operation,
                Input = input.RootElement.Clone()
            });

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal("script_file_error", result.Error?.Kind);
        Assert.Equal(
            ExecutionState.Ambiguous,
            result.Error?.Execution);
        Assert.Contains(
            "file line 2",
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    private ScriptRunFileRequest RequestFor(string path) =>
        new(
            Path.GetFullPath(path),
            Sha256(path),
            """[1,"two",{"x":true}]""");

    private string WriteScript(
        string name,
        string source)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(
            path,
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(
                SHA256.HashData(
                    File.ReadAllBytes(path)))
            .ToLowerInvariant();

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

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}