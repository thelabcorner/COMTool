using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class WorkerHostLaunchExecutorTests
{
    [Fact]
    public async Task WrongProgIdTraversesAuthenticatedLaunchWorkerWithoutActivation()
    {
        var worker = ResolveTestWorkerExecutable();
        var executor = new WorkerHostLaunchExecutor(
            "illustrator",
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = worker,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10)
            });

        var error = await Assert.ThrowsAsync<HostLaunchException>(
            async () => await executor.LaunchAsync(
                new HostLaunchSpec
                {
                    Host = "illustrator",
                    ProgId = "Illustrator.NotARealLaunchClass",
                    ExistingInstance =
                        HostLaunchExistingInstancePolicy.Fail,
                    LaunchTimeoutMs = 10_000,
                    Arguments = []
                }));

        Assert.Equal("unsupported_launch_progid", error.Kind);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
        Assert.False(error.Retryable);
    }

    private static string ResolveTestWorkerExecutable()
    {
        var currentConfiguration =
            new DirectoryInfo(AppContext.BaseDirectory)
                .Parent?
                .Name;
        var configurations =
            string.Equals(
                currentConfiguration,
                "Release",
                StringComparison.OrdinalIgnoreCase)
                ? new[] { "Release", "Debug" }
                : new[] { "Debug", "Release" };
        var candidates = configurations.SelectMany(configuration =>
            new[]
            {
                Path.GetFullPath(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "..",
                        "..",
                        "..",
                        "..",
                        "..",
                        "src",
                        "ComTool.Worker",
                        "bin",
                        configuration,
                        "net10.0-windows",
                        "ComTool.Worker.exe")),
                Path.GetFullPath(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "..",
                        "..",
                        "ComTool.Worker",
                        configuration.ToLowerInvariant(),
                        "ComTool.Worker.exe"))
            }).ToArray();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "ComTool.Worker.exe was not built for the launch-worker integration test.",
                candidates[0]);
    }
}
