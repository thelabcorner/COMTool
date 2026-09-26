# ADR-0001 — V2 Bootstrap Boundaries

**Status:** Accepted for Phase 0  
**Date:** 2026-09-24

## Decision

V2 is developed exclusively under `comtool-v2/`. The existing `comtool/` implementation, current skill documentation, COM index, vendor files, tests, and audit ledger are read-only compatibility inputs unless a separate task explicitly authorizes legacy changes.

## Rationale

The repository is concurrently modified and the existing COM Tool is actively useful. A greenfield rewrite must not destabilize the current agent automation surface.

## Consequences

- No V2 command may import implementation modules directly from legacy `comtool/`.
- Legacy data may be read for specification extraction and differential testing.
- Behavioral parity is measured externally at process/host boundaries.
- Intentional semantic differences must be recorded as migration deltas.
- V2 never depends on MCP for basic operation.
- Phase 0 spikes are disposable evidence, not production architecture.

## Language status

C#/.NET is the leading hypothesis. It is **not locked** until Phase 0 proves COM behavior, packaging, runtime topology, stdio, MCP translation, and a second host.

Native AOT is not an initial requirement. The first packaging target is normal self-contained .NET because COM interoperability is a primary requirement.
