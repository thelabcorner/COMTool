# Workflow concurrency model

**Status:** Architecture constraint  
**Scope:** COM Tool V2 durable workflows and future DAG/batch work

COM Tool V2 must not confuse workflow concurrency with Adobe host concurrency.
For one strong Illustrator target generation, host execution is a single serialized
resource. COM calls, ExtendScript execution, action/menu dispatch, debugger
commands, plug-in control, and typed mutations all converge on that target's
worker/execution lane. A workflow feature must never advertise or implement
parallel Illustrator execution merely because its orchestration graph contains
independent nodes.

## Canonical rule

For a given strong target ID:

```text
maximum host-execution concurrency = 1
```

The supervisor may perform independent runtime work concurrently, and different
strong targets may execute independently, but two workflow steps that need the
same Illustrator target are serialized before host dispatch.

This is both a host limitation and a correctness property. Illustrator is
effectively single-lane for scripting/automation. Attempting to create multiple
logical workflow workers does not create multiple safe Illustrator interpreters;
it creates contention, ordering ambiguity, and misleading cancellation/recovery
semantics.

## What may run concurrently

Concurrency is useful when it does not enter the same target's Adobe execution
lane. Examples include:

- validating or hashing script/file inputs;
- static ES3 preflight;
- knowledge/catalog/example lookup;
- artifact reads and preparation;
- dependency resolution and workflow planning;
- result formatting and persistence;
- work assigned to genuinely distinct strong target generations;
- work assigned to a different Adobe host/process.

A future DAG scheduler may therefore maintain a ready set and execute independent
runtime-only nodes concurrently while using a per-target semaphore/resource key
of one for host-bound nodes.

## DAG semantics

A future DAG is a dependency and resource-scheduling model, not a promise of
same-target parallel speedup.

Each node should declare or derive resources from the authoritative operation
catalog. At minimum:

```text
runtime-only operation             -> no Illustrator target resource
host op on target T                -> resource illustrator:T, capacity 1
host op on independent target U    -> resource illustrator:U, capacity 1
```

The scheduler may launch ready nodes only when all dependency and resource
constraints are satisfied. Existing request IDs, leases, mutation journaling,
conditions, and ambiguity rules remain authoritative because every non-compiled
step still re-enters `RuntimeSupervisor.ExecuteAsync`.

The first useful DAG implementation should optimize orchestration around the
serialized lane rather than add a generic `parallel: true` option.

## Compiled host batches

There is one potentially valuable optimization for multiple compatible logical
steps on the same Illustrator target: compile them into **one ExtendScript host
dispatch**.

This is batching, not parallel execution and not a database transaction.

A compiled batch can reduce bridge round trips and can share local script state,
but the runtime must remain truthful about its weaker observation boundary:

- the supervisor sees one host dispatch, not N independently committed dispatches;
- if the script/worker/host is lost after any mutation may have occurred, the
  entire compiled batch is ambiguous;
- an ambiguous compiled batch is never automatically replayed;
- the outer batch request ID is the durable mutation/idempotency identity;
- logical substep IDs are evidence/result labels, not independent replay tokens;
- the worker watchdog applies to the whole dispatch;
- host-call cancellation cannot be represented as arbitrary per-substep abort;
- per-step results should record `not_started`, `completed`, or
  `failed_before_next_step` only when the in-host batch code can truthfully
  establish that state;
- the runtime must never claim atomic rollback. Illustrator may retain mutations
  made before a later substep fails.

### Compilation eligibility

Compilation should be explicit and fail closed. A batch is eligible only when
all steps are deliberately supported by one batch compiler and their semantics
remain truthful inside one ExtendScript invocation.

Good candidates are tightly related ExtendScript computations/mutations that do
not require supervisor observation between steps.

Compilation must be refused for steps involving, for example:

- debugger session lifecycle or debugger commands;
- native plug-in control or arbitrary external side effects;
- host termination/recovery;
- operations that require a separate mutation-ledger checkpoint between steps;
- document lifecycle transitions whose result must be re-observed before the
  next runtime decision;
- steps whose pre/postconditions require an external host read between mutations;
- operations targeting different strong target generations.

Do not concatenate arbitrary user scripts and call that a compiled workflow.
The compiler needs a versioned, bounded contract and explicit result/error
envelope.

## Recommendation

Keep the current sequential workflow engine as the production baseline.

If workflow DAGs are implemented later, first add dependency scheduling and
resource declarations while retaining capacity one for every strong Illustrator
target. This can improve runtime-side overlap and cross-target orchestration
without weakening host safety.

Treat compiled single-dispatch batching as a separate measured optimization
after concrete round-trip profiles show it is worthwhile. It should be
operation-specific and capability-gated rather than a generic escape hatch.

The invariant to test is:

> Increasing workflow scheduler parallelism must never increase simultaneous
> host execution against one strong Illustrator target above one.
