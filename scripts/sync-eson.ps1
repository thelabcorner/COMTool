param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"

# Compute SHA-256 without relying on cmdlet auto-loading. MSBuild's Exec
# host can launch Windows PowerShell without a usable module path, which
# makes Get-FileHash unresolvable; the BCL path is always available.
function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.IO.File]::ReadAllBytes($Path))
    }
    finally {
        $sha.Dispose()
    }

    $builder = [System.Text.StringBuilder]::new($bytes.Length * 2)
    foreach ($byte in $bytes) {
        [void]$builder.Append($byte.ToString("x2"))
    }

    return $builder.ToString()
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Resolve-Path (Join-Path $projectRoot "..")
$source = Join-Path $scriptsRoot "eson\dist\vendor-eson-runtime.js"
$destination = Join-Path $projectRoot "src\ComTool.Hosts.Illustrator\Assets\eson-runtime.js"
$runtimeSource = Join-Path $projectRoot "src\ComTool.Hosts.Illustrator\IllustratorEsonRuntime.cs"

if (-not (Test-Path $source)) {
    throw "Canonical ESON runtime not found at $source. Build the sibling eson project first."
}

$sourceHash = Get-Sha256Hex -Path $source

if ($Check) {
    if (-not (Test-Path $destination)) {
        throw "Embedded ESON runtime is missing at $destination. Run scripts/sync-eson.ps1."
    }

    $destinationHash = Get-Sha256Hex -Path $destination
    if ($destinationHash -ne $sourceHash) {
        throw "Embedded ESON runtime drifted from the canonical build. expected=$sourceHash actual=$destinationHash. Run scripts/sync-eson.ps1."
    }

    $sourceText = Get-Content -Raw $runtimeSource
    $match = [regex]::Match(
        $sourceText,
        'ExpectedSha256\s*=\s*\r?\n\s*"([0-9a-f]{64})"')
    if (-not $match.Success) {
        throw "Could not locate IllustratorEsonRuntime.ExpectedSha256."
    }

    $pinnedHash = $match.Groups[1].Value
    if ($pinnedHash -ne $sourceHash) {
        throw "IllustratorEsonRuntime.ExpectedSha256 is stale. expected=$sourceHash pinned=$pinnedHash. Run scripts/sync-eson.ps1."
    }

    [pscustomobject]@{
        ok = $true
        source = $source
        embedded = $destination
        sha256 = $sourceHash
    } | ConvertTo-Json -Compress
    exit 0
}

Copy-Item -Path $source -Destination $destination -Force

$sourceText = Get-Content -Raw $runtimeSource
$pinPattern = '(ExpectedSha256\s*=\s*\r?\n\s*")[0-9a-f]{64}(")'
$pinRegex = [regex]::new($pinPattern)
$pinMatch = $pinRegex.Match($sourceText)
if (-not $pinMatch.Success) {
    throw "Could not locate IllustratorEsonRuntime.ExpectedSha256 before updating it."
}

$updated = [regex]::Replace(
    $sourceText,
    $pinPattern,
    [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return $match.Groups[1].Value + $sourceHash + $match.Groups[2].Value
    },
    1)

if ($updated -ne $sourceText) {
    [System.IO.File]::WriteAllText(
        $runtimeSource,
        $updated,
        [System.Text.UTF8Encoding]::new($false))
}

[pscustomobject]@{
    ok = $true
    source = $source
    embedded = $destination
    sha256 = $sourceHash
} | ConvertTo-Json -Compress
