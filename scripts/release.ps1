#Requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$RuntimeIdentifier = "win-x64",

    [switch]$AllowDirty,

    [string]$SignToolPath,

    [string]$SigningCertificateSha1,

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [string]$AipDebugCtlPath,

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$VersionPattern = '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'
$RuntimeIdentifierPattern = '^[A-Za-z0-9][A-Za-z0-9.-]*$'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot "dotnet.ps1"
$defaultAipDebugCtlPath = [IO.Path]::GetFullPath(
    (Join-Path $root "..\aip-debug\build\Release\aipdebugctl.exe")
)
if ([string]::IsNullOrWhiteSpace($AipDebugCtlPath)) {
    $AipDebugCtlPath = $defaultAipDebugCtlPath
}
else {
    $AipDebugCtlPath = [IO.Path]::GetFullPath($AipDebugCtlPath)
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch $VersionPattern) {
    throw "Release version '$Version' is invalid."
}
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier) -or
    $RuntimeIdentifier -notmatch $RuntimeIdentifierPattern) {
    throw "Runtime identifier '$RuntimeIdentifier' is invalid."
}
if ($SkipTests -and -not $AllowDirty) {
    throw "-SkipTests is permitted only with -AllowDirty for non-production validation."
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
$sdkArtifactsRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "ctv2-sdk-" + [Guid]::NewGuid().ToString("N")
)
$zipVerifyRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "ctv2-zipverify-" + [Guid]::NewGuid().ToString("N")
)

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

function Invoke-PackageVerification {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $raw = @(
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (
            Join-Path $PSScriptRoot "verify-package.ps1"
        ) -Source $Source -ExpectedVersion $ExpectedVersion 2>&1
    )
    $exitCode = $LASTEXITCODE
    $text = ($raw | ForEach-Object { "$_" }) -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        throw ($Description + " failed with exit code " + $exitCode + "." +
            [Environment]::NewLine + $text)
    }

    try {
        $result = $text | ConvertFrom-Json
    }
    catch {
        throw "$Description did not return valid JSON: $text"
    }

    if (-not [bool]$result.ok -or
        [string]$result.psVersion -notlike "5.1.*" -or
        [string]$result.version -ne $ExpectedVersion -or
        [string]$result.sourceCommit -ne $ExpectedCommit.ToLowerInvariant() -or
        [string]$result.runtimeInformationalVersion -ne ($ExpectedVersion + "+" + $ExpectedCommit.ToLowerInvariant())) {
        throw "$Description returned invalid provenance metadata: $text"
    }

    foreach ($property in @(
        "stableEntrypoints",
        "liveRuntimeUninstallRefused",
        "damagedInstallRepair",
        "sourceInstallDisjointPreflight",
        "danglingCurrentInstallRecovered",
        "stableNativeHelper",
        "nonJunctionCurrentRefusedBeforeMutation",
        "poisonedCurrentJunctionRefusedBeforeMutation",
        "danglingCurrentUninstallAll",
        "corruptPointerUninstallAll"
    )) {
        if (-not [bool]$result.$property) {
            throw "$Description did not prove '$property': $text"
        }
    }

    return $result
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-NativeHelperUsageSmoke {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutMs = 5000,
        [int]$MaxOutputBytes = 65536
    )

    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()
    $commandPath = Join-Path ([IO.Path]::GetTempPath()) (
        "comtool-v2-aipdebugctl-" + [Guid]::NewGuid().ToString("N") + ".cmd"
    )
    $process = $null

    try {
        $commandText =
            "@echo off" + [Environment]::NewLine +
            '"%COMTOOL_HELPER%" 1>"%COMTOOL_STDOUT%" 2>"%COMTOOL_STDERR%"' +
            [Environment]::NewLine +
            "exit /b %ERRORLEVEL%" + [Environment]::NewLine
        [IO.File]::WriteAllText(
            $commandPath,
            $commandText,
            [Text.Encoding]::ASCII
        )

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        if ([string]::IsNullOrWhiteSpace($env:ComSpec)) {
            $startInfo.FileName = Join-Path $env:SystemRoot "System32\cmd.exe"
        }
        else {
            $startInfo.FileName = $env:ComSpec
        }
        $startInfo.Arguments = '/d /s /c ""' + $commandPath + '""'
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.EnvironmentVariables["COMTOOL_HELPER"] = $Path
        $startInfo.EnvironmentVariables["COMTOOL_STDOUT"] = $stdoutPath
        $startInfo.EnvironmentVariables["COMTOOL_STDERR"] = $stderrPath

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "Staged aipdebugctl.exe usage smoke could not start."
        }

        $clock = [Diagnostics.Stopwatch]::StartNew()
        $breach = $null
        while (-not $process.WaitForExit(50)) {
            $stdoutLength = (Get-Item -LiteralPath $stdoutPath).Length
            $stderrLength = (Get-Item -LiteralPath $stderrPath).Length
            if ($stdoutLength -gt $MaxOutputBytes -or
                $stderrLength -gt $MaxOutputBytes) {
                $breach =
                    "Staged aipdebugctl.exe usage smoke exceeded the " +
                    "$MaxOutputBytes-byte output bound."
                break
            }
            if ($clock.ElapsedMilliseconds -ge $TimeoutMs) {
                $breach =
                    "Staged aipdebugctl.exe did not exit within the " +
                    "$TimeoutMs ms usage-smoke bound."
                break
            }
        }

        if ($null -ne $breach) {
            try {
                & (Join-Path $env:SystemRoot "System32\taskkill.exe") /PID $process.Id /T /F > $null 2>&1
            }
            catch {
            }
            try {
                [void]$process.WaitForExit(2000)
            }
            catch {
            }
            throw $breach
        }

        $process.WaitForExit()
        foreach ($outputPath in @($stdoutPath, $stderrPath)) {
            $outputLength = (Get-Item -LiteralPath $outputPath).Length
            if ($outputLength -gt $MaxOutputBytes) {
                throw (
                    "Staged aipdebugctl.exe usage smoke exceeded the " +
                    "$MaxOutputBytes-byte output bound."
                )
            }
        }

        $usageText =
            ([IO.File]::ReadAllText($stdoutPath, [Text.Encoding]::UTF8)) +
            [Environment]::NewLine +
            ([IO.File]::ReadAllText($stderrPath, [Text.Encoding]::UTF8))
        if ($process.ExitCode -ne 2 -or
            $usageText -notmatch '(?i)usage:') {
            throw (
                "The staged aipdebugctl.exe did not pass its bounded usage smoke " +
                "(expected exit 2 plus usage text)."
            )
        }
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
        Remove-Item -LiteralPath $stdoutPath, $stderrPath, $commandPath -Force -ErrorAction SilentlyContinue
    }
}

function Remove-TreeBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Attempts = 8
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_.Exception.Message
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds ([Math]::Min(75 * $attempt, 500))
            }
        }
    }

    Write-Warning (
        "Release completed, but temporary directory cleanup could not remove " +
        "'$Path' after $Attempts attempts: $lastError"
    )
}

function Get-SourceTreeFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$GitRoot,
        [Parameter(Mandatory = $true)][string]$ScopeRoot
    )

    $paths = @(& git -C $ScopeRoot -c core.quotePath=false ls-files --full-name --cached --others --exclude-standard -- .)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enumerate Git-visible COM Tool V2 source files."
    }

    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in @($paths | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        $normalizedPath = $relativePath.Replace("\", "/")
        $fullPath = Join-Path $GitRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            [void]$entries.Add(("{0}|MISSING" -f $normalizedPath))
            continue
        }

        $item = Get-Item -LiteralPath $fullPath
        $hash = Get-Sha256 $fullPath
        [void]$entries.Add(
            ("{0}|{1}|{2}" -f $normalizedPath, $item.Length, $hash)
        )
    }

    $payload = [Text.Encoding]::UTF8.GetBytes(
        ($entries -join [Environment]::NewLine)
    )
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $fingerprint = [Convert]::ToHexString(
            $sha256.ComputeHash($payload)
        ).ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    return [pscustomobject]@{
        sha256 = $fingerprint
        fileCount = $entries.Count
    }
}

function Assert-SourceStable {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][bool]$AllowDirtySource,
        [Parameter(Mandatory = $true)][string]$ExpectedTreeFingerprint,
        [Parameter(Mandatory = $true)][string]$GitRoot
    )

    $currentCommit = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or
        -not [string]::Equals(
            $currentCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source commit changed while the release transaction was running."
    }

    $currentSourceTree = Get-SourceTreeFingerprint -GitRoot $GitRoot -ScopeRoot $root
    if (-not [string]::Equals(
            [string]$currentSourceTree.sha256,
            $ExpectedTreeFingerprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "COM Tool V2 source bytes changed while the release transaction was running. Refusing to finalize a mixed-source artifact."
    }

    if (-not $AllowDirtySource) {
        $currentDirtyLines = @(
            & git -C $root status --porcelain=v1 --untracked-files=all -- .
        )
        if ($LASTEXITCODE -ne 0) {
            throw "Could not revalidate Git status before release finalization."
        }
        if ($currentDirtyLines.Count -gt 0) {
            throw "COM Tool V2 changed while the release transaction was running. Refusing to finalize a production artifact."
        }
    }
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

$globalJsonPath = Join-Path $root "global.json"
$mcpProjectPath = Join-Path $root "src\ComTool.Transport.Mcp\ComTool.Transport.Mcp.csproj"
$protocolVersionPath = Join-Path $root "src\ComTool.Protocol\ProtocolVersion.cs"
$attributionPath = Join-Path $root "ATTRIBUTION.md"
if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw "global.json was not found at '$globalJsonPath'."
}
if (-not (Test-Path -LiteralPath $mcpProjectPath -PathType Leaf)) {
    throw "MCP adapter project was not found at '$mcpProjectPath'."
}
if (-not (Test-Path -LiteralPath $protocolVersionPath -PathType Leaf)) {
    throw "Protocol version source was not found at '$protocolVersionPath'."
}
if (-not (Test-Path -LiteralPath $attributionPath -PathType Leaf)) {
    throw "Package attribution file was not found at '$attributionPath'."
}

$globalConfig = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$dotnetSdkVersion = [string]$globalConfig.sdk.version
if ([string]::IsNullOrWhiteSpace($dotnetSdkVersion)) {
    throw "global.json does not contain a pinned .NET SDK version."
}

[xml]$mcpProjectXml = Get-Content -LiteralPath $mcpProjectPath -Raw
$mcpPackageReference = @(
    $mcpProjectXml.Project.ItemGroup.PackageReference |
        Where-Object { [string]$_.Include -eq "ModelContextProtocol" }
)
if ($mcpPackageReference.Count -ne 1) {
    throw "MCP adapter must contain exactly one ModelContextProtocol package reference."
}
$mcpSdkVersion = [string]$mcpPackageReference[0].Version
if ([string]::IsNullOrWhiteSpace($mcpSdkVersion)) {
    throw "ModelContextProtocol package reference does not contain a pinned version."
}

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $nodeCommand) {
    throw "Node.js is required to validate the shipped pure-Node SDK surface."
}
$nodeVersion = (& $nodeCommand.Source --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($nodeVersion)) {
    throw "Could not resolve the Node.js version for SDK validation."
}

$protocolVersionSource = Get-Content -LiteralPath $protocolVersionPath -Raw
$protocolVersionMatch = [regex]::Match(
    $protocolVersionSource,
    'public\s+const\s+int\s+Current\s*=\s*(\d+)\s*;')
if (-not $protocolVersionMatch.Success) {
    throw "Could not resolve ProtocolVersion.Current from '$protocolVersionPath'."
}
$protocolVersion = [int]$protocolVersionMatch.Groups[1].Value

$gitRoot = (& git -C $root rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) {
    throw "COM Tool V2 must be released from a Git worktree."
}

$sourceCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve the source Git commit."
}

$sourceTree = Get-SourceTreeFingerprint -GitRoot $gitRoot.Trim() -ScopeRoot $root
$sourceTreeFingerprint = [string]$sourceTree.sha256
$sourceTreeFileCount = [int]$sourceTree.fileCount

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
New-Item -ItemType Directory -Force -Path $publishRoot, $packageRoot, $sdkArtifactsRoot | Out-Null

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

    Invoke-Checked {
        & $nodeCommand.Source (Join-Path $root "scripts\build-knowledge-pack.mjs") --check
    } "Embedded COM knowledge-pack deterministic drift check"

    Invoke-Checked {
        & $dotnet run --project (Join-Path $root "tools\schema-validator\SchemaValidator.csproj") -c Release --no-restore -- $root
    } "Protocol/schema metadata validation"

    if (-not $SkipTests) {
        Invoke-Checked {
            & $dotnet restore (Join-Path $root "ComTool.V2.slnx") --locked-mode --artifacts-path $sdkArtifactsRoot
        } "Locked isolated gate restore"
        Invoke-Checked {
            & $dotnet build (Join-Path $root "ComTool.V2.slnx") -c Release --no-restore --artifacts-path $sdkArtifactsRoot
        } "Release build"
        $previousV2Root = $env:COMTOOL_V2_ROOT
        try {
            $env:COMTOOL_V2_ROOT = $root
            Invoke-Checked {
                & $dotnet test (Join-Path $root "ComTool.V2.slnx") -c Release --no-build --artifacts-path $sdkArtifactsRoot
            } "Deterministic test gate"
        }
        finally {
            if ($null -eq $previousV2Root) {
                Remove-Item Env:COMTOOL_V2_ROOT -ErrorAction SilentlyContinue
            }
            else {
                $env:COMTOOL_V2_ROOT = $previousV2Root
            }
        }
        Invoke-Checked {
            & $nodeCommand.Source --test (Join-Path $root "sdk\node\test\sdk.test.mjs")
        } "Node SDK deterministic test gate"
        Invoke-Checked {
            & $nodeCommand.Source (Join-Path $root "sdk\node\test\typecheck.mjs")
        } "Node SDK TypeScript 5.9.3 declaration gate"
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

        Assert-SourceStable -ExpectedCommit $sourceCommit -AllowDirtySource ([bool]$AllowDirty) -ExpectedTreeFingerprint $sourceTreeFingerprint -GitRoot $gitRoot.Trim()

        Invoke-Checked {
            & $dotnet restore $projectPath -r $RuntimeIdentifier --locked-mode --artifacts-path $sdkArtifactsRoot "-p:NuGetLockFilePath=$ridLockFile"
        } "Locked RID restore $name"

        Assert-SourceStable -ExpectedCommit $sourceCommit -AllowDirtySource ([bool]$AllowDirty) -ExpectedTreeFingerprint $sourceTreeFingerprint -GitRoot $gitRoot.Trim()

        Invoke-Checked {
            & $dotnet publish $projectPath -c Release -r $RuntimeIdentifier --self-contained true --no-restore --artifacts-path $sdkArtifactsRoot -o $output "-p:Version=$Version" "-p:InformationalVersion=$Version" "-p:PublishSingleFile=false" "-p:PublishReadyToRun=false" "-p:PublishTrimmed=false" "-p:DebugType=None" "-p:DebugSymbols=false"
        } "Publish $name"

        Assert-SourceStable -ExpectedCommit $sourceCommit -AllowDirtySource ([bool]$AllowDirty) -ExpectedTreeFingerprint $sourceTreeFingerprint -GitRoot $gitRoot.Trim()

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

    if (-not (Test-Path -LiteralPath $AipDebugCtlPath -PathType Leaf)) {
        throw (
            "The native AIPDebug VectorIPC helper was not found at " +
            "'$AipDebugCtlPath'. Build /scripts/aip-debug first or pass " +
            "-AipDebugCtlPath explicitly."
        )
    }
    if ([IO.Path]::GetExtension($AipDebugCtlPath) -ne ".exe") {
        throw "The AIPDebug helper must be a Windows .exe: '$AipDebugCtlPath'."
    }
    $aipDebugCtlDestination = Join-Path $packageRoot "aipdebugctl.exe"
    Copy-Item -LiteralPath $AipDebugCtlPath -Destination $aipDebugCtlDestination

    Invoke-NativeHelperUsageSmoke -Path $aipDebugCtlDestination

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-user.ps1") -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall-user.ps1") -Destination $packageRoot
    Copy-Item -LiteralPath $attributionPath -Destination (Join-Path $packageRoot "ATTRIBUTION.md")

    # Ship the dependency-free Node control surface without its test harness.
    # It talks only to ComTool.Cli.exe stdio, which forwards into the one
    # persistent RuntimeHost; it is not another Adobe/COM implementation.
    $nodeSdkSource = Join-Path $root "sdk\node"
    $nodeSdkDestination = Join-Path $packageRoot "sdk\node"
    New-Item -ItemType Directory -Force -Path $nodeSdkDestination | Out-Null
    Copy-Item -LiteralPath (Join-Path $nodeSdkSource "package.json") -Destination $nodeSdkDestination
    Copy-Item -LiteralPath (Join-Path $nodeSdkSource "index.mjs") -Destination $nodeSdkDestination
    Copy-Item -LiteralPath (Join-Path $nodeSdkSource "index.d.ts") -Destination $nodeSdkDestination
    Copy-Item -LiteralPath (Join-Path $nodeSdkSource "README.md") -Destination $nodeSdkDestination
    Copy-Item -LiteralPath (Join-Path $nodeSdkSource "lib") -Destination $nodeSdkDestination -Recurse
    Copy-Item -LiteralPath (Join-Path $nodeSdkSource "bin") -Destination $nodeSdkDestination -Recurse

    $nodeSdkRequired = @(
        "package.json",
        "index.mjs",
        "index.d.ts",
        "README.md",
        "lib\client.mjs",
        "lib\cli-options.mjs",
        "lib\runner.mjs",
        "lib\recovery.mjs",
        "lib\session.mjs",
        "bin\comtool-run.mjs"
    )
    foreach ($relativePath in $nodeSdkRequired) {
        $sdkPath = Join-Path $nodeSdkDestination $relativePath
        if (-not (Test-Path -LiteralPath $sdkPath -PathType Leaf)) {
            throw "Required staged Node SDK file '$relativePath' was not copied."
        }
    }

    Invoke-Checked {
        & $nodeCommand.Source (Join-Path $nodeSdkDestination "bin\comtool-run.mjs") --help | Out-Null
    } "Staged Node SDK import/CLI smoke"

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
                    ($_.Name -like "ComTool.*" -or
                     $_.Name -eq "aipdebugctl.exe") -and
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

    # Signing mutates PE bytes, so native-helper provenance must be captured
    # after the optional signing pass rather than from the pre-sign image.
    $aipDebugCtlSha256 = Get-Sha256 $aipDebugCtlDestination

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
        protocolVersion = $protocolVersion
        runtimeIdentifier = $RuntimeIdentifier
        configuration = "Release"
        source = [ordered]@{
            commit = $sourceCommit
            dirty = $sourceDirty
            treeFingerprintSha256 = $sourceTreeFingerprint
            gitVisibleFileCount = $sourceTreeFileCount
        }
        dependencyAudit = [ordered]@{
            kind = "nuget-vulnerability"
            includeTransitive = $true
            vulnerabilityRecords = $vulnerabilityCount
            sources = @($dependencyAudit.sources)
        }
        toolchain = [ordered]@{
            dotnetSdk = $dotnetSdkVersion
            node = $nodeVersion
            modelContextProtocol = $mcpSdkVersion
        }
        gate = [ordered]@{
            deterministicTestsExecuted = -not [bool]$SkipTests
            nodeSdkTestsExecuted = -not [bool]$SkipTests
            schemaMetadataValidationExecuted = $true
            vulnerabilityAuditExecuted = $true
            packageSmokePowerShell51 = $false
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
        nativeHelpers = [ordered]@{
            aipdebugctl = [ordered]@{
                path = "aipdebugctl.exe"
                sha256 = $aipDebugCtlSha256
                transport = "vectoripc"
                required = $true
            }
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

    $packageSmoke = Invoke-PackageVerification -Source $packageRoot -ExpectedVersion $Version -ExpectedCommit $sourceCommit -Description "Stock Windows PowerShell 5.1 staged-package verification"

    $manifest["gate"]["packageSmokePowerShell51"] = $true
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = Get-Sha256 $zipPath
    "$zipHash  $zipName" |
        Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii

    New-Item -ItemType Directory -Force -Path $zipVerifyRoot | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $zipVerifyRoot
    $zipSmoke = Invoke-PackageVerification -Source $zipVerifyRoot -ExpectedVersion $Version -ExpectedCommit $sourceCommit -Description "Stock Windows PowerShell 5.1 shipped-ZIP verification"

    Assert-SourceStable -ExpectedCommit $sourceCommit -AllowDirtySource ([bool]$AllowDirty) -ExpectedTreeFingerprint $sourceTreeFingerprint -GitRoot $gitRoot.Trim()

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
        sourceTreeFingerprintSha256 = $sourceTreeFingerprint
        sourceTreeFileCount = $sourceTreeFileCount
        authenticodeSigned = $signingEnabled
        packageDirectory = $finalPackageRoot
        manifest = $finalManifestPath
        archive = $finalZipPath
        archiveSha256 = $zipHash
        fileCount = $fileEntries.Count
        packageSmokePowerShell51 = [bool]$packageSmoke.ok
        shippedZipSmokePowerShell51 = [bool]$zipSmoke.ok
        powerShellVersion = [string]$zipSmoke.psVersion
    } | ConvertTo-Json -Compress
}
finally {
    Remove-TreeBestEffort $stagingRoot
    Remove-TreeBestEffort $sdkArtifactsRoot
    Remove-TreeBestEffort $zipVerifyRoot
}
