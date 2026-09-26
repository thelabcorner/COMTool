$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$deploy = Join-Path $root ".artifacts\dev-runtime\$stamp"
$runtimeDir = Join-Path $deploy "runtime"
$workerDir = Join-Path $deploy "worker"

$runtimeSource = Join-Path $root "src\ComTool.RuntimeHost\bin\Release\net10.0-windows"
$workerSource = Join-Path $root "src\ComTool.Worker\bin\Release\net10.0-windows"

if (-not (Test-Path (Join-Path $runtimeSource "ComTool.RuntimeHost.exe"))) {
    throw "Build ComTool.RuntimeHost Release before staging."
}

if (-not (Test-Path (Join-Path $workerSource "ComTool.Worker.exe"))) {
    throw "Build ComTool.Worker Release before staging."
}

New-Item -ItemType Directory -Force -Path $runtimeDir, $workerDir | Out-Null
Copy-Item -Path (Join-Path $runtimeSource "*") -Destination $runtimeDir -Recurse -Force
Copy-Item -Path (Join-Path $workerSource "*") -Destination $workerDir -Recurse -Force

[pscustomobject]@{
    deployment = $deploy
    runtime = Join-Path $runtimeDir "ComTool.RuntimeHost.exe"
    worker = Join-Path $workerDir "ComTool.Worker.exe"
} | ConvertTo-Json -Compress
