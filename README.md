<div align="center">

# COMTool: Guarded Adobe automation runtime for ExtendScript (ES3)

## One authority path for COM, ExtendScript, plug-in RPC, debugging, and agent automation

### CLI, local IPC, NDJSON, MCP, and Node all route through one RuntimeHost → Supervisor → Worker execution kernel

[![Deterministic gate](https://img.shields.io/badge/.NET-835%2F835%20passing-success)](#validation)
[![Migration parity](https://img.shields.io/badge/V1%20parity-42%2F42-purple)](#validation)
[![Live engine](https://img.shields.io/badge/Illustrator-30.6.0%20live-success)](#compatibility)
[![Adobe: Creative Suite](https://img.shields.io/badge/Adobe%20-Creative%20Suite-red?logo=adobe&logoColor=white)](https://extendscript.docsforadobe.dev/)
[![Engine](https://img.shields.io/badge/ExtendScript-ES3-green)](#compatibility)
[![Package](https://img.shields.io/badge/package-win--x64%20self--contained-blue)](#installation)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/license-GPL%203.0--or--later-blue)](https://www.gnu.org/licenses/gpl-3.0.html)

</div>

---

## Part Of The Same Toolkit

> Production-grade infrastructure for Adobe ExtendScript.

<table>
<tr>
<td width="50%" valign="top">

### Runtime Primitives

**[ESON](https://github.com/thelabcorner/eson)**  
Strict RFC 8259 JSON for ExtendScript.

**[ESB64](https://github.com/thelabcorner/es-b64)**  
Base64 and UTF-8 utilities.

**[ESARR](https://github.com/thelabcorner/es-arr)**  
ES5+ Array compatibility methods.

**[ESSTR](https://github.com/thelabcorner/es-str)**  
String whitespace and trim methods.

**[ESCHARS](https://github.com/thelabcorner/es-chars)**  
Native bulk byte operations.

**[ESHTTP](https://github.com/thelabcorner/es-http)**  
HTTP transport for ExtendScript automation.

**[ESTIMER](https://github.com/thelabcorner/es-timer)**  
Microsecond timing for ExtendScript automation.

**[ESRAND](https://github.com/thelabcorner/es-rand)**  
Deterministic random streams and sampling for ExtendScript.

**[ESUUID](https://github.com/thelabcorner/es-uuid)**  
RFC 9562 UUID generation, parsing, and conversion for ExtendScript.

</td>
<td width="50%" valign="top">

### Build & Integration Tools

**[ESPACK](https://github.com/thelabcorner/espack)**  
Self-extracting ExternalObject bundles.

**[ESMIN](https://github.com/thelabcorner/es-min)**  
Minification for shipped JSX bundles.

**[ESABI](https://github.com/thelabcorner/esabi)**  
Modern ExternalObject ABI declarations for native integrations.

**[VectorIPC](https://github.com/thelabcorner/vector-ipc)**  
Bounded local IPC for scripting hosts and native plug-ins.

**[ESTC](https://github.com/thelabcorner/estc)**  
TypeScript-to-ExtendScript build, compatibility, and live-parse tooling.

**[ESDB](https://github.com/thelabcorner/esdb)**  
Native state and durable storage for Adobe tooling.

**[COMTool](https://github.com/thelabcorner/COMTool)**  
Guarded COM, ExtendScript, plug-in, and debugger automation for Adobe desktop apps.

**ESOBF** <sub>coming soon</sub>  
Obfuscation for hardened JSX distribution.

</td>
</tr>
</table>

Also from the same team: **[ArcFit.dev](https://arcfit.dev)**, deterministic arc warp for Illustrator.

---

## Table of Contents

- [Why COMTool?](#why-comtool)
- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [API Reference](#api-reference)
- [Validation](#validation)
- [Performance](#performance)
- [Security Model](#security-model)
- [Compatibility](#compatibility)
- [Engine quirks that shaped the design](#engine-quirks-that-shaped-the-design)
- [Development](#development)
- [Repository layout](#repository-layout)
- [Known limitations](#known-limitations)
- [Credits](#credits)
- [License](#license)

---

## Why COMTool?

The original Python Illustrator COM tool proved the workflow, but it accumulated the exact failure modes that become dangerous once multiple agents, long-running jobs, debugger sessions, plug-in RPC, and mutation recovery share one live Adobe host: transport-specific behavior, weak process-generation identity, implicit retry assumptions, and no single durable authority for mutation state.

**V1 is deprecated.** It remains available only as a behavioral oracle and compatibility fallback while migrations finish. New automation should use COMTool's V2 runtime surface.

COMTool makes the executable runtime the product. CLI, NDJSON stdio, current-user local IPC, the optional MCP adapter, and the dependency-free Node SDK all normalize into the same versioned `OperationRequest` / `OperationResult` protocol and route through one `RuntimeSupervisor`. Host execution stays in dedicated STA workers; policy, leases, durable mutation state, reconciliation, artifacts, and observability remain supervisor-owned.

A restarted Adobe process is deliberately a new target. Gate 0A closed this live on Illustrator 30.6.0: Illustrator exited cleanly with zero documents, PID changed from **79016 → 79188**, the same already-running RuntimeHost discovered a new strong target identity, and a fresh STA .NET client reattached and executed `DoJavaScript("1+1") → 2`.

---

## Features

- **One execution authority.** CLI, pipe, NDJSON, MCP, and Node are transports over the same supervisor rather than parallel COM implementations.
- **Strong target identity.** Target IDs are opaque and generation-specific; PID + process-start identity is revalidated before generation-sensitive recovery.
- **Explicit leases.** Effectful operations require exclusive target ownership across runtime processes, backed by an OS-held per-target lock.
- **Write-ahead mutation state.** Non-read-only dispatch is journaled before reaching the worker; completed retries replay durable results instead of re-executing.
- **Conservative ambiguity.** A worker/runtime loss after possible dispatch becomes `reconciliation_required`; host liveness alone never clears it.
- **Guarded recovery.** `core.target.host.terminate` is a generation-pinned break-glass path that bypasses a blocked execution lane without rediscovering or replaying the ambiguous script.
- **Typed Illustrator operations.** Document, layer, and artboard reads/mutations own their selector grammar and target member instead of accepting arbitrary caller-provided COM paths.
- **Generic escape hatches remain explicit.** `com.set`, `com.call`, `script.eval`, and `script.runFile` retain conservative mutation classes and lease requirements.
- **Persistent debugger sessions.** `debug.session.*` drives the Adobe ExtendScript Debugger core through a worker-owned session bound to one target generation.
- **Native plug-in RPC and diagnostics.** `plugin.message` and AIPDebug operations support plug-in data/control flows while preserving runtime ownership and provenance.
- **Opaque artifacts.** Large results are materialized by the supervisor and exposed by artifact ID rather than by granting callers filesystem authority.
- **Durable sequential workflows.** `core.workflow.*` runs 1–64 registered steps through the same dispatcher, lease, mutation ledger, and condition machinery as direct requests.
- **Embedded COM knowledge.** Search/signature/enum lookup is generated from the pinned Illustrator COM inventory; the embedded pack records SQLite and source-JSON SHA-256 provenance.
- **Dependency-free Node SDK.** `sdk/node` provides generic `execute()`, strong-target sessions, leases, file/eval helpers, debugger orchestration, artifact retrieval, and explicit ambiguous-recovery helpers.
- **Caller-controlled watchdogs.** Script worker watchdogs are bounded from **100 ms through 3,600,000 ms** end-to-end.
- **Bounded transport.** Public pipe/NDJSON and the authenticated supervisor↔worker broker retain a **1 MiB** frame ceiling.

---

## Installation

COMTool ships as a self-contained Windows x64 package. It does not require a machine-wide .NET installation.

Build a production package from a clean tree:

```powershell
.\scripts\release.ps1 -Version 0.1.2
```

The release transaction writes a versioned package and ZIP under `.artifacts/release/`, verifies the staged package, extracts and verifies the shipped ZIP, and records source commit/fingerprint, toolchain provenance, NuGet audit results, per-file SHA-256 values, and archive SHA-256 in `release-manifest.json`.

Install the extracted package for the current user:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-user.ps1
```

Installed versions are immutable and side-by-side:

```text
%LOCALAPPDATA%\Programs\ComToolV2\
  versions\<version>\
  current\                 # stable junction to the selected version
```

Configure integrations against:

```text
%LOCALAPPDATA%\Programs\ComToolV2\current\ComTool.Cli.exe
%LOCALAPPDATA%\Programs\ComToolV2\current\ComTool.RuntimeHost.exe
%LOCALAPPDATA%\Programs\ComToolV2\current\ComTool.Transport.Mcp.exe
```

Durable runtime state is separate at `%LOCALAPPDATA%\ComToolV2`. A custom `--state-dir` or `COMTOOL_V2_STATE_DIR` denotes the complete state root. Only one runtime process may own one state root at a time.

For development, build and stage immutable binaries instead of running a persistent runtime directly from `bin/Release`:

```powershell
.\scripts\stage-runtime-dev.ps1
```

Windows can lock loaded assemblies; immutable staging prevents the live development runtime from obstructing subsequent builds.

---

## Quick Start

With an installed runtime:

```powershell
$ct = "$env:LOCALAPPDATA\Programs\ComToolV2\current"

Start-Process "$ct\ComTool.RuntimeHost.exe"

& "$ct\ComTool.Cli.exe" health
& "$ct\ComTool.Cli.exe" targets
```

Read Illustrator state:

```powershell
& "$ct\ComTool.Cli.exe" status
& "$ct\ComTool.Cli.exe" get --path ActiveDocument.Name
```

Effectful work requires a target lease and a caller-stable request ID. The runtime owns the safety classification; a caller cannot downgrade a fixed mutation operation to read-only.

The optional MCP entry point remains a thin adapter:

```powershell
& "$ct\ComTool.Transport.Mcp.exe" --self-test --pipe <name>
```

For Node programs, see [`sdk/node/README.md`](sdk/node/README.md). The SDK forwards arbitrary current/future operations through `ComTool.Cli.exe stdio`; it does not contain a second Adobe/COM implementation.

---

## API Reference

### Transports

| Surface | Entry point | Authority |
|---|---|---|
| CLI | `ComTool.Cli.exe <command>` | RuntimeHost → RuntimeSupervisor |
| Local IPC | `ComTool.RuntimeHost.exe` current-user named pipe | RuntimeSupervisor |
| NDJSON | `ComTool.RuntimeHost.exe --stdio` or `ComTool.Cli.exe stdio` | Same operation protocol |
| MCP | `ComTool.Transport.Mcp.exe --server` | Thin adapter over the runtime pipe |
| Node | `sdk/node` | Thin client over CLI NDJSON |

NDJSON is correlation-based, not response-order-based. `NdjsonServer` admits **32** concurrent requests by default and serializes stdout writes only. The CLI proxy pools runtime-pipe clients so an explicit recovery/control request is not queued behind a blocked execution request.

### Runtime and control

- `core.runtime.health`
- `core.operations.list`
- `core.operation.describe`
- `core.operation.examples`
- `core.artifact.describe`
- `core.artifact.read`
- `knowledge.describe`
- `knowledge.search`
- `knowledge.symbol`
- `knowledge.enum`
- `script.validate`
- `watch.condition`
- `core.incidents.list`
- `core.incident.resolve`
- `core.targets.list`
- `core.target.capabilities`
- `core.target.status`
- `core.target.snapshot`
- `core.target.reconcile`
- `core.target.lease.acquire`
- `core.target.lease.renew`
- `core.target.lease.release`
- `core.target.host.terminate`
- `core.workflow.submit`
- `core.workflow.get`
- `core.workflow.cancel`
- `core.workflow.resume`

### Illustrator reads

- `com.get`
- `com.call.read`
- `illustrator.document.read`
- `illustrator.artboard.read`
- `illustrator.layer.read`

### Guarded scripts

- `script.eval`
- `script.runFile`

`script.runFile` requires an absolute `.jsx` / `.jsxbin` path plus the caller's SHA-256 of the exact file bytes. The adapter re-hashes the file immediately before execution.

Arbitrary script execution cannot self-certify as read-only. Omitted effects default to `unknown`; effectful scripts require an exclusive target lease and caller-stable request ID.

### Typed mutations and lifecycle

- `illustrator.artboard.setName`
- `illustrator.artboard.setRect`
- `illustrator.layer.setName`
- `illustrator.layer.setVisible`
- `illustrator.layer.setLocked`
- `illustrator.layer.setOpacity`
- `illustrator.action.run`
- `illustrator.menu.execute`
- `illustrator.document.create`
- `illustrator.document.open`
- `illustrator.document.save`
- `illustrator.document.saveAs`
- `illustrator.document.close`

Typed layer/artboard operations require explicit selectors, reject ambiguous name matches before mutation, and never accept a caller-supplied COM member/path.

`illustrator.document.close` requires one explicit close policy: `save`, `discard`, or `reject_if_unsaved`. Discard is never the default.

### Native plug-ins

- `plugin.message` — bounded `Application.SendScriptMessage(plugin, selector, input)` data RPC.
- `plugin.debug.diagnostics` — fixed AIPDebug discover/info/logs/snapshot/stats surface.
- `plugin.debug.control` — bounded AIPDebug control actions.

The packaged `aipdebugctl.exe` is re-hashed against `release-manifest.json` before use. Direct VectorIPC diagnostics are allowed only after endpoint provenance is established through the exact Illustrator generation, and the native peer verifies expected PID + process-start identity again.

### ExtendScript debugger

- `debug.session.open`
- `debug.session.status`
- `debug.session.command`
- `debug.session.close`

The debugger session is worker-owned and generation-scoped. Commands cover eval, breakpoint management, break/frame/property inspection, continue/halt/break, and step over/into/out. The worker pins the exact debugger-addon bytes before the bridge child exists and the bridge re-hashes that path immediately before loading it.

### Mutation crash model

For non-read-only operations, the durable state machine is:

```text
prepared → completed
         → not_started
         → ambiguous
```

The request ID is the idempotency key.

- `completed`: an identical retry returns the stored result with `mutation.replay` evidence and `executeMs = 0`.
- `not_started`: the same request may safely retry.
- `prepared` after runtime loss, or `ambiguous`: the target rehydrates as `reconciliation_required`; the mutation is not replayed.
- same request ID + different semantic payload: rejected.
- generic host liveness is not proof of mutation outcome.

`core.target.mutation.reconcile` can clear an incident only from the exact postcondition fingerprint durably declared with the original request. Conditions invented after ambiguity cannot certify the old mutation.

### Durable workflows

`core.workflow.submit` accepts **1–64** registered sequential steps. Each step has an explicit ID, operation, input, and optional pre/postconditions. The outer request ID is the job ID and definition idempotency key.

Step request IDs are deterministically derived from job + step identity, so runtime recovery reuses the same mutation-ledger semantics. Mutating workflows hold one target lease for the run. `targetLease:false` is accepted only when every step is read-only. An ambiguous/reconciliation-required step always halts the workflow.

One strong Illustrator target remains a capacity-1 execution resource. Future DAG scheduling may overlap runtime-only work or genuinely different targets, but not parallel host dispatch against one Illustrator generation. See [`docs/WORKFLOW_CONCURRENCY_MODEL.md`](docs/WORKFLOW_CONCURRENCY_MODEL.md).

### Opaque artifacts and large results

Public pipe/NDJSON and the authenticated runtime↔worker broker retain a **1 MiB** frame ceiling.

Successful host result payloads at or above **32 KiB** are streamed by the worker as correlated **512 KiB** raw chunks. The supervisor verifies chunk order, byte count, result kind, and SHA-256, reconstructs the canonical result, finalizes durable mutation truth when required, then commits the payload through the artifact store and returns a small opaque envelope.

A payload beyond the **64 MiB** artifact ceiling fails explicitly with completed execution truth rather than truncation or replay.

---

## Validation

| Check | Command | Result |
|---|---|---|
| .NET deterministic gate | `.\scripts\dotnet.ps1 test ComTool.V2.slnx -c Release --no-build` | **835/835 passing**, 0 failed |
| V1 migration/parity slice | included in .NET gate | **42/42 passing** |
| Node SDK | `node --test sdk/node/test/sdk.test.mjs` | **20/20 passing** |
| Protocol/schema fixtures | release schema gate | **41/41 matched** |
| TypeScript SDK surface | `node sdk/node/test/typecheck.mjs` | TypeScript **5.9.3** check passed |
| Embedded COM knowledge | `node scripts/build-knowledge-pack.mjs --check` | byte-current against pinned SQLite/manifest provenance |
| Package verification | `scripts/release.ps1` | staged package + extracted shipped ZIP passed |
| Clean-machine shell path | release package smoke | Windows PowerShell **5.1.22621.6133** passed |
| NuGet audit | release transaction | **0 vulnerability records** |

The .NET total covers protocol, runtime/catalog policy, supervisor and durable reconciliation, host launch/attach/recovery, Illustrator adapters, debugger, VectorIPC integration, generic COM, typed mutation semantics, embedded knowledge, runtime IPC, pipe, stdio, MCP, and migration/parity.

Live Illustrator evidence additionally covers STA worker ownership, persistent worker reuse, typed COM reads, snapshot parity, ESON-backed scalar/array/object result fidelity, structured ESON arguments, hard runtime/worker crash replay with **zero target-worker re-execution**, cross-runtime lease contention, immediate kernel-lock takeover after owner death, structure reads on a disposable document, debugger sessions, and restart/reconnect generation change.

Captured results live under [`evidence/`](evidence/).

---

## Performance

The measured live Illustrator 30.6.0 session recorded warm small-expression execution around **0.9–1.1 ms** inside Illustrator.

COMTool deliberately optimizes control-plane liveness rather than pretending Illustrator itself can execute host work in parallel. NDJSON admits bounded concurrent requests, but same-target Adobe execution is serialized; overlapping runtime-only/control work can use separate pooled pipe connections so recovery is not trapped behind a wedged script request.

The artifact path avoids forcing large results through the 1 MiB public/broker frame ceiling: results at the 32 KiB threshold are chunk-streamed to the supervisor and exposed by opaque artifact ID.

These measurements are evidence for the recorded environment, not cross-host performance promises.

---

## Security Model

The public persistent runtime named pipe uses Windows `CurrentUserOnly`. The current Windows user is therefore the local public trust boundary; COMTool does not claim a credential boundary against arbitrary processes already running as that same user.

The private supervisor→worker pipe adds a separate ephemeral bootstrap token plus verified child PID/process-start identity.

Mutation safety is independent of transport:

- effectful operations use explicit target leases;
- mutation intent is durably prepared before dispatch;
- completed retries replay state rather than re-executing;
- possible-dispatch failures remain ambiguous;
- host heartbeat is insufficient reconciliation evidence;
- break-glass host termination requires the already-known exact PID + process-start generation;
- caller-supplied endpoints cannot replace worker-owned plug-in provenance;
- packaged native/debugger helpers are hash-verified before load/use.

Install and release packages are content-addressed by a schema-versioned manifest and archive SHA-256. Authenticode signing is supported but optional. An unsigned archive detects corruption/inconsistency; it does not authenticate the publisher, so distribution of unsigned packages still requires a trusted channel.

---

## Compatibility

| Target | Status |
|---|---|
| Windows x86-64 | Primary supported runtime/package target |
| Adobe Illustrator 2026 / 30.6.0 | Full reference adapter; deterministic + live conformance evidence |
| ExtendScript / ES3 | Script execution, ES3 preflight, ESON transport, debugger evidence |
| Photoshop 2026 | Gate 0F proved second-host COM/script viability and prevented Illustrator-only core assumptions |
| Other Adobe desktop hosts | Require a truthful capability-driven adapter; not advertised as Illustrator-equivalent |
| .NET runtime | Self-contained release package; no machine-wide .NET install required |
| Node.js | Dependency-free SDK client; Node does not implement Adobe/COM behavior |

Illustrator is the complete production adapter today. Other hosts integrate by capability rather than being forced into an Illustrator-shaped API.

---

## Engine quirks that shaped the design

### STA and UI ownership are real constraints

Illustrator automation is STA/UI-bound. COMTool therefore keeps Adobe execution inside dedicated workers and treats one strong target generation as a capacity-1 host-execution resource.

### Host health and script-engine health are different

Gate 0A observed `RPC_E_SERVERFAULT` (`0x80010105`) from `DoJavaScript` while ordinary COM status reads remained healthy. The legacy Python tool observed the same transient condition. The runtime therefore does not equate host heartbeat with script-engine health and does not blindly replay a mutation after a script-route fault.

### Process identity is not PID alone

A restarted application can reuse names and eventually PIDs. Target identity includes process-start generation evidence, and a restarted Illustrator is always a new target.

### A timeout after submission is not cancellation

Node `responseTimeoutMs` or `AbortSignal` can stop the caller waiting locally. After submission, that is conservatively ambiguous; it does not cancel or replay Adobe work. Owned leases are retained for explicit recovery.

### "Process gone" and "identity unreadable" are different

Break-glass termination fails closed if exact process generation cannot be re-proven. An unreadable identity is not silently converted into evidence that the process exited.

### Large results need a separate data plane

A fixed 1 MiB control frame is kept small enough to bound transport behavior. Large successful host results are chunked, verified, and materialized as opaque artifacts instead of expanding the control-plane authority to arbitrary filesystem paths.

---

## Development

A project-local .NET **10.0.401 x64** SDK is bootstrapped into `.dotnet/`; global PATH and the Windows registry are not modified.

Inspect the toolchain:

```powershell
.\scripts\dotnet.ps1 --info
```

Build and run the deterministic gate:

```powershell
.\scripts\dotnet.ps1 build ComTool.V2.slnx -c Release
.\scripts\dotnet.ps1 test  ComTool.V2.slnx -c Release --no-build
```

Verify the embedded COM knowledge pack:

```powershell
node scripts/build-knowledge-pack.mjs --check
```

Run the Node SDK gate:

```powershell
node --test sdk/node/test/sdk.test.mjs
node sdk/node/test/typecheck.mjs
```

Stage an immutable development runtime:

```powershell
.\scripts\stage-runtime-dev.ps1
```

Build a release from a clean source tree:

```powershell
.\scripts\release.ps1 -Version <semver>
```

Production packaging refuses a dirty/untracked source tree by default. `-AllowDirty` exists for development validation and marks the manifest accordingly; production releases cannot use `-SkipTests`.

---

## Repository layout

| Path | Purpose |
|---|---|
| `src/ComTool.Protocol/` | Versioned request/result/value contracts |
| `src/ComTool.Runtime/` | Operation catalog, examples, artifacts, runtime-owned semantics |
| `src/ComTool.RuntimeHost/` | Persistent RuntimeHost entry point |
| `src/ComTool.Supervisor/` | Routing, leases, mutation state, workflows, host lifecycle/recovery |
| `src/ComTool.Worker/` | Dedicated host worker process |
| `src/ComTool.Hosts.Abstractions/` | Host capability and launch/attach contracts |
| `src/ComTool.Hosts.Illustrator/` | Illustrator COM/script/debugger/plug-in adapter |
| `src/ComTool.Knowledge/` | Embedded Illustrator COM knowledge pack and query service |
| `src/ComTool.Transport.*` | Pipe, stdio, and MCP framings/adapters |
| `src/ComTool.Cli/` | Thin command-line client over RuntimeHost |
| `sdk/node/` | Dependency-free Node client/orchestration layer |
| `schemas/` | Protocol JSON schemas |
| `protocol/operation-registry.json` | Checked-in runtime catalog snapshot |
| `migration/` | V1 feature/parity model and fixtures |
| `tests/` | Deterministic .NET transport/runtime/host/parity suites |
| `spikes/` | Phase-0 executable architecture probes |
| `evidence/` | Captured live/release evidence |
| `scripts/` | Toolchain bootstrap, staging, knowledge generation, packaging, install verification |

Architectural decisions live under [`docs/`](docs/). `PHASE0_STATUS.md` is the executable gate ledger.

---

## Known limitations

- Illustrator 30.6.0 is the complete live-conformance host today; Photoshop 2026 has second-host viability evidence, not full Illustrator-equivalent coverage.
- Same-target host execution is intentionally serialized. A workflow DAG cannot make Illustrator safely execute two independent mutations at once.
- Arbitrary script/COM escape hatches cannot be given the same narrow semantic guarantees as runtime-owned typed operations.
- Typed layer/artboard mutations have deterministic adapter coverage. Live mutation conformance must continue to use disposable documents with exact identity/postcondition verification rather than user artwork.
- Authenticode is optional. Unsigned release manifests/checksums provide integrity, not publisher authentication.
- The legacy Python V1 tool is deprecated but intentionally retained as a differential oracle during migration; removing it is a separate compatibility decision.

---

## Credits

COMTool builds on Adobe's COM/OLE and ExtendScript host surfaces, the community-maintained [docsforadobe ExtendScript documentation](https://extendscript.docsforadobe.dev/), the sibling [ESON](https://github.com/thelabcorner/eson) structured-data runtime, and [VectorIPC](https://github.com/thelabcorner/vector-ipc) for bounded native plug-in IPC.

Historical V1 behavior remains valuable as a differential oracle; V2's migration/parity suite keeps that evidence explicit rather than silently reinterpreting it.

---

## License

COMTool is licensed under **GPL-3.0-or-later**. See [`LICENSE`](LICENSE).
