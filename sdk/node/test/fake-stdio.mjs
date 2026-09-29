import { createHash } from 'node:crypto';
import readline from 'node:readline';

const leaseId = 'A'.repeat(64);
const artifactContent = Buffer.from('artifact-content-paged', 'utf8');
const artifactSha256 = createHash('sha256')
  .update(artifactContent)
  .digest('hex');
const target = {
  host: 'illustrator',
  id: 'illustrator:fake-generation',
  generation: 0
};
const identity = {
  host: 'illustrator',
  processId: 4242,
  processStartedAt: '2026-09-26T08:00:00.0000000+00:00',
  executablePath: 'C:\\Program Files\\Adobe\\Illustrator.exe',
  hostVersion: '30.0',
  adapterVersion: 'test',
  endpointIdentity: 'fake',
  targetId: target.id
};

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity
});

for await (const line of rl) {
  if (!line) continue;
  const request = JSON.parse(line);

  if (request.operation === 'crash.after-read') {
    process.exit(17);
  }

  if (request.operation === 'delay.echo') {
    const delayMs = Number(request.input?.delayMs ?? 0);
    setTimeout(() => {
      process.stdout.write(JSON.stringify(success(request, {
        value: request.input?.value ?? null,
        delayMs
      })) + '\n');
    }, delayMs);
    continue;
  }

  if (
    request.operation === 'script.runFile' &&
    String(request.input?.path).includes('slow-response')
  ) {
    setTimeout(() => {
      process.stdout.write(JSON.stringify(success(request, {
        watchdogMs: request.policy?.workerWatchdogMs ?? null,
        expectedSha256: request.input?.expectedSha256 ?? null,
        args: request.input?.args ?? null,
        effects: request.input?.effects ?? null
      }, 'known_changed')) + '\n');
    }, 80);
    continue;
  }

  let response;
  switch (request.operation) {
    case 'core.operations.list':
      response = success(request, [
        operationDescriptor('core.operation.describe', false, false, 'runtime'),
        operationDescriptor('core.operations.list', false, false, 'runtime'),
        operationDescriptor(
          'script.eval',
          true,
          true,
          'host',
          'illustrator',
          'unknown',
          'declared_or_unknown'
        )
      ]);
      break;

    case 'core.operation.describe': {
      const name = request.input?.name;
      const known = {
        'core.operations.list':
          operationDescriptor('core.operations.list', false, false, 'runtime'),
        'core.operation.describe':
          operationDescriptor('core.operation.describe', false, false, 'runtime'),
        'script.eval':
          operationDescriptor(
            'script.eval',
            true,
            true,
            'host',
            'illustrator',
            'unknown',
            'declared_or_unknown'
          )
      }[name];

      response = known
        ? success(request, known)
        : {
            protocolVersion: 1,
            id: request.id,
            operation: request.operation,
            ok: false,
            status: 'unsupported_operation',
            targetState: 'known',
            error: {
              kind: 'unsupported_operation',
              message: `Operation '${name}' is not registered.`,
              retryable: false,
              execution: 'not_started',
              suggestedActions: ['core.operations.list']
            }
          };
      break;
    }

    case 'core.operation.examples':
      response = success(request, {
        query: request.input ?? {},
        count: 1,
        totalExamples: 19,
        coveredOperations: 19,
        catalogOperations: 60,
        examples: [{
          id: 'fake-example',
          operation: request.input?.operation ?? 'script.eval',
          title: 'Fake example',
          summary: 'Synthetic SDK transport fixture.',
          tags: request.input?.tags ?? ['test'],
          prerequisites: [],
          notes: [],
          safety: {
            mutationClass: 'read_only',
            requiresTarget: false,
            executionScope: 'runtime',
            host: null,
            requiresLease: false,
            mutationResolution: 'fixed'
          },
          request: {
            protocolVersion: 1,
            id: 'example-request-id',
            operation: request.input?.operation ?? 'script.eval',
            input: {}
          }
        }]
      });
      break;

    case 'knowledge.describe':
      response = success(request, {
        format: 'comtool.knowledge.pack/v1',
        version: 1,
        bodyBytes: 566322,
        bodySha256: 'a'.repeat(64),
        source: {
          kind: 'sqlite-index',
          database: 'illustrator_com.sqlite',
          databaseSha256: 'b'.repeat(64),
          json: 'illustrator_com_commands.json',
          jsonSha256: 'c'.repeat(64),
          jsonOnDiskSha256Observed: 'd'.repeat(64),
          jsonOnDiskMatchesManifest: false,
          manifest: 'inventory_manifest.json',
          exportGeneratedAtUtc: '2026-07-23T16:30:08Z',
          interfaceKinds: '{"DISPATCH":160,"COCLASS":64}',
          sourceOutgoingInterfacesFlagged: 0
        },
        host: {
          family: 'illustrator',
          version: 'unknown',
          versionKnown: false,
          versionSource: 'none'
        },
        buildEnvironment: {
          illustratorProduct: 'Adobe Illustrator 2026',
          illustratorProductVersion: '30.6.0',
          authoritative: false
        },
        counts: { methods: 670 },
        limits: { maxLimit: 200 }
      });
      break;

    case 'knowledge.search':
      response = success(request, {
        query: request.input?.query ?? '',
        count: 1,
        limit: request.input?.limit ?? 20,
        items: [{
          kind: 'method',
          interface: '_Application',
          name: 'DoScript'
        }],
        scan: { scanBudgetExhausted: false }
      });
      break;

    case 'knowledge.symbol':
      response = success(request, {
        name: request.input?.name ?? '',
        interface: request.input?.interface ?? null,
        count: 1,
        limit: request.input?.limit ?? 20,
        methods: [{
          interface: request.input?.interface ?? 'Document',
          name: request.input?.name ?? 'Close',
          parameters: [{ name: 'Saving', type: 'VT_VARIANT' }]
        }],
        properties: [],
        scan: { scanBudgetExhausted: false }
      });
      break;

    case 'knowledge.enum':
      response = success(request, {
        query: request.input?.name ?? '',
        count: 1,
        limit: request.input?.limit ?? 20,
        enums: [{
          name: request.input?.name ?? 'AiSaveOptions',
          values: [{ name: 'aiSaveChanges', value: 1 }]
        }],
        scan: { scanBudgetExhausted: false }
      });
      break;

    case 'knowledge.paths':
      response = success(request, {
        start: '_Application',
        targetInterface: 'Document',
        targetMember: request.input?.target?.includes('.')
          ? request.input.target.split('.').at(-1)
          : null,
        maxDepth: request.input?.maxDepth ?? 8,
        steps: [{
          from: '_Application',
          to: 'Document',
          kind: 'property',
          member: 'ActiveDocument',
          returnType: 'Document',
          parameters: [],
          signature: '_Application.ActiveDocument: Document'
        }],
        terminal: null,
        note: 'type navigation',
        scan: { scanBudgetExhausted: false }
      });
      break;

    case 'core.artifact.describe':
      response = success(request, {
        artifact: {
          artifactId: request.input?.artifactId ?? 'art_fake',
          sha256: request.input?.artifactId === 'art_bad_hash'
            ? '0'.repeat(64)
            : artifactSha256,
          byteCount: artifactContent.length,
          mediaType: 'text/plain',
          encoding: 'utf-8',
          createdAt: '2026-09-27T08:00:00Z',
          expiresAt: '2026-09-28T08:00:00Z',
          revision: 1
        }
      });
      break;

    case 'core.artifact.read':
      {
        const offset = request.input?.offset ?? 0;
        const length = request.input?.length ?? artifactContent.length;
        const content = artifactContent.subarray(offset, offset + length);
      response = success(request, {
        artifact: {
          artifactId: request.input?.artifactId ?? 'art_fake',
          sha256: request.input?.artifactId === 'art_bad_hash'
            ? '0'.repeat(64)
            : artifactSha256,
          byteCount: artifactContent.length,
          mediaType: 'text/plain',
          encoding: 'utf-8',
          createdAt: '2026-09-27T08:00:00Z',
          expiresAt: '2026-09-28T08:00:00Z',
          revision: 1
        },
        offset,
        returnedByteCount: content.length,
        isPartial:
          offset !== 0 ||
          content.length !== artifactContent.length,
        contentBase64: content.toString('base64')
      });
      break;
      }

    case 'core.incidents.list':
      response = success(request, [{
        targetId: target.id,
        host: target.host,
        processId: identity.processId,
        processStartedAt: identity.processStartedAt,
        requestId: 'ambiguous-request-offline',
        operation: 'script.eval',
        mutationClass: 'unknown',
        phase: 'ambiguous',
        incidentKind: 'worker_transport_lost',
        reconciliationFingerprint: 'f'.repeat(64),
        preparedAt: '2026-09-27T07:59:00Z',
        updatedAt: '2026-09-27T08:00:00Z',
        recordCorrupt: false
      }]);
      break;

    case 'core.incident.resolve':
      response = success(request, {
        targetId: request.input?.targetId ?? target.id,
        host: target.host,
        incidentRequestId:
          request.input?.incidentRequestId ?? 'ambiguous-request-offline',
        resolution: request.input?.resolution ?? 'known_unchanged',
        rationale: request.input?.rationale ?? 'test resolution',
        resolvedAt: '2026-09-27T08:05:00Z',
        targetRunning: false
      }, 'unavailable');
      break;

    case 'core.target.attach':
      response = success(request, {
        target: request.target ?? target,
        identity,
        running: true,
        capabilities: [],
        ownershipAcquired: false
      });
      break;

    case 'core.target.launch':
      if (request.input?.progId === 'Ambiguous.Application') {
        response = {
          protocolVersion: 1,
          id: request.id,
          operation: request.operation,
          ok: false,
          status: 'reconciliation_required',
          targetState: 'reconciliation_required',
          error: {
            kind: 'launch_transport_lost',
            message: 'synthetic ambiguous activation',
            retryable: false,
            execution: 'ambiguous',
            suggestedActions: ['core.targets.list']
          }
        };
      } else {
        response = success(request, {
          host: request.input?.host ?? 'illustrator',
          progId: request.input?.progId ?? 'Illustrator.Application',
          launchSpecKey: 'f'.repeat(64),
          ownership: 'launched_by_runtime',
          target,
          identity,
          observedAt: '2026-09-27T08:00:00Z',
          openDocumentCount: 0,
          attachHResult: null,
          activationHResult: 0,
          attempt: {
            requestId: request.id,
            outcome: 'launched',
            startedAt: '2026-09-27T07:59:59Z',
            completedAt: '2026-09-27T08:00:00Z',
            elapsedMs: 1000
          },
          durableOwnership: {
            targetId: target.id,
            state: 'owned',
            launchRequestId: request.id,
            launchSpecKey: 'f'.repeat(64)
          }
        });
      }
      break;

    case 'core.targets.list':
      response = success(request, [{
        target,
        identity,
        running: true,
        capabilities: [],
        runtimeState: { state: 'known', revision: 0 },
        lease: { held: false }
      }]);
      break;

    case 'core.target.lease.acquire':
      response = success(request, {
        target,
        leaseId,
        acquiredAt: '2026-09-26T08:00:00Z',
        expiresAt: '2026-09-26T09:00:00Z'
      });
      break;

    case 'core.target.lease.renew':
      response = success(request, {
        target,
        leaseId,
        acquiredAt: '2026-09-26T08:00:00Z',
        expiresAt: '2026-09-26T10:00:00Z'
      });
      break;

    case 'core.target.lease.release':
      response = success(request, {
        target,
        released: true,
        lease: { held: false }
      });
      break;

    case 'core.target.host.terminate':
      response = success(request, {
        target,
        processId: identity.processId,
        processStartedAt: identity.processStartedAt,
        killIssued: true,
        alreadyExited: false,
        exitObserved: true,
        workerAbortIssued: true
      }, 'unavailable');
      break;

    case 'session.inspect':
      response = success(request, {
        target: request.target ?? null,
        policy: request.policy ?? null,
        input: request.input ?? null
      });
      break;

    case 'core.target.reconcile':
    case 'core.target.mutation.reconcile':
    case 'core.target.incident.resolve':
      response = success(request, {
        target: request.target ?? null,
        policy: request.policy ?? null,
        input: request.input ?? null,
        postconditions: request.postconditions ?? null
      });
      break;

    case 'plugin.message': {
      const input = String(request.input?.input ?? '');
      const responseText = JSON.stringify({
        ok: true,
        plugin: request.input?.plugin ?? null,
        selector: request.input?.selector ?? null
      });
      response = success(request, {
        plugin: request.input?.plugin ?? null,
        selector: request.input?.selector ?? null,
        response: responseText,
        inputUtf8Bytes: Buffer.byteLength(input, 'utf8'),
        inputSha256: '1'.repeat(64),
        responseUtf8Bytes: Buffer.byteLength(responseText, 'utf8'),
        responseSha256: '2'.repeat(64)
      }, 'known_changed');
      break;
    }

    case 'debug.session.open':
      response = success(request, {
        sessionId: 'dbg-fake-session',
        appSpec: 'illustrator-30.064',
        engine: request.input?.engine ?? 'main',
        engines: ['main', 'transient'],
        idleTimeoutMs: request.input?.idleTimeoutMs ?? 120000,
        transport: 'estk3',
        workerOwned: true
      }, 'known_changed');
      break;

    case 'debug.session.command':
      response = success(request, {
        sessionId: request.input?.sessionId ?? null,
        command: request.input?.command ?? null,
        input: request.input ?? null,
        policy: request.policy ?? null,
        state: request.input?.command === 'eval' ? 'completed' : null
      }, 'known_changed');
      break;

    case 'debug.session.close':
      response = success(request, {
        sessionId: request.input?.sessionId ?? null,
        closed: true,
        graceful: true,
        exitObserved: true
      }, 'known_changed');
      break;

    case 'script.eval':
      {
        const discard = request.input?.resultMode === 'discard';
        const present = !discard && (
          request.input?.kind === 'expression' ||
          /\breturn\b/.test(String(request.input?.source ?? ''))
        );
        const value = discard
          ? null
          : request.input?.kind === 'expression' &&
            request.input?.source === '6*7'
            ? 42
            : null;
        response = scriptSuccess(
          request,
          value,
          present,
          'known_changed'
        );
      }
      break;

    case 'script.runFile':
      if (String(request.input?.path).includes('ambiguous')) {
        response = {
          protocolVersion: 1,
          id: request.id,
          operation: request.operation,
          ok: false,
          status: 'reconciliation_required',
          targetState: 'reconciliation_required',
          error: {
            kind: 'worker_watchdog_timeout',
            message: 'synthetic timeout after dispatch',
            retryable: false,
            execution: 'ambiguous',
            suggestedActions: ['inspect_mutation_ledger']
          }
        };
      } else {
        const discard = request.input?.resultMode === 'discard';
        response = scriptSuccess(
          request,
          discard
            ? null
            : {
                watchdogMs: request.policy?.workerWatchdogMs ?? null,
                expectedSha256: request.input?.expectedSha256 ?? null,
                args: request.input?.args ?? null,
                effects: request.input?.effects ?? null
              },
          !discard,
          'known_changed'
        );
      }
      break;

    default:
      response = success(request, {
        echo: request.input,
        policy: request.policy ?? null
      });
      break;
  }

  process.stdout.write(JSON.stringify(response) + '\n');
}

function success(request, value, targetState = 'known') {
  return {
    protocolVersion: 1,
    id: request.id,
    operation: request.operation,
    ok: true,
    status: 'completed',
    targetState,
    result: {
      kind: inferKind(value),
      value
    }
  };
}

function scriptSuccess(
  request,
  value,
  present,
  targetState = 'known'
) {
  const response = success(request, value, targetState);
  response.evidence = [{
    kind: 'script.result',
    value: {
      mode: request.input?.resultMode ?? 'capture',
      present
    }
  }];
  return response;
}

function inferKind(value) {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value === 'object' ? 'object' : typeof value;
}

function operationDescriptor(
  name,
  requiresTarget,
  requiresLease,
  executionScope,
  host = null,
  mutationClass = 'read_only',
  mutationResolution = 'fixed'
) {
  return {
    name,
    version: '1',
    mutationClass,
    requiresTarget,
    executionScope,
    host,
    requiresLease,
    mutationResolution,
    policyLimits: {
      workerWatchdogMs: { min: 100, max: 3_600_000 },
      retryBudgetMs: { min: 0, max: 3_600_000, default: 2_000 }
    }
  };
}
