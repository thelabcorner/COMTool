param(
    [string]$Source = $PSScriptRoot,

    [string]$InstallRoot = (
        Join-Path $env:LOCALAPPDATA "Programs\ComToolV2"
    ),

    [switch]$AllowDirtyPackage
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$VersionPattern = '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Relative
    )

    $rootFull = [IO.Path]::GetFullPath($Root)
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $Relative))
    $prefix = $rootFull.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar

    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Relative path '$Relative' escapes root '$rootFull'."
    }

    return $candidate
}

function Assert-SafeVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    if ([string]::IsNullOrWhiteSpace($Version) -or
        $Version -notmatch $VersionPattern) {
        throw "Release version '$Version' is invalid."
    }
}

function Assert-ReleasePayload {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Manifest
    )

    if ($null -eq $Manifest.files -or $Manifest.files.Count -eq 0) {
        throw "Release manifest does not contain a payload inventory."
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $Manifest.files) {
        $relative = ([string]$entry.path).Replace("\", "/")
        if ([string]::IsNullOrWhiteSpace($relative)) {
            throw "Release manifest contains an empty payload path."
        }
        if (-not $expected.Add($relative)) {
            throw "Release manifest contains duplicate payload path '$relative'."
        }

        $path = Resolve-ContainedPath -Root $Root -Relative $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release file '$relative' is missing."
        }

        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne [long]$entry.size) {
            throw "Release file '$relative' has an unexpected size."
        }

        $hash = Get-Sha256 $path
        if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "Release file '$relative' failed SHA-256 verification."
        }
    }

    $controlFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    [void]$controlFiles.Add("release-manifest.json")
    [void]$controlFiles.Add("SHA256SUMS.txt")

    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath(
            $Root,
            $file.FullName).Replace("\", "/")
        if (-not $expected.Contains($relative) -and
            -not $controlFiles.Contains($relative)) {
            throw "Release package contains unlisted file '$relative'."
        }
    }
}

$sourceRoot = [IO.Path]::GetFullPath($Source)
$manifestPath = Join-Path $sourceRoot "release-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "release-manifest.json was not found in '$sourceRoot'."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.format -ne "comtool-v2-release" -or $manifest.schemaVersion -ne 1) {
    throw "Unsupported COM Tool V2 release manifest."
}
if ([string]::IsNullOrWhiteSpace($manifest.version)) {
    throw "Release manifest does not contain a version."
}
Assert-SafeVersion -Version ([string]$manifest.version)
if ($manifest.source.dirty -eq $true -and -not $AllowDirtyPackage) {
    throw "This package was produced from a dirty source tree. Refusing a production install; use -AllowDirtyPackage only for validation."
}

Assert-ReleasePayload -Root $sourceRoot -Manifest $manifest

if ($manifest.signing.authenticode -eq $true) {
    if ($null -eq $manifest.signing.files -or $manifest.signing.files.Count -eq 0) {
        throw "Signed release manifest does not identify its first-party signed files."
    }
    foreach ($signedRelativePath in $manifest.signing.files) {
        $path = Resolve-ContainedPath -Root $sourceRoot -Relative ([string]$signedRelativePath)
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Release file '$signedRelativePath' does not have a valid Authenticode signature."
        }

        if (-not [string]::IsNullOrWhiteSpace($manifest.signing.certificateSha1) -and
            $signature.SignerCertificate.Thumbprint.ToLowerInvariant() -ne
                ([string]$manifest.signing.certificateSha1).ToLowerInvariant()) {
            throw "Release file '$signedRelativePath' was signed by an unexpected certificate."
        }
    }
}

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$versionsRoot = Join-Path $installRootFull "versions"
$versionRoot = Resolve-ContainedPath -Root $versionsRoot -Relative ([string]$manifest.version)
$currentPath = Join-Path $installRootFull "current.json"
New-Item -ItemType Directory -Force -Path $versionsRoot | Out-Null

if (Test-Path -LiteralPath $versionRoot) {
    $installedManifest = Join-Path $versionRoot "release-manifest.json"
    if (-not (Test-Path -LiteralPath $installedManifest) -or
        (Get-Sha256 $installedManifest) -ne (Get-Sha256 $manifestPath)) {
        throw "Version '$($manifest.version)' is already installed with different content. Version directories are immutable."
    }
    Assert-ReleasePayload -Root $versionRoot -Manifest $manifest
    $reused = $true
}
else {
    $stage = Join-Path $installRootFull (".install-" + [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        Get-ChildItem -LiteralPath $sourceRoot -Force |
            Copy-Item -Destination $stage -Recurse
        Move-Item -LiteralPath $stage -Destination $versionRoot
        Assert-ReleasePayload -Root $versionRoot -Manifest $manifest
    }
    catch {
        if (Test-Path -LiteralPath $versionRoot) {
            Remove-Item -LiteralPath $versionRoot -Recurse -Force
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
    $reused = $false
}

$pointer = [ordered]@{
    format = "comtool-v2-current"
    schemaVersion = 1
    version = [string]$manifest.version
    path = $versionRoot
    selectedAt = [DateTimeOffset]::UtcNow.ToString("O")
}
$pointerTemp = $currentPath + "." + [Guid]::NewGuid().ToString("N") + ".tmp"
try {
    $pointer | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $pointerTemp -Encoding utf8NoBOM
    Move-Item -LiteralPath $pointerTemp -Destination $currentPath -Force
}
finally {
    if (Test-Path -LiteralPath $pointerTemp) {
        Remove-Item -LiteralPath $pointerTemp -Force
    }
}

[pscustomobject]@{
    ok = $true
    version = [string]$manifest.version
    reusedExistingVersion = $reused
    installRoot = $installRootFull
    versionRoot = $versionRoot
    stateRoot = Join-Path $env:LOCALAPPDATA "ComToolV2"
    cli = Join-Path $versionRoot ([string]$manifest.entrypoints.cli)
    runtimeHost = Join-Path $versionRoot ([string]$manifest.entrypoints.runtimeHost)
    worker = Join-Path $versionRoot ([string]$manifest.entrypoints.worker)
    mcp = Join-Path $versionRoot ([string]$manifest.entrypoints.mcp)
} | ConvertTo-Json -Compress