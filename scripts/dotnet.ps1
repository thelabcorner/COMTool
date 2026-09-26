$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    throw "Local .NET SDK not found at $dotnet. Bootstrap V2 before running this wrapper."
}
& $dotnet @args
exit $LASTEXITCODE
