# COM Tool V2

Greenfield successor to the existing COM Tool. **The existing `comtool/` tree is a read-only behavioral oracle during V2 development and must not be modified by V2 work.**

V2 is an executable-first, agent-native **universal programmatic development and control surface for Adobe desktop software**. Illustrator is the primary reference/conformance host today. ExtendScript execution, structured Illustrator automation, native plug-in/debugger tooling, and agent-oriented manipulation all belong behind the same versioned runtime surface; other Adobe applications integrate through truthful capability-driven adapters instead of being forced into an Illustrator-shaped API.

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

**Production foundation + guarded Illustrator controlled mutation/document lifecycle surface.**

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

The persistent runtime pipe uses Windows `CurrentUserOnly` isolation. The
current Windows user is therefore the public local trust boundary; V2 does not
pretend that the pipe provides an additional credential boundary against other
processes already running as that same user. The private supervisor→worker pipe
does have a separate ephemeral token plus child PID/start-time verification.

## Transports

Every agent-facing surface normalizes into the one versioned `OperationRequest` /
`OperationResult` contract and routes through the one `RuntimeSupervisor`:

| Surface | Entry point |
|---|---|
| CLI commands | `ComTool.Cli.exe <command>` (use `--runtime`) |
| Local IPC | `ComTool.RuntimeHost.exe` (current-user named pipe, default) |
| JSON/stdio (NDJSON) | `ComTool.RuntimeHost.exe --stdio`, or `ComTool.Cli.exe stdio` as a pipe-backed proxy |
| MCP (optional) | `ComTool.Transport.Mcp.exe --server` |

NDJSON is correlation-based, not response-order-based. `NdjsonServer` admits a bounded set of concurrent requests (32 by default) and serializes only stdout writes. `ComTool.Cli.exe stdio` backs those concurrent frames with `RuntimePipeClientPool`: sequential requests reuse idle runtime-pipe connections, while overlapping requests use independent pipe instances. This is a control-plane safety property, not merely throughput optimization—a `core.target.host.terminate` recovery request must be able to reach the RuntimeSupervisor while another request is blocked on an execution connection.

MCP is a thin adapter: its one tool builds a real `OperationRequest` and forwards
it over the ordinary runtime pipe. It contains no Adobe/host logic and never
becomes the architecture. Validate it with:

```powershell
ComTool.Transport.Mcp.exe --self-test --pipe <name>
```

See `docs/TRANSPORT_CLI_MCP_PARITY.md` for the parity matrix, runtime/worker
discovery rules, and state-directory layout.

The comprehensive governing plan remains:

`../agent-skills/illustrator-com-automation-skill/comtool/GREENFIELD_ARCHITECTURE_AUDIT_AND_EXECUTION_PLAN.md`
Important V2 documents:

- `PHASE0_STATUS.md` — evidence-backed architecture gate ledger.
- `docs/LEGACY_LESSONS_AND_V2_PRINCIPLES.md` — binding successor principles derived from the old tool.
- `docs/ADR-0001-bootstrap-boundaries.md` — isolation from the legacy implementation.
- `docs/ADR-0002-runtime-topology.md` — process/transport topology.
- `docs/ADR-0003-mutation-safety-and-script-runtime.md` — durable mutation, leases, and ESON-backed script execution.
- `docs/TRANSPORT_CLI_MCP_PARITY.md` — how CLI, stdio, local IPC, and MCP share one operation protocol; runtime/worker discovery; state layout.
- `docs/RELEASE_READINESS.md` — packaging, clean-machine checklist, and what still needs a live host.

## Pure Node programmatic surface

`sdk/node/` is a dependency-free Node.js client for the same persistent V2 runtime. Its primary API is generic: `ComToolClient.execute(operation, input, options)` sends ordinary protocol-v1 requests over `ComTool.Cli.exe stdio`, which forwards to the existing RuntimeHost pipe. Concurrent calls may complete out of submission order and are correlated strictly by request ID; the proxy can use separate pooled runtime-pipe connections for overlapping calls, so a control/recovery request is not queued behind a wedged execution request. There is no Node-side Adobe/COM implementation, so future ExtendScript, native-plugin/debugger, inventory, telemetry, or agent-facing operations become usable without another transport architecture.

`ComToolRunner.openSession()` binds one strong target generation to a generic `ComToolTargetSession`, so long-lived Node/agent programs can call arbitrary current or future operations without repeatedly threading target metadata. A session may explicitly bind/acquire/renew/release a lease; it never auto-releases one after ambiguous work. `runFile()` / `testFile()` and `runEval()` / `testEval()` remain convenience orchestration on top of the same generic client and target-session machinery. Hot harnesses can therefore discover once, lease once, and reuse the exact strong generation while RuntimeHost remains authoritative on every request.

The script worker watchdog is caller-controlled end-to-end through `policy.workerWatchdogMs` / `watchdogMs` (100 ms through 3,600,000 ms). Separately, Node callers may use `responseTimeoutMs` or an `AbortSignal` to stop waiting locally; after submission that is conservatively ambiguous and does not cancel or replay Adobe work. The runner sizes an owned lease to cover the selected watchdog plus recovery grace, and retains it for ambiguous outcomes.

If Illustrator continues executing after the worker watchdog has killed its COM worker, `core.target.host.terminate` provides explicit break-glass recovery. It bypasses the blocked COM/operation lane, requires the same active lease, and revalidates the already-known exact PID + process-start generation before terminating anything. It performs no fresh COM discovery and never retries the script. The Node helper is `terminateHostGeneration()`; CLI `--terminate-host-on-ambiguous` is explicit opt-in only.

See `sdk/node/README.md` for API examples and machine-readable run/test semantics.

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
- `core.operations.list` (live authoritative operation catalog)
- `core.operation.describe` (one operation's runtime-owned safety/dispatch semantics)
- `core.operation.examples` (machine-readable usage examples)
- `core.artifact.describe`
- `core.artifact.read` (bounded opaque-artifact range reads; no filesystem paths)
- `knowledge.describe`
- `knowledge.search`
- `knowledge.symbol`
- `knowledge.enum`
- `script.validate` (read-only advisory ES3 preflight)
- `watch.condition`
- `core.incidents.list`
- `core.incident.resolve` (offline/dead-generation incident resolution)
- `core.targets.list`
- `core.target.capabilities`
- `core.target.status`
- `core.target.snapshot`
- `core.target.reconcile`
- `core.target.lease.acquire`
- `core.target.lease.renew`
- `core.target.lease.release`
- `core.target.host.terminate` (generation-pinned break-glass recovery)
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
- `script.runFile` (absolute `.jsx`/`.jsxbin` path + required SHA-256 pin)

Controlled mutation surface:

- `illustrator.artboard.setName` (`idempotent_write`)
- `illustrator.artboard.setRect` (`idempotent_write`)
- `illustrator.layer.setName` (`idempotent_write`)
- `illustrator.layer.setVisible` (`idempotent_write`)
- `illustrator.layer.setLocked` (`idempotent_write`)
- `illustrator.layer.setOpacity` (`idempotent_write`)
- `illustrator.action.run` (`external_side_effect`)
- `illustrator.menu.execute` (`external_side_effect`)

The typed layer/artboard operations require explicit document/object selectors,
reject ambiguous name matches before mutation, and never accept a caller-supplied
COM path/member. They provide a narrow high-value alternative to the generic COM
mutation surface while retaining the same supervisor lease/journal/ambiguity
semantics.

Native plug-in RPC surface:

- `plugin.message` — bounded `Application.SendScriptMessage(plugin, selector, input)` data RPC to native `.aip` plug-ins. V2 conservatively fixes it as `external_side_effect`, requires the target lease, never retries after possible dispatch, and records input/response UTF-8 byte counts plus SHA-256 provenance without echoing the input payload. The plug-in/selector contract is plug-in-owned; narrower future operations may declare stronger semantics only when the runtime owns and verifies that contract.
- `plugin.debug.diagnostics` / `plugin.debug.control` — typed AIPDebug operations with a COM bootstrap/control lane and an optional direct VectorIPC diagnostic lane. Direct IPC is permitted only after the worker discovers endpoint provenance through the exact Illustrator generation; the packaged `aipdebugctl.exe` is runtime-rehashed against `release-manifest.json`, then verifies the named-pipe server PID and process-start generation again. A packaged helper cannot be replaced by `COMTOOL_AIPDEBUGCTL`; that override is unpackaged-development-only. Caller-supplied endpoints cannot replace runtime-discovered provenance, and effectful control never silently falls back across transports.

ExtendScript debugger surface:

- `debug.session.open` / `debug.session.command` / `debug.session.close` — a real Adobe ExtendScript Debugger (`estk3`) session. The session is owned by the target worker and scoped to one strong target generation, so it cannot outlive or drift onto a different Illustrator process. Commands cover `eval`, `set-breakpoints` / `get-breakpoints`, `get-break` / `get-frame` / `set-frame` / `get-properties`, and `break` / `continue` / `halt` / `stepover` / `stepinto` / `stepout`; this is a distinct debugger facet and is never faked as generic `script.eval`. The worker pins the exact native debugger addon bytes before the bridge child exists and the bridge re-hashes them immediately before `require()`, so replaced addon bytes fail closed instead of loading different code than the reported provenance. All three operations are `external_side_effect`, require the target lease, and are never replayed. `sdk/node` exposes this as `ComToolTargetSession.openDebugger()` returning a `ComToolDebugSession`; the Node side is orchestration only and contains no Adobe or debugger implementation.

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

`script.eval` and `script.runFile` require an exclusive target lease and a caller-stable request ID. Their `effects` field may declare a write class, but arbitrary source/file execution may **not** declare `read_only`; omitted effects default to `unknown`. `script.runFile` additionally requires the caller's SHA-256 of the exact file bytes, which the host adapter verifies again immediately before execution.

Every controlled-mutation and document-lifecycle operation is registered in the runtime-owned catalog with a `Fixed` mutation class and `RequiresLease: true`. A caller cannot weaken the runtime's safety floor: the resolver returns the catalog's class verbatim and ignores any caller-declared `effects`. Typed Illustrator property operations own their target member and selector grammar; callers cannot substitute a COM member/path. The lower-level `com.set` / `com.call` surfaces remain explicitly classified external-side-effect escape hatches rather than the contract for typed agent automation. Structured script values are transported as ESON data, never concatenated into script text.

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
Wave 3 currently runs steps sequentially. That is also the required host-execution
baseline: one strong Illustrator target has one serialized Adobe execution
resource. A future DAG may overlap runtime-only preparation or work on genuinely
different target generations, but it must never turn independent graph nodes into
parallel execution against the same Illustrator target. A narrower future
optimization may compile deliberately compatible logical steps into one
ExtendScript dispatch; that is batching, not parallelism or transactional
rollback. See `docs/WORKFLOW_CONCURRENCY_MODEL.md`.

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

`script.runFile` provides explicit directive-aware JSX file routing under the
same runtime-owned mutation/lease policy as `script.eval`. The former Wave 4
backlog has materially landed: action/menu dispatch, advisory ES3 validation,
condition watching, embedded COM knowledge search/signature/enum lookup, opaque
artifact retrieval, and the first richer typed Illustrator mutations are all
present in the live operation catalog.

## Opaque artifacts and large results

`core.artifact.describe` and `core.artifact.read` expose immutable artifacts by
opaque runtime ID. Callers never receive a state-directory filesystem path.
`core.artifact.read` supports bounded byte ranges and returns base64 data with
offset/length/hash metadata.

Public pipe/NDJSON and the authenticated runtime↔worker broker all retain the
same 1 MiB frame ceiling. When a successful host result reaches the 32 KiB
artifact threshold, the worker serializes only that result payload and streams
it as correlated 512 KiB raw chunks whose base64 broker frames remain below
1 MiB. The sole supervisor validates order, byte count, result kind, and
SHA-256, reconstructs the canonical result, durably records it when mutation
semantics require that, then commits it through the runtime-owned artifact store
and replaces the outward result with a small opaque envelope. The worker never
writes artifact-store files and a payload beyond the 64 MiB artifact ceiling
fails explicitly with completed execution truth instead of being replayed or
truncated.

## Installation and upgrades

The per-user installer keeps immutable side-by-side versions under
`%LOCALAPPDATA%\Programs\ComToolV2\versions\` and exposes the active
version through the stable `%LOCALAPPDATA%\Programs\ComToolV2\current\`
junction. Configure integrations against `current\ComTool.Cli.exe`,
`current\ComTool.RuntimeHost.exe`, or `current\ComTool.Transport.Mcp.exe`
rather than a version-specific path. Upgrades verify the new payload before
switching the stable path; uninstalling the current version rolls back to the
newest remaining verified version.

Durable state is separate at `%LOCALAPPDATA%\ComToolV2`. Only one runtime
process may own a given state root at a time; custom-pipe/shadow runtimes must
use distinct `--state-dir` values. `core.targets.list` exposes any durable
`activeIncident` descriptor so a restarted agent can recover the request ID
and reconciliation fingerprint needed to resolve an ambiguous mutation.

## Verification

The current deterministic gate is **833/833 passing**. It covers protocol,
runtime/catalog policy, supervisor and durable reconciliation, Illustrator COM
and document-operation adapters (including debugger, VectorIPC, generic COM,
typed-mutation selector/ambiguity semantics), migration/parity, embedded
knowledge validation, length-prefixed pipe, NDJSON stdio, runtime IPC, and the
MCP bridge. The pure Node SDK gate is separately **20/20 passing**. The schema
gate additionally validates **41/41** protocol/schema cases across the current
V1 and draft surfaces, including the live operation-registry snapshot. Run the
deterministic test suite with:

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

The typed layer/artboard mutations have deterministic adapter tests but have not
been dispatched against user artwork. Live mutation conformance must use a
disposable document fixture and verify exact identity and postconditions.

See `evidence/` for captured results.
