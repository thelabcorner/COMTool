using System.Diagnostics;
using System.IO.Pipes;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class WorkerProcessStartupTests
{
    [Fact]
    public async Task ChildExitFailsBeforeConnectTimeoutAndCarriesDiagnostic()
    {
        var commandProcessor =
            Environment.GetEnvironmentVariable("ComSpec");
        Assert.False(string.IsNullOrWhiteSpace(commandProcessor));

        var pipeName =
            $"comtool-v2-startup-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = commandProcessor!,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "/d",
                    "/s",
                    "/c",
                    "echo synthetic-worker-startup-error 1>&2 & exit /b 7"
                }
            });
        Assert.NotNull(process);

        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<
            WorkerExitedBeforeHandshakeException>(
            () => WorkerProcessStartup.WaitForConnectionOrExitAsync(
                pipe,
                process!,
                timeout.Token));

        Assert.Equal(7, ex.ExitCode);
        Assert.Contains(
            "synthetic-worker-startup-error",
            ex.Diagnostic);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(3),
            $"Worker exit was not detected promptly: {clock.Elapsed}.");
    }
}