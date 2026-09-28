# Legacy Contract Extraction & V2 Migration Matrix

**Status:** Phase 1 living specification  
**Date:** 2026-09-24  
**Source:** existing Python COM Tool, current command reference, legacy audit, live Phase 0 probes

The goal is not bug-for-bug compatibility. Each legacy behavior is classified as **preserve**, **redesign**, **deprecate**, or **drop**.

| Legacy surface | Valuable semantics | Defect / accidental behavior not to preserve | V2 destination | Safety class | Priority |
|---|---|---|---|---|---|
| `status` | host/version/build/doc/UI-state inspection | nested reads bypassed retry in places | `core.target.status` + host status facet | read_only | Illustrator Wave A |
| `snapshot` | active document + selection state fingerprint | inner failure could be wrapped as outer success | `core.target.snapshot` | read_only | Wave A |
| `get <path>` | generic COM dotted-path read | empty path segments silently collapse; dynamic summaries can hide failures | `illustrator.com.get` / generic COM facet | read_only | Wave A |
| `set <path>` | generic direct COM property mutation | mutation lacked first-class safety metadata/postconditions | `illustrator.com.set` | conditional_write | Wave B |
| `call <path>` | direct COM method invocation | arbitrary methods treated too uniformly | `illustrator.com.call` with operation metadata | unknown / declared | Wave A/B |
| `eval --expr` | low-friction JSX expression execution | result decoding could change JSON-looking strings into other types | `script.eval` + ESON structured transport + durable request ledger | declared write class or `unknown`; arbitrary source cannot claim read-only | Wave A — implemented/live |
| `eval --code` | arbitrary ExtendScript execution | single-expression auto-return heuristics are CLI-specific | `script.eval` with explicit mode, lease, stable request ID, ESON envelope | declared write class or `unknown` | Wave A — implemented/live |
| `eval --file` | directive-aware `$.evalFile` routing | route semantics were implicit; injected vs evalFile behavior differed | `script.runFile` with explicit route metadata | unknown unless declared | Wave C — implemented/deterministic |
| `--args-json` | structured argument transport | wrapper and evalFile use different argument mechanisms | tagged protocol values + route-specific binding | n/a | Wave C |
| ES3 preflight | catches common unsupported syntax before COM | regex-literal blind; static checker is not authoritative | advisory `script.validate` capability | read_only | Wave C |
| JSX error excerpts | actionable line/context diagnostics | wrapper/source remapping complexity | typed script error + source span | read_only diagnostic | Wave A |
| ESON/JSON shared bootstrap | strict, type-faithful structured transport in ExtendScript | legacy bootstrap mixed global JSON state and optional acceleration concerns | hash-pinned ESON runtime installed once into COM Tool-owned engine namespace | host_global_state | Wave A — implemented/live |
| ESPACK acceleration | native acceleration for ESON parse-heavy workloads | native bootstrap cost is not justified for tiny high-frequency `script.eval` envelopes | optional adapter optimization retained for measured parse-heavy operations; slim ESON runtime is current eval path | host_global_state | measured/optional |
| `open` | explicit document open | active-document assumptions can redirect later mutation | `document.open` returning document identity | document_lifecycle | Wave B |
| `close --save` | document lifecycle | Boolean was passed where `AiSaveOptions` enum required | `document.close {policy: save|discard|prompt|reject_if_dirty}` | document_lifecycle | Wave B |
| action runner | load/run/poll/restore workflow | `UserInteractionLevel` restoration could leak; timeout semantics too strong | `illustrator.action.run` with capture/restore verification | external_side_effect | Wave C |
| menu commands | fallback for Illustrator commands | version-sensitive strings / weaker semantics | host-specific capability, never generic core | external_side_effect | later |
| `batch` | amortizes COM connection/round-trip overhead | continued after prerequisite failure by default | `workflow.run`, fail-fast default | mixed | Phase 5 |
| persisted `queue` | durable deferred execution | at-least-once replay can duplicate mutations | durable `job` with ambiguity-aware recovery | mixed | Phase 5 |
| queue resume | persistence between invocations | “resume-safe” overclaimed exactly-once behavior | job state machine + reconciliation | mixed | Phase 5 |
| `--stay` stdin chain | one session/lock for multi-step operations | process-local lifetime exposed confusingly | brokered target lease / direct stdio session | mixed | replaced |
| named sessions | durable agent-owned state | filename sanitization could alias names; file writes lacked CAS | versioned session records with exact IDs + CAS | metadata | Phase 5 |
| `task run/status/wait` | asynchronous work | PID-only ownership; racey state transitions; running state drift; wrong wait exit semantics | supervisor-owned jobs/process handles | mixed | Phase 5 |
| task cancel | cancel background child | stale PID reuse could kill unrelated process | owned worker/job handle + process creation identity | process_control | Phase 5 |
| `watch` | poll until condition/change | bool/number equality conflated; provider call could exceed deadline | `subscription.poll` / `condition.watch` with JSON-type equality | read_only | Wave C |
| cross-agent mutex | prevents Illustrator mutation interleaving | global metadata race; abandoned lock did not imply document consistency | per-target scheduler + cryptographic TTL lease + process-global OS target lock + reconciliation state | synchronization | Runtime — implemented/live |
| retry HRESULTs | targeted transient retry | timeout/retry budget conflated; backoff could overshoot | typed error taxonomy + retry budget | n/a | Runtime |
| operation timeout | bounded waiting intent | synchronous COM call was not actually preemptible | soft timeout + worker watchdog | n/a | Runtime |
| abandoned mutex handling | deadlock recovery | called “crash-safe” despite possible partial mutation | worker loss ⇒ `reconciliation_required` | n/a | Runtime |
| large result offload | avoids giant result payloads | character count mislabeled as bytes | explicit artifact descriptor with real byte count/hash | read_only metadata | Protocol |
| result envelope | one machine-readable result per command | transport/operation/host/postcondition status could be conflated | versioned `OperationResult` | n/a | Protocol |
| HRESULT suggestions | agent-friendly recovery hints | ad hoc string hints | structured `error.kind/retryable/execution/suggestedActions` | n/a | Protocol |
| COM inventory DB | searchable signatures/enums/types | Illustrator-specific storage shape | versioned host knowledge pack | read_only | Phase 6 |
| `com_lookup.py search` | targeted symbol discovery avoids context bloat | separate script/tool surface | `inspect.search` | read_only | Phase 6 |
| `com_lookup.py signature` | exact parameter/enum lookup | interface naming quirks | `inspect.symbol` | read_only | Phase 6 |
| `--examples` | discoverable copy-paste recipes | documentation scraping tied CLI/docs together | self-describing operation examples | read_only | Phase 6 |
| `debug attach` | real ExtendScript debugger transport | one process per invocation undermined session continuity | `debug.session.open` — worker-owned, generation-scoped session handle over the Adobe ExtendScript Debugger (`estk3`) | debugging | Wave D — implemented/live |
| debug breakpoint APIs | persistent host breakpoint state | independent debugger clients can collide | `debug.session.command` `set-breakpoints` / `get-breakpoints`, bound to the worker's own lease-bound session so independent clients cannot collide | debugging | Wave D — implemented/live |
| debug eval/step | break/inspect/continue automation | REPL semantics differ from COM eval | `debug.session.command` `eval` / `get-break` / `get-frame` / `set-frame` / `get-properties` / `break` / `continue` / `halt` / `stepover` / `stepinto` / `stepout`, a distinct debugger facet that is never faked as generic `script.eval` | debugging | Wave D — implemented/live |
| ESD Node bridge | proven transport to Adobe debugger core | Node dependency hidden inside Python command | retained behind the host adapter: an embedded, SHA-pinned bridge asset plus the exact native addon bytes, spawned only by the target worker | debugging | Wave D — implemented/live |
| `SendScriptMessage` | direct native plug-in messaging | plugin contracts were bespoke | `plugin.message` generic bounded RPC; target generation + lease + no-replay + provenance remain runtime-owned | external_side_effect | Wave E — generic bridge implemented |
| AIPDebug integration | reusable native plugin diagnostics | must not mix debug/product endpoint semantics | dedicated plugin debug facet | debugging | Wave E |
| VectorIPC | efficient native/helper transport | should not be mandatory for basic automation | optional native transport facet | transport | Wave E |
| launch/attach default | convenient auto-connect | docs/default semantics drifted | explicit target lifecycle policy | lifecycle | Runtime |
| persisted JSON records | simple durable state | fixed temp names, no multi-writer safety, corruption conflated with missing | versioned store + unique temp/CAS/locks | metadata | Phase 5 |
| validator/tests | useful pure helper coverage | actual comtool implementation under-validated | protocol + fake-host + live-host conformance suites | n/a | all phases |

## Compatibility rules

### Preserve exactly when valuable

Preserve:

- machine-readable structured output;
- targeted HRESULT/error knowledge;
- explicit Illustrator COM inventory/search capability;
- one-target-at-a-time execution baseline;
- dense script/workflow execution to reduce cross-process round trips;
- postcondition-first automation philosophy;
- ESD debugger functionality;
- plugin-message and VectorIPC interoperability.

### Intentionally incompatible

V2 intentionally changes:

- mutation replay after ambiguous failure;
- close/save policy representation;
- workflow failure defaults;
- process/task ownership semantics;
- result type encoding;
- timeout terminology;
- lock/crash semantics;
- target identity;
- debugger session lifetime;
- persisted state schemas.

These incompatibilities are safety/correctness improvements and must not be “fixed” back toward legacy behavior for superficial parity.

## Differential oracle strategy

For operations whose semantics should match, tests invoke both executables externally against controlled fixtures and compare normalized meaning rather than raw JSON shape.

Examples:

- status version/document count;
- COM property get;
- read-only method call;
- simple JSX expression;
- open disposable fixture;
- action result on controlled disposable fixture.

For intentional semantic changes, a migration delta must state why outputs differ.
