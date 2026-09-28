import {
  ComToolTransportError,
  createRequestId,
  unwrapProtocolValue
} from './client.mjs';
import {
  classifyOperationResult,
  serializeTransportError,
  serializeUnknownError
} from './runner.mjs';

/**
 * Explicit break-glass recovery for a run that retained its lease because the
 * script outcome is ambiguous. The runtime operation itself validates the same
 * lease plus the exact PID/process-start generation before taking action.
 */
export async function terminateHostGeneration(
  runner,
  run,
  {
    waitTimeoutMs = 10_000,
    releaseLease = true
  } = {}
) {
  if (
    runner?.client == null ||
    run?.target?.target == null ||
    run?.target?.identity == null ||
    typeof run?.lease?.id !== 'string' ||
    run.lease.retained !== true
  ) {
    throw new TypeError(
      'terminateHostGeneration requires a runner and a run result with a retained target lease.'
    );
  }

  if (
    !Number.isSafeInteger(waitTimeoutMs) ||
    waitTimeoutMs < 100 ||
    waitTimeoutMs > 60_000
  ) {
    throw new RangeError(
      'waitTimeoutMs must be an integer between 100 and 60000 ms.'
    );
  }

  const target = run.target;
  const requestId = createRequestId('host-recovery');
  let result = null;
  let transportError = null;

  try {
    result = await runner.client.execute(
      'core.target.host.terminate',
      {
        expectedProcessId: target.identity.processId,
        expectedProcessStartedAt:
          target.identity.processStartedAt,
        waitTimeoutMs
      },
      {
        id: requestId,
        target: target.target,
        policy: { leaseId: run.lease.id }
      }
    );
  } catch (error) {
    if (!(error instanceof ComToolTransportError)) throw error;
    transportError = serializeTransportError(error);
  }

  const outcome = transportError
    ? {
        classification: transportError.submitted
          ? 'transport_ambiguous'
          : 'transport_not_sent',
        exitCode: transportError.submitted ? 2 : 3,
        ambiguous: transportError.submitted
      }
    : classifyOperationResult(result);

  let leaseReleased = false;
  let cleanupError = null;
  const value =
    result?.ok === true
      ? unwrapProtocolValue(result.result)
      : undefined;

  if (
    releaseLease &&
    result?.ok === true &&
    value?.exitObserved === true &&
    run.lease.owned
  ) {
    try {
      await runner.releaseLease(target, run.lease.id);
      leaseReleased = true;
    } catch (error) {
      cleanupError = serializeUnknownError(error);
    }
  }

  return {
    kind: 'comtool-v2-host-recovery',
    requestId,
    classification: cleanupError
      ? 'host_recovered_lease_cleanup_unknown'
      : outcome.classification,
    exitCode: cleanupError ? 3 : outcome.exitCode,
    ambiguous: outcome.ambiguous,
    target,
    operation: result,
    value,
    transportError,
    leaseReleased,
    cleanupError
  };
}
