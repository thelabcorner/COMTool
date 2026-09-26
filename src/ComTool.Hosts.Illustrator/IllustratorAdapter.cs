using System.Diagnostics;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

public sealed class IllustratorAdapter : IHostAdapter
{
    public const string HostName = "illustrator";
    public const string CurrentAdapterVersion = "0.1.0-dev";

    public string Host => HostName;
    public string AdapterVersion => CurrentAdapterVersion;

    public ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var processes = Process.GetProcessesByName("Illustrator");
        try
        {
            if (processes.Length == 0)
                return ValueTask.FromResult<IReadOnlyList<HostTargetDescriptor>>([]);

            if (processes.Length != 1)
            {
                throw new HostAdapterException(
                    "ambiguous_host_instances",
                    $"Found {processes.Length} Illustrator processes; the current ROT adapter cannot safely map a specific process to Illustrator.Application.",
                    retryable: false,
                    ExecutionState.NotStarted);
            }

            var process = processes[0];
            process.Refresh();

            string executablePath;
            DateTimeOffset startedAt;
            try
            {
                executablePath = process.MainModule?.FileName
                    ?? throw new InvalidOperationException("Illustrator executable path unavailable.");
                startedAt = new DateTimeOffset(process.StartTime);
            }
            catch (Exception ex)
            {
                throw new HostAdapterException(
                    "target_identity_unavailable",
                    $"Could not establish Illustrator process identity: {ex.Message}",
                    retryable: true,
                    ExecutionState.NotStarted,
                    innerException: ex);
            }

            object? appObject = null;
            try
            {
                appObject = IllustratorComInterop.AttachActive();
                dynamic app = appObject;

                var version = IllustratorComInterop.RetryRead(
                    () => Convert.ToString(app.Version) ?? string.Empty);

                var identity = new HostTargetIdentity
                {
                    Host = HostName,
                    ProcessId = process.Id,
                    ProcessStartedAt = startedAt,
                    ExecutablePath = executablePath,
                    HostVersion = version,
                    AdapterVersion = CurrentAdapterVersion,
                    EndpointIdentity = IllustratorComInterop.ProgId
                };

                var descriptor = new HostTargetDescriptor
                {
                    Identity = identity,
                    Target = new TargetRef(
                        HostName,
                        identity.TargetId,
                        Generation: 0),
                    Capabilities = IllustratorOperations.Capabilities,
                    Running = true
                };

                return ValueTask.FromResult<IReadOnlyList<HostTargetDescriptor>>([descriptor]);
            }
            finally
            {
                IllustratorComInterop.Release(appObject);
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public ValueTask<IHostSession> ConnectAsync(
        HostTargetDescriptor target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(target.Identity.Host, HostName, StringComparison.Ordinal))
            throw new ArgumentException("Target is not an Illustrator target.", nameof(target));

        var process = Process.GetProcessById(target.Identity.ProcessId);
        using (process)
        {
            process.Refresh();
            var currentStartedAt = new DateTimeOffset(process.StartTime);
            if (currentStartedAt != target.Identity.ProcessStartedAt)
            {
                throw new HostAdapterException(
                    "target_generation_changed",
                    "Illustrator PID was reused or the host restarted.",
                    retryable: true,
                    ExecutionState.NotStarted);
            }
        }

        var appObject = IllustratorComInterop.AttachActive();
        try
        {
            dynamic app = appObject;
            var version = IllustratorComInterop.RetryRead(
                () => Convert.ToString(app.Version) ?? string.Empty);

            if (!string.Equals(version, target.Identity.HostVersion, StringComparison.Ordinal))
            {
                throw new HostAdapterException(
                    "target_version_changed",
                    $"Expected Illustrator {target.Identity.HostVersion}, attached to {version}.",
                    retryable: true,
                    ExecutionState.NotStarted);
            }

            return ValueTask.FromResult<IHostSession>(
                new IllustratorSession(target.Identity, appObject));
        }
        catch
        {
            IllustratorComInterop.Release(appObject);
            throw;
        }
    }
}
