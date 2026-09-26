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
            "core.targets.list",
            MutationClass.ReadOnly,
            RequiresTarget: false,
            Scope: OperationExecutionScope.Runtime),

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
            Scope: OperationExecutionScope.Runtime),

        new OperationDefinition(
            "core.target.lease.release",
            MutationClass.ReadOnly,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Runtime),

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
