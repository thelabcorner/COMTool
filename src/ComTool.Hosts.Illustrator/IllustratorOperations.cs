using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

public static class IllustratorOperations
{
    public static IReadOnlyList<CapabilityDescriptor> Capabilities { get; } =
    [
        new()
        {
            Name = "core.target.status",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Read Illustrator host/version/document/UI state."
        },
        new()
        {
            Name = "core.target.snapshot",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Capture a coarse active-document and selection state fingerprint."
        },
        new()
        {
            Name = "com.get",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Read a strict dotted COM property path with optional 0-based collection indexing."
        },
        new()
        {
            Name = "com.call.read",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Invoke only runtime-allowlisted COM query methods; arbitrary COM call remains unsupported."
        },
        new()
        {
            Name = "script.eval",
            Version = "1",
            MutationClass = MutationClass.Unknown,
            Supported = true,
            Host = "illustrator",
            Description = "Execute inline ExtendScript through DoJavaScript. Effects default to unknown; arbitrary script cannot self-certify read-only and requires a target lease."
        },
        new()
        {
            Name = "script.runFile",
            Version = "1",
            MutationClass = MutationClass.Unknown,
            Supported = true,
            Host = "illustrator",
            Description = "Execute a caller-SHA-256-bound absolute .jsx/.jsxbin through $.evalFile. Arguments are exposed temporarily as $.global.__comtool_v2_runfile_args and restored afterward. Effects default to unknown; requires a target lease."
        },
        new()
        {
            Name = "illustrator.artboard.setName",
            Version = "1",
            MutationClass = MutationClass.IdempotentWrite,
            Supported = true,
            Host = "illustrator",
            Description = "Set the name of the active document's active artboard through a runtime-owned fixed property put. Not a generic COM setter; requires a target lease."
        },
        new()
        {
            Name = "illustrator.document.read",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Read one document's identity and dirty state by name, index, or document id. Never mutates; usable as a runtime precondition/postcondition source."
        },
        new()
        {
            Name = "illustrator.artboard.read",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Read a document's artboards by name, index, or active selector: index, name, ArtboardRect, ruler origin, and ruler pixel aspect ratio. Never mutates; usable as a runtime precondition/postcondition source."
        },
        new()
        {
            Name = "illustrator.layer.read",
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Read a document's layers by name, index, or active selector: index, name, visibility, lock, template/preview flags, opacity, uuid, and path-item count. Never mutates; usable as a runtime precondition/postcondition source."
        },
        new()
        {
            Name = "illustrator.document.create",
            Version = "1",
            MutationClass = MutationClass.DocumentLifecycle,
            Supported = true,
            Host = "illustrator",
            Description = "Create a new empty document and return its identity. Requires an exclusive target lease; the new document identity is verified after dispatch."
        },
        new()
        {
            Name = "illustrator.document.open",
            Version = "1",
            MutationClass = MutationClass.DocumentLifecycle,
            Supported = true,
            Host = "illustrator",
            Description = "Open a document from an absolute path with explicit OpenOptions and return its identity. Refuses paths already open; requires an exclusive target lease."
        },
        new()
        {
            Name = "illustrator.document.save",
            Version = "1",
            MutationClass = MutationClass.DocumentLifecycle,
            Supported = true,
            Host = "illustrator",
            Description = "Save an identified document in place and verify its file identity after dispatch. Requires an exclusive target lease."
        },
        new()
        {
            Name = "illustrator.document.saveAs",
            Version = "1",
            MutationClass = MutationClass.DocumentLifecycle,
            Supported = true,
            Host = "illustrator",
            Description = "Save an identified document to a new absolute path, optionally overwriting an existing file. Requires an exclusive target lease."
        },
        new()
        {
            Name = "illustrator.document.close",
            Version = "1",
            MutationClass = MutationClass.DocumentLifecycle,
            Supported = true,
            Host = "illustrator",
            Description = "Close an identified document under an explicit mandatory policy (save | discard | reject_if_unsaved). Discard requires the exact token and is never a default; requires an exclusive target lease."
        }
    ];
}
