# V2 Protocol Schema Validator

Validates the draft protocol fixtures against the checked-in JSON Schema 2020-12 contracts.

The validator intentionally:

- loads every schema into a local process registry before evaluation,
- requires `format` validation (important for target/process/event timestamps),
- evaluates both expected-valid and expected-invalid fixtures,
- fails the process if a negative fixture unexpectedly passes or a positive fixture fails,
- performs no network schema fetching.

Run from `comtool-v2/`:

```powershell
.\scripts\dotnet.ps1 run --project tools/schema-validator -c Release
```
