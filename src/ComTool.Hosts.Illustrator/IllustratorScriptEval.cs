using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

internal static class IllustratorScriptEval
{
    /// <summary>
    /// AiJavaScriptExecutionMode.aiNeverShowDebugger. Exposed so
    /// runtime-generated document-lifecycle dispatch uses the same
    /// no-modality execution mode as <c>script.eval</c>.
    /// </summary>
    internal const int NeverShowDebugger = 1;
    private const int MaxSourceChars = 1_000_000;
    private const int MaxArgsJsonChars = 1_000_000;
    private const string EsonMissingEnvelope =
        """{"__comtoolTransport":"eson_missing"}""";
    internal const string EsonReadyToken = "comtool-eson-ready";

    /// <summary>
    /// Read-only probe that reports whether the canonical ESON runtime is
    /// already installed in this host. It never installs or mutates host
    /// state, so a mutation surface can use it to fail closed before
    /// dispatch instead of mid-script.
    /// </summary>
    internal static string EsonPresenceProbeSource =>
        "(function(){var g=$.global;" +
        "var e=g&&g.__CT_ESON_GLOBAL__;" +
        "if(e&&g.__CT_ESON_HASH_GLOBAL__==='__CT_ESON_SHA__'" +
        "&&typeof e.parse==='function'&&typeof e.stringify==='function')" +
        "{return 'comtool-eson-ready';}" +
        "return 'comtool-eson-missing';}());"
            .Replace(
                "__CT_ESON_GLOBAL__",
                EsonGlobalName,
                StringComparison.Ordinal)
            .Replace(
                "__CT_ESON_HASH_GLOBAL__",
                EsonHashGlobalName,
                StringComparison.Ordinal)
            .Replace(
                "__CT_ESON_SHA__",
                IllustratorEsonRuntime.ExpectedSha256,
                StringComparison.Ordinal);
    private const string EsonGlobalName = "__comtool_v2_eson";
    private const string EsonHashGlobalName =
        "__comtool_v2_eson_sha256";

    public static ScriptEvalRequest ParseRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("script.eval input must be a JSON object.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind",
            "source",
            "effects",
            "args",
            "resultMode"
        };

        foreach (var property in input.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new ArgumentException(
                    $"Unknown script.eval input field '{property.Name}'.");
        }

        if (!input.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                "'kind' must be 'expression' or 'code'.");

        var kind = kindElement.GetString();
        if (kind is not ("expression" or "code"))
            throw new ArgumentException(
                "'kind' must be 'expression' or 'code'.");

        if (!input.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("'source' must be a string.");

        var source = sourceElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("'source' must be non-empty.");

        if (source.Length > MaxSourceChars)
            throw new ArgumentException(
                $"'source' exceeds the {MaxSourceChars} character limit.");

        if (input.TryGetProperty("effects", out var effectsElement) &&
            effectsElement.ValueKind != JsonValueKind.Null)
        {
            if (effectsElement.ValueKind != JsonValueKind.String)
                throw new ArgumentException(
                    "'effects' must be a mutation-class string.");

            var effects = effectsElement.GetString();
            if (effects == "read_only")
                throw new ArgumentException(
                    "Arbitrary script execution cannot declare itself read-only.");

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

        string argsJson;
        if (!input.TryGetProperty("args", out var argsElement) ||
            argsElement.ValueKind == JsonValueKind.Null)
        {
            argsJson = "[]";
        }
        else
        {
            if (argsElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("'args' must be a JSON array.");

            argsJson = argsElement.GetRawText();
            if (argsJson.Length > MaxArgsJsonChars)
                throw new ArgumentException(
                    $"'args' exceeds the {MaxArgsJsonChars} character transport limit.");
        }

        return new ScriptEvalRequest(
            kind,
            source,
            argsJson,
            ParseResultMode(input));
    }

    internal static string ParseResultMode(JsonElement input)
    {
        if (!input.TryGetProperty("resultMode", out var modeElement) ||
            modeElement.ValueKind == JsonValueKind.Null)
        {
            return "capture";
        }

        if (modeElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "'resultMode' must be 'capture' or 'discard'.");
        }

        var mode = modeElement.GetString();
        if (mode is not ("capture" or "discard"))
        {
            throw new ArgumentException(
                "'resultMode' must be 'capture' or 'discard'.");
        }

        return mode;
    }

    public static ScriptEvalOutcome Execute(
        object appObject,
        ScriptEvalRequest request,
        Func<object, string, int, string>? scriptExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        var executeScript =
            scriptExecutor ??
            ((object application, string source, int executionMode) =>
                IllustratorComInterop.ExecuteJavaScript(
                    application,
                    source,
                    executionMode));
        var wrapped = BuildWrapper(request);

        var raw = executeScript(
            appObject,
            wrapped.Source,
            NeverShowDebugger);

        if (IsEsonMissingEnvelope(raw))
        {
            EnsureEsonInstalled(
                appObject,
                executeScript);

            raw = executeScript(
                appObject,
                wrapped.Source,
                NeverShowDebugger);

            if (IsEsonMissingEnvelope(raw))
            {
                throw new HostAdapterException(
                    "eson_bootstrap_failed",
                    "Illustrator did not retain the ESON runtime after bootstrap.",
                    retryable: false,
                    ExecutionState.NotStarted);
            }
        }

        return ParseEnvelope(raw, wrapped);
    }

    private static void EnsureEsonInstalled(
        object appObject,
        Func<object, string, int, string> executeScript)
    {
        string result;
        try
        {
            result = executeScript(
                appObject,
                BuildEsonInstallerSource(),
                NeverShowDebugger);
        }
        catch (HostAdapterException ex)
        {
            // The bootstrap contains no user code and no document mutation.
            // Even if the scripting-engine global was partially installed,
            // the requested script itself has not started.
            throw new HostAdapterException(
                "eson_bootstrap_failed",
                ex.Message,
                ex.Retryable,
                ExecutionState.NotStarted,
                ex.HResultCode,
                ex);
        }

        if (!string.Equals(
                result,
                EsonReadyToken,
                StringComparison.Ordinal))
        {
            throw new HostAdapterException(
                "eson_bootstrap_failed",
                $"Illustrator ESON bootstrap returned '{result}'.",
                retryable: false,
                ExecutionState.NotStarted);
        }
    }

    internal static bool IsEsonMissingEnvelope(string raw) =>
        string.Equals(
            raw,
            EsonMissingEnvelope,
            StringComparison.Ordinal);

    internal static string BuildEsonInstallerSource() =>
        (
            """
            (function(){
            var __ct_g=$.global;
            if(!__ct_g){return 'comtool-eson-no-global';}
            var __ct_existing=__ct_g.__CT_ESON_GLOBAL__;
            if(__ct_existing&&
               __ct_g.__CT_ESON_HASH_GLOBAL__==='__CT_ESON_SHA__'&&
               typeof __ct_existing.parse==='function'&&
               typeof __ct_existing.stringify==='function'){
              return 'comtool-eson-ready';
            }
            var JSON={};
            """ +
            IllustratorEsonRuntime.Source +
            """
            
            if(typeof ESON!=='object'||!ESON||
               typeof ESON.parse!=='function'||
               typeof ESON.stringify!=='function'){
              return 'comtool-eson-invalid-runtime';
            }
            try{
              __ct_g.__CT_ESON_GLOBAL__=ESON;
              __ct_g.__CT_ESON_HASH_GLOBAL__='__CT_ESON_SHA__';
            }catch(__ct_install_error){
              return 'comtool-eson-install-error:'+String(__ct_install_error);
            }
            var __ct_verify=__ct_g.__CT_ESON_GLOBAL__;
            if(__ct_verify&&
               __ct_g.__CT_ESON_HASH_GLOBAL__==='__CT_ESON_SHA__'&&
               typeof __ct_verify.parse==='function'&&
               typeof __ct_verify.stringify==='function'){
              return 'comtool-eson-ready';
            }
            return 'comtool-eson-install-failed';
            }());
            """
        )
        .Replace(
            "__CT_ESON_GLOBAL__",
            EsonGlobalName,
            StringComparison.Ordinal)
        .Replace(
            "__CT_ESON_HASH_GLOBAL__",
            EsonHashGlobalName,
            StringComparison.Ordinal)
        .Replace(
            "__CT_ESON_SHA__",
            IllustratorEsonRuntime.ExpectedSha256,
            StringComparison.Ordinal);

    internal static ScriptWrapper BuildWrapper(ScriptEvalRequest request)
    {
        var argsLiteral = JsonSerializer.Serialize(
            request.ArgsJson);

        var prefix =
            """
            (function(){
            var __ct_g=$.global;
            var ESON=__ct_g&&__ct_g.__comtool_v2_eson;
            if(!ESON||
               __ct_g.__comtool_v2_eson_sha256!=='__CT_ESON_SHA__'||
               typeof ESON.parse!=='function'||
               typeof ESON.stringify!=='function'){
              return '{"__comtoolTransport":"eson_missing"}';
            }
            function __ct_validate_json(v,stack,depth){
              if(v===null||typeof v!=='object'){return;}
              if(depth>64){
                throw new Error('script result exceeds maximum serialization depth');
              }
              var i,k;
              for(i=0;i<stack.length;i++){
                if(stack[i]===v){
                  throw new Error('script result contains a cycle');
                }
              }
              stack.push(v);
              if(Object.prototype.toString.call(v)==='[object Array]'){
                for(i=0;i<v.length;i++){
                  __ct_validate_json(v[i],stack,depth+1);
                }
              }else{
                for(k in v){
                  if(Object.prototype.hasOwnProperty.call(v,k)){
                    __ct_validate_json(v[k],stack,depth+1);
                  }
                }
              }
              stack.pop();
            }
            function __ct_error(e){
              var line=-1;
              try{
                if(e&&e.line!==undefined){line=Number(e.line);}
              }catch(_ignore){}
              if(!isFinite(line)){line=-1;}
              try{
                return ESON.stringify({
                  ok:false,
                  name:e&&e.name?String(e.name):'Error',
                  message:e&&e.message?String(e.message):String(e),
                  line:line
                });
              }catch(_transport_error){
                return '{"ok":false,"name":"TransportError","message":"ESON error transport failed","line":-1}';
              }
            }
            try{
              var __ct_args=ESON.parse(__CT_ARGS_JSON__);
              var __ct_result=(function(){
            """;

        var sourcePrefix = request.Kind == "expression"
            ? "return (\n"
            : string.Empty;
        var sourceSuffix = request.Kind == "expression"
            ? "\n);"
            : string.Empty;

        var resultTransport = request.ResultMode == "discard"
            ? """
              var __ct_result_present=false;
              __ct_result=null;
              return ESON.stringify({ok:true,result:__ct_result,resultPresent:__ct_result_present});
              """
            : """
              var __ct_result_present=(__ct_result!==void 0);
              if(!__ct_result_present){__ct_result=null;}
              __ct_validate_json(__ct_result,[],0);
              return ESON.stringify({ok:true,result:__ct_result,resultPresent:__ct_result_present});
              """;

        var suffix =
            """
            
              }).apply(null,__ct_args);
            """ +
            resultTransport +
            """

            }catch(__ct_e){
              return __ct_error(__ct_e);
            }
            }());
            """;

        var prefixWithArgs = prefix
            .Replace(
                "__CT_ESON_SHA__",
                IllustratorEsonRuntime.ExpectedSha256,
                StringComparison.Ordinal)
            .Replace(
                "__CT_ARGS_JSON__",
                argsLiteral,
                StringComparison.Ordinal);

        var userSourceStartLine =
            CountLines(prefixWithArgs) +
            CountLines(sourcePrefix) - 1;

        var sourceLineCount = CountSourceLines(request.Source);

        return new ScriptWrapper(
            prefixWithArgs +
            sourcePrefix +
            request.Source +
            sourceSuffix +
            suffix,
            userSourceStartLine,
            sourceLineCount);
    }

    /// <summary>
    /// Wraps runtime-generated document-lifecycle script (a plain statement
    /// body ending in <c>return</c>) in the same ESON bootstrap gate and
    /// envelope shape as <c>script.eval</c>, without accepting caller source.
    ///
    /// Unlike <c>script.eval</c>, the mutation body is NOT retried by the
    /// transport: a rejected dispatch is reported as an ambiguous outcome so
    /// the runtime can require reconciliation instead of re-running a
    /// document mutation.
    /// </summary>
    internal static string BuildDocumentMutationWrapper(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return
            """
            (function(){
            var __ct_g=$.global;
            var ESON=__ct_g&&__ct_g.__comtool_v2_eson;
            if(!ESON||
               __ct_g.__comtool_v2_eson_sha256!=='__CT_ESON_SHA__'||
               typeof ESON.parse!=='function'||
               typeof ESON.stringify!=='function'){
              return '{"__comtoolTransport":"eson_missing"}';
            }
            function __ct_error(e){
              var line=-1;
              try{
                if(e&&e.line!==undefined){line=Number(e.line);}
              }catch(_ignore){}
              if(!isFinite(line)){line=-1;}
              try{
                return ESON.stringify({
                  ok:false,
                  kind:'failed',
                  message:e&&e.message?String(e.message):String(e),
                  line:line
                });
              }catch(_transport_error){
                return '{"ok":false,"kind":"failed","message":"ESON error transport failed","line":-1}';
              }
            }
            try{
              var __ct_r=(function(){
            __CT_BODY__
              }).apply(null,[]);
              if(!__ct_r||typeof __ct_r!=='object'||__ct_r.document===undefined){
                __ct_r={kind:'completed',document:(__ct_r===void 0?null:__ct_r)};
              }
              return ESON.stringify({
                ok:true,
                kind:__ct_r.kind===undefined?'completed':String(__ct_r.kind),
                document:__ct_r.document===undefined?null:__ct_r.document,
                requestId:__ct_r.requestId===undefined?null:__ct_r.requestId
              });
            }catch(__ct_e){
              return __ct_error(__ct_e);
            }
            }());
            """
            .Replace(
                "__CT_ESON_SHA__",
                IllustratorEsonRuntime.ExpectedSha256,
                StringComparison.Ordinal)
            .Replace(
                "__CT_BODY__",
                body,
                StringComparison.Ordinal);
    }

    internal static ScriptEvalOutcome ParseEnvelope(
        string raw,
        ScriptWrapper wrapper)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new HostAdapterException(
                "script_transport_invalid",
                "Illustrator returned an invalid script transport envelope.",
                retryable: false,
                ExecutionState.Ambiguous,
                ex.HResult,
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new HostAdapterException(
                    "script_transport_invalid",
                    "Illustrator returned a malformed script transport envelope.",
                    retryable: false,
                    ExecutionState.Ambiguous);
            }

            if (ok.GetBoolean())
            {
                if (!root.TryGetProperty("result", out var result))
                {
                    throw new HostAdapterException(
                        "script_transport_invalid",
                        "Successful script envelope did not contain a result.",
                        retryable: false,
                        ExecutionState.Ambiguous);
                }

                var resultPresent = true;
                if (root.TryGetProperty(
                        "resultPresent",
                        out var resultPresentElement))
                {
                    if (resultPresentElement.ValueKind is not (
                            JsonValueKind.True or
                            JsonValueKind.False))
                    {
                        throw new HostAdapterException(
                            "script_transport_invalid",
                            "Successful script envelope contained an invalid resultPresent flag.",
                            retryable: false,
                            ExecutionState.Ambiguous);
                    }

                    resultPresent = resultPresentElement.GetBoolean();
                }

                return new ScriptEvalOutcome(
                    true,
                    ProtocolValue.From(result),
                    resultPresent,
                    null);
            }

            var name =
                root.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : "Error";

            var message =
                root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : "Unknown ExtendScript error.";

            int? wrapperLine = null;
            if (root.TryGetProperty("line", out var lineElement) &&
                lineElement.ValueKind == JsonValueKind.Number &&
                lineElement.TryGetInt32(out var parsedLine) &&
                parsedLine > 0)
                wrapperLine = parsedLine;

            int? sourceLine = null;
            if (wrapperLine is not null)
            {
                var candidate =
                    wrapperLine.Value - wrapper.UserSourceStartLine + 1;

                if (candidate >= 1 &&
                    candidate <= wrapper.UserSourceLineCount)
                    sourceLine = candidate;
            }

            return new ScriptEvalOutcome(
                false,
                null,
                false,
                new ScriptEvalError(
                    name ?? "Error",
                    message ?? "Unknown ExtendScript error.",
                    wrapperLine,
                    sourceLine));
        }
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return lines;
    }

    private static int CountSourceLines(string source) =>
        source.Length == 0
            ? 1
            : source.Count(static ch => ch == '\n') + 1;
}

internal sealed record ScriptEvalRequest(
    string Kind,
    string Source,
    string ArgsJson,
    string ResultMode);

internal sealed record ScriptWrapper(
    string Source,
    int UserSourceStartLine,
    int UserSourceLineCount);

internal sealed record ScriptEvalOutcome(
    bool Ok,
    ProtocolValue? Value,
    bool ResultPresent,
    ScriptEvalError? Error);

internal sealed record ScriptEvalError(
    string Name,
    string Message,
    int? WrapperLine,
    int? SourceLine);
