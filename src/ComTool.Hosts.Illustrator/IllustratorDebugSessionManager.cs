using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

internal static class IllustratorDebuggerSupport
{
    private const string AddonRelativePath =
        @"lib\esdebugger-core\win\x64\esdcorelibinterface.node";

    public static DebuggerDependencyStatus Probe()
    {
        var nodePath = ResolveNodePath();
        var addonPath = ResolveAddonPath();

        if (nodePath is null)
        {
            return new DebuggerDependencyStatus(
                false,
                null,
                addonPath,
                "Node.js was not found. Set COMTOOL_NODE_PATH to an absolute node.exe path.");
        }

        if (addonPath is null)
        {
            return new DebuggerDependencyStatus(
                false,
                nodePath,
                null,
                "Adobe ExtendScript Debugger Core was not found. Set COMTOOL_ESD_ADDON_PATH to esdcorelibinterface.node.");
        }

        return new DebuggerDependencyStatus(
            true,
            nodePath,
            addonPath,
            null);
    }

    private static string? ResolveNodePath()
    {
        var configured = Environment.GetEnvironmentVariable(
            "COMTOOL_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "node.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string? ResolveAddonPath()
    {
        foreach (var name in new[]
                 {
                     "COMTOOL_ESD_ADDON_PATH",
                     "ESD_ADDON_PATH"
                 })
        {
            var configured = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(configured))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(configured);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch
            {
            }
        }

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return null;

        var extensionsRoot = Path.Combine(
            userProfile,
            ".vscode",
            "extensions");
        if (!Directory.Exists(extensionsRoot))
            return null;

        try
        {
            return Directory
                .EnumerateDirectories(
                    extensionsRoot,
                    "adobe.extendscript-debug-*",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(
                    static value => Path.GetFileName(value),
                    StringComparer.OrdinalIgnoreCase)
                .Select(value => Path.Combine(value, AddonRelativePath))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record DebuggerDependencyStatus(
    bool Supported,
    string? NodePath,
    string? AddonPath,
    string? Reason);

/// <summary>
/// Why a debugger bridge exchange failed. The value decides both the reported
/// execution state and whether the owning session survives, so it is always
/// taken from concrete transport evidence rather than from elapsed time or a
/// generic failure.
/// </summary>
internal enum DebuggerBridgeFault
{
    /// <summary>The request was accepted and the exchange completed.</summary>
    None,

    /// <summary>
    /// The child refused the request locally and provably sent nothing to
    /// Adobe. Nothing started, and a healthy session stays healthy.
    /// </summary>
    RejectedBeforeSend,

    /// <summary>
    /// The bridge child is gone. Nothing was submitted, but the session can
    /// never be used again.
    /// </summary>
    ChildUnavailable,

    /// <summary>
    /// The request reached the child and the reply did not arrive. The
    /// submission may or may not have reached Adobe, so this is ambiguous by
    /// policy and is never retried.
    /// </summary>
    TransportLostAfterSubmit
}

/// <summary>
/// Which transport step of one bridge exchange a fault was observed at. This
/// is decided from the write/flush fact and from where the child reported it,
/// never from elapsed time, so a phase can never imply more than was actually
/// known at the moment of failure.
/// </summary>
internal enum DebuggerBridgeFaultPhase
{
    /// <summary>No bridge fault was observed.</summary>
    None,

    /// <summary>Child spawn, handshake read, or addon load.</summary>
    BridgeStartup,

    /// <summary>
    /// The request line was never flushed. Provably unsent.
    /// </summary>
    ExchangePreSubmit,

    /// <summary>
    /// The request line was flushed and the exchange did not resolve
    /// cleanly. Possibly submitted.
    /// </summary>
    ExchangePostSubmit,

    /// <summary>Waiting for or parsing the reply line.</summary>
    ResponseRead
}

/// <summary>
/// A bounded, payload-free summary of the bridge child's captured stderr.
///
/// Raw stderr text is deliberately never forwarded. Node can echo a rejected
/// request line, a filesystem path, or a stack frame into stderr, so any free
/// text could smuggle eval source or host paths into a protocol payload. Only
/// fixed-vocabulary signals and counts cross this boundary.
/// </summary>
internal sealed record DebuggerStderrSummary(
    int LineCount,
    int CharCount,
    bool Truncated,
    IReadOnlyList<string> Signals);

/// <summary>
/// Classifies captured bridge stderr into a closed vocabulary. The classifier
/// reads the raw text but emits only the constants declared here, so no
/// caller-supplied or host-derived substring can reach the evidence payload.
/// </summary>
internal static class DebuggerStderrSignals
{
    internal const string AddonDigestMismatch =
        "addon_digest_mismatch";
    internal const string AddonLoadFailed =
        "addon_load_failed";
    internal const string NativeModuleLoadFailed =
        "native_module_load_failed";
    internal const string MalformedRequestLine =
        "malformed_request_line";
    internal const string ResourceExhausted =
        "resource_exhausted";
    internal const string StreamClosed =
        "stream_closed";
    internal const string UnhandledException =
        "unhandled_exception";
    internal const string Unclassified =
        "unclassified";

    /// <summary>Upper bound on inspected characters.</summary>
    internal const int MaxChars = 512;

    /// <summary>Upper bound on reported lines.</summary>
    internal const int MaxLines = 64;

    /// <summary>
    /// Evaluation order. The output list follows this order exactly, so the
    /// classification of a given stderr buffer is deterministic.
    /// </summary>
    private static readonly (string Signal, string[] Markers)[] Vocabulary =
    [
        (AddonDigestMismatch, ["digest mismatch"]),
        (AddonLoadFailed,
        [
            "COMTOOL_ESD_ADDON_PATH",
            "Cannot find module",
            "ERR_MODULE_NOT_FOUND"
        ]),
        (NativeModuleLoadFailed,
        [
            "ERR_DLOPEN_FAILED",
            "NODE_MODULE_VERSION",
            "was compiled against a different Node.js version",
            ".node"
        ]),
        (MalformedRequestLine,
        [
            "is not valid JSON",
            "Unexpected token",
            "JSON"
        ]),
        (ResourceExhausted,
        [
            "heap out of memory",
            "ENOMEM",
            "Array buffer allocation failed"
        ]),
        (StreamClosed,
        [
            "EPIPE",
            "ERR_STREAM_DESTROYED",
            "write after end",
            "premature close"
        ]),
        (UnhandledException,
        [
            "UnhandledPromiseRejection",
            "ERR_UNHANDLED",
            "throw er;",
            "Error:"
        ])
    ];

    public static DebuggerStderrSummary? Summarize(
        string? captured,
        bool truncated)
    {
        if (string.IsNullOrEmpty(captured))
            return null;

        var limited = captured.Length > MaxChars
            ? captured[..MaxChars]
            : captured;
        var lines = limited.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries);

        List<string> matched = [];
        foreach (var (signal, markers) in Vocabulary)
        {
            if (markers.Any(
                    marker => limited.Contains(
                        marker,
                        StringComparison.Ordinal)))
            {
                matched.Add(signal);
            }
        }

        if (matched.Count == 0)
            matched.Add(Unclassified);

        return new DebuggerStderrSummary(
            lines.Length > MaxLines
                ? MaxLines
                : lines.Length,
            limited.Length,
            truncated,
            matched);
    }
}

/// <summary>
/// Bounded transport facts captured at the instant a bridge exchange failed.
/// Every field is a closed-vocabulary token, a small integer, or an
/// already-redacted count. There is deliberately no field for a request body,
/// a response body, a filesystem path, a lease identifier, or document
/// content.
/// </summary>
internal sealed record DebuggerBridgeDiagnostics(
    DebuggerBridgeFaultPhase Phase,
    string? ReportedPhase,
    IReadOnlyList<int>? EventSerials,
    IReadOnlyList<int>? ResultSerials,
    DebuggerStderrSummary? Stderr)
{
    /// <summary>Upper bound on reported serial correlation handles.</summary>
    internal const int MaxSerials = 16;

    /// <summary>
    /// Extracts whatever correlation handles the child actually reported. A
    /// reply that carries no events yields nulls rather than invented zeros,
    /// so "not reported" stays distinguishable from "reported as 0".
    /// </summary>
    public static DebuggerBridgeDiagnostics From(
        DebuggerBridgeFaultPhase phase,
        JsonElement? response,
        string? stderr,
        bool stderrTruncated)
    {
        string? reportedPhase = null;
        IReadOnlyList<int>? eventSerials = null;
        IReadOnlyList<int>? resultSerials = null;

        if (response is { ValueKind: JsonValueKind.Object } root)
        {
            if (root.TryGetProperty("phase", out var phaseValue) &&
                phaseValue.ValueKind ==
                JsonValueKind.String)
            {
                // Only the two phases the bridge contract defines are
                // accepted. An unrecognised token is dropped rather than
                // forwarded, so a child can never inject free text here.
                var token = phaseValue.GetString();
                reportedPhase = token is
                    "before_send" or "send_attempted"
                        ? token
                        : null;
            }

            if (root.TryGetProperty("events", out var events) &&
                events.ValueKind == JsonValueKind.Array)
            {
                eventSerials = ReadSerials(
                    events,
                    "serialNumber");
                resultSerials = ReadSerials(
                    events,
                    "resultSerial");
            }
        }

        return new DebuggerBridgeDiagnostics(
            phase,
            reportedPhase,
            eventSerials,
            resultSerials,
            DebuggerStderrSignals.Summarize(
                stderr,
                stderrTruncated));
    }

    private static IReadOnlyList<int>? ReadSerials(
        JsonElement events,
        string property)
    {
        List<int>? values = null;

        foreach (var element in events.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(
                    property,
                    out var value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var serial))
            {
                continue;
            }

            values ??= [];
            if (!values.Contains(serial))
                values.Add(serial);

            if (values.Count >= MaxSerials)
                break;
        }

        return values;
    }
}

/// <summary>
/// The closed set of facts a debugger fault may report. Every field is a
/// scalar, a hash, a strong process generation, or an already-bounded
/// diagnostic. The type has no field for a source string, an eval payload, a
/// breakpoint file, a filesystem path, a lease identifier, or document
/// content, so no call site can leak one by accident.
/// </summary>
internal sealed record DebuggerFaultFacts(
    string Operation,
    string? SessionId,
    string? Command,
    string? Engine,
    string? AppSpec,
    int TargetProcessId,
    DateTimeOffset TargetProcessStartedAt,
    int? ChildProcessId,
    DateTimeOffset? ChildProcessStartedAt,
    bool? ChildAlive,
    string? AddonSha256,
    string? BridgeSha256,
    string? NodeVersion,
    DebuggerBridgeFault? BridgeFault,
    DebuggerBridgeDiagnostics? Diagnostics,
    string? SessionState,
    string? TerminalReason,
    bool? NodeResolved,
    bool? AddonResolved);

/// <summary>
/// Closed-vocabulary wire tokens for the debugger enums, so a reported value
/// never depends on <c>Enum.ToString</c> formatting and renaming a member
/// cannot silently change an operator-visible token.
/// </summary>
internal static class DebuggerFaultText
{
    public static string BridgeFault(
        DebuggerBridgeFault fault) =>
        fault switch
        {
            DebuggerBridgeFault.RejectedBeforeSend =>
                "rejected_before_send",
            DebuggerBridgeFault.ChildUnavailable =>
                "child_unavailable",
            DebuggerBridgeFault
                .TransportLostAfterSubmit =>
                "transport_lost_after_submit",
            _ => "none"
        };

    public static string Phase(
        DebuggerBridgeFaultPhase phase) =>
        phase switch
        {
            DebuggerBridgeFaultPhase.BridgeStartup =>
                "bridge_startup",
            DebuggerBridgeFaultPhase.ExchangePreSubmit =>
                "exchange_pre_submit",
            DebuggerBridgeFaultPhase.ExchangePostSubmit =>
                "exchange_post_submit",
            DebuggerBridgeFaultPhase.ResponseRead =>
                "response_read",
            _ => "none"
        };
}

/// <summary>
/// Builds the single additive diagnostics channel for debugger failures. The
/// evidence is attached to a failure that already exists; it never changes
/// that failure's kind, message, retryability, execution state, target state,
/// or status, and it is never consulted to decide session survival.
/// </summary>
internal static class DebuggerFaultEvidence
{
    internal const string Kind = "debug.session.fault";

    public static EvidenceItem Build(
        DebuggerFaultFacts facts) =>
        new(
            Kind,
            JsonSerializer.SerializeToElement(
                new
                {
                    operation = facts.Operation,
                    sessionId = facts.SessionId,
                    command = facts.Command,
                    engine = facts.Engine,
                    appSpec = facts.AppSpec,
                    transport = "estk3",
                    target = new
                    {
                        processId = facts.TargetProcessId,
                        processStartedAt =
                            facts.TargetProcessStartedAt
                    },
                    child = facts.ChildProcessId is
                        null
                        ? null
                        : new
                        {
                            processId = facts.ChildProcessId,
                            processStartedAt =
                                facts.ChildProcessStartedAt,
                            alive = facts.ChildAlive
                        },
                    // Hashes and a safe version only. No filesystem path.
                    provenance = new
                    {
                        nodeVersion = facts.NodeVersion,
                        addonSha256 = facts.AddonSha256,
                        bridgeSha256 = facts.BridgeSha256
                    },
                    bridgeFault =
                        facts.BridgeFault is { } fault
                            ? DebuggerFaultText.BridgeFault(
                                fault)
                            : null,
                    bridgeFaultPhase =
                        facts.Diagnostics is { } d
                            ? DebuggerFaultText.Phase(
                                d.Phase)
                            : null,
                    bridgeReportedPhase =
                        facts.Diagnostics?.ReportedPhase,
                    serials = facts.Diagnostics is { } s
                        ? new
                        {
                            @event = s.EventSerials,
                            result = s.ResultSerials
                        }
                        : null,
                    // Signals only, never raw stderr text.
                    stderr =
                        facts.Diagnostics?.Stderr is
                            { } stderr
                            ? new
                            {
                                lineCount =
                                    stderr.LineCount,
                                charCount =
                                    stderr.CharCount,
                                truncated =
                                    stderr.Truncated,
                                signals = stderr.Signals
                            }
                            : null,
                    sessionState = facts.SessionState,
                    terminalReason = facts.TerminalReason,
                    // Booleans only, so a missing dependency is diagnosable
                    // without publishing where it was looked for.
                    dependencies =
                        facts.NodeResolved is null
                            ? null
                            : new
                            {
                                nodeResolved =
                                    facts.NodeResolved,
                                addonResolved =
                                    facts.AddonResolved
                            }
                }));
}

/// <summary>
/// The command-capable lifecycle state of the worker-owned debugger session.
/// Only <see cref="Open"/> may accept commands. Every other value is terminal
/// and is reported, never silently reconnected.
/// </summary>
internal enum DebugSessionState
{
    /// <summary>No session has ever existed in this worker.</summary>
    None,

    /// <summary>Constructed and connected but not yet published to callers.</summary>
    Opening,

    /// <summary>Published and accepting commands.</summary>
    Open,

    /// <summary>Released on request or by idle expiry.</summary>
    Closed,

    /// <summary>
    /// Permanently unusable: the bridge child died, a submitted-or-possibly-
    /// submitted exchange was lost, or the bound Illustrator generation was
    /// lost. Recovery is an explicit reopen, never an automatic rebind.
    /// </summary>
    Faulted
}

/// <summary>Why a session stopped being command-capable.</summary>
internal enum DebugSessionTerminalReason
{
    None,
    ClosedByCaller,
    CloseFailed,
    IdleExpired,
    WorkerDisposed,
    TargetGenerationLost,
    BridgeChildUnavailable,
    BridgeTransportLostAfterSubmit,
    BridgeStartupFailed
}

/// <summary>
/// The single most recent terminal session record. It holds immutable scalars
/// only — never a bridge, a process, or a timer — so it can report history but
/// can never act as a second authority, submit anything, or keep a child alive.
/// At most one record is retained; it is overwritten, never appended. Human
/// message text is deliberately absent: it belongs in bounded diagnostic
/// evidence, not in an ordinary status snapshot.
/// </summary>
internal sealed record TerminalSessionRecord(
    string SessionId,
    string Engine,
    DebugSessionState State,
    DebugSessionTerminalReason Reason,
    string AppSpec,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastUsedAt,
    int IdleTimeoutMs,
    int CommandCount,
    int TargetProcessId,
    DateTimeOffset TargetProcessStartedAt,
    int ChildProcessId,
    DateTimeOffset ChildProcessStartedAt,
    string AddonSha256,
    string BridgeSha256);

/// <summary>
/// A session-state or lease-authority failure. These are not caller input
/// errors, and rejecting one never destroys the session that refused it.
/// </summary>
internal sealed class DebugSessionStateException(
    string kind,
    string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

internal sealed class IllustratorDebugSessionManager : IDisposable
{
    public const string OpenOperation = "debug.session.open";
    public const string StatusOperation = "debug.session.status";
    public const string CommandOperation = "debug.session.command";
    public const string CloseOperation = "debug.session.close";

    internal const int DefaultIdleTimeoutMs = 120_000;
    internal const int MinIdleTimeoutMs = 5_000;
    internal const int MaxIdleTimeoutMs = 600_000;
    internal const int DefaultCommandTimeoutMs = 5_000;
    internal const int DefaultEvalTimeoutMs = 8_000;
    internal const int MinCommandTimeoutMs = 100;
    internal const int MaxCommandTimeoutMs = 600_000;
    private const int MaxSourceUtf8Bytes = 1024 * 1024;
    private const int MaxBreakpoints = 256;

    /// <summary>
    /// The exact command set <see cref="BuildCommandXml"/> can build. Kept in
    /// one place so an unsupported command is rejected during validation,
    /// before any session or bridge state is involved.
    /// </summary>
    private static readonly HashSet<string> SupportedCommands =
        new(StringComparer.Ordinal)
        {
            "eval",
            "set-breakpoints",
            "get-breakpoints",
            "get-break",
            "get-frame",
            "set-frame",
            "get-properties",
            "continue",
            "break",
            "halt",
            "stepover",
            "stepinto",
            "stepout"
        };

    private readonly HostTargetIdentity _identity;
    private readonly Func<DebuggerDependencyStatus> _probeDependencies;
    private readonly Func<DebuggerBridgeLaunch, IDebuggerBridge> _createBridge;
    private readonly Action<HostTargetIdentity> _verifyTargetGeneration;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly Timer _idleTimer;
    private ActiveDebugSession? _active;
    private TerminalSessionRecord? _terminal;
    private int _disposed;

    public IllustratorDebugSessionManager(
        HostTargetIdentity identity)
        : this(
            identity,
            IllustratorDebuggerSupport.Probe,
            static launch => new DebuggerBridge(
                launch.NodePath,
                launch.AddonPath,
                launch.AddonSha256,
                launch.BridgePath,
                launch.AppSpecifier),
            static current => VerifyTargetGeneration(current),
            static () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Test-only seam. The production path always uses the real dependency
    /// probe, the real bridge child, the real process-generation check, and the
    /// real wall clock; injecting them lets deterministic tests prove the
    /// debugger command contract and session-lifecycle rules without an Adobe
    /// host, a spawned process, or a real idle wait. No Node-side or alternate
    /// debugger implementation is introduced, and no static mutable state is
    /// involved.
    /// </summary>
    internal IllustratorDebugSessionManager(
        HostTargetIdentity identity,
        Func<DebuggerDependencyStatus> probeDependencies,
        Func<DebuggerBridgeLaunch, IDebuggerBridge> createBridge,
        Action<HostTargetIdentity>? verifyTargetGeneration = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _identity = identity;
        _probeDependencies = probeDependencies;
        _createBridge = createBridge;
        _verifyTargetGeneration = verifyTargetGeneration ??
                                   (static current =>
                                       VerifyTargetGeneration(current));
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _idleTimer = new Timer(
            OnIdleTimer,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public OperationResult Execute(OperationRequest request)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        try
        {
            return request.Operation switch
            {
                OpenOperation => Open(request),
                StatusOperation => Status(request),
                CommandOperation => Command(request),
                CloseOperation => Close(request),
                _ => throw new ArgumentException(
                    $"Unsupported debugger operation '{request.Operation}'.")
            };
        }
        catch (DebuggerBridgeException ex)
        {
            // Single authoritative mapping from a bridge fault to a reported
            // host failure. Diagnostics are attached to the result of that
            // mapping; they are never an input to it.
            throw MapBridgeFailure(
                ex,
                BuildBridgeFaultEvidence(request, ex));
        }
        catch (DebugSessionStateException ex)
        {
            // A session-identity or lease-authority refusal is not a malformed
            // request, and it is never a reason to destroy the session that
            // refused it. The rejection is reported as not-started.
            return FailureNotStarted(
                request,
                ex.Kind,
                ex.Message,
                ["inspect_debug_session"],
                BuildSessionStateEvidence(request));
        }
        catch (ArgumentException ex)
        {
            return InvalidRequest(
                request,
                "invalid_debug_request",
                ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
            return;

        ActiveDebugSession? active;
        lock (_gate)
        {
            active = _active;
            if (active is not null)
            {
                RetireLocked(
                    active,
                    DebugSessionState.Closed,
                    DebugSessionTerminalReason.WorkerDisposed);
            }
        }

        active?.Bridge.Dispose();
        _idleTimer.Dispose();
    }

    private OperationResult Open(OperationRequest request)
    {
        var leaseId = RequireLeaseId(request);
        EnsureOnlyFields(
            request.Input,
            "engine",
            "appSpec",
            "idleTimeoutMs",
            "connectTimeoutMs");

        var engine = ReadOptionalString(
                request.Input,
                "engine",
                128)
            ?? "main";
        var appSpec = ReadOptionalString(
            request.Input,
            "appSpec",
            256);
        var idleTimeoutMs = ReadOptionalInt(
                request.Input,
                "idleTimeoutMs",
                MinIdleTimeoutMs,
                MaxIdleTimeoutMs)
            ?? DefaultIdleTimeoutMs;
        var connectTimeoutMs = ReadOptionalInt(
                request.Input,
                "connectTimeoutMs",
                100,
                60_000)
            ?? DefaultCommandTimeoutMs;

        try
        {
            EnsureTargetGeneration();
        }
        catch (HostAdapterException ex)
        {
            // Same pre-existing target-generation failure, reported
            // unchanged, plus bounded evidence about which strong generation
            // was expected.
            throw new HostAdapterException(
                ex.Kind,
                ex.Message,
                retryable: false,
                ex.Execution,
                ex.HResultCode,
                ex,
                [BuildSessionStateEvidence(request)]);
        }

        var dependencies = _probeDependencies();
        if (!dependencies.Supported ||
            dependencies.NodePath is null ||
            dependencies.AddonPath is null)
        {
            return Unsupported(
                request,
                "debugger_dependency_unavailable",
                dependencies.Reason ??
                "ExtendScript debugger dependencies are unavailable.",
                BuildDependencyEvidence(
                    request,
                    engine,
                    dependencies));
        }

        lock (_gate)
        {
            if (_active is not null)
            {
                return FailureCompleted(
                    request,
                    "debug_session_already_open",
                    "This target worker already owns an active debugger session. Close it before opening another.",
                    BuildSessionStateEvidenceLocked(request));
            }

            var bridgeAsset =
                DebuggerBridgeAsset.Materialize();
            // Pin the exact native debugger bytes *before* the child process
            // exists. The bridge re-hashes this exact path immediately before
            // require(), so a replaced addon fails closed instead of silently
            // loading different debugger code than the reported provenance.
            var addonSha256 = ComputeSha256Hex(
                dependencies.AddonPath);
            var bridge = _createBridge(
                new DebuggerBridgeLaunch(
                    dependencies.NodePath,
                    dependencies.AddonPath,
                    addonSha256,
                    bridgeAsset.Path,
                    appSpec));

            try
            {
                var exchange = bridge.Exchange(
                    "<connect/>",
                    connectTimeoutMs);
                var engines = ExtractEngines(exchange);
                if (engines.Count > 0 &&
                    !engines.Contains(
                        engine,
                        StringComparer.Ordinal))
                {
                    bridge.Dispose();
                    return FailureCompleted(
                        request,
                        "debug_engine_not_found",
                        $"Debugger engine '{engine}' was not reported by Illustrator. Available engines: {string.Join(", ", engines)}.",
                        DebuggerFaultEvidence.Build(
                            OpenFaultFactsLocked(
                                request,
                                engine,
                                dependencies,
                                bridgeAsset,
                                addonSha256,
                                bridge)));
                }

                var openedAt = _utcNow();
                var session = new ActiveDebugSession(
                    $"dbg-{Guid.NewGuid():N}",
                    leaseId,
                    engine,
                    idleTimeoutMs,
                    openedAt,
                    openedAt,
                    bridge,
                    addonSha256,
                    bridgeAsset.Sha256,
                    dependencies,
                    bridgeAsset);
                _active = session;
                ScheduleIdleLocked(session);

                var payload = JsonSerializer.SerializeToElement(
                    new
                    {
                        sessionId = session.SessionId,
                        appSpec = bridge.AppSpecifier,
                        engine,
                        engines,
                        idleTimeoutMs,
                        openedAt,
                        transport = "estk3",
                        workerOwned = true,
                        child = new
                        {
                            processId = bridge.ProcessId,
                            processStartedAt =
                                bridge.ProcessStartedAt
                        },
                        provenance = new
                        {
                            nodePath =
                                dependencies.NodePath,
                            nodeVersion =
                                bridge.NodeVersion,
                            addonSha256 =
                                addonSha256,
                            bridgeSha256 =
                                bridgeAsset.Sha256
                        }
                    });

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    new EvidenceItem(
                        "debug.session.provenance",
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                sessionId =
                                    session.SessionId,
                                appSpec =
                                    bridge.AppSpecifier,
                                engine,
                                addonSha256 =
                                    addonSha256,
                                bridgeSha256 =
                                    bridgeAsset.Sha256
                            })));
            }
            catch (DebuggerBridgeException ex)
            {
                // The session was never published, so the published-state
                // snapshot cannot describe this fault. The open-specific
                // facts are captured here, before the child is torn down.
                bridge.Dispose();
                throw MapBridgeFailure(
                    ex,
                    DebuggerFaultEvidence.Build(
                        OpenFaultFactsLocked(
                            request,
                            engine,
                            dependencies,
                            bridgeAsset,
                            addonSha256,
                            bridge,
                            ex)));
            }
            catch
            {
                bridge.Dispose();
                throw;
            }
        }
    }

    private OperationResult Status(OperationRequest request)
    {
        // Observation only. No lease is required to see whether this worker
        // currently owns a usable debugger session: the lease protects *use*
        // of the debugger, not *sight* of it, and nothing lease-owned is
        // echoed back. This never contacts Adobe, never touches user state,
        // and never creates, reopens, or rebinds a session.
        EnsureOnlyFields(request.Input);

        lock (_gate)
        {
            // Internal bookkeeping only. Strong child-generation evidence that
            // the child is gone retires the record so status cannot keep
            // claiming an open session over a dead child.
            ObserveChildGenerationLocked();

            var active = _active;
            if (active is not null)
            {
                return Success(
                    request,
                    ProtocolValue.From(
                        BuildStatusPayloadLocked(active)));
            }

            return Success(
                request,
                ProtocolValue.From(
                    BuildTerminalStatusPayloadLocked(_terminal)));
        }
    }

    private JsonElement BuildStatusPayloadLocked(
        ActiveDebugSession session)
    {
        var childAlive = IsGenerationAlive(
            session.Bridge.ProcessId,
            session.Bridge.ProcessStartedAt);

        return JsonSerializer.SerializeToElement(
            new
            {
                sessionState = "open",
                sessionId = session.SessionId,
                engine = session.Engine,
                appSpec = session.Bridge.AppSpecifier,
                openedAt = session.OpenedAt,
                lastUsedAt = session.LastUsedAt,
                lastCommandAt = session.LastCommandAt,
                idleTimeoutMs = session.IdleTimeoutMs,
                commandCount = session.CommandCount,
                transport = "estk3",
                workerOwned = true,
                target = new
                {
                    processId = _identity.ProcessId,
                    processStartedAt =
                        _identity.ProcessStartedAt
                },
                child = new
                {
                    processId = session.Bridge.ProcessId,
                    processStartedAt =
                        session.Bridge.ProcessStartedAt,
                    alive = childAlive
                },
                // Hashes and safe versions only. No filesystem path, lease ID,
                // script source, breakpoint condition, or document content.
                provenance = new
                {
                    nodeVersion = session.Bridge.NodeVersion,
                    addonSha256 = session.AddonSha256,
                    bridgeSha256 = session.BridgeSha256
                },
                terminalReason = (string?)null
            });
    }

    private static JsonElement BuildTerminalStatusPayloadLocked(
        TerminalSessionRecord? terminal)
    {
        if (terminal is null)
        {
            return JsonSerializer.SerializeToElement(
                new
                {
                    sessionState = "none",
                    sessionId = (string?)null,
                    engine = (string?)null,
                    appSpec = (string?)null,
                    openedAt = (DateTimeOffset?)null,
                    lastUsedAt = (DateTimeOffset?)null,
                    lastCommandAt = (DateTimeOffset?)null,
                    idleTimeoutMs = (int?)null,
                    commandCount = 0,
                    transport = (string?)null,
                    workerOwned = true,
                    target = (object?)null,
                    child = (object?)null,
                    provenance = (object?)null,
                    terminalReason = (string?)null
                });
        }

        // The retained record is history, not authority: it carries no bridge,
        // no process handle, and no way to submit anything.
        return JsonSerializer.SerializeToElement(
            new
            {
                sessionState = terminal.State switch
                {
                    DebugSessionState.Faulted => "faulted",
                    _ => "closed"
                },
                sessionId = terminal.SessionId,
                engine = terminal.Engine,
                appSpec = terminal.AppSpec,
                openedAt = terminal.OpenedAt,
                lastUsedAt = terminal.LastUsedAt,
                lastCommandAt = (DateTimeOffset?)null,
                idleTimeoutMs = terminal.IdleTimeoutMs,
                commandCount = terminal.CommandCount,
                transport = "estk3",
                workerOwned = true,
                target = new
                {
                    processId = terminal.TargetProcessId,
                    processStartedAt =
                        terminal.TargetProcessStartedAt
                },
                child = new
                {
                    processId = terminal.ChildProcessId,
                    processStartedAt =
                        terminal.ChildProcessStartedAt,
                    alive = IsGenerationAlive(
                        terminal.ChildProcessId,
                        terminal.ChildProcessStartedAt)
                },
                provenance = new
                {
                    nodeVersion = (string?)null,
                    addonSha256 = terminal.AddonSha256,
                    bridgeSha256 = terminal.BridgeSha256
                },
                terminalReason =
                    TerminalReasonText(terminal.Reason)
            });
    }

    private static string TerminalReasonText(
        DebugSessionTerminalReason reason) =>
        reason switch
        {
            DebugSessionTerminalReason.ClosedByCaller =>
                "closed_by_caller",
            DebugSessionTerminalReason.CloseFailed =>
                "close_failed",
            DebugSessionTerminalReason.IdleExpired =>
                "idle_expired",
            DebugSessionTerminalReason.WorkerDisposed =>
                "worker_disposed",
            DebugSessionTerminalReason.TargetGenerationLost =>
                "target_generation_lost",
            DebugSessionTerminalReason.BridgeChildUnavailable =>
                "bridge_child_unavailable",
            DebugSessionTerminalReason
                .BridgeTransportLostAfterSubmit =>
                "bridge_transport_lost_after_submit",
            _ => "none"
        };

    /// <summary>
    /// Retires a session and retains exactly one bounded terminal record. The
    /// record stores immutable scalars only, so it can never be used to submit
    /// anything or to keep a child alive. The caller keeps the returned session
    /// so the live bridge can be torn down outside the lock.
    /// </summary>
    private ActiveDebugSession RetireLocked(
        ActiveDebugSession session,
        DebugSessionState state,
        DebugSessionTerminalReason reason)
    {
        // Safe on an already-retired session: only the live slot owner may
        // write the single terminal record, so a double retire can never
        // clobber a newer session's history or resurrect authority.
        if (!ReferenceEquals(_active, session))
            return session;

        _active = null;

        _idleTimer.Change(
            Timeout.Infinite,
            Timeout.Infinite);

        _terminal = new TerminalSessionRecord(
            session.SessionId,
            session.Engine,
            state,
            reason,
            session.Bridge.AppSpecifier,
            session.OpenedAt,
            session.LastUsedAt,
            session.IdleTimeoutMs,
            session.CommandCount,
            _identity.ProcessId,
            _identity.ProcessStartedAt,
            session.Bridge.ProcessId,
            session.Bridge.ProcessStartedAt,
            session.AddonSha256,
            session.BridgeSha256);

        return session;
    }

    /// <summary>
    /// Generation-safe liveness. A recycled PID is not the same process, so
    /// both the PID and the captured start instant must match.
    /// </summary>
    internal static bool IsGenerationAlive(
        int processId,
        DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(
                processId);
            if (process.HasExited)
                return false;

            process.Refresh();
            return new DateTimeOffset(process.StartTime)
                .ToUniversalTime()
                .Ticks == startedAt.ToUniversalTime().Ticks;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Retires the active session when strong child-generation evidence proves
    /// the child is gone, so a snapshot can never report an open session over a
    /// dead child. Never creates, reopens, or rebinds anything.
    /// </summary>
    private void ObserveChildGenerationLocked()
    {
        var active = _active;
        if (active is null)
            return;

        if (IsGenerationAlive(
                active.Bridge.ProcessId,
                active.Bridge.ProcessStartedAt))
            return;

        RetireLocked(
            active,
            DebugSessionState.Faulted,
            DebugSessionTerminalReason.BridgeChildUnavailable)
            .Bridge.Dispose();
    }

    private OperationResult Command(OperationRequest request)
    {
        // ------------------------------------------------------------------
        // Phase 1 - validate and normalize. Nothing in this phase reads or
        // mutates the active session, the bridge child, or the idle timer.
        // Every failure here is deterministic and pre-submit, so it is
        // reported as not-started and leaves any healthy session untouched.
        // ------------------------------------------------------------------
        var leaseId = RequireLeaseId(request);
        EnsureInputObject(request.Input);

        var sessionId = ReadRequiredString(
            request.Input,
            "sessionId",
            128);
        var command = ReadRequiredString(
            request.Input,
            "command",
            64);

        // The command name is checked before anything else can fail so an
        // unsupported command is always the reported reason, and never a
        // timeout or field error about a request that would never be sent.
        EnsureKnownCommand(command);

        var timeoutMs = ReadOptionalInt(
                request.Input,
                "timeoutMs",
                MinCommandTimeoutMs,
                MaxCommandTimeoutMs)
            ?? ResolveDefaultCommandTimeoutMs(command);

        lock (_gate)
        {
            // Session and lease authority. A refusal here throws
            // DebugSessionStateException, which is reported without touching
            // the session that refused the request.
            var session = RequireActiveSession(
                sessionId,
                leaseId);

            // Command-specific field validation and ESTK3 XML construction are
            // still pure: they read only the request and the session's engine.
            // A throw here is pre-submit, so the session stays open and usable.
            var normalized = NormalizeCommand(
                request.Input,
                command,
                session.Engine,
                timeoutMs);

            try
            {
                EnsureTargetGeneration();
            }
            catch (HostAdapterException ex)
            {
                // The bound Illustrator generation is gone, so the session can
                // never be used again. Retire it as faulted before reporting
                // the not-started failure so status can never keep claiming an
                // open session. No fresh target discovery or substitution.
                RetireLocked(
                        session,
                        DebugSessionState.Faulted,
                        DebugSessionTerminalReason.TargetGenerationLost)
                    .Bridge.Dispose();
                throw new HostAdapterException(
                    ex.Kind,
                    ex.Message,
                    retryable: false,
                    ex.Execution,
                    ex.HResultCode,
                    ex,
                    // The retire above already published the terminal record,
                    // so the evidence truthfully reports the session as
                    // faulted rather than open.
                    [BuildSessionStateEvidenceLocked(request)]);
            }

            // ------------------------------------------------------------------
            // Phase 2 - execute. Only failures past this line may tear the
            // session down, and only when bridge evidence says the request
            // could have reached Adobe or that the child is unusable.
            // ------------------------------------------------------------------
            _idleTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite);

            JsonElement exchange;
            try
            {
                exchange = session.Bridge.Exchange(
                    normalized.Xml,
                    normalized.TimeoutMs);
            }
            catch (DebuggerBridgeException ex)
            {
                if (ex.Fault == DebuggerBridgeFault.RejectedBeforeSend)
                {
                    // The child provably sent nothing to Adobe and is still
                    // running. Nothing started and the session is intact.
                    session.LastUsedAt = _utcNow();
                    ScheduleIdleLocked(session);
                }
                else
                {
                    // The child is gone, or the request may have reached
                    // Adobe. Both end the session: a broken or
                    // unaccounted-for bridge must never be reused for a later
                    // command.
                    RetireLocked(
                            session,
                            DebugSessionState.Faulted,
                            ex.Fault == DebuggerBridgeFault.ChildUnavailable
                                ? DebugSessionTerminalReason
                                    .BridgeChildUnavailable
                                : DebugSessionTerminalReason
                                    .BridgeTransportLostAfterSubmit)
                        .Bridge.Dispose();
                }

                // Snapshotted after the survival decision, so the evidence
                // describes exactly the session state the caller will observe.
                // Rejected-before-send therefore reports "open" and a lost
                // submission reports "faulted".
                throw MapBridgeFailure(
                    ex,
                    DebuggerFaultEvidence.Build(
                        SnapshotFaultFactsLocked(
                            request,
                            command,
                            session.Engine,
                            ex)));
            }

            var completedAt = _utcNow();
            session.LastUsedAt = completedAt;
            session.LastCommandAt = completedAt;
            session.CommandCount++;
            ScheduleIdleLocked(session);

            var state = InferExecutionState(
                command,
                exchange);
            var response = NormalizeDebuggerResponse(
                command,
                exchange);
            var payload = JsonSerializer.SerializeToElement(
                new
                {
                    sessionId = session.SessionId,
                    command,
                    state,
                    appSpec = session.Bridge.AppSpecifier,
                    engine = session.Engine,
                    response,
                    transport = exchange
                });

            return Success(
                request,
                ProtocolValue.From(payload));
        }
    }

    /// <summary>
    /// Resolves the effective per-command timeout. Unknown command names get the
    /// generic default so the unsupported-command rejection stays the reported
    /// failure instead of an unrelated timeout error.
    /// </summary>
    private static int ResolveDefaultCommandTimeoutMs(
        string command) =>
        string.Equals(
            command,
            "eval",
            StringComparison.Ordinal)
            ? DefaultEvalTimeoutMs
            : DefaultCommandTimeoutMs;

    private static void EnsureKnownCommand(string command)
    {
        if (!SupportedCommands.Contains(command))
            throw new ArgumentException(
                $"Unsupported debugger command '{command}'.");
    }

    /// <summary>
    /// The complete, side-effect-free debugger command contract: validate every
    /// command-specific field and build the exact ESTK3 body that will be
    /// submitted. Exposed for deterministic tests so contract coverage never
    /// requires a live Adobe host or a spawned bridge child.
    /// </summary>
    internal static NormalizedDebugCommand NormalizeCommand(
        JsonElement input,
        string command,
        string engine,
        int timeoutMs)
    {
        EnsureInputObject(input);
        EnsureKnownCommand(command);

        return new NormalizedDebugCommand(
            ReadRequiredString(input, "sessionId", 128),
            command,
            engine,
            timeoutMs,
            BuildCommandXml(input, command, engine, timeoutMs));
    }

    private OperationResult Close(OperationRequest request)
    {
        // Close deliberately skips EnsureTargetGeneration(). If the host died
        // or the generation was recycled, the worker still owns a live child
        // bridge process that must be torn down. Cleanup must not be refused
        // precisely when it is most needed.
        var leaseId = RequireLeaseId(request);
        EnsureOnlyFields(
            request.Input,
            "sessionId");

        var sessionId = ReadRequiredString(
            request.Input,
            "sessionId",
            128);

        ActiveDebugSession session;
        lock (_gate)
        {
            session = RequireActiveSession(
                sessionId,
                leaseId);

            // Live authority is retired *before* teardown. From here the
            // session can no longer accept a command no matter what the
            // teardown does, and it is never reactivated.
            RetireLocked(
                session,
                DebugSessionState.Closed,
                DebugSessionTerminalReason.ClosedByCaller);
        }

        DebuggerBridgeCloseResult close;
        try
        {
            close = session.Bridge.Close();
        }
        catch (Exception ex)
        {
            // Teardown itself failed. The session is gone either way, so report
            // the single terminal record as faulted rather than falsely clean.
            // Nothing was submitted to Adobe, so this is never ambiguous.
            EvidenceItem evidence;
            lock (_gate)
            {
                if (_terminal is not null &&
                    string.Equals(
                        _terminal.SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                {
                    _terminal = _terminal with
                    {
                        State = DebugSessionState.Faulted,
                        Reason =
                            DebugSessionTerminalReason.CloseFailed
                    };
                }

                evidence =
                    BuildSessionStateEvidenceLocked(request);
            }

            session.Bridge.Dispose();
            return FailureNotStarted(
                request,
                "debug_session_close_failed",
                $"Debugger session teardown failed after the session was retired: {ex.Message}",
                ["inspect_debug_session"],
                evidence);
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                sessionId,
                closed = true,
                graceful = close.Graceful,
                exitObserved = close.ExitObserved
            });

        return Success(
            request,
            ProtocolValue.From(payload));
    }

    private ActiveDebugSession RequireActiveSession(
        string sessionId,
        string leaseId)
    {
        var active = _active;
        if (active is null)
        {
            throw new DebugSessionStateException(
                "debug_session_not_open",
                "No debugger session is open in this target worker. Open a new session explicitly; an existing session is never reconnected automatically.");
        }

        if (!string.Equals(
                active.SessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            throw new DebugSessionStateException(
                "debug_session_identity_mismatch",
                "Debugger session ID does not match the worker-owned session. The current session is unchanged.");
        }

        // FixedTimeEquals requires equal-length inputs and throws otherwise, so
        // a different-length lease must be classified before the comparison
        // rather than escaping as an input error.
        var owned = Encoding.UTF8.GetBytes(active.LeaseId);
        var presented = Encoding.UTF8.GetBytes(leaseId);
        if (owned.Length != presented.Length ||
            !CryptographicOperations.FixedTimeEquals(
                owned,
                presented))
        {
            throw new DebugSessionStateException(
                "debug_session_lease_mismatch",
                "Debugger session belongs to a different target lease. The current session is unchanged.");
        }

        return active;
    }

    private void OnIdleTimer(object? _)
    {
        ExpireIdleIfDue();
    }

    /// <summary>
    /// The single idle-expiry decision point, shared by the timer callback and
    /// by deterministic tests. It reads the injected clock, so no test needs a
    /// real idle wait and production timing semantics are unchanged.
    /// </summary>
    internal void ExpireIdleIfDue()
    {
        ActiveDebugSession? expired = null;

        lock (_gate)
        {
            var active = _active;
            if (active is null)
                return;

            var elapsed = _utcNow() - active.LastUsedAt;
            var remaining =
                TimeSpan.FromMilliseconds(
                    active.IdleTimeoutMs) -
                elapsed;

            if (remaining > TimeSpan.Zero)
            {
                _idleTimer.Change(
                    remaining,
                    Timeout.InfiniteTimeSpan);
                return;
            }

            expired = RetireLocked(
                active,
                DebugSessionState.Closed,
                DebugSessionTerminalReason.IdleExpired);
        }

        expired.Bridge.Dispose();
    }

    private void ScheduleIdleLocked(
        ActiveDebugSession session)
    {
        _idleTimer.Change(
            session.IdleTimeoutMs,
            Timeout.Infinite);
    }

    private void EnsureTargetGeneration()
    {
        _verifyTargetGeneration(_identity);
    }

    /// <summary>
    /// Strong PID + process-start generation proof for the bound host. A
    /// recycled PID is not the same Illustrator process, so both must match.
    /// </summary>
    internal static void VerifyTargetGeneration(
        HostTargetIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(
                identity.ProcessId);
            process.Refresh();

            var startedAt =
                new DateTimeOffset(process.StartTime);
            if (startedAt.ToUniversalTime().Ticks !=
                identity.ProcessStartedAt
                    .ToUniversalTime()
                    .Ticks)
            {
                throw new HostAdapterException(
                    "target_generation_changed",
                    "Illustrator process generation changed while a debugger request was being prepared.",
                    retryable: false,
                    ExecutionState.NotStarted);
            }
        }
        catch (ArgumentException ex)
        {
            throw new HostAdapterException(
                "target_unavailable",
                "Illustrator process no longer exists.",
                retryable: false,
                ExecutionState.NotStarted,
                innerException: ex);
        }
    }

    internal static string BuildCommandXml(
        JsonElement input,
        string command,
        string engine,
        int timeoutMs)
    {
        return command switch
        {
            "eval" => BuildEvalXml(
                input,
                engine,
                timeoutMs),
            "set-breakpoints" =>
                BuildBreakpointsXml(
                    input,
                    engine),
            "get-breakpoints" =>
                BuildSimpleXml(
                    input,
                    command,
                    engine,
                    []),
            "get-break" =>
                BuildInspectionXml(
                    input,
                    command,
                    engine,
                    allowFrame: false,
                    allowObject: false),
            "get-frame" =>
                BuildInspectionXml(
                    input,
                    command,
                    engine,
                    allowFrame: true,
                    allowObject: false),
            "set-frame" =>
                BuildSetFrameXml(
                    input,
                    engine),
            "get-properties" =>
                BuildInspectionXml(
                    input,
                    command,
                    engine,
                    allowFrame: false,
                    allowObject: true),
            "continue" or
            "break" or
            "halt" or
            "stepover" or
            "stepinto" or
            "stepout" =>
                BuildControlXml(
                    input,
                    command,
                    engine),
            _ => throw new ArgumentException(
                $"Unsupported debugger command '{command}'.")
        };
    }

    private static string BuildEvalXml(
        JsonElement input,
        string engine,
        int timeoutMs)
    {
        EnsureOnlyFields(
            input,
            "sessionId",
            "command",
            "timeoutMs",
            "source",
            "debugLevel",
            "file",
            "reset",
            "breakpoints");

        var source = ReadRequiredString(
            input,
            "source",
            MaxSourceUtf8Bytes,
            limitByUtf8Bytes: true);
        var debugLevel = ReadOptionalInt(
                input,
                "debugLevel",
                0,
                2)
            ?? 1;
        var file = ReadOptionalString(
            input,
            "file",
            4096);
        var reset = ReadOptionalBool(
            input,
            "reset");
        var breakpoints = ReadBreakpoints(
            input);

        return WriteXml(writer =>
        {
            writer.WriteStartElement("eval");
            WriteAttribute(writer, "engine", engine);
            WriteAttribute(
                writer,
                "debug",
                debugLevel);
            WriteAttribute(
                writer,
                "timeout",
                timeoutMs);
            if (file is not null)
                WriteAttribute(
                    writer,
                    "file",
                    file);
            if (reset.HasValue)
                WriteAttribute(
                    writer,
                    "reset",
                    reset.Value);

            writer.WriteStartElement("source");
            WriteCData(writer, source);
            writer.WriteEndElement();

            WriteBreakpoints(
                writer,
                breakpoints);
            writer.WriteEndElement();
        });
    }

    private static string BuildBreakpointsXml(
        JsonElement input,
        string engine)
    {
        EnsureOnlyFields(
            input,
            "sessionId",
            "command",
            "timeoutMs",
            "breakpoints",
            "flags");

        var breakpoints = ReadBreakpoints(
            input,
            required: true);
        var flags = ReadOptionalInt(
                input,
                "flags",
                0,
                int.MaxValue)
            ?? 1024;

        return WriteXml(writer =>
        {
            writer.WriteStartElement("breakpoints");
            WriteAttribute(writer, "engine", engine);
            WriteAttribute(writer, "flags", flags);
            WriteBreakpoints(
                writer,
                breakpoints);
            writer.WriteEndElement();
        });
    }

    private static string BuildInspectionXml(
        JsonElement input,
        string command,
        string engine,
        bool allowFrame,
        bool allowObject)
    {
        var allowed = new List<string>
        {
            "sessionId",
            "command",
            "timeoutMs",
            "exclude",
            "all",
            "max"
        };
        if (allowFrame)
            allowed.Add("frame");
        if (allowObject)
            allowed.Add("object");

        EnsureOnlyFields(
            input,
            [.. allowed]);

        var exclude = ReadStringArray(
            input,
            "exclude",
            maxItems: 128,
            maxStringLength: 256);
        var all = ReadOptionalBool(
                input,
                "all")
            ?? true;
        var max = ReadOptionalInt(
            input,
            "max",
            0,
            1_000_000);
        var frame = allowFrame
            ? ReadOptionalInt(
                    input,
                    "frame",
                    0,
                    1_000_000)
                ?? 0
            : (int?)null;
        var objectName = allowObject
            ? ReadOptionalString(
                    input,
                    "object",
                    4096,
                    allowEmpty: true)
                ?? string.Empty
            : null;

        return WriteXml(writer =>
        {
            writer.WriteStartElement(command);
            WriteAttribute(writer, "engine", engine);
            if (exclude.Count > 0)
                WriteAttribute(
                    writer,
                    "exclude",
                    string.Join(",", exclude));
            WriteAttribute(writer, "all", all);
            if (max.HasValue)
                WriteAttribute(
                    writer,
                    "max",
                    max.Value);
            if (frame.HasValue)
                WriteAttribute(
                    writer,
                    "frame",
                    frame.Value);
            if (objectName is not null)
                WriteAttribute(
                    writer,
                    "object",
                    objectName);
            writer.WriteEndElement();
        });
    }

    private static string BuildSetFrameXml(
        JsonElement input,
        string engine)
    {
        EnsureOnlyFields(
            input,
            "sessionId",
            "command",
            "timeoutMs",
            "frame");

        var frame = ReadOptionalInt(
                input,
                "frame",
                0,
                1_000_000)
            ?? 0;

        return WriteXml(writer =>
        {
            writer.WriteStartElement("set-frame");
            WriteAttribute(writer, "engine", engine);
            WriteAttribute(writer, "frame", frame);
            writer.WriteEndElement();
        });
    }

    private static string BuildControlXml(
        JsonElement input,
        string command,
        string engine)
    {
        EnsureOnlyFields(
            input,
            "sessionId",
            "command",
            "timeoutMs",
            "shutdown",
            "ignoreErrors",
            "breakpoints");

        var shutdown = ReadOptionalBool(
            input,
            "shutdown");
        var ignoreErrors = ReadOptionalBool(
            input,
            "ignoreErrors");
        var breakpoints = ReadBreakpoints(
            input);

        return WriteXml(writer =>
        {
            writer.WriteStartElement(command);
            WriteAttribute(writer, "engine", engine);
            if (shutdown.HasValue)
                WriteAttribute(
                    writer,
                    "shutdown",
                    shutdown.Value);
            if (ignoreErrors.HasValue)
                WriteAttribute(
                    writer,
                    "ignore-errors",
                    ignoreErrors.Value);
            WriteBreakpoints(
                writer,
                breakpoints);
            writer.WriteEndElement();
        });
    }

    private static string BuildSimpleXml(
        JsonElement input,
        string command,
        string engine,
        IReadOnlyList<string> additionalFields)
    {
        EnsureOnlyFields(
            input,
            [
                "sessionId",
                "command",
                "timeoutMs",
                .. additionalFields
            ]);

        return WriteXml(writer =>
        {
            writer.WriteStartElement(command);
            WriteAttribute(writer, "engine", engine);
            writer.WriteEndElement();
        });
    }

    private static IReadOnlyList<DebugBreakpoint>
        ReadBreakpoints(
            JsonElement input,
            bool required = false)
    {
        if (!input.TryGetProperty(
                "breakpoints",
                out var value))
        {
            if (required)
                throw new ArgumentException(
                    "breakpoints is required.");
            return [];
        }

        if (value.ValueKind !=
            JsonValueKind.Array)
        {
            throw new ArgumentException(
                "breakpoints must be an array.");
        }

        var output =
            new List<DebugBreakpoint>();
        foreach (var element in
                 value.EnumerateArray())
        {
            if (output.Count >= MaxBreakpoints)
                throw new ArgumentException(
                    $"breakpoints may contain at most {MaxBreakpoints} entries.");
            EnsureOnlyFields(
                element,
                "file",
                "line",
                "enabled",
                "condition",
                "hits",
                "count");

            output.Add(
                new DebugBreakpoint(
                    ReadRequiredString(
                        element,
                        "file",
                        4096),
                    ReadRequiredInt(
                        element,
                        "line",
                        0,
                        10_000_000),
                    ReadOptionalBool(
                            element,
                            "enabled")
                        ?? true,
                    ReadOptionalString(
                            element,
                            "condition",
                            8192,
                            allowEmpty: true)
                        ?? string.Empty,
                    ReadOptionalInt(
                        element,
                        "hits",
                        0,
                        int.MaxValue),
                    ReadOptionalInt(
                        element,
                        "count",
                        0,
                        int.MaxValue)));
        }

        return output;
    }

    private static void WriteBreakpoints(
        XmlWriter writer,
        IReadOnlyList<DebugBreakpoint>
            breakpoints)
    {
        foreach (var breakpoint in
                 breakpoints)
        {
            writer.WriteStartElement(
                "breakpoint");
            WriteAttribute(
                writer,
                "file",
                breakpoint.File);
            WriteAttribute(
                writer,
                "line",
                breakpoint.Line);
            WriteAttribute(
                writer,
                "enabled",
                breakpoint.Enabled);
            if (breakpoint.Hits.HasValue)
                WriteAttribute(
                    writer,
                    "hits",
                    breakpoint.Hits.Value);
            if (breakpoint.Count.HasValue)
                WriteAttribute(
                    writer,
                    "count",
                    breakpoint.Count.Value);
            if (breakpoint.Condition.Length > 0)
                writer.WriteString(
                    breakpoint.Condition);
            writer.WriteEndElement();
        }
    }

    private static string WriteXml(
        Action<XmlWriter> write)
    {
        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(
            builder,
            new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                ConformanceLevel =
                    ConformanceLevel.Fragment,
                NewLineHandling =
                    NewLineHandling.None
            });
        write(writer);
        writer.Flush();
        return builder.ToString();
    }

    private static void WriteCData(
        XmlWriter writer,
        string value)
    {
        var offset = 0;
        while (true)
        {
            var index = value.IndexOf(
                "]]>",
                offset,
                StringComparison.Ordinal);
            if (index < 0)
            {
                writer.WriteCData(
                    value[offset..]);
                return;
            }

            writer.WriteCData(
                value[offset..(index + 2)]);
            writer.WriteString(">");
            offset = index + 3;
        }
    }

    private static void WriteAttribute(
        XmlWriter writer,
        string name,
        object value)
    {
        var text = value switch
        {
            bool boolean =>
                boolean ? "true" : "false",
            _ => Convert.ToString(
                     value,
                     System.Globalization
                         .CultureInfo.InvariantCulture)
                 ?? string.Empty
        };
        writer.WriteAttributeString(
            name,
            text);
    }

    private static IReadOnlyList<string>
        ExtractEngines(JsonElement exchange)
    {
        if (!exchange.TryGetProperty(
                "events",
                out var events) ||
            events.ValueKind !=
            JsonValueKind.Array)
            return [];

        var engines = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var item in
                 events.EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "body",
                    out var bodyValue) ||
                bodyValue.ValueKind !=
                JsonValueKind.String)
                continue;

            var body = bodyValue.GetString();
            if (string.IsNullOrWhiteSpace(
                    body) ||
                !body.TrimStart().StartsWith(
                    "<",
                    StringComparison.Ordinal))
                continue;

            try
            {
                var document =
                    XDocument.Parse(body);
                foreach (var engine in
                         document.Descendants(
                             "engine"))
                {
                    var name =
                        (string?)engine
                            .Attribute("name");
                    if (!string.IsNullOrWhiteSpace(
                            name))
                        engines.Add(name);
                }
            }
            catch
            {
                // A non-engine event is not a connect-contract failure.
            }
        }

        return engines.ToArray();
    }

    private static string? InferExecutionState(
        string command,
        JsonElement exchange)
    {
        if (!string.Equals(
                command,
                "eval",
                StringComparison.Ordinal) &&
            command is not (
                "continue" or
                "stepover" or
                "stepinto" or
                "stepout"))
            return null;

        if (!exchange.TryGetProperty(
                "events",
                out var events) ||
            events.ValueKind !=
            JsonValueKind.Array)
            return "unknown";

        var tags = events
            .EnumerateArray()
            .Select(item =>
                item.TryGetProperty(
                    "bodyTag",
                    out var value) &&
                value.ValueKind ==
                JsonValueKind.String
                    ? value.GetString()
                    : null)
            .ToArray();

        if (tags.Contains(
                "break",
                StringComparer.Ordinal))
            return "break";
        if (tags.Contains(
                "evalresult",
                StringComparer.Ordinal))
            return "completed";
        return "unknown";
    }

    internal static JsonElement?
        NormalizeDebuggerResponse(
            string command,
            JsonElement exchange)
    {
        if (!exchange.TryGetProperty(
                "events",
                out var events) ||
            events.ValueKind !=
            JsonValueKind.Array)
            return null;

        JsonElement? fallback = null;
        JsonElement? breakEvent = null;
        JsonElement? evalResultEvent = null;

        foreach (var item in
                 events.EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "body",
                    out var body) ||
                body.ValueKind !=
                JsonValueKind.String ||
                string.IsNullOrWhiteSpace(
                    body.GetString()))
                continue;

            fallback = item.Clone();

            var tag =
                item.TryGetProperty(
                    "bodyTag",
                    out var tagValue) &&
                tagValue.ValueKind ==
                JsonValueKind.String
                    ? tagValue.GetString()
                    : null;

            if (string.Equals(
                    tag,
                    "break",
                    StringComparison.Ordinal))
                breakEvent = item.Clone();
            else if (string.Equals(
                         tag,
                         "evalresult",
                         StringComparison.Ordinal))
                evalResultEvent = item.Clone();
        }

        var prefersExecutionResult =
            string.Equals(
                command,
                "eval",
                StringComparison.Ordinal) ||
            command is
                "continue" or
                "stepover" or
                "stepinto" or
                "stepout";

        var selected =
            prefersExecutionResult
                ? evalResultEvent ??
                  breakEvent ??
                  fallback
                : fallback;

        if (selected is null)
            return null;

        var selectedValue =
            selected.Value;
        var bodyText =
            selectedValue.GetProperty(
                    "body")
                .GetString()!;

        try
        {
            var normalized =
                NormalizeDebuggerXml(bodyText);
            return JsonSerializer.SerializeToElement(
                new
                {
                    available = true,
                    eventReason =
                        ReadOptionalJsonInt(
                            selectedValue,
                            "reason"),
                    eventSerial =
                        ReadOptionalJsonInt(
                            selectedValue,
                            "serialNumber"),
                    resultSerial =
                        ReadOptionalJsonInt(
                            selectedValue,
                            "resultSerial"),
                    errorSerial =
                        ReadOptionalJsonInt(
                            selectedValue,
                            "errorSerial"),
                    normalized
                });
        }
        catch (Exception ex)
            when (ex is
                  XmlException or
                  InvalidDataException or
                  FormatException or
                  OverflowException)
        {
            // Normalization is an ergonomic view over the already-received
            // bounded raw transport. It must never turn a completed Adobe
            // exchange into an execution ambiguity.
            return JsonSerializer.SerializeToElement(
                new
                {
                    available = false,
                    parseError =
                        ex.Message,
                    bodyTag =
                        selectedValue
                            .TryGetProperty(
                                "bodyTag",
                                out var tagValue) &&
                        tagValue.ValueKind ==
                        JsonValueKind.String
                            ? tagValue.GetString()
                            : null
                });
        }
    }

    private static object NormalizeDebuggerXml(
        string body)
    {
        const long maxCharacters =
            4L * 1024L * 1024L;
        using var stringReader =
            new StringReader(body);
        using var reader = XmlReader.Create(
            stringReader,
            new XmlReaderSettings
            {
                DtdProcessing =
                    DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument =
                    maxCharacters,
                IgnoreComments = true
            });
        var document = XDocument.Load(
            reader,
            LoadOptions.None);
        var root = document.Root ??
                   throw new InvalidDataException(
                       "Debugger XML response has no root element.");

        var nodeCount = 0;
        var tree = NormalizeXmlElement(
            root,
            depth: 0,
            ref nodeCount);

        object? typedValue = null;
        string? valueType = null;
        object? error = null;

        if (string.Equals(
                root.Name.LocalName,
                "evalresult",
                StringComparison.Ordinal))
        {
            var valueElement =
                root.Element("value");
            if (valueElement is not null)
            {
                valueType =
                    (string?)valueElement
                        .Attribute("type");
                typedValue =
                    ParseDebuggerValue(
                        valueType,
                        valueElement.Value);
            }

            var errorElement =
                root.Element("error");
            if (errorElement is not null)
            {
                error = new
                {
                    message =
                        errorElement.Value,
                    id = ParseOptionalInt64(
                        (string?)errorElement
                            .Attribute("id")),
                    file =
                        (string?)errorElement
                            .Attribute("file"),
                    line = ParseOptionalInt64(
                        (string?)errorElement
                            .Attribute("line")),
                    col = ParseOptionalInt64(
                        (string?)errorElement
                            .Attribute("col"))
                };
            }
        }

        return new
        {
            tag = root.Name.LocalName,
            valueType,
            value = typedValue,
            error,
            tree
        };
    }

    private static object NormalizeXmlElement(
        XElement element,
        int depth,
        ref int nodeCount)
    {
        const int maxDepth = 64;
        const int maxNodes = 4096;
        const int maxDirectTextChars =
            1024 * 1024;

        if (depth > maxDepth)
            throw new InvalidDataException(
                $"Debugger XML exceeds maximum depth {maxDepth}.");
        if (++nodeCount > maxNodes)
            throw new InvalidDataException(
                $"Debugger XML exceeds maximum node count {maxNodes}.");

        var attributes = element
            .Attributes()
            .ToDictionary(
                static attribute =>
                    attribute.Name.LocalName,
                static attribute =>
                    attribute.Value,
                StringComparer.Ordinal);

        var directText = string.Concat(
            element
                .Nodes()
                .Where(static node =>
                    node is XText)
                .Select(static node =>
                    ((XText)node).Value));
        if (directText.Length >
            maxDirectTextChars)
        {
            throw new InvalidDataException(
                "Debugger XML direct text exceeds the normalized text limit.");
        }

        var childElements =
            element.Elements().ToArray();
        var children =
            new object[childElements.Length];
        for (var i = 0;
             i < childElements.Length;
             i++)
        {
            children[i] =
                NormalizeXmlElement(
                    childElements[i],
                    depth + 1,
                    ref nodeCount);
        }

        return new
        {
            tag = element.Name.LocalName,
            attributes,
            text = directText.Length == 0
                ? null
                : directText,
            children
        };
    }

    private static object?
        ParseDebuggerValue(
            string? valueType,
            string text)
    {
        switch (valueType)
        {
            case "undefined":
            case "null":
                return null;
            case "boolean":
                if (bool.TryParse(
                        text,
                        out var boolean))
                    return boolean;
                return text;
            case "number":
                if (long.TryParse(
                        text,
                        System.Globalization
                            .NumberStyles.Integer,
                        System.Globalization
                            .CultureInfo.InvariantCulture,
                        out var integer))
                    return integer;
                if (double.TryParse(
                        text,
                        System.Globalization
                            .NumberStyles.Float,
                        System.Globalization
                            .CultureInfo.InvariantCulture,
                        out var number) &&
                    double.IsFinite(number))
                    return number;
                return text;
            default:
                return text;
        }
    }

    private static long? ParseOptionalInt64(
        string? value) =>
        long.TryParse(
            value,
            System.Globalization
                .NumberStyles.Integer,
            System.Globalization
                .CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private static int? ReadOptionalJsonInt(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(
                propertyName,
                out var value) &&
            value.ValueKind ==
            JsonValueKind.Number &&
            value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static string RequireLeaseId(
        OperationRequest request)
    {
        var leaseId = request.Policy?.LeaseId;
        if (string.IsNullOrWhiteSpace(
                leaseId))
        {
            throw new ArgumentException(
                "Debugger operations require policy.leaseId.");
        }

        return leaseId;
    }

    private static void EnsureInputObject(
        JsonElement input)
    {
        if (input.ValueKind !=
            JsonValueKind.Object)
            throw new ArgumentException(
                "Debugger operation input must be a JSON object.");
    }

    private static void EnsureOnlyFields(
        JsonElement input,
        params string[] allowed)
    {
        EnsureInputObject(input);
        var set = new HashSet<string>(
            allowed,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var property in
                 input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new ArgumentException(
                    $"Duplicate debugger field '{property.Name}'.");
            if (!set.Contains(
                    property.Name))
                throw new ArgumentException(
                    $"Unknown debugger field '{property.Name}'.");
        }
    }

    private static string ReadRequiredString(
        JsonElement input,
        string name,
        int maxLength,
        bool limitByUtf8Bytes = false)
    {
        var value = ReadOptionalString(
            input,
            name,
            maxLength,
            allowEmpty: false,
            limitByUtf8Bytes);
        return value ??
               throw new ArgumentException(
                   $"{name} is required.");
    }

    private static string? ReadOptionalString(
        JsonElement input,
        string name,
        int maxLength,
        bool allowEmpty = false,
        bool limitByUtf8Bytes = false)
    {
        if (!input.TryGetProperty(
                name,
                out var value))
            return null;
        if (value.ValueKind !=
            JsonValueKind.String)
            throw new ArgumentException(
                $"{name} must be a string.");

        var text = value.GetString() ??
                   string.Empty;
        if (!allowEmpty &&
            string.IsNullOrWhiteSpace(text))
            throw new ArgumentException(
                $"{name} must be non-empty.");

        var length = limitByUtf8Bytes
            ? Encoding.UTF8.GetByteCount(text)
            : text.Length;
        if (length > maxLength)
            throw new ArgumentException(
                $"{name} exceeds the supported limit of {maxLength} {(limitByUtf8Bytes ? "UTF-8 bytes" : "characters")}.");

        return text;
    }

    private static int ReadRequiredInt(
        JsonElement input,
        string name,
        int min,
        int max) =>
        ReadOptionalInt(
            input,
            name,
            min,
            max) ??
        throw new ArgumentException(
            $"{name} is required.");

    private static int? ReadOptionalInt(
        JsonElement input,
        string name,
        int min,
        int max)
    {
        if (!input.TryGetProperty(
                name,
                out var value))
            return null;
        if (value.ValueKind !=
                JsonValueKind.Number ||
            !value.TryGetInt32(
                out var result) ||
            result < min ||
            result > max)
        {
            throw new ArgumentException(
                $"{name} must be an integer in [{min}, {max}].");
        }

        return result;
    }

    private static bool? ReadOptionalBool(
        JsonElement input,
        string name)
    {
        if (!input.TryGetProperty(
                name,
                out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException(
                $"{name} must be a boolean.")
        };
    }

    private static IReadOnlyList<string>
        ReadStringArray(
            JsonElement input,
            string name,
            int maxItems,
            int maxStringLength)
    {
        if (!input.TryGetProperty(
                name,
                out var value))
            return [];
        if (value.ValueKind !=
            JsonValueKind.Array)
            throw new ArgumentException(
                $"{name} must be an array.");

        var output = new List<string>();
        foreach (var item in
                 value.EnumerateArray())
        {
            if (output.Count >= maxItems)
                throw new ArgumentException(
                    $"{name} may contain at most {maxItems} entries.");
            if (item.ValueKind !=
                JsonValueKind.String)
                throw new ArgumentException(
                    $"{name} entries must be strings.");
            var text = item.GetString() ??
                       string.Empty;
            if (text.Length >
                maxStringLength)
                throw new ArgumentException(
                    $"{name} entry exceeds {maxStringLength} characters.");
            output.Add(text);
        }

        return output;
    }

    private static string ComputeSha256Hex(
        string path)
    {
        using var stream = File.OpenRead(
            path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The one mapping from a bridge fault to a reported host failure. Kept in
    /// a single place so that attaching diagnostics can never drift from the
    /// already-established kind, retryability, or execution state.
    /// </summary>
    private static HostAdapterException MapBridgeFailure(
        DebuggerBridgeException ex,
        EvidenceItem? evidence) =>
        new(
            ex.Fault == DebuggerBridgeFault.TransportLostAfterSubmit
                ? "debugger_transport_lost_after_submit"
                : ex.Fault == DebuggerBridgeFault.ChildUnavailable
                    ? "debugger_child_unavailable"
                    : "debugger_request_rejected",
            ex.Message,
            retryable: false,
            ex.Fault == DebuggerBridgeFault.TransportLostAfterSubmit
                ? ExecutionState.Ambiguous
                : ExecutionState.NotStarted,
            innerException: ex,
            evidence: evidence is null ? null : [evidence]);

    /// <summary>
    /// Facts for a fault raised anywhere, described only from worker-owned
    /// published state. A live session wins over the retained terminal record
    /// because it is the stronger, still-authoritative fact.
    /// </summary>
    private DebuggerFaultFacts SnapshotFaultFactsLocked(
        OperationRequest request,
        string? command,
        string? engine,
        DebuggerBridgeException? bridgeFailure)
    {
        var active = _active;
        var terminal = _terminal;

        var childProcessId =
            active?.Bridge.ProcessId ??
            terminal?.ChildProcessId;
        var childProcessStartedAt =
            active?.Bridge.ProcessStartedAt ??
            terminal?.ChildProcessStartedAt;
        bool? childAlive =
            childProcessId is { } childPid &&
            childProcessStartedAt is { } childStarted
                ? IsGenerationAlive(
                    childPid,
                    childStarted)
                : null;

        return new DebuggerFaultFacts(
            request.Operation,
            active?.SessionId ?? terminal?.SessionId,
            command,
            engine ?? active?.Engine ?? terminal?.Engine,
            active?.Bridge.AppSpecifier ?? terminal?.AppSpec,
            _identity.ProcessId,
            _identity.ProcessStartedAt,
            childProcessId,
            childProcessStartedAt,
            childAlive,
            active?.AddonSha256 ?? terminal?.AddonSha256,
            active?.BridgeSha256 ?? terminal?.BridgeSha256,
            active?.Bridge.NodeVersion,
            bridgeFailure?.Fault,
            bridgeFailure?.Diagnostics,
            active is not null
                ? "open"
                : terminal is null
                    ? "none"
                    : terminal.State == DebugSessionState.Faulted
                        ? "faulted"
                        : "closed",
            terminal is null
                ? null
                : TerminalReasonText(terminal.Reason),
            null,
            null);
    }

    /// <summary>
    /// Facts for a fault raised during <see cref="Open"/>, where no session
    /// has been published yet and the child plus its pinned digests are known
    /// only locally.
    /// </summary>
    private DebuggerFaultFacts OpenFaultFactsLocked(
        OperationRequest request,
        string engine,
        DebuggerDependencyStatus dependencies,
        DebuggerBridgeAssetInfo? bridgeAsset,
        string? addonSha256,
        IDebuggerBridge? bridge,
        DebuggerBridgeException? bridgeFailure = null)
    {
        var childProcessId = bridge?.ProcessId;
        var childProcessStartedAt = bridge?.ProcessStartedAt;
        bool? childAlive =
            childProcessId is { } childPid &&
            childProcessStartedAt is { } childStarted
                ? IsGenerationAlive(
                    childPid,
                    childStarted)
                : null;

        return new DebuggerFaultFacts(
            request.Operation,
            null,
            null,
            engine,
            bridge?.AppSpecifier,
            _identity.ProcessId,
            _identity.ProcessStartedAt,
            childProcessId,
            childProcessStartedAt,
            childAlive,
            addonSha256,
            bridgeAsset?.Sha256,
            bridge?.NodeVersion,
            bridgeFailure?.Fault,
            bridgeFailure?.Diagnostics,
            "opening",
            null,
            dependencies.NodePath is not null,
            dependencies.AddonPath is not null);
    }

    /// <summary>
    /// Evidence for a refused open: which dependency was missing, as
    /// booleans only, so a missing Node or addon is diagnosable without
    /// publishing where either one was looked for.
    /// </summary>
    private EvidenceItem BuildDependencyEvidence(
        OperationRequest request,
        string engine,
        DebuggerDependencyStatus dependencies) =>
        DebuggerFaultEvidence.Build(
            OpenFaultFactsLocked(
                request,
                engine,
                dependencies,
                bridgeAsset: null,
                addonSha256: null,
                bridge: null));

    private EvidenceItem BuildSessionStateEvidence(
        OperationRequest request)
    {
        lock (_gate)
        {
            return BuildSessionStateEvidenceLocked(request);
        }
    }

    /// <summary>
    /// Evidence for a refusal that never reached the bridge: published session
    /// state only, never the presented session identifier, the lease, or any
    /// other caller-supplied field.
    /// </summary>
    private EvidenceItem BuildSessionStateEvidenceLocked(
        OperationRequest request) =>
        DebuggerFaultEvidence.Build(
            SnapshotFaultFactsLocked(
                request,
                command: null,
                engine: null,
                bridgeFailure: null));

    private EvidenceItem BuildBridgeFaultEvidence(
        OperationRequest request,
        DebuggerBridgeException ex)
    {
        lock (_gate)
        {
            return DebuggerFaultEvidence.Build(
                SnapshotFaultFactsLocked(
                    request,
                    command: null,
                    engine: null,
                    bridgeFailure: ex));
        }
    }

    private static OperationResult Success(
        OperationRequest request,
        ProtocolValue value,
        EvidenceItem? evidence = null) =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = value,
            Evidence = evidence is null
                ? null
                : [evidence]
        };

    private static OperationResult InvalidRequest(
        OperationRequest request,
        string kind,
        string message) =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status =
                OperationStatus.InvalidRequest,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution =
                    ExecutionState.NotStarted,
                SuggestedActions =
                    ["inspect_debug_request"]
            }
        };

    private static OperationResult Unsupported(
        OperationRequest request,
        string kind,
        string message,
        EvidenceItem? evidence = null) =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status =
                OperationStatus.UnsupportedOperation,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution =
                    ExecutionState.NotStarted,
                SuggestedActions =
                    ["core.target.capabilities"]
            },
            Evidence = evidence is null ? null : [evidence]
        };

    /// <summary>
    /// An authority or session-state refusal. It is a failure rather than an
    /// invalid request because the caller's input was well formed, and it is
    /// always not-started because nothing was submitted and nothing changed.
    /// </summary>
    private static OperationResult FailureNotStarted(
        OperationRequest request,
        string kind,
        string message,
        IReadOnlyList<string> suggestedActions,
        EvidenceItem? evidence = null) =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.Failed,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution =
                    ExecutionState.NotStarted,
                SuggestedActions =
                    suggestedActions
            },
            Evidence = evidence is null ? null : [evidence]
        };

    private static OperationResult FailureCompleted(
        OperationRequest request,
        string kind,
        string message,
        EvidenceItem? evidence = null) =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.Failed,
            TargetState =
                TargetState.KnownChanged,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution =
                    ExecutionState.Completed,
                SuggestedActions =
                    ["inspect_debug_session"]
            },
            Evidence = evidence is null ? null : [evidence]
        };

    private sealed record DebugBreakpoint(
        string File,
        int Line,
        bool Enabled,
        string Condition,
        int? Hits,
        int? Count);

    /// <summary>
    /// A fully validated debugger command. Producing one has no side effects:
    /// no session lookup, no bridge call, and no timer change.
    /// </summary>
    internal sealed record NormalizedDebugCommand(
        string SessionId,
        string Command,
        string Engine,
        int TimeoutMs,
        string Xml);

    private sealed class ActiveDebugSession(
        string sessionId,
        string leaseId,
        string engine,
        int idleTimeoutMs,
        DateTimeOffset openedAt,
        DateTimeOffset lastUsedAt,
        IDebuggerBridge bridge,
        string addonSha256,
        string bridgeSha256,
        DebuggerDependencyStatus dependencies,
        DebuggerBridgeAssetInfo bridgeAsset)
    {
        public string SessionId { get; } =
            sessionId;
        public string LeaseId { get; } =
            leaseId;
        public string Engine { get; } =
            engine;
        public int IdleTimeoutMs { get; } =
            idleTimeoutMs;
        public DateTimeOffset OpenedAt { get; } =
            openedAt;
        public DateTimeOffset LastUsedAt
        {
            get;
            set;
        } = lastUsedAt;
        public DateTimeOffset? LastCommandAt
        {
            get;
            set;
        }
        public int CommandCount
        {
            get;
            set;
        }
        public IDebuggerBridge Bridge { get; } =
            bridge;
        public string AddonSha256 { get; } =
            addonSha256;
        public string BridgeSha256 { get; } =
            bridgeSha256;
        public DebuggerDependencyStatus
            Dependencies { get; } =
            dependencies;
        public DebuggerBridgeAssetInfo
            BridgeAsset { get; } =
            bridgeAsset;
    }
}

/// <summary>
/// Everything needed to start one worker-owned bridge child. Bound to the real
/// <see cref="DebuggerBridge"/> in production; tests inject a fake so the command
/// contract and session-survival rules are provable without an Adobe host.
/// </summary>
internal sealed record DebuggerBridgeLaunch(
    string NodePath,
    string AddonPath,
    string AddonSha256,
    string BridgePath,
    string? AppSpecifier);

/// <summary>
/// The single transport seam between the session manager and the ESD bridge
/// child. There is exactly one production implementation; no second debugger
/// path exists.
/// </summary>
internal interface IDebuggerBridge : IDisposable
{
    string AppSpecifier { get; }
    int ProcessId { get; }
    DateTimeOffset ProcessStartedAt { get; }
    string? NodeVersion { get; }

    JsonElement Exchange(string body, int timeoutMs);

    DebuggerBridgeCloseResult Close();
}

internal sealed class DebuggerBridge : IDebuggerBridge
{
    private readonly Process _process;
    private readonly object _ioGate = new();
    private readonly StringBuilder _stderr =
        new();
    private int _closed;
    private int _stderrTruncated;

    public DebuggerBridge(
        string nodePath,
        string addonPath,
        string addonSha256,
        string bridgePath,
        string? appSpecifier)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding =
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding =
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding =
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false)
        };
        startInfo.ArgumentList.Add(
            bridgePath);
        if (!string.IsNullOrWhiteSpace(
                appSpecifier))
            startInfo.ArgumentList.Add(
                appSpecifier);
        ApplyAddonProvenance(
            startInfo,
            addonPath,
            addonSha256);

        _process = Process.Start(startInfo) ??
                   throw new DebuggerBridgeException(
                       "Failed to start the worker-owned ExtendScript debugger bridge.",
                       DebuggerBridgeFault.ChildUnavailable,
                       diagnostics: Diagnostics(
                           DebuggerBridgeFaultPhase.BridgeStartup,
                           response: null));
        _process.ErrorDataReceived +=
            OnErrorDataReceived;
        _process.BeginErrorReadLine();

        try
        {
            ProcessId = _process.Id;
            ProcessStartedAt =
                new DateTimeOffset(
                    _process.StartTime);
            NodeVersion =
                FileVersionInfo
                    .GetVersionInfo(
                        nodePath)
                    .ProductVersion;

            var ready = ReadResponse(
                TimeSpan.FromSeconds(10),
                submitted: false);
            if (!ReadOk(ready))
            {
                throw new DebuggerBridgeException(
                    ReadError(
                        ready,
                        "Debugger bridge initialization failed."),
                    DebuggerBridgeFault.ChildUnavailable,
                    diagnostics: Diagnostics(
                        DebuggerBridgeFaultPhase.BridgeStartup,
                        ready));
            }

            AppSpecifier =
                ready.TryGetProperty(
                    "appSpec",
                    out var spec) &&
                spec.ValueKind ==
                JsonValueKind.String
                    ? spec.GetString() ??
                      string.Empty
                    : string.Empty;

            if (string.IsNullOrWhiteSpace(
                    AppSpecifier))
            {
                throw new DebuggerBridgeException(
                    "Debugger bridge did not report an Illustrator application specifier.",
                    DebuggerBridgeFault.ChildUnavailable,
                    diagnostics: Diagnostics(
                        DebuggerBridgeFaultPhase.BridgeStartup,
                        response: null));
            }
        }
        catch
        {
            Close();
            throw;
        }
    }

    public string AppSpecifier { get; }
    public int ProcessId { get; }
    public DateTimeOffset ProcessStartedAt
    {
        get;
    }
    public string? NodeVersion { get; }

    /// <summary>
    /// Hands the child the exact addon path plus the digest C# already pinned
    /// for it. The bridge never searches for the addon itself; it must load
    /// these bytes or fail closed.
    /// </summary>
    internal static void ApplyAddonProvenance(
        ProcessStartInfo startInfo,
        string addonPath,
        string addonSha256)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(addonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(addonSha256);

        startInfo.Environment[
            "COMTOOL_ESD_ADDON_PATH"] =
            addonPath;
        startInfo.Environment[
            "COMTOOL_ESD_ADDON_SHA256"] =
            addonSha256;
    }

    public JsonElement Exchange(
        string body,
        int timeoutMs)
    {
        lock (_ioGate)
        {
            if (Volatile.Read(
                    ref _closed) != 0 ||
                _process.HasExited)
            {
                throw new DebuggerBridgeException(
                    "Debugger bridge is not running.",
                    DebuggerBridgeFault.ChildUnavailable,
                    diagnostics: Diagnostics(
                        DebuggerBridgeFaultPhase.ExchangePreSubmit,
                        response: null));
            }

            var json = JsonSerializer.Serialize(
                new
                {
                    body,
                    timeoutMs,
                    pumpMs = timeoutMs
                });

            // 'submitted' is set only after the request line was written and
            // flushed to the child. It is never inferred from elapsed time or
            // from the shape of a failure, so a pre-write fault can never be
            // reported as a possibly-submitted one.
            var submitted = false;
            try
            {
                _process.StandardInput
                    .WriteLine(json);
                _process.StandardInput.Flush();
                submitted = true;

                var response = ReadResponse(
                    TimeSpan.FromMilliseconds(
                        timeoutMs + 5_000),
                    submitted: true);

                if (!ReadOk(response))
                {
                    // The child reports where it stopped. 'before_send' is
                    // positive proof that no ESD message was sent, so the
                    // session survives; anything else is treated as possibly
                    // submitted.
                    var phase =
                        response.TryGetProperty(
                            "phase",
                            out var phaseValue) &&
                        phaseValue.ValueKind ==
                        JsonValueKind.String
                            ? phaseValue
                                .GetString()
                            : null;
                    var rejectedBeforeSend =
                        string.Equals(
                            phase,
                            "before_send",
                            StringComparison.Ordinal);

                    throw new DebuggerBridgeException(
                        rejectedBeforeSend
                            ? $"Debugger bridge rejected the request before sending it: {ReadError(response, "no reason reported")}"
                            : ReadError(
                                response,
                                "Debugger bridge rejected the command."),
                        rejectedBeforeSend
                            ? DebuggerBridgeFault.RejectedBeforeSend
                            : DebuggerBridgeFault.TransportLostAfterSubmit,
                        diagnostics: Diagnostics(
                            submitted
                                ? DebuggerBridgeFaultPhase
                                    .ExchangePostSubmit
                                : DebuggerBridgeFaultPhase
                                    .ExchangePreSubmit,
                            response));
                }

                return response;
            }
            catch (DebuggerBridgeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A write or flush that never completed is provably
                // pre-submission even though the exception is generic. Once
                // the line was flushed, the request may have reached Adobe and
                // the outcome is ambiguous by policy.
                throw new DebuggerBridgeException(
                    $"Debugger bridge I/O failed: {ex.Message}",
                    submitted
                        ? DebuggerBridgeFault.TransportLostAfterSubmit
                        : DebuggerBridgeFault.ChildUnavailable,
                    ex,
                    diagnostics: Diagnostics(
                        submitted
                            ? DebuggerBridgeFaultPhase
                                .ExchangePostSubmit
                            : DebuggerBridgeFaultPhase
                                .ExchangePreSubmit,
                        response: null));
            }
        }
    }

    public DebuggerBridgeCloseResult Close()
    {
        if (Interlocked.Exchange(
                ref _closed,
                1) != 0)
        {
            return new DebuggerBridgeCloseResult(
                Graceful: true,
                ExitObserved: _process.HasExited);
        }

        var graceful = false;
        var exitObserved = false;

        lock (_ioGate)
        {
            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.StandardInput
                            .Close();
                    }
                    catch
                    {
                    }

                    graceful =
                        _process.WaitForExit(
                            5_000);
                }
                else
                {
                    graceful = true;
                }

                if (!graceful &&
                    !_process.HasExited)
                {
                    _process.Kill(
                        entireProcessTree: true);
                    _process.WaitForExit(
                        2_000);
                }

                exitObserved =
                    _process.HasExited;
            }
            catch
            {
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(
                            entireProcessTree: true);
                }
                catch
                {
                }

                try
                {
                    exitObserved =
                        _process.HasExited;
                }
                catch
                {
                }
            }
        }

        return new DebuggerBridgeCloseResult(
            graceful,
            exitObserved);
    }

    public void Dispose()
    {
        Close();
        _process.ErrorDataReceived -=
            OnErrorDataReceived;
        _process.Dispose();
    }

    private JsonElement ReadResponse(
        TimeSpan timeout,
        bool submitted)
    {
        // Whether the request line had already been flushed is the only thing
        // that separates a startup failure from a lost submission, so the
        // phase is fixed once, here, from that fact alone.
        var phase = submitted
            ? DebuggerBridgeFaultPhase.ResponseRead
            : DebuggerBridgeFaultPhase.BridgeStartup;

        string? line;
        try
        {
            line = _process
                .StandardOutput
                .ReadLineAsync()
                .WaitAsync(timeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException ex)
        {
            // A wait that expired carries no bridge evidence at all. The fault
            // is decided solely by whether the request line was already
            // flushed, never by how long the wait took.
            throw new DebuggerBridgeException(
                $"Timed out waiting for debugger bridge response after {timeout.TotalMilliseconds:0} ms. {ReadStderr()}",
                submitted
                    ? DebuggerBridgeFault.TransportLostAfterSubmit
                    : DebuggerBridgeFault.ChildUnavailable,
                ex,
                diagnostics: Diagnostics(
                    phase,
                    response: null));
        }

        if (line is null)
        {
            throw new DebuggerBridgeException(
                $"Debugger bridge closed stdout unexpectedly. {ReadStderr()}",
                submitted
                    ? DebuggerBridgeFault.TransportLostAfterSubmit
                    : DebuggerBridgeFault.ChildUnavailable,
                diagnostics: Diagnostics(
                    phase,
                    response: null));
        }

        try
        {
            using var document =
                JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // The reply line is unparseable, so its phase cannot be trusted.
            // Fall back to the write-completed fact alone.
            throw new DebuggerBridgeException(
                "Debugger bridge returned malformed JSON.",
                submitted
                    ? DebuggerBridgeFault.TransportLostAfterSubmit
                    : DebuggerBridgeFault.ChildUnavailable,
                ex,
                diagnostics: Diagnostics(
                    phase,
                    response: null));
        }
    }

    private static bool ReadOk(
        JsonElement response) =>
        response.TryGetProperty(
            "ok",
            out var ok) &&
        ok.ValueKind ==
        JsonValueKind.True;

    private static string ReadError(
        JsonElement response,
        string fallback) =>
        response.TryGetProperty(
            "error",
            out var error) &&
        error.ValueKind ==
        JsonValueKind.String
            ? error.GetString() ??
              fallback
            : fallback;

    private void OnErrorDataReceived(
        object sender,
        DataReceivedEventArgs args)
    {
        if (string.IsNullOrEmpty(
                args.Data))
            return;

        lock (_stderr)
        {
            const int maxChars = 16_384;
            if (_stderr.Length >=
                maxChars)
            {
                Volatile.Write(
                    ref _stderrTruncated,
                    1);
                return;
            }

            var remaining =
                maxChars -
                _stderr.Length;
            if (args.Data.Length > remaining)
                Volatile.Write(
                    ref _stderrTruncated,
                    1);

            _stderr.AppendLine(
                args.Data.Length <= remaining
                    ? args.Data
                    : args.Data[..remaining]);
        }
    }

    private string ReadStderr()
    {
        lock (_stderr)
        {
            return _stderr.Length == 0
                ? string.Empty
                : $"stderr: {_stderr}";
        }
    }

    /// <summary>
    /// Captures the raw stderr buffer for classification. The raw text is only
    /// ever handed to the closed-vocabulary classifier and is never forwarded
    /// into a protocol payload, because Node can echo a rejected request line
    /// or a filesystem path into stderr.
    /// </summary>
    private DebuggerBridgeDiagnostics Diagnostics(
        DebuggerBridgeFaultPhase phase,
        JsonElement? response)
    {
        string captured;
        bool truncated;
        lock (_stderr)
        {
            captured = _stderr.ToString();
            truncated =
                Volatile.Read(ref _stderrTruncated) != 0;
        }

        return DebuggerBridgeDiagnostics.From(
            phase,
            response,
            captured,
            truncated);
    }
}

/// <summary>
/// A failed debugger bridge exchange. <see cref="Fault"/> is the only thing the
/// caller needs to decide reported execution state and session survival, and it
/// is always derived from concrete transport evidence.
/// </summary>
internal sealed class DebuggerBridgeException(
    string message,
    DebuggerBridgeFault fault,
    Exception? innerException = null,
    DebuggerBridgeDiagnostics? diagnostics = null)
    : Exception(
        message,
        innerException)
{
    public DebuggerBridgeFault Fault { get; } =
        fault;

    /// <summary>
    /// Bounded transport facts captured at the fault. Purely additive
    /// observability: <see cref="Fault"/> remains the single input to the
    /// reported execution state and to session survival, and no consumer may
    /// re-derive submission from this record.
    /// </summary>
    public DebuggerBridgeDiagnostics? Diagnostics { get; } =
        diagnostics;

    /// <summary>
    /// True only when the request may have reached Adobe. Kept as a property so
    /// no consumer can re-derive submission from elapsed time.
    /// </summary>
    public bool Submitted =>
        Fault == DebuggerBridgeFault.TransportLostAfterSubmit;
}

internal sealed record DebuggerBridgeCloseResult(
    bool Graceful,
    bool ExitObserved);

internal sealed record DebuggerBridgeAssetInfo(
    string Path,
    string Sha256);

internal static class DebuggerBridgeAsset
{
    private const string ResourceName =
        "ComTool.Hosts.Illustrator.Assets.esd-debugger-bridge.mjs";

    public static DebuggerBridgeAssetInfo
        Materialize()
    {
        var assembly =
            typeof(DebuggerBridgeAsset)
                .Assembly;
        using var stream =
            assembly.GetManifestResourceStream(
                ResourceName) ??
            throw new InvalidOperationException(
                $"Embedded debugger bridge resource '{ResourceName}' was not found.");

        using var memory =
            new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var sha256 =
            Convert.ToHexString(
                    SHA256.HashData(bytes))
                .ToLowerInvariant();

        var localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData);
        if (string.IsNullOrWhiteSpace(
                localAppData))
            throw new InvalidOperationException(
                "LOCALAPPDATA is unavailable; debugger bridge cannot be materialized.");

        var directory = Path.Combine(
            localAppData,
            "ComToolV2",
            "runtime-assets",
            "esd-debugger",
            sha256);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory,
            "esd-debugger-bridge.mjs");

        if (File.Exists(path) &&
            string.Equals(
                ComputeSha256Hex(path),
                sha256,
                StringComparison.Ordinal))
        {
            return new DebuggerBridgeAssetInfo(
                path,
                sha256);
        }

        var tempPath =
            path +
            $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(
            tempPath,
            bytes);
        File.Move(
            tempPath,
            path,
            overwrite: true);

        if (!string.Equals(
                ComputeSha256Hex(path),
                sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Materialized debugger bridge failed SHA-256 verification.");
        }

        return new DebuggerBridgeAssetInfo(
            path,
            sha256);
    }

    private static string ComputeSha256Hex(
        string path)
    {
        using var stream =
            File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }
}
