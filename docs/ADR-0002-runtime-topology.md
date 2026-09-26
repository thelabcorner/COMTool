# ADR-0002 — Runtime Topology and Transport Ownership

**Status:** Accepted  
**Date:** 2026-09-24

## Context

Phase 0 proved that modern C#/.NET can own Illustrator COM safely from an explicit STA, that a supervisor/worker topology can enforce serialized per-target execution and ambiguity-aware recovery, and that CLI, stdio, local IPC, and MCP can all translate into one transport-neutral operation model.

The existing Python COM Tool remains the behavioral oracle. V2 remains isolated under `comtool-v2/`.

## Decision

### 1. The persistent runtime is the preferred production topology

The long-lived topology is:

```text
agent/client
    │
    │ OperationRequest / OperationResult
    ▼
current-user local runtime pipe
    │
    ▼
RuntimeSupervisor
    │
    ├── short-lived discovery worker (STA)
    │
    └── persistent per-target worker (STA)
             │
             ▼
        Adobe host API
```

The runtime process itself does **not** acquire Illustrator COM proxies.

### 2. Direct and one-shot broker modes remain available

They are useful for:

- recovery,
- diagnostics,
- differential tests,
- environments where a persistent runtime is undesirable,
- debugging the runtime itself.

They are not separate semantic APIs.

### 3. Runtime-scoped and host-scoped operations share one catalog

Examples:

- runtime-scoped: `core.runtime.health`, `core.targets.list`, `core.target.capabilities`, `core.target.reconcile`
- host-scoped: `core.target.status`, `core.target.snapshot`

Host workers independently reject runtime-scoped operations.

### 4. Target identity is opaque and generation-specific

Clients must not parse target IDs for meaning. Strong target identity includes process identity/generation information in the adapter descriptor. A restarted Adobe process is a new target generation, even when the application family and ProgID are unchanged.

### 5. Discovery is cached but never used to silently retarget

The runtime caches discovery results for a bounded window to avoid launching a discovery worker on every agent call.

A stale target ID may fail or require refresh. It must **never** be transparently redirected to a new host generation, especially for mutations.

Unknown/non-running target execution triggers real discovery before proceeding.

### 6. Execution ambiguity outranks availability

If a mutation outcome is unknown and the host subsequently disappears, `reconciliation_required` remains authoritative. Host loss cannot erase an unresolved mutation incident.

Mutation state is durably write-ahead journaled by request ID, so runtime-process loss also cannot erase the incident. Completed mutations may replay their stored result without re-executing the host; prepared/ambiguous mutations remain stopped. See ADR-0003.

### 7. Public local IPC is OS-user bounded

The runtime pipe is:

- local only,
- Windows named pipe,
- `CurrentUserOnly`,
- length-prefixed,
- size-bounded,
- strict request/response correlation.

No unauthenticated network listener is part of V2.

The internal supervisor→worker pipe additionally uses an ephemeral 256-bit bootstrap token and verifies child PID + creation time.

### 8. Mutation lease ownership is process-global

A runtime-local cryptographic lease token is paired with an OS-held lock keyed by strong target ID. Separate runtime processes therefore cannot both acquire mutation ownership of the same Adobe target generation.

The kernel releases ownership when the runtime dies; a TTL timer releases it when an otherwise-idle lease expires.

### 9. Development runtime deployments are immutable shadows

Do not execute a long-lived development runtime directly from `bin/Release`.

Use `scripts/stage-runtime-dev.ps1` to copy runtime + worker artifacts into a unique `.artifacts/dev-runtime/<timestamp>/` deployment. This prevents Windows DLL locks from breaking subsequent builds.

Production installers/releases will similarly execute installed artifacts rather than compiler output.

## Evidence

Phase 0:

- 0B–0F: PASS.
- 0A: all required live behavior except an intentional real-host restart/reconnect exercise.

Production-path evidence:

- authenticated target worker passed live Illustrator status/snapshot.
- 50 concurrent callers serialized through one STA worker.
- worker identity changed across explicit restart while target identity remained stable.
- injected mutation ambiguity survived worker restart and required explicit reconciliation.
- persistent runtime retained one warm worker.
- first-touch runtime status paid worker startup; warm reads fell to roughly 65–100 ms end-to-end in live probes.
- cached target listing measured 0.316 ms inside the runtime after initial discovery.
- a second runtime instance exits immediately rather than competing for the same session.
- full source build and deterministic tests pass while a shadow-deployed runtime is actively running.
- completed ESON-backed script result replayed after a hard runtime/worker crash with no target worker launched.
- two independent runtimes contending for the same target returned `target_leased_external`; hard-killing the owner allowed immediate takeover without waiting for TTL.

## Consequences

- Host adapters remain independently testable.
- MCP remains a thin transport rather than an architectural dependency.
- COM apartment ownership is explicit.
- hard watchdogs can kill an owned worker without killing the runtime.
- worker replacement cannot launder target ambiguity.
- adding Photoshop, After Effects, InDesign, or Premiere adapters does not require changing the public operation protocol merely because their host mechanisms differ.
