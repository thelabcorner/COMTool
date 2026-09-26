# Gate 0A — .NET ↔ Illustrator COM Viability

This is a disposable architectural spike, not production V2 code.

## Safety

Default execution is read-only. A reversible mutation probe runs **only** with `--mutation-probe` and creates its own disposable Illustrator document. It does not mutate a document that was open before the spike.

## Run

```powershell
..\..\scripts\dotnet.ps1 run --project . -- --read-only
..\..\scripts\dotnet.ps1 run --project . -- --mutation-probe
```

Output is one JSON object so the spike can become a regression fixture.

## Gate intent

Prove:

- explicit STA thread,
- ROT/active-object attach,
- dynamic `IDispatch`,
- version/document reads,
- `DoJavaScript`,
- disposable direct-DOM mutation,
- explicit no-save close,
- HRESULT capture,
- deterministic COM-reference cleanup.

A successful run does not by itself close Gate 0A; restart/reconnect behavior must also be exercised.
