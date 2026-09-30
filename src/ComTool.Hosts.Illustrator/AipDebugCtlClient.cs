using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// One fully-bounded <c>aipdebugctl</c> invocation. The endpoint, the action,
/// and the expected server process id are supplied by runtime-owned code, never
/// forwarded from caller input.
/// </summary>
internal sealed record AipDebugCtlInvocation(
    string Endpoint,
    string Action,
    int ExpectedProcessId,
    long ExpectedProcessStartFileTimeUtc,
    long? LogAfter,
    int? LogLimit,
    int TimeoutMs,
    int MaxOutputBytes);

internal enum AipDebugCtlCompletion
{
    Completed,
    Unavailable,
    TimedOut,
    Cancelled,
    OutputLimitExceeded,
    LaunchFailed,
    ProcessFailure
}

internal sealed record AipDebugCtlOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    AipDebugCtlCompletion Completion);

/// <summary>
/// Seam for the proven native VectorIPC client. The production implementation
/// spawns the bounded child process; tests substitute a deterministic invoker so
/// the facet's provenance, transport, and ambiguity rules can be proven without
/// a live Illustrator process or a real named pipe.
/// </summary>
internal interface IAipDebugCtlInvoker
{
    AipDebugCtlOutcome Invoke(
        AipDebugCtlInvocation invocation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Bounded invoker for the AIPDebug project's own native VectorIPC client.
/// The VectorIPC wire format, named-pipe ACLs, and the peer-PID check stay in
/// the audited native client; this type only bounds process lifetime and output
/// so a hung or hostile child cannot stall or flood the worker.
/// </summary>
internal sealed class AipDebugCtlClient : IAipDebugCtlInvoker
{
    internal const string ExecutableName = "aipdebugctl.exe";
    internal const string ExecutableOverrideVariable = "COMTOOL_AIPDEBUGCTL";
    internal const int MinTimeoutMs = 1;
    internal const int DefaultTimeoutMs = 1_000;
    internal const int MaxTimeoutMs = 30_000;
    internal const int MaxOutputBytes = 1024 * 1024;

    /// <summary>0 = protocol success.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>
    /// 4 = a valid VectorIPC response that carries an application-level
    /// <c>ok:false</c> result. The request reached the server and the server
    /// refused; this is not a transport failure.
    /// </summary>
    internal const int ExitApplicationError = 4;

    private const int WallClockGraceMs = 1_000;

    private readonly Func<string?> _locate;

    public AipDebugCtlClient()
        : this(Locate)
    {
    }

    internal AipDebugCtlClient(Func<string?> locate) =>
        _locate = locate;

    public AipDebugCtlOutcome Invoke(
        AipDebugCtlInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var validationError = ValidateInvocation(invocation);
        if (validationError is not null)
        {
            return new AipDebugCtlOutcome(
                -1,
                string.Empty,
                validationError,
                AipDebugCtlCompletion.LaunchFailed);
        }

        string? executable;
        try
        {
            executable = _locate();
        }
        catch (AipDebugCtlTrustException ex)
        {
            return new AipDebugCtlOutcome(
                -1,
                string.Empty,
                ex.Message,
                AipDebugCtlCompletion.LaunchFailed);
        }

        if (executable is null)
        {
            return new AipDebugCtlOutcome(
                -1,
                string.Empty,
                $"{ExecutableName} was not found. Build/install AIPDebug or set " +
                $"{ExecutableOverrideVariable} to an absolute executable path.",
                AipDebugCtlCompletion.Unavailable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in BuildArguments(invocation))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new AipDebugCtlOutcome(
                    -1,
                    string.Empty,
                    $"{ExecutableName} did not start.",
                    AipDebugCtlCompletion.LaunchFailed);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AipDebugCtlOutcome(
                -1,
                string.Empty,
                $"{ExecutableName} could not be launched: {ex.Message}",
                AipDebugCtlCompletion.LaunchFailed);
        }

        var stdout = new BoundedByteReader(
            process.StandardOutput.BaseStream,
            invocation.MaxOutputBytes);
        var stderr = new BoundedByteReader(
            process.StandardError.BaseStream,
            invocation.MaxOutputBytes);

        using var wallClock = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            wallClock.Token);
        wallClock.CancelAfter(
            invocation.TimeoutMs + WallClockGraceMs);

        try
        {
            process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            CompleteReaders(stdout, stderr);
            return new AipDebugCtlOutcome(
                -1,
                stdout.Text,
                stderr.Text,
                cancellationToken.IsCancellationRequested
                    ? AipDebugCtlCompletion.Cancelled
                    : AipDebugCtlCompletion.TimedOut);
        }
        catch (InvalidOperationException)
        {
            KillQuietly(process);
            CompleteReaders(stdout, stderr);
            return new AipDebugCtlOutcome(
                -1,
                stdout.Text,
                stderr.Text,
                AipDebugCtlCompletion.ProcessFailure);
        }

        CompleteReaders(stdout, stderr);
        var completion = stdout.Truncated || stderr.Truncated
            ? AipDebugCtlCompletion.OutputLimitExceeded
            : AipDebugCtlCompletion.Completed;

        var exitCode = completion == AipDebugCtlCompletion.Completed
            ? process.ExitCode
            : -1;
        if (completion == AipDebugCtlCompletion.OutputLimitExceeded)
            KillQuietly(process);

        return new AipDebugCtlOutcome(
            exitCode,
            stdout.Text,
            stderr.Text,
            completion);
    }

    private static string? ValidateInvocation(
        AipDebugCtlInvocation invocation)
    {
        if (!IllustratorPluginDebugManager.IsValidEndpoint(
                invocation.Endpoint))
        {
            return "The runtime-owned AIPDebug endpoint is invalid.";
        }

        if (string.IsNullOrWhiteSpace(invocation.Action) ||
            invocation.Action.Length > 32)
        {
            return "The runtime-owned AIPDebug action is invalid.";
        }

        if (invocation.ExpectedProcessId <= 0 ||
            invocation.ExpectedProcessStartFileTimeUtc <= 0)
        {
            return "The runtime-owned AIPDebug process generation is invalid.";
        }

        if (invocation.TimeoutMs < MinTimeoutMs ||
            invocation.TimeoutMs > MaxTimeoutMs)
        {
            return
                $"The AIPDebug timeout must be between {MinTimeoutMs} and " +
                $"{MaxTimeoutMs} ms.";
        }

        if (invocation.MaxOutputBytes <= 0 ||
            invocation.MaxOutputBytes > MaxOutputBytes)
        {
            return
                $"The AIPDebug output bound must be between 1 and " +
                $"{MaxOutputBytes} bytes.";
        }

        if (invocation.LogAfter is < 0)
            return "The AIPDebug log offset must be non-negative.";

        if (invocation.LogLimit is < 0 or > 1000)
            return "The AIPDebug log limit is outside the runtime bound.";

        return null;
    }

    /// <summary>
    /// Builds the child argv. The expected server process id is always present
    /// and always comes from the runtime-owned target identity, so a caller can
    /// never talk to an endpoint whose server was not proven to be the pinned
    /// Illustrator generation.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        AipDebugCtlInvocation invocation)
    {
        var arguments = new List<string>(10)
        {
            invocation.Endpoint,
            invocation.Action,
            "--timeout-ms",
            invocation.TimeoutMs.ToString(CultureInfo.InvariantCulture)
        };

        if (invocation.LogAfter is not null || invocation.LogLimit is not null)
        {
            var after = Math.Max(0L, invocation.LogAfter ?? 0L);
            var limit = invocation.LogLimit ?? 0;

            arguments.Add("--after");
            arguments.Add(after.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--limit");
            arguments.Add(limit.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--expect-pid");
        arguments.Add(
            invocation.ExpectedProcessId.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--expect-start-filetime");
        arguments.Add(
            invocation.ExpectedProcessStartFileTimeUtc.ToString(
                CultureInfo.InvariantCulture));
        return arguments;
    }

    /// <summary>
    /// Runtime-owned resolution. A release-manifest sibling is authoritative
    /// and hash-verified; the environment override exists only as an explicit
    /// unpackaged-development fallback and is never treated as diagnostic input.
    /// </summary>
    internal static string? Locate()
    {
        var configured =
            Environment.GetEnvironmentVariable(
                ExecutableOverrideVariable);
        return LocateFrom(
            AppContext.BaseDirectory,
            configured);
    }

    internal static string? LocateFrom(
        string baseDirectory,
        string? configured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var sibling = Path.Combine(
            Path.GetFullPath(baseDirectory),
            ExecutableName);
        var manifest = Path.Combine(
            Path.GetFullPath(baseDirectory),
            "release-manifest.json");

        // A packaged runtime is authoritative over developer/operator
        // overrides. The release manifest is emitted after optional signing,
        // so this check binds the exact bytes that package verification and
        // installation accepted to the helper that the worker will execute.
        if (File.Exists(manifest))
        {
            var descriptor = ReadPackagedDescriptor(manifest);
            if (!descriptor.Available)
            {
                if (File.Exists(sibling))
                {
                    throw new AipDebugCtlTrustException(
                        "The release manifest declares aipdebugctl.exe unavailable, " +
                        "but an unmanifested sibling is present.");
                }

                return null;
            }

            if (!File.Exists(sibling))
            {
                throw new AipDebugCtlTrustException(
                    "The release manifest declares aipdebugctl.exe available, but " +
                    "the packaged sibling is missing.");
            }

            VerifyPackagedSibling(sibling, descriptor);
            return sibling;
        }

        // Local development may stage a sibling without a release manifest.
        // Prefer that deterministic adjacent tool before consulting the
        // explicit development override.
        if (File.Exists(sibling))
            return sibling;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var full = Path.GetFullPath(configured);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // Fall through to the remaining runtime-owned locations.
            }
        }

        return null;
    }

    private static PackagedHelperDescriptor ReadPackagedDescriptor(
        string manifestPath)
    {
        try
        {
            using var manifestStream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(manifestStream);
            var helper = document.RootElement
                .GetProperty("nativeHelpers")
                .GetProperty("aipdebugctl");
            var path = helper.GetProperty("path").GetString();
            var transport =
                helper.GetProperty("transport").GetString();
            var required =
                helper.GetProperty("required").GetBoolean();
            var available =
                helper.TryGetProperty("available", out var availableElement)
                    ? availableElement.GetBoolean()
                    : true;
            var expectedSha256 =
                helper.TryGetProperty("sha256", out var shaElement) &&
                shaElement.ValueKind != JsonValueKind.Null
                    ? shaElement.GetString()
                    : null;
            var shaValid =
                !string.IsNullOrWhiteSpace(expectedSha256) &&
                expectedSha256.Length == 64 &&
                expectedSha256.All(
                    character => Uri.IsHexDigit(character));

            if (!string.Equals(
                    path,
                    ExecutableName,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    transport,
                    "vectoripc",
                    StringComparison.Ordinal) ||
                (required && !available) ||
                (available && !shaValid) ||
                (!available && expectedSha256 is not null))
            {
                throw new AipDebugCtlTrustException(
                    "release-manifest.json contains invalid aipdebugctl provenance.");
            }

            return new PackagedHelperDescriptor(
                required,
                available,
                expectedSha256);
        }
        catch (AipDebugCtlTrustException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidOperationException or
            KeyNotFoundException)
        {
            throw new AipDebugCtlTrustException(
                "Packaged aipdebugctl provenance could not be verified.",
                ex);
        }
    }

    private static void VerifyPackagedSibling(
        string executablePath,
        PackagedHelperDescriptor descriptor)
    {
        try
        {
            using var executable = File.OpenRead(executablePath);
            var actualSha256 = Convert
                .ToHexString(SHA256.HashData(executable))
                .ToLowerInvariant();
            if (!string.Equals(
                    actualSha256,
                    descriptor.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AipDebugCtlTrustException(
                    "Packaged aipdebugctl.exe does not match the " +
                    "release-manifest SHA-256.");
            }
        }
        catch (AipDebugCtlTrustException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidOperationException or
            KeyNotFoundException)
        {
            throw new AipDebugCtlTrustException(
                "Packaged aipdebugctl provenance could not be verified.",
                ex);
        }
    }

    private sealed record PackagedHelperDescriptor(
        bool Required,
        bool Available,
        string? Sha256);

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void CompleteReaders(
        BoundedByteReader stdout,
        BoundedByteReader stderr)
    {
        try
        {
            Task.WhenAll(stdout.Completion, stderr.Completion)
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The child process has already exited or has just been killed.
            // Reader failures are represented by whatever bounded prefix was
            // captured; they must not mask the invocation completion state.
        }
    }

    /// <summary>
    /// Reads one child stream into a byte-bounded buffer. The limit is measured
    /// on the actual redirected UTF-8 bytes, not UTF-16 characters, so
    /// non-ASCII output cannot exceed the advertised byte budget.
    /// </summary>
    private sealed class BoundedByteReader
    {
        private readonly int _limit;
        private readonly MemoryStream _buffer;
        private readonly object _gate = new();
        private long _seen;
        private readonly Task _completion;

        public BoundedByteReader(Stream stream, int limit)
        {
            _limit = limit;
            _buffer = new MemoryStream(Math.Min(limit, 64 * 1024));
            _completion = Task.Run(
                () => Pump(stream),
                CancellationToken.None);
        }

        public bool Truncated { get; private set; }

        public Task Completion => _completion;

        public string Text
        {
            get
            {
                lock (_gate)
                    return Encoding.UTF8.GetString(_buffer.ToArray());
            }
        }

        /// <summary>
        /// Drains the stream to completion so the child is never blocked on a
        /// full pipe, but keeps only the bounded prefix: everything past the
        /// limit is discarded and reported instead of buffered.
        /// </summary>
        private void Pump(Stream stream)
        {
            var scratch = new byte[8 * 1024];
            try
            {
                while (true)
                {
                    var read = stream.Read(
                        scratch,
                        0,
                        scratch.Length);
                    if (read <= 0)
                        return;

                    lock (_gate)
                    {
                        var remaining =
                            Math.Max(0L, (long)_limit - _seen);
                        var keep = (int)Math.Min(remaining, read);
                        if (keep > 0)
                        {
                            _buffer.Write(scratch, 0, keep);
                        }

                        _seen += read;
                        if (_seen > _limit)
                            Truncated = true;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}

internal sealed class AipDebugCtlTrustException : Exception
{
    public AipDebugCtlTrustException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
