# Gate 0D — Canonical JSON/stdio protocol

This spike tests the process-facing protocol independently of COM, named pipes, or MCP.

Protocol shape:

- UTF-8 newline-delimited JSON,
- exactly one response per non-empty input line,
- protocol version on every request/response,
- caller-supplied request ID,
- strict root request fields,
- stable structured errors,
- tagged result values to preserve JSON types,
- malformed input fails only that line,
- stdout contains machine JSON only.

NDJSON is deliberately limited to the external process-facing stdio transport. Internal supervisor/worker named pipes use length-prefixed frames instead.
