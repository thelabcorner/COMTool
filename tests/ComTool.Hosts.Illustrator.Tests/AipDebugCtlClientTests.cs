using System.Security.Cryptography;
using System.Text.Json;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class AipDebugCtlClientTests
{
    [Fact]
    public void PackagedSiblingWinsOverEnvironmentOverrideAndMatchesManifest()
    {
        var root = CreateTempDirectory();
        try
        {
            var sibling = Path.Combine(
                root,
                AipDebugCtlClient.ExecutableName);
            File.WriteAllBytes(sibling, "trusted-helper"u8.ToArray());
            WriteManifest(root, sibling);

            var overridePath = Path.Combine(root, "override.exe");
            File.WriteAllBytes(
                overridePath,
                "different-helper"u8.ToArray());

            var located = AipDebugCtlClient.LocateFrom(
                root,
                overridePath);

            Assert.Equal(
                Path.GetFullPath(sibling),
                Path.GetFullPath(located!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackagedSiblingHashMismatchFailsClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            var sibling = Path.Combine(
                root,
                AipDebugCtlClient.ExecutableName);
            File.WriteAllBytes(sibling, "trusted-helper"u8.ToArray());
            WriteManifest(root, sibling);
            File.WriteAllBytes(sibling, "tampered-helper"u8.ToArray());

            var error = Assert.Throws<AipDebugCtlTrustException>(
                () => AipDebugCtlClient.LocateFrom(
                    root,
                    configured: null));

            Assert.Contains(
                "SHA-256",
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManifestRequiredHelperCannotFallBackToOverrideWhenMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            var sibling = Path.Combine(
                root,
                AipDebugCtlClient.ExecutableName);
            File.WriteAllBytes(sibling, "trusted-helper"u8.ToArray());
            WriteManifest(root, sibling);
            File.Delete(sibling);

            var overridePath = Path.Combine(root, "override.exe");
            File.WriteAllBytes(
                overridePath,
                "different-helper"u8.ToArray());

            Assert.Throws<AipDebugCtlTrustException>(
                () => AipDebugCtlClient.LocateFrom(
                    root,
                    overridePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrustRefusalIsReportedBeforeHelperLaunch()
    {
        var client = new AipDebugCtlClient(
            () => throw new AipDebugCtlTrustException(
                "helper provenance refused"));
        var outcome = client.Invoke(
            new AipDebugCtlInvocation(
                "aipdbg-test",
                "info",
                42,
                123,
                null,
                null,
                1000,
                1024),
            CancellationToken.None);

        Assert.Equal(
            AipDebugCtlCompletion.LaunchFailed,
            outcome.Completion);
        Assert.Contains(
            "provenance refused",
            outcome.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRuntimeOwnedBoundsFailBeforeHelperResolution()
    {
        var locateCalls = 0;
        var client = new AipDebugCtlClient(
            () =>
            {
                locateCalls++;
                return @"C:\should-not-launch\aipdebugctl.exe";
            });

        var outcome = client.Invoke(
            new AipDebugCtlInvocation(
                "aipdbg-test-42",
                "info",
                42,
                123,
                null,
                null,
                AipDebugCtlClient.MaxTimeoutMs + 1,
                AipDebugCtlClient.MaxOutputBytes),
            CancellationToken.None);

        Assert.Equal(
            AipDebugCtlCompletion.LaunchFailed,
            outcome.Completion);
        Assert.Contains(
            "timeout",
            outcome.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, locateCalls);
    }

    [Fact]
    public void ChildArgumentsPinExactProcessGeneration()
    {
        var arguments = AipDebugCtlClient.BuildArguments(
            new AipDebugCtlInvocation(
                "aipdbg-test-4242",
                "logs",
                4242,
                133700000000000000,
                12,
                25,
                2345,
                AipDebugCtlClient.MaxOutputBytes));

        Assert.Contains("--expect-pid", arguments);
        Assert.Contains("4242", arguments);
        Assert.Contains("--expect-start-filetime", arguments);
        Assert.Contains("133700000000000000", arguments);
        Assert.Contains("--timeout-ms", arguments);
        Assert.Contains("2345", arguments);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "comtool-aipdebugctl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteManifest(
        string root,
        string executablePath)
    {
        using var executable = File.OpenRead(executablePath);
        var sha256 = Convert
            .ToHexString(SHA256.HashData(executable))
            .ToLowerInvariant();
        var manifest = JsonSerializer.Serialize(
            new
            {
                nativeHelpers = new
                {
                    aipdebugctl = new
                    {
                        path = AipDebugCtlClient.ExecutableName,
                        sha256,
                        transport = "vectoripc",
                        required = true
                    }
                }
            });
        File.WriteAllText(
            Path.Combine(root, "release-manifest.json"),
            manifest);
    }
}