using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class HostAttachRuntimeTests
{
    [Fact]
    public async Task ExactStrongGenerationIsReturnedUnchanged()
    {
        var identity = Identity();
        var descriptor = Descriptor(identity);
        var runtime = new HostAttachRuntime(
            ["illustrator"],
            new FakeDiscovery([descriptor]));

        var result = await runtime.AttachAsync(
            new TargetRef(
                "illustrator",
                identity.TargetId,
                Generation: 0));

        Assert.Same(descriptor, result);
    }

    [Fact]
    public async Task MissingGenerationIsNeverSubstituted()
    {
        var current = Identity();
        var runtime = new HostAttachRuntime(
            ["illustrator"],
            new FakeDiscovery([Descriptor(current)]));

        var error = await Assert.ThrowsAsync<HostAttachException>(
            async () => await runtime.AttachAsync(
                new TargetRef(
                    "illustrator",
                    "illustrator:000000000000000000000000",
                    Generation: 0)));

        Assert.Equal("target_generation_not_found", error.Kind);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task UnconfiguredHostFailsBeforeDiscovery()
    {
        var discovery = new FakeDiscovery([]);
        var runtime = new HostAttachRuntime(
            ["illustrator"],
            discovery);

        var error = await Assert.ThrowsAsync<HostAttachException>(
            async () => await runtime.AttachAsync(
                new TargetRef(
                    "photoshop",
                    "photoshop:000000000000000000000000",
                    Generation: 0)));

        Assert.Equal("host_family_not_configured", error.Kind);
        Assert.Equal(0, discovery.Calls);
    }

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = "illustrator",
            ProcessId = 4242,
            ProcessStartedAt =
                new DateTimeOffset(
                    2026,
                    9,
                    27,
                    8,
                    0,
                    0,
                    TimeSpan.Zero),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "0.1.0-dev",
            EndpointIdentity = "Illustrator.Application"
        };

    private static HostTargetDescriptor Descriptor(
        HostTargetIdentity identity) =>
        new()
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Capabilities = [],
            Running = true
        };

    private sealed class FakeDiscovery(
        IReadOnlyList<HostTargetDescriptor> targets)
        : IHostTargetDiscovery
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(targets);
        }
    }
}
