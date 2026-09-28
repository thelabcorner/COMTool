import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  mkdtemp,
  readFile,
  rm,
  writeFile
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

import {
  ComToolClient,
  ComToolRunner,
  ComToolTransportError,
  terminateHostGeneration,
  unwrapProtocolValue
} from '../index.mjs';
import { parseRunnerArgs } from '../lib/cli-options.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const fakeTransport = join(here, 'fake-stdio.mjs');

function createClient() {
  return new ComToolClient({
    transportCommand: {
      command: process.execPath,
      args: [fakeTransport]
    }
  });
}

test('runner CLI parser keeps file/eval modes strict and preserves orchestration controls', () => {
  const evalOptions = parseRunnerArgs('test-eval', [
    '--expr', '6*7',
    '--args-json', '["lane",42]',
    '--timeout-ms', '180000',
    '--retry-budget-ms', '0',
    '--response-timeout-ms', '15000',
    '--recovery-grace-ms', '90000',
    '--lease-ttl-ms', '300000',
    '--lease', 'L'.repeat(64),
    '--request-id', 'cli-eval-1',
    '--preconditions-json', '[]',
    '--postconditions-json', '[]',
    '--expect-json', '42',
    '--terminate-host-on-ambiguous'
  ]);

  assert.equal(evalOptions.evalKind, 'expression');
  assert.equal(evalOptions.source, '6*7');
  assert.deepEqual(evalOptions.scriptArgs, ['lane', 42]);
  assert.equal(evalOptions.watchdogMs, 180000);
  assert.equal(evalOptions.retryBudgetMs, 0);
  assert.equal(evalOptions.responseTimeoutMs, 15000);
  assert.equal(evalOptions.recoveryGraceMs, 90000);
  assert.equal(evalOptions.leaseTtlMs, 300000);
  assert.equal(evalOptions.requestId, 'cli-eval-1');
  assert.deepEqual(evalOptions.preconditions, []);
  assert.deepEqual(evalOptions.postconditions, []);
  assert.equal(evalOptions.hasExpected, true);
  assert.equal(evalOptions.expected, 42);
  assert.equal(evalOptions.terminateHostOnAmbiguous, true);

  const fileOptions = parseRunnerArgs('run', ['probe.jsx']);
  assert.equal(fileOptions.path, 'probe.jsx');
  assert.equal(fileOptions.watchdogMs, 60000);
  assert.equal(fileOptions.recoveryGraceMs, 120000);

  assert.throws(
    () => parseRunnerArgs('eval', ['--expr', '1', '--code', 'return 1;']),
    /exactly one of --expr/
  );
  assert.throws(
    () => parseRunnerArgs('run', ['probe.jsx', '--expr', '1']),
    /use eval\/test-eval/
  );
  assert.throws(
    () => parseRunnerArgs('run', ['probe.jsx', '--bogus', 'x']),
    /Unknown option/
  );
  assert.throws(
    () => parseRunnerArgs('eval', ['--expr', '1', '--expect-json', '1']),
    /valid only for test or test-eval/
  );

  assert.throws(
    () => parseRunnerArgs('eval', [
      '--expr', '1',
      '--retry-budget-ms', '-1'
    ]),
    /retryBudgetMs must be an integer between 0 and 3600000 ms/
  );
});

test('runner discovers the live operation catalog without target discovery', async () => {
  const runner = new ComToolRunner({ client: createClient() });
  try {
    const operations = await runner.listOperations();
    assert.deepEqual(
      operations.map(operation => operation.name),
      [
        'core.operation.describe',
        'core.operations.list',
        'script.eval'
      ]
    );

    const scriptEval = await runner.describeOperation('script.eval');
    assert.equal(scriptEval.executionScope, 'host');
    assert.equal(scriptEval.host, 'illustrator');
    assert.equal(scriptEval.requiresLease, true);
    assert.equal(scriptEval.mutationClass, 'unknown');
    assert.equal(scriptEval.mutationResolution, 'declared_or_unknown');
    assert.equal(scriptEval.policyLimits.retryBudgetMs.min, 0);
    assert.equal(scriptEval.policyLimits.retryBudgetMs.max, 3_600_000);
    assert.equal(scriptEval.policyLimits.retryBudgetMs.default, 2_000);
  } finally {
    await runner.close();
  }
});

test('runner runtime conveniences stay thin and preserve ambiguous launch semantics', async () => {
  const runner = new ComToolRunner({ client: createClient() });
  try {
    const examples = await runner.operationExamples({
      operation: 'script.eval',
      limit: 4
    });
    assert.equal(examples.count, 1);
    assert.equal(examples.examples[0].operation, 'script.eval');

    const knowledge = await runner.knowledgeDescribe();
    assert.equal(knowledge.source.kind, 'sqlite-index');
    assert.equal(knowledge.host.versionKnown, false);

    const search = await runner.knowledgeSearch('DoScript', { limit: 5 });
    assert.equal(search.items[0].name, 'DoScript');

    const symbol = await runner.knowledgeSymbol('Close', {
      interface: 'Document'
    });
    assert.equal(symbol.methods[0].name, 'Close');

    const enumResult = await runner.knowledgeEnum('AiSaveOptions');
    assert.equal(enumResult.enums[0].name, 'AiSaveOptions');

    const path = await runner.knowledgePaths(
      'Application',
      'Document',
      { maxDepth: 4 }
    );
    assert.equal(path.start, '_Application');
    assert.equal(path.targetInterface, 'Document');
    assert.equal(path.steps[0].member, 'ActiveDocument');

    const described = await runner.describeArtifact('art_fake');
    assert.equal(described.artifact.artifactId, 'art_fake');
    assert.equal('path' in described.artifact, false);

    const read = await runner.readArtifact('art_fake', {
      offset: 0,
      length: 8
    });
    assert.equal(
      Buffer.from(read.contentBase64, 'base64').toString('utf8'),
      'artifact'
    );

    const fullArtifact = await runner.readArtifactAll('art_fake', {
      chunkBytes: 5
    });
    assert.equal(
      Buffer.from(fullArtifact.content).toString('utf8'),
      'artifact-content-paged'
    );
    assert.equal(
      fullArtifact.artifact.sha256,
      described.artifact.sha256
    );
    await assert.rejects(
      () => runner.readArtifactAll('art_bad_hash', {
        chunkBytes: 5
      }),
      error => error?.kind === 'artifact_hash_mismatch'
    );

    const incidents = await runner.listIncidents({
      id: 'sdk-incidents-list'
    });
    assert.equal(incidents.length, 1);
    assert.equal(incidents[0].targetId, 'illustrator:fake-generation');
    assert.equal(incidents[0].phase, 'ambiguous');

    const offlineResolution = await runner.resolveOfflineIncident({
      targetId: incidents[0].targetId,
      incidentRequestId: incidents[0].requestId,
      expectedUpdatedAt: incidents[0].updatedAt,
      resolution: 'known_unchanged',
      rationale: 'Synthetic SDK offline reconciliation proof.',
      evidence: { source: 'sdk-test' },
      id: 'sdk-incident-resolve-offline'
    });
    assert.equal(
      offlineResolution.incidentRequestId,
      incidents[0].requestId
    );
    assert.equal(offlineResolution.resolution, 'known_unchanged');
    assert.equal(offlineResolution.targetRunning, false);

    const selected = await runner.selectTarget({ refresh: false });
    const attached = await runner.attachTarget(selected, {
      id: 'sdk-target-attach'
    });
    assert.equal(attached.target.id, selected.target.id);
    assert.equal(attached.ownershipAcquired, false);

    const launched = await runner.launchTarget({
      host: 'illustrator',
      progId: 'Illustrator.Application',
      existingInstance: 'fail',
      arguments: []
    }, {
      id: 'sdk-target-launch'
    });
    assert.equal(launched.ok, true);
    assert.equal(
      unwrapProtocolValue(launched.result).ownership,
      'launched_by_runtime'
    );

    const ambiguous = await runner.launchTarget({
      host: 'illustrator',
      progId: 'Ambiguous.Application',
      existingInstance: 'fail',
      arguments: []
    }, {
      id: 'sdk-target-launch-ambiguous'
    });
    assert.equal(ambiguous.ok, false);
    assert.equal(ambiguous.status, 'reconciliation_required');
    assert.equal(ambiguous.error.execution, 'ambiguous');
  } finally {
    await runner.close();
  }
});

test('target session binds one strong generation and explicit lease across generic and script operations', async () => {
  const runner = new ComToolRunner({ client: createClient() });
  try {
    const session = await runner.openSession();
    assert.equal(session.target.target.id, 'illustrator:fake-generation');
    assert.equal(session.identity.processId, 4242);
    assert.equal(session.leaseId, null);

    const before = unwrapProtocolValue((await session.execute(
      'session.inspect',
      { phase: 'before-lease' },
      { id: 'session-before-lease' }
    )).result);
    assert.equal(before.target.id, 'illustrator:fake-generation');
    assert.equal(before.policy, null);

    const refreshed = await session.refreshTarget({ refresh: false });
    assert.equal(refreshed.target.id, 'illustrator:fake-generation');

    await assert.rejects(
      session.pluginMessage('ExamplePlugin', 'rpc/v1', '{}'),
      /no bound lease/
    );

    const lease = await session.acquireLease({ ttlMs: 180000 });
    assert.equal(session.leaseId, lease.id);

    const reconciled = unwrapProtocolValue((await session.reconcile({
      id: 'session-reconcile'
    })).result);
    assert.equal(reconciled.policy.leaseId, lease.id);

    const originalPostconditions = [{
      sourceOperation: 'core.target.status',
      sourceInput: {},
      assertion: { kind: 'equals', path: 'documentsCount', expected: 1 }
    }];
    const mutationReconciled = unwrapProtocolValue((await session.reconcileMutation({
      incidentRequestId: 'ambiguous-request-1',
      expectedRevision: 7,
      postconditions: originalPostconditions,
      id: 'session-mutation-reconcile'
    })).result);
    assert.deepEqual(mutationReconciled.input, {
      incidentRequestId: 'ambiguous-request-1',
      expectedRevision: 7
    });
    assert.deepEqual(mutationReconciled.postconditions, originalPostconditions);
    assert.equal(mutationReconciled.policy.leaseId, lease.id);

    const resolved = unwrapProtocolValue((await session.resolveIncident({
      incidentRequestId: 'ambiguous-request-2',
      expectedRevision: 8,
      resolution: 'known_changed',
      rationale: 'Observed the changed document state through an independent read.',
      evidence: { observed: true },
      id: 'session-incident-resolve'
    })).result);
    assert.equal(resolved.input.incidentRequestId, 'ambiguous-request-2');
    assert.equal(resolved.input.expectedRevision, 8);
    assert.equal(resolved.input.resolution, 'known_changed');
    assert.equal(resolved.input.rationale, 'Observed the changed document state through an independent read.');
    assert.deepEqual(resolved.input.evidence, { observed: true });
    assert.equal(resolved.policy.leaseId, lease.id);

    const leased = unwrapProtocolValue((await session.execute(
      'session.inspect',
      { phase: 'leased' },
      { id: 'session-leased' }
    )).result);
    assert.equal(leased.target.id, 'illustrator:fake-generation');
    assert.equal(leased.policy.leaseId, lease.id);

    await assert.rejects(
      session.execute(
        'session.inspect',
        {},
        { policy: { leaseId: 'B'.repeat(64) } }
      ),
      /conflicts with the target session lease/
    );

    const pluginMessage = unwrapProtocolValue((await session.pluginMessage(
      'ExamplePlugin',
      'rpc/v1',
      '{"requestId":"plugin-message-test"}',
      { id: 'session-plugin-message' }
    )).result);
    assert.equal(pluginMessage.plugin, 'ExamplePlugin');
    assert.equal(pluginMessage.selector, 'rpc/v1');
    assert.equal(pluginMessage.inputSha256.length, 64);
    assert.equal(pluginMessage.responseSha256.length, 64);

    const tested = await session.testEval({
      source: '6*7',
      expected: 42,
      requestId: 'session-test-eval'
    });
    assert.equal(tested.exitCode, 0);
    assert.equal(tested.value, 42);
    assert.equal(tested.target.target.id, 'illustrator:fake-generation');
    assert.equal(tested.lease.id, lease.id);
    assert.equal(tested.lease.owned, false);

    const renewed = await session.renewLease({ ttlMs: 240000 });
    assert.equal(renewed.id, lease.id);

    await session.releaseLease();
    assert.equal(session.leaseId, null);

    const after = unwrapProtocolValue((await session.execute(
      'session.inspect',
      { phase: 'after-release' },
      { id: 'session-after-release' }
    )).result);
    assert.equal(after.policy, null);
  } finally {
    await runner.close();
  }
});

test('generic execute surface preserves arbitrary operations over persistent NDJSON', async () => {
  const client = createClient();
  try {
    const result = await client.execute(
      'plugin.future.debug.inspect',
      { address: 1234 },
      {
        id: 'generic-execute-test',
        policy: {
          workerWatchdogMs: 2500,
          retryBudgetMs: 0
        }
      }
    );

    assert.equal(result.ok, true);
    assert.deepEqual(
      unwrapProtocolValue(result.result),
      {
        echo: { address: 1234 },
        policy: {
          workerWatchdogMs: 2500,
          retryBudgetMs: 0
        }
      }
    );
  } finally {
    await client.close();
  }
});

test('concurrent out-of-order responses remain correlated by request id', async () => {
  const client = createClient();
  const completionOrder = [];

  try {
    const slow = client.execute(
      'delay.echo',
      { value: 'slow', delayMs: 80 },
      { id: 'out-of-order-slow' }
    ).then(result => {
      completionOrder.push('slow');
      return result;
    });

    const fast = client.execute(
      'delay.echo',
      { value: 'fast', delayMs: 5 },
      { id: 'out-of-order-fast' }
    ).then(result => {
      completionOrder.push('fast');
      return result;
    });

    const [slowResult, fastResult] = await Promise.all([slow, fast]);

    assert.equal(unwrapProtocolValue(slowResult.result).value, 'slow');
    assert.equal(unwrapProtocolValue(fastResult.result).value, 'fast');
    assert.deepEqual(completionOrder, ['fast', 'slow']);
  } finally {
    await client.close();
  }
});

test('pre-aborted request is rejected before transport start or submission', async () => {
  const client = createClient();
  const controller = new AbortController();
  controller.abort();

  try {
    await assert.rejects(
      client.execute(
        'delay.echo',
        { value: 'never-sent', delayMs: 1 },
        {
          id: 'abort-before-submit',
          signal: controller.signal
        }
      ),
      error => {
        assert.ok(error instanceof ComToolTransportError);
        assert.equal(error.kind, 'request_aborted');
        assert.equal(error.submitted, false);
        assert.equal(error.classification, 'transport_not_sent');
        assert.equal(error.requestId, 'abort-before-submit');
        return true;
      }
    );
    assert.equal(client.started, false);
  } finally {
    await client.close();
  }
});

test('response wait timeout after submission is ambiguous without poisoning persistent transport', async () => {
  const client = createClient();
  const events = [];
  client.on('event', event => events.push(event));

  try {
    await assert.rejects(
      client.execute(
        'delay.echo',
        { value: 'slow', delayMs: 80 },
        {
          id: 'response-timeout-slow',
          responseTimeoutMs: 10
        }
      ),
      error => {
        assert.ok(error instanceof ComToolTransportError);
        assert.equal(error.kind, 'response_wait_timeout');
        assert.equal(error.submitted, true);
        assert.equal(error.classification, 'transport_ambiguous');
        assert.equal(error.requestId, 'response-timeout-slow');
        return true;
      }
    );

    const fast = await client.execute(
      'delay.echo',
      { value: 'still-healthy', delayMs: 1 },
      {
        id: 'after-response-timeout',
        responseTimeoutMs: 1000
      }
    );
    assert.equal(
      unwrapProtocolValue(fast.result).value,
      'still-healthy'
    );

    await new Promise(resolve => setTimeout(resolve, 100));
    assert.ok(events.some(event =>
      event.event === 'response.orphaned' &&
      event.id === 'response-timeout-slow'
    ));
  } finally {
    await client.close();
  }
});

test('abort after submission is ambiguous and leaves concurrent transport usable', async () => {
  const client = createClient();
  const controller = new AbortController();

  try {
    const slow = client.execute(
      'delay.echo',
      { value: 'aborted-wait', delayMs: 80 },
      {
        id: 'abort-after-submit',
        signal: controller.signal
      }
    );
    setTimeout(() => controller.abort(), 10);

    await assert.rejects(
      slow,
      error => {
        assert.ok(error instanceof ComToolTransportError);
        assert.equal(error.kind, 'request_aborted');
        assert.equal(error.submitted, true);
        assert.equal(error.classification, 'transport_ambiguous');
        return true;
      }
    );

    const result = await client.execute(
      'delay.echo',
      { value: 'after-abort', delayMs: 1 },
      { id: 'after-abort' }
    );
    assert.equal(unwrapProtocolValue(result.result).value, 'after-abort');
  } finally {
    await client.close();
  }
});

test('runner can renew a target lease for multi-operation programs', async () => {
  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const target = await runner.selectTarget();
    const lease = await runner.acquireLease(target, { ttlMs: 120_000 });
    const renewed = await runner.renewLease(
      target,
      lease.id,
      { ttlMs: 180_000 }
    );

    assert.equal(renewed.id, lease.id);
    assert.equal(renewed.ttlMs, 180_000);
    assert.equal(renewed.expiresAt, '2026-09-26T10:00:00Z');

    await runner.releaseLease(target, lease.id);
  } finally {
    await runner.close();
  }
});

test('preselected strong target plus caller-owned lease avoids redundant discovery and lease churn', async () => {
  const client = createClient();
  const events = [];
  const runner = new ComToolRunner({
    client,
    onEvent(event) {
      events.push(event);
    }
  });

  let target;
  let lease;
  try {
    target = await runner.selectTarget();
    lease = await runner.acquireLease(target, { ttlMs: 180_000 });
    events.length = 0;

    const first = await runner.runEval({
      target,
      leaseId: lease.id,
      kind: 'expression',
      source: '6*7',
      requestId: 'reuse-target-1'
    });
    const second = await runner.runEval({
      target,
      leaseId: lease.id,
      kind: 'expression',
      source: '6*7',
      requestId: 'reuse-target-2'
    });

    assert.equal(first.value, 42);
    assert.equal(second.value, 42);
    assert.equal(first.lease.owned, false);
    assert.equal(second.lease.owned, false);

    const submitted = events
      .filter(event => event.event === 'request.submitted')
      .map(event => event.operation);
    assert.deepEqual(submitted, ['script.eval', 'script.eval']);
  } finally {
    if (target && lease) {
      await runner.releaseLease(target, lease.id).catch(() => {});
    }
    await runner.close();
  }
});

test('runEval provides lease/watchdog orchestration with source-hash provenance', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'comtool-v2-node-'));
  const artifactDir = join(directory, 'artifacts');
  const source = '6*7';

  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const run = await runner.runEval({
      kind: 'expression',
      source,
      watchdogMs: 90_000,
      artifactDir
    });

    assert.equal(run.classification, 'completed');
    assert.equal(run.exitCode, 0);
    assert.equal(run.value, 42);
    assert.equal(run.lease.retained, false);
    assert.equal(run.script.kind, 'eval');
    assert.equal(run.script.evalKind, 'expression');
    assert.equal(
      run.script.sourceSha256,
      createHash('sha256').update(source, 'utf8').digest('hex')
    );

    const artifact = await readFile(run.artifactPath, 'utf8');
    assert.doesNotMatch(artifact, /6\*7/);
    assert.match(artifact, /sourceSha256/);
  } finally {
    await runner.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test('runFile SHA-pins bytes and forwards caller watchdog instead of a hard-coded 60s', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'comtool-v2-node-'));
  const script = join(directory, 'custom-timeout.jsx');
  const artifactDir = join(directory, 'artifacts');
  const source = '42;\n';
  await writeFile(script, source, 'utf8');

  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const run = await runner.runFile({
      path: script,
      watchdogMs: 180_000,
      args: ['lane-v5'],
      artifactDir
    });

    assert.equal(run.classification, 'completed');
    assert.equal(run.exitCode, 0);
    assert.equal(run.lease.retained, false);
    assert.equal(run.value.watchdogMs, 180_000);
    assert.deepEqual(run.value.args, ['lane-v5']);
    assert.equal(
      run.value.expectedSha256,
      createHash('sha256').update(source).digest('hex')
    );

    const artifact = await readFile(run.artifactPath, 'utf8');
    assert.match(artifact, /\[redacted-lease-id\]/);
    assert.doesNotMatch(artifact, /A{64}/);
  } finally {
    await runner.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test('runner response timeout after script submission retains lease for explicit recovery', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'comtool-v2-node-'));
  const script = join(directory, 'slow-response.jsx');
  await writeFile(script, '42;\n', 'utf8');

  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const run = await runner.runFile({
      path: script,
      watchdogMs: 125_000,
      responseTimeoutMs: 10
    });

    assert.equal(run.classification, 'transport_ambiguous');
    assert.equal(run.exitCode, 2);
    assert.equal(run.ambiguous, true);
    assert.equal(run.transportError?.kind, 'response_wait_timeout');
    assert.equal(run.lease.retained, true);
    assert.equal(run.recovery.available, true);

    const recovery = await terminateHostGeneration(runner, run, {
      waitTimeoutMs: 5000
    });
    assert.equal(recovery.classification, 'completed');
    assert.equal(recovery.leaseReleased, true);
  } finally {
    await runner.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test('ambiguous script execution retains lease and explicit generation-pinned recovery can use it', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'comtool-v2-node-'));
  const script = join(directory, 'ambiguous.jsx');
  await writeFile(script, 'while(false){}\n', 'utf8');

  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const run = await runner.runFile({
      path: script,
      watchdogMs: 125_000
    });

    assert.equal(run.classification, 'ambiguous');
    assert.equal(run.exitCode, 2);
    assert.equal(run.ambiguous, true);
    assert.equal(run.lease.retained, true);
    assert.equal(run.recovery.available, true);

    const recovery = await terminateHostGeneration(
      runner,
      run,
      { waitTimeoutMs: 5000 }
    );

    assert.equal(recovery.classification, 'completed');
    assert.equal(recovery.value.exitObserved, true);
    assert.equal(recovery.leaseReleased, true);
  } finally {
    await runner.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test('transport loss after submission is surfaced as ambiguous and is never retried', async () => {
  const client = createClient();
  try {
    await assert.rejects(
      client.execute(
        'crash.after-read',
        { possibleMutation: true },
        { id: 'crash-after-submit' }
      ),
      error => {
        assert.ok(error instanceof ComToolTransportError);
        assert.equal(error.submitted, true);
        assert.equal(error.classification, 'transport_ambiguous');
        return true;
      }
    );
  } finally {
    await client.close();
  }
});

test('transport ambiguity exposes an auto-generated request id for reconciliation', async () => {
  const client = createClient();
  try {
    await assert.rejects(
      client.execute(
        'crash.after-read',
        { possibleMutation: true }
      ),
      error => {
        assert.ok(error instanceof ComToolTransportError);
        assert.equal(error.submitted, true);
        assert.equal(error.classification, 'transport_ambiguous');
        assert.match(error.requestId, /^op-[0-9a-f-]{36}$/i);
        assert.equal(error.operation, 'crash.after-read');
        return true;
      }
    );
  } finally {
    await client.close();
  }
});

test('testEval applies one-shot assertions without persisting inline source', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'comtool-v2-node-'));
  const artifactDir = join(directory, 'artifacts');

  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const result = await runner.testEval({
      kind: 'expression',
      source: '6*7',
      expected: 42,
      artifactDir
    });

    assert.equal(result.verdict, 'passed');
    assert.equal(result.classification, 'completed');
    assert.equal(result.exitCode, 0);
    assert.equal(result.value, 42);

    const artifact = await readFile(result.artifactPath, 'utf8');
    assert.doesNotMatch(artifact, /6\*7/);
    assert.match(artifact, /sourceSha256/);
  } finally {
    await runner.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test('testFile produces deterministic test_failed classification without rerunning script', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'comtool-v2-node-'));
  const script = join(directory, 'assertion.jsx');
  await writeFile(script, '123;\n', 'utf8');

  const client = createClient();
  const runner = new ComToolRunner({ client });
  try {
    const result = await runner.testFile({
      path: script,
      expected: { definitely: 'not-the-result' }
    });

    assert.equal(result.verdict, 'failed');
    assert.equal(result.classification, 'test_failed');
    assert.equal(result.exitCode, 1);
    assert.equal(result.operation.ok, true);
  } finally {
    await runner.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test('target debugger session remains pure orchestration over target-bound generic operations', async () => {
  const runner = new ComToolRunner({ client: createClient() });
  try {
    const targetSession = await runner.openSession();
    const lease = await targetSession.acquireLease({ ttlMs: 180000 });

    const opened = await targetSession.openDebugger({
      engine: 'main',
      idleTimeoutMs: 45000
    });
    assert.equal(opened.result.ok, true);
    assert.ok(opened.session);
    assert.equal(opened.session.sessionId, 'dbg-fake-session');
    assert.equal(opened.session.openInfo.workerOwned, true);

    const evalResult = await opened.session.eval(
      '6*7',
      { debugLevel: 0, timeoutMs: 12000 }
    );
    const evalValue = unwrapProtocolValue(evalResult.result);
    assert.equal(evalResult.ok, true);
    assert.equal(evalValue.command, 'eval');
    assert.equal(evalValue.input.source, '6*7');
    assert.equal(evalValue.input.debugLevel, 0);
    assert.equal(evalValue.policy.leaseId, lease.id);

    const breakpointsResult = await opened.session.setBreakpoints([
      {
        file: 'file:///C:/tmp/probe.jsx',
        line: 12,
        condition: 'i > 5'
      }
    ]);
    const breakpointsValue = unwrapProtocolValue(
      breakpointsResult.result
    );
    assert.equal(
      breakpointsValue.input.breakpoints[0].line,
      12
    );
    assert.equal(
      breakpointsValue.policy.leaseId,
      lease.id
    );

    const closed = await opened.session.close();
    assert.equal(closed.ok, true);
    assert.equal(opened.session.closed, true);
    await assert.rejects(
      opened.session.getBreakpoints(),
      /already closed/
    );

    const released = await targetSession.releaseLease();
    assert.equal(released.ok, true);
  } finally {
    await runner.close();
  }
});
