using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Read-only environment probe for Adobe desktop host families. This surface
/// never activates a COM server and never creates an Adobe automation object.
/// It only observes process presence and COM ProgID registration, then reports
/// whether the current COMTool build actually contains an automation adapter.
/// </summary>
internal static class AdobeHostProbe
{
    private static readonly JsonSerializerOptions PayloadJson =
        new(JsonSerializerDefaults.Web);

    private static readonly HostDefinition[] Hosts =
    [
        new(
            "illustrator",
            "Adobe Illustrator",
            ["Illustrator"],
            ["Illustrator.Application"],
            ["ai"]),
        new(
            "photoshop",
            "Adobe Photoshop",
            ["Photoshop"],
            ["Photoshop.Application"],
            ["ps"]),
        new(
            "indesign",
            "Adobe InDesign",
            ["InDesign"],
            ["InDesign.Application"],
            ["id"]),
        new(
            "after-effects",
            "Adobe After Effects",
            ["AfterFX"],
            ["AfterEffects.Application"],
            ["aftereffects", "after_effects", "ae"]),
        new(
            "premiere-pro",
            "Adobe Premiere Pro",
            ["Adobe Premiere Pro"],
            [],
            ["premiere", "premierepro", "pr"]),
        new(
            "audition",
            "Adobe Audition",
            ["Adobe Audition"],
            [],
            ["au"]),
        new(
            "bridge",
            "Adobe Bridge",
            ["Bridge"],
            ["Bridge.Application"],
            []),
        new(
            "animate",
            "Adobe Animate",
            ["Animate"],
            [],
            ["flash"]),
        new(
            "media-encoder",
            "Adobe Media Encoder",
            ["Adobe Media Encoder"],
            [],
            ["ame", "mediaencoder"]),
        new(
            "acrobat",
            "Adobe Acrobat",
            ["Acrobat"],
            ["AcroExch.App"],
            ["acrobat-pro"])
    ];

    public static JsonElement Execute(
        JsonElement input,
        IReadOnlyCollection<string> configuredHosts)
    {
        var requestedHost = ReadOptionalString(input, "host");
        var includeUndetected = ReadOptionalBoolean(
            input,
            "includeUndetected",
            defaultValue: true);

        HostDefinition[] selected;
        if (requestedHost is null)
        {
            selected = Hosts;
        }
        else
        {
            var normalized = NormalizeHost(requestedHost);
            selected = Hosts
                .Where(host => string.Equals(
                    host.Host,
                    normalized,
                    StringComparison.Ordinal))
                .ToArray();

            if (selected.Length == 0)
            {
                throw new AdobeHostProbeException(
                    "unknown_adobe_host",
                    $"Unknown Adobe host '{requestedHost}'. Known hosts: " +
                    string.Join(
                        ", ",
                        Hosts.Select(static host => host.Host)));
            }
        }

        var configured = configuredHosts.ToHashSet(StringComparer.Ordinal);
        var observations = selected
            .Select(host => Observe(host, configured))
            .Where(host => includeUndetected || host.Detected)
            .ToArray();

        return JsonSerializer.SerializeToElement(
            new
            {
                probeVersion = "1",
                authoritativeFor = new[]
                {
                    "process_presence",
                    "com_registration",
                    "comtool_adapter_availability"
                },
                notAuthoritativeFor = new[]
                {
                    "application_responsiveness",
                    "document_state",
                    "operation_support_until_target_capabilities_are_queried"
                },
                configuredHosts = configuredHosts
                    .OrderBy(static host => host, StringComparer.Ordinal)
                    .ToArray(),
                requestedHost = requestedHost is null
                    ? null
                    : NormalizeHost(requestedHost),
                count = observations.Length,
                hosts = observations
            },
            PayloadJson);
    }

    internal static string? NormalizeHost(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var host in Hosts)
        {
            if (string.Equals(
                    host.Host,
                    normalized,
                    StringComparison.Ordinal) ||
                host.Aliases.Contains(
                    normalized,
                    StringComparer.Ordinal))
            {
                return host.Host;
            }
        }

        return null;
    }

    private static HostObservation Observe(
        HostDefinition host,
        IReadOnlySet<string> configuredHosts)
    {
        var processes = ObserveProcesses(host.ProcessNames);
        var registeredProgIds = host.ProgIds
            .Where(IsProgIdRegistered)
            .ToArray();
        var adapterAvailable = BuiltInOperations.Catalog.Definitions.Any(
            definition => string.Equals(
                definition.Host,
                host.Host,
                StringComparison.Ordinal));
        var configured = configuredHosts.Contains(host.Host);
        var detected =
            processes.Count > 0 ||
            registeredProgIds.Length > 0;

        var automationTier = adapterAvailable
            ? configured
                ? "full"
                : "adapter_available_not_configured"
            : "probe_only";

        return new HostObservation(
            host.Host,
            host.DisplayName,
            detected,
            configured,
            adapterAvailable,
            automationTier,
            host.ProcessNames,
            host.ProgIds,
            registeredProgIds,
            processes,
            RecommendedActions(
                host.Host,
                detected,
                configured,
                adapterAvailable));
    }

    private static IReadOnlyList<ProcessObservation> ObserveProcesses(
        IReadOnlyList<string> processNames)
    {
        var observations = new List<ProcessObservation>();

        foreach (var processName in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    DateTimeOffset? startedAt = null;
                    try
                    {
                        startedAt = new DateTimeOffset(process.StartTime)
                            .ToUniversalTime();
                    }
                    catch
                    {
                    }

                    observations.Add(new ProcessObservation(
                        process.ProcessName,
                        process.Id,
                        startedAt));
                }
            }
        }

        return observations
            .OrderBy(static process => process.ProcessName, StringComparer.Ordinal)
            .ThenBy(static process => process.ProcessId)
            .ToArray();
    }

    private static bool IsProgIdRegistered(string progId)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(
                progId,
                writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> RecommendedActions(
        string host,
        bool detected,
        bool configured,
        bool adapterAvailable)
    {
        if (!adapterAvailable)
        {
            return detected
                ? ["probe_only_adapter_not_implemented"]
                : ["install_or_start_host_if_needed"];
        }

        if (!configured)
        {
            return
            [
                $"restart_runtime_with_host:{host}",
                "core.adobe.probe"
            ];
        }

        return detected
            ?
            [
                "core.targets.list",
                "core.target.capabilities",
                "core.operation.examples"
            ]
            :
            [
                "core.target.launch",
                "core.targets.list",
                "core.target.capabilities"
            ];
    }

    private static string? ReadOptionalString(
        JsonElement input,
        string property)
    {
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new AdobeHostProbeException(
                "invalid_adobe_probe_query",
                "core.adobe.probe input must be a JSON object.");
        }

        if (!input.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AdobeHostProbeException(
                "invalid_adobe_probe_query",
                $"'{property}' must be a string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AdobeHostProbeException(
                "invalid_adobe_probe_query",
                $"'{property}' must not be empty.");
        }

        return text;
    }

    private static bool ReadOptionalBoolean(
        JsonElement input,
        string property,
        bool defaultValue)
    {
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return defaultValue;
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new AdobeHostProbeException(
                "invalid_adobe_probe_query",
                "core.adobe.probe input must be a JSON object.");
        }

        if (!input.TryGetProperty(property, out var value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new AdobeHostProbeException(
                "invalid_adobe_probe_query",
                $"'{property}' must be a boolean.")
        };
    }

    private sealed record HostDefinition(
        string Host,
        string DisplayName,
        IReadOnlyList<string> ProcessNames,
        IReadOnlyList<string> ProgIds,
        IReadOnlyList<string> Aliases);

    private sealed record ProcessObservation(
        string ProcessName,
        int ProcessId,
        DateTimeOffset? StartedAtUtc);

    private sealed record HostObservation(
        string Host,
        string DisplayName,
        bool Detected,
        bool Configured,
        bool AdapterAvailable,
        string AutomationTier,
        IReadOnlyList<string> ProcessNames,
        IReadOnlyList<string> ProgIds,
        IReadOnlyList<string> RegisteredProgIds,
        IReadOnlyList<ProcessObservation> RunningProcesses,
        IReadOnlyList<string> RecommendedActions);
}

internal sealed class AdobeHostProbeException(
    string kind,
    string message)
    : Exception(message)
{
    public string Kind { get; } = kind;
}