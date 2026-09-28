# COM Tool V2 Node SDK

Pure Node.js access to COM Tool V2's **existing persistent Adobe control plane**, with first-party TypeScript declarations for the stable transport/session/runner envelopes.

This package is not a second COM implementation and it is not the definition of COM Tool V2. It speaks newline-delimited JSON to `ComTool.Cli.exe stdio`, which forwards the same versioned `OperationRequest` objects into the installed `RuntimeHost -> RuntimeSupervisor -> Worker -> Adobe host` path used by the other V2 surfaces.

The primary API is deliberately generic so new V2 operations—ExtendScript, Illustrator DOM operations, native-plugin/debugger tooling, agent manipulation, and future Adobe-host capabilities—become usable from Node without changing the transport.

## Generic control surface

```js
import { ComToolClient, unwrapProtocolValue } from '@comtool/v2-node';

const client = new ComToolClient();

const targets = await client.execute(
  'core.targets.list',
  { refresh: true },
  { id: 'my-stable-request-id' }
);

console.log(unwrapProtocolValue(targets.result));

const futureOperation = await client.execute(
  'plugin.future.debug.inspect',
  { address: 0x1234 },
  {
    id: 'debug-inspect-1',
    target: {
      host: 'illustrator',
      id: 'illustrator:opaque-generation-id'
    }
  }
);

await client.close();
```

`ComToolClient.execute(operation, input, options)` accepts arbitrary registered V2 operations. `ComToolRunner.listOperations()` and `describeOperation(name)` query the live RuntimeHost-owned catalog, so an agent can discover the installed runtime's exact operation version, mutation class, target/lease requirements, execution scope, host binding, and mutation-resolution rule instead of trusting package docs or a stale checked-in registry. Target-specific support remains authoritative through `session.capabilities()` / `core.target.capabilities`; catalog membership and live target support are intentionally distinct facts.

The client keeps one persistent stdio proxy alive and correlates concurrent responses by request ID. Completion order is intentionally independent of submission order: the proxy can dispatch overlapping requests over separate pooled runtime-pipe connections, allowing a control/recovery call to complete while another execution request remains blocked. The proxy itself admits bounded concurrent NDJSON requests and leases independent pooled runtime-pipe connections, so a later runtime-only control request is not head-of-line blocked behind a hung host execution.

It does not retry requests after bytes may have been submitted. Transport loss after submission is classified conservatively as ambiguous. `ComToolTransportError` preserves `submitted`, `requestId`, and `operation` for every in-flight request it rejects, including auto-generated IDs, so callers can inspect/reconcile the exact durable request instead of inventing a new mutation identity.

`execute()` / `request()` also accept an `AbortSignal` and `responseTimeoutMs`. These control only how long the Node caller waits for a response; they do **not** cancel an operation already submitted to RuntimeHost or Adobe. Stopping the wait after submission therefore returns `transport_ambiguous`, preserves the exact request ID, leaves the persistent stdio transport usable by other requests, and never causes an automatic replay. A late response is surfaced as an orphaned-response event rather than being misattributed to another request.

```js
const controller = new AbortController();
const pending = client.execute(
  'core.targets.list',
  { refresh: true },
  {
    id: 'bounded-wait-1',
    signal: controller.signal,
    responseTimeoutMs: 15_000
  }
);
```

## Target-bound universal sessions

`ComToolRunner.openSession()` binds one strong Adobe target generation to a `ComToolTargetSession`. This is the preferred surface for long-lived development agents and `.mjs` harnesses that mix read operations, ExtendScript, COM reads, and future native-plugin/debugger operations. `session.execute()` remains fully generic; convenience methods do not replace the operation protocol.

```js
const runner = new ComToolRunner();
const session = await runner.openSession({ host: 'illustrator' });

const snapshot = await session.snapshot();

const lease = await session.acquireLease({ ttlMs: 300_000 });
try {
  const test = await session.testEval({
    source: '6*7',
    expected: 42
  });

  const future = await session.execute(
    'plugin.future.debug.inspect',
    { address: 0x1234 }
  );
} finally {
  await session.releaseLease();
  await runner.close();
}
```

A bound lease is attached to generic target-session operations unless `useLease: false` is explicitly requested. Conflicting lease IDs are rejected locally. Session lease management is intentionally explicit: an ambiguous mutation does not cause automatic release, cancellation, reconciliation, or replay.

Current thin read conveniences are `status()`, `snapshot()`, `capabilities()`, `comGet()`, and `comCallRead()`. `pluginMessage(plugin, selector, input)` is the thin native plug-in RPC convenience: it requires an explicitly bound lease and delegates one `plugin.message` request to RuntimeHost; it does not load a plug-in, speak Illustrator COM, or retry a submitted call from Node. `runFile()`, `runEval()`, `testFile()`, and `testEval()` reuse the same bound target and lease. `refreshTarget()` re-reads the exact strong target ID and rejects silent process-generation substitution.

For post-ambiguity lifecycle work, the session also exposes the runtime-owned reconciliation operations without weakening their contracts:

- `reconcile()` performs generic target reconciliation under the bound lease; it does not clear a durable mutation incident merely because the host is live.
- `reconcileMutation()` requires the durable incident request ID, exact expected target-state revision, and the **exact original non-empty postcondition list**. The runtime remains responsible for fingerprint matching and live read-only verification.
- `resolveIncident()` is the explicit resolution path and requires incident ID, expected revision, `known_changed` / `known_unchanged`, and a non-empty rationale. The SDK does not infer or invent any of those values.

Call `refreshTarget()` first when an agent needs the current `runtimeState` / `activeIncident` metadata before choosing a reconciliation path.

When the exact incident generation is no longer running and therefore cannot
hold a target lease, use the runner-level offline path instead of weakening the
target-session contract: `listIncidents()` returns unresolved durable incidents,
and `resolveOfflineIncident()` forwards `core.incident.resolve` with the exact
`targetId`, incident request ID, `expectedUpdatedAt` revision token, explicit
`known_changed` / `known_unchanged` resolution, and rationale. The runtime
still verifies that the recorded PID+process-start generation is no longer
running before it will resolve the incident.

Runtime-owned large-result artifacts are retrieved by opaque ID only.
`describeArtifact()` returns the immutable descriptor, `readArtifact()`
performs one explicitly bounded protocol read, and `readArtifactAll()` pages
the artifact in at most 512 KiB chunks, pins the descriptor across every page,
reassembles in order, and verifies the final SHA-256 before returning the bytes.
This runtime artifact surface is separate from the optional local JSON run-log
files produced by `writeArtifact()` / `artifactDir`.

## ExtendScript run/test orchestration

`ComToolRunner` layers ergonomic script execution on top of the generic client. `runFile()` performs target discovery, file SHA-256 pinning, target-lease acquisition, stable request-ID creation, structured classification, cleanup, and optional JSON artifact emission while still dispatching exactly one `script.runFile` operation through the ordinary runtime path. `runEval()` provides the same orchestration for inline `script.eval`; its result metadata records a SHA-256 and character count for the source without persisting the source text itself.

```js
import { ComToolRunner } from '@comtool/v2-node';

const runner = new ComToolRunner({
  onEvent(event) {
    process.stderr.write(JSON.stringify(event) + '\n');
  }
});

const run = await runner.runFile({
  path: './probe.jsx',
  args: ['lane-v5'],
  effects: 'unknown',
  watchdogMs: 180_000,
  artifactDir: './artifacts'
});

console.log(run.classification, run.exitCode, run.value);
await runner.close();
```

### Caller-controlled watchdog

`watchdogMs` is forwarded to V2 as `policy.workerWatchdogMs`; it is not replaced by the old 60-second broker default.

Supported range:

- minimum: 100 ms
- maximum: 3,600,000 ms (1 hour)
- runner default: 60,000 ms

For runner-owned leases, the SDK automatically chooses a lease TTL long enough to cover the watchdog plus recovery grace. The current maximum lease lifetime is 3,720,000 ms. Multi-operation Node programs can explicitly call `acquireLease()`, `renewLease()`, and `releaseLease()` and pass the same lease ID to later operations.

For hot test loops, select the strong target once and pass that exact object back as `target`. The runner then skips redundant `core.targets.list` discovery; RuntimeHost remains authoritative and still validates the target generation on every operation.

```js
const target = await runner.selectTarget();
const lease = await runner.acquireLease(target, { ttlMs: 300_000 });
try {
  await runner.testEval({
    target,
    leaseId: lease.id,
    source: '6*7',
    expected: 42
  });
  await runner.testFile({
    target,
    leaseId: lease.id,
    path: './next-test.jsx',
    expected: { ok: true }
  });
} finally {
  await runner.releaseLease(target, lease.id);
}
```

`responseTimeoutMs` is deliberately separate from `watchdogMs`: it is only a caller-side response-wait bound. A response wait that expires after submission is ambiguous and retains an owned lease for reconciliation/recovery. Likewise, a worker watchdog timeout after dispatch never means "the script did not run." V2 preserves its ordinary started/ambiguous semantics and the runner never silently replays a potentially-mutating script.

## Break-glass host recovery

Killing the COM worker does not guarantee code already executing inside Illustrator has stopped. When a timed-out or transport-interrupted script has an ambiguous outcome, the runner retains its lease instead of silently releasing ownership.

Explicit recovery is available with `terminateHostGeneration(runner, run)`:

```js
import {
  ComToolRunner,
  terminateHostGeneration
} from '@comtool/v2-node';

const runner = new ComToolRunner();
const run = await runner.runFile({
  path: './possibly-pathological.jsx',
  watchdogMs: 180_000
});

if (run.ambiguous && run.lease?.retained) {
  const recovery = await terminateHostGeneration(
    runner,
    run,
    { waitTimeoutMs: 10_000 }
  );

  console.log(recovery);
}

await runner.close();
```

The runtime refuses break-glass termination unless all of the following still match:

- the same active target lease;
- the already-known strong target ID;
- exact process ID;
- exact process start time.

Recovery does not perform fresh COM discovery and does not wait on the ordinary host-operation lane. This is intentional: the lane being recovered may itself be wedged. The stdio proxy also has a separate concurrent runtime-pipe lane available for the control request, so recovery is not serialized behind the blocked `script.runFile` transport round trip. No script is retried after host termination.

## Test helpers

`testFile()` and `testEval()` execute ExtendScript exactly once, then apply a deterministic Node-side assertion to the returned value.

```js
const fileResult = await runner.testFile({
  path: './uuid-tests.jsx',
  watchdogMs: 240_000,
  expected: { ok: true }
});

const evalResult = await runner.testEval({
  kind: 'expression',
  source: '6 * 7',
  expected: 42
});
```

A failed assertion becomes `classification: "test_failed"`; the ExtendScript is not rerun. Inline eval artifacts contain source provenance hashes, not the source body.

## Classification and exit codes

| Exit | Meaning |
|---:|---|
| `0` | operation/test completed successfully |
| `1` | deterministic input, operation, or test failure |
| `2` | execution started or outcome is ambiguous; do not automatically replay |
| `3` | control/transport/cleanup failure that requires inspection |

The structured result contains the exact operation result when available, target generation metadata, SHA-256, configured watchdog, lease disposition, transport diagnostics, and recovery metadata.

Artifact files redact lease IDs.

## Command-line wrapper

The package also includes a thin Node CLI over the same SDK:

```powershell
node .\sdk\node\bin\comtool-run.mjs run .\probe.jsx --timeout-ms 180000 --response-timeout-ms 195000 --effects unknown --artifact-dir .\artifacts
node .\sdk\node\bin\comtool-run.mjs test .\probe.jsx --timeout-ms 180000 --expect-json "{\"ok\":true}"
node .\sdk\node\bin\comtool-run.mjs eval --expr "app.documents.length" --timeout-ms 120000
node .\sdk\node\bin\comtool-run.mjs test-eval --expr "6*7" --expect-json "42"
```

The wrapper also accepts `--lease`, `--lease-ttl-ms`, `--recovery-grace-ms`, `--preconditions-json`, and `--postconditions-json`. `--timeout-ms` is the runtime worker watchdog; `--response-timeout-ms` only stops the Node side from waiting and is ambiguous if the request was already submitted. `--terminate-host-on-ambiguous` is an explicit opt-in break-glass action and is never enabled implicitly.

Use `--cli <path>` or `COMTOOL_V2_CLI_PATH` to override the installed CLI. By default the client resolves:

```text
%LOCALAPPDATA%\Programs\ComToolV2\current\ComTool.Cli.exe
```

## TypeScript

The package ships `index.d.ts` and a type-aware package `exports` map. Operation names and payloads remain intentionally open-ended so future Adobe/plugin/debugger operations are callable immediately through `execute()`; the stable request/result envelope, target identity, leases, runner/session methods, recovery results, and classifications are typed. A strict NodeNext consumer smoke is maintained under `test/types-smoke.ts`.

## Design invariants

- No Adobe COM calls exist in the Node package.
- No parallel worker/runtime implementation exists in the Node package.
- All operations use the existing V2 protocol and persistent runtime.
- Stable request IDs and V2's durable mutation ledger remain authoritative.
- A lost, locally timed-out, or caller-aborted response after submission is treated conservatively.
- Caller wait cancellation never masquerades as runtime/Adobe cancellation.
- Potentially-mutating ExtendScript is never silently retried.
- Lease IDs are not written to runner artifacts.
- The package contains no CI-provider-specific integration; ordinary Node processes and shell exit codes are the automation boundary.
