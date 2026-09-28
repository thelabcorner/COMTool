using System.Diagnostics;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

/// <summary>
/// Deterministic clock for the watch poller. Time only moves when a poll or a
/// wait asks it to, so no watch test ever sleeps.
/// </summary>
internal sealed class FakeWatchClock : IWatchClock
{
    private static readonly double TicksPerMillisecond =
        Stopwatch.Frequency / 1000d;

    private readonly long _origin;
    private long _now;

    public FakeWatchClock()
    {
        _origin = Stopwatch.GetTimestamp();
        _now = _origin;
    }

    /// <summary>Every interval the poller asked to wait, in order.</summary>
    public List<TimeSpan> Waits { get; } = [];

    public long GetTimestamp() => _now;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waits.Add(delay);
        Advance(delay);
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return;

        _now += (long)Math.Ceiling(
            delay.TotalMilliseconds * TicksPerMillisecond);
    }

    public TimeSpan Elapsed =>
        Stopwatch.GetElapsedTime(_origin, _now);
}
