#!/usr/bin/env node
import {
  ComToolClient,
  ComToolRunner,
  terminateHostGeneration
} from '../index.mjs';
import { parseRunnerArgs } from '../lib/cli-options.mjs';

const argv = process.argv.slice(2);
const command = argv.shift();

if (!command || command === '--help' || command === '-h' || command === 'help') {
  printHelp();
  process.exitCode = 0;
} else if (!['run', 'test', 'eval', 'test-eval'].includes(command)) {
  console.error(JSON.stringify({
    ok: false,
    error: {
      kind: 'unknown_command',
      message: `Unknown command '${command}'. Expected run, test, eval, or test-eval.`
    }
  }));
  process.exitCode = 1;
} else {
  await main(command, argv);
}

async function main(mode, args) {
  let runner;
  try {
    const options = parseRunnerArgs(mode, args);
    const onEvent = options.verbose
      ? event => console.error(JSON.stringify(event))
      : undefined;

    const client = new ComToolClient({
      cliPath: options.cliPath,
      pipeName: options.pipeName,
      selfHeal: options.selfHeal
    });
    runner = new ComToolRunner({ client, onEvent });

    const common = {
      args: options.scriptArgs,
      effects: options.effects,
      resultMode: options.resultMode,
      watchdogMs: options.watchdogMs,
      retryBudgetMs: options.retryBudgetMs,
      recoveryGraceMs: options.recoveryGraceMs,
      leaseTtlMs: options.leaseTtlMs,
      leaseId: options.leaseId,
      requestId: options.requestId,
      targetId: options.targetId,
      host: options.host,
      preconditions: options.preconditions,
      postconditions: options.postconditions,
      artifactDir: options.artifactDir,
      responseTimeoutMs: options.responseTimeoutMs
    };

    const isEval = mode === 'eval' || mode === 'test-eval';
    const isTest = mode === 'test' || mode === 'test-eval';

    if (isEval) {
      common.kind = options.evalKind;
      common.source = options.source;
    } else {
      common.path = options.path;
    }

    if (isTest && options.hasExpected) {
      common.expected = options.expected;
    }

    let result;
    if (mode === 'test') {
      result = await runner.testFile(common);
    } else if (mode === 'test-eval') {
      result = await runner.testEval(common);
    } else if (mode === 'eval') {
      result = await runner.runEval(common);
    } else {
      result = await runner.runFile(common);
    }

    if (
      options.terminateHostOnAmbiguous &&
      result.ambiguous &&
      result.lease?.retained === true
    ) {
      result.hostRecovery = await terminateHostGeneration(
        runner,
        result,
        { waitTimeoutMs: options.recoveryWaitMs }
      );
    }

    console.log(JSON.stringify(result));
    process.exitCode = result.exitCode;
  } catch (error) {
    console.error(JSON.stringify({
      ok: false,
      classification: 'runner_process_error',
      exitCode: 3,
      error: {
        name: error?.name ?? 'Error',
        message: error?.message ?? String(error)
      }
    }));
    process.exitCode = 3;
  } finally {
    await runner?.close().catch(() => {});
  }
}

function printHelp() {
  console.log(JSON.stringify({
    ok: true,
    product: 'COM Tool V2 Node runner',
    agentHint:
      "AI/automation agents: run 'ComTool.Cli.exe agent-guide --content' first. It emits COMTool's packaged skill, safety contract, and OpenFork command guidance.",
    commands: [
      'run <file.jsx> [--timeout-ms <ms>] [--response-timeout-ms <ms>] [--effects <class>] [--args-json <array>] [--target <id>] [--artifact-dir <dir>]',
      'test <file.jsx> [--timeout-ms <ms>] [--response-timeout-ms <ms>] [--expect-json <json>] [--args-json <array>] [--discard-result] [--no-self-heal] [--target <id>] [--artifact-dir <dir>]',
      'eval (--expr <source> | --code <source>) [--timeout-ms <ms>] [--response-timeout-ms <ms>] [--effects <class>] [--args-json <array>] [--discard-result] [--no-self-heal] [--target <id>] [--artifact-dir <dir>]',
      'test-eval (--expr <source> | --code <source>) [--timeout-ms <ms>] [--response-timeout-ms <ms>] [--expect-json <json>] [--args-json <array>] [--discard-result] [--no-self-heal] [--target <id>] [--artifact-dir <dir>]'
    ],
    transport:
      'Persistent NDJSON proxy through ComTool.Cli.exe stdio -> existing RuntimeHost pipe.',
    timeoutSemantics:
      '--timeout-ms controls the runtime worker watchdog; --response-timeout-ms only stops the Node caller from waiting and is ambiguous after submission.',
    resultSemantics:
      'Results are captured by default with explicit result-presence metadata; --discard-result executes without transporting the script return value.',
    selfHealing:
      'Transport self-healing is enabled by default for future/not-yet-submitted requests; --no-self-heal disables it. Submitted requests are never replayed automatically.',
    recovery:
      '--terminate-host-on-ambiguous is explicit opt-in break-glass recovery; it is never automatic.'
  }));
}
