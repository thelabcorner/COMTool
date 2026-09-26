param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$RuntimeIdentifier = "win-x64",

    [switch]$AllowDirty,

    [string]$SignToolPath,

    [string]$SigningCertificateSha1,

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$VersionPattern = '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'
$RuntimeIdentifierPattern = '^[A-Za-z0-9][A-Za-z0-9.-]*$'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot "dotnet.ps1"

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch $VersionPattern) {
    throw "Release version '$Version' is invalid."
}
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier) -or
    $RuntimeIdentifier -notmatch $RuntimeIdentifierPattern) {
    throw "Runtime identifier '$RuntimeIdentifier' is invalid."
}

$releaseRoot = Join-Path $root ".artifacts\release"
$artifactName = "$Version-$RuntimeIdentifier"
$artifactRoot = Join-Path $releaseRoot $artifactName
$stagingRoot = Join-Path $releaseRoot (
    ".building-$artifactName-" + [Guid]::NewGuid().ToString("N")
)
$publishRoot = Join-Path $stagingRoot "publish"
$packageRoot = Join-Path $stagingRoot "package"
$zipName = "ComToolV2-$Version-$RuntimeIdentifier.zip"
$zipPath = Join-Path $stagingRoot $zipName

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-NuGetVulnerabilityCount {
    param($Node)

    if ($null -eq $Node) {
        return 0
    }

    $count = 0
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) {
            if ($property.Name -eq "vulnerabilities") {
                $count += @($property.Value).Count
                continue
            }

            $count += Get-NuGetVulnerabilityCount $property.Value
        }
        return $count
    }

    if ($Node -is [System.Collections.IEnumerable] -and
        $Node -isnot [string]) {
        foreach ($item in $Node) {
            $count += Get-NuGetVulnerabilityCount $item
        }
    }

    return $count
}

function Merge-PublishDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        New-Item -ItemType Directory -Force -Path $parent | Out-Null

        if (Test-Path -LiteralPath $target) {
            $sourceHash = Get-Sha256 $file.FullName
            $targetHash = Get-Sha256 $target
            if ($sourceHash -ne $targetHash) {
                throw "Publish merge collision for '$relative' with different content."
            }
            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "Project-local dotnet wrapper was not found at '$dotnet'."
}

$gitRoot = (& git -C $root rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) {
    throw "COM Tool V2 must be released from a Git worktree."
}

$sourceCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve the source Git commit."
}

$dirtyLines = @(& git -C $root status --porcelain=v1 --untracked-files=all -- .)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect Git status for COM Tool V2."
}
$sourceDirty = $dirtyLines.Count -gt 0
if ($sourceDirty -and -not $AllowDirty) {
    throw "COM Tool V2 has uncommitted or untracked changes. Commit/stage a release baseline first, or use -AllowDirty only for non-production validation."
}

if (Test-Path -LiteralPath $artifactRoot) {
    throw "Release artifact '$artifactName' already exists. Release versions are immutable; choose a new version."
}
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot, $packageRoot | Out-Null

try {
    Invoke-Checked {
        & $dotnet restore (Join-Path $root "ComTool.V2.slnx") --locked-mode
    } "Locked dependency restore"

    $auditLines = @(& $dotnet list (Join-Path $root "ComTool.V2.slnx") package --vulnerable --include-transitive --format json --no-restore)
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet vulnerability audit failed with exit code $LASTEXITCODE."
    }
    try {
        $dependencyAudit = ($auditLines -join [Environment]::NewLine) |
            ConvertFrom-Json
    }
    catch {
        throw "NuGet vulnerability audit did not return valid JSON: $($_.Exception.Message)"
    }

    $vulnerabilityCount =
        Get-NuGetVulnerabilityCount $dependencyAudit
    if ($vulnerabilityCount -ne 0) {
        throw "NuGet vulnerability audit found $vulnerabilityCount known vulnerability record(s)."
    }

    if (-not $SkipTests) {
        Invoke-Checked {
            & $dotnet build (Join-Path $root "ComTool.V2.slnx") -c Release --no-restore
        } "Release build"
        Invoke-Checked {
            & $dotnet test (Join-Path $root "ComTool.V2.slnx") -c Release --no-build
        } "Deterministic test gate"
    }

    $projects = @(
        "src\ComTool.Cli\ComTool.Cli.csproj",
        "src\ComTool.RuntimeHost\ComTool.RuntimeHost.csproj",
        "src\ComTool.Worker\ComTool.Worker.csproj",
        "src\ComTool.Transport.Mcp\ComTool.Transport.Mcp.csproj"
    )

    foreach ($project in $projects) {
        $projectPath = Join-Path $root $project
        $name = [IO.Path]::GetFileNameWithoutExtension($project)
        $output = Join-Path $publishRoot $name
        $ridLockFile = "packages.$RuntimeIdentifier.lock.json"

        Invoke-Checked {
            & $dotnet restore $projectPath -r $RuntimeIdentifier --locked-mode "-p:NuGetLockFilePath=$ridLockFile"
        } "Locked RID restore $name"

        Invoke-Checked {
            & $dotnet publish $projectPath -c Release -r $RuntimeIdentifier --self-contained true --no-restore -o $output "-p:Version=$Version" "-p:InformationalVersion=$Version" "-p:PublishSingleFile=false" "-p:PublishReadyToRun=false" "-p:PublishTrimmed=false" "-p:DebugType=None" "-p:DebugSymbols=false"
        } "Publish $name"

        Merge-PublishDirectory -Source $output -Destination $packageRoot
    }

    $entrypoints = [ordered]@{
        cli = "ComTool.Cli.exe"
        runtimeHost = "ComTool.RuntimeHost.exe"
        worker = "ComTool.Worker.exe"
        mcp = "ComTool.Transport.Mcp.exe"
    }

    foreach ($entrypoint in $entrypoints.Values) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $entrypoint))) {
            throw "Required release entry point '$entrypoint' was not published."
        }
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-user.ps1") -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall-user.ps1") -Destination $packageRoot

    $signingEnabled =
        -not [string]::IsNullOrWhiteSpace($SignToolPath) -or
        -not [string]::IsNullOrWhiteSpace($SigningCertificateSha1)

    $signedFiles = [System.Collections.Generic.List[string]]::new()
    if ($signingEnabled) {
        if ([string]::IsNullOrWhiteSpace($SignToolPath) -or
            [string]::IsNullOrWhiteSpace($SigningCertificateSha1)) {
            throw "-SignToolPath and -SigningCertificateSha1 must be supplied together."
        }
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
            throw "signtool.exe was not found at '$SignToolPath'."
        }

        $peFiles = @(
            Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
                Where-Object {
                    $_.Name -like "ComTool.*" -and
                    $_.Extension -in ".exe", ".dll"
                } |
                Sort-Object FullName
        )
        foreach ($file in $peFiles) {
            Invoke-Checked {
                & $SignToolPath sign /sha1 $SigningCertificateSha1 /fd SHA256 /td SHA256 /tr $TimestampUrl $file.FullName
            } "Authenticode sign $($file.Name)"
            Invoke-Checked {
                & $SignToolPath verify /pa /all $file.FullName
            } "Authenticode verify $($file.Name)"
            [void]$signedFiles.Add(
                [IO.Path]::GetRelativePath(
                    $packageRoot,
                    $file.FullName
                ).Replace("\", "/"))
        }
    }

    $fileEntries = @(
        Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace("\", "/")
                    size = $_.Length
                    sha256 = Get-Sha256 $_.FullName
                }
            }
    )

    $manifest = [ordered]@{
        format = "comtool-v2-release"
        schemaVersion = 1
        product = "COM Tool V2"
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        configuration = "Release"
        source = [ordered]@{
            commit = $sourceCommit
            dirty = $sourceDirty
        }
        dependencyAudit = [ordered]@{
            kind = "nuget-vulnerability"
            includeTransitive = $true
            vulnerabilityRecords = $vulnerabilityCount
            sources = @($dependencyAudit.sources)
        }
        signing = [ordered]@{
            authenticode = $signingEnabled
            requiredForProduction = $false
            certificateSha1 = if ($signingEnabled) {
                $SigningCertificateSha1.ToLowerInvariant()
            } else {
                $null
            }
            timestampUrl = if ($signingEnabled) { $TimestampUrl } else { $null }
            files = $signedFiles
        }
        entrypoints = $entrypoints
        files = $fileEntries
    }

    $manifestPath = Join-Path $packageRoot "release-manifest.json"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    $sumsPath = Join-Path $packageRoot "SHA256SUMS.txt"
    $sumLines = $fileEntries | ForEach-Object {
        "$($_.sha256)  $($_.path)"
    }
    $sumLines | Set-Content -LiteralPath $sumsPath -Encoding utf8NoBOM

    Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = Get-Sha256 $zipPath
    "$zipHash  $zipName" |
        Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii

    Move-Item -LiteralPath $stagingRoot -Destination $artifactRoot

    $finalPackageRoot = Join-Path $artifactRoot "package"
    $finalManifestPath = Join-Path $finalPackageRoot "release-manifest.json"
    $finalZipPath = Join-Path $artifactRoot $zipName

    [pscustomobject]@{
        ok = $true
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        authenticodeSigned = $signingEnabled
        packageDirectory = $finalPackageRoot
        manifest = $finalManifestPath
        archive = $finalZipPath
        archiveSha256 = $zipHash
        fileCount = $fileEntries.Count
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}