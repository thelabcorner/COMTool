using System.Diagnostics;
using System.Runtime.InteropServices;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

public sealed class IllustratorAdapter : IHostAdapter, IHostLaunchAdapter
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
                var version = Convert.ToString(
                    IllustratorComInterop.ReadProperty(appObject, "Version"))
                    ?? string.Empty;

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

    public async ValueTask<HostLaunchObservation> LaunchAsync(
        HostLaunchSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = HostLaunchSpecValidator.Validate(
            spec,
            [HostName]);
        if (validation is not null)
        {
            throw new HostLaunchException(
                validation.Kind,
                validation.Message,
                retryable: false,
                ExecutionState.NotStarted);
        }

        if (!string.Equals(
                spec.Host,
                HostName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HostLaunchException(
                "host_family_mismatch",
                $"Illustrator adapter cannot launch host family '{spec.Host}'.",
                retryable: false,
                ExecutionState.NotStarted);
        }

        if (!string.Equals(
                spec.ProgId,
                IllustratorComInterop.ProgId,
                StringComparison.Ordinal))
        {
            throw new HostLaunchException(
                "unsupported_launch_progid",
                $"Illustrator launch requires explicit ProgID " +
                $"'{IllustratorComInterop.ProgId}'.",
                retryable: false,
                ExecutionState.NotStarted);
        }

        if (spec.Arguments.Count != 0)
        {
            throw new HostLaunchException(
                "launch_arguments_unsupported",
                "Illustrator COM activation cannot faithfully apply process " +
                "arguments; launch requires an empty arguments array.",
                retryable: false,
                ExecutionState.NotStarted);
        }

        var preexistingProcesses =
            Process.GetProcessesByName("Illustrator");
        try
        {
            if (preexistingProcesses.Length != 0)
            {
                if (spec.ExistingInstance ==
                    HostLaunchExistingInstancePolicy.Fail)
                {
                    throw new HostLaunchException(
                        "host_already_running",
                        $"Found {preexistingProcesses.Length} running " +
                        "Illustrator process generation(s); launch policy is fail.",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                IReadOnlyList<HostTargetDescriptor> existing;
                try
                {
                    existing = await DiscoverAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new HostLaunchException(
                        "preexisting_target_unavailable",
                        "A preexisting Illustrator process exists but its strong " +
                        $"target identity could not be established: {ex.Message}",
                        retryable: false,
                        ExecutionState.NotStarted,
                        ex.HResult,
                        ex);
                }

                if (existing.Count != 1)
                {
                    throw new HostLaunchException(
                        "preexisting_target_ambiguous",
                        $"Expected one preexisting Illustrator target, found " +
                        $"{existing.Count}.",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                var existingIdentity = existing[0].Identity;
                if (spec.ExpectedHostVersion is { } expected &&
                    !string.Equals(
                        expected,
                        existingIdentity.HostVersion,
                        StringComparison.Ordinal))
                {
                    throw new HostLaunchException(
                        "host_version_mismatch",
                        $"Expected Illustrator {expected}, found " +
                        $"{existingIdentity.HostVersion}.",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                return new HostLaunchObservation
                {
                    Host = HostName,
                    ProgId = IllustratorComInterop.ProgId,
                    Ownership =
                        HostLaunchOwnership.PreexistingWithoutOwnership,
                    Identity = existingIdentity,
                    ObservedAt = DateTimeOffset.UtcNow,
                    OpenDocumentCount = -1
                };
            }
        }
        finally
        {
            foreach (var process in preexistingProcesses)
                process.Dispose();
        }

        var activationStartedAt = DateTimeOffset.UtcNow;
        object? activated = null;
        try
        {
            try
            {
                activated = IllustratorComInterop.Activate(spec.ProgId);
            }
            catch (COMException ex)
            {
                throw new HostLaunchException(
                    "host_activation_failed",
                    $"Illustrator COM activation failed: {ex.Message}",
                    retryable: false,
                    ExecutionState.Ambiguous,
                    ex.HResult,
                    ex);
            }
            catch (Exception ex)
            {
                throw new HostLaunchException(
                    "host_activation_failed",
                    $"Illustrator COM activation failed: {ex.Message}",
                    retryable: false,
                    ExecutionState.Ambiguous,
                    ex.HResult,
                    ex);
            }

            var deadline =
                activationStartedAt +
                TimeSpan.FromMilliseconds(spec.LaunchTimeoutMs);
            HostTargetDescriptor? discovered = null;
            string? lastDiscoveryError = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var targets = await DiscoverAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (targets.Count == 1)
                    {
                        discovered = targets[0];
                        break;
                    }

                    if (targets.Count > 1)
                    {
                        return new HostLaunchObservation
                        {
                            Host = HostName,
                            ProgId = spec.ProgId,
                            Ownership = HostLaunchOwnership.Unproven,
                            ObservedAt = DateTimeOffset.UtcNow,
                            ActivationHResult = 0,
                            AmbiguityKind =
                                "launch_multiple_generations_observed",
                            AmbiguityMessage =
                                $"Observed {targets.Count} Illustrator targets " +
                                "after activation; ownership cannot be attributed.",
                            OpenDocumentCount = -1
                        };
                    }
                }
                catch (Exception ex)
                {
                    lastDiscoveryError = ex.Message;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (discovered is null)
            {
                return new HostLaunchObservation
                {
                    Host = HostName,
                    ProgId = spec.ProgId,
                    Ownership = HostLaunchOwnership.Unproven,
                    ObservedAt = DateTimeOffset.UtcNow,
                    ActivationHResult = 0,
                    AmbiguityKind = "launch_timeout",
                    AmbiguityMessage =
                        "COM activation returned, but no single strongly " +
                        "identifiable Illustrator generation became available " +
                        $"within {spec.LaunchTimeoutMs} ms." +
                        (string.IsNullOrWhiteSpace(lastDiscoveryError)
                            ? string.Empty
                            : $" Last discovery error: {lastDiscoveryError}"),
                    OpenDocumentCount = -1
                };
            }

            var identity = discovered.Identity;
            if (spec.ExpectedHostVersion is { } expectedVersion &&
                !string.Equals(
                    expectedVersion,
                    identity.HostVersion,
                    StringComparison.Ordinal))
            {
                return new HostLaunchObservation
                {
                    Host = HostName,
                    ProgId = spec.ProgId,
                    Ownership = HostLaunchOwnership.Unproven,
                    Identity = identity,
                    ObservedAt = DateTimeOffset.UtcNow,
                    ActivationHResult = 0,
                    AmbiguityKind = "host_version_mismatch",
                    AmbiguityMessage =
                        $"Expected Illustrator {expectedVersion}, observed " +
                        $"{identity.HostVersion}.",
                    OpenDocumentCount = -1
                };
            }

            if (identity.ProcessStartedAt.ToUniversalTime() <
                activationStartedAt.ToUniversalTime())
            {
                return new HostLaunchObservation
                {
                    Host = HostName,
                    ProgId = spec.ProgId,
                    Ownership = HostLaunchOwnership.Unproven,
                    Identity = identity,
                    ObservedAt = DateTimeOffset.UtcNow,
                    ActivationHResult = 0,
                    AmbiguityKind = "generation_predates_activation",
                    AmbiguityMessage =
                        "The discovered Illustrator generation predates the " +
                        "activation dispatch, so ownership cannot be attributed.",
                    OpenDocumentCount = -1
                };
            }

            var openDocumentCount = -1;
            try
            {
                dynamic app = activated;
                openDocumentCount = IllustratorComInterop.RetryRead(
                    () => Convert.ToInt32(app.Documents.Count));
            }
            catch
            {
                // Document count is diagnostic only; unknown stays -1.
            }

            return new HostLaunchObservation
            {
                Host = HostName,
                ProgId = spec.ProgId,
                Ownership = HostLaunchOwnership.LaunchedByRuntime,
                Identity = identity,
                ObservedAt = DateTimeOffset.UtcNow,
                ActivationHResult = 0,
                OpenDocumentCount = openDocumentCount
            };
        }
        finally
        {
            IllustratorComInterop.Release(activated);
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
