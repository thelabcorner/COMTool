---
name: comtool
description: >-
  Use COMTool to probe, inspect, automate, debug, and safely recover Adobe
  desktop applications through its guarded RuntimeHost, live operation catalog,
  exact target capabilities, leases, COM, ExtendScript, workflows, and plug-in
  surfaces.
---

# COMTool

COMTool is one stateful automation authority. Do not bypass its
RuntimeHost -> RuntimeSupervisor -> Worker execution path with ad-hoc COM code.

## Bootstrap yourself

First ask COMTool where its packaged agent guidance lives:

```powershell
ComTool.Cli.exe agent-guide
```

Use `--content` when you need the packaged guidance emitted directly rather
than reading the returned files.

For a live Adobe task, establish truth before acting:

```powershell
ComTool.Cli.exe health
ComTool.Cli.exe probe
ComTool.Cli.exe targets
```

Narrow the probe when a host is named:

```powershell
ComTool.Cli.exe probe --host illustrator
ComTool.Cli.exe probe --host photoshop
```

## Read probe tiers literally

- `full`: continue to exact target discovery and capabilities.
- `adapter_available_not_configured`: the build supports the host, but the
  active runtime is not configured for it.
- `probe_only`: environment inspection only. Do not invent automation parity.

A detected Adobe application is not automatically controllable by COMTool.

## Discover instead of memorizing

Use the live catalog and target:

```text
core.operations.list
core.operation.describe
core.operation.examples
core.targets.list
core.target.capabilities
```

Use `knowledge.*` operations for Illustrator COM signatures, enums, and
navigation rather than guessing.

Prefer typed host operations, then bounded generic reads, then guarded generic
mutations, then arbitrary ExtendScript.

## Mutations

Obey runtime lease requirements. Use stable request IDs.

Never replay an operation after dispatch may have occurred. Preserve ambiguity,
reconcile the exact target generation, and verify postconditions.

## ExtendScript

Use `script.validate` first for unfamiliar source. Use expression mode for one
value-producing expression and statement mode for statement/function bodies.
Capture results only when needed. Arbitrary script cannot declare itself
read-only.

## More detail

Read `AGENT_CONTRACT.md` next to this file for the complete safety,
reconciliation, discovery, and completion contract.
