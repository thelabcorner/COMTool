# COMTool Agent Contract

COMTool is a guarded automation runtime for Adobe desktop applications. An
agent must discover live runtime and host capabilities before choosing an
execution path.

## Prime directive

**Probe first. Discover second. Mutate only through runtime-owned policy.**

Never infer automation support from installation or process presence alone.

`core.adobe.probe` separates:

1. environment detection;
2. COM registration;
3. COMTool adapter availability and runtime configuration.

## Bootstrap sequence

For a new Adobe task:

1. `ComTool.Cli.exe health`
2. `ComTool.Cli.exe probe`
3. inspect `core.operations.list`
4. inspect `core.operation.describe` for relevant operations
5. `ComTool.Cli.exe targets`
6. query `core.target.capabilities` for the exact target generation
7. query `core.operation.examples` when an input shape is unfamiliar
8. execute the least-powerful operation that satisfies the task

For one host:

```powershell
ComTool.Cli.exe probe --host illustrator
ComTool.Cli.exe probe --host photoshop
```

## Probe semantics

`core.adobe.probe` is read-only, target-independent, and does not activate an
Adobe application.

Important fields:

- `detected`: known process or COM registration evidence exists;
- `configured`: the active RuntimeHost is configured for that host family;
- `adapterAvailable`: this COMTool build contains automation support;
- `automationTier`:
  - `full`: adapter exists and the runtime is configured;
  - `adapter_available_not_configured`: support exists but this runtime was
    not configured for it;
  - `probe_only`: COMTool can inspect the environment but does not claim host
    automation support.

Probe output is not proof of host responsiveness, document state, or an exact
target's supported operation set. Use `core.target.capabilities` for that.

## Operation selection

Prefer, in order:

1. fixed typed host operation;
2. bounded read-only generic operation;
3. guarded generic mutation;
4. arbitrary ExtendScript only when no narrower operation exists.

For Illustrator, prefer typed `illustrator.*` operations over generic
`com.set`, `com.call`, or arbitrary scripts whenever possible.

The live operation catalog owns mutation class, target requirements, lease
requirements, execution scope, and mutation-resolution policy. Agent
confidence does not override runtime policy.

## Strong target identity

Always operate on the exact target returned by `core.targets.list`. Adobe
process restarts create a new generation. Never silently substitute a new
process for a stale target identity.

## Leases, request identity, and ambiguity

Acquire a lease whenever the runtime requires one. Use a stable request ID for
effectful work.

Potentially mutating work must never be automatically replayed after dispatch
may have occurred. A transport failure after submission is not proof that the
Adobe-side action failed.

For ambiguous execution or `reconciliation_required`:

1. preserve the original request ID;
2. inspect incident/runtime state;
3. reconcile the exact target generation;
4. verify operation-specific postconditions;
5. do not rerun merely because transport recovered.

Transport self-healing repairs infrastructure for later/new requests. It is not
mutation replay.

## ExtendScript

Run `script.validate` before unfamiliar scripts.

Use expression mode for one value-producing expression and statement mode for
statement/function bodies. Use result capture only when the result matters;
discard it when completion is all that matters.

Arbitrary scripts cannot self-certify as read-only.

## Local real-engine CI

Use the Node SDK `ComToolLocalRuntime` for private real-engine test runtimes.
It still uses the canonical RuntimeHost -> RuntimeSupervisor -> Worker path; do
not introduce a second COM implementation.

## Introspection precedence

When information differs, prefer:

1. `core.target.capabilities` for the exact live target;
2. `core.operations.list` / `core.operation.describe`;
3. `core.adobe.probe`;
4. the checked-in operation registry;
5. prose documentation.

## Completion checklist

Before reporting completion, verify:

- correct host and exact process generation;
- operation was advertised by the live runtime/target;
- required lease was held;
- ambiguous execution was not silently replayed;
- requested postconditions were observed;
- owned leases/sessions/private runtimes were released.
