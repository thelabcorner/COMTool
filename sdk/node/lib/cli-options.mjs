export function parseRunnerArgs(mode, args) {
  const values = new Map();
  const flags = new Set();
  const positionals = [];
  const flagOptions = new Set([
    '--verbose',
    '--terminate-host-on-ambiguous',
    '--discard-result',
    '--no-self-heal'
  ]);
  const valueOptions = new Set([
    '--path',
    '--expr',
    '--code',
    '--args-json',
    '--effects',
    '--timeout-ms',
    '--retry-budget-ms',
    '--response-timeout-ms',
    '--recovery-grace-ms',
    '--lease-ttl-ms',
    '--lease',
    '--request-id',
    '--target',
    '--host',
    '--pipe',
    '--cli',
    '--artifact-dir',
    '--expect-json',
    '--preconditions-json',
    '--postconditions-json',
    '--recovery-wait-ms'
  ]);

  if (!['run', 'test', 'eval', 'test-eval'].includes(mode)) {
    throw new Error(`Unsupported runner mode '${mode}'.`);
  }
  if (!Array.isArray(args)) {
    throw new TypeError('args must be an array.');
  }

  for (let i = 0; i < args.length; i++) {
    const token = args[i];
    if (typeof token !== 'string') {
      throw new TypeError('runner arguments must be strings.');
    }
    if (!token.startsWith('--')) {
      positionals.push(token);
      continue;
    }

    if (flagOptions.has(token)) {
      if (flags.has(token)) {
        throw new Error(`Duplicate option '${token}'.`);
      }
      flags.add(token);
      continue;
    }

    if (!valueOptions.has(token)) {
      throw new Error(`Unknown option '${token}'.`);
    }
    if (values.has(token)) {
      throw new Error(`Duplicate option '${token}'.`);
    }
    if (i + 1 >= args.length) {
      throw new Error(`${token} requires a value.`);
    }
    values.set(token, args[++i]);
  }

  const isEval = mode === 'eval' || mode === 'test-eval';
  const isTest = mode === 'test' || mode === 'test-eval';

  let path;
  let evalKind;
  let source;

  if (isEval) {
    if (positionals.length > 0 || values.has('--path')) {
      throw new Error(
        `${mode} accepts inline source only; use exactly one of --expr or --code.`
      );
    }
    const expression = values.get('--expr');
    const code = values.get('--code');
    if ((expression === undefined) === (code === undefined)) {
      throw new Error(
        `${mode} requires exactly one of --expr <source> or --code <source>.`
      );
    }
    evalKind = expression !== undefined ? 'expression' : 'code';
    source = expression ?? code;
  } else {
    if (values.has('--expr') || values.has('--code')) {
      throw new Error(
        `${mode} executes a .jsx/.jsxbin file; use eval/test-eval for inline source.`
      );
    }
    if (positionals.length > 1) {
      throw new Error(`${mode} accepts at most one positional script path.`);
    }
    if (values.has('--path') && positionals.length === 1) {
      throw new Error(
        'Specify the script path either positionally or with --path, not both.'
      );
    }
    path = values.get('--path') ?? positionals[0];
    if (!path) {
      throw new Error('A .jsx/.jsxbin path is required.');
    }
  }

  if (!isTest && values.has('--expect-json')) {
    throw new Error('--expect-json is valid only for test or test-eval.');
  }

  const scriptArgs = parseJsonArray(
    values.get('--args-json') ?? '[]',
    '--args-json'
  );
  const preconditions = values.has('--preconditions-json')
    ? parseJsonArray(
        values.get('--preconditions-json'),
        '--preconditions-json'
      )
    : undefined;
  const postconditions = values.has('--postconditions-json')
    ? parseJsonArray(
        values.get('--postconditions-json'),
        '--postconditions-json'
      )
    : undefined;

  const expectedRaw = values.get('--expect-json');
  const hasExpected = expectedRaw !== undefined;
  const retryBudgetMs = values.has('--retry-budget-ms')
    ? parseInteger(
        values.get('--retry-budget-ms'),
        undefined,
        '--retry-budget-ms'
      )
    : undefined;

  if (
    retryBudgetMs !== undefined &&
    (retryBudgetMs < 0 || retryBudgetMs > 3_600_000)
  ) {
    throw new RangeError(
      'retryBudgetMs must be an integer between 0 and 3600000 ms.'
    );
  }

  return {
    path,
    evalKind,
    source,
    scriptArgs,
    effects: values.get('--effects') ?? 'unknown',
    resultMode: flags.has('--discard-result') ? 'discard' : 'capture',
    watchdogMs: parseInteger(
      values.get('--timeout-ms'),
      60_000,
      '--timeout-ms'
    ),
    retryBudgetMs,
    responseTimeoutMs: values.has('--response-timeout-ms')
      ? parseInteger(
          values.get('--response-timeout-ms'),
          undefined,
          '--response-timeout-ms'
        )
      : undefined,
    recoveryGraceMs: parseInteger(
      values.get('--recovery-grace-ms'),
      120_000,
      '--recovery-grace-ms'
    ),
    leaseTtlMs: values.has('--lease-ttl-ms')
      ? parseInteger(
          values.get('--lease-ttl-ms'),
          undefined,
          '--lease-ttl-ms'
        )
      : undefined,
    leaseId: values.get('--lease'),
    requestId: values.get('--request-id'),
    targetId: values.get('--target'),
    host: values.get('--host') ?? 'illustrator',
    pipeName: values.get('--pipe'),
    cliPath: values.get('--cli'),
    artifactDir: values.get('--artifact-dir'),
    preconditions,
    postconditions,
    verbose: flags.has('--verbose'),
    selfHeal: !flags.has('--no-self-heal'),
    terminateHostOnAmbiguous:
      flags.has('--terminate-host-on-ambiguous'),
    recoveryWaitMs: parseInteger(
      values.get('--recovery-wait-ms'),
      10_000,
      '--recovery-wait-ms'
    ),
    hasExpected,
    expected: hasExpected
      ? parseJson(expectedRaw, '--expect-json')
      : undefined
  };
}

function parseJsonArray(raw, name) {
  const value = parseJson(raw, name);
  if (!Array.isArray(value)) {
    throw new Error(`${name} must decode to a JSON array.`);
  }
  return value;
}

function parseJson(raw, name) {
  try {
    return JSON.parse(raw);
  } catch (error) {
    throw new Error(`${name} is not valid JSON: ${error.message}`);
  }
}

function parseInteger(raw, fallback, name) {
  if (raw === undefined) return fallback;
  const value = Number(raw);
  if (!Number.isSafeInteger(value)) {
    throw new Error(`${name} must be an integer.`);
  }
  return value;
}
