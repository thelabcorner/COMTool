export type TargetState =
  | 'known'
  | 'known_changed'
  | 'busy'
  | 'unavailable'
  | 'reconciliation_required'
  | string;

export type OperationStatus =
  | 'completed'
  | 'failed'
  | 'invalid_request'
  | 'unsupported_operation'
  | 'host_busy'
  | 'target_unavailable'
  | 'reconciliation_required'
  | string;

export type ExecutionState =
  | 'not_started'
  | 'started'
  | 'completed'
  | 'ambiguous'
  | string;

export type ScriptEffects =
  | 'unknown'
  | 'idempotent_write'
  | 'conditional_write'
  | 'non_idempotent_write'
  | 'document_lifecycle'
  | 'external_side_effect';

export type MutationClass =
  | 'read_only'
  | ScriptEffects
  | string;

export interface TargetRef {
  host: string;
  id: string;
  generation?: number;
}

export interface TargetIdentity {
  host: string;
  processId: number;
  processStartedAt: string;
  executablePath?: string | null;
  hostVersion?: string | null;
  adapterVersion?: string | null;
  endpointIdentity?: string | null;
  targetId: string;
}

export interface CapabilityDescriptor {
  name: string;
  version: string;
  mutationClass: MutationClass;
  supported: boolean;
  host?: string | null;
  description?: string | null;
  inputSchemaRef?: string | null;
  outputSchemaRef?: string | null;
}

export interface OperationDescriptor {
  name: string;
  version: string;
  mutationClass: MutationClass;
  requiresTarget: boolean;
  executionScope: 'runtime' | 'host' | string;
  host: string | null;
  requiresLease: boolean;
  mutationResolution: 'fixed' | 'declared_or_unknown' | string;
  policyLimits?: {
    workerWatchdogMs: { min: number; max: number };
    retryBudgetMs: { min: number; max: number; default: number };
  };
}

export interface ScriptCodecStatusResult {
  codec: 'eson' | string;
  optional: boolean;
  expectedSha256: string;
  embeddedResource: string;
  embeddedVerified: boolean;
  installed: boolean;
  installedSha256Matches: boolean;
  presenceProbe: 'fixed_read_only' | string;
  executionMode: number;
  fallback: 'bootstrap_on_demand' | string;
  bootstrapRequired: boolean;
}

export interface OperationExamplesQuery {
  operation?: string;
  tags?: string[];
  words?: string[];
  limit?: number;
}

export interface OperationExampleProjection {
  id: string;
  operation: string;
  title: string;
  summary: string;
  tags: string[];
  prerequisites: Array<{ operation: string; why: string }>;
  notes: string[];
  safety: Omit<OperationDescriptor, 'name' | 'version'>;
  request: OperationRequest;
}

export interface OperationExamplesResult {
  query: OperationExamplesQuery;
  count: number;
  totalExamples: number;
  coveredOperations: number;
  catalogOperations: number;
  examples: OperationExampleProjection[];
}

export interface KnowledgeDescribeResult {
  format: string;
  version: number;
  bodyBytes: number;
  bodySha256: string;
  source: {
    kind: string;
    database: string;
    databaseSha256: string;
    json: string;
    jsonSha256: string;
    jsonOnDiskSha256Observed?: string | null;
    jsonOnDiskMatchesManifest: boolean;
    manifest: string;
    exportGeneratedAtUtc: string;
    interfaceKinds: string;
    sourceOutgoingInterfacesFlagged: number;
  };
  host: {
    family: string;
    version: string;
    versionKnown: boolean;
    versionSource: string;
  };
  buildEnvironment: {
    illustratorProduct?: string | null;
    illustratorProductVersion?: string | null;
    authoritative: boolean;
  };
  counts: Record<string, number>;
  limits: Record<string, number>;
}

export interface KnowledgeSearchItem {
  kind: string;
  [key: string]: unknown;
}

export interface KnowledgeSearchResult {
  query: string;
  count: number;
  limit: number;
  items: KnowledgeSearchItem[];
  scan: Record<string, unknown>;
}

export interface KnowledgeSymbolResult {
  name: string;
  interface?: string | null;
  count: number;
  limit: number;
  methods: Array<Record<string, unknown>>;
  properties: Array<Record<string, unknown>>;
  scan: Record<string, unknown>;
}

export interface KnowledgeEnumResult {
  query: string;
  count: number;
  limit: number;
  enums: Array<{
    name: string;
    values: Array<Record<string, unknown>>;
  }>;
  scan: Record<string, unknown>;
}

export interface KnowledgePathStep {
  from: string;
  to: string;
  kind: 'property' | 'method' | string;
  member: string;
  returnType?: string | null;
  parameters: Array<Record<string, unknown>>;
  signature: string;
}

export interface KnowledgePathsResult {
  start: string;
  targetInterface: string;
  targetMember?: string | null;
  maxDepth: number;
  steps: KnowledgePathStep[];
  terminal?: Record<string, unknown> | null;
  note: string;
  scan: Record<string, unknown>;
}

export interface ArtifactDescriptor {
  artifactId: string;
  sha256: string;
  byteCount: number;
  mediaType: string;
  encoding: string;
  createdAt: string;
  expiresAt: string;
  revision: number;
}

export interface ArtifactDescribeResult {
  artifact: ArtifactDescriptor;
}

export interface ArtifactReadResult {
  artifact: ArtifactDescriptor;
  offset: number;
  returnedByteCount: number;
  isPartial: boolean;
  contentBase64: string;
}

export interface ArtifactReadAllResult {
  artifact: ArtifactDescriptor;
  content: Uint8Array;
}

export interface MutationIncidentSummary {
  targetId: string;
  host: string;
  processId: number;
  processStartedAt: string;
  requestId: string;
  operation: string;
  mutationClass: MutationClass;
  phase: 'prepared' | 'ambiguous' | string;
  incidentKind?: string | null;
  reconciliationFingerprint?: string | null;
  preparedAt: string;
  updatedAt: string;
  recordCorrupt: boolean;
}

export interface OfflineIncidentResolutionOptions extends RequestWaitOptions {
  targetId: string;
  incidentRequestId: string;
  expectedUpdatedAt: string;
  resolution: 'known_changed' | 'known_unchanged';
  rationale: string;
  evidence?: unknown;
  id?: string;
}

export interface OfflineIncidentResolutionResult {
  targetId: string;
  host: string;
  incidentRequestId: string;
  resolution: 'known_changed' | 'known_unchanged' | string;
  rationale: string;
  resolvedAt: string;
  targetRunning: false;
}

export type HostLaunchExistingInstancePolicy =
  | 'fail'
  | 'return_preexisting_without_ownership';

export type HostLaunchOwnership =
  | 'preexisting_without_ownership'
  | 'launched_by_runtime'
  | 'unproven';

export interface HostLaunchSpec {
  host: string;
  progId: string;
  expectedHostVersion?: string | null;
  launchTimeoutMs?: number;
  existingInstance?: HostLaunchExistingInstancePolicy;
  arguments?: string[];
}

export interface HostLaunchResult {
  host: string;
  progId: string;
  launchSpecKey: string;
  ownership: HostLaunchOwnership;
  target: TargetRef | null;
  identity: TargetIdentity | null;
  observedAt: string;
  openDocumentCount: number;
  attachHResult?: number | null;
  activationHResult?: number | null;
  attempt: {
    requestId: string;
    outcome: string;
    startedAt: string;
    completedAt: string;
    elapsedMs: number;
  };
  durableOwnership?: {
    targetId: string;
    state: string;
    launchRequestId: string;
    launchSpecKey: string;
  } | null;
}

export interface PluginMessageResult {
  plugin: string;
  selector: string;
  response: string;
  inputUtf8Bytes: number;
  inputSha256: string;
  responseUtf8Bytes: number;
  responseSha256: string;
}

export interface DebugBreakpoint {
  file: string;
  line: number;
  enabled?: boolean;
  condition?: string;
  hits?: number;
  count?: number;
}

export interface DebugSessionOpenInput {
  engine?: string;
  appSpec?: string;
  idleTimeoutMs?: number;
  connectTimeoutMs?: number;
}

export interface DebugSessionOpenInfo {
  sessionId: string;
  appSpec: string;
  engine: string;
  engines: string[];
  idleTimeoutMs: number;
  transport: 'estk3' | string;
  workerOwned: boolean;
  child?: {
    processId: number;
    processStartedAt: string;
  };
  provenance?: {
    nodePath?: string;
    nodeVersion?: string | null;
    addonSha256?: string;
    bridgeSha256?: string;
  };
  [key: string]: unknown;
}

export interface OpenDebuggerResult {
  result: OperationResult<DebugSessionOpenInfo>;
  session: ComToolDebugSession | null;
}

export type DebugCommandInput = Record<string, unknown>;

export interface RuntimeTargetState {
  state: TargetState;
  revision?: number;
  incidentKind?: string | null;
  [key: string]: unknown;
}

export interface TargetDescriptor {
  target: TargetRef;
  identity: TargetIdentity;
  running: boolean;
  capabilities?: CapabilityDescriptor[];
  runtimeState?: RuntimeTargetState;
  lease?: Record<string, unknown>;
  activeIncident?: unknown;
  [key: string]: unknown;
}

export interface OperationPolicy {
  leaseId?: string;
  workerWatchdogMs?: number;
  retryBudgetMs?: number;
}

export interface OperationTiming {
  queueMs?: number | null;
  executeMs?: number | null;
  totalMs?: number | null;
  [key: string]: number | null | undefined;
}

export interface ProtocolError {
  kind: string;
  message: string;
  retryable?: boolean;
  execution?: ExecutionState;
  hResult?: number | null;
  hResultHex?: string | null;
  suggestedActions?: string[];
  [key: string]: unknown;
}

export interface ProtocolValue<T = unknown> {
  kind: string;
  value: T;
  [key: string]: unknown;
}

export interface EvidenceItem {
  kind?: string;
  type?: string;
  value?: unknown;
  [key: string]: unknown;
}

export interface OperationResult<T = unknown> {
  protocolVersion: 1;
  id: string;
  operation: string;
  ok: boolean;
  status: OperationStatus;
  targetState: TargetState;
  result?: ProtocolValue<T>;
  error?: ProtocolError | null;
  evidence?: EvidenceItem[] | null;
  timing?: OperationTiming | null;
  [key: string]: unknown;
}

export interface OperationRequest<TInput = unknown> {
  protocolVersion: 1;
  id: string;
  operation: string;
  input: TInput;
  target?: TargetRef;
  policy?: OperationPolicy;
  preconditions?: unknown[];
  postconditions?: unknown[];
}

export interface RequestWaitOptions {
  signal?: AbortSignal;
  responseTimeoutMs?: number;
}

export interface RuntimeOperationOptions extends RequestWaitOptions {
  id?: string;
}

export interface ExecuteOptions extends RequestWaitOptions {
  id?: string;
  target?: TargetRef;
  policy?: OperationPolicy;
  preconditions?: unknown[];
  postconditions?: unknown[];
}

export interface ComToolClientOptions {
  cliPath?: string;
  pipeName?: string;
  cwd?: string;
  env?: Record<string, string | undefined>;
  spawnImpl?: (...args: any[]) => any;
  maxResponseBytes?: number;
  transportCommand?: {
    command: string;
    args: string[];
  };
}

export interface ComToolEvent {
  type?: string;
  event?: string;
  [key: string]: unknown;
}

export class ComToolTransportError extends Error {
  kind: string;
  classification: string;
  submitted: boolean;
  requestId: string | null;
  operation: string | null;
  details: unknown;
}

export function createRequestId(prefix?: string): string;
export function resolveDefaultCliPath(
  env?: Record<string, string | undefined>
): string;
export function unwrapProtocolValue<T = unknown>(
  protocolValue: ProtocolValue<T> | null | undefined
): T | undefined;

export class ComToolClient {
  constructor(options?: ComToolClientOptions);
  readonly started: boolean;
  start(): Promise<this>;
  execute<T = unknown, TInput = unknown>(
    operation: string,
    input?: TInput,
    options?: ExecuteOptions
  ): Promise<OperationResult<T>>;
  request<T = unknown>(
    request: OperationRequest,
    options?: RequestWaitOptions
  ): Promise<OperationResult<T>>;
  close(options?: { killAfterMs?: number }): Promise<void>;
  on(event: 'event', listener: (event: ComToolEvent) => void): this;
  on(event: string, listener: (...args: any[]) => void): this;
}

export interface Lease {
  id: string;
  acquiredAt: string | null;
  expiresAt: string | null;
  ttlMs: number | null;
  owned?: boolean;
  retained?: boolean;
}

export interface RunRecoveryDescriptor {
  available: boolean;
  operation?: 'core.target.host.terminate';
  requiresSameLease?: boolean;
  exactProcessGeneration?: {
    processId: number;
    processStartedAt: string;
  };
}

export type RunClassification =
  | 'completed'
  | 'ambiguous'
  | 'started_failure'
  | 'failed_not_started'
  | 'operation_failed'
  | 'transport_ambiguous'
  | 'transport_not_sent'
  | 'lease_cleanup_unknown'
  | 'input_error'
  | 'control_failure'
  | 'test_failed'
  | string;

export interface RunResult<T = unknown> {
  kind: 'comtool-v2-run';
  mode: 'run' | 'eval' | 'test' | 'test-eval' | string;
  classification: RunClassification;
  exitCode: 0 | 1 | 2 | 3 | number;
  ambiguous: boolean;
  startedAt: string;
  finishedAt: string;
  requestId: string;
  target: {
    target: TargetRef;
    identity: TargetIdentity;
  } | null;
  script: Record<string, unknown>;
  lease: Lease | null;
  operation: OperationResult<T> | null;
  value?: T;
  transportError?: Record<string, unknown> | null;
  cleanupError?: Record<string, unknown> | null;
  error?: Record<string, unknown> | null;
  recovery: RunRecoveryDescriptor;
  artifactPath?: string;
  test?: {
    passed: boolean;
    [key: string]: unknown;
  };
  [key: string]: unknown;
}

export interface RunnerOptions extends ComToolClientOptions {
  client?: ComToolClient;
  onEvent?: (event: ComToolEvent) => void;
}

export interface SelectTargetOptions {
  host?: string;
  targetId?: string;
  refresh?: boolean;
}

export interface OpenSessionOptions extends SelectTargetOptions {
  target?: TargetDescriptor;
  leaseId?: string | null;
}

export interface ScriptCommonOptions extends RequestWaitOptions {
  args?: unknown[];
  effects?: ScriptEffects;
  watchdogMs?: number;
  retryBudgetMs?: number;
  recoveryGraceMs?: number;
  leaseTtlMs?: number;
  leaseId?: string;
  requestId?: string;
  target?: TargetDescriptor;
  targetId?: string;
  host?: string;
  preconditions?: unknown[];
  postconditions?: unknown[];
  artifactDir?: string;
}

export interface RunFileOptions extends ScriptCommonOptions {
  path: string;
}

export interface RunEvalOptions extends ScriptCommonOptions {
  kind?: 'expression' | 'code';
  source: string;
}

export interface TestAssertionOptions {
  expected?: unknown;
  assert?: (value: unknown, run: RunResult) => boolean | void | Promise<boolean | void>;
}

export type TestFileOptions = RunFileOptions & TestAssertionOptions;
export type TestEvalOptions = RunEvalOptions & TestAssertionOptions;

export interface SessionExecuteOptions extends RequestWaitOptions {
  id?: string;
  policy?: OperationPolicy;
  preconditions?: unknown[];
  postconditions?: unknown[];
  useLease?: boolean;
}

export interface ReconcileMutationOptions extends RequestWaitOptions {
  incidentRequestId: string;
  expectedRevision: number;
  postconditions: unknown[];
  id?: string;
}

export interface ResolveIncidentOptions extends RequestWaitOptions {
  incidentRequestId: string;
  expectedRevision: number;
  resolution: 'known_changed' | 'known_unchanged';
  rationale: string;
  evidence?: unknown;
  id?: string;
}

export class ComToolTargetSession {
  constructor(
    runner: ComToolRunner,
    target: TargetDescriptor,
    options?: { leaseId?: string | null }
  );
  readonly target: TargetDescriptor;
  readonly identity: TargetIdentity | null;
  readonly leaseId: string | null;
  bindLease(leaseId: string): this;
  unbindLease(): this;
  refreshTarget(options?: { refresh?: boolean }): Promise<TargetDescriptor>;
  acquireLease(options?: { ttlMs?: number }): Promise<Lease>;
  renewLease(options?: { ttlMs?: number }): Promise<Lease>;
  releaseLease(): Promise<OperationResult>;
  execute<T = unknown, TInput = unknown>(
    operation: string,
    input?: TInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  reconcile(options?: SessionExecuteOptions): Promise<OperationResult>;
  reconcileMutation(
    options: ReconcileMutationOptions
  ): Promise<OperationResult>;
  resolveIncident(
    options: ResolveIncidentOptions
  ): Promise<OperationResult>;
  status(options?: SessionExecuteOptions): Promise<OperationResult>;
  snapshot<T = unknown>(
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  capabilities(
    options?: SessionExecuteOptions
  ): Promise<OperationResult<CapabilityDescriptor[]>>;
  scriptCodecStatus(
    options?: SessionExecuteOptions
  ): Promise<OperationResult<ScriptCodecStatusResult>>;
  comGet<T = unknown>(
    path: string,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  comCallRead<T = unknown>(
    path: string,
    args?: unknown[],
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  pluginMessage(
    plugin: string,
    selector: string,
    input?: string,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<PluginMessageResult>>;
  openDebugger(
    input?: DebugSessionOpenInput,
    options?: SessionExecuteOptions
  ): Promise<OpenDebuggerResult>;
  runFile<T = unknown>(options: RunFileOptions): Promise<RunResult<T>>;
  runEval<T = unknown>(options: RunEvalOptions): Promise<RunResult<T>>;
  testFile<T = unknown>(options: TestFileOptions): Promise<RunResult<T>>;
  testEval<T = unknown>(options: TestEvalOptions): Promise<RunResult<T>>;
}

export class ComToolDebugSession {
  constructor(
    targetSession: ComToolTargetSession,
    sessionId: string,
    openInfo?: DebugSessionOpenInfo | null
  );
  readonly sessionId: string;
  readonly openInfo: DebugSessionOpenInfo | null;
  readonly closed: boolean;
  command<T = unknown>(
    command: string,
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  eval<T = unknown>(
    source: string,
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  setBreakpoints<T = unknown>(
    breakpoints: DebugBreakpoint[],
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  getBreakpoints<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  getBreak<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  getFrame<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  setFrame<T = unknown>(
    frame?: number,
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  getProperties<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  resume<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  pause<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  halt<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  stepOver<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  stepInto<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  stepOut<T = unknown>(
    input?: DebugCommandInput,
    options?: SessionExecuteOptions
  ): Promise<OperationResult<T>>;
  close(
    options?: SessionExecuteOptions
  ): Promise<OperationResult>;
}

export class ComToolRunner {
  constructor(options?: RunnerOptions);
  readonly client: ComToolClient;
  close(): Promise<void>;
  listOperations(): Promise<OperationDescriptor[]>;
  describeOperation(name: string): Promise<OperationDescriptor>;
  operationExamples(
    query?: OperationExamplesQuery,
    options?: RuntimeOperationOptions
  ): Promise<OperationExamplesResult | undefined>;
  knowledgeDescribe(
    options?: RuntimeOperationOptions
  ): Promise<KnowledgeDescribeResult | undefined>;
  knowledgeSearch(
    query: string,
    options?: RuntimeOperationOptions & { limit?: number }
  ): Promise<KnowledgeSearchResult | undefined>;
  knowledgeSymbol(
    name: string,
    options?: RuntimeOperationOptions & {
      interface?: string;
      limit?: number;
    }
  ): Promise<KnowledgeSymbolResult | undefined>;
  knowledgeEnum(
    name: string,
    options?: RuntimeOperationOptions & { limit?: number }
  ): Promise<KnowledgeEnumResult | undefined>;
  knowledgePaths(
    start: string,
    target: string,
    options?: RuntimeOperationOptions & { maxDepth?: number }
  ): Promise<KnowledgePathsResult | undefined>;
  describeArtifact(
    artifactId: string,
    options?: RuntimeOperationOptions
  ): Promise<ArtifactDescribeResult | undefined>;
  readArtifact(
    artifactId: string,
    options?: RuntimeOperationOptions & {
      offset?: number;
      length?: number;
    }
  ): Promise<ArtifactReadResult | undefined>;
  readArtifactAll(
    artifactId: string,
    options?: RuntimeOperationOptions & {
      chunkBytes?: number;
      maxBytes?: number;
    }
  ): Promise<ArtifactReadAllResult>;
  listIncidents(
    options?: RuntimeOperationOptions
  ): Promise<MutationIncidentSummary[]>;
  resolveOfflineIncident(
    options: OfflineIncidentResolutionOptions
  ): Promise<OfflineIncidentResolutionResult | undefined>;
  attachTarget(
    target: TargetDescriptor,
    options?: RuntimeOperationOptions
  ): Promise<TargetDescriptor & { ownershipAcquired?: boolean }>;
  launchTarget(
    spec: HostLaunchSpec,
    options?: RuntimeOperationOptions
  ): Promise<OperationResult<HostLaunchResult>>;
  listTargets(options?: { refresh?: boolean }): Promise<TargetDescriptor[]>;
  selectTarget(options?: SelectTargetOptions): Promise<TargetDescriptor>;
  openSession(options?: OpenSessionOptions): Promise<ComToolTargetSession>;
  acquireLease(
    target: TargetDescriptor,
    options?: { ttlMs?: number }
  ): Promise<Lease>;
  renewLease(
    target: TargetDescriptor,
    leaseId: string,
    options?: { ttlMs?: number }
  ): Promise<Lease>;
  releaseLease(
    target: TargetDescriptor,
    leaseId: string
  ): Promise<OperationResult>;
  runFile<T = unknown>(options: RunFileOptions): Promise<RunResult<T>>;
  runEval<T = unknown>(options: RunEvalOptions): Promise<RunResult<T>>;
  testFile<T = unknown>(options: TestFileOptions): Promise<RunResult<T>>;
  testEval<T = unknown>(options: TestEvalOptions): Promise<RunResult<T>>;
  writeArtifact(
    directory: string,
    requestId: string,
    value: unknown
  ): Promise<string>;
}

export interface OperationClassification {
  classification: RunClassification;
  exitCode: number;
  ambiguous: boolean;
}

export function classifyOperationResult(
  result: OperationResult | null | undefined
): OperationClassification;

export interface HostRecoveryResult<T = unknown> {
  kind: 'comtool-v2-host-recovery';
  requestId: string;
  classification: RunClassification;
  exitCode: number;
  ambiguous: boolean;
  target: {
    target: TargetRef;
    identity: TargetIdentity;
  };
  operation: OperationResult<T> | null;
  value?: T;
  transportError?: Record<string, unknown> | null;
  leaseReleased: boolean;
  cleanupError?: Record<string, unknown> | null;
}

export function terminateHostGeneration<T = unknown>(
  runner: ComToolRunner,
  run: RunResult,
  options?: {
    waitTimeoutMs?: number;
    releaseLease?: boolean;
  }
): Promise<HostRecoveryResult<T>>;

export const DEFAULT_WORKER_WATCHDOG_MS: number;
export const MAX_WORKER_WATCHDOG_MS: number;
export const MAX_RETRY_BUDGET_MS: number;
export const MAX_LEASE_TTL_MS: number;
export const MAX_ARTIFACT_PROTOCOL_READ_BYTES: number;
export const DEFAULT_ARTIFACT_READ_ALL_MAX_BYTES: number;
