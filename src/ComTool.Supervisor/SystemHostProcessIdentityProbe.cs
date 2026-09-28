using System.Diagnostics;

namespace ComTool.Supervisor;

/// <summary>
/// Reads strong process generation identity from the operating system. It
/// only ever reads; it never starts or stops a host.
/// </summary>
public sealed class SystemHostProcessIdentityProbe : IHostProcessIdentityProbe
{
    public ValueTask<HostProcessIdentity?> TryReadAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (processId <= 0)
            return ValueTask.FromResult<HostProcessIdentity?>(null);

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();

            if (process.HasExited)
                return ValueTask.FromResult<HostProcessIdentity?>(null);

            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return ValueTask.FromResult<HostProcessIdentity?>(null);

            return ValueTask.FromResult<HostProcessIdentity?>(
                new HostProcessIdentity(
                    process.Id,
                    new DateTimeOffset(process.StartTime),
                    executablePath));
        }
        catch (ArgumentException)
        {
            // No such process id.
            return ValueTask.FromResult<HostProcessIdentity?>(null);
        }
        catch (InvalidOperationException)
        {
            // Exited between refresh and read.
            return ValueTask.FromResult<HostProcessIdentity?>(null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Identity is not readable (protected or cross-session process).
            // Unreadable is unproven, never absent.
            return ValueTask.FromResult<HostProcessIdentity?>(null);
        }
    }
}
