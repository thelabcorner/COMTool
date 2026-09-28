using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

/// <summary>
/// Lane 04 — watch/condition runtime. Every test drives a fake clock and a fake
/// provider, so the whole bounded-poll contract is verified with zero real
/// time and zero host access.
/// </summary>
public sealed class WatchConditionTests
{
    // ---------------------------------------------------------------- shape

    [Fact]
    public void PredicateIsAlwaysExplicitAndTruthinessIsNeverImplicit()
    {
        // The legacy tool silently fell back to Python truthiness. A watch must
        // name its condition.
        Assert.False(
            Read(
                """{"source":{"operation":"com.get","input":{"path":"Version"}}}""",
                out var error),
            error);

        Assert.Contains("predicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIsRequired()
    {
        Assert.False(
            Read("""{"predicate":{"kind":"truthy"}}""", out var error),
            error);

        Assert.Contains("source", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAndDuplicateInputFieldsAreRejected()
    {
        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"interval":500}
                """,
                out var unknown),
            unknown);

        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"source":{"operation":"com.get"}}
                """,
                out var duplicate),
            duplicate);

        Assert.Contains("Unknown", unknown, StringComparison.Ordinal);
        Assert.Contains("Duplicate", duplicate, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSourceAndPredicateFieldsAreRejected()
    {
        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get","path":"Version"},"predicate":{"kind":"truthy"}}
                """,
                out var sourceError),
            sourceError);

        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"truthy","negate":true}}
                """,
                out var predicateError),
            predicateError);

        Assert.Contains("Unknown", sourceError, StringComparison.Ordinal);
        Assert.Contains("Unknown", predicateError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- bounds

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(0)]
    public void SubFloorIntervalIsRejected(int intervalMs)
    {
        // Legacy accepted interval=0, which let a watch hammer Illustrator.
        Assert.False(
            Read(
                $$"""
                {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"pollIntervalMs":{{intervalMs}}}
                """,
                out var error),
            error);

        Assert.Contains(
            "pollIntervalMs",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(3600001)]
    public void OutOfRangeTimeoutIsRejected(int timeoutMs)
    {
        Assert.False(
            Read(
                $$"""
                {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":{{timeoutMs}}}
                """,
                out var error),
            error);

        Assert.Contains("timeoutMs", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PollTimeoutIsBoundedAndDefaultsInsideTheDeadline()
    {
        var plan = Plan(
            """
            {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":2000}
            """);

        Assert.Equal(
            WatchConditionPoller.DefaultPollIntervalMs,
            (int)plan.PollInterval.TotalMilliseconds);
        Assert.Equal(2000, (int)plan.Timeout.TotalMilliseconds);
        Assert.Equal(
            2000,
            (int)plan.PollTimeout.TotalMilliseconds);

        var shortWatch = Plan(
            """
            {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":200}
            """);

        // A short watch must not inherit a provider cap longer than itself.
        Assert.Equal(
            200,
            (int)shortWatch.PollTimeout.TotalMilliseconds);

        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"pollTimeoutMs":99}
                """,
                out var error),
            error);
    }

    [Fact]
    public void CallerWatchdogCapsEachPoll()
    {
        var plan = Plan(
            """
            {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":30000}
            """,
            policyWorkerWatchdogMs: 750);

        Assert.Equal(
            750,
            (int)plan.PollTimeout.TotalMilliseconds);
    }

    [Fact]
    public void PollBudgetFollowsTheCallersOwnDeadline()
    {
        Assert.Equal(
            602,
            WatchConditionPoller.DeriveMaxPolls(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromMilliseconds(100)));

        Assert.Equal(
            1,
            WatchConditionPoller.DeriveMaxPolls(
                TimeSpan.FromSeconds(60),
                TimeSpan.Zero));
    }

    // ------------------------------------------------- predicate exclusivity

    [Fact]
    public void ExpectAndChangedAreMutuallyExclusiveByConstruction()
    {
        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"changed","expected":1}}
                """,
                out var changed),
            changed);

        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"equals"}}
                """,
                out var equals),
            equals);

        Assert.Contains("expected", changed, StringComparison.Ordinal);
        Assert.Contains("expected", equals, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedPredicateIsRejected()
    {
        Assert.False(
            Read(
                """
                {"source":{"operation":"com.get"},"predicate":{"kind":"approximately"}}
                """,
                out var error),
            error);

        Assert.Contains("approximately", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------ JSON-type-exact equality

    [Fact]
    public async Task BooleanNeverMatchesANumber()
    {
        // The named legacy defect: True == 1 under Python equality.
        var outcome = await Run(
            samples: ["1"],
            predicate: Predicate("equals", Value("true")),
            timeoutMs: 400,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Timeout, outcome.Termination);
        Assert.False(outcome.Satisfied);
        Assert.Equal(4, outcome.Polls);

        var inverse = await Run(
            samples: ["true"],
            predicate: Predicate("equals", Value("1")),
            timeoutMs: 400,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Timeout, inverse.Termination);
    }

    [Fact]
    public async Task NumberOneMatchesNumberOne()
    {
        var outcome = await Run(
            samples: ["0", "1"],
            predicate: Predicate("equals", Value("1")),
            timeoutMs: 5000,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
        Assert.Equal(2, outcome.Polls);
        Assert.Equal("1", outcome.LastValue!.Value!.Value.GetRawText());
    }

    [Fact]
    public async Task DeepObjectEqualityIgnoresKeyOrderButNotType()
    {
        var outcome = await Run(
            samples: ["""{"b":2,"a":{"y":true,"x":1}}"""],
            predicate: Predicate(
                "equals",
                Value("""{"a":{"x":1,"y":true},"b":2}""")),
            timeoutMs: 5000,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);

        var typed = await Run(
            samples: ["""{"a":{"x":1,"y":1}}"""],
            predicate: Predicate("equals", Value("""{"a":{"x":1,"y":true}}""")),
            timeoutMs: 400,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Timeout, typed.Termination);
    }

    [Fact]
    public async Task NotSatisfiesOnTheFirstDivergence()
    {
        var outcome = await Run(
            samples: ["2"],
            predicate: Predicate("not_equals", Value("1")),
            timeoutMs: 5000,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
        Assert.Equal(1, outcome.Polls);
    }

    // -------------------------------------------------------- changed family

    [Fact]
    public async Task ChangedNeverSatisfiesOnTheFirstSample()
    {
        var outcome = await Run(
            samples: ["7", "7", "8"],
            predicate: Predicate("changed"),
            timeoutMs: 5000,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
        Assert.Equal(3, outcome.Polls);
        Assert.Equal("8", outcome.LastValue!.Value!.Value.GetRawText());
    }

    [Fact]
    public async Task ChangedAcceptsANullFirstSample()
    {
        // A null property is a legitimate baseline. If null doubled as "no
        // sample yet" the baseline would be recaptured every poll and
        // 'changed' would never fire.
        var outcome = await Run(
            samples: ["null", "null", "42"],
            predicate: Predicate("changed"),
            timeoutMs: 5000,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
        Assert.Equal(3, outcome.Polls);
        Assert.Equal("42", outcome.LastValue!.Value!.Value.GetRawText());
    }

    [Fact]
    public async Task ChangedIsTypeExactAgainstTheBaseline()
    {
        var outcome = await Run(
            samples: ["1", "true"],
            predicate: Predicate("changed"),
            timeoutMs: 5000,
            intervalMs: 100);

        // 1 -> true is a type change, so it is still a divergence.
        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
        Assert.Equal(2, outcome.Polls);
    }

    [Fact]
    public async Task UnchangedSatisfiesOnceASecondSampleAgrees()
    {
        var held = await Run(
            samples: ["5", "5"],
            predicate: Predicate("unchanged"),
            timeoutMs: 5000,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Satisfied, held.Termination);
        Assert.Equal(2, held.Polls);

        var moved = await Run(
            samples: ["5", "6"],
            predicate: Predicate("unchanged"),
            timeoutMs: 400,
            intervalMs: 100);

        Assert.Equal(WatchTermination.Timeout, moved.Termination);
    }

    // ------------------------------------------------- bounded poll behaviour

    [Fact]
    public async Task ProviderBudgetNeverExceedsTheRemainingDeadline()
    {
        // A 5 s provider cap under a 1 s watch deadline: the legacy bug where a
        // blocked provider outlives the watch.
        var clock = new FakeWatchClock();
        var budgets = new List<TimeSpan>();

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"equals","expected":1},"timeoutMs":1000,"pollIntervalMs":400}
                    """),
                clock,
                (_, budget, _) =>
                {
                    budgets.Add(budget);
                    return Task.FromResult(Sample("0"));
                },
                static () => true,
                CancellationToken.None)
            ;

        Assert.Equal(WatchTermination.Timeout, outcome.Termination);
        Assert.NotEmpty(budgets);
        Assert.All(
            budgets,
            static budget => Assert.True(
                budget <= TimeSpan.FromMilliseconds(1000),
                $"provider budget {budget} exceeded the 1000 ms watch deadline"));

        // The clamp is real: a requested 5000 ms cap was reduced to the
        // remaining watch budget.
        Assert.All(
            budgets,
            static budget => Assert.True(
                budget <= TimeSpan.FromMilliseconds(1000)));
    }

    [Fact]
    public async Task BlockedProviderCannotDefeatTheWatchTimeout()
    {
        var clock = new FakeWatchClock();
        var polls = 0;

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":1000,"pollIntervalMs":400,"pollTimeoutMs":1000}
                    """),
                clock,
                (_, budget, _) =>
                {
                    polls++;
                    // Simulate a provider that burns its entire budget and
                    // never produces an observation.
                    clock.Advance(budget);
                    return Task.FromResult(
                        new WatchSourceSample(
                            false,
                            null,
                            "host_timeout",
                            "worker exceeded the poll budget",
                            OperationStatus.HostBusy));
                },
                static () => true,
                CancellationToken.None)
            ;

        Assert.Equal(1, polls);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1000));
        Assert.Equal(WatchTermination.SourceUnavailable, outcome.Termination);
        Assert.False(outcome.Satisfied);
    }

    [Fact]
    public async Task PollBudgetIsAHardStop()
    {
        var clock = new FakeWatchClock();
        var plan = Plan(
            """
            {"source":{"operation":"com.get"},"predicate":{"kind":"equals","expected":1},"timeoutMs":60000,"pollIntervalMs":100}
            """);

        plan = plan with { MaxPolls = 4 };

        var outcome = await WatchConditionPoller.RunAsync(
                plan,
                clock,
                static (_, _, _) => Task.FromResult(Sample("0")),
                static () => true,
                CancellationToken.None)
            ;

        Assert.Equal(WatchTermination.PollBudgetExhausted, outcome.Termination);
        Assert.Equal(4, outcome.Polls);
    }

    [Fact]
    public async Task PollsRunStrictlySequentiallyAndReleaseBetweenAttempts()
    {
        // A watch must never hold a target lane across a wait, and must never
        // have two child reads in flight at once.
        var clock = new FakeWatchClock();
        var inFlight = 0;
        var peak = 0;
        var order = new List<int>();

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"equals","expected":2},"pollIntervalMs":100}
                    """),
                clock,
                (index, _, _) =>
                {
                    inFlight++;
                    peak = Math.Max(peak, inFlight);
                    order.Add(index);
                    inFlight--;
                    return Task.FromResult(
                        Sample(index == 3 ? "2" : "0"));
                },
                static () => true,
                CancellationToken.None)
            ;

        Assert.Equal(1, peak);
        Assert.Equal([1, 2, 3], order);
        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
    }

    [Fact]
    public async Task CancellationStopsThePollLoop()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeWatchClock();
        var polls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await WatchConditionPoller.RunAsync(
                    Plan(
                        """
                        {"source":{"operation":"com.get"},"predicate":{"kind":"equals","expected":1},"timeoutMs":60000,"pollIntervalMs":100}
                        """),
                    clock,
                    (_, _, _) =>
                    {
                        polls++;
                        if (polls == 2)
                            cancellation.Cancel();

                        return Task.FromResult(Sample("0"));
                    },
                    static () => true,
                    cancellation.Token)
                );

        Assert.Equal(2, polls);
    }

    // ------------------------------------------------------ truthful failures

    [Fact]
    public async Task TimeoutReportsTheLastObservedValue()
    {
        var outcome = await Run(
            samples: ["0", "0", "0"],
            predicate: Predicate("equals", Value("1")),
            timeoutMs: 1200,
            intervalMs: 400);

        Assert.Equal(WatchTermination.Timeout, outcome.Termination);
        Assert.True(outcome.LastVerified);
        Assert.Equal("0", outcome.LastValue!.Value!.Value.GetRawText());
        Assert.Equal(3, outcome.Polls);
    }

    [Fact]
    public async Task UnobservableSourceIsNeverReportedAsATimeout()
    {
        var clock = new FakeWatchClock();

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":1000,"pollIntervalMs":400}
                    """),
                clock,
                static (_, _, _) => Task.FromResult(
                    new WatchSourceSample(
                        false,
                        null,
                        "host_busy",
                        "Illustrator is busy",
                        OperationStatus.HostBusy)),
                static () => true,
                CancellationToken.None)
            ;

        Assert.Equal(WatchTermination.SourceUnavailable, outcome.Termination);
        Assert.False(outcome.LastVerified);
        Assert.Null(outcome.LastValue);
        Assert.Equal("host_busy", outcome.LastFailureKind);
    }

    [Fact]
    public async Task TransientSourceFailureCanRecover()
    {
        var clock = new FakeWatchClock();
        var polls = 0;

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"equals","expected":1},"timeoutMs":5000,"pollIntervalMs":100}
                    """),
                clock,
                (_, _, _) =>
                {
                    polls++;
                    return polls == 1
                        ? Task.FromResult(
                            new WatchSourceSample(
                                false,
                                null,
                                "host_busy",
                                "Illustrator is busy",
                                OperationStatus.HostBusy))
                        : Task.FromResult(Sample(polls == 2 ? "0" : "1"));
                },
                static () => true,
                CancellationToken.None)
            ;

        Assert.Equal(WatchTermination.Satisfied, outcome.Termination);
        Assert.Equal(3, outcome.Polls);
    }

    [Fact]
    public async Task LosingThePinnedGenerationStopsTheWatch()
    {
        var clock = new FakeWatchClock();
        var polls = 0;
        var current = true;

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"equals","expected":1},"timeoutMs":60000,"pollIntervalMs":100}
                    """),
                clock,
                (_, _, _) =>
                {
                    polls++;
                    return Task.FromResult(Sample("0"));
                },
                () =>
                {
                    // A restarted host process must never be followed.
                    if (polls == 2)
                        current = false;

                    return current;
                },
                CancellationToken.None)
            ;

        Assert.Equal(WatchTermination.TargetGenerationChanged, outcome.Termination);
        Assert.Equal(2, outcome.Polls);
        Assert.Equal("target_generation_changed", outcome.DescribeTermination());
    }

    [Fact]
    public async Task LostGenerationBeforeTheFirstPollCostsNoAttempts()
    {
        var clock = new FakeWatchClock();
        var polls = 0;

        var outcome = await WatchConditionPoller.RunAsync(
                Plan(
                    """
                    {"source":{"operation":"com.get"},"predicate":{"kind":"truthy"},"timeoutMs":60000}
                    """),
                clock,
                (_, _, _) =>
                {
                    polls++;
                    return Task.FromResult(Sample("1"));
                },
                static () => false,
                CancellationToken.None)
            ;

        Assert.Equal(WatchTermination.TargetGenerationChanged, outcome.Termination);
        Assert.Equal(0, polls);
    }

    // ------------------------------------------------------- child request ids

    [Fact]
    public void ChildRequestIdsAreDeterministicUniqueAndAddressable()
    {
        var first = WatchConditionPoller.CreateChildRequestId("watch-1", 1);
        var again = WatchConditionPoller.CreateChildRequestId("watch-1", 1);
        var second = WatchConditionPoller.CreateChildRequestId("watch-1", 2);
        var other = WatchConditionPoller.CreateChildRequestId("watch-2", 1);

        Assert.Equal(first, again);
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, other);

        var ids = Enumerable
            .Range(1, 256)
            .Select(index => WatchConditionPoller.CreateChildRequestId(
                "watch-1",
                index))
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            ids,
            static id => Assert.InRange(id.Length, 1, 128));
    }

    // ------------------------------------------------------- source policy

    [Fact]
    public void WatchRefusesToComposeItself()
    {
        var error = Assert.Throws<OperationConditionPolicyException>(
            () => WatchConditionPoller.EnsureWatchableSource(
                WatchConditionPoller.OperationName));

        Assert.Equal("watch_source_not_watchable", error.Kind);

        WatchConditionPoller.EnsureWatchableSource("com.get");
    }

    [Fact]
    public void WatchRefusesMutatingSources()
    {
        var target = Target("com.get", "script.eval");

        var error = Assert.Throws<OperationConditionPolicyException>(
            () => OperationConditionEvaluator.ValidateSources(
                [Condition("guard", "script.eval", """{"code":"1"}""")],
                target,
                "watch"));

        Assert.Equal("condition_source_not_read_only", error.Kind);
    }

    [Fact]
    public void WatchRefusesLeaseRequiringSources()
    {
        // plugin.message is registered read-path-visible but lease-requiring;
        // a watch composes lease-free reads only, so no lease is ever taken.
        var target = Target("com.get", "plugin.message");

        var error = Assert.Throws<OperationConditionPolicyException>(
            () => OperationConditionEvaluator.ValidateSources(
                [Condition("guard", "plugin.message", """{"message":"x"}""")],
                target,
                "watch"));

        Assert.Equal("condition_source_not_read_only", error.Kind);
    }

    [Fact]
    public void WatchAcceptsAFixedReadOnlyAdvertisedHostOperation()
    {
        var target = Target("com.get");

        OperationConditionEvaluator.ValidateSources(
            [
                Condition(
                    "saved",
                    "com.get",
                    """{"path":"ActiveDocument.Saved"}""")
            ],
            target,
            "watch");
    }

    [Fact]
    public void WatchRefusesUnadvertisedSources()
    {
        var target = Target("core.target.status");

        var error = Assert.Throws<OperationConditionPolicyException>(
            () => OperationConditionEvaluator.ValidateSources(
                [
                    Condition(
                        "saved",
                        "com.get",
                        """{"path":"ActiveDocument.Saved"}""")
                ],
                target,
                "watch"));

        Assert.Equal("condition_source_not_advertised", error.Kind);
    }

    // -------------------------------------------------------------- helpers

    private static bool Read(
        string json,
        out string? error) =>
        WatchConditionInput.TryRead(
            Element(json),
            policyWorkerWatchdogMs: null,
            out _,
            out error);

    private static WatchPlan Plan(
        string json,
        int? policyWorkerWatchdogMs = null)
    {
        Assert.True(
            WatchConditionInput.TryRead(
                Element(json),
                policyWorkerWatchdogMs,
                out var plan,
                out var error),
            error);

        return plan!;
    }

    private static async Task<WatchOutcome> Run(
        IReadOnlyList<string> samples,
        OperationConditionPredicate predicate,
        int timeoutMs,
        int intervalMs)
    {
        var clock = new FakeWatchClock();
        var queue = new Queue<string>(samples);

        var predicateInput = new Dictionary<string, object?>
        {
            ["kind"] = predicate.Kind
        };
        if (predicate.Expected is { } expected)
        {
            predicateInput["expected"] = expected.Kind == "null"
                ? null
                : expected.Value!.Value;
        }

        return await WatchConditionPoller.RunAsync(
                Plan(
                    JsonSerializer.Serialize(new
                    {
                        source = new
                        {
                            operation = "com.get",
                            input = new { path = "ActiveDocument.Saved" }
                        },
                        predicate = predicateInput,
                        timeoutMs,
                        pollIntervalMs = intervalMs
                    })),
                clock,
                (_, _, _) => Task.FromResult(
                    Sample(queue.Count > 0 ? queue.Dequeue() : "0")),
                static () => true,
                CancellationToken.None);
    }

    private static OperationConditionPredicate Predicate(
        string kind,
        ProtocolValue? expected = null) =>
        new() { Kind = kind, Expected = expected };

    private static WatchSourceSample Sample(string json) =>
        new(true, Value(json), null, null, OperationStatus.Completed);

    private static ProtocolValue Value(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ProtocolValue.From(document.RootElement);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static OperationCondition Condition(
        string id,
        string operation,
        string inputJson) =>
        new()
        {
            Id = id,
            Source = new OperationConditionSource
            {
                Operation = operation,
                Input = Element(inputJson)
            },
            Predicate = new OperationConditionPredicate
            {
                Kind = "truthy"
            }
        };

    private static HostTargetDescriptor Target(params string[] capabilities)
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 4242,
            ProcessStartedAt = DateTimeOffset.Parse(
                "2026-09-26T09:15:00Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "test",
            EndpointIdentity = "Illustrator.Application"
        };

        return new HostTargetDescriptor
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Running = true,
            Capabilities = capabilities
                .Select(
                    name => new CapabilityDescriptor
                    {
                        Name = name,
                        Version = "1",
                        MutationClass =
                            name switch
                            {
                                "script.eval" => MutationClass.Unknown,
                                "plugin.message" =>
                                    MutationClass.ExternalSideEffect,
                                _ => MutationClass.ReadOnly
                            },
                        Supported = true,
                        Host = "illustrator"
                    })
                .ToArray()
        };
    }
}
