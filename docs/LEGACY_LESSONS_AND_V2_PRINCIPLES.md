# V2 Design Principles Seeded from Legacy COM Tool Findings

**Status:** Binding V2 architecture guidance  
**Date:** 2026-09-24  
**Purpose:** Convert accumulated legacy-tool bugs, audit findings, live-engine discoveries, and recovery lessons into positive V2 design principles.

This document is intentionally about **principles**, not bug-for-bug migration. The existing tool remains the behavioral oracle, but V2 must not preserve unsafe or misleading semantics simply for compatibility.

---

## 1. Host automation is stateful, UI-bound, and failure-prone

Treat every Adobe desktop host as a stateful GUI application, not a headless RPC server.

Implications:

- modal UI can reject or delay calls,
- focus and active-document state can change independently,
- scripts/actions can partially mutate state before failure,
- host crashes invalidate proxies,
- synchronous calls can block beyond local timeout bookkeeping,
- successful transport return does not prove the intended mutation occurred.

V2 therefore separates:

```text
transport success
operation success
postcondition success
target-state confidence
```

These are never conflated.

---

## 2. One target, one execution lane by default

The legacy named mutex correctly recognized that Illustrator automation is effectively serialized.

V2 generalizes this to **per-target schedulers**:

- one mutation lane per target instance,
- reads are not assumed safe to parallelize,
- separate Adobe host instances may execute independently,
- multi-step workflows can acquire a target lease,
- the runtime must never interleave dependent mutation sequences from different agents.

A target lease is orchestration-level exclusivity, not an ACID transaction.

---

## 3. COM apartment ownership must be explicit and balanced

Legacy issue:

- repeated `CoInitializeEx` calls were tracked by a Boolean,
- `S_FALSE` and reconnect accounting could become unbalanced,
- `RPC_E_CHANGED_MODE` could be swallowed,
- reconnecting proxies could accidentally reinitialize the apartment.

V2 rule:

> A COM worker owns exactly one apartment lifecycle on exactly one worker thread.

The worker:

1. initializes STA once,
2. records the initialization result,
3. creates/attaches proxies only from that owner thread,
4. reconnects proxies without reinitializing the apartment,
5. uninitializes exactly once during worker teardown.

Apartment-mode mismatch is a typed fatal worker error, never silently ignored.

---

## 4. Dead proxy recovery and host restart are first-class

A COM proxy that survives an Illustrator crash is not trustworthy.

V2 target identity includes at minimum:

- host family,
- process ID,
- process creation time,
- executable identity/path where available,
- adapter version,
- host version,
- COM/BridgeTalk/other endpoint identity.

PID alone is never sufficient.

A target restart invalidates:

- cached COM proxies,
- debugger session handles,
- script-engine assumptions,
- per-target generation/fingerprint,
- in-flight mutation certainty.

The target moves through explicit lifecycle states rather than being invisibly reattached.

---

## 5. Retry only known transient failures

Legacy HRESULT handling was one of the stronger components and should be retained conceptually.

V2 distinguishes:

```text
retryable transport/host busy
semantic/API failure
script/runtime failure
ambiguous execution
worker failure
target unavailable
```

Retry policy is keyed to typed error classes/HRESULTs, not generic exception retry.

Backoff must be bounded by a real retry budget.

---

## 6. A timeout is not proof of non-execution

The legacy `--timeout` could only inspect elapsed time after a synchronous COM call returned or threw. It was therefore not a hard operation deadline.

V2 uses separate concepts:

- `queueTimeout`
- `retryBudget`
- `operationSoftTimeout`
- `workerWatchdog`

If a worker is killed while a mutating host call is in flight:

```text
targetState = reconciliation_required
```

The runtime must never report:

```text
"timed out" == "did not execute"
```

unless that is actually provable.

---

## 7. Ambiguous mutation failure must stop automatic replay

Legacy persisted queues were effectively **at-least-once**:

1. host mutation succeeds,
2. process dies before persistence,
3. entry still looks pending,
4. resume repeats the mutation.

V2 rule:

> Non-idempotent or lifecycle-changing mutations are never automatically replayed after ambiguous failure.

Replay is allowed only when:

- the operation is explicitly idempotent, or
- a precondition/idempotency key proves replay safety, or
- reconciliation determines the mutation did not occur, or
- the caller explicitly authorizes replay.

---

## 8. Dependent workflows fail fast by default

Legacy batch/queue semantics continued after step failures, which could redirect later mutations onto the wrong active document/state.

V2 workflow default:

```text
onError = stop
```

Continue-on-error is allowed only when explicitly requested for independent diagnostic/probe steps.

Every stateful mutation workflow should support guards such as:

- active document identity,
- document generation/fingerprint,
- selection count/type,
- saved/dirty state,
- output-file existence,
- required capability,
- expected host version/state.

---

## 9. Destructive choices must be explicit

Legacy bug: `Document.Close` accepted a Boolean even though Illustrator expects `AiSaveOptions`.

V2 rule:

Never model destructive policies as ambiguous booleans.

Prefer explicit enums/verbs:

```text
save
discard
prompt
reject_if_dirty
```

The protocol must validate the exact domain before a host call occurs.

---

## 10. State restoration is part of operation correctness

Legacy action execution could change `UserInteractionLevel` and fail to restore it.

V2 operations that modify host-global/session-global state must model:

```text
capture
mutate
execute
restore
verify restoration
```

Restoration failure is not swallowed. It becomes a structured warning/error and may place the target into `reconciliation_required`.

The same principle applies to:

- user-interaction mode,
- active document,
- active artboard/layer when relevant,
- temporary actions,
- temporary preferences,
- debugger state,
- temporary plugin/debug settings.

---

## 11. Result transport must be type-faithful

Legacy JSX decoding could reinterpret strings like:

```text
"true"
"123"
"null"
"{\"x\":1}"
```

as JSON values of another type.

V2 never infers result type from string content.

Use a tagged value envelope or schema-known value encoding.

Examples:

```json
{"kind":"string","value":"true"}
{"kind":"number","value":123}
{"kind":"boolean","value":true}
{"kind":"null"}
{"kind":"object","value":{"x":1}}
```

Large results use explicit artifact references rather than unreliable giant COM strings.

---

## 12. Script execution routes are different capabilities

Legacy experience showed that:

- injected code,
- `$.evalFile`,
- files containing `#target` / `#include`,
- debugger REPL-style eval,
- BridgeTalk,
- UXP execution,

have different semantics.

V2 must not collapse them into one fake universal "eval".

The operation protocol can share a common script intent, but the adapter must report the exact route/capability used.

---

## 13. Static compatibility checking is advisory, not authoritative

Legacy ES3 preflight caught many errors but also had parser blind spots such as regex literals and reserved-word edge cases.

V2 principle:

- static checks are useful fast filters,
- live-engine validation remains authoritative,
- the checker must identify its grammar/version,
- false-positive/false-negative risks are explicit,
- no static checker may claim stronger compatibility than it proves.

---

## 14. Host API knowledge must be versioned and queryable

Legacy type-library indexing was strategically valuable.

V2 knowledge packs must be:

- host-specific,
- version-aware,
- searchable without loading giant schemas,
- explicit about source/provenance,
- capable of distinguishing COM DOM vs ExtendScript DOM vs UXP DOM,
- able to surface enum domains and exact signatures.

Do not propagate stale cross-surface assumptions.

Example discovered during Gate 0A:

```text
ExtendScript-style PathItem.remove()
!=
Illustrator COM PathItem.Delete()
```

V2 knowledge/discovery should prevent this class of mistake.

---

## 15. COM is command plane, not assumed event plane

Prior research found no rich Office-style outgoing event model in the supplied Illustrator type library.

V2 therefore treats events as capability-specific:

- polling/watch,
- CEP/UXP lifecycle/event mechanisms,
- native plugin notifiers,
- host-specific callbacks,
- BridgeTalk where appropriate.

Do not invent event semantics on top of COM.

---

## 16. Plugin integration must respect Illustrator's main thread

For native `.aip` plugin integration:

- VectorIPC/background transport threads may receive messages,
- but Illustrator SDK suites that require the host/UI thread must be marshaled/queued to Illustrator's main thread,
- debugging/control APIs must never call host suites directly from arbitrary IPC threads.

The reusable plugin bridge should make the correct threading model the default.

---

## 17. Debugger state is a real session

Legacy ESD integration proved that debugging is a distinct transport with persistent/suspended execution semantics.

V2 debugger abstractions must model:

- attached target/engine identity,
- suspended vs running state,
- persistent breakpoints,
- frame/stack context,
- resume/step transitions,
- debugger-client ownership,
- host restart invalidation.

Do not model debugging as a stateless `eval` subcommand.

---

## 18. Persistence requires generations/CAS, not atomic replace alone

Legacy tasks/queues/sessions used atomic file replacement but remained vulnerable to lost updates and multi-writer races.

V2 durable records include:

- schema version,
- generation/revision,
- owner identity,
- process identity,
- created/updated timestamps,
- operation/workflow hash where useful.

State transitions use compare-and-swap semantics or equivalent locking.

Corrupt state is distinguished from missing state and preserved for diagnostics.

---

## 19. Cancellation requires ownership proof

Legacy task cancellation trusted a persisted PID and could theoretically kill an unrelated process after PID reuse.

V2 never terminates a process from PID alone.

Worker/job identity must include:

- PID,
- process creation time,
- executable identity,
- runtime-issued token/generation.

Where practical, use Windows Job Objects/owned handles for child lifecycle.

---

## 20. State machines must match documentation

Legacy drift included:

- advertised `queued → running → ...` while `running` was not persisted,
- `task wait` success envelopes hiding child failure,
- `--stay` wording implying process persistence that did not exist.

V2 rule:

> Documented states, exit statuses, and transitions are executable contract.

Tests must verify:

- every documented state is reachable,
- invalid transitions fail,
- terminal result semantics propagate to transports,
- generated docs/schemas derive from the same operation registry where possible.

---

## 21. Cross-process synchronization needs owner-safe observability

Legacy lock metadata had:

- one metadata file shared across different ProgIDs,
- release/delete races,
- abandoned mutex semantics stronger than actual application-state safety.

V2 separates:

```text
scheduler authority
owner metadata
target state confidence
```

A worker/scheduler becoming available does not imply the Adobe document is consistent.

---

## 22. Multi-agent communication channels need strict framing

Prior IPC hardening work established reusable principles that apply directly to V2's named-pipe layer:

- one reader / one writer per logical channel,
- explicit frame length/version,
- bounded maximum message size,
- malformed/truncated frame rejection,
- connection poisoning after framing/time-out ambiguity,
- cancellation/leak tests,
- same-direction busy rejection,
- persistent overlapped-I/O state where used,
- stress tests over large message counts.

V2 should not invent a loose ad-hoc line protocol for the supervisor/worker boundary.

JSON/stdio may be newline-delimited for process-facing simplicity; named-pipe IPC should use a framed protocol.

---

## 23. Agent-facing errors must explain the next safe action

The legacy tool's structured envelopes and HRESULT suggestions were valuable.

V2 error objects should contain:

- stable error kind,
- host/native code,
- retryable flag,
- execution certainty,
- target-state confidence,
- safe suggested action,
- evidence/context,
- operation ID.

Example:

```json
{
  "kind": "host_call_ambiguous",
  "retryable": false,
  "execution": "unknown",
  "targetState": "reconciliation_required",
  "suggestedAction": "reconcile_target"
}
```

The runtime should make the safe next move discoverable without relying on a giant prompt.

---

## 24. Probe first, then mutate, then verify

Preserve the strongest legacy operating loop:

```text
discover
→ inspect exact API/signature
→ probe target state
→ establish preconditions
→ perform smallest required mutation
→ verify postcondition
→ restore temporary/global state
→ release transient references
```

For destructive-risk experimentation, use disposable or duplicated documents whenever possible.

---

## 25. V2 compatibility policy

Legacy behavior falls into four categories:

```text
PRESERVE      proven and desirable semantics
REDESIGN      same user intent, safer/new semantics
DEPRECATE     legacy surface retained temporarily through compatibility shim
DROP          accidental/unsafe behavior intentionally removed
```

Compatibility never overrides safety or truthfulness.

Every intentional semantic difference gets a migration note and regression fixture.
