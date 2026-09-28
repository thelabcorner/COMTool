import {
  createRequestId,
  unwrapProtocolValue
} from './client.mjs';

export class ComToolTargetSession {
  #runner;
  #target;
  #leaseId;

  constructor(runner, target, { leaseId = null } = {}) {
    if (
      runner == null ||
      typeof runner !== 'object' ||
      typeof runner.client?.execute !== 'function'
    ) {
      throw new TypeError('runner must be a ComToolRunner-like object.');
    }

    validateTarget(target);
    validateOptionalLeaseId(leaseId);

    this.#runner = runner;
    this.#target = target;
    this.#leaseId = leaseId;
  }

  get target() {
    return this.#target;
  }

  get identity() {
    return this.#target.identity ?? null;
  }

  get leaseId() {
    return this.#leaseId;
  }

  bindLease(leaseId) {
    validateLeaseId(leaseId);
    this.#leaseId = leaseId;
    return this;
  }

  unbindLease() {
    this.#leaseId = null;
    return this;
  }

  async refreshTarget({ refresh = true } = {}) {
    const targets = await this.#runner.listTargets({ refresh });
    const current = targets.find(entry =>
      entry?.running === true &&
      entry?.target?.host === this.#target.target.host &&
      entry?.target?.id === this.#target.target.id
    );

    if (!current) {
      throw new Error(
        `Strong target '${this.#target.target.id}' is no longer running or discoverable.`
      );
    }

    validateTarget(current);
    if (
      current.identity.processId !== this.#target.identity.processId ||
      current.identity.processStartedAt !== this.#target.identity.processStartedAt
    ) {
      throw new Error(
        'Target identity changed while retaining the same target id; refusing silent generation substitution.'
      );
    }

    this.#target = current;
    return current;
  }

  async acquireLease(options = {}) {
    if (this.#leaseId !== null) {
      throw new Error(
        'Target session already has a bound lease; release or unbind it before acquiring another.'
      );
    }

    const lease = await this.#runner.acquireLease(
      this.#target,
      options
    );
    this.#leaseId = lease.id;
    return lease;
  }

  async renewLease(options = {}) {
    const leaseId = this.#requireLease();
    return this.#runner.renewLease(
      this.#target,
      leaseId,
      options
    );
  }

  async releaseLease() {
    const leaseId = this.#requireLease();
    const result = await this.#runner.releaseLease(
      this.#target,
      leaseId
    );
    this.#leaseId = null;
    return result;
  }

  async execute(
    operation,
    input = null,
    {
      id = createRequestId('session'),
      policy,
      preconditions,
      postconditions,
      signal,
      responseTimeoutMs,
      useLease = true
    } = {}
  ) {
    const mergedPolicy = mergePolicy(
      policy,
      useLease ? this.#leaseId : null
    );

    return this.#runner.client.execute(
      operation,
      input,
      {
        id,
        target: this.#target.target,
        policy: mergedPolicy,
        preconditions,
        postconditions,
        signal,
        responseTimeoutMs
      }
    );
  }

  async reconcile(options = {}) {
    this.#requireLease();
    return this.execute(
      'core.target.reconcile',
      {},
      options
    );
  }

  async reconcileMutation({
    incidentRequestId,
    expectedRevision,
    postconditions,
    ...options
  } = {}) {
    this.#requireLease();
    validateIncidentRequestId(incidentRequestId);
    validateExpectedRevision(expectedRevision);
    if (!Array.isArray(postconditions) || postconditions.length === 0) {
      throw new TypeError(
        'postconditions must be the non-empty exact original postcondition array.'
      );
    }

    return this.execute(
      'core.target.mutation.reconcile',
      { incidentRequestId, expectedRevision },
      {
        ...options,
        postconditions
      }
    );
  }

  async resolveIncident({
    incidentRequestId,
    expectedRevision,
    resolution,
    rationale,
    evidence,
    ...options
  } = {}) {
    this.#requireLease();
    validateIncidentRequestId(incidentRequestId);
    validateExpectedRevision(expectedRevision);
    if (resolution !== 'known_changed' && resolution !== 'known_unchanged') {
      throw new TypeError(
        "resolution must be 'known_changed' or 'known_unchanged'."
      );
    }
    if (typeof rationale !== 'string' || rationale.trim().length === 0) {
      throw new TypeError('rationale must be a non-empty string.');
    }

    const input = {
      incidentRequestId,
      expectedRevision,
      resolution,
      rationale: rationale.trim()
    };
    if (evidence !== undefined) input.evidence = evidence;

    return this.execute(
      'core.target.incident.resolve',
      input,
      options
    );
  }

  async status(options = {}) {
    return this.execute(
      'core.target.status',
      {},
      options
    );
  }

  async snapshot(options = {}) {
    return this.execute(
      'core.target.snapshot',
      {},
      options
    );
  }

  async capabilities(options = {}) {
    return this.execute(
      'core.target.capabilities',
      {},
      options
    );
  }

  async scriptCodecStatus(options = {}) {
    return this.execute(
      'script.codec.status',
      {},
      {
        ...options,
        useLease: false
      }
    );
  }

  async comGet(path, options = {}) {
    if (typeof path !== 'string' || path.length === 0) {
      throw new TypeError('path must be a non-empty string.');
    }

    return this.execute(
      'com.get',
      { path },
      options
    );
  }

  async comCallRead(path, args = [], options = {}) {
    if (typeof path !== 'string' || path.length === 0) {
      throw new TypeError('path must be a non-empty string.');
    }
    if (!Array.isArray(args)) {
      throw new TypeError('args must be an array.');
    }

    return this.execute(
      'com.call.read',
      { path, args },
      options
    );
  }

  async pluginMessage(plugin, selector, input = '', options = {}) {
    this.#requireLease();
    if (typeof plugin !== 'string' || plugin.trim().length === 0) {
      throw new TypeError('plugin must be a non-empty string.');
    }
    if (typeof selector !== 'string' || selector.trim().length === 0) {
      throw new TypeError('selector must be a non-empty string.');
    }
    if (typeof input !== 'string') {
      throw new TypeError('input must be a string.');
    }

    return this.execute(
      'plugin.message',
      {
        plugin: plugin.trim(),
        selector: selector.trim(),
        input
      },
      options
    );
  }

  async openDebugger(input = {}, options = {}) {
    this.#requireLease();
    validatePlainObject(input, 'input');
    if (input.sessionId !== undefined || input.command !== undefined) {
      throw new TypeError(
        'debugger open input must not contain sessionId or command.'
      );
    }

    const result = await this.execute(
      'debug.session.open',
      input,
      options
    );
    if (result?.ok !== true) {
      return { result, session: null };
    }

    const payload = unwrapProtocolValue(result.result);
    if (
      payload == null ||
      typeof payload !== 'object' ||
      typeof payload.sessionId !== 'string' ||
      payload.sessionId.length === 0
    ) {
      throw new Error(
        'debug.session.open completed without a valid sessionId.'
      );
    }

    return {
      result,
      session: new ComToolDebugSession(
        this,
        payload.sessionId,
        payload
      )
    };
  }

  async runFile(options = {}) {
    return this.#runner.runFile(
      bindScriptOptions(
        options,
        this.#target,
        this.#leaseId
      )
    );
  }

  async runEval(options = {}) {
    return this.#runner.runEval(
      bindScriptOptions(
        options,
        this.#target,
        this.#leaseId
      )
    );
  }

  async testFile(options = {}) {
    return this.#runner.testFile(
      bindScriptOptions(
        options,
        this.#target,
        this.#leaseId
      )
    );
  }

  async testEval(options = {}) {
    return this.#runner.testEval(
      bindScriptOptions(
        options,
        this.#target,
        this.#leaseId
      )
    );
  }

  #requireLease() {
    if (this.#leaseId === null) {
      throw new Error(
        'Target session has no bound lease.'
      );
    }
    return this.#leaseId;
  }
}

export class ComToolDebugSession {
  #targetSession;
  #sessionId;
  #openInfo;
  #closed = false;

  constructor(targetSession, sessionId, openInfo = null) {
    if (!(targetSession instanceof ComToolTargetSession)) {
      throw new TypeError(
        'targetSession must be a ComToolTargetSession.'
      );
    }
    if (typeof sessionId !== 'string' || sessionId.length === 0) {
      throw new TypeError('sessionId must be a non-empty string.');
    }

    this.#targetSession = targetSession;
    this.#sessionId = sessionId;
    this.#openInfo = openInfo;
  }

  get sessionId() {
    return this.#sessionId;
  }

  get openInfo() {
    return this.#openInfo;
  }

  get closed() {
    return this.#closed;
  }

  async command(command, input = {}, options = {}) {
    this.#ensureOpen();
    if (typeof command !== 'string' || command.trim().length === 0) {
      throw new TypeError('command must be a non-empty string.');
    }
    validatePlainObject(input, 'input');
    if (input.sessionId !== undefined || input.command !== undefined) {
      throw new TypeError(
        'debugger command input must not override sessionId or command.'
      );
    }

    return this.#targetSession.execute(
      'debug.session.command',
      {
        sessionId: this.#sessionId,
        command: command.trim(),
        ...input
      },
      options
    );
  }

  eval(source, input = {}, options = {}) {
    if (typeof source !== 'string') {
      throw new TypeError('source must be a string.');
    }
    return this.command(
      'eval',
      mergeDebugInput(input, { source }),
      options
    );
  }

  setBreakpoints(breakpoints, input = {}, options = {}) {
    if (!Array.isArray(breakpoints)) {
      throw new TypeError('breakpoints must be an array.');
    }
    return this.command(
      'set-breakpoints',
      mergeDebugInput(input, { breakpoints }),
      options
    );
  }

  getBreakpoints(input = {}, options = {}) {
    return this.command('get-breakpoints', input, options);
  }

  getBreak(input = {}, options = {}) {
    return this.command('get-break', input, options);
  }

  getFrame(input = {}, options = {}) {
    return this.command('get-frame', input, options);
  }

  setFrame(frame = 0, input = {}, options = {}) {
    if (!Number.isInteger(frame) || frame < 0) {
      throw new TypeError('frame must be a non-negative integer.');
    }
    return this.command(
      'set-frame',
      mergeDebugInput(input, { frame }),
      options
    );
  }

  getProperties(input = {}, options = {}) {
    return this.command('get-properties', input, options);
  }

  resume(input = {}, options = {}) {
    return this.command('continue', input, options);
  }

  pause(input = {}, options = {}) {
    return this.command('break', input, options);
  }

  halt(input = {}, options = {}) {
    return this.command('halt', input, options);
  }

  stepOver(input = {}, options = {}) {
    return this.command('stepover', input, options);
  }

  stepInto(input = {}, options = {}) {
    return this.command('stepinto', input, options);
  }

  stepOut(input = {}, options = {}) {
    return this.command('stepout', input, options);
  }

  async close(options = {}) {
    this.#ensureOpen();
    const result = await this.#targetSession.execute(
      'debug.session.close',
      { sessionId: this.#sessionId },
      options
    );
    if (result?.ok === true) {
      this.#closed = true;
    }
    return result;
  }

  #ensureOpen() {
    if (this.#closed) {
      throw new Error('Debugger session is already closed.');
    }
  }
}

function bindScriptOptions(
  options,
  target,
  boundLeaseId
) {
  if (options == null || typeof options !== 'object') {
    throw new TypeError('options must be an object.');
  }

  if (
    options.target !== undefined ||
    options.targetId !== undefined ||
    options.host !== undefined
  ) {
    throw new TypeError(
      'Target-bound session methods do not accept target, targetId, or host overrides.'
    );
  }

  if (
    boundLeaseId !== null &&
    options.leaseId !== undefined &&
    options.leaseId !== boundLeaseId
  ) {
    throw new Error(
      'Script operation leaseId conflicts with the target session lease.'
    );
  }

  return {
    ...options,
    target,
    host: target.target.host,
    leaseId: options.leaseId ?? boundLeaseId ?? undefined
  };
}

function validatePlainObject(value, name) {
  if (
    value == null ||
    typeof value !== 'object' ||
    Array.isArray(value)
  ) {
    throw new TypeError(`${name} must be an object.`);
  }
}

function mergeDebugInput(input, required) {
  validatePlainObject(input, 'input');
  for (const key of Object.keys(required)) {
    if (input[key] !== undefined) {
      throw new TypeError(
        `debugger input must not override ${key}.`
      );
    }
  }
  return {
    ...input,
    ...required
  };
}

function mergePolicy(
  policy,
  boundLeaseId
) {
  if (
    policy !== undefined &&
    (
      policy === null ||
      typeof policy !== 'object' ||
      Array.isArray(policy)
    )
  ) {
    throw new TypeError('policy must be an object.');
  }

  if (boundLeaseId === null) {
    return policy;
  }

  if (
    policy?.leaseId !== undefined &&
    policy.leaseId !== boundLeaseId
  ) {
    throw new Error(
      'Operation policy leaseId conflicts with the target session lease.'
    );
  }

  return {
    ...(policy ?? {}),
    leaseId: boundLeaseId
  };
}

function validateTarget(target) {
  if (
    target == null ||
    typeof target !== 'object' ||
    target.target == null ||
    typeof target.target !== 'object' ||
    typeof target.target.host !== 'string' ||
    target.target.host.length === 0 ||
    typeof target.target.id !== 'string' ||
    target.target.id.length === 0 ||
    target.identity == null ||
    !Number.isInteger(target.identity.processId) ||
    typeof target.identity.processStartedAt !== 'string' ||
    target.identity.processStartedAt.length === 0
  ) {
    throw new TypeError(
      'target must be a strong target selection with target and PID/start-time identity.'
    );
  }
}

function validateIncidentRequestId(incidentRequestId) {
  if (
    typeof incidentRequestId !== 'string' ||
    incidentRequestId.length === 0 ||
    incidentRequestId.length > 512
  ) {
    throw new TypeError(
      'incidentRequestId must be a non-empty string of at most 512 characters.'
    );
  }
}

function validateExpectedRevision(expectedRevision) {
  if (
    !Number.isSafeInteger(expectedRevision) ||
    expectedRevision < 0
  ) {
    throw new TypeError(
      'expectedRevision must be a non-negative safe integer.'
    );
  }
}

function validateOptionalLeaseId(leaseId) {
  if (leaseId === null || leaseId === undefined) return;
  validateLeaseId(leaseId);
}

function validateLeaseId(leaseId) {
  if (typeof leaseId !== 'string' || leaseId.length === 0) {
    throw new TypeError('leaseId must be a non-empty string.');
  }
}
