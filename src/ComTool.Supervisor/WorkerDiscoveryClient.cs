using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Transport.Pipe;

namespace ComTool.Supervisor;

/// <summary>
/// Runs host discovery inside a short-lived STA worker so the persistent
/// supervisor never acquires COM proxies or depends on host threading rules.
/// </summary>
public static class WorkerDiscoveryClient
{
    private const string TokenEnvironmentVariable = "COMTOOL_V2_WORKER_TOKEN";

    public static async Task<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
        string host,
        WorkerBrokerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);

        var workerPath = Path.GetFullPath(options.WorkerExecutablePath);
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("Worker executable was not found.", workerPath);

        var pipeName = $"comtool-v2-discovery-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process = null;
        LengthPrefixedFramedStream? framed = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = options.CreateNoWindow,
                WorkingDirectory = Path.GetDirectoryName(workerPath)
                    ?? Environment.CurrentDirectory
            };

            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(host);
            startInfo.ArgumentList.Add("--discover");
            startInfo.ArgumentList.Add("--max-frame-bytes");
            startInfo.ArgumentList.Add(
                options.MaxFrameBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            startInfo.Environment[TokenEnvironmentVariable] = token;

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Discovery worker process failed to start.");

            process.Refresh();
            var processStartedAt = new DateTimeOffset(process.StartTime);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            connectCts.CancelAfter(options.ConnectTimeout);

            await WorkerProcessStartup
                .WaitForConnectionOrExitAsync(
                    pipe,
                    process,
                    connectCts.Token)
                .ConfigureAwait(false);

            framed = new LengthPrefixedFramedStream(
                pipe,
                options.MaxFrameBytes,
                leaveOpen: true);

            using var helloFrame = await framed
                .ReadAsync(connectCts.Token)
                .ConfigureAwait(false);

            var hello = BrokerJson.DeserializeHello(helloFrame.Span);
            var validationError = ValidateHello(
                hello,
                host,
                token,
                process,
                processStartedAt);

            await framed.WriteAsync(
                BrokerJson.Serialize(new WorkerHelloAck
                {
                    BrokerVersion = BrokerVersion.Current,
                    Accepted = validationError is null,
                    Error = validationError
                }),
                connectCts.Token).ConfigureAwait(false);

            if (validationError is not null)
                throw new InvalidDataException(validationError);

            var discover = new BrokerCommand
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = 1,
                RequestId = $"discover-{Guid.NewGuid():N}",
                Kind = BrokerCommandKind.Discover
            };

            BrokerResponse discoveryResponse;
            using (var commandCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken))
            {
                commandCts.CancelAfter(options.CommandTimeout);

                await framed
                    .WriteAsync(
                        BrokerJson.Serialize(discover),
                        commandCts.Token)
                    .ConfigureAwait(false);

                using var responseFrame = await framed
                    .ReadAsync(commandCts.Token)
                    .ConfigureAwait(false);

                discoveryResponse = BrokerJson.DeserializeResponse(
                    responseFrame.Span);
            }

            ValidateResponse(discover, discoveryResponse);

            if (!discoveryResponse.Ok ||
                discoveryResponse.Kind != BrokerResponseKind.Discovery ||
                discoveryResponse.Targets is null)
            {
                throw new InvalidOperationException(
                    discoveryResponse.Error is null
                        ? "Discovery worker returned no target list."
                        : $"{discoveryResponse.Error.Kind}: {discoveryResponse.Error.Message}");
            }

            var targets = discoveryResponse.Targets.ToArray();

            var shutdown = new BrokerCommand
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = 2,
                RequestId = $"shutdown-{Guid.NewGuid():N}",
                Kind = BrokerCommandKind.Shutdown
            };

            using (var shutdownCts = new CancellationTokenSource(
                       TimeSpan.FromSeconds(5)))
            {
                await framed
                    .WriteAsync(
                        BrokerJson.Serialize(shutdown),
                        shutdownCts.Token)
                    .ConfigureAwait(false);

                using var shutdownFrame = await framed
                    .ReadAsync(shutdownCts.Token)
                    .ConfigureAwait(false);

                var shutdownResponse = BrokerJson.DeserializeResponse(
                    shutdownFrame.Span);
                ValidateResponse(shutdown, shutdownResponse);
            }

            using (var exitCts = new CancellationTokenSource(
                       TimeSpan.FromSeconds(5)))
            {
                await process
                    .WaitForExitAsync(exitCts.Token)
                    .ConfigureAwait(false);
            }

            return targets;
        }
        catch
        {
            if (process is not null)
                await TerminateOwnedProcessAsync(process).ConfigureAwait(false);

            throw;
        }
        finally
        {
            if (framed is not null)
                await framed.DisposeAsync().ConfigureAwait(false);

            process?.Dispose();
        }
    }

    private static string? ValidateHello(
        WorkerHello hello,
        string expectedHost,
        string expectedToken,
        Process process,
        DateTimeOffset expectedProcessStartedAt)
    {
        if (hello.BrokerVersion != BrokerVersion.Current)
            return "Discovery worker broker protocol version mismatch.";

        if (!FixedTimeEquals(hello.Token, expectedToken))
            return "Discovery worker authentication token mismatch.";

        if (hello.Mode != WorkerMode.Discovery)
            return $"Expected discovery worker, got '{hello.Mode}'.";

        if (hello.ProcessId != process.Id)
            return "Discovery worker PID does not match launched child.";

        if (hello.ProcessStartedAt.ToUniversalTime().Ticks !=
            expectedProcessStartedAt.ToUniversalTime().Ticks)
            return "Discovery worker process creation time mismatch.";

        if (!string.Equals(hello.Apartment, "STA", StringComparison.Ordinal))
            return $"Discovery worker apartment must be STA, got '{hello.Apartment}'.";

        if (!string.Equals(
                hello.Host,
                expectedHost,
                StringComparison.Ordinal))
            return "Discovery worker host does not match request.";

        if (hello.TargetId is not null)
            return "Discovery worker must not claim a target identity.";

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
                "Discovery worker response correlation/version check failed.");
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task TerminateOwnedProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
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
