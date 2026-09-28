using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// The worker must pin exact native debugger bytes before the bridge child
/// exists, and the bridge must refuse to load any addon that is not those
/// bytes. These tests drive the real embedded bridge script through node so a
/// regression in the digest gate fails here rather than in a live session.
/// </summary>
public sealed class IllustratorDebugAddonProvenanceTests
{
    [Fact]
    public void ChildEnvironmentCarriesExactAddonPathAndPinnedDigest()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node.exe"
        };

        DebuggerBridge.ApplyAddonProvenance(
            startInfo,
            @"C:\deps\esdcorelibinterface.node",
            new string('a', 64));

        Assert.Equal(
            @"C:\deps\esdcorelibinterface.node",
            startInfo.Environment["COMTOOL_ESD_ADDON_PATH"]);
        Assert.Equal(
            new string('a', 64),
            startInfo.Environment["COMTOOL_ESD_ADDON_SHA256"]);
    }

    [Fact]
    public void ApplyAddonProvenanceRejectsMissingPathOrDigest()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node.exe"
        };

        Assert.Throws<ArgumentException>(
            () => DebuggerBridge.ApplyAddonProvenance(
                startInfo,
                "  ",
                new string('a', 64)));
        Assert.Throws<ArgumentException>(
            () => DebuggerBridge.ApplyAddonProvenance(
                startInfo,
                @"C:\deps\esdcorelibinterface.node",
                string.Empty));
    }

    [Fact]
    public void BridgeRefusesToLoadAddonBytesThatWereNotPinned()
    {
        var node = ResolveNode();
        if (node is null)
            return;

        using var scratch = new TemporaryDirectory();
        var addon = Path.Combine(scratch.Path, "esdcorelibinterface.node");
        File.WriteAllBytes(addon, [0x4D, 0x5A, 0x00, 0x01]);

        // A well-formed but wrong digest must be rejected before require(),
        // so the failure is about provenance and not about module loading.
        var mismatch = RunBridge(node, addon, new string('b', 64));

        Assert.False(mismatch.Ok);
        Assert.Equal(2, mismatch.ExitCode);
        Assert.Contains(
            "digest mismatch",
            mismatch.Error,
            StringComparison.OrdinalIgnoreCase);

        // The correct digest passes the provenance gate and fails later, at
        // module load. That ordering is what proves the gate is real.
        var pinned = RunBridge(
            node,
            addon,
            Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(addon)))
                .ToLowerInvariant());

        Assert.False(pinned.Ok);
        Assert.Equal(2, pinned.ExitCode);
        Assert.DoesNotContain(
            "digest mismatch",
            pinned.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BridgeRefusesToStartWithoutAPinnedDigest()
    {
        var node = ResolveNode();
        if (node is null)
            return;

        using var scratch = new TemporaryDirectory();
        var addon = Path.Combine(scratch.Path, "esdcorelibinterface.node");
        File.WriteAllBytes(addon, [0x4D, 0x5A, 0x00, 0x01]);

        var startInfo = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(
            DebuggerBridgeAsset.Materialize().Path);
        startInfo.Environment["COMTOOL_ESD_ADDON_PATH"] = addon;
        startInfo.Environment.Remove("COMTOOL_ESD_ADDON_SHA256");

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
            process.Kill(entireProcessTree: true);
        Assert.Equal(2, process.ExitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.False(
            document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "COMTOOL_ESD_ADDON_SHA256",
            document.RootElement
                .GetProperty("error")
                .GetString()!,
            StringComparison.Ordinal);
    }

    private static BridgeResult RunBridge(
        string node,
        string addonPath,
        string pinnedSha256)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(
            DebuggerBridgeAsset.Materialize().Path);
        DebuggerBridge.ApplyAddonProvenance(
            startInfo,
            addonPath,
            pinnedSha256);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        return new BridgeResult(
            root.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.True,
            process.ExitCode,
            root.TryGetProperty("error", out var error)
                ? error.GetString() ?? string.Empty
                : string.Empty);
    }

    private static string? ResolveNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "node.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private sealed record BridgeResult(
        bool Ok,
        int ExitCode,
        string Error);

    private sealed class TemporaryDirectory :
        IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "comtool-v2-debug-prov-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Scratch cleanup is best effort.
            }
        }
    }
}
