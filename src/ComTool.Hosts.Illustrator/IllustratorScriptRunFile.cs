using System.Security.Cryptography;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Executes an exact, caller-hash-bound ExtendScript file through $.evalFile.
/// The expected content hash participates in the outer OperationRequest
/// fingerprint, so a stable request id can never silently bind to changed file
/// bytes. A read-sharing handle remains open through dispatch to prevent
/// writers from replacing the verified content before Illustrator opens it.
/// </summary>
internal static class IllustratorScriptRunFile
{
    internal const string Operation = "script.runFile";
    internal const string ArgsGlobalName =
        "__comtool_v2_runfile_args";

    private const int MaxArgsJsonChars = 1_000_000;

    public static ScriptRunFileRequest ParseRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                "script.runFile input must be a JSON object.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "path",
            "expectedSha256",
            "effects",
            "args"
        };

        foreach (var property in input.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"Unknown script.runFile input field '{property.Name}'.");
            }
        }

        if (!input.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            throw new ArgumentException(
                "'path' must be a non-empty absolute local file path.");
        }

        var rawPath = pathElement.GetString()!;
        if (!Path.IsPathFullyQualified(rawPath))
        {
            throw new ArgumentException(
                "'path' must be fully qualified.");
        }

        string path;
        try
        {
            path = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            throw new ArgumentException(
                $"'path' is invalid: {ex.Message}",
                ex);
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) ||
            root.Length < 2 ||
            root[1] != ':')
        {
            throw new ArgumentException(
                "'path' must resolve to a local drive path; UNC/device paths are not accepted.");
        }

        var extension = Path.GetExtension(path);
        if (!string.Equals(
                extension,
                ".jsx",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                extension,
                ".jsxbin",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "'path' must end in .jsx or .jsxbin.");
        }

        if (!input.TryGetProperty(
                "expectedSha256",
                out var shaElement) ||
            shaElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "'expectedSha256' is required and must be a 64-character SHA-256 hex string.");
        }

        var expectedSha256 =
            (shaElement.GetString() ?? string.Empty)
            .ToLowerInvariant();
        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(
                static ch =>
                    !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException(
                "'expectedSha256' must be a 64-character SHA-256 hex string.");
        }

        ValidateEffects(input);

        string argsJson;
        if (!input.TryGetProperty("args", out var argsElement) ||
            argsElement.ValueKind == JsonValueKind.Null)
        {
            argsJson = "[]";
        }
        else
        {
            if (argsElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(
                    "'args' must be a JSON array.");

            argsJson = argsElement.GetRawText();
            if (argsJson.Length > MaxArgsJsonChars)
            {
                throw new ArgumentException(
                    $"'args' exceeds the {MaxArgsJsonChars} character transport limit.");
            }
        }

        return new ScriptRunFileRequest(
            path,
            expectedSha256,
            argsJson);
    }

    public static ScriptEvalOutcome Execute(
        object appObject,
        ScriptRunFileRequest request,
        Func<object, string, int, string>? scriptExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        using var file = OpenVerifiedFile(request);

        var source = BuildEvalFileSource(request.Path);
        var outcome = IllustratorScriptEval.Execute(
            appObject,
            new ScriptEvalRequest(
                "code",
                source,
                request.ArgsJson),
            scriptExecutor);

        // Measured legacy/live behavior: an exception originating inside
        // $.evalFile reports the file's line, not a wrapper-relative line.
        // Preserve that useful semantic and do not pretend the generated
        // run-file wrapper is the source document.
        if (outcome.Error is { } error)
        {
            var fileLine =
                error.WrapperLine ??
                error.SourceLine;
            return outcome with
            {
                Error = error with
                {
                    WrapperLine = null,
                    SourceLine = fileLine
                }
            };
        }

        return outcome;
    }

    internal static string BuildEvalFileSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var pathLiteral = JsonSerializer.Serialize(
            path.Replace('\\', '/'));

        return
            """
            var __ct_rf_g=$.global;
            var __ct_rf_had=Object.prototype.hasOwnProperty.call(__ct_rf_g,'__CT_ARGS_GLOBAL__');
            var __ct_rf_saved=__ct_rf_g.__CT_ARGS_GLOBAL__;
            var __ct_rf_args=[];
            var __ct_rf_i;
            for(__ct_rf_i=0;__ct_rf_i<arguments.length;__ct_rf_i++){
              __ct_rf_args[__ct_rf_i]=arguments[__ct_rf_i];
            }
            __ct_rf_g.__CT_ARGS_GLOBAL__=__ct_rf_args;
            try{
              return $.evalFile(new File(__CT_PATH__));
            }finally{
              if(__ct_rf_had){
                __ct_rf_g.__CT_ARGS_GLOBAL__=__ct_rf_saved;
              }else{
                try{delete __ct_rf_g.__CT_ARGS_GLOBAL__;}
                catch(__ct_rf_delete_error){
                  __ct_rf_g.__CT_ARGS_GLOBAL__=void 0;
                }
              }
            }
            """
            .Replace(
                "__CT_ARGS_GLOBAL__",
                ArgsGlobalName,
                StringComparison.Ordinal)
            .Replace(
                "__CT_PATH__",
                pathLiteral,
                StringComparison.Ordinal);
    }

    private static FileStream OpenVerifiedFile(
        ScriptRunFileRequest request)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                request.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or
                DirectoryNotFoundException or
                IOException or
                UnauthorizedAccessException)
        {
            throw new HostAdapterException(
                "script_file_unavailable",
                $"Could not open script file '{request.Path}': {ex.Message}",
                retryable: false,
                ExecutionState.NotStarted,
                ex.HResult,
                ex);
        }

        try
        {
            var actualSha256 = Convert.ToHexString(
                    SHA256.HashData(stream))
                .ToLowerInvariant();

            if (!string.Equals(
                    actualSha256,
                    request.ExpectedSha256,
                    StringComparison.Ordinal))
            {
                throw new HostAdapterException(
                    "script_file_hash_mismatch",
                    $"Script file '{request.Path}' does not match expected SHA-256 {request.ExpectedSha256}; actual {actualSha256}.",
                    retryable: false,
                    ExecutionState.NotStarted);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateEffects(JsonElement input)
    {
        if (!input.TryGetProperty("effects", out var effectsElement) ||
            effectsElement.ValueKind == JsonValueKind.Null)
            return;

        if (effectsElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                "'effects' must be a mutation-class string.");

        var effects = effectsElement.GetString();
        if (effects == "read_only")
        {
            throw new ArgumentException(
                "Arbitrary script-file execution cannot declare itself read-only.");
        }

        if (effects is not (
                "unknown" or
                "idempotent_write" or
                "conditional_write" or
                "non_idempotent_write" or
                "document_lifecycle" or
                "external_side_effect"))
        {
            throw new ArgumentException(
                $"Unsupported effects value '{effects ?? "<null>"}'.");
        }
    }
}

internal sealed record ScriptRunFileRequest(
    string Path,
    string ExpectedSha256,
    string ArgsJson);