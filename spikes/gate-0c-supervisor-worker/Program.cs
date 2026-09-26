using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const int MaxFrameBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [STAThread]
    private static int Main(string[] args)
    {
        // Host-worker mode deliberately remains synchronous on the process main
        // STA thread. No await is allowed to migrate host execution to an MTA
        // thread-pool continuation.
        if (args.Contains("--worker", StringComparer.Ordinal))
            return RunWorker(args);

        return RunSupervisorAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunSupervisorAsync()
    {
        var sw = Stopwatch.StartNew();
        const string targetId = "fake:illustrator-reference:gate0c";
        var checks = new List<Check>();
        WorkerClient? worker = null;
        WorkerClient? restarted = null;
        WorkerClient? recovered = null;

        try
        {
            worker = await WorkerClient.StartAsync(targetId);
            checks.Add(new("handshake", worker.Identity.TargetId == targetId &&
                                        worker.Identity.Pid == worker.Process.Id &&
                                        worker.Identity.Apartment == "STA",
                worker.Identity));

            var ping = await worker.SendAsync("ping", timeout: TimeSpan.FromSeconds(3));
            checks.Add(new("ping", ping.Ok && ping.Op == "ping", ping));

            // Forty concurrent callers x 25 reads. WorkerClient serializes the single
            // target execution lane; worker readSequence proves exactly-once ordering.
            var tasks = Enumerable.Range(0, 40)
                .SelectMany(_ => Enumerable.Range(0, 25))
                .Select(_ => worker.SendAsync("read", timeout: TimeSpan.FromSeconds(5)))
                .ToArray();

            var responses = await Task.WhenAll(tasks);
            var sequences = responses.Select(r => r.Sequence ?? -1).Order().ToArray();
            var serializedOk =
                responses.All(r => r.Ok) &&
                sequences.Length == 1000 &&
                sequences.Distinct().Count() == 1000 &&
                sequences[0] == 1 &&
                sequences[^1] == 1000;

            checks.Add(new("concurrent-callers-serialized", serializedOk,
                new
                {
                    callers = 40,
                    requestsPerCaller = 25,
                    total = responses.Length,
                    firstSequence = sequences[0],
                    lastSequence = sequences[^1],
                    uniqueSequences = sequences.Distinct().Count()
                }));

            var firstIdentity = worker.Identity;
            await worker.ShutdownAsync();
            await worker.DisposeAsync();
            worker = null;

            restarted = await WorkerClient.StartAsync(targetId);
            var secondIdentity = restarted.Identity;
            checks.Add(new("worker-restart",
                firstIdentity.TargetId == secondIdentity.TargetId &&
                firstIdentity.Pid != secondIdentity.Pid &&
                firstIdentity.StartUtc != secondIdentity.StartUtc &&
                firstIdentity.TokenFingerprint != secondIdentity.TokenFingerprint,
                new { before = firstIdentity, after = secondIdentity }));

            string targetState = "known";
            bool watchdogFired = false;
            try
            {
                await restarted.SendAsync("mutate-hang", timeout: TimeSpan.FromMilliseconds(500));
            }
            catch (OperationCanceledException)
            {
                watchdogFired = true;
                targetState = "reconciliation_required";
            }
            catch (TimeoutException)
            {
                watchdogFired = true;
                targetState = "reconciliation_required";
            }

            if (!watchdogFired)
                throw new InvalidOperationException("mutate-hang unexpectedly completed");

            // We own this Process object from Process.Start. Termination is performed
            // only against that still-live owned child, never by stale persisted PID.
            await restarted.TerminateOwnedChildAsync();
            await restarted.DisposeAsync();
            restarted = null;

            checks.Add(new("ambiguous-mutation-watchdog",
                watchdogFired && targetState == "reconciliation_required",
                new { watchdogFired, targetState }));

            recovered = await WorkerClient.StartAsync(targetId);
            var reconcile = await recovered.SendAsync("reconcile", timeout: TimeSpan.FromSeconds(3));
            if (reconcile.Ok)
                targetState = "known";

            checks.Add(new("explicit-reconcile",
                reconcile.Ok && targetState == "known",
                new { reconcile, targetState, recoveredWorker = recovered.Identity }));

            await recovered.ShutdownAsync();

            sw.Stop();
            var report = new
            {
                gate = "0C",
                ok = checks.All(c => c.Ok),
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                maxFrameBytes = MaxFrameBytes,
                targetId,
                checks
            };

            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return checks.All(c => c.Ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var report = new
            {
                gate = "0C",
                ok = false,
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                error = new
                {
                    kind = ex.GetType().Name,
                    message = ex.Message,
                    hresult = ex.HResult,
                    hresultHex = $"0x{unchecked((uint)ex.HResult):X8}"
                },
                checks
            };
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 1;
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            if (restarted is not null) await restarted.DisposeAsync();
            if (recovered is not null) await recovered.DisposeAsync();
        }
    }

    private static int RunWorker(string[] args)
    {
        var pipeName = RequiredArg(args, "--pipe");
        var token = RequiredArg(args, "--token");
        var targetId = RequiredArg(args, "--target");

        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None);

        pipe.WaitForConnection();

        long readSequence = 0;
        while (pipe.IsConnected)
        {
            Request request;
            try
            {
                request = Frames.Read<Request>(pipe);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            if (request.Token != token)
            {
                Frames.Write(pipe,
                    new Response(request.Id, request.Op, false, null, "authentication_failed", null, null));
                break;
            }

            switch (request.Op)
            {
                case "hello":
                {
                    var process = Process.GetCurrentProcess();
                    var identity = new WorkerIdentity(
                        targetId,
                        process.Id,
                        process.StartTime.ToUniversalTime().ToString("O"),
                        Thread.CurrentThread.GetApartmentState().ToString(),
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).Substring(0, 16));

                    Frames.Write(pipe,
                        new Response(request.Id, request.Op, true, null, null, identity, null));
                    break;
                }
                case "ping":
                    Frames.Write(pipe,
                        new Response(request.Id, request.Op, true, null, null, null, null));
                    break;
                case "read":
                {
                    var sequence = Interlocked.Increment(ref readSequence);
                    Frames.Write(pipe,
                        new Response(request.Id, request.Op, true, sequence, null, null, null));
                    break;
                }
                case "reconcile":
                    Frames.Write(pipe,
                        new Response(request.Id, request.Op, true, null, null, null, "known"));
                    break;
                case "mutate-hang":
                    // Simulate a synchronous host mutation whose execution status becomes
                    // unknowable when the worker watchdog fires. No response is emitted.
                    Thread.Sleep(Timeout.Infinite);
                    break;
                case "shutdown":
                    Frames.Write(pipe,
                        new Response(request.Id, request.Op, true, null, null, null, null));
                    return 0;
                default:
                    Frames.Write(pipe,
                        new Response(request.Id, request.Op, false, null, "unknown_operation", null, null));
                    break;
            }
        }

        return 0;
    }

    private static string RequiredArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException($"Missing {name}");
        return args[index + 1];
    }

    private sealed class WorkerClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly SemaphoreSlim _lane = new(1, 1);
        private long _nextRequestId;
        private bool _disposed;

        private WorkerClient(Process process, NamedPipeClientStream pipe, string token, WorkerIdentity identity)
        {
            Process = process;
            _pipe = pipe;
            Token = token;
            Identity = identity;
        }

        public Process Process { get; }
        public string Token { get; }
        public WorkerIdentity Identity { get; }

        public static async Task<WorkerClient> StartAsync(string targetId)
        {
            var pipeName = $"comtoolv2_gate0c_{Guid.NewGuid():N}";
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            var startInfo = CreateSelfStartInfo(
                "--worker",
                "--pipe", pipeName,
                "--token", token,
                "--target", targetId);

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start worker process");

            var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await pipe.ConnectAsync(cts.Token);

                var provisional = new WorkerClient(
                    process,
                    pipe,
                    token,
                    new WorkerIdentity(targetId, -1, "", "", ""));

                var hello = await provisional.SendAsync("hello", TimeSpan.FromSeconds(3));
                if (!hello.Ok || hello.Identity is null)
                    throw new InvalidOperationException("Worker handshake failed");

                return new WorkerClient(process, pipe, token, hello.Identity);
            }
            catch
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }

                pipe.Dispose();
                process.Dispose();
                throw;
            }
        }

        public async Task<Response> SendAsync(string op, TimeSpan timeout)
        {
            await _lane.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                var id = Interlocked.Increment(ref _nextRequestId).ToString();
                var request = new Request(id, op, Token);
                await Frames.WriteAsync(_pipe, request, cts.Token);
                var response = await Frames.ReadAsync<Response>(_pipe, cts.Token);
                if (response.Id != id)
                    throw new InvalidDataException($"Response id mismatch: expected {id}, got {response.Id}");
                return response;
            }
            finally
            {
                _lane.Release();
            }
        }

        public async Task ShutdownAsync()
        {
            if (_disposed || Process.HasExited)
                return;

            try
            {
                await SendAsync("shutdown", TimeSpan.FromSeconds(3));
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                await TerminateOwnedChildAsync();
            }
        }

        public async Task TerminateOwnedChildAsync()
        {
            if (Process.HasExited)
                return;

            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);
                if (!Process.HasExited)
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { }
            finally
            {
                _lane.Dispose();
                _pipe.Dispose();
                Process.Dispose();
            }
        }

        private static ProcessStartInfo CreateSelfStartInfo(params string[] args)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Environment.ProcessPath unavailable");
            var assemblyPath = Assembly.GetExecutingAssembly().Location;

            var psi = new ProcessStartInfo();
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = processPath;
                psi.ArgumentList.Add(assemblyPath);
            }
            else
            {
                psi.FileName = processPath;
            }

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            return psi;
        }
    }

    private static class Frames
    {
        public static void Write<T>(Stream stream, T value)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            if (payload.Length is <= 0 or > MaxFrameBytes)
                throw new InvalidDataException($"Frame size {payload.Length} outside allowed range");

            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            stream.Write(header);
            stream.Write(payload);
            stream.Flush();
        }

        public static T Read<T>(Stream stream)
        {
            Span<byte> header = stackalloc byte[4];
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > MaxFrameBytes)
                throw new InvalidDataException($"Frame size {length} outside allowed range");

            var payload = new byte[length];
            stream.ReadExactly(payload);

            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new InvalidDataException("Frame JSON decoded to null");
        }

        public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            if (payload.Length is <= 0 or > MaxFrameBytes)
                throw new InvalidDataException($"Frame size {payload.Length} outside allowed range");

            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, cancellationToken);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > MaxFrameBytes)
                throw new InvalidDataException($"Frame size {length} outside allowed range");

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellationToken);

            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new InvalidDataException("Frame JSON decoded to null");
        }
    }

    private sealed record Request(string Id, string Op, string Token);
    private sealed record Response(
        string Id,
        string Op,
        bool Ok,
        long? Sequence,
        string? Error,
        WorkerIdentity? Identity,
        string? TargetState);

    private sealed record WorkerIdentity(
        string TargetId,
        int Pid,
        string StartUtc,
        string Apartment,
        string TokenFingerprint);

    private sealed record Check(string Name, bool Ok, object Evidence);
}
