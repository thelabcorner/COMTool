import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import { basename, extname, join, resolve } from 'node:path';
import { isDeepStrictEqual } from 'node:util';

import {
  ComToolClient,
  ComToolTransportError,
  createRequestId,
  unwrapProtocolValue
} from './client.mjs';
import { ComToolTargetSession } from './session.mjs';

export const DEFAULT_WORKER_WATCHDOG_MS = 60_000;
export const MAX_WORKER_WATCHDOG_MS = 3_600_000;
export const MAX_RETRY_BUDGET_MS = 3_600_000;
export const MAX_LEASE_TTL_MS = 3_720_000;
export const MAX_ARTIFACT_PROTOCOL_READ_BYTES = 512 * 1024;
export const DEFAULT_ARTIFACT_READ_ALL_MAX_BYTES = 64 * 1024 * 1024;
const MIN_WORKER_WATCHDOG_MS = 100;
const MIN_RETRY_BUDGET_MS = 0;
const MIN_LEASE_TTL_MS = 1_000;
const DEFAULT_RECOVERY_GRACE_MS = 120_000;

const SCRIPT_EFFECTS = new Set([
  'unknown',
  'idempotent_write',
  'conditional_write',
  'non_idempotent_write',
  'document_lifecycle',
  'external_side_effect'
]);

class RunnerControlError extends Error {
  constructor(kind, message, result = null) {
    super(message);
    this.name = 'RunnerControlError';
    this.kind = kind;
    this.result = result;
  }
}

export function classifyOperationResult(result) {
  if (result?.ok === true && result?.status === 'completed') {
    return { classification: 'completed', exitCode: 0, ambiguous: false };
  }

  const execution = result?.error?.execution ?? null;
  if (
    execution === 'ambiguous' ||
    result?.status === 'reconciliation_required' ||
    result?.targetState === 'reconciliation_required'
  ) {
    return { classification: 'ambiguous', exitCode: 2, ambiguous: true };
  }

  if (execution === 'started') {
    return { classification: 'started_failure', exitCode: 2, ambiguous: true };
  }

  if (execution === 'not_started') {
    return { classification: 'failed_not_started', exitCode: 1, ambiguous: false };
  }

  return { classification: 'operation_failed', exitCode: 1, ambiguous: false };
}

export class ComToolRunner {
  #client;
  #onEvent;

  constructor({ client, onEvent, ...clientOptions } = {}) {
    this.#client = client ?? new ComToolClient(clientOptions);
    this.#onEvent = typeof onEvent === 'function' ? onEvent : null;

    if (this.#onEvent) {
      this.#client.on('event', event => this.#onEvent(event));
    }
  }

  get client() {
    return this.#client;
  }

  async close() {
    await this.#client.close();
  }

  async listOperations() {
    const result = await this.#client.execute(
      'core.operations.list',
      null,
      { id: createRequestId('operations') }
    );
    this.#requireOk(result, 'operation_catalog_failed');

    const operations = unwrapProtocolValue(result.result);
    if (!Array.isArray(operations)) {
      throw new RunnerControlError(
        'operation_catalog_invalid',
        'core.operations.list did not return an array.',
        result
      );
    }
    return operations;
  }

  async describeOperation(name) {
    if (typeof name !== 'string' || name.trim().length === 0) {
      throw new TypeError('name must be a non-empty operation name.');
    }

    const requestedName = name.trim();
    const result = await this.#client.execute(
      'core.operation.describe',
      { name: requestedName },
      { id: createRequestId('operation-describe') }
    );
    this.#requireOk(result, 'operation_describe_failed');

    const operation = unwrapProtocolValue(result.result);
    if (
      operation == null ||
      typeof operation !== 'object' ||
      operation.name !== requestedName
    ) {
      throw new RunnerControlError(
        'operation_describe_invalid',
        'core.operation.describe returned malformed operation metadata.',
        result
      );
    }
    return operation;
  }

  async operationExamples(query = {}, {
    id = createRequestId('operation-examples'),
    signal,
    responseTimeoutMs
  } = {}) {
    const result = await this.#client.execute(
      'core.operation.examples',
      query ?? {},
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'operation_examples_failed');
    return unwrapProtocolValue(result.result);
  }

  async knowledgeDescribe({
    id = createRequestId('knowledge-describe'),
    signal,
    responseTimeoutMs
  } = {}) {
    const result = await this.#client.execute(
      'knowledge.describe',
      {},
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'knowledge_describe_failed');
    return unwrapProtocolValue(result.result);
  }

  async knowledgeSearch(query, {
    limit,
    id = createRequestId('knowledge-search'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(query, 'query');
    const input = { query: query.trim() };
    if (limit !== undefined) input.limit = limit;

    const result = await this.#client.execute(
      'knowledge.search',
      input,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'knowledge_search_failed');
    return unwrapProtocolValue(result.result);
  }

  async knowledgeSymbol(name, {
    interface: interfaceName,
    limit,
    id = createRequestId('knowledge-symbol'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(name, 'name');
    const input = { name: name.trim() };
    if (interfaceName !== undefined) {
      requireNonEmptyString(interfaceName, 'interface');
      input.interface = interfaceName.trim();
    }
    if (limit !== undefined) input.limit = limit;

    const result = await this.#client.execute(
      'knowledge.symbol',
      input,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'knowledge_symbol_failed');
    return unwrapProtocolValue(result.result);
  }

  async knowledgeEnum(name, {
    limit,
    id = createRequestId('knowledge-enum'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(name, 'name');
    const input = { name: name.trim() };
    if (limit !== undefined) input.limit = limit;

    const result = await this.#client.execute(
      'knowledge.enum',
      input,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'knowledge_enum_failed');
    return unwrapProtocolValue(result.result);
  }

  async knowledgePaths(start, target, {
    maxDepth,
    id = createRequestId('knowledge-paths'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(start, 'start');
    requireNonEmptyString(target, 'target');
    const input = {
      start: start.trim(),
      target: target.trim()
    };
    if (maxDepth !== undefined) input.maxDepth = maxDepth;

    const result = await this.#client.execute(
      'knowledge.paths',
      input,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'knowledge_paths_failed');
    return unwrapProtocolValue(result.result);
  }

  async describeArtifact(artifactId, {
    id = createRequestId('artifact-describe'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(artifactId, 'artifactId');

    const result = await this.#client.execute(
      'core.artifact.describe',
      { artifactId: artifactId.trim() },
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'artifact_describe_failed');
    return unwrapProtocolValue(result.result);
  }

  async readArtifact(artifactId, {
    offset,
    length,
    id = createRequestId('artifact-read'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(artifactId, 'artifactId');
    const input = { artifactId: artifactId.trim() };
    if (offset !== undefined) input.offset = offset;
    if (length !== undefined) input.length = length;

    const result = await this.#client.execute(
      'core.artifact.read',
      input,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'artifact_read_failed');
    return unwrapProtocolValue(result.result);
  }

  async readArtifactAll(artifactId, {
    chunkBytes = MAX_ARTIFACT_PROTOCOL_READ_BYTES,
    maxBytes = DEFAULT_ARTIFACT_READ_ALL_MAX_BYTES,
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(artifactId, 'artifactId');
    validateBoundedInteger(
      chunkBytes,
      'chunkBytes',
      1,
      MAX_ARTIFACT_PROTOCOL_READ_BYTES
    );
    validateBoundedInteger(
      maxBytes,
      'maxBytes',
      1,
      Number.MAX_SAFE_INTEGER
    );

    const described = await this.describeArtifact(artifactId, {
      id: createRequestId('artifact-describe-all'),
      signal,
      responseTimeoutMs
    });
    const descriptor = described?.artifact;
    if (
      !descriptor ||
      !Number.isSafeInteger(descriptor.byteCount) ||
      descriptor.byteCount < 0 ||
      typeof descriptor.sha256 !== 'string'
    ) {
      throw new RunnerControlError(
        'artifact_descriptor_invalid',
        'core.artifact.describe returned an invalid descriptor.'
      );
    }
    if (descriptor.byteCount > maxBytes) {
      throw new RunnerControlError(
        'artifact_sdk_read_limit_exceeded',
        'Artifact contains ' + descriptor.byteCount +
          ' bytes, exceeding the SDK readArtifactAll maxBytes limit of ' +
          maxBytes + '.'
      );
    }

    const chunks = [];
    let offset = 0;
    while (offset < descriptor.byteCount) {
      const length = Math.min(
        chunkBytes,
        descriptor.byteCount - offset
      );
      const page = await this.readArtifact(artifactId, {
        offset,
        length,
        id: createRequestId('artifact-read-page'),
        signal,
        responseTimeoutMs
      });

      if (
        !page ||
        page.offset !== offset ||
        page.returnedByteCount !== length ||
        !sameArtifactDescriptor(page.artifact, descriptor)
      ) {
        throw new RunnerControlError(
          'artifact_page_inconsistent',
          'Artifact page at offset ' + offset +
            ' did not match the pinned descriptor.'
        );
      }

      const bytes = Buffer.from(page.contentBase64, 'base64');
      if (bytes.length !== length) {
        throw new RunnerControlError(
          'artifact_page_length_mismatch',
          'Artifact page at offset ' + offset + ' decoded to ' +
            bytes.length + ' bytes; ' + length + ' were expected.'
        );
      }

      chunks.push(bytes);
      offset += length;
    }

    const content = Buffer.concat(chunks, descriptor.byteCount);
    const sha256 = createHash('sha256').update(content).digest('hex');
    if (sha256 !== descriptor.sha256) {
      throw new RunnerControlError(
        'artifact_hash_mismatch',
        'Reassembled artifact SHA-256 ' + sha256 +
          ' did not match descriptor ' + descriptor.sha256 + '.'
      );
    }

    return { artifact: descriptor, content };
  }

  async listIncidents({
    id = createRequestId('incidents'),
    signal,
    responseTimeoutMs
  } = {}) {
    const result = await this.#client.execute(
      'core.incidents.list',
      null,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'incident_list_failed');

    const incidents = unwrapProtocolValue(result.result);
    if (!Array.isArray(incidents)) {
      throw new RunnerControlError(
        'incident_list_invalid',
        'core.incidents.list did not return an array.',
        result
      );
    }
    return incidents;
  }

  async resolveOfflineIncident({
    targetId,
    incidentRequestId,
    expectedUpdatedAt,
    resolution,
    rationale,
    evidence,
    id = createRequestId('incident-resolve-offline'),
    signal,
    responseTimeoutMs
  } = {}) {
    requireNonEmptyString(targetId, 'targetId');
    requireNonEmptyString(incidentRequestId, 'incidentRequestId');
    requireNonEmptyString(expectedUpdatedAt, 'expectedUpdatedAt');
    requireNonEmptyString(rationale, 'rationale');
    if (
      resolution !== 'known_changed' &&
      resolution !== 'known_unchanged'
    ) {
      throw new TypeError(
        "resolution must be 'known_changed' or 'known_unchanged'."
      );
    }

    const input = {
      targetId: targetId.trim(),
      incidentRequestId: incidentRequestId.trim(),
      expectedUpdatedAt: expectedUpdatedAt.trim(),
      resolution,
      rationale: rationale.trim()
    };
    if (evidence !== undefined) input.evidence = evidence;

    const result = await this.#client.execute(
      'core.incident.resolve',
      input,
      { id, signal, responseTimeoutMs }
    );
    this.#requireOk(result, 'offline_incident_resolution_failed');
    return unwrapProtocolValue(result.result);
  }

  async attachTarget(target, {
    id = createRequestId('target-attach'),
    signal,
    responseTimeoutMs
  } = {}) {
    const selected = validateSelectedTarget(target);
    const result = await this.#client.execute(
      'core.target.attach',
      {},
      {
        id,
        target: selected.target,
        signal,
        responseTimeoutMs
      }
    );
    this.#requireOk(result, 'target_attach_failed');

    const attached = unwrapProtocolValue(result.result);
    return validateSelectedTarget(attached);
  }

  async launchTarget(spec, {
    id = createRequestId('target-launch'),
    signal,
    responseTimeoutMs
  } = {}) {
    if (spec == null || typeof spec !== 'object' || Array.isArray(spec)) {
      throw new TypeError('launch spec must be an object.');
    }

    // Deliberately return the raw OperationResult. In particular,
    // reconciliation_required / execution=ambiguous is not converted into a
    // throw that a caller might accidentally retry.
    return this.#client.execute(
      'core.target.launch',
      spec,
      { id, signal, responseTimeoutMs }
    );
  }

  async listTargets({ refresh = true } = {}) {
    const result = await this.#client.execute(
      'core.targets.list',
      { refresh },
      { id: createRequestId('targets') }
    );
    this.#requireOk(result, 'target_discovery_failed');

    const targets = unwrapProtocolValue(result.result);
    if (!Array.isArray(targets)) {
      throw new RunnerControlError(
        'target_discovery_invalid',
        'core.targets.list did not return an array.',
        result
      );
    }
    return targets;
  }

  async selectTarget({
    host = 'illustrator',
    targetId,
    refresh = true
  } = {}) {
    const targets = await this.listTargets({ refresh });
    const candidates = targets.filter(entry =>
      entry?.running === true &&
      entry?.target?.host === host &&
      (!targetId || entry?.target?.id === targetId)
    );

    if (candidates.length === 0) {
      throw new RunnerControlError(
        'target_not_found',
        targetId
          ? `No running ${host} target matched '${targetId}'.`
          : `No running ${host} target was discovered.`
      );
    }

    if (candidates.length > 1) {
      throw new RunnerControlError(
        'target_ambiguous',
        `Multiple running ${host} targets were discovered; pass targetId explicitly.`
      );
    }

    const selected = candidates[0];
    if (
      !selected?.identity ||
      !Number.isInteger(selected.identity.processId) ||
      !selected.identity.processStartedAt
    ) {
      throw new RunnerControlError(
        'target_identity_invalid',
        'Selected target did not include a strong PID/start-time identity.'
      );
    }

    return selected;
  }

  async openSession({
    target: selectedTarget,
    targetId,
    host = 'illustrator',
    refresh = true,
    leaseId = null
  } = {}) {
    const target = selectedTarget === undefined
      ? await this.selectTarget({ host, targetId, refresh })
      : validateSelectedTarget(selectedTarget, { host, targetId });

    return new ComToolTargetSession(
      this,
      target,
      { leaseId }
    );
  }

  async acquireLease(target, { ttlMs = 120_000 } = {}) {
    validateLeaseTtl(ttlMs);

    const result = await this.#client.execute(
      'core.target.lease.acquire',
      { ttlMs },
      {
        id: createRequestId('lease-acquire'),
        target: target.target
      }
    );
    this.#requireOk(result, 'lease_acquire_failed');

    const value = unwrapProtocolValue(result.result);
    if (
      typeof value?.leaseId !== 'string' ||
      typeof value?.expiresAt !== 'string'
    ) {
      throw new RunnerControlError(
        'lease_response_invalid',
        'Lease acquisition returned malformed lease metadata.',
        result
      );
    }

    return {
      id: value.leaseId,
      acquiredAt: value.acquiredAt ?? null,
      expiresAt: value.expiresAt,
      ttlMs,
      owned: true,
      retained: true
    };
  }

  async renewLease(target, leaseId, { ttlMs } = {}) {
    if (typeof leaseId !== 'string' || leaseId.length === 0) {
      throw new TypeError('leaseId is required.');
    }
    if (ttlMs !== undefined) {
      validateLeaseTtl(ttlMs);
    }

    const result = await this.#client.execute(
      'core.target.lease.renew',
      ttlMs === undefined ? {} : { ttlMs },
      {
        id: createRequestId('lease-renew'),
        target: target.target,
        policy: { leaseId }
      }
    );
    this.#requireOk(result, 'lease_renew_failed');

    const value = unwrapProtocolValue(result.result);
    if (
      typeof value?.leaseId !== 'string' ||
      typeof value?.expiresAt !== 'string'
    ) {
      throw new RunnerControlError(
        'lease_response_invalid',
        'Lease renewal returned malformed lease metadata.',
        result
      );
    }

    return {
      id: value.leaseId,
      acquiredAt: value.acquiredAt ?? null,
      expiresAt: value.expiresAt,
      ttlMs: ttlMs ?? null
    };
  }

  async releaseLease(target, leaseId) {
    if (typeof leaseId !== 'string' || leaseId.length === 0) {
      throw new TypeError('leaseId is required.');
    }

    const result = await this.#client.execute(
      'core.target.lease.release',
      {},
      {
        id: createRequestId('lease-release'),
        target: target.target,
        policy: { leaseId }
      }
    );
    this.#requireOk(result, 'lease_release_failed');
    return result;
  }

  async runFile({
    path,
    args = [],
    effects = 'unknown',
    watchdogMs = DEFAULT_WORKER_WATCHDOG_MS,
    retryBudgetMs,
    recoveryGraceMs = DEFAULT_RECOVERY_GRACE_MS,
    leaseTtlMs,
    leaseId,
    requestId = createRequestId('run'),
    target: selectedTarget,
    targetId,
    host = 'illustrator',
    preconditions,
    postconditions,
    artifactDir,
    signal,
    responseTimeoutMs
  } = {}) {
    const startedAt = new Date().toISOString();
    let target = null;
    let lease = null;
    let sha256 = null;
    let fullPath = null;
    let operationResult = null;
    let transportError = null;
    let cleanupError = null;
    let executionStage = 'input';

    try {
      fullPath = normalizeScriptPath(path);
      validateScriptEffects(effects);
      validateWatchdog(watchdogMs);
      validateRetryBudget(retryBudgetMs);
      validateAbortSignal(signal);
      validateResponseTimeoutMs(responseTimeoutMs);

      if (!Array.isArray(args)) {
        throw new TypeError('args must be an array.');
      }

      const reusableTarget = selectedTarget === undefined
        ? null
        : validateSelectedTarget(selectedTarget, { host, targetId });

      sha256 = await sha256File(fullPath);
      executionStage = 'target';
      target = reusableTarget ?? await this.selectTarget({
        host,
        targetId,
        refresh: true
      });

      executionStage = 'lease';
      if (leaseId) {
        lease = {
          id: leaseId,
          acquiredAt: null,
          expiresAt: null,
          ttlMs: null,
          owned: false,
          retained: true
        };
      } else {
        lease = await this.acquireLease(target, {
          ttlMs: resolveLeaseTtl(
            watchdogMs,
            recoveryGraceMs,
            leaseTtlMs
          )
        });
      }

      this.#emit('runner.script.submitting', {
        requestId,
        targetId: target.target.id,
        path: fullPath,
        sha256,
        watchdogMs,
        effects
      });

      executionStage = 'execution';
      try {
        operationResult = await this.#client.execute(
          'script.runFile',
          {
            path: fullPath,
            expectedSha256: sha256,
            effects,
            args
          },
          {
            id: requestId,
            target: target.target,
            policy: {
              leaseId: lease.id,
              workerWatchdogMs: watchdogMs,
              ...(retryBudgetMs === undefined
                ? {}
                : { retryBudgetMs })
            },
            preconditions,
            postconditions,
            signal,
            responseTimeoutMs
          }
        );
      } catch (error) {
        if (!(error instanceof ComToolTransportError)) throw error;
        transportError = serializeTransportError(error);
      }

      let classification;
      let exitCode;
      let ambiguous;

      if (transportError) {
        ambiguous = transportError.submitted === true;
        classification = ambiguous
          ? 'transport_ambiguous'
          : 'transport_not_sent';
        exitCode = ambiguous ? 2 : 3;
      } else {
        ({ classification, exitCode, ambiguous } =
          classifyOperationResult(operationResult));
      }

      const mustRetainLease =
        ambiguous || classification === 'started_failure';

      if (lease.owned && !mustRetainLease) {
        try {
          await this.releaseLease(target, lease.id);
          lease.retained = false;
        } catch (error) {
          cleanupError = serializeUnknownError(error);
          lease.retained = true;
          classification = 'lease_cleanup_unknown';
          exitCode = 3;
        }
      }

      const run = {
        kind: 'comtool-v2-run',
        mode: 'run',
        classification,
        exitCode,
        ambiguous: mustRetainLease,
        startedAt,
        finishedAt: new Date().toISOString(),
        requestId,
        target: {
          target: target.target,
          identity: target.identity
        },
        script: {
          path: fullPath,
          name: basename(fullPath),
          sha256,
          effects,
          watchdogMs,
          argsCount: args.length
        },
        lease,
        operation: operationResult,
        value: operationResult?.ok === true
          ? unwrapProtocolValue(operationResult.result)
          : undefined,
        transportError,
        cleanupError,
        recovery: mustRetainLease
          ? {
              available: true,
              operation: 'core.target.host.terminate',
              requiresSameLease: true,
              exactProcessGeneration: {
                processId: target.identity.processId,
                processStartedAt: target.identity.processStartedAt
              }
            }
          : { available: false }
      };

      if (artifactDir) {
        run.artifactPath = await this.writeArtifact(
          artifactDir,
          requestId,
          run
        );
      }

      this.#emit('runner.script.finished', {
        requestId,
        classification: run.classification,
        exitCode: run.exitCode,
        leaseRetained: run.lease?.retained === true
      });

      return run;
    } catch (error) {
      const preflightTransport =
        error instanceof ComToolTransportError
          ? serializeTransportError(error)
          : null;
      const controlResult =
        error instanceof RunnerControlError
          ? error.result
          : null;

      const failed = {
        kind: 'comtool-v2-run',
        mode: 'run',
        classification:
          executionStage === 'input'
            ? 'input_error'
            : 'control_failure',
        exitCode: executionStage === 'input' ? 1 : 3,
        ambiguous: false,
        startedAt,
        finishedAt: new Date().toISOString(),
        requestId,
        target: target
          ? { target: target.target, identity: target.identity }
          : null,
        script: {
          path: fullPath,
          sha256,
          effects,
          watchdogMs,
          argsCount: Array.isArray(args) ? args.length : null
        },
        lease,
        operation: controlResult,
        value: undefined,
        transportError: preflightTransport,
        cleanupError: null,
        error: serializeUnknownError(error),
        recovery: { available: false }
      };

      if (lease?.owned && lease.retained && executionStage !== 'execution') {
        try {
          await this.releaseLease(target, lease.id);
          lease.retained = false;
        } catch (releaseError) {
          failed.cleanupError = serializeUnknownError(releaseError);
        }
      }

      if (artifactDir) {
        failed.artifactPath = await this.writeArtifact(
          artifactDir,
          requestId,
          failed
        );
      }

      return failed;
    }
  }

  async runEval({
    kind = 'expression',
    source,
    args = [],
    effects = 'unknown',
    watchdogMs = DEFAULT_WORKER_WATCHDOG_MS,
    retryBudgetMs,
    recoveryGraceMs = DEFAULT_RECOVERY_GRACE_MS,
    leaseTtlMs,
    leaseId,
    requestId = createRequestId('eval'),
    target: selectedTarget,
    targetId,
    host = 'illustrator',
    preconditions,
    postconditions,
    artifactDir,
    signal,
    responseTimeoutMs
  } = {}) {
    const startedAt = new Date().toISOString();
    let target = null;
    let lease = null;
    let sourceSha256 = null;
    let operationResult = null;
    let transportError = null;
    let cleanupError = null;
    let executionStage = 'input';

    try {
      validateEvalKind(kind);
      validateEvalSource(source);
      validateScriptEffects(effects);
      validateWatchdog(watchdogMs);
      validateRetryBudget(retryBudgetMs);
      validateAbortSignal(signal);
      validateResponseTimeoutMs(responseTimeoutMs);

      if (!Array.isArray(args)) {
        throw new TypeError('args must be an array.');
      }

      const reusableTarget = selectedTarget === undefined
        ? null
        : validateSelectedTarget(selectedTarget, { host, targetId });

      sourceSha256 = createHash('sha256')
        .update(source, 'utf8')
        .digest('hex');

      executionStage = 'target';
      target = reusableTarget ?? await this.selectTarget({
        host,
        targetId,
        refresh: true
      });

      executionStage = 'lease';
      if (leaseId) {
        lease = {
          id: leaseId,
          acquiredAt: null,
          expiresAt: null,
          ttlMs: null,
          owned: false,
          retained: true
        };
      } else {
        lease = await this.acquireLease(target, {
          ttlMs: resolveLeaseTtl(
            watchdogMs,
            recoveryGraceMs,
            leaseTtlMs
          )
        });
      }

      this.#emit('runner.script.submitting', {
        requestId,
        targetId: target.target.id,
        scriptKind: 'eval',
        evalKind: kind,
        sourceSha256,
        sourceChars: source.length,
        watchdogMs,
        effects
      });

      executionStage = 'execution';
      try {
        operationResult = await this.#client.execute(
          'script.eval',
          {
            kind,
            source,
            effects,
            args
          },
          {
            id: requestId,
            target: target.target,
            policy: {
              leaseId: lease.id,
              workerWatchdogMs: watchdogMs,
              ...(retryBudgetMs === undefined
                ? {}
                : { retryBudgetMs })
            },
            preconditions,
            postconditions,
            signal,
            responseTimeoutMs
          }
        );
      } catch (error) {
        if (!(error instanceof ComToolTransportError)) throw error;
        transportError = serializeTransportError(error);
      }

      let classification;
      let exitCode;
      let ambiguous;

      if (transportError) {
        ambiguous = transportError.submitted === true;
        classification = ambiguous
          ? 'transport_ambiguous'
          : 'transport_not_sent';
        exitCode = ambiguous ? 2 : 3;
      } else {
        ({ classification, exitCode, ambiguous } =
          classifyOperationResult(operationResult));
      }

      const mustRetainLease =
        ambiguous || classification === 'started_failure';

      if (lease.owned && !mustRetainLease) {
        try {
          await this.releaseLease(target, lease.id);
          lease.retained = false;
        } catch (error) {
          cleanupError = serializeUnknownError(error);
          lease.retained = true;
          classification = 'lease_cleanup_unknown';
          exitCode = 3;
        }
      }

      const run = {
        kind: 'comtool-v2-run',
        mode: 'eval',
        classification,
        exitCode,
        ambiguous: mustRetainLease,
        startedAt,
        finishedAt: new Date().toISOString(),
        requestId,
        target: {
          target: target.target,
          identity: target.identity
        },
        script: {
          kind: 'eval',
          evalKind: kind,
          sourceSha256,
          sourceChars: source.length,
          effects,
          watchdogMs,
          argsCount: args.length
        },
        lease,
        operation: operationResult,
        value: operationResult?.ok === true
          ? unwrapProtocolValue(operationResult.result)
          : undefined,
        transportError,
        cleanupError,
        recovery: mustRetainLease
          ? {
              available: true,
              operation: 'core.target.host.terminate',
              requiresSameLease: true,
              exactProcessGeneration: {
                processId: target.identity.processId,
                processStartedAt: target.identity.processStartedAt
              }
            }
          : { available: false }
      };

      if (artifactDir) {
        run.artifactPath = await this.writeArtifact(
          artifactDir,
          requestId,
          run
        );
      }

      this.#emit('runner.script.finished', {
        requestId,
        mode: 'eval',
        classification: run.classification,
        exitCode: run.exitCode,
        leaseRetained: run.lease?.retained === true
      });

      return run;
    } catch (error) {
      const preflightTransport =
        error instanceof ComToolTransportError
          ? serializeTransportError(error)
          : null;
      const controlResult =
        error instanceof RunnerControlError
          ? error.result
          : null;

      const failed = {
        kind: 'comtool-v2-run',
        mode: 'eval',
        classification:
          executionStage === 'input'
            ? 'input_error'
            : 'control_failure',
        exitCode: executionStage === 'input' ? 1 : 3,
        ambiguous: false,
        startedAt,
        finishedAt: new Date().toISOString(),
        requestId,
        target: target
          ? { target: target.target, identity: target.identity }
          : null,
        script: {
          kind: 'eval',
          evalKind: kind,
          sourceSha256,
          sourceChars: typeof source === 'string'
            ? source.length
            : null,
          effects,
          watchdogMs,
          argsCount: Array.isArray(args) ? args.length : null
        },
        lease,
        operation: controlResult,
        value: undefined,
        transportError: preflightTransport,
        cleanupError: null,
        error: serializeUnknownError(error),
        recovery: { available: false }
      };

      if (lease?.owned && lease.retained && executionStage !== 'execution') {
        try {
          await this.releaseLease(target, lease.id);
          lease.retained = false;
        } catch (releaseError) {
          failed.cleanupError = serializeUnknownError(releaseError);
        }
      }

      if (artifactDir) {
        failed.artifactPath = await this.writeArtifact(
          artifactDir,
          requestId,
          failed
        );
      }

      return failed;
    }
  }

  async testFile(options = {}) {
    const {
      expected,
      assert: assertResult,
      artifactDir,
      ...runOptions
    } = options;

    const run = await this.runFile({
      ...runOptions,
      artifactDir: undefined
    });

    return this.#finalizeTestRun(run, {
      expected,
      assertResult,
      artifactDir,
      hasExpected: Object.hasOwn(options, 'expected')
    });
  }

  async testEval(options = {}) {
    const {
      expected,
      assert: assertResult,
      artifactDir,
      ...runOptions
    } = options;

    const run = await this.runEval({
      ...runOptions,
      artifactDir: undefined
    });

    return this.#finalizeTestRun(run, {
      expected,
      assertResult,
      artifactDir,
      hasExpected: Object.hasOwn(options, 'expected')
    });
  }

  async #finalizeTestRun(
    run,
    {
      expected,
      assertResult,
      artifactDir,
      hasExpected
    }
  ) {
    let test = {
      ...run,
      mode: 'test',
      verdict:
        run.classification === 'completed'
          ? 'passed'
          : run.ambiguous
            ? 'indeterminate'
            : 'failed'
    };

    if (run.classification === 'completed') {
      try {
        if (hasExpected && !isDeepStrictEqual(run.value, expected)) {
          throw new Error(
            `Result did not deep-equal expected value. expected=${JSON.stringify(expected)} actual=${JSON.stringify(run.value)}`
          );
        }

        if (assertResult) {
          if (typeof assertResult !== 'function') {
            throw new TypeError('assert must be a function.');
          }
          const assertion = await assertResult(run.value, run);
          if (assertion === false) {
            throw new Error('Custom result assertion returned false.');
          }
        }
      } catch (error) {
        test = {
          ...test,
          classification: 'test_failed',
          exitCode: 1,
          verdict: 'failed',
          assertionError: serializeUnknownError(error)
        };
      }
    }

    if (artifactDir) {
      test.artifactPath = await this.writeArtifact(
        artifactDir,
        test.requestId,
        test
      );
    }

    return test;
  }

  async writeArtifact(directory, requestId, value) {
    const root = resolve(directory);
    await mkdir(root, { recursive: true });

    const safeId = requestId
      .replace(/[^A-Za-z0-9_.-]/g, '_')
      .slice(0, 128);
    const path = join(root, `${safeId}.json`);
    const temp = `${path}.tmp-${process.pid}-${Date.now()}`;

    await writeFile(
      temp,
      JSON.stringify(redactForArtifact(value), null, 2) + '\n',
      { encoding: 'utf8', flag: 'wx' }
    );
    await rename(temp, path);
    return path;
  }

  #requireOk(result, kind) {
    if (result?.ok === true) return;
    throw new RunnerControlError(
      kind,
      result?.error?.message ??
        `COM Tool control operation failed with status '${result?.status ?? 'unknown'}'.`,
      result
    );
  }

  #emit(event, fields = {}) {
    this.#onEvent?.({
      timestamp: new Date().toISOString(),
      event,
      ...fields
    });
  }
}

function requireNonEmptyString(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new TypeError(`${label} must be a non-empty string.`);
  }
}

function normalizeScriptPath(path) {
  if (typeof path !== 'string' || path.trim().length === 0) {
    throw new TypeError('path must be a non-empty .jsx or .jsxbin file path.');
  }

  const fullPath = resolve(path);
  const extension = extname(fullPath).toLowerCase();
  if (extension !== '.jsx' && extension !== '.jsxbin') {
    throw new TypeError('path must end in .jsx or .jsxbin.');
  }
  return fullPath;
}

function validateSelectedTarget(selected, { host, targetId } = {}) {
  if (selected == null || typeof selected !== 'object' || Array.isArray(selected)) {
    throw new TypeError(
      'target must be a strong target object previously returned by selectTarget().'
    );
  }

  const target = selected.target;
  const identity = selected.identity;
  if (
    target == null ||
    typeof target !== 'object' ||
    typeof target.host !== 'string' ||
    target.host.length === 0 ||
    typeof target.id !== 'string' ||
    target.id.length === 0
  ) {
    throw new TypeError('target.target must contain non-empty host and id fields.');
  }
  if (
    identity == null ||
    typeof identity !== 'object' ||
    !Number.isInteger(identity.processId) ||
    identity.processId <= 0 ||
    typeof identity.processStartedAt !== 'string' ||
    identity.processStartedAt.length === 0
  ) {
    throw new TypeError(
      'target.identity must contain a positive processId and processStartedAt.'
    );
  }
  if (host && target.host !== host) {
    throw new TypeError(
      `Preselected target host '${target.host}' does not match requested host '${host}'.`
    );
  }
  if (targetId && target.id !== targetId) {
    throw new TypeError(
      `Preselected target id '${target.id}' does not match requested targetId '${targetId}'.`
    );
  }
  if (identity.targetId && identity.targetId !== target.id) {
    throw new TypeError(
      'Preselected target identity.targetId does not match target.id.'
    );
  }
  if (identity.host && identity.host !== target.host) {
    throw new TypeError(
      'Preselected target identity.host does not match target.host.'
    );
  }

  return selected;
}

function validateEvalKind(kind) {
  if (kind !== 'expression' && kind !== 'code') {
    throw new TypeError("kind must be 'expression' or 'code'.");
  }
}

function validateEvalSource(source) {
  if (typeof source !== 'string' || source.trim().length === 0) {
    throw new TypeError('source must be a non-empty string.');
  }
  if (source.length > 1_000_000) {
    throw new RangeError('source exceeds the 1000000 character script.eval limit.');
  }
}

function validateScriptEffects(effects) {
  if (!SCRIPT_EFFECTS.has(effects)) {
    throw new TypeError(
      `effects must be one of: ${[...SCRIPT_EFFECTS].join(', ')}.`
    );
  }
}

function validateBoundedInteger(value, name, min, max) {
  if (
    !Number.isSafeInteger(value) ||
    value < min ||
    value > max
  ) {
    throw new RangeError(
      name + ' must be an integer between ' + min + ' and ' + max + '.'
    );
  }
}

function sameArtifactDescriptor(left, right) {
  return Boolean(left && right) &&
    left.artifactId === right.artifactId &&
    left.sha256 === right.sha256 &&
    left.byteCount === right.byteCount &&
    left.mediaType === right.mediaType &&
    left.encoding === right.encoding &&
    left.revision === right.revision;
}

function validateWatchdog(watchdogMs) {
  if (
    !Number.isSafeInteger(watchdogMs) ||
    watchdogMs < MIN_WORKER_WATCHDOG_MS ||
    watchdogMs > MAX_WORKER_WATCHDOG_MS
  ) {
    throw new RangeError(
      `watchdogMs must be an integer between ${MIN_WORKER_WATCHDOG_MS} and ${MAX_WORKER_WATCHDOG_MS} ms.`
    );
  }
}

function validateRetryBudget(retryBudgetMs) {
  if (retryBudgetMs === undefined) return;
  if (
    !Number.isSafeInteger(retryBudgetMs) ||
    retryBudgetMs < MIN_RETRY_BUDGET_MS ||
    retryBudgetMs > MAX_RETRY_BUDGET_MS
  ) {
    throw new RangeError(
      `retryBudgetMs must be an integer between ${MIN_RETRY_BUDGET_MS} and ${MAX_RETRY_BUDGET_MS} ms.`
    );
  }
}

function validateAbortSignal(signal) {
  if (signal === undefined) return;
  if (
    signal == null ||
    typeof signal.aborted !== 'boolean' ||
    typeof signal.addEventListener !== 'function' ||
    typeof signal.removeEventListener !== 'function'
  ) {
    throw new TypeError('signal must be an AbortSignal-compatible object.');
  }
}

function validateResponseTimeoutMs(value) {
  if (value === undefined || value === null) return;
  if (
    !Number.isSafeInteger(value) ||
    value < 1 ||
    value > 2_147_483_647
  ) {
    throw new RangeError(
      'responseTimeoutMs must be an integer between 1 and 2147483647 ms.'
    );
  }
}

function validateLeaseTtl(ttlMs) {
  if (
    !Number.isSafeInteger(ttlMs) ||
    ttlMs < MIN_LEASE_TTL_MS ||
    ttlMs > MAX_LEASE_TTL_MS
  ) {
    throw new RangeError(
      `lease ttl must be an integer between ${MIN_LEASE_TTL_MS} and ${MAX_LEASE_TTL_MS} ms.`
    );
  }
}

function resolveLeaseTtl(
  watchdogMs,
  recoveryGraceMs,
  explicitTtlMs
) {
  if (
    !Number.isSafeInteger(recoveryGraceMs) ||
    recoveryGraceMs < 0 ||
    recoveryGraceMs > 600_000
  ) {
    throw new RangeError(
      'recoveryGraceMs must be an integer between 0 and 600000 ms.'
    );
  }

  const required = Math.max(
    120_000,
    watchdogMs + recoveryGraceMs
  );

  if (required > MAX_LEASE_TTL_MS) {
    throw new RangeError(
      'watchdogMs + recoveryGraceMs exceeds V2 maximum lease lifetime.'
    );
  }

  if (explicitTtlMs === undefined) return required;

  validateLeaseTtl(explicitTtlMs);
  if (explicitTtlMs < required) {
    throw new RangeError(
      `leaseTtlMs must be at least ${required} ms for the selected watchdog/recovery grace.`
    );
  }
  return explicitTtlMs;
}

async function sha256File(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) {
    hash.update(chunk);
  }
  return hash.digest('hex');
}

export function serializeTransportError(error) {
  return {
    name: error.name,
    kind: error.kind,
    message: error.message,
    classification: error.classification,
    submitted: error.submitted === true,
    requestId: error.requestId ?? null,
    operation: error.operation ?? null,
    details: error.details ?? null
  };
}

export function serializeUnknownError(error) {
  if (error == null) return null;
  return {
    name: error.name ?? 'Error',
    kind: error.kind ?? null,
    message: error.message ?? String(error),
    classification: error.classification ?? null,
    submitted: error.submitted ?? null
  };
}

function redactForArtifact(value) {
  return JSON.parse(JSON.stringify(value, function (key, current) {
    if (
      key === 'id' &&
      this?.owned !== undefined &&
      this?.retained !== undefined
    ) {
      return '[redacted-lease-id]';
    }
    if (key === 'leaseId') return '[redacted-lease-id]';
    return current;
  }));
}
