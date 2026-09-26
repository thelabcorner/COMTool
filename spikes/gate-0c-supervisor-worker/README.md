# Gate 0C — Supervisor / worker / framed named pipe

This spike proves runtime topology only. It does **not** talk to Illustrator.

It intentionally uses the same binary in two roles:

- supervisor,
- isolated host worker.

The supervisor spawns and owns the worker process, authenticates it with a per-launch token, and communicates over a length-prefixed local named pipe.

## Evidence required

- worker handshake + strong worker identity,
- stable logical target ID across worker restart,
- bounded frame size,
- 1,000 concurrent-caller requests serialized through one worker lane,
- clean worker restart,
- simulated in-flight mutating hang,
- watchdog terminates only the owned child,
- target moves to `reconciliation_required`,
- fresh worker + explicit reconcile returns target to `known`.

This is a topology/state-semantics spike, not the final protocol implementation.
