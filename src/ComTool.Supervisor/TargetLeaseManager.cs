using System.Security.Cryptography;
using System.Text;

namespace ComTool.Supervisor;

public sealed record TargetLeaseGrant(
    string LeaseId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt)
{
    public double TtlMs =>
        (ExpiresAt - AcquiredAt).TotalMilliseconds;
}

public sealed record TargetLeaseStatus(
    bool Held,
    DateTimeOffset? AcquiredAt,
    DateTimeOffset? ExpiresAt);

public sealed class TargetLeaseException(
    string kind,
    string message,
    bool retryable,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
    public bool Retryable { get; } = retryable;
}

internal sealed class TargetLeaseManager
{
    public const int DefaultTtlMs = 30_000;
    public const int MinTtlMs = 1_000;
    public const int MaxTtlMs = 300_000;

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private ActiveLease? _active;

    public TargetLeaseManager(
        Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TargetLeaseStatus Status
    {
        get
        {
            lock (_gate)
            {
                ExpireIfNeeded(_clock());
                return StatusCore();
            }
        }
    }

    public TargetLeaseGrant Acquire(int? ttlMs = null)
    {
        var ttl = ValidateTtl(ttlMs);
        var now = _clock();

        lock (_gate)
        {
            ExpireIfNeeded(now);

            if (_active is not null)
            {
                throw new TargetLeaseException(
                    "target_leased",
                    $"Target is already leased until {_active.ExpiresAt:O}.",
                    retryable: true);
            }

            var grant = new TargetLeaseGrant(
                Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(32)),
                now,
                now.AddMilliseconds(ttl));

            _active = new ActiveLease(
                grant.LeaseId,
                grant.AcquiredAt,
                grant.ExpiresAt);

            return grant;
        }
    }

    public TargetLeaseGrant Renew(
        string leaseId,
        int? ttlMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        var ttl = ValidateTtl(ttlMs);
        var now = _clock();

        lock (_gate)
        {
            ExpireIfNeeded(now);
            var active = RequireActiveLease();

            if (!TokenEquals(active.LeaseId, leaseId))
                throw LeaseMismatch();

            var grant = new TargetLeaseGrant(
                active.LeaseId,
                active.AcquiredAt,
                now.AddMilliseconds(ttl));

            _active = new ActiveLease(
                grant.LeaseId,
                grant.AcquiredAt,
                grant.ExpiresAt);

            return grant;
        }
    }

    public void Release(string leaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        lock (_gate)
        {
            ExpireIfNeeded(_clock());
            var active = RequireActiveLease();

            if (!TokenEquals(active.LeaseId, leaseId))
                throw LeaseMismatch();

            _active = null;
        }
    }

    public void EnsureAccess(
        string? leaseId,
        bool requiresLease)
    {
        lock (_gate)
        {
            ExpireIfNeeded(_clock());

            if (_active is null)
            {
                if (requiresLease)
                {
                    throw new TargetLeaseException(
                        "lease_required",
                        "This operation requires an active target lease.",
                        retryable: false);
                }

                return;
            }

            if (leaseId is not null &&
                TokenEquals(_active.LeaseId, leaseId))
                return;

            throw new TargetLeaseException(
                "target_leased",
                $"Target is leased until {_active.ExpiresAt:O}.",
                retryable: true);
        }
    }

    public static int ValidateTtl(int? ttlMs)
    {
        var value = ttlMs ?? DefaultTtlMs;

        if (value is < MinTtlMs or > MaxTtlMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttlMs),
                value,
                $"Lease TTL must be between {MinTtlMs} and {MaxTtlMs} ms.");
        }

        return value;
    }

    private ActiveLease RequireActiveLease() =>
        _active
        ?? throw new TargetLeaseException(
            "lease_not_found",
            "No active lease exists for this target.",
            retryable: false);

    private TargetLeaseStatus StatusCore() =>
        _active is null
            ? new TargetLeaseStatus(
                Held: false,
                AcquiredAt: null,
                ExpiresAt: null)
            : new TargetLeaseStatus(
                Held: true,
                _active.AcquiredAt,
                _active.ExpiresAt);

    private void ExpireIfNeeded(DateTimeOffset now)
    {
        if (_active is not null &&
            _active.ExpiresAt <= now)
            _active = null;
    }

    private static TargetLeaseException LeaseMismatch() =>
        new(
            "lease_mismatch",
            "The supplied lease does not own this target.",
            retryable: false);

    private static bool TokenEquals(
        string expected,
        string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expectedBytes,
                   actualBytes);
    }

    private sealed record ActiveLease(
        string LeaseId,
        DateTimeOffset AcquiredAt,
        DateTimeOffset ExpiresAt);
}
