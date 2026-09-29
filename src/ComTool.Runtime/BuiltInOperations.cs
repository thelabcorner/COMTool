using ComTool.Protocol;

namespace ComTool.Runtime;

/// <summary>
/// Runtime-owned semantic catalog. Transports and host workers must derive
/// mutation/recovery policy from this catalog instead of trusting caller input.
/// </summary>
public static class BuiltInOperations
{
    public static OperationCatalog Catalog { get; } = new(
    [
        new OperationDefinition(
            "core.runtime.health",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.operations.list",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.operation.describe",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.operation.examples",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "core.adobe.probe",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "core.artifact.describe",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "core.artifact.read",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "knowledge.describe",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "knowledge.search",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "knowledge.symbol",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "knowledge.enum",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "knowledge.paths",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "script.validate",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "watch.condition",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "core.incidents.list",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.incident.resolve",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.targets.list",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.target.attach",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "core.target.launch",
            MutationClass.ExternalSideEffect,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "core.target.capabilities",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.target.reconcile",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            RequiresLease: true),

        new OperationDefinition(
            "core.target.mutation.reconcile",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            RequiresLease: true),

        new OperationDefinition(
            "core.target.incident.resolve",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            RequiresLease: true),

        new OperationDefinition(
            "core.workflow.submit",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.workflow.get",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.workflow.cancel",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.workflow.resume",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.target.lease.acquire",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.target.lease.renew",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            RequiresLease: true),

        new OperationDefinition(
            "core.target.lease.release",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            RequiresLease: true),

        new OperationDefinition(
            "core.target.host.terminate",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime,
            RequiresLease: true),

        new OperationDefinition(
            "core.target.status",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host),

        new OperationDefinition(
            "core.target.snapshot",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host),

        new OperationDefinition(
            "illustrator.document.read",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.artboard.read",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.layer.read",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.layer.setName",
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.layer.setVisible",
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.layer.setLocked",
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.layer.setOpacity",
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.artboard.setRect",
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "com.get",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host),

        new OperationDefinition(
            "com.call.read",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host),

        new OperationDefinition(
            "com.set",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "com.call",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "plugin.message",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "plugin.debug.diagnostics",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "plugin.debug.control",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.action.run",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.menu.execute",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "debug.session.open",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "debug.session.status",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "debug.session.command",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "debug.session.close",
            MutationClass.ExternalSideEffect,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "script.codec.status",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "script.eval",
            MutationClass.Unknown,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.DeclaredOrUnknown),

        new OperationDefinition(
            "script.runFile",
            MutationClass.Unknown,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.DeclaredOrUnknown),

        new OperationDefinition(
            "illustrator.artboard.setName",
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.document.create",
            MutationClass.DocumentLifecycle,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.document.open",
            MutationClass.DocumentLifecycle,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.document.save",
            MutationClass.DocumentLifecycle,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.document.saveAs",
            MutationClass.DocumentLifecycle,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed),

        new OperationDefinition(
            "illustrator.document.close",
            MutationClass.DocumentLifecycle,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true,
            MutationResolution: MutationResolutionMode.Fixed)
    ]);
}
