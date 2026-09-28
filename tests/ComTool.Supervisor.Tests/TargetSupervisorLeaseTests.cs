using System.Diagnostics;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;
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

    [Fact]
    public async Task BreakGlassHostTerminationBypassesInFlightOperationGate()
    {
        using var process = StartSleeper();
        await using var supervisor =
            CreateSupervisor(process);
        var grant =
            await supervisor.AcquireLeaseAsync(30_000);

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = supervisor.WithLeaseAccessAsync(
            grant.LeaseId,
            requiresLease: true,
            async _ =>
            {
                entered.SetResult();
                await release.Task;
                return 11;
            });

        await entered.Task;

        try
        {
            var termination =
                await supervisor
                    .TerminateHostGenerationAsync(
                        grant.LeaseId,
                        process.Id,
                        new DateTimeOffset(
                            process.StartTime),
                        waitTimeoutMs: 5_000)
                    .WaitAsync(
                        TimeSpan.FromSeconds(10));

            Assert.True(termination.KillIssued);
            Assert.True(termination.ExitObserved);
            Assert.False(termination.AlreadyExited);
            Assert.Equal(
                TargetState.Unavailable,
                supervisor.State.State);
            Assert.True(
                supervisor.LeaseStatus.ExpiresAt >
                grant.ExpiresAt);
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(11, await operation);

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        await supervisor.ReleaseLeaseAsync(
            grant.LeaseId);
    }

    [Fact]
    public async Task BreakGlassHostTerminationRejectsWrongLeaseWithoutKillingProcess()
    {
        using var process = StartSleeper();
        await using var supervisor =
            CreateSupervisor(process);
        var grant =
            await supervisor.AcquireLeaseAsync(30_000);

        try
        {
            var error =
                await Assert.ThrowsAsync<TargetLeaseException>(
                    () => supervisor.TerminateHostGenerationAsync(
                        new string('B', 64),
                        process.Id,
                        new DateTimeOffset(process.StartTime),
                        waitTimeoutMs: 1_000));

            Assert.Equal("lease_mismatch", error.Kind);
            Assert.False(process.HasExited);
        }
        finally
        {
            await supervisor.ReleaseLeaseAsync(
                grant.LeaseId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task BreakGlassHostTerminationRejectsWrongGenerationWithoutKillingProcess()
    {
        using var process = StartSleeper();
        await using var supervisor =
            CreateSupervisor(process);
        var grant =
            await supervisor.AcquireLeaseAsync(30_000);

        try
        {
            var error =
                await Assert.ThrowsAsync<TargetStateException>(
                    () => supervisor.TerminateHostGenerationAsync(
                        grant.LeaseId,
                        process.Id,
                        new DateTimeOffset(process.StartTime)
                            .AddSeconds(1),
                        waitTimeoutMs: 1_000));

            Assert.Equal(
                "target_termination_confirmation_mismatch",
                error.Kind);
            Assert.False(process.HasExited);
        }
        finally
        {
            await supervisor.ReleaseLeaseAsync(
                grant.LeaseId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
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

    private static TargetSupervisor CreateSupervisor(
        Process process)
    {
        var identity = new HostTargetIdentity
        {
            Host = "test",
            ProcessId = process.Id,
            ProcessStartedAt =
                new DateTimeOffset(process.StartTime),
            ExecutablePath =
                Path.Combine(
                    Environment.SystemDirectory,
                    "cmd.exe"),
            HostVersion = "1",
            AdapterVersion = "1",
            EndpointIdentity =
                $"test-{process.Id}"
        };

        return new TargetSupervisor(
            new HostTargetDescriptor
            {
                Identity = identity,
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0),
                Capabilities =
                    Array.Empty<CapabilityDescriptor>(),
                Running = true
            },
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    "unused.exe"
            });
    }

    private static Process StartSleeper() =>
        Process.Start(
            new ProcessStartInfo
            {
                FileName =
                    Path.Combine(
                        Environment.SystemDirectory,
                        "cmd.exe"),
                Arguments =
                    "/d /s /c \"ping.exe 127.0.0.1 -n 120 >nul\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })
        ?? throw new InvalidOperationException(
            "Failed to start host-termination test process.");
}
