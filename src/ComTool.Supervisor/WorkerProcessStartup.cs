using System.Diagnostics;
using System.IO.Pipes;

namespace ComTool.Supervisor;

internal sealed class WorkerExitedBeforeHandshakeException(
    int exitCode,
    string? diagnostic)
    : Exception(BuildMessage(exitCode, diagnostic))
{
    public int ExitCode { get; } = exitCode;
    public string? Diagnostic { get; } = diagnostic;

    private static string BuildMessage(
        int exitCode,
        string? diagnostic)
    {
        var message =
            $"Worker process exited with code {exitCode} before completing the broker handshake.";
        return string.IsNullOrWhiteSpace(diagnostic)
            ? message
            : $"{message} Worker diagnostic: {diagnostic}";
    }
}

internal static class WorkerProcessStartup
{
    private const int MaxDiagnosticCharacters = 4096;

    public static async Task WaitForConnectionOrExitAsync(
        NamedPipeServerStream pipe,
        Process process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(process);

        var connectionTask =
            pipe.WaitForConnectionAsync(cancellationToken);
        var exitTask =
            process.WaitForExitAsync(cancellationToken);

        var completed = await Task
            .WhenAny(connectionTask, exitTask)
            .ConfigureAwait(false);

        if (ReferenceEquals(completed, exitTask) &&
            process.HasExited)
        {
            await exitTask.ConfigureAwait(false);
            var diagnostic = await ReadDiagnosticAsync(process)
                .ConfigureAwait(false);
            throw new WorkerExitedBeforeHandshakeException(
                process.ExitCode,
                diagnostic);
        }

        await connectionTask.ConfigureAwait(false);
    }

    private static async Task<string?> ReadDiagnosticAsync(
        Process process)
    {
        if (!process.StartInfo.RedirectStandardError)
            return null;

        var diagnostic = (await process.StandardError
                .ReadToEndAsync()
                .ConfigureAwait(false))
            .Trim();

        if (diagnostic.Length == 0)
            return null;

        return diagnostic.Length <= MaxDiagnosticCharacters
            ? diagnostic
            : diagnostic[..MaxDiagnosticCharacters] + "…";
    }
}