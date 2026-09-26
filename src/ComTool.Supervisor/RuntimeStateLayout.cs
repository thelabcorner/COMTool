using System.Text.Json;

namespace ComTool.Supervisor;

public sealed class RuntimeStateLayoutException(
    string kind,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// Owns the durable on-disk contract for one COM Tool V2 runtime state root.
/// The root manifest is intentionally separate from per-record schema versions
/// so upgrades cannot silently reinterpret an incompatible directory layout.
/// </summary>
public sealed class RuntimeStateLayout
{
    public const int CurrentSchemaVersion = 1;
    private const string ManifestFormat = "comtool-v2-state";
    private const string ManifestFileName = "state-manifest.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private RuntimeStateLayout(string root)
    {
        Root = root;
        ManifestPath = Path.Combine(root, ManifestFileName);
        MutationLedgerRoot = Path.Combine(root, "mutation-ledger");
        WorkflowRoot = Path.Combine(root, "workflows");
    }

    public string Root { get; }
    public string ManifestPath { get; }
    public string MutationLedgerRoot { get; }
    public string WorkflowRoot { get; }

    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ComToolV2");

    public static RuntimeStateLayout Open(string? root = null)
    {
        var resolved = Path.GetFullPath(
            string.IsNullOrWhiteSpace(root)
                ? DefaultRoot
                : root);
        var layout = new RuntimeStateLayout(resolved);

        try
        {
            Directory.CreateDirectory(layout.Root);
            layout.EnsureManifest();
            return layout;
        }
        catch (RuntimeStateLayoutException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new RuntimeStateLayoutException(
                "runtime_state_open_failed",
                $"Could not open runtime state directory '{resolved}': {ex.Message}",
                ex);
        }
    }

    private void EnsureManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            if (Directory.Exists(Path.Combine(Root, "records")) ||
                Directory.Exists(Path.Combine(Root, "active")))
            {
                throw new RuntimeStateLayoutException(
                    "runtime_state_legacy_layout",
                    $"State directory '{Root}' uses the pre-V1 custom mutation-ledger layout. " +
                    "Refusing to start because automatically ignoring or moving unresolved mutation state is unsafe.");
            }

            WriteInitialManifest();
        }

        RuntimeStateManifest manifest;
        try
        {
            manifest =
                JsonSerializer.Deserialize<RuntimeStateManifest>(
                    File.ReadAllBytes(ManifestPath),
                    Json)
                ?? throw new JsonException(
                    "State manifest deserialized to null.");
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new RuntimeStateLayoutException(
                "runtime_state_manifest_corrupt",
                $"Could not read runtime state manifest '{ManifestPath}': {ex.Message}",
                ex);
        }

        if (!string.Equals(
                manifest.Format,
                ManifestFormat,
                StringComparison.Ordinal))
        {
            throw new RuntimeStateLayoutException(
                "runtime_state_manifest_format_mismatch",
                $"State directory '{Root}' is not a COM Tool V2 state root.");
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new RuntimeStateLayoutException(
                "runtime_state_schema_mismatch",
                $"State directory '{Root}' uses schema version {manifest.SchemaVersion}; " +
                $"this runtime supports only version {CurrentSchemaVersion}.");
        }
    }

    private void WriteInitialManifest()
    {
        var manifest = new RuntimeStateManifest
        {
            Format = ManifestFormat,
            SchemaVersion = CurrentSchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var temporary =
            ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(
                    temporary,
                    ManifestPath,
                    overwrite: false);
            }
            catch (IOException) when (File.Exists(ManifestPath))
            {
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record RuntimeStateManifest
    {
        public required string Format { get; init; }
        public required int SchemaVersion { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }
}