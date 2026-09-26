using System.Diagnostics;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Ipc;

internal static class Program
{
    private static async Task<int> Main()
    {
        await using var client = await RuntimePipeClient
            .ConnectAsync(timeout: TimeSpan.FromSeconds(10));

        using var nullDocument = JsonDocument.Parse("null");
        var nullInput = nullDocument.RootElement.Clone();

        var health = await Execute(
            client,
            "runtime-smoke-health-1",
            "core.runtime.health",
            target: null,
            nullInput);

        var targets = await Execute(
            client,
            "runtime-smoke-targets",
            "core.targets.list",
            target: null,
            nullInput);

        if (!targets.Ok ||
            targets.Result?.Value is not JsonElement targetArray ||
            targetArray.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Target list was not returned.");

        var first = targetArray.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("No running target was returned.");

        var targetElement = first.GetProperty("target");
        var target = new TargetRef(
            targetElement.GetProperty("host").GetString()
                ?? throw new InvalidOperationException("Target host missing."),
            targetElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Target id missing."),
            targetElement.TryGetProperty("generation", out var generation) &&
            generation.ValueKind == JsonValueKind.Number
                ? generation.GetInt64()
                : null);

        var firstClock = Stopwatch.StartNew();
        var firstStatus = await Execute(
            client,
            "runtime-smoke-status-first",
            "core.target.status",
            target,
            nullInput);
        firstClock.Stop();

        var warmTimes = new List<double>(20);
        var allWarmOk = true;
        for (var i = 0; i < 20; i++)
        {
            var clock = Stopwatch.StartNew();
            var result = await Execute(
                client,
                $"runtime-smoke-status-{i:D2}",
                "core.target.status",
                target,
                nullInput);
            clock.Stop();

            warmTimes.Add(clock.Elapsed.TotalMilliseconds);
            allWarmOk &= result.Ok;
        }

        warmTimes.Sort();

        var healthAfter = await Execute(
            client,
            "runtime-smoke-health-2",
            "core.runtime.health",
            target: null,
            nullInput);

        var liveWorkers = ExtractNumber(
            healthAfter,
            "liveWorkers");

        var report = new
        {
            ok =
                health.Ok &&
                targets.Ok &&
                firstStatus.Ok &&
                allWarmOk &&
                healthAfter.Ok &&
                liveWorkers == 1,
            pipe = RuntimeEndpoint.DefaultPipeName,
            target,
            firstStatusWallMs = Math.Round(
                firstClock.Elapsed.TotalMilliseconds,
                3),
            warm = new
            {
                count = warmTimes.Count,
                minMs = Math.Round(warmTimes[0], 3),
                medianMs = Math.Round(warmTimes[warmTimes.Count / 2], 3),
                maxMs = Math.Round(warmTimes[^1], 3)
            },
            liveWorkers
        };

        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.ok ? 0 : 1;
    }

    private static Task<OperationResult> Execute(
        RuntimePipeClient client,
        string id,
        string operation,
        TargetRef? target,
        JsonElement input) =>
        client.ExecuteAsync(new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Target = target,
            Operation = operation,
            Input = input
        });

    private static int ExtractNumber(
        OperationResult result,
        string property)
    {
        if (result.Result?.Value is not JsonElement value ||
            value.ValueKind != JsonValueKind.Object)
            return -1;

        return value.TryGetProperty(property, out var number)
            ? number.GetInt32()
            : -1;
    }
}
