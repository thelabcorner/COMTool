using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

public static class IllustratorOperations
{
    private static readonly bool DebuggerSupported =
        IllustratorDebuggerSupport.Probe().Supported;

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
            Name = IllustratorComMutationBridge.SetOperation,
            Version = "1",
            MutationClass = IllustratorComMutationBridge.GenericMutationClass,
            Supported = true,
            Host = "illustrator",
            Description = "Assign a general COM property through a strict dotted path and an explicit bounded JSON value domain. Fixed external side effect: never assumed idempotent, requires a target lease, and is never replayed after a dispatch that may have been accepted. Proof that one property is safe belongs to a typed operation."
        },
        new()
        {
            Name = IllustratorComMutationBridge.CallOperation,
            Version = "1",
            MutationClass = IllustratorComMutationBridge.GenericMutationClass,
            Supported = true,
            Host = "illustrator",
            Description = "Invoke a general COM method through a strict dotted path with a bounded JSON argument domain. Fixed external side effect: the bridge never self-certifies read-only, requires a target lease, and is never replayed after a dispatch that may have been accepted. Use com.call.read for proven read-only queries."
        },
        new()
        {
            Name = "plugin.message",
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = true,
            Host = "illustrator",
            Description = "Send a bounded data request to a native Illustrator plug-in through Application.SendScriptMessage. The plug-in/selector contract remains plug-in-owned; V2 supplies target generation, lease, no-replay, incident, and payload provenance semantics."
        },
        new()
        {
            Name = IllustratorPluginDebugManager.DiagnosticsOperation,
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Read the fixed AIPDebug/1 discover/info/logs/snapshot/stats surface. Direct VectorIPC is only a bounded transport optimization after endpoint and exact server-generation provenance has been established through the worker-owned host path."
        },
        new()
        {
            Name = IllustratorPluginDebugManager.ControlOperation,
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = true,
            Host = "illustrator",
            Description = "Issue one bounded AIPDebug/1 control action (clear or break) through the worker-owned plug-in debug facet. Requires an exclusive target lease and is never replayed after a dispatch that may have been accepted."
        },
        new()
        {
            Name = IllustratorDebugSessionManager.OpenOperation,
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = DebuggerSupported,
            Host = "illustrator",
            Description = "Open one persistent worker-owned ExtendScript debugger session for this exact Illustrator generation. Requires an exclusive target lease and available ESD native-core dependencies."
        },
        new()
        {
            Name = IllustratorDebugSessionManager.StatusOperation,
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = DebuggerSupported,
            Host = "illustrator",
            Description = "Report whether this target worker currently owns a usable debugger session: session state, bounded safe metadata, strong target and child generations, and pinned debugger provenance. Observation only; it never requires a lease, contacts Adobe, or creates, reopens, or rebinds a session."
        },
        new()
        {
            Name = IllustratorDebugSessionManager.CommandOperation,
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = DebuggerSupported,
            Host = "illustrator",
            Description = "Execute a typed ESTK3 debugger command inside the existing worker-owned session: eval, breakpoints, stack/frame/property inspection, continue, break, halt, and stepping. No stateless debugger subprocess per command."
        },
        new()
        {
            Name = IllustratorDebugSessionManager.CloseOperation,
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = DebuggerSupported,
            Host = "illustrator",
            Description = "Close the lease-owned persistent ExtendScript debugger session and release its native debugger client."
        },
        new()
        {
            Name = IllustratorActionRun.Operation,
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = true,
            Host = "illustrator",
            Description = "Run one named action from one named action set through Application.DoScript with an explicit dialog policy, then wait on ActionIsRunning for a bounded, caller-derived budget. ActionIsRunning is process-global, so a run whose baseline is already busy is refused instead of attributed; everything from the first possible DoScript submission onward is reported as ambiguous and never replayed. Requires an exclusive target lease. The host's global UserInteractionLevel is never mutated: the DoScript dialog flag is the only dialog policy."
        },
        new()
        {
            Name = IllustratorScriptCodecStatus.Operation,
            Version = "1",
            MutationClass = MutationClass.ReadOnly,
            Supported = true,
            Host = "illustrator",
            Description = "Report whether the exact pinned optional ESON script codec is already present in this Illustrator generation. Runs one fixed read-only presence probe, never installs or bootstraps the codec, and exposes expected SHA-256 provenance plus on-demand fallback semantics."
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
            Name = IllustratorTypedMutationOperations.LayerSetNameOperation,
            Version = "1",
            MutationClass = MutationClass.IdempotentWrite,
            Supported = true,
            Host = "illustrator",
            Description = "Set one explicitly selected layer's name. The document and layer are resolved before dispatch; callers cannot supply a COM path. Requires an exclusive target lease."
        },
        new()
        {
            Name = IllustratorTypedMutationOperations.LayerSetVisibleOperation,
            Version = "1",
            MutationClass = MutationClass.IdempotentWrite,
            Supported = true,
            Host = "illustrator",
            Description = "Set one explicitly selected layer's visibility through a fixed typed property assignment. Requires an exclusive target lease."
        },
        new()
        {
            Name = IllustratorTypedMutationOperations.LayerSetLockedOperation,
            Version = "1",
            MutationClass = MutationClass.IdempotentWrite,
            Supported = true,
            Host = "illustrator",
            Description = "Set one explicitly selected layer's locked state through a fixed typed property assignment. Requires an exclusive target lease."
        },
        new()
        {
            Name = IllustratorTypedMutationOperations.LayerSetOpacityOperation,
            Version = "1",
            MutationClass = MutationClass.IdempotentWrite,
            Supported = true,
            Host = "illustrator",
            Description = "Set one explicitly selected layer's opacity to a finite value from 0 through 100. Requires an exclusive target lease."
        },
        new()
        {
            Name = IllustratorTypedMutationOperations.ArtboardSetRectOperation,
            Version = "1",
            MutationClass = MutationClass.IdempotentWrite,
            Supported = true,
            Host = "illustrator",
            Description = "Set one explicitly selected artboard's [left, top, right, bottom] rectangle through a fixed typed property assignment. Requires an exclusive target lease."
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
        },
        new()
        {
            Name = IllustratorMenuCommand.ExecuteOperation,
            Version = "1",
            MutationClass = MutationClass.ExternalSideEffect,
            Supported = true,
            Host = "illustrator",
            Description = "Dispatch exactly one caller-supplied Illustrator menu command string through Application.ExecuteMenuCommand. No route selection, no fallback cascade, and no caller-declared effects; the fixed external-side-effect class requires an exclusive target lease, journals before dispatch, and never replays after a possible dispatch. A completed dispatch does not prove the command's effect: verify with explicit postconditions. Menu command strings are version-sensitive and undocumented, so prefer a typed operation whenever one exists."
        }
    ];
}
