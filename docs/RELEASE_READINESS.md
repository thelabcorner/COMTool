# Release and Clean-Machine Readiness

**Status:** Living checklist  
**Date:** 2026-09-26  
**Scope:** Production transports, operation catalog, and current Wave B/Wave 4 readiness.

This document tracks what is required to ship COM Tool V2 to a clean Windows
machine and which items are verified versus still missing. Nothing here changes
host-facing behavior; it is packaging and layout only.

## 1. Ship layout

All executable artifacts, one directory, worker beside runtime:

```text
comtool-v2/
  ComTool.Cli.exe                 # agent/operator front end
  ComTool.RuntimeHost.exe         # persistent runtime (pipe or --stdio)
  ComTool.Worker.exe              # STA COM worker (spawned by the runtime)
  ComTool.Transport.Mcp.exe       # optional MCP adapter
  aipdebugctl.exe                 # packaged optional AIPDebug/VectorIPC native helper
  ComTool.*.dll                   # protocol / runtime / supervisor / transports
  *.runtimeconfig.json
  *.deps.json
```

`RuntimeHost` and `Cli` resolve `ComTool.Worker.exe` from
`AppContext.BaseDirectory`, so worker and runtime host must be colocated.
`--worker` / `COMTOOL_V2_WORKER_PATH` override this for shadow/dev deployments.

## 2. Packaging model

- Gate 0B proved that a self-contained `win-x64` single-file executable is
  viable (~70.12 MiB, median startup ~63.53 ms / 20 runs). That remains
  historical architecture evidence, not the current multi-process ship layout.
- Production packaging publishes CLI, RuntimeHost, Worker, and MCP as
  self-contained `win-x64` entry points, merges their shared runtime payload
  into one package directory, hashes every inventoried payload file, writes an
  archive SHA-256, and finalizes the version directory transactionally only
  after packaging succeeds. Clean production releases revalidate both HEAD and
  V2 worktree cleanliness immediately before finalization so build hooks or
  concurrent edits cannot silently invalidate source provenance.
- Before an archive may finalize, `release.ps1` invokes stock Windows
  PowerShell 5.1 against the staged package itself. That gate installs through
  the packaged installer, launches the installed RuntimeHost/Worker, verifies
  runtime version/health through the installed CLI, proves live-runtime
  uninstall refusal, exercises damaged-version `-Repair`, proves both
  Source/InstallRoot overlap orientations fail before mutation, rejects both a
  non-junction `current` path and a junction poisoned to target outside
  `versions\`, corrupts `current.json`, and proves
  `uninstall-user.ps1 -All` can still recover.
- `release.ps1` is a PowerShell 7+ build/release tool. The shipped installer
  and uninstaller are intentionally Windows PowerShell 5.1 compatible.
- The project-local SDK (`.dotnet/`, pinned `10.0.401`) touches neither global
  PATH nor the registry; it is a build-time tool and is **not** shipped.
- NuGet package resolution is lockfile-backed for every project. Generic
  solution restores use `packages.lock.json`; RID-specific restores use
  `packages.<rid>.lock.json` so the generic TFM graph and self-contained
  shipping graph cannot overwrite each other. Normal development restore can
  deliberately update lockfiles, while `scripts/release.ps1` requires both
  the generic graph and each shipping `win-x64` graph to restore in locked
  mode before publish. The RID lockfile path is passed explicitly to NuGet so
  project-reference evaluation cannot silently fall back to the generic file.
- Release packaging also runs the machine-readable NuGet vulnerability audit
  across direct and transitive dependencies and refuses to finalize when the
  audit command fails or reports any vulnerability records. The audit result
  and package source are embedded into the release manifest.
- Development deployments must be immutable shadows:
  `scripts/stage-runtime-dev.ps1` copies runtime + worker into
  `.artifacts/dev-runtime/<timestamp>/{runtime,worker}`. Never run a long-lived
  runtime from `bin/Release` (Windows locks loaded assemblies and breaks builds).
- No network listener is part of V2. The runtime pipe is `CurrentUserOnly`;
  the supervisor→worker pipe uses an ephemeral 256-bit token plus child
  PID + creation-time verification.
- The public runtime pipe intentionally treats the current Windows user as the
  local trust boundary; it does not add a second application-level credential
  between same-user client processes and the runtime. This is not a
  cross-user/network boundary and should not be exposed through a relay that
  weakens Windows identity isolation. The internal supervisor→worker channel
  remains separately authenticated because it is a child-process integrity
  boundary.

## 3. Clean-machine checklist

| Item | State | Notes |
|---|---|---|
| Self-contained single-file feasibility proven | **DONE** | Historical Gate 0B evidence; current production packaging is the merged multi-entrypoint layout described above. |
| Runtime resolves sibling worker | **DONE** | `RuntimeHost.ResolveWorkerPath` / `Cli.ResolveWorkerPath`. |
| Runtime single-instance per pipe | **DONE** | Mutex guard; a second instance for the same pipe exits code 3. Distinct custom pipes may coexist only when they use distinct durable state roots. |
| Current-user-only pipe | **DONE** | `PipeOptions.CurrentUserOnly`. |
| Runtime local trust boundary documented | **DONE** | Same-user clients are trusted at the runtime pipe; there is no network listener or cross-user access. Internal workers additionally require the ephemeral bootstrap token and verified child identity. |
| Stdio front end over the same supervisor | **DONE** | `RuntimeHost --stdio`; verified `core.runtime.health` live. |
| CLI stdio proxy | **DONE** | `Cli stdio`; bounded-concurrent NDJSON dispatch with request-ID correlation and pooled single-flight runtime-pipe clients. `CliStdioConcurrencyTests` process-spawns the real CLI and proves `core.target.host.terminate` returns while an earlier `script.runFile` request is still deliberately blocked. |
| ExtendScript debugger session surface | **DONE (live-proven)** | `debug.session.open` / `debug.session.command` / `debug.session.close` over the Adobe ExtendScript Debugger (`estk3`). The debugger resource is Worker-owned and generation-scoped: the worker pins the exact native addon bytes *before* the bridge child exists and the bridge re-hashes that exact path immediately before `require()`, so replaced addon bytes fail closed instead of loading different debugger code than the reported provenance. All three operations are catalogued `external_side_effect` with `requiresLease: true` and `mutationResolution: fixed`, so they are never replayed. One session per worker; `close` deliberately skips the target-generation check so cleanup still happens when the host died. Live proof: `evidence/illustrator-debugger-live-proof-2026-09-26.json`. |
| Pure Node programmatic surface | **DONE (production-capable initial surface)** | Dependency-free `sdk/node` generic `execute()` client over persistent CLI NDJSON → RuntimeHost; correlation-safe concurrency; local AbortSignal/response-wait bounds with conservative post-submit ambiguity; explicit target lease acquire/renew/release; generic strong-target `ComToolTargetSession`; `runFile` / `testFile` and `runEval` / `testEval` orchestration; structured classifications/artifacts; custom worker watchdogs; and explicit generation-pinned break-glass recovery. The target session can bind one strong Adobe generation and issue arbitrary current/future operations without another COM implementation. No CI-provider logic exists in the package. |
| Caller-controlled worker watchdog | **DONE** | `policy.workerWatchdogMs` is enforced end-to-end at 100–3,600,000 ms. The RuntimeSupervisor forwards it to the existing worker path instead of silently using the broker's 60 s default. |
| Wedged-host break-glass recovery | **DONE** | `core.target.host.terminate` bypasses the blocked COM/operation lane, requires the same lease and exact PID/start-time/executable generation, extends that lease for the bounded recovery window without shortening a longer lease, never performs fresh COM discovery, and never retries the ambiguous script. A null/unreadable OS identity is deliberately **unproven**, not evidence that the process is gone: termination is refused and the durable `Owned` launch record is left unchanged for explicit reconciliation. Transport liveness is proven through the real CLI stdio proxy: a blocked execution request cannot monopolize the only recovery path. |
| MCP adapter over the real runtime | **DONE** | `ComTool.Transport.Mcp --self-test` parity pass against a live runtime. |
| Deterministic test gate | **DONE** | See §4; run in Release, no Illustrator required. |
| Wave B operation metadata and lease policy | **DONE** | Fixed catalog entries for controlled artboard naming and document lifecycle; catalog tests verify mutation classes and lease requirements. |
| Durable condition-bound reconciliation | **DONE** | Only the original request's postcondition fingerprint can be reevaluated to clear ambiguity; new conditions cannot be attached afterward. |
| Durable sequential workflow jobs | **DONE (initial slice)** | Submit/get/cancel/resume, step write-ahead state, stable step request IDs, restart-to-interrupted recovery; see README for constraints. |
| Illustrator workflow concurrency contract | **DONE (architecture constraint)** | One strong Illustrator target remains a capacity-1 host-execution resource. Future DAG scheduling may overlap runtime-only or genuinely different-target work; same-target batching means one compiled host dispatch, not parallel Illustrator execution. See `docs/WORKFLOW_CONCURRENCY_MODEL.md`. |
| Wave 4 read-only structure surfaces | **DONE** | `illustrator.artboard.read` / `illustrator.layer.read` with explicit document selectors; catalogued read-only, no lease, no ledger entry. Live disposable-document conformance captured in `evidence/wave4-live-structure-2026-09-25.json`. |
| Opaque artifact retrieval | **DONE (runtime API + bounded producer streaming)** | `core.artifact.describe` / `core.artifact.read` expose immutable runtime artifacts by opaque ID with bounded range reads and no filesystem-path authority. Public transports and the authenticated runtime↔worker broker all retain the 1 MiB frame ceiling. Successful host result payloads at or above 32 KiB stream from the worker as correlated 512 KiB raw chunks; the sole supervisor validates order/count/kind/SHA-256, reconstructs the canonical result for mutation-ledger finalization, then alone commits it to the artifact store and returns an opaque envelope. The worker never writes artifact-store files; payloads beyond the 64 MiB artifact ceiling fail with completed execution truth rather than truncation or replay. |
| Typed richer Illustrator mutations | **DONE (deterministic initial slice)** | Fixed lease-owned `illustrator.layer.setName/setVisible/setLocked/setOpacity` and `illustrator.artboard.setRect`; explicit selectors, bounded values, and no caller-supplied COM member/path. Missing selectors and duplicate-name ambiguity are distinct NotStarted outcomes; resolved document/layer/artboard identity is revalidated immediately before the fixed property put so observed collection drift fails NotStarted rather than silently retargeting. Mutating `Started` outcomes escalate the target to reconciliation-required, and direct ledger tests prove typed idempotent writes persist PID+process-start generation identity and replay only within that exact target generation. Live disposable-document mutation conformance remains a separate proof item. |
| Packaged AIPDebug VectorIPC helper | **DONE (packaging + deterministic semantics)** | Releases stage `aipdebugctl.exe` beside the worker, hash it in the manifest after optional signing, and verify it through the installed stable path. Both staged and installed helper usage smokes are bounded to 5 s / 64 KiB, package verification runs under stock Windows PowerShell 5.1, and the same verifier runs against the extracted shipped ZIP. At runtime a packaged sibling is re-hashed against `release-manifest.json` and cannot be replaced by the unpackaged-development `COMTOOL_AIPDEBUGCTL` override. Direct IPC requires worker-owned endpoint provenance bound to the exact target generation and passes both expected PID and process-start FILETIME to the native peer verifier; caller endpoint substitution is refused, effectful control never silently falls back, and post-dispatch malformed/invalid control responses remain ambiguous rather than certifying replay safety. |
| Immutable dev staging script | **DONE** | `scripts/stage-runtime-dev.ps1`. |
| Elevation-free install (no Program Files writes) | **DONE (by shape)** | Runtime writes only to the configured state dir / temp. |
| Versioned release manifest / checksums | **DONE** | `scripts/release.ps1` emits a schema-versioned release manifest, wire protocol version, gate provenance, toolchain versions, per-file SHA-256 inventory, archive SHA-256, source commit, dirty-tree provenance, transactional finalization, and immutable release-version enforcement. The release transaction fingerprints every Git-visible V2 source byte and revalidates that fingerprint before/after each entrypoint publish, so concurrent source edits abort before mixed-generation publish outputs can be merged. |
| Installer / uninstaller | **DONE (user-scoped slice)** | `install-user.ps1` verifies package identity, explicit dirty provenance, complete manifest/hash inventory, required entrypoints and containment, rejects Source/InstallRoot overlap before mutation, serializes install/uninstall operations cross-process, supports explicit `-Repair` for damaged immutable versions, and installs side-by-side under `%LOCALAPPDATA%\\Programs\\ComToolV2`. A stable `current\\` junction gives CLI/runtime/MCP consumers version-independent executable paths; install/uninstall reject a `current` junction whose resolved target is not a direct child of the managed `versions\\` root. `uninstall-user.ps1` refuses live installed processes, preserves state by default, can recover current selection from the junction when pointer metadata is corrupt, and makes `-All` independent of `current.json`. |
| Clean-machine shell dependency | **DONE** | Install/uninstall scripts avoid PowerShell 7-only APIs and are validated against stock Windows PowerShell 5.1; the self-contained runtime itself requires no machine-wide .NET install. |
| State-dir migration / versioning | **DONE (V1 contract)** | `state-manifest.json` versions the root before durable stores open. Unsupported schemas and the incompatible pre-V1 custom mutation-ledger layout fail closed rather than silently hiding unresolved mutation state. |
| Default state-dir path finalized | **DONE** | `%LOCALAPPDATA%\\ComToolV2`; custom `--state-dir` / `COMTOOL_V2_STATE_DIR` denotes the complete state root, with `mutation-ledger/` and `workflows/` beneath it. The root is single-writer across processes via `.runtime-owner.lock`; a second runtime using the same state root fails closed with `runtime_state_in_use`. |
| Code signing | **OPTIONAL / NOT REQUIRED FOR PRODUCTION** | Production releases are intentionally allowed to ship unsigned. `release.ps1` still supports optional Authenticode signing when a certificate is available. For unsigned packages, the manifest inventory and archive checksum detect corruption/inconsistency but do **not** authenticate the publisher; the archive/checksum must be obtained through a trusted distribution channel. |
| Illustrator restart/reconnect gate 0A closure | **DONE (live-proven)** | With zero open documents, Illustrator was quit gracefully and relaunched. The same already-running packaged RuntimeHost discovered a new strong target generation (PID 79016 -> 79188), and a fresh STA .NET Gate 0A probe reattached and executed `DoJavaScript("1+1") -> 2`. Evidence: `evidence/gate-0a-restart-reconnect-2026-09-27.json`. |
| MCP SDK version pin documented in release | **DONE** | Current releases embed `ModelContextProtocol 2.2.0` and the exact pinned .NET SDK version as toolchain provenance. |

## 4. Deterministic verification gate (no Illustrator)

From `comtool-v2/`:

```powershell
.\scripts\dotnet.ps1 build ComTool.V2.slnx -c Release
.\scripts\dotnet.ps1 test  ComTool.V2.slnx -c Release --no-build
```

Current result: **835/835 passing**, 0 failed. Suites cover protocol (29), runtime
catalog/policy (182), supervisor and durable reconciliation (192), generation-pinned
host recovery, Illustrator adapters including debugger, VectorIPC, generic COM, and
typed-mutation semantics (308), embedded COM knowledge/inventory validation (39),
runtime IPC (8), pipe (7), stdio (19), MCP bridge (9), and migration/parity (42).
These deterministic tests require no Adobe host.

The pure Node SDK has a separate deterministic gate: **20/20 passing**. It proves
generic arbitrary-operation forwarding over persistent NDJSON; target-bound universal
session lifecycle and explicit lease ownership; concurrent out-of-order response
correlation by request ID; pre-submit abort; post-submit local wait timeout/abort
ambiguity without poisoning the transport; lease renewal; strong-target reuse;
SHA-pinned `script.runFile`; source-hashed `script.eval`; retained-lease handling
for ambiguous execution plus explicit generation-pinned recovery; preservation of
the exact request ID/operation on transport ambiguity; and one-shot `testFile` /
`testEval` assertion behavior without replay; and that a target-bound debugger
session is pure orchestration, with no Node-side Adobe/debugger implementation.
The same SDK gate also exercises opaque runtime-artifact retrieval, including
descriptor-pinned paged reassembly with final SHA-256 verification, and the
dead-generation incident list/resolution convenience path.

The release schema gate is separately **41/41 passing** and includes direct
validation of the checked-in schemaVersion 2 operation-registry snapshot
against `schemas/v1/operation-registry.schema.json`.
The release path also regenerates the embedded COM knowledge pack in memory
from the pinned read-only SQLite inventory and fails if the committed pack
differs byte-for-byte, so knowledge provenance cannot silently drift between
deterministic tests and packaging.

Production packaging uses scripts/release.ps1 and refuses a dirty/untracked V2
source tree by default. The -AllowDirty switch exists only for development
validation and marks the release manifest dirty; install-user.ps1 independently
refuses a dirty package unless -AllowDirtyPackage is explicitly supplied.
Production releases cannot use `-SkipTests`; that switch is restricted to
explicit dirty validation builds.
Authenticode is optional. When `-SignToolPath` and
`-SigningCertificateSha1` are supplied together, every first-party
`ComTool.*.exe/.dll` plus the packaged `aipdebugctl.exe` native helper is
signed and verified and the installer revalidates the manifest-declared
signed-file set and signer thumbprint. Without those options, the same package is valid for production. Its manifest,
per-file SHA-256 inventory, and archive checksum provide corruption and
consistency detection, not publisher authentication.

`core.runtime.health` reports both the runtime assembly version and
informational version along with the durable-state schema version, allowing a
running installed runtime to be correlated with release provenance.

Current validated production artifact: **0.1.1 / win-x64**, built from clean
source commit `81ac72986652c95beb03d28773e71a9ddc7c2a2c`, unsigned by policy,
with zero NuGet vulnerability records. Archive SHA-256:
`cfde0b6c0905a24200a5de1d345ccbb122dfe25a93dbde22b6785e8d09cef8fc`.
The packaged installer and uninstaller were exercised directly under Windows
PowerShell `5.1.22621.6133`, and the installed RuntimeHost reported
`0.1.1+81ac72986652c95beb03d28773e71a9ddc7c2a2c`.
That artifact is historical evidence and intentionally immutable; newer
installer/state-root/package-gate hardening in the current source tree requires
a new release version rather than rewriting 0.1.1.

Current dirty-tree validation artifact: **0.1.2-dev.20260927.20 / win-x64**, built
successfully **after relocating the source tree to `/scripts/comtool-v2` and closing
Gate 0A with live restart/reconnect evidence**. Source commit is
`65ec79f776c8f89eed468401d7c5ca3253434eeb`, source-tree fingerprint is
`91786157d1162b817ab485b840a7de9debfc81e2a1aa45d542411e3ca709795d`, and archive
SHA-256 is `761556cef3709d761182a0731c2ea26c7679740e04391e01af5e14a69d4a83bf`
(39,339,559 bytes). It is development evidence, not a clean production release.
Its full 835/835 .NET gate, 20/20 Node SDK gate, TypeScript 5.9.3 check, 41/41
schema gate, staged package, and extracted shipped ZIP all passed from the new
root; package verification used stock Windows PowerShell `5.1.22621.6133`. The
260-file release manifest records zero NuGet vulnerability findings, all four
required executable entrypoints, the dependency-free Node SDK, and the packaged
`aipdebugctl.exe` VectorIPC helper.

A subsequent inventory-provenance audit found that the committed raw COM JSON and
its committed manifest had different SHA-256 values despite producing identical
semantic rows across every indexed table. The canonical SQLite index and manifest
were rebuilt from the current raw JSON, V2's embedded knowledge pack was
regenerated from that exact revision, and the parent COM skill validator now
passes with `source_hash_matches: true`. The next release gate therefore validates
the corrected provenance chain rather than silently accepting that historical
metadata drift.

Additional lane-3 release-path proof used dirty validation version
`0.0.0-lane3.20260927.1535 / win-x64` from the same source commit. Its source
tree remained byte-stable across every guarded entrypoint publish, its staged
package and extracted shipped ZIP both passed stock Windows PowerShell
`5.1.22621.6133` verification, and both installed the manifest-hash-verified
`aipdebugctl.exe` through the stable `current\` path and executed the bounded
usage smoke. Archive SHA-256:
`c55789fd735743f9ceda7406085afd2fad5c33a80952dca92ed08027ed515527`.

The lane-1/3/5 closure pass additionally produced dirty validation artifact
`0.0.0-lane135.20260927.1554 / win-x64` from source commit
`65ec79f776c8f89eed468401d7c5ca3253434eeb`, source-tree fingerprint
`bd2a1075e26936b9a58db20a8771601142e4af0eda9697ea32fae06f06de08e7`.
The release path itself passed the deterministic knowledge-pack drift check and
41/41 schema gate, staged 260 files, then passed both staged-package and shipped
ZIP smoke under Windows PowerShell `5.1.22621.6133`. Archive SHA-256:
`5f762ba180cd8b0e5b3f186b17bc16b0ecf6de26a147af6a6ed622fada7faf5d`.
This was an explicit `-AllowDirty -SkipTests` packaging validation; the
independent full 834/834 .NET gate plus 20/20 Node and TypeScript gates above
were run immediately before it. A direct import of the packaged Node SDK also
confirmed the shipped `readArtifactAll()` helper and its 512 KiB protocol-read
bound.

The earlier **0.1.2-dev.20260926.8** validation artifact also has a separate
live-package SDK proof: that exact packaged RuntimeHost was launched on an
isolated pipe/state directory and driven through its **shipped** Node SDK plus
packaged CLI. Runtime health reported
`0.1.2-dev.20260926.8+65ec79f776c8f89eed468401d7c5ca3253434eeb`.
The newer `.19` artifact above has deterministic, staged-package, and shipped-ZIP
proof from the relocated `/scripts/comtool-v2` root; it has not been substituted for `.8` in this historical live-host evidence.
`ComToolRunner.openSession()` discovered the existing Illustrator 30.6.0 process
by strong PID/start-time identity, and the bound session successfully executed
`core.target.status` plus `core.target.snapshot` against the real host. This
live proof specifically closes the former `System.Array.Count` snapshot failure:
the active `Untitled-1` document reported 1 layer, 2 artboards, 0 page items,
0 path items, and 0 selected items. The session then acquired a 180 s lease,
executed one harmless `testEval` (`6*7` → `42`) under stable request ID
`dev8-session-test-eval`, and explicitly released the lease. No document mutation,
replay, or break-glass termination was used. See
`evidence/node-sdk-live-package-2026-09-26.json`.

A bounded live **debugger** proof then ran through the ordinary
RuntimeHost -> Supervisor -> Worker path only (Node SDK -> `ComTool.Cli.exe stdio`
-> isolated runtime pipe -> worker -> host adapter); the bridge and the Node child
were never invoked directly. Against the already-running Illustrator 30.6.0
process it re-verified the strong generation (PID 67764, start
`2026-09-26T22:18:25.9950628Z`) before doing anything else, acquired a 120 s lease,
then completed:

| Request ID | Operation | Result |
|---|---|---|
| `dbgproof-health` | `core.runtime.health` | completed |
| `dbgproof-target-status` | `core.target.status` | completed — 30.6.0 build 109R, 1 document |
| `dbgproof-debug-open` | `debug.session.open` | completed — session `dbg-b52486361b9446e3aec603b0e5cc4db2`, `appSpec illustrator-30.064`, engines `main`/`transient`, `estk3`, worker-owned, child PID 87204 |
| `dbgproof-debug-get-breakpoints` | `debug.session.command` (`get-breakpoints`) | completed — one harmless read, ESD replied `<breakpoints flags="0" engine="main"/>` |
| `dbgproof-debug-close` | `debug.session.close` | completed — `graceful: true`, `exitObserved: true` |
| (lease) | `core.target.lease.acquire` / `release` | acquired, then released; `held: false` |

The reported provenance matched independently recomputed hashes: addon SHA-256
`6295c57e95002e4c10a4081ee14ef86e35d081213dcc3553b34de51c44e4aaa2` and bridge
SHA-256 `16cc581e7b3be548e510695aa2125c31668c964784a4f3a5e915bd3b71497374`.
There was no ambiguity (`ambiguous: false`) and therefore no retry; no document was
created, renamed, saved, or closed; no break-glass host termination was used; the
debugger child was observed gone after `close`; and the isolated state root ended
with no unresolved incident. See
`evidence/illustrator-debugger-live-proof-2026-09-26.json`
(SHA-256 `55d5d78810802c8417e0bab20c634b8949649af69bead9f549a6a12037b6b4c2`).

Debugger-child containment is intentionally *not* duplicated. The bridge child is
spawned by the target worker, and `DebuggerBridge.Close` already uses
`Kill(entireProcessTree: true)`; the Supervisor's
`ForceAbortCurrentWorkerAsync` -> `WorkerBrokerClient` path already terminates the
worker with `entireProcessTree: true`, so containment is transitive. By contrast
`core.target.host.terminate` deliberately uses `entireProcessTree: false` on the
Illustrator process itself so the user's own host tree is never killed.

### Non-promoting package validation (no install, publish, or promotion)

A dirty-tree validation package was built to the `release.ps1` layout without any
promoting stage: the four self-contained `win-x64` entry points were published and
merged, the installer/uninstaller and `ATTRIBUTION.md` were copied, and the
dependency-free `sdk/node` runtime surface was staged without its test harness.
No release manifest was finalized, no archive was produced, `verify-package.ps1`
was deliberately not run, and no installer/uninstaller action, `current` pointer
change, or channel promotion occurred.

| Fact | Value |
|---|---|
| Package root | `.artifacts/package-validation/20260926-174656-15d55ce4/package` |
| File count | 258 |
| Package tree SHA-256 | `675b500872e25980ae72fb604138713c29e4a8c841510d43f62074fa0f5e5370` |
| `package-validation-summary.json` SHA-256 | `18f9b18d39911cb224991067132d27d98c22790ace970ffd5dfc2a8c9b782f07` |
| Entrypoints | `ComTool.Cli.exe`, `ComTool.RuntimeHost.exe`, `ComTool.Worker.exe`, `ComTool.Transport.Mcp.exe` all present |
| Packaged runtime catalog | exactly `debug.session.open` / `debug.session.command` / `debug.session.close`, each `ExternalSideEffect` + `requiresLease` + `Fixed` |
| Packaged `ComTool.Hosts.Illustrator.dll` | SHA-256 `b95779471e441f81cd69ea4962c49e38de703c7288b42e4068c9a374e7ef8c9f`, embedded `esd-debugger-bridge.mjs` resource plus the debugger manager surface |
| TypeScript declaration smoke | passed — `ComToolDebugSession` (15 debugger methods), `openDebugger`, generic `execute` all declared in the shipped `index.d.ts` |
| Packaged SDK JavaScript smoke | passed — shipped module exports the debugger/session surface and enforces session/command identity guards |
| Windows PowerShell 5.1 | `5.1.22621.6133`; both packaged scripts parse with 0 errors; no PS7-only API; **no installer action executed** |
| Verifier result | 18/18 checks pass |

Two independent builds of this validation package produced byte-identical trees
(same 258 files, same tree SHA-256, zero per-file differences).

## 5. Installed layout and upgrades

The default per-user installation is:

```text
%LOCALAPPDATA%\Programs\ComToolV2\
  current\                         # stable junction to active version
    ComTool.Cli.exe
    ComTool.RuntimeHost.exe
    ComTool.Worker.exe
    ComTool.Transport.Mcp.exe
  current.json                     # validated activation metadata
  versions\
    <version>\                     # immutable verified payload
```

Consumers should configure the stable `current\...` paths, not a
`versions\<version>\...` path. Installing a newer version verifies it before
atomically switching the stable junction/pointer. Removing the current version
selects the newest remaining integrity-verified install as rollback; `-All`
removes program files even when `current.json` is corrupt. Durable runtime
state is separate at `%LOCALAPPDATA%\ComToolV2` and is preserved unless
`-RemoveState -StateRemovalToken DELETE_COMTOOL_V2_STATE` is explicit.

Live-host verification (Illustrator) is separate and must be run where a host is
available; see the evidence files for the current live coverage.

## 6. Clean-machine smoke sequence

With a host running (for host-scoped ops) or without (for runtime-scoped ops):

```powershell
# 1. Runtime-scoped, no host needed
ComTool.RuntimeHost.exe --worker .\ComTool.Worker.exe --pipe smoke
ComTool.Cli.exe health --runtime --pipe smoke

# 2. Stdio front end over the same runtime
'{"protocolVersion":1,"id":"h","operation":"core.runtime.health","input":null}' |
  ComTool.Cli.exe stdio --pipe smoke

# 3. MCP adapter parity
ComTool.Transport.Mcp.exe --self-test --pipe smoke
```

## 7. What still requires a live Illustrator run

- Any host-scoped operation (`core.target.status`, `core.target.snapshot`,
  `com.get`, `com.call.read`, `script.eval`) and its capability report.
- Live dispatch and postcondition verification of `illustrator.artboard.setName`
  and `illustrator.document.create/open/save/saveAs/close` against a disposable
  fixture document; do not use the user's real document as a mutation fixture.
- End-to-end successful `core.target.mutation.reconcile` after a deliberately
  induced ambiguous mutation with matching, durably bound postconditions. A
  live no-incident fail-safe check has passed, but an actual resolve-path
  clearing demonstration remains to be captured.
- Worker/discovery spawn against a real `Illustrator.Application`.
- Gate 0A restart/reconnect closure.

See `docs/TRANSPORT_CLI_MCP_PARITY.md` for the transport/discovery/state layout
that this checklist packages.
