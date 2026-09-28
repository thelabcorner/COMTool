import { spawn as nodeSpawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { join, resolve } from 'node:path';
import { StringDecoder } from 'node:string_decoder';

export const PROTOCOL_VERSION = 1;
const DEFAULT_MAX_RESPONSE_BYTES = 4 * 1024 * 1024;

export class ComToolTransportError extends Error {
  constructor(kind, message, {
    classification = 'transport_failure',
    submitted = false,
    requestId = null,
    operation = null,
    cause,
    details
  } = {}) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = 'ComToolTransportError';
    this.kind = kind;
    this.classification = classification;
    this.submitted = submitted;
    this.requestId = requestId;
    this.operation = operation;
    this.details = details ?? null;
  }
}

export function createRequestId(prefix = 'node') {
  const safePrefix = String(prefix)
    .replace(/[^A-Za-z0-9_.-]/g, '-')
    .slice(0, 24) || 'node';
  return `${safePrefix}-${randomUUID()}`;
}

export function resolveDefaultCliPath(env = process.env) {
  if (env.COMTOOL_V2_CLI_PATH) {
    return resolve(env.COMTOOL_V2_CLI_PATH);
  }

  if (!env.LOCALAPPDATA) {
    throw new ComToolTransportError(
      'cli_path_unavailable',
      'COM Tool V2 CLI path could not be inferred because LOCALAPPDATA is unset. Set COMTOOL_V2_CLI_PATH or pass cliPath.',
      { classification: 'transport_not_started' }
    );
  }

  return join(
    env.LOCALAPPDATA,
    'Programs',
    'ComToolV2',
    'current',
    'ComTool.Cli.exe'
  );
}

export function unwrapProtocolValue(protocolValue) {
  if (protocolValue == null || typeof protocolValue !== 'object') {
    return undefined;
  }
  return protocolValue.value;
}

export class ComToolClient extends EventEmitter {
  #command;
  #args;
  #cwd;
  #env;
  #spawn;
  #maxResponseBytes;
  #child = null;
  #startPromise = null;
  #closing = false;
  #faulted = false;
  #pending = new Map();
  #stdoutDecoder = new StringDecoder('utf8');
  #stderrDecoder = new StringDecoder('utf8');
  #stdoutBuffer = '';
  #stderrBuffer = '';

  constructor({
    cliPath,
    pipeName,
    cwd,
    env = process.env,
    spawnImpl = nodeSpawn,
    maxResponseBytes = DEFAULT_MAX_RESPONSE_BYTES,
    transportCommand
  } = {}) {
    super();

    if (!Number.isSafeInteger(maxResponseBytes) || maxResponseBytes < 1024) {
      throw new TypeError('maxResponseBytes must be an integer >= 1024.');
    }

    this.#cwd = cwd;
    this.#env = env;
    this.#spawn = spawnImpl;
    this.#maxResponseBytes = maxResponseBytes;

    if (transportCommand !== undefined) {
      if (
        transportCommand == null ||
        typeof transportCommand.command !== 'string' ||
        transportCommand.command.length === 0 ||
        !Array.isArray(transportCommand.args)
      ) {
        throw new TypeError(
          'transportCommand must be { command: string, args: string[] }.'
        );
      }
      this.#command = transportCommand.command;
      this.#args = [...transportCommand.args];
    } else {
      this.#command = cliPath
        ? resolve(cliPath)
        : resolveDefaultCliPath(env);
      this.#args = ['stdio'];
      if (pipeName) {
        this.#args.push('--pipe', String(pipeName));
      }
    }
  }

  get started() {
    return this.#child !== null && !this.#faulted;
  }

  async start() {
    if (this.#startPromise) {
      return this.#startPromise;
    }

    if (this.#closing) {
      throw new ComToolTransportError(
        'client_closed',
        'COM Tool client has already been closed.',
        { classification: 'transport_not_started' }
      );
    }

    this.#startPromise = new Promise((resolveStart, rejectStart) => {
      let child;
      try {
        child = this.#spawn(this.#command, this.#args, {
          cwd: this.#cwd,
          env: this.#env,
          shell: false,
          windowsHide: true,
          stdio: ['pipe', 'pipe', 'pipe']
        });
      } catch (error) {
        rejectStart(new ComToolTransportError(
          'transport_spawn_failed',
          `Failed to start COM Tool stdio transport: ${error.message}`,
          {
            classification: 'transport_not_started',
            submitted: false,
            cause: error
          }
        ));
        return;
      }

      this.#child = child;

      child.stdout.on('data', chunk => this.#onStdout(chunk));
      child.stderr.on('data', chunk => this.#onStderr(chunk));
      child.on('exit', (code, signal) => this.#onExit(code, signal));
      child.on('error', error => this.#onChildError(error));

      child.once('spawn', () => {
        this.#emitEvent('transport.started', {
          command: this.#command,
          pid: child.pid ?? null
        });
        resolveStart(this);
      });

      child.once('error', error => {
        if (!child.pid) {
          rejectStart(new ComToolTransportError(
            'transport_spawn_failed',
            `Failed to start COM Tool stdio transport: ${error.message}`,
            {
              classification: 'transport_not_started',
              submitted: false,
              cause: error
            }
          ));
        }
      });
    });

    try {
      return await this.#startPromise;
    } catch (error) {
      this.#startPromise = null;
      throw error;
    }
  }

  async execute(operation, input = null, {
    id = createRequestId('op'),
    target,
    policy,
    preconditions,
    postconditions,
    signal,
    responseTimeoutMs
  } = {}) {
    if (typeof operation !== 'string' || operation.length === 0) {
      throw new TypeError('operation must be a non-empty string.');
    }

    const request = {
      protocolVersion: PROTOCOL_VERSION,
      id,
      operation,
      input
    };

    if (target !== undefined) request.target = target;
    if (policy !== undefined) request.policy = policy;
    if (preconditions !== undefined) request.preconditions = preconditions;
    if (postconditions !== undefined) request.postconditions = postconditions;

    return this.request(request, {
      signal,
      responseTimeoutMs
    });
  }

  async request(request, {
    signal,
    responseTimeoutMs
  } = {}) {
    if (
      request == null ||
      typeof request !== 'object' ||
      request.protocolVersion !== PROTOCOL_VERSION ||
      typeof request.id !== 'string' ||
      request.id.length === 0 ||
      request.id.length > 128 ||
      typeof request.operation !== 'string' ||
      request.operation.length === 0
    ) {
      throw new TypeError(
        'request must be a valid protocol-v1 OperationRequest with id and operation.'
      );
    }

    validateAbortSignal(signal);
    const normalizedResponseTimeoutMs =
      validateResponseTimeoutMs(responseTimeoutMs);

    if (signal?.aborted) {
      throw new ComToolTransportError(
        'request_aborted',
        'COM Tool request was aborted before submission.',
        {
          classification: 'transport_not_sent',
          submitted: false,
          requestId: request.id,
          operation: request.operation
        }
      );
    }

    await this.start();

    if (this.#faulted || !this.#child?.stdin?.writable) {
      throw new ComToolTransportError(
        'transport_unavailable',
        'COM Tool stdio transport is not writable.',
        { classification: 'transport_not_sent', submitted: false }
      );
    }

    if (this.#pending.has(request.id)) {
      throw new Error(
        `A request with id '${request.id}' is already in flight on this client.`
      );
    }

    let frame;
    try {
      frame = JSON.stringify(request) + '\n';
    } catch (error) {
      throw new TypeError(
        `OperationRequest is not JSON-serializable: ${error.message}`
      );
    }

    const frameBytes = Buffer.byteLength(frame);
    if (frameBytes > 1024 * 1024) {
      throw new ComToolTransportError(
        'request_frame_too_large',
        `OperationRequest frame is ${frameBytes} bytes; V2 stdio accepts at most 1048576 bytes.`,
        { classification: 'transport_not_sent', submitted: false }
      );
    }

    return new Promise((resolveRequest, rejectRequest) => {
      let responseTimer = null;
      let abortHandler = null;

      const cleanup = () => {
        if (responseTimer !== null) {
          clearTimeout(responseTimer);
          responseTimer = null;
        }
        if (abortHandler !== null && signal) {
          signal.removeEventListener('abort', abortHandler);
          abortHandler = null;
        }
      };

      const pending = {
        requestId: request.id,
        operation: request.operation,
        submitted: false,
        resolve(value) {
          cleanup();
          resolveRequest(value);
        },
        reject(error) {
          cleanup();
          rejectRequest(error);
        }
      };
      this.#pending.set(request.id, pending);

      const stopWaiting = (kind, message) => {
        if (this.#pending.get(request.id) !== pending) return;
        this.#pending.delete(request.id);
        pending.reject(new ComToolTransportError(
          kind,
          message,
          {
            classification: pending.submitted
              ? 'transport_ambiguous'
              : 'transport_not_sent',
            submitted: pending.submitted,
            requestId: request.id,
            operation: request.operation
          }
        ));
      };

      if (signal) {
        abortHandler = () => stopWaiting(
          'request_aborted',
          pending.submitted
            ? 'COM Tool caller stopped waiting after request submission; execution outcome is unknown.'
            : 'COM Tool request was aborted before submission.'
        );
        signal.addEventListener('abort', abortHandler, { once: true });
        if (signal.aborted) {
          abortHandler();
          return;
        }
      }

      if (normalizedResponseTimeoutMs !== null) {
        responseTimer = setTimeout(() => stopWaiting(
          'response_wait_timeout',
          pending.submitted
            ? `COM Tool response was not observed within ${normalizedResponseTimeoutMs} ms after submission; execution outcome is unknown.`
            : `COM Tool request could not be submitted within ${normalizedResponseTimeoutMs} ms.`
        ), normalizedResponseTimeoutMs);
        responseTimer.unref?.();
      }

      try {
        this.#child.stdin.write(frame, 'utf8', error => {
          if (!error) return;
          this.#rejectPending(
            request.id,
            new ComToolTransportError(
              'transport_write_failed',
              `COM Tool transport write failed after submission: ${error.message}`,
              {
                classification: 'transport_ambiguous',
                submitted: true,
                requestId: request.id,
                operation: request.operation,
                cause: error
              }
            )
          );
        });
        // From this point onward bytes may have reached the proxy/runtime.
        // Any lost response is therefore conservatively ambiguous.
        pending.submitted = true;
        this.#emitEvent('request.submitted', {
          id: request.id,
          operation: request.operation,
          targetId: request.target?.id ?? null
        });
      } catch (error) {
        this.#pending.delete(request.id);
        rejectRequest(new ComToolTransportError(
          'transport_write_failed',
          `COM Tool request could not be submitted: ${error.message}`,
          {
            classification: 'transport_not_sent',
            submitted: false,
            requestId: request.id,
            operation: request.operation,
            cause: error
          }
        ));
      }
    });
  }

  async close({ killAfterMs = 2000 } = {}) {
    if (this.#closing) return;
    this.#closing = true;

    const child = this.#child;
    if (!child) return;

    if (child.stdin.writable) {
      child.stdin.end();
    }

    if (child.exitCode !== null || child.signalCode !== null) {
      return;
    }

    await new Promise(resolveClose => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        resolveClose();
      };
      const timer = setTimeout(() => {
        try {
          child.kill();
        } catch {
          // Best effort process teardown only.
        }
        finish();
      }, killAfterMs);
      timer.unref?.();
      child.once('exit', finish);
    });
  }

  #onStdout(chunk) {
    this.#stdoutBuffer += this.#stdoutDecoder.write(chunk);

    while (true) {
      const newline = this.#stdoutBuffer.indexOf('\n');
      if (newline < 0) break;
      const line = this.#stdoutBuffer.slice(0, newline).replace(/\r$/, '');
      this.#stdoutBuffer = this.#stdoutBuffer.slice(newline + 1);
      if (line.length === 0) continue;

      if (Buffer.byteLength(line) > this.#maxResponseBytes) {
        this.#fault(
          new ComToolTransportError(
            'transport_response_too_large',
            `COM Tool stdio response exceeded ${this.#maxResponseBytes} bytes.`,
            {
              classification: 'transport_ambiguous',
              submitted: true
            }
          )
        );
        return;
      }

      this.#handleResponseLine(line);
      if (this.#faulted) return;
    }

    if (Buffer.byteLength(this.#stdoutBuffer) > this.#maxResponseBytes) {
      this.#fault(
        new ComToolTransportError(
          'transport_response_too_large',
          `COM Tool stdio response exceeded ${this.#maxResponseBytes} bytes.`,
          {
            classification: 'transport_ambiguous',
            submitted: true
          }
        )
      );
    }
  }

  #handleResponseLine(line) {
    let response;
    try {
      response = JSON.parse(line);
    } catch (error) {
      this.#fault(new ComToolTransportError(
        'transport_invalid_json',
        'COM Tool stdio returned invalid JSON.',
        {
          classification: 'transport_ambiguous',
          submitted: true,
          cause: error,
          details: { line: line.slice(0, 4096) }
        }
      ));
      return;
    }

    const id = response?.id;
    if (typeof id !== 'string') {
      this.#fault(new ComToolTransportError(
        'transport_response_missing_id',
        'COM Tool stdio returned a response without a request id.',
        {
          classification: 'transport_ambiguous',
          submitted: true,
          details: { response }
        }
      ));
      return;
    }

    const pending = this.#pending.get(id);
    if (!pending) {
      this.#emitEvent('response.orphaned', {
        id,
        operation: response?.operation ?? null
      });
      return;
    }

    this.#pending.delete(id);
    this.#emitEvent('response.received', {
      id,
      operation: response?.operation ?? pending.operation,
      ok: response?.ok === true,
      status: response?.status ?? null,
      execution: response?.error?.execution ?? null
    });
    pending.resolve(response);
  }

  #onStderr(chunk) {
    this.#stderrBuffer += this.#stderrDecoder.write(chunk);
    while (true) {
      const newline = this.#stderrBuffer.indexOf('\n');
      if (newline < 0) break;
      const line = this.#stderrBuffer.slice(0, newline).replace(/\r$/, '');
      this.#stderrBuffer = this.#stderrBuffer.slice(newline + 1);
      if (line.length > 0) {
        this.#emitEvent('transport.stderr', { message: line });
      }
    }
  }

  #onChildError(error) {
    this.#fault(new ComToolTransportError(
      'transport_process_error',
      `COM Tool stdio process failed: ${error.message}`,
      {
        classification: 'transport_ambiguous',
        submitted: true,
        cause: error
      }
    ));
  }

  #onExit(code, signal) {
    if (this.#stderrBuffer.length > 0) {
      this.#emitEvent('transport.stderr', {
        message: this.#stderrBuffer
      });
      this.#stderrBuffer = '';
    }

    this.#emitEvent('transport.exited', {
      code,
      signal,
      expected: this.#closing
    });

    const error = new ComToolTransportError(
      'transport_exited',
      `COM Tool stdio transport exited${code === null ? '' : ` with code ${code}`}${signal ? ` (signal ${signal})` : ''}.`,
      {
        classification: 'transport_ambiguous',
        submitted: true,
        details: { code, signal, expected: this.#closing }
      }
    );
    this.#fault(error, { terminateChild: false });
  }

  #fault(error, { terminateChild = true } = {}) {
    if (this.#faulted) return;
    this.#faulted = true;

    if (terminateChild && this.#child) {
      try {
        this.#child.kill();
      } catch {
        // Best effort only.
      }
    }

    for (const [id, pending] of this.#pending) {
      this.#pending.delete(id);
      pending.reject(new ComToolTransportError(
        error.kind,
        error.message,
        {
          classification: pending.submitted
            ? 'transport_ambiguous'
            : 'transport_not_sent',
          submitted: pending.submitted,
          requestId: pending.requestId,
          operation: pending.operation,
          cause: error.cause,
          details: error.details
        }
      ));
    }
  }

  #rejectPending(id, error) {
    const pending = this.#pending.get(id);
    if (!pending) return;
    this.#pending.delete(id);
    pending.reject(error);
  }

  #emitEvent(event, fields = {}) {
    this.emit('event', {
      timestamp: new Date().toISOString(),
      event,
      ...fields
    });
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
  if (value === undefined || value === null) return null;
  if (
    !Number.isSafeInteger(value) ||
    value < 1 ||
    value > 2_147_483_647
  ) {
    throw new RangeError(
      'responseTimeoutMs must be an integer between 1 and 2147483647 ms.'
    );
  }
  return value;
}
