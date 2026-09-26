using System.Diagnostics;
using System.Text.Json;
using ComTool.Hosts.Illustrator;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Supervisor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: supervisor-smoke <ComTool.Worker.exe>");
            return 2;
        }

        return Run(args[0]);
    }

    private static int Run(string workerPath)
    {
        var adapter = new IllustratorAdapter();
        var targets = adapter
            .DiscoverAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        if (targets.Count != 1)
            throw new InvalidOperationException(
                $"Expected exactly one Illustrator target, found {targets.Count}.");

        var target = targets[0];
        var state = new TargetStateMachine();

        var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = Path.GetFullPath(workerPath),
                CommandTimeout = TimeSpan.FromSeconds(30)
            },
            state);

        try
        {
            var firstWorker = supervisor
                .PingAsync()
                .GetAwaiter()
                .GetResult();

            using var nullDoc = JsonDocument.Parse("null");
            var nullInput = nullDoc.RootElement.Clone();

            var clock = Stopwatch.StartNew();

            var tasks = Enumerable.Range(0, 50)
                .Select(i => supervisor.ExecuteAsync(new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = $"smoke-{i:D3}",
                    Target = target.Target,
                    Operation = "core.target.status",
                    Input = nullInput
                }))
                .ToArray();

            var results = Task
                .WhenAll(tasks)
                .GetAwaiter()
                .GetResult();

            clock.Stop();

            var allCompleted = results.All(
                result => result.Ok &&
                          result.Status == OperationStatus.Completed &&
                          result.TargetState == TargetState.Known);

            var workerAfterLoad = supervisor.Worker
                ?? throw new InvalidOperationException(
                    "Worker disappeared after concurrent read load.");

            var sameWorker =
                string.Equals(
                    firstWorker.WorkerId,
                    workerAfterLoad.WorkerId,
                    StringComparison.Ordinal) &&
                firstWorker.ProcessId == workerAfterLoad.ProcessId;

            // Simulate the exact state a hard watchdog on a mutation would leave.
            // No mutation is executed in this smoke test.
            state.MarkAmbiguousExecution(
                MutationClass.ConditionalWrite,
                "smoke_ambiguous_mutation");

            var beforeRestart = supervisor.State;
            var secondWorker = supervisor
                .RestartWorkerAsync()
                .GetAwaiter()
                .GetResult();

            var afterRestart = supervisor.State;
            var workerChanged =
                !string.Equals(
                    firstWorker.WorkerId,
                    secondWorker.WorkerId,
                    StringComparison.Ordinal);

            var ambiguitySurvivedRestart =
                beforeRestart.State == TargetState.ReconciliationRequired &&
                afterRestart.State == TargetState.ReconciliationRequired &&
                string.Equals(
                    afterRestart.IncidentKind,
                    "smoke_ambiguous_mutation",
                    StringComparison.Ordinal);

            var reconciliation = supervisor
                .ReconcileAsync()
                .GetAwaiter()
                .GetResult();

            var afterReconcile = supervisor.State;
            var reconciled =
                reconciliation.Reconciled &&
                afterReconcile.State == TargetState.Known;

            var timingValues = results
                .Select(result => result.Timing?.TotalMs ?? 0)
                .Order()
                .ToArray();

            var report = new
            {
                ok =
                    allCompleted &&
                    sameWorker &&
                    workerChanged &&
                    ambiguitySurvivedRestart &&
                    reconciled,
                targetId = target.Identity.TargetId,
                firstWorker = new
                {
                    firstWorker.WorkerId,
                    firstWorker.ProcessId,
                    firstWorker.Apartment
                },
                secondWorker = new
                {
                    secondWorker.WorkerId,
                    secondWorker.ProcessId,
                    secondWorker.Apartment
                },
                concurrentReads = new
                {
                    count = results.Length,
                    allCompleted,
                    sameWorker,
                    wallMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3),
                    minResultMs = Math.Round(timingValues[0], 3),
                    medianResultMs = Math.Round(
                        timingValues[timingValues.Length / 2],
                        3),
                    maxResultMs = Math.Round(timingValues[^1], 3)
                },
                recovery = new
                {
                    workerChanged,
                    ambiguitySurvivedRestart,
                    beforeRestart,
                    afterRestart,
                    reconciliation,
                    afterReconcile
                }
            };

            Console.WriteLine(JsonSerializer.Serialize(report));
            return report.ok ? 0 : 1;
        }
        finally
        {
            supervisor
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }
}
