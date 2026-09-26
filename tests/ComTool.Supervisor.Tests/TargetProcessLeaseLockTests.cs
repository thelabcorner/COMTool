using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Supervisor.Tests;

public sealed class TargetProcessLeaseLockTests : IDisposable
{
    private readonly string _root =
        Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-target-lock-tests",
            Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProcessLockExcludesSecondOwnerAndReleasesCleanly()
    {
        Directory.CreateDirectory(_root);

        using (var first = TargetProcessLeaseLock.Acquire(
                   "illustrator:test-target",
                   _root))
        {
            var conflict = Assert.Throws<TargetLeaseException>(
                () => TargetProcessLeaseLock.Acquire(
                    "illustrator:test-target",
                    _root));

            Assert.Equal(
                "target_leased_external",
                conflict.Kind);
            Assert.True(conflict.Retryable);
            Assert.True(File.Exists(first.Path));
        }

        using var second = TargetProcessLeaseLock.Acquire(
            "illustrator:test-target",
            _root);

        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task SupervisorsCannotHoldConcurrentLeasesForSameTarget()
    {
        Directory.CreateDirectory(_root);

        var descriptor = Descriptor();

        await using var first = CreateSupervisor(
            descriptor,
            _root);
        await using var second = CreateSupervisor(
            descriptor,
            _root);

        var firstLease = await first.AcquireLeaseAsync(5_000);

        var conflict = await Assert.ThrowsAsync<TargetLeaseException>(
            () => second.AcquireLeaseAsync(5_000));

        Assert.Equal(
            "target_leased_external",
            conflict.Kind);

        await first.ReleaseLeaseAsync(
            firstLease.LeaseId);

        var secondLease =
            await second.AcquireLeaseAsync(5_000);

        Assert.True(second.LeaseStatus.Held);

        await second.ReleaseLeaseAsync(
            secondLease.LeaseId);
    }

    [Fact]
    public async Task LeaseExpiryReleasesProcessLockWithoutFurtherOwnerTraffic()
    {
        Directory.CreateDirectory(_root);

        var descriptor = Descriptor();

        await using var first = CreateSupervisor(
            descriptor,
            _root);
        await using var second = CreateSupervisor(
            descriptor,
            _root);

        _ = await first.AcquireLeaseAsync(
            TargetLeaseManager.MinTtlMs);

        var initialConflict =
            await Assert.ThrowsAsync<TargetLeaseException>(
                () => second.AcquireLeaseAsync(5_000));

        Assert.Equal(
            "target_leased_external",
            initialConflict.Kind);

        // Do not query/renew/release the first supervisor. The timer must
        // release its kernel handle independently when the TTL expires.
        await Task.Delay(
            TargetLeaseManager.MinTtlMs + 750);

        var secondLease =
            await second.AcquireLeaseAsync(5_000);

        Assert.True(second.LeaseStatus.Held);

        await second.ReleaseLeaseAsync(
            secondLease.LeaseId);
    }

    private static TargetSupervisor CreateSupervisor(
        HostTargetDescriptor descriptor,
        string lockDirectory) =>
        new(
            descriptor,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = "unused.exe"
            },
            stateMachine: null,
            mutationLedger: null,
            leaseLockDirectory: lockDirectory);

    private static HostTargetDescriptor Descriptor()
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 4242,
            ProcessStartedAt =
                DateTimeOffset.Parse(
                    "2026-09-24T12:00:00Z"),
            ExecutablePath =
                @"C:\fake\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "test",
            EndpointIdentity =
                "Illustrator.Application"
        };

        return new HostTargetDescriptor
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Capabilities = [],
            Running = true
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(
                    _root,
                    recursive: true);
        }
        catch
        {
        }
    }
}
