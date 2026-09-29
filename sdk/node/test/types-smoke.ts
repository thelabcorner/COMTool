import {
  ComToolClient,
  ComToolDebugSession,
  ComToolLocalRuntime,
  ComToolRunner,
  ComToolTargetSession,
  terminateHostGeneration,
  unwrapProtocolValue,
  type ArtifactReadResult,
  type HostLaunchResult,
  type KnowledgePathsResult,
  type KnowledgeSearchResult,
  type OperationExamplesResult,
  type OperationResult,
  type TargetDescriptor
} from '@comtool/v2-node';

async function exerciseLocalRuntimeTypes() {
  const local = new ComToolLocalRuntime({
    selfHeal: false,
    layout: {
      kind: 'explicit',
      root: null,
      cliPath: 'C:\\ComTool.Cli.exe',
      runtimeHostPath: 'C:\\ComTool.RuntimeHost.exe',
      workerPath: 'C:\\ComTool.Worker.exe'
    }
  });
  local.layout.cliPath;
  local.pipeName;
  local.stateDir;
  local.started;
  await local.start({ startupTimeoutMs: 20_000 });
  const target = await local.selectTarget({ launch: true });
  target.target.id;
  const session = await local.openSession({
    targetId: target.target.id,
    lease: true,
    leaseTtlMs: 300_000
  });
  session.target.target.id;
  await local.close();
}

void exerciseLocalRuntimeTypes;

async function exercise(client: ComToolClient, target: TargetDescriptor) {
  const generic = await client.execute<{ ok: boolean }>(
    'plugin.future.debug.inspect',
    { address: 0x1234 },
    {
      id: 'types-generic',
      target: target.target,
      policy: { workerWatchdogMs: 120_000 }
    }
  );
  const genericValue = unwrapProtocolValue(generic.result);
  genericValue?.ok;

  const runner = new ComToolRunner({ client });
  const operations = await runner.listOperations();
  operations[0]?.requiresLease;
  const operation = await runner.describeOperation('script.eval');
  operation.mutationResolution;
  operation.policyLimits?.retryBudgetMs.default;

  const examples: OperationExamplesResult | undefined =
    await runner.operationExamples({
      operation: 'script.eval',
      tags: ['script'],
      limit: 4
    });
  examples?.examples[0]?.safety.requiresLease;

  const knowledgeSearch: KnowledgeSearchResult | undefined =
    await runner.knowledgeSearch('DoScript', { limit: 5 });
  knowledgeSearch?.items[0]?.kind;

  const symbol = await runner.knowledgeSymbol('Close', {
    interface: 'Document',
    limit: 5
  });
  symbol?.methods[0]?.name;

  const enumResult = await runner.knowledgeEnum('AiSaveOptions');
  enumResult?.enums[0]?.values[0];

  const path: KnowledgePathsResult | undefined =
    await runner.knowledgePaths('Application', 'Document', {
      maxDepth: 4
    });
  path?.steps[0]?.signature;

  const artifact = await runner.describeArtifact('art_fake');
  artifact?.artifact.sha256;

  const artifactRead: ArtifactReadResult | undefined =
    await runner.readArtifact('art_fake', {
      offset: 0,
      length: 64
    });
  artifactRead?.contentBase64;
  const artifactAll = await runner.readArtifactAll('art_fake', {
    chunkBytes: 64
  });
  artifactAll.content.byteLength;

  const incidents = await runner.listIncidents();
  incidents[0]?.recordCorrupt;
  const offlineResolution = await runner.resolveOfflineIncident({
    targetId: 'illustrator:fake-generation',
    incidentRequestId: 'request-offline-1',
    expectedUpdatedAt: '2026-09-27T08:00:00Z',
    resolution: 'known_unchanged',
    rationale: 'Type-surface proof only.'
  });
  offlineResolution?.targetRunning;

  const attached: TargetDescriptor = await runner.attachTarget(target);
  attached.target.id;

  const launch: OperationResult<HostLaunchResult> =
    await runner.launchTarget({
      host: 'illustrator',
      progId: 'Illustrator.Application',
      existingInstance: 'fail',
      arguments: []
    });
  launch.result?.value?.ownership;

  const session: ComToolTargetSession = await runner.openSession({ target });

  const status: OperationResult = await session.status();
  status.ok;

  const lease = await session.acquireLease({ ttlMs: 300_000 });
  lease.id;

  const pluginMessage = await session.pluginMessage(
    'ExamplePlugin',
    'rpc/v1',
    '{"requestId":"types-plugin"}'
  );
  pluginMessage.result?.value?.responseSha256;

  const openedDebugger = await session.openDebugger({
    engine: 'main',
    idleTimeoutMs: 60_000
  });
  const debuggerSession: ComToolDebugSession | null =
    openedDebugger.session;
  if (debuggerSession) {
    await debuggerSession.setBreakpoints([
      {
        file: 'file:///C:/tmp/probe.jsx',
        line: 12,
        condition: 'i > 5'
      }
    ]);
    await debuggerSession.eval('6*7', {
      debugLevel: 0,
      timeoutMs: 12_000
    });
    await debuggerSession.getFrame({ frame: 0 });
    await debuggerSession.stepOver();
    await debuggerSession.close();
  }

  await session.reconcileMutation({
    incidentRequestId: 'request-1',
    expectedRevision: 7,
    postconditions: [{
      sourceOperation: 'core.target.status',
      sourceInput: {},
      assertion: {
        kind: 'equals',
        path: 'documentsCount',
        expected: 1
      }
    }]
  });

  await session.resolveIncident({
    incidentRequestId: 'request-2',
    expectedRevision: 8,
    resolution: 'known_changed',
    rationale: 'Verified independently.',
    evidence: { observed: true }
  });

  const tested = await session.testEval<number>({
    source: '6*7',
    resultMode: 'capture',
    expected: 42
  });
  const value: number | undefined = tested.value;
  const resultPresent: boolean | null = tested.resultPresent;
  void value;
  void resultPresent;

  if (tested.ambiguous) {
    await terminateHostGeneration(runner, tested, {
      waitTimeoutMs: 10_000,
      releaseLease: false
    });
  }

  await session.releaseLease();
}

void exercise;
