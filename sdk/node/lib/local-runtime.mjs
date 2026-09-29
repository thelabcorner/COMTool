import { randomUUID } from 'node:crypto';
import { existsSync, statSync } from 'node:fs';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  ComToolClient,
  createRequestId,
  unwrapProtocolValue
} from './client.mjs';
import { ComToolRunner } from './runner.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const SDK_ROOT = resolve(HERE, '..');
const PRODUCT_ROOT = resolve(SDK_ROOT, '..', '..');

function firstNonEmpty(...values) {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) {
      return value.trim();
    }
  }
  return null;
}

function isFile(file) {
  if (!file || !existsSync(file)) return false;
  try {
    return statSync(file).isFile();
  } catch {
    return false;
  }
}

function completeLayout(layout) {
  return !!layout &&
    isFile(layout.cliPath) &&
    isFile(layout.runtimeHostPath) &&
    isFile(layout.workerPath);
}

function layoutFromRoot(rootPath, kind) {
  if (!rootPath) return null;
  const root = resolve(rootPath);
  return {
    kind,
    root,
    cliPath: join(root, 'ComTool.Cli.exe'),
    runtimeHostPath: join(root, 'ComTool.RuntimeHost.exe'),
    workerPath: join(root, 'ComTool.Worker.exe')
  };
}

function workspaceLayout(rootPath = PRODUCT_ROOT) {
  const root = resolve(rootPath);
  return {
    kind: 'workspace',
    root,
    cliPath: join(
      root,
      'src',
      'ComTool.Cli',
      'bin',
      'Release',
      'net10.0-windows',
      'ComTool.Cli.exe'
    ),
    runtimeHostPath: join(
      root,
      'src',
      'ComTool.RuntimeHost',
      'bin',
      'Release',
      'net10.0-windows',
      'ComTool.RuntimeHost.exe'
    ),
    workerPath: join(
      root,
      'src',
      'ComTool.Worker',
      'bin',
      'Release',
      'net10.0-windows',
      'ComTool.Worker.exe'
    )
  };
}

function installedLayout(env) {
  if (!env.LOCALAPPDATA) return null;
  return layoutFromRoot(
    join(env.LOCALAPPDATA, 'Programs', 'ComToolV2', 'current'),
    'installed'
  );
}

export function resolveLocalComToolLayout({
  env = process.env,
  productRoot = PRODUCT_ROOT
} = {}) {
  const explicitCli = firstNonEmpty(
    env.COMTOOL_CLI_PATH,
    env.COMTOOL_V2_CLI_PATH,
    env.COMTOOL_V2_CLI
  );
  const explicitRuntime = firstNonEmpty(
    env.COMTOOL_RUNTIME_HOST_PATH,
    env.COMTOOL_V2_RUNTIME_HOST
  );
  const explicitWorker = firstNonEmpty(
    env.COMTOOL_WORKER_PATH,
    env.COMTOOL_V2_WORKER_PATH,
    env.COMTOOL_V2_WORKER
  );

  if (explicitCli || explicitRuntime || explicitWorker) {
    if (!(explicitCli && explicitRuntime && explicitWorker)) {
      throw new Error(
        'Explicit COMTool layout requires COMTOOL_CLI_PATH, ' +
        'COMTOOL_RUNTIME_HOST_PATH, and COMTOOL_WORKER_PATH together.'
      );
    }

    const explicit = {
      kind: 'explicit',
      root: null,
      cliPath: resolve(explicitCli),
      runtimeHostPath: resolve(explicitRuntime),
      workerPath: resolve(explicitWorker)
    };

    if (!completeLayout(explicit)) {
      throw new Error(
        'Explicit COMTool layout is incomplete: ' + JSON.stringify(explicit)
      );
    }
    return explicit;
  }

  // A release package places the SDK under sdk/node and the executables at the
  // package root. In a source checkout, the same relative root resolves first
  // but only the workspace layout below is complete.
  const packaged = layoutFromRoot(productRoot, 'package');
  if (completeLayout(packaged)) return packaged;

  const workspace = workspaceLayout(productRoot);
  if (completeLayout(workspace)) return workspace;

  const installed = installedLayout(env);
  if (completeLayout(installed)) return installed;

  throw new Error(
    'COMTool runtime binaries are unavailable. Build COMTool in Release, ' +
    'install it for the current user, or set COMTOOL_CLI_PATH, ' +
    'COMTOOL_RUNTIME_HOST_PATH, and COMTOOL_WORKER_PATH.'
  );
}

function normalizePositiveInteger(value, fallback, label) {
  const resolved = value === undefined ? fallback : value;
  if (!Number.isSafeInteger(resolved) || resolved <= 0) {
    throw new TypeError(label + ' must be a positive integer.');
  }
  return resolved;
}

function isHostAbsentDiscovery(error) {
  const message = String(
    error?.result?.error?.message ||
    error?.message ||
    ''
  );
  return message.includes('MK_E_UNAVAILABLE') ||
    message.includes('0x800401E3') ||
    message.includes('Operation unavailable');
}

function isTransientDiscoveryFailure(error) {
  const runtimeError = error?.result?.error ?? null;
  const message = String(runtimeError?.message || error?.message || '');
  return runtimeError?.retryable === true ||
    runtimeError?.kind === 'host_busy' ||
    message.includes('0x800706BA') ||
    message.includes('RPC server is unavailable') ||
    message.includes('0x800706BE') ||
    message.includes('0x80010001') ||
    message.includes('0x8001010A');
}

function hasCapability(entry, name) {
  return Array.isArray(entry?.capabilities) &&
    entry.capabilities.some(capability =>
      capability?.name === name && capability?.supported === true);
}

function isExternalLeaseContention(error) {
  const runtimeError = error?.result?.error ?? null;
  return runtimeError?.kind === 'target_leased_external' &&
    runtimeError?.retryable === true;
}

function isRetryableWorkerHandshakeFailure(result) {
  if (result?.ok === true) return false;
  const message = String(result?.error?.message || '');
  return result?.error?.retryable === true ||
    message.includes('worker_start_failed') ||
    (message.includes('Expected target') &&
      message.includes('was not discovered')) ||
    message.includes('Could not establish Illustrator process identity') ||
    message.toLowerCase().includes('operation was canceled');
}

function isGenerationGoneHandshakeFailure(result) {
  if (result?.ok === true) return false;
  const message = String(result?.error?.message || '');
  return (
    message.includes('Expected target') &&
    message.includes('was not discovered')
  ) || message.includes('Could not establish Illustrator process identity');
}

async function sleep(ms) {
  await new Promise(resolveDelay => setTimeout(resolveDelay, ms));
}

/**
 * Owns one local COMTool control plane for CI/test orchestration.
 *
 * Default mode is deliberately hermetic: Node spawns RuntimeHost directly in
 * --stdio mode, so ESTC/CI talks to the SAME RuntimeSupervisor without creating
 * a second COM implementation or an extra CLI->pipe hop. RuntimeHost lifetime
 * is then bound to the stdio transport: close() ends stdin cleanly, and a hard
 * parent-process exit closes the inherited pipe so the owned RuntimeHost cannot
 * remain as a forgotten local-CI daemon.
 *
 * Supplying pipeName switches to attach mode: the SDK talks through ComTool.Cli
 * to an already-running RuntimeHost pipe and does not own that runtime.
 */
export class ComToolLocalRuntime {
  #layout;
  #env;
  #host;
  #pipeName;
  #stateDir;
  #ownsStateDir = false;
  #runner = null;
  #started = false;
  #closed = false;
  #spawnImpl;
  #selfHeal;

  constructor({
    layout,
    env = process.env,
    host = 'illustrator',
    pipeName,
    stateDir,
    spawnImpl,
    selfHeal = true
  } = {}) {
    if (process.platform !== 'win32') {
      throw new Error('COMTool local Adobe runtime is supported only on Windows.');
    }
    if (typeof selfHeal !== 'boolean') {
      throw new TypeError('selfHeal must be a boolean.');
    }

    this.#layout = layout ?? resolveLocalComToolLayout({ env });
    if (!completeLayout(this.#layout)) {
      throw new Error(
        'COMTool layout is incomplete: ' + JSON.stringify(this.#layout)
      );
    }

    this.#env = env;
    this.#host = String(host || 'illustrator');
    this.#pipeName = firstNonEmpty(pipeName);
    this.#stateDir = stateDir ? resolve(stateDir) : null;
    this.#spawnImpl = spawnImpl;
    this.#selfHeal = selfHeal;
  }

  static async start(options = {}) {
    const runtime = new ComToolLocalRuntime(options);
    await runtime.start(options);
    return runtime;
  }

  get layout() {
    return { ...this.#layout };
  }

  get pipeName() {
    return this.#pipeName;
  }

  get stateDir() {
    return this.#stateDir;
  }

  get runner() {
    if (!this.#runner) {
      throw new Error('COMTool local runtime has not been started.');
    }
    return this.#runner;
  }

  get started() {
    return this.#started && !this.#closed;
  }

  async start({
    startupTimeoutMs = 20_000
  } = {}) {
    if (this.#closed) {
      throw new Error('COMTool local runtime is already closed.');
    }
    if (this.#started) return this;

    const timeoutMs = normalizePositiveInteger(
      startupTimeoutMs,
      20_000,
      'startupTimeoutMs'
    );

    let client;
    if (this.#pipeName) {
      // Attach mode: an existing RuntimeHost owns its own state/lifetime.
      client = new ComToolClient({
        cliPath: this.#layout.cliPath,
        pipeName: this.#pipeName,
        env: this.#env,
        selfHeal: this.#selfHeal,
        ...(this.#spawnImpl ? { spawnImpl: this.#spawnImpl } : {})
      });
    } else {
      if (!this.#stateDir) {
        this.#stateDir = await mkdtemp(
          join(tmpdir(), 'comtool-local-state-')
        );
        this.#ownsStateDir = true;
      }

      // RuntimeHost owns a process-global mutex derived from its endpoint
      // name even when stdio is the active front end. Give every owned local
      // stdio runtime a unique endpoint identity so independent CI sessions
      // cannot collide on the default mutex.
      const runtimeEndpoint =
        'comtool-local-' + randomUUID().replace(/-/g, '');

      const args = [
        '--stdio',
        '--pipe', runtimeEndpoint,
        '--worker', this.#layout.workerPath,
        '--state-dir', this.#stateDir,
        '--host', this.#host
      ];

      client = new ComToolClient({
        cwd: dirname(this.#layout.runtimeHostPath),
        env: this.#env,
        selfHeal: this.#selfHeal,
        transportCommand: {
          command: this.#layout.runtimeHostPath,
          args
        },
        ...(this.#spawnImpl ? { spawnImpl: this.#spawnImpl } : {})
      });
    }

    const runner = new ComToolRunner({ client });
    try {
      const health = await runner.client.execute(
        'core.runtime.health',
        null,
        {
          id: createRequestId('local-runtime-health'),
          responseTimeoutMs: timeoutMs
        }
      );

      if (health?.ok !== true) {
        throw new Error(
          'COMTool local RuntimeHost health check failed: ' +
          (health?.error?.message || health?.status || 'unknown failure')
        );
      }
    } catch (error) {
      await runner.close().catch(() => {});
      if (this.#ownsStateDir && this.#stateDir) {
        await rm(this.#stateDir, { recursive: true, force: true }).catch(() => {});
        this.#stateDir = null;
        this.#ownsStateDir = false;
      }
      throw error;
    }

    this.#runner = runner;
    this.#started = true;
    return this;
  }

  async selectTarget({
    host = this.#host,
    targetId,
    launch = false,
    progId = host === 'illustrator'
      ? 'Illustrator.Application'
      : undefined,
    expectedHostVersion,
    launchTimeoutMs = 90_000,
    discoveryStabilizeMs = 10_000,
    requiredCapabilities = ['script.eval']
  } = {}) {
    if (!this.started) {
      throw new Error('COMTool local runtime must be started first.');
    }

    const choose = async () => {
      const deadline = Date.now() + Math.max(0, discoveryStabilizeMs);
      let targets;
      while (true) {
        try {
          targets = await this.runner.listTargets({ refresh: true });
          break;
        } catch (error) {
          // Illustrator's ROT can briefly retain a generation while its COM
          // endpoint is still starting or tearing down. Retry only failures
          // explicitly typed/reported as transient; never retry ambiguity or
          // arbitrary discovery errors.
          if (isHostAbsentDiscovery(error)) return null;
          if (
            !isTransientDiscoveryFailure(error) ||
            error?.result?.error?.execution === 'ambiguous' ||
            Date.now() >= deadline
          ) {
            throw error;
          }
          await sleep(Math.min(250, Math.max(1, deadline - Date.now())));
        }
      }

      const candidates = targets.filter(entry =>
        entry?.running === true &&
        entry?.target?.host === host &&
        (!targetId || entry?.target?.id === targetId) &&
        requiredCapabilities.every(name => hasCapability(entry, name))
      );

      if (candidates.length > 1 && !targetId) {
        throw new Error(
          'Multiple running ' + host +
          ' targets were discovered; pass targetId explicitly.'
        );
      }
      return candidates[0] ?? null;
    };

    let selected = await choose();
    if (selected) return selected;

    if (!launch) {
      const error = new Error(
        targetId
          ? 'Requested COMTool target is not running: ' + targetId
          : 'No running ' + host + ' target is available.'
      );
      error.unavailable = true;
      throw error;
    }
    if (!progId) {
      throw new Error('Launching host ' + host + ' requires an explicit progId.');
    }

    const launched = await this.runner.launchTarget({
      host,
      progId,
      expectedHostVersion: expectedHostVersion ?? null,
      launchTimeoutMs,
      existingInstance: 'return_preexisting_without_ownership',
      arguments: []
    });

    if (launched?.ok !== true) {
      const error = new Error(
        'COMTool host launch did not complete successfully: ' +
        (launched?.error?.message || launched?.status || 'unknown failure')
      );
      error.result = launched;
      error.ambiguous =
        launched?.status === 'reconciliation_required' ||
        launched?.error?.execution === 'ambiguous';
      throw error;
    }

    const launchValue = unwrapProtocolValue(launched.result);
    if (launchValue?.target?.id) {
      targetId = launchValue.target.id;
    }

    const deadline = Date.now() + launchTimeoutMs;
    while (Date.now() < deadline) {
      selected = await choose();
      if (selected) return selected;
      await sleep(250);
    }

    throw new Error(
      'COMTool launched ' + host +
      ' but no strong target generation became discoverable within ' +
      launchTimeoutMs + ' ms.'
    );
  }

  async openSession({
    host = this.#host,
    targetId,
    launch = false,
    progId,
    expectedHostVersion,
    launchTimeoutMs,
    requiredCapabilities = ['script.eval'],
    discoveryStabilizeMs = 10_000,
    targetRebindMs = 30_000,
    lease = true,
    leaseTtlMs = 600_000,
    leaseWaitMs = 60_000,
    workerStabilizeMs = 10_000
  } = {}) {
    const explicitTarget = typeof targetId === 'string' && targetId.length > 0;
    const rebindDeadline = Date.now() + Math.max(0, targetRebindMs);

    while (true) {
      const target = await this.selectTarget({
        host,
        targetId,
        launch,
        progId,
        expectedHostVersion,
        launchTimeoutMs,
        discoveryStabilizeMs,
        requiredCapabilities
      });
      const session = await this.runner.openSession({ target });

      try {
        if (lease) {
          const deadline = Date.now() + Math.max(0, leaseWaitMs);
          while (true) {
            try {
              await session.acquireLease({ ttlMs: leaseTtlMs });
              break;
            } catch (error) {
              if (!isExternalLeaseContention(error) || Date.now() >= deadline) {
                throw error;
              }
              await sleep(Math.min(500, Math.max(1, deadline - Date.now())));
            }
          }
        }

        const stabilizeDeadline =
          Date.now() + Math.max(0, workerStabilizeMs);
        let lastResult = null;

        while (true) {
          lastResult = await session.status({
            responseTimeoutMs: 30_000
          });
          if (lastResult?.ok === true) return session;

          if (
            !isRetryableWorkerHandshakeFailure(lastResult) ||
            Date.now() >= stabilizeDeadline
          ) {
            break;
          }

          await sleep(
            Math.min(250, Math.max(1, stabilizeDeadline - Date.now()))
          );
        }

        const canRebind =
          this.#selfHeal &&
          !explicitTarget &&
          launch &&
          isGenerationGoneHandshakeFailure(lastResult) &&
          Date.now() < rebindDeadline;

        if (!canRebind) {
          const error = new Error(
            'COMTool target worker could not be stabilized: ' +
            (lastResult?.error?.message ||
              lastResult?.status ||
              'unknown failure')
          );
          error.result = lastResult;
          throw error;
        }

        // No user operation has been dispatched: only target discovery, lease
        // acquisition, and read-only worker handshake occurred. It is therefore
        // safe to release this stale generation and discover the replacement.
        if (session.leaseId) {
          await session.releaseLease().catch(() => {});
        }
        await sleep(250);
      } catch (error) {
        if (session.leaseId) {
          await session.releaseLease().catch(() => {});
        }
        throw error;
      }
    }
  }

  async close() {
    if (this.#closed) return;
    this.#closed = true;

    if (this.#runner) {
      const runner = this.#runner;
      this.#runner = null;

      if (this.#pipeName) {
        // Attach mode owns only its CLI transport, not the shared RuntimeHost.
        await runner.close().catch(() => {});
      } else {
        // An owned stdio RuntimeHost may spend several seconds shutting down
        // its target worker cleanly (broker shutdown + bounded owned-process
        // termination). Let that authoritative path finish before any fallback
        // force termination so local CI returns only after the owned control
        // plane has actually gone away.
        await runner.client.close({
          killAfterMs: 10_000,
          forceKillWaitMs: 5_000
        }).catch(() => {});
      }
    }

    if (this.#ownsStateDir && this.#stateDir) {
      await rm(this.#stateDir, { recursive: true, force: true }).catch(() => {});
      this.#stateDir = null;
      this.#ownsStateDir = false;
    }

    this.#started = false;
  }
}

export async function withLocalComTool(options, callback) {
  if (typeof callback !== 'function') {
    throw new TypeError('callback must be a function.');
  }

  const runtime = await ComToolLocalRuntime.start(options);
  try {
    return await callback(runtime);
  } finally {
    await runtime.close();
  }
}
