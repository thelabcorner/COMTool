# ADR-0002 — Phase 0 Architecture Selection

**Status:** Accepted with one retained validation item  
**Date:** 2026-09-24

## Decision

COM Tool V2 will proceed with:

- **C# on modern .NET** as the control-plane/runtime implementation language;
- a **self-contained Windows x64 executable** as the primary distribution target;
- a **runtime supervisor + isolated per-host worker processes**;
- **one serialized execution lane per target by default**;
- a **transport-neutral operation protocol** as the durable API;
- CLI and JSON/stdio as first-class executable surfaces;
- local named-pipe RPC for brokered operation;
- MCP as an optional translation adapter;
- capability-driven host adapters, with Illustrator as the primary reference host;
- Photoshop as the first second-host validation target;
- host-specific scripting/debug/native transports behind adapter facets rather than forced uniformity.

This decision does **not** select Native AOT. Initial packaging remains normal self-contained .NET because dynamic COM interoperability is a primary requirement.

## Evidence

### Gate 0A — Illustrator/.NET COM

Materially proven:

- process apartment is STA;
- active Illustrator ROT attachment works;
- direct COM reads work;
- Illustrator version/document-count reads work;
- `DoJavaScript` works;
- a disposable document can be created;
- a direct COM `PathItem` mutation can be performed and verified;
- the item can be deleted using the actual COM `Delete` surface;
- the disposable document can be explicitly closed with `aiDoNotSaveChanges = 2`;
- the original document count is restored;
- HRESULTs are surfaced structurally.

A transient `RPC_E_SERVERFAULT (0x80010105)` was observed from both the new C# spike and the legacy Python tool in the same Illustrator state. The host later recovered without V2-specific changes. This is evidence for typed transient/ambiguous host failure handling, not a C# regression.

Still pending:

- a real Illustrator host restart/reconnect cycle.

We deliberately do not terminate a user's active Illustrator instance solely to close a test checklist.

### Gate 0B — packaging

Self-contained single-file Windows x64 packaging passed.

Measured over 20 runs:

| Variant | Size | Median process startup | P95 |
|---|---:|---:|---:|
| baseline self-contained | 70.12 MiB | 63.53 ms | 72.50 ms |
| ReadyToRun | 79.37 MiB | 65.59 ms | 79.75 ms |

ReadyToRun was both larger and slower for this minimal executable, so it is **not** the default packaging strategy.

### Gate 0C — supervisor/worker topology

Passed:

- framed named-pipe protocol;
- explicit maximum frame size;
- authenticated child handshake;
- STA worker;
- 1,000 requests from concurrent callers serialized exactly once;
- stable target identity across worker replacement;
- new worker identity after restart;
- simulated wedged mutation;
- watchdog transition to `reconciliation_required`;
- explicit reconciliation before normal mutation resumes.

### Gate 0D — JSON/stdio

Passed 11 protocol fixtures including:

- strict protocol version handling;
- request IDs;
- tagged string/number/boolean/null/object/array fidelity;
- malformed-line isolation;
- unknown-operation errors;
- strict unknown fields;
- one response per request line;
- no human prose requirement on stdout.

### Gate 0E — MCP

Using official `ModelContextProtocol 2.2.0`:

- server/client handshake passed;
- two MCP tools were discovered;
- MCP status result matched direct operation-kernel result byte-for-byte;
- MCP execute result matched direct operation-kernel result byte-for-byte;
- MCP adds no host/business semantics;
- stderr remained clean in the self-test.

### Gate 0F — second host

Photoshop 2026 validated the abstraction:

- `Photoshop.Application` COM activation succeeded;
- generic host state reads succeeded;
- script capability was probed independently;
- the host-specific DOM was not forced into Illustrator semantics;
- spike ownership was tracked;
- a spike-owned Photoshop instance with zero documents was safely quit;
- host lifecycle ownership was explicit.

This falsified the assumption that Illustrator-specific operations need to leak into the core.

## Why C#/.NET won Phase 0

The decision is evidence-based rather than aesthetic.

C#/.NET provides:

- direct Windows COM interop;
- explicit STA ownership;
- strong process and named-pipe primitives;
- self-contained executable deployment;
- first-party-quality async/concurrency primitives;
- a current official MCP SDK;
- strong JSON/schema tooling;
- a natural supervisor/worker implementation model.

Python remains the **legacy behavioral oracle** during migration. Node remains valid for host-specific JavaScript-native tooling such as UXP or the existing ESD bridge. C/C++ remains valid for native plug-ins and VectorIPC.

The architecture is intentionally polyglot at the edges while keeping one durable control plane.

### Follow-on decision — VectorIPC is not the V2 COM/runtime transport

V2 retains the .NET current-user named pipe for agent-to-runtime traffic and its
separate named-pipe link for supervisor-to-STA-worker traffic. The latter uses a
fresh token and validates worker PID, process start time, apartment, and target
identity. VectorIPC is not inserted into either path: it provides framed local
transport, not those product ownership semantics. The ExternalObject adapter is
synchronous, globally serialized in the Illustrator host, and capped at 256
KiB; putting normal COM operations behind it would add a native DLL and another
helper hop without evidence that transport is the bottleneck.

VectorIPC remains a candidate optional facet for native plug-in/helper traffic,
events, or bulk native processing. Reconsider that integration only after a
same-host, end-to-end benchmark demonstrates a material improvement and the
adapter can preserve V2 target identity, authentication, leases, write-ahead
mutation state, deadlines, and ambiguity semantics without moving Illustrator
SDK calls off the main thread.

## Non-decisions

This ADR does **not** decide:

- final public product name;
- every v1 operation;
- long-term plugin RPC format;
- whether the ESD Node bridge will eventually be replaced;
- whether any future component will use Native AOT;
- whether every Adobe application will be supported.

Those remain evidence-driven.

## Consequences

1. Production code may now be scaffolded under `src/`.
2. Protocol/schema work may proceed while Gate 0A restart/reconnect remains open.
3. Host adapters must expose capability facets honestly.
4. MCP-specific code must remain outside operation/runtime semantics.
5. No V2 production project may import legacy Python implementation modules.
6. Differential tests may invoke the legacy tool externally.
7. Ambiguous mutation failure is a protocol/runtime state, not a generic exception.
8. All future architecture changes that violate these invariants require a new ADR.
