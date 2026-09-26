# Gate 0A transient host-state evidence

Before the successful Gate 0A run, both implementations independently observed the same transient Illustrator failure:

```text
HRESULT: 0x80010105
Symbol: RPC_E_SERVERFAULT
Message: The server threw an exception.
```

Observed in:

1. C#/.NET Gate 0A after successful STA attach, `Version`, and `Documents.Count` reads, at `DoJavaScript`.
2. The existing Python/pywin32 COM Tool immediately afterward, also at `eval` / `DoJavaScript`.

The legacy `status` command remained healthy during the condition. Later, without restarting Illustrator, `DoJavaScript` recovered and Gate 0A passed.

## Architectural interpretation

This is evidence for a host/script-engine state distinction, not a C# marshaling regression.

V2 must therefore:

- keep host heartbeat/read health distinct from script-engine health,
- classify `RPC_E_SERVERFAULT` carefully,
- not blindly replay mutations after it,
- expose route-specific capability/health evidence,
- allow reconciliation/retry policy to depend on operation class.
