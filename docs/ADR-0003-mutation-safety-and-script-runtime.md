# ADR-0003 — Mutation Safety and Script Runtime

**Status:** Accepted  
**Date:** 2026-09-24

## Context

V2 initially proved its process/COM topology with read-only operations. Enabling arbitrary ExtendScript changes the failure model: a script can mutate Illustrator before throwing, a worker can die after dispatch, a runtime can crash after host completion but before replying, and an agent can retry the same request after losing the response.

Those cases cannot be represented truthfully by ordinary transport retry semantics.

The legacy COM Tool also proved that ExtendScript needs a reliable JSON layer. V2 must preserve JSON type identity without maintaining another ad-hoc serializer.

## Decision

### 1. Runtime-owned mutation classification

Operation metadata, not caller optimism, owns the safety floor.

`script.eval` uses `DeclaredOrUnknown` mutation resolution:

- omitted `effects` ⇒ `unknown`;
- recognized write classes may be declared;
- arbitrary source **cannot** declare `read_only`.

Both supervisor and worker independently derive and verify the effective mutation class.

### 2. Mutating operations require a target lease

`script.eval` requires an explicit lease.

Lease ownership has two layers:

1. a cryptographically random runtime lease token with bounded TTL;
2. an OS-held per-target lock keyed by the strong target ID.

The OS lock is process-global. Two runtime processes cannot simultaneously own mutation leases for the same Adobe target generation. Process death releases the kernel handle automatically; TTL expiry releases it through the supervisor expiry timer.

Read-only operations do not require global ownership unless another caller already holds a lease.

### 3. Every mutating request has a durable idempotency key

For the CLI, `script.eval` requires `--request-id <stable-id>`.

The runtime fingerprints semantic request content independently of JSON object property order. The same request ID cannot be reused for different intent.

### 4. Mutations are write-ahead journaled

Before dispatch, the runtime durably records:

```text
prepared
```

Terminal transitions are:

```text
prepared → completed
         → not_started
         → ambiguous
```

The active per-target marker is written before worker dispatch.

If the runtime dies while the durable phase is still `prepared`, the next runtime treats the mutation as unresolved and rehydrates the target as `reconciliation_required`.

### 5. Completed requests replay from storage, never from Adobe

An identical retry of a `completed` request returns the stored `OperationResult`.

Replay is observable:

- `mutation.replay` evidence is appended;
- `executeMs = 0`;
- no target worker is launched merely to serve the replay.

A `not_started` request may retry. A `prepared` or `ambiguous` request is never blindly replayed.

### 6. Generic host liveness cannot resolve mutation ambiguity

A heartbeat proves availability, not outcome.

If a prior mutation is unresolved, generic `core.target.reconcile` always preserves `reconciliation_required`; a responding host is availability evidence only.

Read-only success, read-only failure, worker replacement, host loss, or runtime restart cannot launder an unresolved mutation into `known`.

The automatic operation-specific route is `core.target.mutation.reconcile`. It
requires an exclusive target lease, the active incident request ID, and the
current target-state revision. The caller must repeat the postconditions that
were durably included with the original mutation request. The mutation ledger
stores their canonical fingerprint at `prepared` time; newly attached or
changed conditions are rejected, and older records without a fingerprint cannot
use this automatic path. Each condition source must be a fixed, read-only host
operation. The runtime evaluates the matching conditions against live host
state and resolves the durable ledger record before transitioning the target
state, and only when the entire batch is verified and passing. If the original
request had no postconditions, an operator must use the separate explicit
`core.target.incident.resolve` decision path.

### 7. ESON is the canonical structured ExtendScript codec

The Illustrator adapter embeds the canonical sibling ESON runtime build as a resource and verifies its SHA-256 at build time.

Current pinned runtime SHA-256:

```text
31ee6046390589fabcc8a574c0b1fad50684912ae1d04682f026f95a9e7750fc
```

At runtime, V2 installs ESON once into a COM Tool-owned namespace on `$.global` and fingerprints the installed build. `script.eval` uses ESON for structured arguments and result/error envelopes.

The high-frequency eval path deliberately uses the slim runtime build. ESPACK/native ESON acceleration is reserved for operations whose measured workload is parse-heavy enough to justify the additional cold-start/native state.

### 8. Controlled Illustrator mutation and document lifecycle

The first non-script mutation is `illustrator.artboard.setName`. It is a fixed
runtime-catalog `idempotent_write`, requires the target lease, and accepts only
the runtime-owned `document.artboard.active.name` property key and a string
value. The caller cannot provide a COM member, path, collection index, or
arbitrary setter. The emitted ExtendScript uses ESON to parse the value as data.

The document surface adds `illustrator.document.read` (fixed read-only) and
`illustrator.document.create`, `.open`, `.save`, `.saveAs`, and `.close` (all
fixed `document_lifecycle` mutations requiring a lease). Lifecycle operations
validate a document selector and verify the resulting document identity/state
after dispatch; an unprovable outcome is ambiguous. `open` requires an absolute
path and refuses an already-open path. `close` requires exactly one explicit
policy: `save`, `discard`, or `reject_if_unsaved`; the last refuses dirty files,
and discard never has a default.

## Live evidence

Against Illustrator 30.6.0 on 2026-09-24:

- nested object result preserved string/number/boolean/null/array/object types;
- number, boolean, null, string, array, and structured-argument cases all passed live;
- warm small expressions measured about 0.9–1.1 ms host execution in the observed session;
- a completed structured-object request survived a hard runtime + worker crash and replayed after restart with `liveWorkers = 0`;
- two independent runtime processes contending for the same target produced `target_leased_external`;
- after hard-killing the owning runtime, the second runtime acquired the target lease roughly 300 ms later instead of waiting for the original TTL.
- the Wave B operation catalog and routing were exercised against a freshly staged runtime and live Illustrator 30.6.0; operation-specific reconciliation with a valid lease but no matching incident returned `no_reconciliation_incident` without changing target state.

The Wave B controlled mutations were validated with deterministic host-adapter
fakes. They were not dispatched against the user's real open document; live
mutation tests must use a disposable document and operation-specific
postconditions.

## Consequences

- Transport retry is no longer confused with exactly-once host execution.
- Agent retries can be safe when they preserve request identity.
- Runtime crashes cannot erase unresolved mutation state.
- Multiple runtime instances cannot create concurrent mutation owners for one target.
- ESON semantics are shared with the broader ES* ecosystem instead of duplicated in V2.
- The system remains conservative: arbitrary source is treated as potentially mutating even when a human believes it is observational.
