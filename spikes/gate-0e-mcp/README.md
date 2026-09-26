# Gate 0E — MCP is a transport adapter

Uses the official `ModelContextProtocol` C# SDK **2.2.0**.

The executable has two modes:

- self-test client (default),
- stdio MCP server (`--server`).

The MCP tools contain no Adobe/COM logic. They translate arguments into the same small `OperationKernel` used directly by the self-test. The gate compares direct and MCP-mediated results byte-for-byte for the same normalized operation.

This validates the architectural rule:

```text
MCP call
  -> adapter
  -> operation request
  -> operation kernel
  -> operation result
```

No host-specific behavior belongs in MCP.
