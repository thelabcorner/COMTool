using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class HostLaunchRuntimeTests : IDisposable
{
    private readonly string _stateRoot;

    public HostLaunchRuntimeTests()
    {
        _stateRoot = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-host-launch-tests",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateRoot))
            Directory.Delete(_stateRoot, recursive: true);
    }

    [Fact]
    public async Task PreexistingReturnNeverCreatesOwnership()
    {
        var identity = Identity();
        var descriptor = Descriptor(identity);
        var discovery = new FakeDiscovery([descriptor]);
        var executor = new FakeExecutor(
            Observation(
                HostLaunchOwnership.LaunchedByRuntime,
                identity));
        var ledger = new HostOwnershipLedger(_stateRoot);
        var runtime = Runtime(
            discovery,
            executor,
            ledger,
            new FakeProbe(ProcessIdentity(identity)));

        var decision = await runtime.LaunchAsync(
            "launch-preexisting-return",
            Spec(
                HostLaunchExistingInstancePolicy
                    .ReturnPreexistingWithoutOwnership));

        Assert.Equal(
            HostLaunchOwnership.PreexistingWithoutOwnership,
            decision.Observation.Ownership);
        Assert.Null(decision.Ownership);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(
            HostLaunchAttemptOutcome.PreexistingWithoutOwnership,
            decision.Attempt.Outcome);
        Assert.Null(ledger.TryGetGeneration(identity.TargetId));
    }

    [Fact]
    public async Task PreexistingFailRecordsRefusalWithoutDispatch()
    {
        var identity = Identity();
        var discovery = new FakeDiscovery([Descriptor(identity)]);
        var executor = new FakeExecutor(
            Observation(
                HostLaunchOwnership.LaunchedByRuntime,
                identity));
        var ledger = new HostOwnershipLedger(_stateRoot);
        var runtime = Runtime(
            discovery,
            executor,
            ledger,
            new FakeProbe(ProcessIdentity(identity)));

        var error = await Assert.ThrowsAsync<HostLaunchException>(
            async () => await runtime.LaunchAsync(
                "launch-preexisting-fail",
                Spec(HostLaunchExistingInstancePolicy.Fail)));

        Assert.Equal("host_already_running", error.Kind);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
        Assert.Equal(0, executor.Calls);

        var attempt =
            ledger.TryGetAttempt("launch-preexisting-fail");
        Assert.NotNull(attempt);
        Assert.Equal(
            HostLaunchAttemptOutcome.Refused,
            attempt!.Outcome);
        Assert.Null(ledger.TryGetGeneration(identity.TargetId));
    }

    [Fact]
    public async Task MatchingLaunchedGenerationCreatesExactOwnership()
    {
        var identity = Identity();
        var discovery = new FakeDiscovery([]);
        var executor = new FakeExecutor(
            Observation(
                HostLaunchOwnership.LaunchedByRuntime,
                identity));
        var ledger = new HostOwnershipLedger(_stateRoot);
        var runtime = Runtime(
            discovery,
            executor,
            ledger,
            new FakeProbe(ProcessIdentity(identity)));

        var decision = await runtime.LaunchAsync(
            "launch-owned",
            Spec(HostLaunchExistingInstancePolicy.Fail));

        Assert.Equal(1, executor.Calls);
        Assert.Equal(
            HostLaunchOwnership.LaunchedByRuntime,
            decision.Observation.Ownership);
        Assert.NotNull(decision.Ownership);
        Assert.Equal(
            HostOwnershipRecordState.Owned,
            decision.Ownership!.State);
        Assert.Equal(identity.TargetId, decision.Ownership.TargetId);
        Assert.Equal(
            HostLaunchAttemptOutcome.Launched,
            decision.Attempt.Outcome);

        var durable =
            ledger.TryGetGeneration(identity.TargetId);
        Assert.NotNull(durable);
        Assert.Equal(
            "launch-owned",
            durable!.LaunchRequestId);
        Assert.Equal(
            Spec(HostLaunchExistingInstancePolicy.Fail).SpecKey,
            durable.LaunchSpecKey);
    }

    [Fact]
    public async Task GenerationMismatchDowngradesOwnershipToUnproven()
    {
        var identity = Identity();
        var mismatchedProcess = ProcessIdentity(identity) with
        {
            ProcessStartedAt =
                identity.ProcessStartedAt.AddSeconds(1)
        };
        var ledger = new HostOwnershipLedger(_stateRoot);
        var runtime = Runtime(
            new FakeDiscovery([]),
            new FakeExecutor(
                Observation(
                    HostLaunchOwnership.LaunchedByRuntime,
                    identity)),
            ledger,
            new FakeProbe(mismatchedProcess));

        var decision = await runtime.LaunchAsync(
            "launch-generation-mismatch",
            Spec(HostLaunchExistingInstancePolicy.Fail));

        Assert.Equal(
            HostLaunchOwnership.Unproven,
            decision.Observation.Ownership);
        Assert.Equal(
            "launch_generation_unproven",
            decision.Observation.AmbiguityKind);
        Assert.Null(decision.Ownership);
        Assert.Equal(
            HostLaunchAttemptOutcome.Unproven,
            decision.Attempt.Outcome);
        Assert.Null(ledger.TryGetGeneration(identity.TargetId));
    }

    [Fact]
    public async Task AmbiguousExecutorFailureNeverCreatesOwnership()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        var executor = new FakeExecutor(
            new HostLaunchException(
                "launch_transport_lost",
                "Worker response was lost after dispatch.",
                retryable: false,
                ExecutionState.Ambiguous));
        var runtime = Runtime(
            new FakeDiscovery([]),
            executor,
            ledger,
            new FakeProbe(ProcessIdentity(identity)));

        var error = await Assert.ThrowsAsync<HostLaunchException>(
            async () => await runtime.LaunchAsync(
                "launch-ambiguous",
                Spec(HostLaunchExistingInstancePolicy.Fail)));

        Assert.Equal(ExecutionState.Ambiguous, error.Execution);
        Assert.Equal(1, executor.Calls);

        var attempt =
            ledger.TryGetAttempt("launch-ambiguous");
        Assert.NotNull(attempt);
        Assert.Equal(
            HostLaunchAttemptOutcome.Unproven,
            attempt!.Outcome);
        Assert.Empty(ledger.ListGenerations());
    }

    [Fact]
    public async Task RequestIdCannotBeReusedForAnotherLaunch()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        var runtime = Runtime(
            new FakeDiscovery([Descriptor(identity)]),
            new FakeExecutor(
                Observation(
                    HostLaunchOwnership.LaunchedByRuntime,
                    identity)),
            ledger,
            new FakeProbe(ProcessIdentity(identity)));

        _ = await runtime.LaunchAsync(
            "launch-reused",
            Spec(
                HostLaunchExistingInstancePolicy
                    .ReturnPreexistingWithoutOwnership));

        var error = await Assert.ThrowsAsync<HostLaunchException>(
            async () => await runtime.LaunchAsync(
                "launch-reused",
                Spec(
                    HostLaunchExistingInstancePolicy
                        .ReturnPreexistingWithoutOwnership)));

        Assert.Equal("launch_request_id_reused", error.Kind);
        Assert.Single(ledger.ListAttempts());
    }

    [Fact]
    public async Task DistinctRequestIdsCannotCollideInDurableAttemptKeys()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        var runtime = Runtime(
            new FakeDiscovery([Descriptor(identity)]),
            new FakeExecutor(
                Observation(
                    HostLaunchOwnership.LaunchedByRuntime,
                    identity)),
            ledger,
            new FakeProbe(ProcessIdentity(identity)));
        var spec = Spec(
            HostLaunchExistingInstancePolicy
                .ReturnPreexistingWithoutOwnership);

        _ = await runtime.LaunchAsync("a/b", spec);
        _ = await runtime.LaunchAsync("a_b", spec);

        var attempts = ledger.ListAttempts();
        Assert.Equal(2, attempts.Count);
        Assert.Contains(attempts, item => item.LaunchRequestId == "a/b");
        Assert.Contains(attempts, item => item.LaunchRequestId == "a_b");
    }

    [Fact]
    public async Task CorruptReplayProvenanceBecomesBoundedLaunchFailure()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        var executor = new FakeExecutor(
            Observation(
                HostLaunchOwnership.LaunchedByRuntime,
                identity));
        var runtime = Runtime(
            new FakeDiscovery([Descriptor(identity)]),
            executor,
            ledger,
            new FakeProbe(ProcessIdentity(identity)));
        var spec = Spec(
            HostLaunchExistingInstancePolicy
                .ReturnPreexistingWithoutOwnership);

        _ = await runtime.LaunchAsync("launch-corrupt-provenance", spec);
        var attemptPath = Assert.Single(
            Directory.GetFiles(ledger.AttemptsRoot, "*.json"));
        File.WriteAllText(attemptPath, "{}");

        var error = await Assert.ThrowsAsync<HostLaunchException>(
            async () => await runtime.LaunchAsync(
                "launch-corrupt-provenance",
                spec));

        Assert.Equal("host_ownership_corrupt", error.Kind);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
        Assert.Equal(0, executor.Calls);
    }

    private HostLaunchRuntime Runtime(
        IHostTargetDiscovery discovery,
        IHostLaunchExecutor executor,
        HostOwnershipLedger ledger,
        IHostProcessIdentityProbe probe) =>
        new(
            ["illustrator"],
            discovery,
            host => string.Equals(
                    host,
                    executor.Host,
                    StringComparison.OrdinalIgnoreCase)
                ? executor
                : null,
            ledger,
            probe,
            runtimeId: "runtime-test-generation",
            clock: () =>
                new DateTimeOffset(
                    2026,
                    9,
                    27,
                    8,
                    0,
                    0,
                    TimeSpan.Zero));

    private static HostLaunchSpec Spec(
        HostLaunchExistingInstancePolicy policy) =>
        new()
        {
            Host = "illustrator",
            ProgId = "Illustrator.Application",
            ExpectedHostVersion = "30.6.0",
            ExistingInstance = policy,
            LaunchTimeoutMs = 10_000,
            Arguments = []
        };

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = "illustrator",
            ProcessId = 4242,
            ProcessStartedAt =
                new DateTimeOffset(
                    2026,
                    9,
                    27,
                    7,
                    59,
                    58,
                    TimeSpan.Zero),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "0.1.0-dev",
            EndpointIdentity = "Illustrator.Application"
        };

    private static HostTargetDescriptor Descriptor(
        HostTargetIdentity identity) =>
        new()
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Capabilities = [],
            Running = true
        };

    private static HostLaunchObservation Observation(
        HostLaunchOwnership ownership,
        HostTargetIdentity? identity) =>
        new()
        {
            Host = "illustrator",
            ProgId = "Illustrator.Application",
            Ownership = ownership,
            Identity = identity,
            ObservedAt =
                new DateTimeOffset(
                    2026,
                    9,
                    27,
                    8,
                    0,
                    1,
                    TimeSpan.Zero),
            ActivationHResult = 0,
            OpenDocumentCount = 0
        };

    private static HostProcessIdentity ProcessIdentity(
        HostTargetIdentity identity) =>
        new(
            identity.ProcessId,
            identity.ProcessStartedAt,
            identity.ExecutablePath);

    private sealed class FakeDiscovery(
        IReadOnlyList<HostTargetDescriptor> targets)
        : IHostTargetDiscovery
    {
        public ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(targets);
        }
    }

    private sealed class FakeExecutor : IHostLaunchExecutor
    {
        private readonly HostLaunchObservation? _observation;
        private readonly HostLaunchException? _error;

        public FakeExecutor(HostLaunchObservation observation)
        {
            _observation = observation;
        }

        public FakeExecutor(HostLaunchException error)
        {
            _error = error;
        }

        public string Host => "illustrator";
        public int Calls { get; private set; }

        public ValueTask<HostLaunchObservation> LaunchAsync(
            HostLaunchSpec spec,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;

            if (_error is not null)
                throw _error;

            return ValueTask.FromResult(_observation!);
        }
    }

    private sealed class FakeProbe(HostProcessIdentity? identity)
        : IHostProcessIdentityProbe
    {
        public ValueTask<HostProcessIdentity?> TryReadAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(identity);
        }
    }
}
