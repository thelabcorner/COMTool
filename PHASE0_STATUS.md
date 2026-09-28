# Phase 0 Gate Ledger

**Status:** CLOSED — all Phase 0 gates have executable evidence as of 2026-09-27.\n\n**Rule:** no gate is marked PASS without captured executable evidence. Architectural hypotheses remain reversible until Phase 0 closes.

| Gate | State | Purpose | Evidence |
|---|---|---|---|
| 0A | PASS | Prove .NET/COM/STA viability against live Illustrator | STA attach/read/JSX/direct-DOM disposable mutation/explicit close passed; live restart/reconnect also passed with the same persistent RuntimeHost discovering a new strong Illustrator generation (`evidence/gate-0a-restart-reconnect-2026-09-27.json`) |
| 0B | PASS | Prove distributable self-contained executable | win-x64 self-contained single-file builds passed; baseline 70.12 MiB, median startup 63.53 ms over 20 runs; ReadyToRun was larger/slower |
| 0C | PASS | Prove supervisor/worker/named-pipe topology | Framed authenticated pipe; STA worker; 1,000 concurrent-caller requests serialized exactly once; restart identity; watchdog → reconciliation_required → explicit reconcile all passed |
| 0D | PASS | Prove canonical JSON/stdio transport | 11 NDJSON fixtures passed including tagged type fidelity, malformed-line isolation, strict fields, version/operation errors |
| 0E | PASS | Prove MCP is a thin transport adapter | Official ModelContextProtocol 2.2.0 client/server discovered 2 tools; direct-vs-MCP status and execute results matched byte-for-byte; zero stderr noise |
| 0F | PASS | Falsify Illustrator-specific core assumptions with second host | Photoshop 2026 COM activation/read/script probe passed; spike-owned instance quit safely with zero docs; generic COM facet + host-specific DOM/capabilities validated |

## Environment evidence

- OS/RID observed by local SDK: Windows 10.0.22631 / win-x64.
- Existing system runtimes observed: .NET 6, 8, and 10.
- Project-local SDK: .NET SDK 10.0.401.
- Local SDK path: `comtool-v2/.dotnet/`.
- Illustrator 2026 process was observed running during bootstrap.
- No global PATH or registry changes were made by the local SDK bootstrap.

## Gate 0A criteria

The spike must prove:

1. STA ownership is explicit and observable.
2. Attach to the currently running `Illustrator.Application` without launching a replacement.
3. Read version and document count.
4. Execute `DoJavaScript` and receive a value.
5. Perform one mutation only in a newly-created disposable document; verify it and undo/remove it.
6. Close that disposable document with explicit `aiDoNotSaveChanges = 2`.
7. Release COM references deterministically enough for repeated spike runs.
8. Surface COM HRESULTs as structured machine-readable output.
9. Demonstrate reconnect behavior after a host restart before Gate 0A is finally closed.
10. Record any .NET-specific interop limitations instead of papering them over.

A read-only probe may be run before the mutation probe. The mutation probe must never operate on the user's pre-existing active document.
