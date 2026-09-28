# Transport, CLI, and MCP Parity

**Status:** Current  
**Date:** 2026-09-25  
**Scope:** Production transport parity and current runtime operation surface.

This document is the authoritative description of how CLI, JSON/stdio, local IPC,
and MCP relate to one another. It exists to prevent MCP (or any future transport)
from silently becoming a parallel execution path.

## 1. One protocol, several framings

Every agent-facing surface normalizes into the same versioned
`OperationRequest` / `OperationResult` contract (`src/ComTool.Protocol`):

```text
CLI command        ─┐
JSON/stdio (NDJSON) ─┼─► OperationRequest ─► (one supervisor) ─► OperationResult ─► JSON
length-prefixed pipe ┘
MCP tool call      ─┘
```

There is exactly one dispatcher of consequence: `RuntimeSupervisor.ExecuteAsync`.
No transport owns policy, leases, mutation journaling, or routing. Those live in
the supervisor and are shared by every front end.

The Wave 3 `core.workflow.*` operations use that same dispatcher and runtime
state directory. VectorIPC is not substituted for either V2 named-pipe link:
its current-user local IPC is a transport substrate, while V2's control plane
also depends on its own worker authentication, target generation, lease, and
mutation-ledger protocols. VectorIPC remains a possible optional transport for
a future native plug-in/helper facet after a same-workload benchmark, not a
second runtime or a prerequisite for basic automation.

## 2. Transports

| Transport | Where | Framing | Entry point |
|---|---|---|---|
| CLI commands | `ComTool.Cli.exe <command>` | process argv → one JSON envelope on stdout | `src/ComTool.Cli/Program.cs` |
| Local IPC | current-user named pipe | 4-byte little-endian length prefix + UTF-8 JSON | `src/ComTool.Runtime.Ipc` + `src/ComTool.Transport.Pipe` |
| JSON/stdio | `ComTool.RuntimeHost.exe --stdio` | newline-delimited JSON (NDJSON) | `src/ComTool.Transport.Stdio/NdjsonServer.cs` |
| MCP | `ComTool.Transport.Mcp.exe --server` | MCP over stdio | `src/ComTool.Transport.Mcp/Program.cs` |

All four carry the same `OperationRequest` payload. Only the framing differs.

### 2.1 Local IPC (preferred agent surface)

`RuntimeHost` runs the persistent current-user named pipe
(`RuntimeEndpoint.DefaultPipeName`) under a per-session mutex, so exactly one
runtime instance owns a session. Frame size is bounded (1 MiB default);
malformed frames terminate the offending client without poisoning the server.

### 2.2 JSON/stdio

`RuntimeHost --stdio` serves NDJSON on stdin/stdout through the **same**
`RuntimeSupervisor` instance and the same `BuiltInOperations` catalog:

```powershell
'{"protocolVersion":1,"id":"h1","operation":"core.runtime.health","input":null}' |
  ComTool.RuntimeHost.exe --stdio --worker <path> --pipe <name>
```

`ComTool.Cli.exe stdio [--pipe <name>]` is the thin client-side equivalent: it
connects to the runtime pipe and serves NDJSON on stdin/stdout, forwarding each
line to the runtime unchanged. Neither mode introduces a second dispatcher.

The NDJSON server admits bounded concurrent requests (32 by default), and responses are correlated by `id`, not submission order. The CLI proxy uses `RuntimePipeClientPool`: one single-flight pipe client can serve sequential calls repeatedly, while overlapping calls rent independent runtime-pipe connections. That distinction is required for control-plane liveness: a later `core.target.host.terminate` request must be able to reach the RuntimeSupervisor even if an earlier script request is still blocked on another connection.

NDJSON rules (validated by Gate 0D and by
`tests/ComTool.Transport.Stdio.Tests/CrossTransportParityTests.cs`):

- exactly one response per non-empty input line;
- responses may complete out of submission order and must be matched by request `id`;
- malformed input fails only that line (`invalid_json`);
- strict root fields (`JsonUnmappedMemberHandling.Disallow`);
- tagged result values preserve JSON types;
- stdout carries machine JSON only.

### 2.3 MCP

MCP is an **adapter**, never the architecture. `ComTool.Transport.Mcp` exposes a
single tool, `adobe_execute(operation, inputJson, requestId?, leaseId?, targetHost?, targetId?, workerWatchdogMs?, retryBudgetMs?, preconditionsJson?, postconditionsJson?)`,
which builds a real `OperationRequest`, validates it with the same
`ProtocolJson.ValidateRequest`, and forwards it over the ordinary runtime pipe.
All Adobe/host logic remains outside the MCP project.

The phase-0 spike (`spikes/gate-0e-mcp`) proved thinness against a toy
`OperationKernel`. The production adapter replaces that toy with the real
runtime pipe; `--self-test` proves semantic envelope parity between the MCP call
path and a direct pipe call against a live runtime.

## 3. Parity status

| Dimension | State |
|---|---|
| CLI ↔ runtime payload | Parity via `OperationRequest`/`OperationResult` (`Cli.Program.cs` `RunRuntimeOperation`). |
| CLI ↔ runtime discovery | Every CLI command of consequence honors `--pipe` and reaches the persistent RuntimeHost; the CLI no longer instantiates host adapters or workers directly. |
| stdio ↔ runtime payload | Parity by construction: `RuntimeHost --stdio` and `Cli stdio` both carry `OperationRequest` frames. Covered by `CrossTransportParityTests`. |
| pipe ↔ stdio framing independence | Same handler, identical canonical envelope across both framings (`CrossTransportParityTests.SameRequestYieldsIdenticalEnvelopeAcrossTransports`). |
| MCP ↔ runtime payload | Parity via `RuntimeBridge` + `--self-test` (success and error envelopes). |
| Error envelope | `invalid_json`, `invalid_operation`, `invalid_lease_id`, `unsupported_operation` all produced from the shared protocol/validation layer. |

### 3.1 One CLI authority path

- **There is no direct or CLI-owned broker mode.** `ComTool.Cli` has no project
  reference to the Illustrator adapter or supervisor assembly and cannot connect
  COM or launch a worker itself. Legacy `--broker` / `--worker` options are
  rejected before runtime connection.
- `--runtime` is accepted as a compatibility no-op for older automation, but it
  no longer selects a different execution mode: RuntimeHost is always the
  authority path.
- `targets`, `capabilities`, `status`, `snapshot`, `get`, `call-read`,
  structure reads, scripts, leases, incidents, reconciliation, and artifact
  retrieval all normalize to an ordinary `OperationRequest` and cross the
  runtime pipe.
- `incident-resolve-offline` is the CLI convenience for
  `core.incident.resolve`: it carries the durable target id, incident request
  id, exact `expectedUpdatedAt` revision token, explicit resolution, and
  rationale without fabricating a target lease for a dead generation.
- `mutation-reconcile` is dispatched through the same runtime operation catalog
  and supervisor as every other transport. It requires the active incident ID,
  current target-state revision, lease, and the exact postcondition list
  fingerprinted in the original mutation request; conditions supplied only
  after ambiguity cannot clear the incident.
- `core.workflow.submit/get/cancel/resume` likewise carry ordinary
  `OperationRequest` / `OperationResult` envelopes and never dispatch a host
  operation outside `RuntimeSupervisor`.

## 4. Runtime and worker discovery

| Item | Mechanism |
|---|---|
| Runtime pipe name | `--pipe`, else `COMTOOL_V2_RUNTIME_PIPE`, else `comtool-v2-runtime-<sessionId>`. Every runtime-capable CLI command accepts `--pipe` (and `Cli stdio`), so the CLI can reach an isolated/shadow runtime. |
| Runtime single-instance guard | `Local\ComToolV2Runtime_<sessionId>` (or a hash for named pipes). |
| Worker executable | RuntimeHost-owned configuration (`--worker`, else `COMTOOL_V2_WORKER_PATH`, else `ComTool.Worker.exe` beside RuntimeHost). The CLI never resolves or launches a worker. |
| State directory | `--state-dir`, else `COMTOOL_V2_STATE_DIR`, else the supervisor default. |
| Host families | `--host` (repeatable); defaults to `illustrator`. |

The runtime resolves workers from `AppContext.BaseDirectory` first, which is why
the dev/production layout keeps `ComTool.Worker.exe` beside `ComTool.RuntimeHost.exe`.

## 5. State-directory layout

The supervisor owns durable state. `--state-dir` / `COMTOOL_V2_STATE_DIR`
redirects it (used by isolated tests and shadow deployments); otherwise the
supervisor default (`MutationLedger.DefaultRoot`) applies. Layout is owned by
the supervisor, not by any transport:

```text
<state-dir>/
  ... mutation journal keyed by durable request id (prepare → complete/…)
```

Transports never read or write this directory directly; they only submit
requests and observe `OperationResult` / `evidence`.

## 6. Rules for future transports

1. Normalize into `OperationRequest`; never invent a second request shape.
2. Route through the one supervisor; never call a host adapter directly.
3. Keep host/Adobe logic out of the transport.
4. If a transport needs a new operation, register it in
   `BuiltInOperations.cs` (owned by the supervising integrator) — not in the
   transport project.
5. Do not weaken leases, the mutation ledger, or the reconciliation state
   machine to make a transport simpler.
