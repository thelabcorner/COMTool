# Agent integrations

This directory is part of the COMTool release artifact.

- `SKILL.md` is the canonical packaged agent skill.
- `AGENT_CONTRACT.md` is the detailed machine-facing contract.
- `openfork/comtool.md` is a portable OpenFork slash-command definition that
  can be installed into an OpenFork command directory if desired.

Agents do not need to guess where these files are installed. Run:

```powershell
ComTool.Cli.exe agent-guide
```

Use `ComTool.Cli.exe agent-guide --content` to emit the canonical skill and
contract directly as JSON, which is useful when the caller cannot read the
installation filesystem.
