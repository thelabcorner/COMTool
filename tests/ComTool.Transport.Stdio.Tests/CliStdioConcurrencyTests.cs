using System.Diagnostics;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Ipc;

namespace ComTool.Transport.Stdio.Tests;

public sealed class CliStdioConcurrencyTests
{
    [Theory]
    [InlineData("--broker", null)]
    [InlineData("--worker", "never-used-worker.exe")]
    public async Task CliRefusesLegacyParallelControlPlaneOptions(
        string option,
        string? value)
    {
        var cliDll = Path.Combine(
            AppContext.BaseDirectory,
            "ComTool.Cli.dll");
        Assert.True(
            File.Exists(cliDll),
            $"Expected CLI project output at '{cliDll}'.");

        var dotnetHost =
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
            dotnetHost = "dotnet";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnetHost,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add(cliDll);
        process.StartInfo.ArgumentList.Add("targets");
        process.StartInfo.ArgumentList.Add(option);
        if (value is not null)
            process.StartInfo.ArgumentList.Add(value);

        Assert.True(process.Start());
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process
            .WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        var stdout = await stdoutTask;
        _ = await stderrTask;

        Assert.NotEqual(0, process.ExitCode);
        using var payload = JsonDocument.Parse(stdout);
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "Direct/broker CLI control planes were removed",
            payload.RootElement
                .GetProperty("error")
                .GetProperty("message")
                .GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlRequestBypassesBlockedRequestThroughRealCliProxy()
    {
        var pipeName =
            $"comtool-v2-cli-concurrency-{Guid.NewGuid():N}";
        using var serverCts =
            new CancellationTokenSource();

        var slowEntered =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new RuntimePipeServer(
            async (request, cancellationToken) =>
            {
                if (string.Equals(
                        request.Id,
                        "blocked-script",
                        StringComparison.Ordinal))
                {
                    slowEntered.TrySetResult();
                    await releaseSlow.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return new OperationResult
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = request.Id,
                    Operation = request.Operation,
                    Ok = true,
                    Status = OperationStatus.Completed,
                    TargetState = TargetState.Known,
                    Result = ProtocolValue.FromString(request.Id)
                };
            },
            pipeName);

        var serverTask =
            server.RunAsync(serverCts.Token);

        var cliDll =
            Path.Combine(
                AppContext.BaseDirectory,
                "ComTool.Cli.dll");

        Assert.True(
            File.Exists(cliDll),
            $"Expected CLI project output at '{cliDll}'.");

        var dotnetHost =
            Environment.GetEnvironmentVariable(
                "DOTNET_HOST_PATH");

        if (string.IsNullOrWhiteSpace(dotnetHost))
            dotnetHost = "dotnet";

        using var process =
            new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = dotnetHost,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

        process.StartInfo.ArgumentList.Add(cliDll);
        process.StartInfo.ArgumentList.Add("stdio");
        process.StartInfo.ArgumentList.Add("--pipe");
        process.StartInfo.ArgumentList.Add(pipeName);

        Assert.True(process.Start());

        try
        {
            await process.StandardInput.WriteLineAsync(
                RequestLine(
                    "blocked-script",
                    "script.runFile"));
            await process.StandardInput.FlushAsync();

            await slowEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            await process.StandardInput.WriteLineAsync(
                RequestLine(
                    "break-glass",
                    "core.target.host.terminate"));
            await process.StandardInput.FlushAsync();

            // This response must be observable while the first request remains
            // blocked. The legacy stdio proxy could not read/dispatch the
            // second frame until the first runtime-pipe call returned.
            var firstLine = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(firstLine);
            using (var first =
                   JsonDocument.Parse(firstLine))
            {
                Assert.Equal(
                    "break-glass",
                    first.RootElement
                        .GetProperty("id")
                        .GetString());
            }

            Assert.False(
                releaseSlow.Task.IsCompleted);

            releaseSlow.TrySetResult();

            var secondLine = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(secondLine);
            using (var second =
                   JsonDocument.Parse(secondLine))
            {
                Assert.Equal(
                    "blocked-script",
                    second.RootElement
                        .GetProperty("id")
                        .GetString());
            }

            process.StandardInput.Close();

            await process
                .WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            if (process.ExitCode != 0)
            {
                var stderr =
                    await process.StandardError.ReadToEndAsync();
                Assert.Fail(
                    $"CLI stdio proxy exited {process.ExitCode}: {stderr}");
            }
        }
        finally
        {
            releaseSlow.TrySetResult();

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // Best effort cleanup.
            }

            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            serverCts.Cancel();
            await serverTask;
        }
    }

    private static string RequestLine(
        string id,
        string operation) =>
        JsonSerializer.Serialize(
            new
            {
                protocolVersion = 1,
                id,
                operation,
                input = (object?)null
            });
}
