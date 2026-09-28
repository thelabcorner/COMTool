using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class TargetLeaseManagerTests
{
    [Fact]
    public void AcquireCreatesCryptographicSizedLease()
    {
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var leases = new TargetLeaseManager(() => now);

        var grant = leases.Acquire(5_000);

        Assert.Equal(64, grant.LeaseId.Length);
        Assert.Equal(now, grant.AcquiredAt);
        Assert.Equal(now.AddSeconds(5), grant.ExpiresAt);
        Assert.True(leases.Status.Held);
    }

    [Fact]
    public void ActiveLeaseBlocksCallerWithoutTokenEvenForRead()
    {
        var leases = new TargetLeaseManager();
        _ = leases.Acquire();

        var error = Assert.Throws<TargetLeaseException>(
            () => leases.EnsureAccess(
                leaseId: null,
                requiresLease: false));

        Assert.Equal("target_leased", error.Kind);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void MatchingLeaseAllowsAccess()
    {
        var leases = new TargetLeaseManager();
        var grant = leases.Acquire();

        leases.EnsureAccess(
            grant.LeaseId,
            requiresLease: true);

        leases.EnsureAccess(
            grant.LeaseId,
            requiresLease: false);
    }

    [Fact]
    public void OperationRequiringLeaseFailsWhenNoneHeld()
    {
        var leases = new TargetLeaseManager();

        var error = Assert.Throws<TargetLeaseException>(
            () => leases.EnsureAccess(
                leaseId: null,
                requiresLease: true));

        Assert.Equal("lease_required", error.Kind);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void WrongLeaseCannotRenewOrRelease()
    {
        var leases = new TargetLeaseManager();
        _ = leases.Acquire();

        var wrong =
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

        var renew = Assert.Throws<TargetLeaseException>(
            () => leases.Renew(wrong));
        Assert.Equal("lease_mismatch", renew.Kind);

        var release = Assert.Throws<TargetLeaseException>(
            () => leases.Release(wrong));
        Assert.Equal("lease_mismatch", release.Kind);

        Assert.True(leases.Status.Held);
    }

    [Fact]
    public void RenewPreservesLeaseIdentityAndExtendsFromNow()
    {
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var leases = new TargetLeaseManager(() => now);
        var grant = leases.Acquire(5_000);

        now = now.AddSeconds(2);
        var renewed = leases.Renew(
            grant.LeaseId,
            10_000);

        Assert.Equal(grant.LeaseId, renewed.LeaseId);
        Assert.Equal(grant.AcquiredAt, renewed.AcquiredAt);
        Assert.Equal(now.AddSeconds(10), renewed.ExpiresAt);
    }

    [Fact]
    public void RenewAtLeastNeverShortensExistingLease()
    {
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var leases = new TargetLeaseManager(() => now);
        var grant = leases.Acquire(120_000);

        now = now.AddSeconds(2);
        var renewed = leases.RenewAtLeast(
            grant.LeaseId,
            40_000);

        Assert.Equal(grant.LeaseId, renewed.LeaseId);
        Assert.Equal(grant.AcquiredAt, renewed.AcquiredAt);
        Assert.Equal(grant.ExpiresAt, renewed.ExpiresAt);
    }

    [Fact]
    public void RenewAtLeastExtendsLeaseWhenMinimumExceedsRemainingTime()
    {
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var leases = new TargetLeaseManager(() => now);
        var grant = leases.Acquire(30_000);

        now = now.AddSeconds(20);
        var renewed = leases.RenewAtLeast(
            grant.LeaseId,
            40_000);

        Assert.Equal(grant.LeaseId, renewed.LeaseId);
        Assert.Equal(grant.AcquiredAt, renewed.AcquiredAt);
        Assert.Equal(now.AddSeconds(40), renewed.ExpiresAt);
    }

    [Fact]
    public void ExpiredLeaseIsRemovedLazilyAndNoLongerBlocks()
    {
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var leases = new TargetLeaseManager(() => now);
        _ = leases.Acquire(1_000);

        now = now.AddMilliseconds(1_001);

        Assert.False(leases.Status.Held);
        leases.EnsureAccess(
            leaseId: null,
            requiresLease: false);
    }

    [Fact]
    public void ReleaseClearsLease()
    {
        var leases = new TargetLeaseManager();
        var grant = leases.Acquire();

        leases.Release(grant.LeaseId);

        Assert.False(leases.Status.Held);
    }

    [Fact]
    public void MaximumTtlCoversMaximumWatchdogAndRecoveryGrace()
    {
        var leases = new TargetLeaseManager();
        var grant = leases.Acquire(TargetLeaseManager.MaxTtlMs);

        Assert.Equal(
            TargetLeaseManager.MaxTtlMs,
            grant.TtlMs);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(3_720_001)]
    public void InvalidTtlIsRejected(int ttlMs)
    {
        var leases = new TargetLeaseManager();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => leases.Acquire(ttlMs));
    }
}
