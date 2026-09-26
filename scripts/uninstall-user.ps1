param(
    [string]$InstallRoot = (
        Join-Path $env:LOCALAPPDATA "Programs\ComToolV2"
    ),

    [string]$Version,

    [switch]$All,

    [switch]$RemoveState,

    [string]$StateRemovalToken
)

$ErrorActionPreference = "Stop"
$VersionPattern = '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$versionsRoot = Join-Path $installRootFull "versions"
$currentPath = Join-Path $installRootFull "current.json"

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

    if (-not $candidate.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed payload path '$Relative' escapes version root '$rootFull'."
    }

    return $candidate
}

function Assert-InstalledRelease {
    param(
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $manifestPath = Join-Path $VersionRoot "release-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "release manifest is missing"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.format -ne "comtool-v2-release" -or
        $manifest.schemaVersion -ne 1 -or
        [string]$manifest.version -ne $ExpectedVersion) {
        throw "release manifest is inconsistent"
    }
    if ($null -eq $manifest.files -or $manifest.files.Count -eq 0) {
        throw "release manifest has no payload inventory"
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $relative = ([string]$entry.path).Replace("\", "/")
        if ([string]::IsNullOrWhiteSpace($relative) -or
            -not $expected.Add($relative)) {
            throw "release manifest has an invalid or duplicate payload path"
        }

        $path = Resolve-ContainedPath -Root $VersionRoot -Relative $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "payload file '$relative' is missing"
        }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne [long]$entry.size) {
            throw "payload file '$relative' has an unexpected size"
        }
        if ((Get-Sha256 $path) -ne
            ([string]$entry.sha256).ToLowerInvariant()) {
            throw "payload file '$relative' failed SHA-256 verification"
        }
    }

    $controlFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    [void]$controlFiles.Add("release-manifest.json")
    [void]$controlFiles.Add("SHA256SUMS.txt")
    foreach ($file in Get-ChildItem -LiteralPath $VersionRoot -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath(
            $VersionRoot,
            $file.FullName).Replace("\", "/")
        if (-not $expected.Contains($relative) -and
            -not $controlFiles.Contains($relative)) {
            throw "installed version contains unlisted file '$relative'"
        }
    }

    return $manifest
}

function Resolve-VersionRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($Version) -or
        $Version -notmatch $VersionPattern) {
        throw "Release version '$Version' is invalid."
    }

    $rootFull = [IO.Path]::GetFullPath($Root)
    $candidate = [IO.Path]::GetFullPath(
        (Join-Path $rootFull $Version))
    $prefix = $rootFull.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar

    if (-not $candidate.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release version '$Version' escapes the versions root."
    }

    return $candidate
}

if ($All -and -not [string]::IsNullOrWhiteSpace($Version)) {
    throw "-All and -Version are mutually exclusive."
}

$current = $null
if (Test-Path -LiteralPath $currentPath) {
    $current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json
    if ($current.format -ne "comtool-v2-current" -or
        $current.schemaVersion -ne 1) {
        throw "Unsupported or corrupt COM Tool V2 current-version pointer."
    }
}

if (-not $All -and [string]::IsNullOrWhiteSpace($Version)) {
    if ($null -eq $current -or [string]::IsNullOrWhiteSpace($current.version)) {
        throw "No current COM Tool V2 installation was found; specify -Version or -All."
    }
    $Version = [string]$current.version
}

$currentRemoved =
    $All -or
    ($null -ne $current -and [string]$current.version -eq $Version)

$fallback = $null
if (-not $All -and $currentRemoved -and
    (Test-Path -LiteralPath $versionsRoot)) {
    $candidates = @(
        Get-ChildItem -LiteralPath $versionsRoot -Directory |
            Where-Object {
                $_.Name -match $VersionPattern -and
                $_.Name -ne $Version
            } |
            Sort-Object LastWriteTimeUtc -Descending
    )

    $rejections = @()
    foreach ($candidate in $candidates) {
        try {
            [void](Assert-InstalledRelease -VersionRoot $candidate.FullName -ExpectedVersion $candidate.Name)
            $fallback = $candidate
            break
        }
        catch {
            $rejections += "$($candidate.Name): $($_.Exception.Message)"
        }
    }

    if ($candidates.Count -gt 0 -and $null -eq $fallback) {
        throw (
            "Refusing to remove current version '$Version' because no remaining version passes integrity verification. " +
            ($rejections -join "; ")
        )
    }
}

$removed = @()
if ($All) {
    if (Test-Path -LiteralPath $versionsRoot) {
        foreach ($dir in Get-ChildItem -LiteralPath $versionsRoot -Directory) {
            $removed += $dir.Name
        }
        Remove-Item -LiteralPath $versionsRoot -Recurse -Force
    }
}
else {
    $versionRoot = Resolve-VersionRoot -Root $versionsRoot -Version $Version
    if (Test-Path -LiteralPath $versionRoot) {
        Remove-Item -LiteralPath $versionRoot -Recurse -Force
        $removed += $Version
    }
}

$remaining = @()
if (Test-Path -LiteralPath $versionsRoot) {
    $remaining = @(
        Get-ChildItem -LiteralPath $versionsRoot -Directory |
            Where-Object { $_.Name -match $VersionPattern } |
            Sort-Object LastWriteTimeUtc -Descending
    )
}

if ($currentRemoved) {
    if ($All -or $null -eq $fallback) {
        if (Test-Path -LiteralPath $currentPath) {
            Remove-Item -LiteralPath $currentPath -Force
        }
    }
    else {
        [void](Assert-InstalledRelease -VersionRoot $fallback.FullName -ExpectedVersion $fallback.Name)

        $pointer = [ordered]@{
            format = "comtool-v2-current"
            schemaVersion = 1
            version = $fallback.Name
            path = $fallback.FullName
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
    }
}

$stateRemoved = $false
if ($RemoveState) {
    if ($StateRemovalToken -ne "DELETE_COMTOOL_V2_STATE") {
        throw "Deleting durable runtime state requires -StateRemovalToken DELETE_COMTOOL_V2_STATE."
    }

    $stateRoot = Join-Path $env:LOCALAPPDATA "ComToolV2"
    if (Test-Path -LiteralPath $stateRoot) {
        Remove-Item -LiteralPath $stateRoot -Recurse -Force
    }
    $stateRemoved = $true
}

if (Test-Path -LiteralPath $installRootFull) {
    $children = @(Get-ChildItem -LiteralPath $installRootFull -Force)
    if ($children.Count -eq 0) {
        Remove-Item -LiteralPath $installRootFull -Force
    }
}

[pscustomobject]@{
    ok = $true
    removedVersions = $removed
    remainingVersions = @($remaining | ForEach-Object Name)
    stateRemoved = $stateRemoved
    statePreserved = -not $stateRemoved
} | ConvertTo-Json -Compress