# COM Tool V2

Greenfield successor to the existing COM Tool. **The existing `comtool/` tree is a read-only behavioral oracle during V2 development and must not be modified by V2 work.**

V2 is an executable-first, agent-native Adobe desktop automation runtime. Illustrator is the primary reference/conformance host; other Adobe applications integrate through truthful capability-driven adapters instead of being forced into an Illustrator-shaped API.

## Architectural invariants

- The executable/runtime is the product.
- MCP is an optional transport, never the core.
- CLI, JSON/stdio, local IPC, and MCP normalize into one versioned operation protocol.
- COM is a host capability, not the universal abstraction.
- The runtime supervisor owns routing, policy, leases, durable mutation state, and observability; per-host workers own host execution contexts.
- COM/Adobe execution stays inside dedicated STA workers in brokered/runtime mode.
- Target IDs are opaque and generation-specific; a restarted Adobe process is a new target.
- Arbitrary script execution cannot self-certify as read-only.
- Mutation dispatch is write-ahead journaled before it reaches a worker.
- Completed mutation retries with the same request ID are replayed from durable state rather than re-executed.
- Ambiguous mutation state survives worker/runtime crashes and cannot be cleared by a mere host heartbeat.
- Mutation leases are exclusive across runtime processes, not merely inside one runtime.
- Host adapters advertise only capabilities they actually support.
- Illustrator is the first complete adapter and compatibility-oracle target.
- The legacy Python tool remains available until V2 reaches and exceeds behavioral parity.

## Current phase

**Production foundation + Illustrator Wave B controlled mutation/document lifecycle alpha.**

Phase 0 architecture is accepted. The production topology is:

```text
agent / CLI / future MCP
          │
          │ OperationRequest / OperationResult
          ▼
 current-user runtime pipe
          │
          ▼
  RuntimeSupervisor
      │
      ├── discovery worker (STA, short-lived)
      │
      └── target worker (STA, persistent)
                   │
                   ▼
             Adobe host API
```

Direct mode and one-shot broker mode remain diagnostic/fallback transports. The persistent runtime is the preferred agent surface.

## Transports

Every agent-facing surface normalizes into the one versioned `OperationRequest` /
`OperationResult` contract and routes through the one `RuntimeSupervisor`:

| Surface | Entry point |
|---|---|
| CLI commands | `ComTool.Cli.exe <command>` (use `--runtime`) |
| Local IPC | `ComTool.RuntimeHost.exe` (current-user named pipe, default) |
| JSON/stdio (NDJSON) | `ComTool.RuntimeHost.exe --stdio`, or `ComTool.Cli.exe stdio` as a pipe-backed proxy |
| MCP (optional) | `ComTool.Transport.Mcp.exe --server` |

MCP is a thin adapter: its one tool builds a real `OperationRequest` and forwards
it over the ordinary runtime pipe. It contains no Adobe/host logic and never
becomes the architecture. Validate it with:

```powershell
ComTool.Transport.Mcp.exe --self-test --pipe <name>
```

See `docs/TRANSPORT_CLI_MCP_PARITY.md` for the parity matrix, runtime/worker
discovery rules, and state-directory layout.

The comprehensive governing plan remains:

`../comtool/GREENFIELD_ARCHITECTURE_AUDIT_AND_EXECUTION_PLAN.md`
Important V2 documents:

- `PHASE0_STATUS.md` — evidence-backed architecture gate ledger.
- `docs/LEGACY_LESSONS_AND_V2_PRINCIPLES.md` — binding successor principles derived from the old tool.
- `docs/ADR-0001-bootstrap-boundaries.md` — isolation from the legacy implementation.
- `docs/ADR-0002-runtime-topology.md` — process/transport topology.
- `docs/ADR-0003-mutation-safety-and-script-runtime.md` — durable mutation, leases, and ESON-backed script execution.
- `docs/TRANSPORT_CLI_MCP_PARITY.md` — how CLI, stdio, local IPC, and MCP share one operation protocol; runtime/worker discovery; state layout.
- `docs/RELEASE_READINESS.md` — packaging, clean-machine checklist, and what still needs a live host.

## Local toolchain

A project-local .NET 10 SDK is bootstrapped into `.dotnet/`; it does not modify global PATH or the Windows registry.

```powershell
.\scripts\dotnet.ps1 --info
```

Pinned SDK: **10.0.401 x64**.

For a development runtime, build first and then stage an immutable copy:

```powershell
.\scripts\stage-runtime-dev.ps1
```

Do not run a persistent development runtime directly from `bin/Release`; Windows will lock loaded assemblies and obstruct subsequent builds.

## Current production operations

Runtime/control:

- `core.runtime.health`
- `core.targets.list`
- `core.target.capabilities`
- `core.target.status`
- `core.target.snapshot`
- `core.target.reconcile`
- `core.target.lease.acquire`
- `core.target.lease.renew`
- `core.target.lease.release`
- `core.workflow.submit`
- `core.workflow.get`
- `core.workflow.cancel`
- `core.workflow.resume`

Illustrator read surface:

- `com.get`
- `com.call.read`
- `illustrator.document.read`
- `illustrator.artboard.read`
- `illustrator.layer.read`

Guarded script surface:

- `script.eval`

Controlled mutation surface:

- `illustrator.artboard.setName` (`idempotent_write`)

Document lifecycle surface:

- `illustrator.document.create`
- `illustrator.document.open`
- `illustrator.document.save`
- `illustrator.document.saveAs`
- `illustrator.document.close`

Reconciliation surface:

- `core.target.mutation.reconcile` — the only path that may clear a durable
  mutation incident, and only from verified, passing read-only postcondition
  evidence evaluated against live host state. Host liveness, a bare ping, or
  worker replacement never qualify. The postconditions must exactly match the
  conditions durably declared with the original mutation request; new evidence
  cannot be attached after ambiguity.

`script.eval` requires an exclusive target lease and a caller-stable request ID. Its `effects` field may declare a write class, but arbitrary source may **not** declare `read_only`; omitted effects default to `unknown`.

Every controlled-mutation and document-lifecycle operation is registered in the runtime-owned catalog with a `Fixed` mutation class and `RequiresLease: true`. A caller cannot weaken the runtime's safety floor: the resolver returns the catalog's class verbatim and ignores any caller-declared `effects`. The COM write surface is a runtime-owned allowlist of fixed property targets (currently one: the active artboard's name). There is no generic arbitrary COM setter, and callers never supply a COM member, path, or index. Values are transported as ESON data, never concatenated into script text.

The document lifecycle operations address a named, indexed, active, or stable
document-id selector validated by the adapter; they do not silently retarget to
whatever document becomes active later. `illustrator.document.close` requires
an explicit `closePolicy`: `save`, `discard`, or `reject_if_unsaved`. The
`discard` token is exact and mandatory; `reject_if_unsaved` refuses to close a
dirty document.

To reconcile a mutation, call `mutation-reconcile` with the active incident's
request ID and current target-state revision, plus the exact postcondition list
originally included on the mutation request:

```powershell
ComTool.Cli.exe mutation-reconcile --runtime --lease <lease-id> `
  --incident-request-id <request-id> --expected-revision <revision> `
  --postconditions-file <original-postconditions.json> --pipe <pipe-name>
```

The supervisor validates every condition source as a fixed read-only host
operation, compares the canonical condition fingerprint with the durable
mutation record, evaluates it against the live target, and clears the incident
only when the batch is both verified and passing. Mutations that were not
originally submitted with postconditions, including legacy records without the
fingerprint, remain for explicit incident resolution instead.

Structured ExtendScript arguments/results use the canonical ESON runtime embedded from the sibling ESON project and hash-pinned at build time. The slim runtime build is deliberately used for the high-frequency eval path; ESPACK/native ESON acceleration remains optional for future parse-heavy operations.

## Mutation crash model

For non-read-only operations, the runtime persists:

```text
prepared → completed
         → not_started
         → ambiguous
```

The durable request ID is the idempotency key.

- `completed`: an identical retry returns the stored result with `mutation.replay` evidence and `executeMs = 0`.
- `not_started`: the same request may safely retry.
- `prepared` after runtime loss, or `ambiguous`: the target rehydrates as `reconciliation_required`; the mutation is not replayed.
- same request ID + different semantic payload: rejected.
- generic host liveness is not proof of mutation outcome.

Target lease ownership is backed by an OS-held per-target lock keyed by strong target ID. A second runtime receives `target_leased_external`; process crash releases the kernel handle automatically.

## Durable workflow jobs

`core.workflow.submit` accepts a sequential workflow of 1–64 registered
operations. Each step has an explicit step id, operation, input, and optional
preconditions/postconditions. The outer request id is the job id and the
idempotency key: resubmitting the same id and definition returns the existing
job; reusing it for different intent is rejected.

Every step is dispatched by the same `RuntimeSupervisor.ExecuteAsync` path as a
direct request. Step request ids are deterministically derived from the job id
and step id, so runtime recovery reuses the mutation ledger's completed replay,
not-started retry, and ambiguity rules. A mutating workflow automatically holds
one target lease for the run; `targetLease:false` is accepted only when every
step is read-only. `onError` is explicit (`stop` or `continue`), but a step with
an ambiguous/reconciliation-required outcome always halts the workflow.

Jobs persist step state/results under the runtime state directory. A runtime
restart marks unfinished jobs `interrupted`; it never starts them implicitly.
Inspect with `core.workflow.get`, then explicitly resume with the returned job
revision via `core.workflow.resume`. Cancellation through `core.workflow.cancel`
is cooperative at step boundaries; it does not abort a host call in flight.
Graceful runtime shutdown cancels owned queued jobs, requests cancellation of
running jobs, and waits for their current step to finish.
Wave 3 currently runs steps sequentially: dependency DAGs, parallel steps, and
event streaming are not part of this first durable-job slice.

## Richer Illustrator surfaces (Wave 4)

Wave 4 begins the legacy "Wave C — richer Illustrator surfaces" work with the
read-only structural primitives an agent needs before it can safely plan a
mutation:

- `illustrator.artboard.read` — artboards of a selected document (index, name,
  `ArtboardRect`, ruler origin, ruler pixel aspect ratio).
- `illustrator.layer.read` — layers of a selected document (index, name,
  visibility, lock, template/preview flags, opacity, uuid, path-item count).

Both take an explicit document selector (`name`, `index`, or `active=true`),
never silently retargeting to whatever document becomes active later. Exactly
one selector is required. A selector that does not resolve is a truthful
observed state (`exists:false` with a reason), not a hidden failure, so the
runtime decides what a failed condition means.

These operations are catalogued as `read_only` with a `Fixed` mutation class
and are host-scoped, so they require a target but no lease, create no mutation
ledger entry, and are valid precondition/postcondition sources for mutation
steps and workflows. Every item field is read defensively: a single unreadable
field degrades to `null` rather than aborting the read.

`script.runFile` now provides the explicit file-routing slice for JSX files,
including directive-aware route semantics under the same runtime-owned
mutation/lease policy as `script.eval`. Still outstanding in this wave:
actions, menu commands, advisory ES3 validation, watch polling, COM inventory
discovery, enum/signature introspection, and large-result artifacts.

## Verification

The current deterministic gate is **285/285 passing**. It covers protocol,
runtime/catalog policy, supervisor and durable reconciliation, Illustrator COM
and document-operation adapters, length-prefixed pipe, NDJSON stdio, and the
MCP bridge. Run it with:

```powershell
scripts/dotnet.ps1 build ComTool.V2.slnx -c Release
scripts/dotnet.ps1 test ComTool.V2.slnx -c Release --no-build
```

Key transport-parity coverage:

- `tests/ComTool.Transport.Stdio.Tests/CrossTransportParityTests.cs` — the same
  request dispatched by one handler yields an identical canonical envelope across
  NDJSON stdio and the length-prefixed pipe.
- `tests/ComTool.Transport.Mcp.Tests/RuntimeBridgeTests.cs` — the MCP bridge
  produces canonical validation envelopes and a structured `runtime_unreachable`
  error without host dependence.
- `ComTool.Transport.Mcp.exe --self-test` — MCP vs direct pipe semantic envelope
  parity against a live runtime (success and error).

Live Illustrator evidence covers:

- STA worker ownership and persistent worker reuse;
- typed COM reads and snapshot parity;
- ESON-backed string/number/boolean/null/array/object script result fidelity;
- structured ESON argument parsing;
- warm small-expression execution around ~0.9–1.1 ms inside Illustrator in the measured session;
- durable completed-result replay across a hard runtime/worker crash with **zero target-worker re-execution**;
- cross-runtime lease contention returning `target_leased_external`;
- immediate lease takeover after the owning runtime is hard-killed;
- a freshly staged runtime advertising the controlled artboard-name and document-lifecycle operations with their runtime-owned mutation classes;
- operation-specific reconciliation reaching live Illustrator with a valid lease and refusing to clear state when no matching durable incident exists.
- Wave 4 `illustrator.artboard.read` and `illustrator.layer.read` against a
  disposable live document, with cleanup restoring Illustrator to zero open
  documents.

The new artboard/document mutations have deterministic adapter tests but have
not been dispatched against the user's open document. Live mutation conformance
must use a disposable document fixture and verify identity and postconditions.

See `evidence/` for captured results.
