# ADR-0002 — Phase 0 Provisional Conclusions

**Status:** Provisional; final architecture lock blocked only by Illustrator process restart/reconnect evidence  
**Date:** 2026-09-24

## Evidence completed

### .NET / COM

C# on .NET 10 successfully:

- ran on an STA main thread,
- resolved `Illustrator.Application`,
- attached to the active Illustrator ROT object,
- read Illustrator 30.6.0 state,
- executed `DoJavaScript`,
- created an isolated disposable document,
- performed direct COM PathItem creation/deletion,
- closed the disposable document with explicit `aiDoNotSaveChanges = 2`,
- restored document count to its initial value.

A transient `RPC_E_SERVERFAULT` was observed in both C# and the existing Python tool and later self-recovered without an Illustrator restart. This argues for route-specific host health classification rather than against .NET.

### Packaging

A self-contained win-x64 single-file .NET executable is viable.

Measured minimal executable baseline:

- 70.12 MiB,
- 20-run median startup 63.53 ms,
- p95 72.5 ms.

ReadyToRun was rejected as the Phase 0 default because the measured spike was both larger (79.37 MiB) and slower (65.588 ms median).

### Supervisor / worker topology

The single-binary supervisor/worker spike passed:

- per-launch authenticated named-pipe handshake,
- STA worker identity,
- 1,000 exactly serialized operations submitted from 40 concurrent callers,
- worker restart with stable logical target identity and changing worker identity,
- simulated wedged mutation watchdog,
- transition to `reconciliation_required`,
- fresh worker + explicit reconciliation back to `known`.

### stdio

NDJSON is viable for the external universal process transport.

The spike passed strict request/version errors, malformed-line isolation, one-response-per-input, and explicit type fidelity for string/number/boolean/null/object/array.

Named-pipe worker IPC remains length-framed rather than NDJSON.

### MCP

Official `ModelContextProtocol` 2.2.0 works on the selected .NET toolchain.

The MCP self-test proved the intended architecture:

```text
MCP tool → adapter → OperationKernel
direct call ────────→ OperationKernel
```

Direct and MCP-mediated results matched byte-for-byte.

### second host

Photoshop 2026 validated the multi-host architecture:

- `Photoshop.Application` COM activation worked,
- Adobe Photoshop 27.7.0 state was readable,
- its COM `DoJavaScript` route worked,
- the spike distinguished a newly launched instance from a preexisting instance,
- it quit only the instance it owned and only while document count remained zero,
- ROT registration disappeared after owned cleanup.

This supports a generic COM facet while preserving host-specific DOM/adapters.

## Provisional architectural decisions

The following have enough evidence to guide specification work:

1. Executable-first product.
2. C#/.NET remains the leading implementation.
3. Supervisor + isolated per-host workers.
4. Dedicated STA execution ownership for COM workers.
5. Length-framed local worker IPC.
6. NDJSON external stdio transport.
7. MCP as an optional thin adapter.
8. Capability-driven host adapters.
9. Generic COM facet, no universal Adobe DOM.
10. Strong target/worker ownership identities.
11. Ambiguous mutation → reconciliation before further mutation.
12. Illustrator first, Photoshop proven as second host.

## What is not locked yet

The plan required a real Illustrator **process restart/reconnect** test before Gate 0A is fully closed.

That test is intentionally deferred while the user's active Illustrator instance is in use. V2 must not kill or restart it simply to satisfy a gate.

Therefore:

- specification extraction may proceed,
- draft protocol/schema work may proceed,
- production architecture remains formally reopenable until restart/reconnect is exercised,
- no claim should be made that Phase 0 is fully closed.
