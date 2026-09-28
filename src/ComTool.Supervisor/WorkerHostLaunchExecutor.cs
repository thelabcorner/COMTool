using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Transport.Pipe;

namespace ComTool.Supervisor;

/// <summary>
/// Production launch executor. It starts only the authenticated short-lived
/// COM Tool Worker; Adobe activation itself occurs on that worker's STA thread.
/// </summary>
public sealed class WorkerHostLaunchExecutor : IHostLaunchExecutor
{
    private const string TokenEnvironmentVariable = "COMTOOL_V2_WORKER_TOKEN";

    private readonly WorkerBrokerOptions _options;

    public WorkerHostLaunchExecutor(
        string host,
        WorkerBrokerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);

        Host = host;
        _options = options;
    }

    public string Host { get; }

    public async ValueTask<HostLaunchObservation> LaunchAsync(
        HostLaunchSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var validation = HostLaunchSpecValidator.Validate(spec, [Host]);
        if (validation is not null)
        {
            throw new HostLaunchException(
                validation.Kind,
                validation.Message,
                retryable: false,
                ComTool.Protocol.ExecutionState.NotStarted);
        }

        if (!string.Equals(spec.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new HostLaunchException(
                "host_family_mismatch",
                $"Launch executor '{Host}' cannot execute host '{spec.Host}'.",
                retryable: false,
                ComTool.Protocol.ExecutionState.NotStarted);
        }

        var workerPath = Path.GetFullPath(_options.WorkerExecutablePath);
        if (!File.Exists(workerPath))
        {
            throw new HostLaunchException(
                "launch_worker_not_found",
                "Launch worker executable was not found.",
                retryable: false,
                ComTool.Protocol.ExecutionState.NotStarted);
        }

        var pipeName =
            $"comtool-v2-launch-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process = null;
        LengthPrefixedFramedStream? framed = null;
        var commandDispatched = false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = _options.CreateNoWindow,
                WorkingDirectory =
                    Path.GetDirectoryName(workerPath)
                    ?? Environment.CurrentDirectory
            };

            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(Host);
            startInfo.ArgumentList.Add("--launch");
            startInfo.ArgumentList.Add("--max-frame-bytes");
            startInfo.ArgumentList.Add(
                _options.MaxFrameBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            startInfo.Environment[TokenEnvironmentVariable] = token;

            process = Process.Start(startInfo)
                ?? throw new HostLaunchException(
                    "launch_worker_start_failed",
                    "Launch worker process failed to start.",
                    retryable: false,
                    ComTool.Protocol.ExecutionState.NotStarted);

            process.Refresh();
            var processStartedAt =
                new DateTimeOffset(process.StartTime);

            using var connectCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            connectCts.CancelAfter(_options.ConnectTimeout);

            await WorkerProcessStartup
                .WaitForConnectionOrExitAsync(
                    pipe,
                    process,
                    connectCts.Token)
                .ConfigureAwait(false);

            framed = new LengthPrefixedFramedStream(
                pipe,
                _options.MaxFrameBytes,
                leaveOpen: true);

            using var helloFrame = await framed
                .ReadAsync(connectCts.Token)
                .ConfigureAwait(false);

            var hello = BrokerJson.DeserializeHello(helloFrame.Span);
            var helloError = ValidateHello(
                hello,
                token,
                process,
                processStartedAt);

            await framed.WriteAsync(
                    BrokerJson.Serialize(new WorkerHelloAck
                    {
                        BrokerVersion = BrokerVersion.Current,
                        Accepted = helloError is null,
                        Error = helloError
                    }),
                    connectCts.Token)
                .ConfigureAwait(false);

            if (helloError is not null)
            {
                throw new HostLaunchException(
                    "launch_worker_handshake_failed",
                    helloError,
                    retryable: false,
                    ComTool.Protocol.ExecutionState.NotStarted);
            }

            var command = new BrokerCommand
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = 1,
                RequestId = $"launch-{Guid.NewGuid():N}",
                Kind = BrokerCommandKind.Launch,
                Launch = spec
            };

            var launchWindow =
                TimeSpan.FromMilliseconds(spec.LaunchTimeoutMs) +
                TimeSpan.FromSeconds(5);
            var commandTimeout =
                _options.CommandTimeout > launchWindow
                    ? _options.CommandTimeout
                    : launchWindow;

            BrokerResponse response;
            using (var commandCts =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken))
            {
                commandCts.CancelAfter(commandTimeout);

                await framed.WriteAsync(
                        BrokerJson.Serialize(command),
                        commandCts.Token)
                    .ConfigureAwait(false);
                commandDispatched = true;

                using var responseFrame = await framed
                    .ReadAsync(commandCts.Token)
                    .ConfigureAwait(false);

                response = BrokerJson.DeserializeResponse(
                    responseFrame.Span);
            }

            ValidateResponse(command, response);

            if (!response.Ok)
            {
                var error = response.Error;
                throw new HostLaunchException(
                    error?.Kind ?? "host_launch_failed",
                    error?.Message ?? "Launch worker reported failure.",
                    error?.Retryable ?? false,
                    error?.Execution ??
                        ComTool.Protocol.ExecutionState.Ambiguous,
                    error?.HResult);
            }

            if (response.Kind != BrokerResponseKind.Launch ||
                response.Launch is null)
            {
                throw new HostLaunchException(
                    "launch_worker_invalid_response",
                    "Launch worker returned no launch observation.",
                    retryable: false,
                    ComTool.Protocol.ExecutionState.Ambiguous);
            }

            var observation = response.Launch;

            await ShutdownWorkerBestEffortAsync(
                    framed,
                    process,
                    sequence: 2)
                .ConfigureAwait(false);

            return observation;
        }
        catch (HostLaunchException)
        {
            if (process is not null)
                await TerminateOwnedWorkerAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
                await TerminateOwnedWorkerAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (process is not null)
                await TerminateOwnedWorkerAsync(process).ConfigureAwait(false);

            throw new HostLaunchException(
                "launch_worker_failure",
                $"Launch worker communication failed: {ex.Message}",
                retryable: false,
                commandDispatched
                    ? ComTool.Protocol.ExecutionState.Ambiguous
                    : ComTool.Protocol.ExecutionState.NotStarted,
                ex.HResult,
                ex);
        }
        finally
        {
            if (framed is not null)
                await framed.DisposeAsync().ConfigureAwait(false);

            process?.Dispose();
        }
    }

    private string? ValidateHello(
        WorkerHello hello,
        string expectedToken,
        Process process,
        DateTimeOffset expectedProcessStartedAt)
    {
        if (hello.BrokerVersion != BrokerVersion.Current)
            return "Launch worker broker protocol version mismatch.";

        if (!FixedTimeEquals(hello.Token, expectedToken))
            return "Launch worker authentication token mismatch.";

        if (hello.Mode != WorkerMode.Launch)
            return $"Expected launch worker, got '{hello.Mode}'.";

        if (hello.ProcessId != process.Id)
            return "Launch worker PID does not match launched child.";

        if (hello.ProcessStartedAt.ToUniversalTime().Ticks !=
            expectedProcessStartedAt.ToUniversalTime().Ticks)
        {
            return "Launch worker process creation time mismatch.";
        }

        if (!string.Equals(hello.Apartment, "STA", StringComparison.Ordinal))
            return $"Launch worker apartment must be STA, got '{hello.Apartment}'.";

        if (!string.Equals(hello.Host, Host, StringComparison.Ordinal))
            return "Launch worker host does not match request.";

        if (hello.TargetId is not null)
            return "Launch worker must not claim a target identity.";

        return null;
    }

    private static void ValidateResponse(
        BrokerCommand command,
        BrokerResponse response)
    {
        if (response.BrokerVersion != BrokerVersion.Current ||
            response.Sequence != command.Sequence ||
            !string.Equals(
                response.RequestId,
                command.RequestId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Launch worker response correlation/version check failed.");
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   leftBytes,
                   rightBytes);
    }

    private static async Task ShutdownWorkerBestEffortAsync(
        LengthPrefixedFramedStream framed,
        Process process,
        long sequence)
    {
        try
        {
            var shutdown = new BrokerCommand
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = sequence,
                RequestId = $"shutdown-{Guid.NewGuid():N}",
                Kind = BrokerCommandKind.Shutdown
            };

            using var cts =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await framed.WriteAsync(
                    BrokerJson.Serialize(shutdown),
                    cts.Token)
                .ConfigureAwait(false);

            using var responseFrame = await framed
                .ReadAsync(cts.Token)
                .ConfigureAwait(false);

            var response = BrokerJson.DeserializeResponse(
                responseFrame.Span);
            ValidateResponse(shutdown, response);

            await process.WaitForExitAsync(cts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            await TerminateOwnedWorkerAsync(process).ConfigureAwait(false);
        }
    }

    private static async Task TerminateOwnedWorkerAsync(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);
            using var cts =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
