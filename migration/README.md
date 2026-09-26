# Legacy → V2 Migration Specification

The machine-readable source of truth is `legacy-feature-matrix.json`.

The matrix does **not** promise CLI compatibility. It records the legacy intent, what is worth preserving, what is unsafe or misleading, the proposed V2 operation/facet, mutation/idempotency/recovery semantics, migration phase, and test oracle.

## Classification meanings

- **preserve** — keep the proven semantic capability, possibly behind a new transport/API.
- **redesign** — preserve the user/agent intent but intentionally change semantics or architecture.
- **deprecate** — provide a migration path if useful, but do not make the old abstraction foundational.
- **drop** — unsafe/accidental legacy behavior must not appear in V2.

## High-level consolidation

```text
legacy batch + queue + task + --stay + named session
                    ↓
     operation + workflow + job + real session
```

```text
legacy global mutex
        ↓
per-target scheduler + target lease + reconciliation state
```

```text
legacy COM/JSX/debugger/plugin mechanisms
                  ↓
capability-driven host adapter facets
```

The current Python tool remains the external behavioral oracle; V2 does not import its implementation modules.

## Safety deltas that are intentionally incompatible

The following legacy semantics are explicitly rejected:

- Boolean `Document.Close` policy.
- Blind resume/replay of ambiguous mutating queue entries.
- Continue-after-failure as the default for dependent workflows.
- Killing a process from a stale persisted PID alone.
- Treating atomic file replacement as multi-writer concurrency control.
- Treating abandoned mutex recovery as proof of document consistency.
- Treating a local timeout as proof that a host operation never executed.
- Content-based reinterpretation of script result strings as JSON.
- Pretending debugger interactions are stateless.
- Implicit host ownership during launch/quit.

These are product improvements, not compatibility regressions.
