using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ComTool.Protocol;

namespace ComTool.Supervisor;

/// <summary>
/// Time seam for the bounded watch poller. Production runs against the system
/// clock; tests supply a deterministic clock so the whole poller can be driven
/// without a single real sleep.
/// </summary>
internal interface IWatchClock
{
    long GetTimestamp();

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemWatchClock : IWatchClock
{
    public static SystemWatchClock Instance { get; } = new();

    private SystemWatchClock()
    {
    }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        return Task.Delay(delay, cancellationToken);
    }
}

/// <summary>
/// One poll attempt against the watched source. <paramref name="Verified"/> is
/// false when the source operation itself did not produce an observation, which
/// is reported truthfully instead of being coerced into a value.
/// </summary>
internal sealed record WatchSourceSample(
    bool Verified,
    ProtocolValue? Value,
    string? FailureKind,
    string? FailureMessage,
    OperationStatus Status);

/// <summary>
/// A validated watch request. The condition reuses
/// <see cref="OperationCondition"/> verbatim so the runtime catalog stays the
/// single authority on which operations are composable as a watch source.
/// </summary>
internal sealed record WatchPlan(
    OperationCondition Condition,
    string PredicateKind,
    ProtocolValue? Expected,
    TimeSpan PollInterval,
    TimeSpan Timeout,
    TimeSpan PollTimeout,
    int MaxPolls);

internal enum WatchTermination
{
    Satisfied,
    Timeout,
    SourceUnavailable,
    PollBudgetExhausted,
    TargetGenerationChanged
}

internal sealed record WatchOutcome(
    WatchTermination Termination,
    int Polls,
    TimeSpan Waited,
    ProtocolValue? LastValue,
    bool LastVerified,
    string? LastFailureKind,
    string? LastFailureMessage,
    OperationStatus LastStatus)
{
    public bool Satisfied => Termination == WatchTermination.Satisfied;

    public string DescribeTermination() =>
        Termination switch
        {
            WatchTermination.Satisfied => "satisfied",
            WatchTermination.Timeout => "timeout",
            WatchTermination.SourceUnavailable => "source_unavailable",
            WatchTermination.PollBudgetExhausted => "poll_budget_exhausted",
            WatchTermination.TargetGenerationChanged =>
                "target_generation_changed",
            _ => "unknown"
        };
}

/// <summary>
/// Bounded runtime-owned polling/condition primitive. It composes only
/// already-registered read-only operations issued through the canonical
/// dispatcher; it never owns a host adapter, a worker, or a second execution
/// path.
/// </summary>
internal static class WatchConditionPoller
{
    public const string OperationName = "watch.condition";

    // The legacy tool accepted a zero or near-zero interval, which let a watch
    // hammer Illustrator. The floor is a hard policy bound, not a hint.
    public const int MinPollIntervalMs = 100;
    public const int MaxPollIntervalMs = 300_000;
    public const int DefaultPollIntervalMs = 500;

    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 3_600_000;
    public const int DefaultTimeoutMs = 60_000;

    public const int MinPollTimeoutMs = 100;
    public const int MaxPollTimeoutMs = 60_000;
    public const int DefaultPollTimeoutMs = 5_000;

    /// <summary>
    /// Absolute ceiling on poll attempts. The effective bound is normally
    /// derived from timeout/interval so that the budget is never the
    /// constraint a caller actually hits; this is the backstop.
    /// </summary>
    public const int AbsoluteMaxPolls = 65_536;

    /// <summary>
    /// Derive the poll attempt bound from the caller's own deadline so a long
    /// timeout paired with a floored interval still gets every attempt it
    /// legitimately needs.
    /// </summary>
    public static int DeriveMaxPolls(TimeSpan timeout, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            return 1;

        var attempts = (long)Math.Ceiling(
            timeout.TotalMilliseconds / interval.TotalMilliseconds) + 2;

        return (int)Math.Clamp(attempts, 1L, AbsoluteMaxPolls);
    }

    /// <summary>
    /// Deterministic per-poll child request id. The same parent request and
    /// poll index always produce the same id, so a poll is addressable and
    /// never collides with a sibling poll.
    /// </summary>
    public static string CreateChildRequestId(
        string parentRequestId,
        int pollIndex)
    {
        var material = parentRequestId + "\n" +
                       pollIndex.ToString(
                           System.Globalization.CultureInfo.InvariantCulture);

        return "watch-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material))
                    .AsSpan(0, 20))
            .ToLowerInvariant();
    }

    /// <summary>
    /// A watch may only compose registered read-only operations, and may never
    /// compose itself. The recursion guard is explicit so a nested watch
    /// reports a precise reason instead of being masked by the generic
    /// host-scope rejection.
    /// </summary>
    public static void EnsureWatchableSource(string sourceOperation)
    {
        if (string.Equals(
                sourceOperation,
                OperationName,
                StringComparison.Ordinal))
        {
            throw new OperationConditionPolicyException(
                "watch_source_not_watchable",
                $"'{OperationName}' cannot watch itself; select a registered read-only source operation.");
        }
    }

    /// <summary>
    /// Evaluate one observation. <c>changed</c>/<c>unchanged</c> need the first
    /// verified sample, and a first sample can never diverge from itself.
    /// Equality is the same JSON-type-exact comparison the runtime uses for
    /// preconditions, so a boolean never matches a number.
    /// </summary>
    public static bool EvaluatePredicate(
        string kind,
        ProtocolValue? expected,
        ProtocolValue actual,
        ProtocolValue? first,
        bool isFirstVerifiedSample)
    {
        ArgumentNullException.ThrowIfNull(actual);

        if (kind is "changed" or "unchanged")
        {
            if (isFirstVerifiedSample || first is null)
                return false;

            var diverged =
                !OperationConditionEvaluator.ValuesEqual(actual, first);

            return kind == "changed" ? diverged : !diverged;
        }

        return OperationConditionEvaluator.EvaluatePredicate(
            new OperationConditionPredicate
            {
                Kind = kind,
                Expected = expected
            },
            actual);
    }

    /// <summary>
    /// Poll <paramref name="sample"/> until the condition holds, the deadline
    /// passes, the pinned target generation is lost, or the poll budget is
    /// exhausted. The per-poll provider budget is always clamped to the
    /// remaining watch deadline, so a blocked provider cannot outlive the
    /// watch. Nothing is held across polls: each attempt is an independent
    /// child request that is awaited to completion before the next one starts.
    /// </summary>
    public static async Task<WatchOutcome> RunAsync(
        WatchPlan plan,
        IWatchClock clock,
        Func<int, TimeSpan, CancellationToken, Task<WatchSourceSample>>
            sample,
        Func<bool> targetStillCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(targetStillCurrent);

        var started = clock.GetTimestamp();
        var polls = 0;
        var verifiedAny = false;
        ProtocolValue? first = null;
        var hasFirst = false;
        WatchSourceSample? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!targetStillCurrent())
            {
                return Terminal(
                    WatchTermination.TargetGenerationChanged,
                    polls,
                    Elapsed(clock, started),
                    last,
                    verifiedAny);
            }

            if (polls > 0 && Elapsed(clock, started) >= plan.Timeout)
                return Terminal(TimeoutOrUnavailable(verifiedAny), polls, Elapsed(clock, started), last, verifiedAny);

            if (polls >= plan.MaxPolls)
            {
                return Terminal(
                    WatchTermination.PollBudgetExhausted,
                    polls,
                    Elapsed(clock, started),
                    last,
                    verifiedAny);
            }

            // A provider budget can never exceed the remaining watch deadline.
            var remaining = Remaining(clock, started, plan.Timeout);
            var budget = plan.PollTimeout < remaining
                ? plan.PollTimeout
                : remaining;

            var observation = await sample(
                    polls + 1,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);

            polls++;
            last = observation;

            if (observation.Verified && observation.Value is { } value)
            {
                verifiedAny = true;

                var isFirstVerifiedSample = !hasFirst;
                if (isFirstVerifiedSample)
                {
                    // A null first observation is legitimate and must be kept
                    // as the baseline; it can never double as "no sample yet".
                    first = value;
                    hasFirst = true;
                }

                if (EvaluatePredicate(
                        plan.PredicateKind,
                        plan.Expected,
                        value,
                        first,
                        isFirstVerifiedSample))
                {
                    return new WatchOutcome(
                        WatchTermination.Satisfied,
                        polls,
                        Elapsed(clock, started),
                        value,
                        true,
                        null,
                        null,
                        observation.Status);
                }
            }

            // A source that could not be observed is not a matched condition.
            // Polling continues within budget so a transient host_busy can
            // recover, and the terminal error still reports the last failure.
            var waited = Elapsed(clock, started);
            if (waited >= plan.Timeout)
                return Terminal(TimeoutOrUnavailable(verifiedAny), polls, waited, last, verifiedAny);

            var sleepRemaining = Remaining(clock, started, plan.Timeout);
            var delay = plan.PollInterval < sleepRemaining
                ? plan.PollInterval
                : sleepRemaining;

            await clock.DelayAsync(delay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static WatchTermination TimeoutOrUnavailable(bool verifiedAny) =>
        verifiedAny
            ? WatchTermination.Timeout
            : WatchTermination.SourceUnavailable;

    private static WatchOutcome Terminal(
        WatchTermination termination,
        int polls,
        TimeSpan waited,
        WatchSourceSample? last,
        bool verifiedAny)
    {
        // Never claim a timeout when nothing was ever observed: the honest
        // reason is the failing source, and the caller must see which one.
        if (termination == WatchTermination.Timeout && !verifiedAny)
            termination = WatchTermination.SourceUnavailable;

        return new WatchOutcome(
            termination,
            polls,
            waited,
            last?.Value,
            last?.Verified ?? false,
            last?.FailureKind,
            last?.FailureMessage,
            last?.Status ?? OperationStatus.Completed);
    }

    private static TimeSpan Elapsed(IWatchClock clock, long started) =>
        Stopwatch.GetElapsedTime(started, clock.GetTimestamp());

    private static TimeSpan Remaining(
        IWatchClock clock,
        long started,
        TimeSpan timeout)
    {
        var remaining = timeout - Elapsed(clock, started);
        return remaining > TimeSpan.Zero
            ? remaining
            : TimeSpan.Zero;
    }
}
