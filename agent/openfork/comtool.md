---
description: Bootstrap COMTool guidance and safely execute an Adobe automation task
agent: build
---

COMTool ships its own agent guidance. Do not rely on a machine-local skill or a
source-checkout path.

First execute:

```powershell
ComTool.Cli.exe agent-guide --content
```

Follow the emitted canonical COMTool skill/contract for this task:

$ARGUMENTS

Unless the request is purely explanatory, establish live state with COMTool
health, the dynamic Adobe probe, the operation catalog, exact target discovery,
and target capabilities before host work.

Use the least-powerful advertised operation that completes the task. Obey
runtime lease and mutation policy, preserve stable request identity, and never
replay work whose dispatch may have occurred.

If the host is `probe_only`, report the supported boundary instead of
pretending another host has Illustrator parity.

Verify requested postconditions and clean up owned leases/sessions/private
runtimes before reporting completion.
