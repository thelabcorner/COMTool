using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class TargetSupervisorLeaseTests
{
    [Fact]
    public async Task LeaseAcquisitionWaitsBehindInFlightTargetOperation()
    {
        await using var supervisor = CreateSupervisor();

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = supervisor.WithLeaseAccessAsync(
            leaseId: null,
            requiresLease: false,
            async _ =>
            {
                entered.SetResult();
                await release.Task;
                return 7;
            });

        await entered.Task;

        var acquire = supervisor.AcquireLeaseAsync(5_000);

        Assert.False(acquire.IsCompleted);

        release.SetResult();

        Assert.Equal(7, await operation);
        var grant = await acquire;
        Assert.True(supervisor.LeaseStatus.Held);

        await supervisor.ReleaseLeaseAsync(grant.LeaseId);
    }

    [Fact]
    public async Task HeldLeaseRejectsNonOwnerBeforeActionRuns()
    {
        await using var supervisor = CreateSupervisor();
        var grant = await supervisor.AcquireLeaseAsync();

        var invoked = false;

        var error = await Assert.ThrowsAsync<TargetLeaseException>(
            () => supervisor.WithLeaseAccessAsync(
                leaseId: null,
                requiresLease: false,
                _ =>
                {
                    invoked = true;
                    return Task.FromResult(1);
                }));

        Assert.Equal("target_leased", error.Kind);
        Assert.False(invoked);

        var value = await supervisor.WithLeaseAccessAsync(
            grant.LeaseId,
            requiresLease: true,
            _ => Task.FromResult(9));

        Assert.Equal(9, value);
    }

    private static TargetSupervisor CreateSupervisor()
    {
        var identity = new HostTargetIdentity
        {
            Host = "test",
            ProcessId = 1234,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-24T12:00:00Z"),
            ExecutablePath = @"C:\fake\test.exe",
            HostVersion = "1",
            AdapterVersion = "1",
            EndpointIdentity = "test"
        };

        var descriptor = new HostTargetDescriptor
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Capabilities = Array.Empty<CapabilityDescriptor>(),
            Running = true
        };

        return new TargetSupervisor(
            descriptor,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = "unused.exe"
            });
    }
}
