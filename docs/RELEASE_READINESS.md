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
| CLI stdio proxy | **DONE** | `Cli stdio`; verified against a live runtime + Illustrator target. |
| MCP adapter over the real runtime | **DONE** | `ComTool.Transport.Mcp --self-test` parity pass against a live runtime. |
| Deterministic test gate | **DONE** | See §4; run in Release, no Illustrator required. |
| Wave B operation metadata and lease policy | **DONE** | Fixed catalog entries for controlled artboard naming and document lifecycle; catalog tests verify mutation classes and lease requirements. |
| Durable condition-bound reconciliation | **DONE** | Only the original request's postcondition fingerprint can be reevaluated to clear ambiguity; new conditions cannot be attached afterward. |
| Durable sequential workflow jobs | **DONE (initial slice)** | Submit/get/cancel/resume, step write-ahead state, stable step request IDs, restart-to-interrupted recovery; see README for constraints. |
| Wave 4 read-only structure surfaces | **DONE** | `illustrator.artboard.read` / `illustrator.layer.read` with explicit document selectors; catalogued read-only, no lease, no ledger entry. Live disposable-document conformance captured in `evidence/wave4-live-structure-2026-09-25.json`. |
| Immutable dev staging script | **DONE** | `scripts/stage-runtime-dev.ps1`. |
| Elevation-free install (no Program Files writes) | **DONE (by shape)** | Runtime writes only to the configured state dir / temp. |
| Versioned release manifest / checksums | **DONE** | `scripts/release.ps1` emits a schema-versioned release manifest, wire protocol version, gate provenance, toolchain versions, per-file SHA-256 inventory, archive SHA-256, source commit, dirty-tree provenance, transactional finalization, and immutable release-version enforcement. |
| Installer / uninstaller | **DONE (user-scoped slice)** | `install-user.ps1` verifies package identity, explicit dirty provenance, complete manifest/hash inventory, required entrypoints and containment, rejects Source/InstallRoot overlap before mutation, serializes install/uninstall operations cross-process, supports explicit `-Repair` for damaged immutable versions, and installs side-by-side under `%LOCALAPPDATA%\\Programs\\ComToolV2`. A stable `current\\` junction gives CLI/runtime/MCP consumers version-independent executable paths; install/uninstall reject a `current` junction whose resolved target is not a direct child of the managed `versions\\` root. `uninstall-user.ps1` refuses live installed processes, preserves state by default, can recover current selection from the junction when pointer metadata is corrupt, and makes `-All` independent of `current.json`. |
| Clean-machine shell dependency | **DONE** | Install/uninstall scripts avoid PowerShell 7-only APIs and are validated against stock Windows PowerShell 5.1; the self-contained runtime itself requires no machine-wide .NET install. |
| State-dir migration / versioning | **DONE (V1 contract)** | `state-manifest.json` versions the root before durable stores open. Unsupported schemas and the incompatible pre-V1 custom mutation-ledger layout fail closed rather than silently hiding unresolved mutation state. |
| Default state-dir path finalized | **DONE** | `%LOCALAPPDATA%\\ComToolV2`; custom `--state-dir` / `COMTOOL_V2_STATE_DIR` denotes the complete state root, with `mutation-ledger/` and `workflows/` beneath it. The root is single-writer across processes via `.runtime-owner.lock`; a second runtime using the same state root fails closed with `runtime_state_in_use`. |
| Code signing | **OPTIONAL / NOT REQUIRED FOR PRODUCTION** | Production releases are intentionally allowed to ship unsigned. `release.ps1` still supports optional Authenticode signing when a certificate is available. For unsigned packages, the manifest inventory and archive checksum detect corruption/inconsistency but do **not** authenticate the publisher; the archive/checksum must be obtained through a trusted distribution channel. |
| Illustrator restart/reconnect gate 0A closure | **MISSING** | Requires live host restart; not a packaging item. |
| MCP SDK version pin documented in release | **DONE** | Current releases embed `ModelContextProtocol 2.2.0` and the exact pinned .NET SDK version as toolchain provenance. |

## 4. Deterministic verification gate (no Illustrator)

From `comtool-v2/`:

```powershell
.\scripts\dotnet.ps1 build ComTool.V2.slnx -c Release
.\scripts\dotnet.ps1 test  ComTool.V2.slnx -c Release --no-build
```

Current result: **296/296 passing**, 0 failed. Suites cover protocol, runtime
catalog/policy, supervisor and durable reconciliation, Illustrator adapters,
pipe, stdio, and the MCP bridge. These deterministic tests require no Adobe
host.

The release schema gate is separately **31/31 passing** and includes direct
validation of the checked-in schemaVersion 2 operation-registry snapshot
against `schemas/v1/operation-registry.schema.json`.

Production packaging uses scripts/release.ps1 and refuses a dirty/untracked V2
source tree by default. The -AllowDirty switch exists only for development
validation and marks the release manifest dirty; install-user.ps1 independently
refuses a dirty package unless -AllowDirtyPackage is explicitly supplied.
Production releases cannot use `-SkipTests`; that switch is restricted to
explicit dirty validation builds.
Authenticode is optional. When `-SignToolPath` and
`-SigningCertificateSha1` are supplied together, every first-party
`ComTool.*.exe/.dll` is signed and verified and the installer revalidates the
manifest-declared signed-file set and signer thumbprint. Without those options, the same package is valid for production. Its manifest,
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
