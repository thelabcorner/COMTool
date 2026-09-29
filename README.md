<div align="center">

# COMTool

## Guarded automation runtime for Adobe desktop applications

### One execution authority for COM, ExtendScript, plug-in RPC, debugging, workflows, and agent automation

[![Deterministic gate](https://img.shields.io/badge/.NET-835%2F835%20passing-success)](#validation)
[![Node SDK](https://img.shields.io/badge/Node%20SDK-20%2F20%20passing-purple)](#validation)
[![Schema gate](https://img.shields.io/badge/schema-41%2F41%20matched-success)](#validation)
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

**[ESENV](https://github.com/thelabcorner/es-env)**  
Environment and capability detection for ExtendScript.

**[ESPATH](https://github.com/thelabcorner/es-path)**  
Deterministic Windows/POSIX path and RFC 8089 file-URI transformations.

**[ESFS](https://github.com/thelabcorner/es-fs)**  
Synchronous ExtendScript File/Folder I/O with explicit text, BINARY, and replacement semantics.

**[ESHASH](https://github.com/thelabcorner/es-hash)**  
CRC-32/ISO-HDLC and SHA-256 for byte strings and UTF-8 text.

**[ESLOG](https://github.com/thelabcorner/es-log)**  
Structured logging with bounded text and JSONL sinks.

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

**ESsemble** <sub>coming soon</sub>  
Typed framework, resolver, and composition layer for the ExtendScript toolkit.

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
- [Agent Automation](#agent-automation)
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

Adobe desktop automation is deceptively stateful. A single operation can cross a process boundary, enter a single-threaded COM apartment, execute ExtendScript inside a UI application, call a native plug-in, and return through another transport. If the caller loses contact after dispatch, "try it again" is not a safe recovery strategy: the mutation may already have happened.

COMTool treats that entire path as one runtime problem rather than a collection of unrelated wrappers.

CLI, local IPC, NDJSON stdio, the optional MCP adapter, and the Node SDK all normalize into the same versioned operation protocol and route through one authoritative execution kernel:

```text
caller / agent / CLI / Node / MCP
              │
              ▼
        RuntimeHost
              │
              ▼
      RuntimeSupervisor
         │         │
         │         └── durable state, leases,
         │             workflows, artifacts,
         │             reconciliation
         ▼
      STA Worker
         │
         ▼
 Adobe host / ExtendScript / plug-in
```

The supervisor owns routing, policy, target identity, leases, durable mutation truth, reconciliation, workflows, artifacts, and observability. Host workers own Adobe execution contexts. Transports do not get to invent their own automation semantics.

**COMTool 0.1.x is the first public release line of this project.** Earlier experimental tooling was private development work and is not part of COMTool's public release lineage.

---

## Features

- **One execution authority.** CLI, pipe, NDJSON, MCP, and Node are transports over the same runtime rather than independent COM implementations.
- **Strong target identity.** A target is tied to a specific process generation, not merely an application name or PID.
- **Persistent STA workers.** Adobe execution stays in dedicated single-threaded apartment workers instead of borrowing arbitrary caller threads.
- **Explicit leases.** Effectful work requires exclusive target ownership across runtime processes.
- **Write-ahead mutation state.** Mutation intent is persisted before host dispatch.
- **Safe replay semantics.** Completed mutations can replay their stored result; potentially executed mutations are never silently re-run.
- **Conservative ambiguity.** A lost worker/runtime after possible dispatch becomes `reconciliation_required` rather than "probably failed."
- **Generation-pinned recovery.** Break-glass host termination re-proves the exact known process generation before doing anything destructive.
- **Typed Illustrator operations.** Document, artboard, and layer operations own their selector grammar and target member instead of accepting arbitrary caller paths.
- **Guarded generic automation.** COM and ExtendScript escape hatches remain available, but keep conservative mutation classifications.
- **Durable workflows.** Registered operations can run as recoverable multi-step jobs through the same policy and mutation machinery as direct calls.
- **Persistent ExtendScript debugging.** Debug sessions are worker-owned and bound to one exact target generation.
- **Native plug-in RPC and diagnostics.** Script messages and AIPDebug control remain behind the same runtime authority and provenance checks.
- **Opaque large-result artifacts.** Large host results are verified and materialized by the supervisor without exposing arbitrary state-directory paths.
- **Embedded Illustrator COM knowledge.** Search, signature, and enum lookup are backed by a generated, provenance-pinned knowledge pack.
- **Dependency-free Node SDK.** Generic execution, target sessions, leases, debugger orchestration, artifact access, and explicit ambiguity recovery are available without a second Adobe implementation.
- **Caller-controlled watchdogs.** Script worker watchdogs are bounded from **100 ms to 3,600,000 ms**.
- **Bounded control-plane framing.** Public IPC and the authenticated worker broker retain a **1 MiB** frame ceiling.

---

## Installation

COMTool ships as a self-contained Windows x64 package and does not require a machine-wide .NET installation.

Build a production package from a clean source tree:

```powershell
.\scripts\release.ps1 -Version 0.1.2
```

The release transaction:

1. validates the source tree and dependency lock state;
2. runs deterministic .NET, schema, Node, type, and knowledge-pack gates;
3. publishes self-contained runtime binaries;
4. stages a complete immutable package;
5. verifies the staged package;
6. builds the release ZIP;
7. extracts that ZIP into a fresh directory and verifies the shipped bytes again;
8. records source, toolchain, NuGet-audit, file-hash, and archive-hash provenance in `release-manifest.json`.

Install an extracted package for the current user:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-user.ps1
```

The installer keeps immutable versions side-by-side and exposes a stable `current\` junction for integrations. Point long-lived integrations at the executables beneath that stable `current\` directory rather than at a version-specific package directory.

Durable runtime state is stored separately from installed binaries and is preserved across ordinary upgrades and uninstalls. A custom `--state-dir` can isolate a development or shadow runtime; one state root has one runtime owner at a time.

For development, build first and stage an immutable runtime copy:

```powershell
.\scripts\stage-runtime-dev.ps1
```

Do not run a persistent development runtime directly from `bin/Release`. Windows can lock loaded assemblies and obstruct later builds.

---

## Quick Start

From an installed `current\` directory or an extracted verified package:

```powershell
.\ComTool.RuntimeHost.exe
```

In another shell:

```powershell
.\ComTool.Cli.exe health
.\ComTool.Cli.exe probe
.\ComTool.Cli.exe targets
```

Read Illustrator state:

```powershell
.\ComTool.Cli.exe status
.\ComTool.Cli.exe get --path ActiveDocument.Name
```

The runtime's machine-readable operation catalog is authoritative. Programmatic clients can query `core.operations.list` and `core.operation.describe` rather than hard-coding assumptions about the available surface.

`probe` is deliberately broader than target discovery. It reports live
process presence, COM registration, runtime configuration, and whether this
COMTool build actually contains an automation adapter for each known Adobe
desktop family. A detected application is not automatically an
automation-capable application.

Effectful work requires the runtime's mutation policy to be satisfied. Fixed mutation operations cannot be weakened by caller input, and arbitrary script execution cannot declare itself read-only.

The optional MCP entry point remains a thin transport adapter:

```powershell
.\ComTool.Transport.Mcp.exe --self-test --pipe <name>
```

For programmatic Node usage, see [`sdk/node/README.md`](sdk/node/README.md). The SDK forwards ordinary runtime operations; it contains no independent Adobe/COM implementation.

---

## Agent Automation

COMTool ships its own agent bundle inside every release under `agent/`:
`agent/SKILL.md`, `agent/AGENT_CONTRACT.md`, and a portable OpenFork
slash-command definition at `agent/openfork/comtool.md`.

Agents can discover those installed assets without knowing the installation
path:

```powershell
.\ComTool.Cli.exe agent-guide
.\ComTool.Cli.exe agent-guide --content
```

Both `ComTool.Cli.exe help` and the packaged Node runner's `--help` output
also include a short agent bootstrap hint pointing to `agent-guide --content`,
so an unfamiliar agent can discover the full contract through normal CLI
exploration.

The agent workflow starts with the live dynamic probe, then consults the
runtime-owned operation catalog and exact target capabilities before choosing
an execution path. This avoids teaching agents a stale hard-coded command
surface and prevents a detected Photoshop/InDesign/etc. installation from
being mistaken for a fully implemented COMTool adapter.

The complete machine-facing safety and discovery contract is documented in
[`agent/AGENT_CONTRACT.md`](agent/AGENT_CONTRACT.md).

---

## API Reference

### Transports

| Surface | Entry point | Execution authority |
|---|---|---|
| CLI | `ComTool.Cli.exe <command>` | RuntimeHost → RuntimeSupervisor |
| Local IPC | `ComTool.RuntimeHost.exe` current-user named pipe | RuntimeSupervisor |
| NDJSON | `ComTool.RuntimeHost.exe --stdio` or `ComTool.Cli.exe stdio` | Same operation protocol |
| MCP | `ComTool.Transport.Mcp.exe --server` | Thin adapter over the runtime pipe |
| Node | `sdk/node` | Thin client over CLI NDJSON |

NDJSON is correlation-based rather than response-order-based. The server admits **32 concurrent requests by default** and serializes stdout writes only. Overlapping CLI stdio requests can use independent pooled runtime-pipe connections, so a recovery/control request does not have to wait behind an execution request that is blocked in Adobe.

### Runtime and control

Core runtime operations include:

- `core.runtime.health`
- `core.operations.list`
- `core.operation.describe`
- `core.operation.examples`
- `core.adobe.probe`
- `core.artifact.describe`
- `core.artifact.read`
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

Introspection and planning operations include:

- `knowledge.describe`
- `knowledge.search`
- `knowledge.symbol`
- `knowledge.enum`
- `script.validate`
- `watch.condition`

The live operation catalog is authoritative. Call `core.operations.list` / `core.operation.describe` when exact policy or capability metadata matters.

### Illustrator reads

- `com.get`
- `com.call.read`
- `illustrator.document.read`
- `illustrator.artboard.read`
- `illustrator.layer.read`

Read operations are target-scoped but do not require a mutation lease.

### Guarded scripts

- `script.eval`
- `script.runFile`

`script.runFile` requires an absolute `.jsx` or `.jsxbin` path plus the caller's SHA-256 of the exact file bytes. The host adapter verifies those bytes again immediately before execution.

Arbitrary source execution may declare a write class, but it cannot self-certify as read-only. Omitted effects default to `unknown`.

### Typed mutations and document lifecycle

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

Typed layer/artboard operations require explicit selectors, reject ambiguous name matches before mutation, and never accept a caller-supplied COM member path.

Document close requires an explicit policy: `save`, `discard`, or `reject_if_unsaved`. Destructive discard is never implied.

### Native plug-ins

- `plugin.message` — bounded `Application.SendScriptMessage(plugin, selector, input)` RPC.
- `plugin.debug.diagnostics` — fixed AIPDebug discovery/info/log/snapshot/statistics surface.
- `plugin.debug.control` — bounded AIPDebug control actions.

The packaged native diagnostic helper is hash-checked against `release-manifest.json` before use. Direct VectorIPC diagnostics are allowed only after endpoint provenance is established through the exact Illustrator generation; the native peer then verifies the expected PID and process-start generation again.

### ExtendScript debugger

- `debug.session.open`
- `debug.session.status`
- `debug.session.command`
- `debug.session.close`

A debugger session is worker-owned and target-generation-scoped. Commands cover evaluation, breakpoint management, break/frame/property inspection, continue, halt, break, and step over/into/out.

The worker pins the debugger add-on bytes before creating the bridge child, and the bridge re-hashes that path immediately before loading it.

### Mutation crash model

For non-read-only operations, durable execution state is:

```text
prepared → completed
         → not_started
         → ambiguous
```

The request ID is the idempotency key.

- **completed** — an identical retry returns the stored result with replay evidence and does not execute the host operation again.
- **not_started** — the request may be safely retried.
- **prepared after runtime loss / ambiguous** — the target enters `reconciliation_required`; COMTool does not guess and does not replay the operation.
- **same request ID, different semantic payload** — rejected.

Host liveness is not mutation-outcome evidence.

`core.target.mutation.reconcile` can clear an incident only from verified postconditions whose canonical fingerprint matches the conditions durably declared with the original mutation request. New evidence cannot be invented after ambiguity and used to retroactively certify an unknown operation.

### Durable workflows

`core.workflow.submit` accepts **1–64 registered sequential steps**. Each step has an explicit step ID, operation, input, and optional preconditions/postconditions. The outer request ID is the durable job ID and definition idempotency key.

Step request IDs are deterministically derived from job + step identity, so restart recovery reuses the same mutation-ledger semantics. Mutating workflows hold one target lease for the run. `targetLease:false` is accepted only when every step is read-only. Any ambiguous or reconciliation-required step halts the workflow.

One Illustrator target is intentionally a capacity-1 Adobe execution resource. Future scheduling may overlap runtime-only work or independent target generations, but it must not turn logical workflow parallelism into unsafe concurrent host execution against one Illustrator process. See [`docs/WORKFLOW_CONCURRENCY_MODEL.md`](docs/WORKFLOW_CONCURRENCY_MODEL.md).

### Opaque artifacts and large results

Public pipe/NDJSON and the authenticated runtime↔worker broker retain a **1 MiB** frame ceiling.

Successful host result payloads at or above **32 KiB** are streamed as correlated **512 KiB raw chunks**. The supervisor validates sequence, byte count, result kind, and SHA-256, reconstructs the canonical result, finalizes durable mutation truth when required, commits the payload through the artifact store, and returns a small opaque descriptor.

A result beyond the **64 MiB** artifact ceiling fails explicitly rather than being truncated or silently replayed.

---

## Validation

| Check | Command | Result |
|---|---|---|
| Deterministic .NET gate | release/test pipeline | **835/835 passing**, 0 failed |
| Node SDK | `node --test sdk/node/test/sdk.test.mjs` | **20/20 passing** |
| Protocol/schema fixtures | release schema gate | **41/41 matched** |
| TypeScript SDK surface | `node sdk/node/test/typecheck.mjs` | TypeScript **5.9.3** check passed |
| Embedded COM knowledge | `node scripts/build-knowledge-pack.mjs --check` | byte-current against pinned SQLite/manifest provenance |
| Package verification | `.\scripts\release.ps1 -Version <semver>` | staged package and extracted shipped ZIP verified |
| Windows PowerShell package smoke | release verification | **5.1.22621.6133** passed |
| NuGet audit | release transaction | **0 vulnerability records** |

The deterministic .NET gate covers protocol contracts, operation-catalog policy, durable mutation/reconciliation behavior, target leasing, runtime IPC, pipe and NDJSON transport, MCP bridging, host launch/attach/recovery, Illustrator COM reads and mutations, document lifecycle, debugger integration, native plug-in diagnostics, artifact handling, knowledge lookup, workflows, and compatibility regression fixtures.

Live Illustrator 30.6.0 evidence covers:

- STA worker ownership and persistent worker reuse;
- typed COM reads and target snapshots;
- ESON-backed string, number, boolean, null, array, and object fidelity;
- structured argument transport;
- durable completed-result replay across a hard runtime/worker crash with **zero target-worker re-execution**;
- cross-runtime lease contention and immediate kernel-lock takeover after owner death;
- typed structure reads against a disposable document;
- persistent ExtendScript debugger operation;
- native plug-in diagnostics;
- host restart/reconnect where the process generation changed and the already-running RuntimeHost discovered the replacement target.

Captured proof lives under [`evidence/`](evidence/).

---

## Performance

The measured live Illustrator 30.6.0 session recorded warm small-expression execution around **0.9–1.1 ms inside Illustrator**.

COMTool optimizes for control-plane liveness and correctness rather than pretending a single Illustrator process can safely perform arbitrary host mutations in parallel. Same-target Adobe execution remains serialized, while runtime-only work and independent pipe connections can overlap so recovery/control traffic is not trapped behind a blocked execution call.

Large-result offload keeps the control plane bounded: payloads crossing the 32 KiB artifact threshold are streamed to the supervisor rather than inflating the 1 MiB control frame.

These numbers describe the recorded validation environment; they are not cross-machine or cross-host performance guarantees.

---

## Security Model

The persistent public runtime pipe uses Windows `CurrentUserOnly`. The current Windows user is therefore the local public trust boundary. COMTool does not claim to isolate one same-user process from another same-user process that already has equivalent local authority.

The private supervisor→worker channel adds a separate ephemeral bootstrap token plus verified child PID/process-start identity.

Mutation safety is transport-independent:

- effectful operations use explicit target leases;
- mutation intent is durably prepared before host dispatch;
- completed retries replay stored truth instead of re-executing;
- possible-dispatch failures remain ambiguous;
- a heartbeat cannot certify mutation outcome;
- break-glass host termination requires the already-known exact process generation;
- caller-supplied endpoints cannot replace worker-established plug-in provenance;
- packaged native/debugger helpers are hash-verified before load or use.

Release packages are content-addressed by a schema-versioned manifest and archive SHA-256. Authenticode signing is supported but optional. Checksums and manifests establish integrity; an unsigned archive does not establish publisher identity, so unsigned distribution still requires a trusted channel.

---

## Compatibility

| Target | Status |
|---|---|
| Windows x86-64 | Primary supported runtime and package target |
| Adobe Illustrator 2026 / 30.6.0 | Complete reference adapter with deterministic and live conformance evidence |
| ExtendScript / ES3 | Script execution, static preflight, ESON transport, and debugger evidence |
| Photoshop 2026 | Second-host COM/script viability verified; not a complete Illustrator-equivalent adapter |
| Other Adobe desktop hosts | Capability-driven adapter required; unsupported capabilities are not advertised |
| .NET | Self-contained production package; no machine-wide .NET runtime required |
| Node.js | Dependency-free client/orchestration SDK; no independent Adobe implementation |

Illustrator is the complete production host adapter today. The runtime architecture is intentionally host-capability-driven so additional Adobe applications can integrate without being forced into Illustrator-specific semantics.

---

## Engine quirks that shaped the design

### Adobe execution is STA and UI-bound

A single Illustrator target cannot be treated like an ordinary parallel server. COMTool keeps Adobe execution in dedicated STA workers and treats one strong target generation as a capacity-1 host-execution resource.

### Host health and script-engine health are different signals

Live testing observed `RPC_E_SERVERFAULT` (`0x80010105`) from `DoJavaScript` while ordinary COM status reads still succeeded. Independent development probes reproduced the condition. COMTool therefore does not interpret a healthy COM heartbeat as proof that the script route is healthy, and it does not automatically replay a mutation after a script-route fault.

### PID alone is not an identity

Applications restart. PIDs can eventually be reused. A COMTool target carries process-generation evidence, and a restarted Adobe process is always treated as a new target.

### Caller timeout is not host cancellation

A Node `responseTimeoutMs` or `AbortSignal` can stop the caller from waiting. Once an operation has been submitted, local cancellation does not prove the host stopped. The result is therefore treated conservatively and any owned recovery authority is retained.

### "Gone" and "unreadable" are different

Destructive recovery fails closed if the exact process generation cannot be re-proven. Failure to read identity is not silently converted into evidence that the old process exited.

### Control traffic and result payloads need different bounds

A fixed 1 MiB control frame keeps transport behavior bounded. Large successful results move through the verified artifact path rather than expanding the command surface into arbitrary filesystem access.

---

## Development

A project-local .NET **10.0.401 x64** SDK is bootstrapped into `.dotnet/`; it does not alter global PATH or the Windows registry.

Inspect the pinned toolchain:

```powershell
.\scripts\dotnet.ps1 --info
```

Build and test the repository without depending on a product-generation filename:

```powershell
$sln = (Get-ChildItem -File *.slnx | Select-Object -First 1).FullName
.\scripts\dotnet.ps1 build $sln -c Release
.\scripts\dotnet.ps1 test  $sln -c Release --no-build
```

Verify the embedded COM knowledge pack:

```powershell
node scripts/build-knowledge-pack.mjs --check
```

Run the Node SDK gates:

```powershell
node --test sdk/node/test/sdk.test.mjs
node sdk/node/test/typecheck.mjs
```

Stage an immutable development runtime:

```powershell
.\scripts\stage-runtime-dev.ps1
```

Build a production release:

```powershell
.\scripts\release.ps1 -Version <semver>
```

Production packaging refuses dirty or untracked source by default. Development validation can explicitly permit a dirty tree, and the resulting manifest records that provenance. Production release transactions cannot skip the deterministic test gate.

---

## Repository layout

| Path | Purpose |
|---|---|
| `src/ComTool.Protocol/` | Versioned request/result/value contracts |
| `src/ComTool.Runtime/` | Operation catalog, examples, artifacts, and runtime-owned semantics |
| `src/ComTool.RuntimeHost/` | Persistent RuntimeHost entry point |
| `src/ComTool.Supervisor/` | Routing, leases, mutation state, workflows, host lifecycle, and recovery |
| `src/ComTool.Worker/` | Dedicated host worker process |
| `src/ComTool.Hosts.Abstractions/` | Host capability, launch, attach, and session contracts |
| `src/ComTool.Hosts.Illustrator/` | Illustrator COM, script, debugger, and plug-in adapter |
| `src/ComTool.Knowledge/` | Embedded Illustrator COM knowledge pack and query service |
| `src/ComTool.Transport.*` | Pipe, stdio, and MCP transport adapters |
| `src/ComTool.Cli/` | Thin command-line client over RuntimeHost |
| `sdk/node/` | Dependency-free Node client and orchestration layer |
| `schemas/` | JSON Schema contracts |
| `protocol/operation-registry.json` | Checked-in runtime operation-catalog snapshot |
| `migration/` | Historical compatibility and semantic-regression fixtures |
| `tests/` | Deterministic protocol, runtime, host, transport, and compatibility suites |
| `spikes/` | Executable architecture and host-behavior probes |
| `evidence/` | Captured live and release evidence |
| `scripts/` | Toolchain bootstrap, staging, knowledge generation, packaging, and install verification |

Architectural decisions and operational contracts live under [`docs/`](docs/). `PHASE0_STATUS.md` is the executable architecture-gate ledger retained from development.

---

## Known limitations

- Illustrator 30.6.0 is the only complete live-conformance host adapter today.
- Photoshop 2026 has second-host viability evidence, not full feature parity with the Illustrator adapter.
- Same-target Adobe execution is intentionally serialized.
- Generic script and COM escape hatches cannot provide the same narrow semantic guarantees as runtime-owned typed operations.
- Typed layer/artboard mutations have deterministic adapter coverage; live mutation conformance should continue to use disposable documents with exact identity and postcondition verification rather than user artwork.
- Authenticode signing is optional. Unsigned manifests and hashes establish package integrity, not publisher authentication.
- Some internal source filenames and compatibility fixtures preserve development-history naming. They are implementation details, not separate public release lines.

---

## Credits

COMTool builds on Adobe's COM/OLE and ExtendScript host surfaces, the community-maintained [docsforadobe ExtendScript documentation](https://extendscript.docsforadobe.dev/), the sibling [ESON](https://github.com/thelabcorner/eson) structured-data runtime, and [VectorIPC](https://github.com/thelabcorner/vector-ipc) for bounded native plug-in IPC.

The project also benefits from extensive differential and failure-mode testing performed during private development before the first public release.

---

## License

COMTool is licensed under **GPL-3.0-or-later**. See [`LICENSE`](LICENSE).
