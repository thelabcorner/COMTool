using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class HostTerminationAuthorityTests : IDisposable
{
    private readonly string _stateRoot = Path.Combine(
        Path.GetTempPath(),
        "comtool-v2-host-termination-authority-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateRoot))
            Directory.Delete(_stateRoot, recursive: true);
    }

    [Fact]
    public async Task UnownedGenerationIsRefusedBeforeProcessProbe()
    {
        var identity = Identity();
        var probe = new FakeProbe(ProcessIdentity(identity));
        var authority = new HostTerminationAuthority(
            new HostOwnershipLedger(_stateRoot),
            probe);

        var error =
            await Assert.ThrowsAsync<HostTerminationAuthorityException>(
                async () => await authority.RequireOwnedGenerationAsync(
                    Descriptor(identity)));

        Assert.Equal("host_termination_not_owned", error.Kind);
        Assert.False(error.ReconciliationRequired);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task OwnedGenerationRequiresMatchingOsIdentity()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        var owned = RecordOwned(ledger, identity);
        var probe = new FakeProbe(ProcessIdentity(identity));
        var authority = new HostTerminationAuthority(ledger, probe);

        var authorized = await authority.RequireOwnedGenerationAsync(
            Descriptor(identity));

        Assert.Equal(owned.TargetId, authorized.TargetId);
        Assert.Equal(HostOwnershipRecordState.Owned, authorized.State);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task InactiveOwnershipCannotAuthorizeTermination()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        _ = RecordOwned(ledger, identity);
        _ = ledger.MarkState(
            identity.TargetId,
            HostOwnershipRecordState.Released,
            DateTimeOffset.UtcNow,
            "released by test");
        var probe = new FakeProbe(ProcessIdentity(identity));
        var authority = new HostTerminationAuthority(ledger, probe);

        var error =
            await Assert.ThrowsAsync<HostTerminationAuthorityException>(
                async () => await authority.RequireOwnedGenerationAsync(
                    Descriptor(identity)));

        Assert.Equal(
            "host_termination_ownership_inactive",
            error.Kind);
        Assert.False(error.ReconciliationRequired);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task GenerationMismatchRequiresReconciliation()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        _ = RecordOwned(ledger, identity);
        var probe = new FakeProbe(
            ProcessIdentity(identity) with
            {
                ExecutablePath = @"C:\Other\Illustrator.exe"
            });
        var authority = new HostTerminationAuthority(ledger, probe);

        var error =
            await Assert.ThrowsAsync<HostTerminationAuthorityException>(
                async () => await authority.RequireOwnedGenerationAsync(
                    Descriptor(identity)));

        Assert.Equal(
            "host_termination_generation_unproven",
            error.Kind);
        Assert.True(error.ReconciliationRequired);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task UnreadableOwnedGenerationRemainsOwnedAndCannotAuthorizeTermination()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);
        var owned = RecordOwned(ledger, identity);
        var probe = new FakeProbe(null);
        var authority = new HostTerminationAuthority(ledger, probe);

        var error =
            await Assert.ThrowsAsync<HostTerminationAuthorityException>(
                async () => await authority.RequireOwnedGenerationAsync(
                    Descriptor(identity)));

        Assert.Equal(
            "host_termination_generation_unproven",
            error.Kind);
        Assert.True(error.ReconciliationRequired);
        Assert.Equal(1, probe.Calls);

        var persisted = ledger.TryGetGeneration(identity.TargetId);
        Assert.NotNull(persisted);
        Assert.Equal(owned.TargetId, persisted.TargetId);
        Assert.Equal(
            HostOwnershipRecordState.Owned,
            persisted.State);
        Assert.Equal(owned.UpdatedAt, persisted.UpdatedAt);
        Assert.Equal(owned.Note, persisted.Note);
    }

    [Fact]
    public void OwnershipStateTransitionCannotInventOwnership()
    {
        var identity = Identity();
        var ledger = new HostOwnershipLedger(_stateRoot);

        var error = Assert.Throws<HostOwnershipLedgerException>(
            () => ledger.MarkState(
                identity.TargetId,
                HostOwnershipRecordState.Quitted,
                DateTimeOffset.UtcNow,
                "must not invent provenance"));

        Assert.Equal("host_ownership_not_found", error.Kind);
    }

    private static HostOwnershipRecord RecordOwned(
        HostOwnershipLedger ledger,
        HostTargetIdentity identity) =>
        ledger.RecordLaunched(
            identity,
            progId: "Illustrator.Application",
            launchSpecKey: "spec-key",
            runtimeId: "runtime-test",
            launchRequestId: "launch-test",
            launchedAt: DateTimeOffset.UtcNow);

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = "illustrator",
            ProcessId = 4242,
            ProcessStartedAt = new DateTimeOffset(
                2026,
                9,
                27,
                8,
                0,
                0,
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

    private static HostProcessIdentity ProcessIdentity(
        HostTargetIdentity identity) =>
        new(
            identity.ProcessId,
            identity.ProcessStartedAt,
            identity.ExecutablePath);

    private sealed class FakeProbe(HostProcessIdentity? identity)
        : IHostProcessIdentityProbe
    {
        public int Calls { get; private set; }

        public ValueTask<HostProcessIdentity?> TryReadAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(identity);
        }
    }
}