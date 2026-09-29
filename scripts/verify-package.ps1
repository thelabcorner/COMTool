param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if ($PSVersionTable.PSVersion.Major -ne 5 -or
    $PSVersionTable.PSVersion.Minor -ne 1) {
    throw "Package verification must run under stock Windows PowerShell 5.1."
}

function Read-JsonObject {
    param([Parameter(Mandatory = $true)][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw "Expected JSON output but received no content."
    }

    return ($Text | ConvertFrom-Json)
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
            throw "Installed aipdebugctl.exe usage smoke could not start."
        }

        $clock = [Diagnostics.Stopwatch]::StartNew()
        $breach = $null
        while (-not $process.WaitForExit(50)) {
            $stdoutLength = (Get-Item -LiteralPath $stdoutPath).Length
            $stderrLength = (Get-Item -LiteralPath $stderrPath).Length
            if ($stdoutLength -gt $MaxOutputBytes -or
                $stderrLength -gt $MaxOutputBytes) {
                $breach =
                    "Installed aipdebugctl.exe usage smoke exceeded the " +
                    "$MaxOutputBytes-byte output bound."
                break
            }
            if ($clock.ElapsedMilliseconds -ge $TimeoutMs) {
                $breach =
                    "Installed aipdebugctl.exe did not exit within the " +
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
                    "Installed aipdebugctl.exe usage smoke exceeded the " +
                    "$MaxOutputBytes-byte output bound."
                )
            }
        }

        $usageText =
            ([IO.File]::ReadAllText($stdoutPath, [Text.Encoding]::UTF8)) +
            [Environment]::NewLine +
            ([IO.File]::ReadAllText($stderrPath, [Text.Encoding]::UTF8))
        if ($process.ExitCode -ne 2 -or $usageText -notmatch '(?i)usage:') {
            throw (
                "Installed aipdebugctl.exe did not pass its bounded usage smoke " +
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

function Invoke-PackagedInstall {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$PackageSource,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][bool]$AllowDirty,
        [switch]$Repair
    )

    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        throw "Package verification attempted to invoke the installer with an empty release source."
    }
    if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
        throw "Package verification attempted to invoke the installer with an empty install root."
    }

    if ($AllowDirty) {
        return (& $ScriptPath -Source ([string]$PackageSource) -InstallRoot ([string]$DestinationRoot) -AllowDirtyPackage -Repair:$Repair.IsPresent)
    }

    return (& $ScriptPath -Source ([string]$PackageSource) -InstallRoot ([string]$DestinationRoot) -Repair:$Repair.IsPresent)
}

$sourceRoot = [IO.Path]::GetFullPath($Source)
$manifestPath = Join-Path $sourceRoot "release-manifest.json"
$installScript = Join-Path $sourceRoot "install-user.ps1"
$uninstallScript = Join-Path $sourceRoot "uninstall-user.ps1"

foreach ($required in @($manifestPath, $installScript, $uninstallScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required packaged verification input '$required' was not found."
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.version -ne $ExpectedVersion) {
    throw "Package version '$($manifest.version)' does not match expected version '$ExpectedVersion'."
}
$dirtyProperty = $manifest.source.PSObject.Properties["dirty"]
if ($null -eq $dirtyProperty -or $dirtyProperty.Value -isnot [bool]) {
    throw "Package manifest source.dirty must be an explicit boolean."
}
$sourceCommit = [string]$manifest.source.commit
if ([string]::IsNullOrWhiteSpace($sourceCommit) -or
    $sourceCommit -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "Package manifest source.commit must be a full 40-character Git commit."
}
$expectedRuntimeInformationalVersion =
    $ExpectedVersion + "+" + $sourceCommit.ToLowerInvariant()

$nativeHelper = $manifest.nativeHelpers.aipdebugctl
if ($null -eq $nativeHelper -or
    [string]$nativeHelper.path -ne "aipdebugctl.exe" -or
    -not [bool]$nativeHelper.required -or
    [string]$nativeHelper.transport -ne "vectoripc" -or
    [string]$nativeHelper.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw "Package manifest nativeHelpers.aipdebugctl is missing or invalid."
}
$packagedNativeHelper = Join-Path $sourceRoot ([string]$nativeHelper.path)
if (-not (Test-Path -LiteralPath $packagedNativeHelper -PathType Leaf)) {
    throw "Packaged native helper '$packagedNativeHelper' is missing."
}
if ((Get-Sha256 $packagedNativeHelper) -ne
    ([string]$nativeHelper.sha256).ToLowerInvariant()) {
    throw "Packaged aipdebugctl.exe hash does not match the release manifest."
}

$base = Join-Path ([IO.Path]::GetTempPath()) ("comtool v2 package verify " + [Guid]::NewGuid().ToString("N"))
$installRoot = Join-Path $base "install"
$stateRoot = Join-Path $base "state"
$localAppData = Join-Path $base "localappdata"
$oldLocalAppData = $env:LOCALAPPDATA
$runtime = $null

try {
    New-Item -ItemType Directory -Force -Path $base, $localAppData | Out-Null
    $env:LOCALAPPDATA = $localAppData

    $allowDirtyPackage = [bool]$dirtyProperty.Value
    $installOutput = Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage
    $installed = Read-JsonObject (($installOutput | Out-String))

    if ([string]$installed.version -ne $ExpectedVersion) {
        throw "Packaged installer activated unexpected version '$($installed.version)'."
    }

    $expectedCurrentRoot = [IO.Path]::GetFullPath((Join-Path $installRoot "current"))
    if (-not [IO.Path]::GetFullPath([string]$installed.currentRoot).Equals($expectedCurrentRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Packaged installer did not expose the stable current junction."
    }

    $currentPrefix = $expectedCurrentRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($entrypointName in @("cli", "runtimeHost", "worker", "mcp")) {
        $path = [string]$installed.$entrypointName
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Installed stable entry point '$entrypointName' is missing."
        }

        if (-not [IO.Path]::GetFullPath($path).StartsWith($currentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed entry point '$entrypointName' is not routed through the stable current path."
        }
    }

    foreach ($sdkRelative in @(
        "sdk\node\package.json",
        "sdk\node\index.mjs",
        "sdk\node\index.d.ts",
        "sdk\node\README.md",
        "sdk\node\lib\client.mjs",
        "sdk\node\lib\cli-options.mjs",
        "sdk\node\lib\runner.mjs",
        "sdk\node\lib\recovery.mjs",
        "sdk\node\lib\session.mjs",
        "sdk\node\lib\local-runtime.mjs",
        "sdk\node\bin\comtool-run.mjs"
    )) {
        $sdkPath = Join-Path $expectedCurrentRoot $sdkRelative
        if (-not (Test-Path -LiteralPath $sdkPath -PathType Leaf)) {
            throw "Installed Node SDK surface '$sdkRelative' is missing from the stable current path."
        }
    }

    foreach ($agentRelative in @(
        "agent\README.md",
        "agent\SKILL.md",
        "agent\AGENT_CONTRACT.md",
        "agent\openfork\comtool.md"
    )) {
        $agentPath = Join-Path $expectedCurrentRoot $agentRelative
        if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf)) {
            throw "Installed agent surface '$agentRelative' is missing from the stable current path."
        }
    }

    $nativeHelp = Read-JsonObject ((& ([string]$installed.cli) help | Out-String))
    if (-not [bool]$nativeHelp.ok -or
        [string]$nativeHelp.agentHint -notlike "*agent-guide --content*") {
        throw "Installed CLI help does not advertise the COMTool agent bootstrap."
    }

    $agentGuide = Read-JsonObject ((& ([string]$installed.cli) agent-guide --content | Out-String))
    if (-not [bool]$agentGuide.ok -or
        -not (Test-Path -LiteralPath ([string]$agentGuide.agent.skillPath) -PathType Leaf) -or
        -not (Test-Path -LiteralPath ([string]$agentGuide.agent.contractPath) -PathType Leaf) -or
        -not (Test-Path -LiteralPath ([string]$agentGuide.agent.openForkCommandPath) -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace([string]$agentGuide.content.skill) -or
        [string]::IsNullOrWhiteSpace([string]$agentGuide.content.contract) -or
        [string]::IsNullOrWhiteSpace([string]$agentGuide.content.openForkCommand)) {
        throw "Installed CLI agent-guide did not expose the packaged agent surfaces."
    }

    $installedNativeHelper = Join-Path $expectedCurrentRoot "aipdebugctl.exe"
    if (-not (Test-Path -LiteralPath $installedNativeHelper -PathType Leaf)) {
        throw "Installed VectorIPC helper is missing from the stable current path."
    }
    if ((Get-Sha256 $installedNativeHelper) -ne
        ([string]$nativeHelper.sha256).ToLowerInvariant()) {
        throw "Installed aipdebugctl.exe does not match the release-manifest hash."
    }

    Invoke-NativeHelperUsageSmoke -Path $installedNativeHelper

    $pipe = "comtool-v2-package-verify-" + [Guid]::NewGuid().ToString("N")
    $runtimeArgs =
        "--worker `"$([string]$installed.worker)`" " +
        "--pipe `"$pipe`" " +
        "--state-dir `"$stateRoot`""
    $runtime = Start-Process -FilePath ([string]$installed.runtimeHost) -ArgumentList $runtimeArgs -PassThru -WindowStyle Hidden

    $health = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 200
        if ($runtime.HasExited) {
            throw "Installed RuntimeHost exited during package verification with code $($runtime.ExitCode)."
        }

        $rawHealth = & ([string]$installed.cli) health --runtime --pipe $pipe 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($rawHealth -join [Environment]::NewLine))) {
            try {
                $health = Read-JsonObject ($rawHealth -join [Environment]::NewLine)
                break
            }
            catch {
            }
        }
    }

    if ($null -eq $health -or -not [bool]$health.ok) {
        throw "Installed runtime did not become healthy during package verification."
    }

    $runtimeInfoVersion = [string]$health.result.value.runtimeInformationalVersion
    if (-not $runtimeInfoVersion.Equals($expectedRuntimeInformationalVersion, [StringComparison]::Ordinal)) {
        throw "Installed runtime reports informational version '$runtimeInfoVersion'; expected '$expectedRuntimeInformationalVersion'."
    }
    $runtimeProtocolVersion = [int]$health.result.value.protocolVersion
    if ($runtimeProtocolVersion -ne [int]$manifest.protocolVersion) {
        throw "Installed runtime protocol version '$runtimeProtocolVersion' does not match package protocol version '$($manifest.protocolVersion)'."
    }
    $runtimeStateSchemaVersion = [int]$health.result.value.stateSchemaVersion
    if ($runtimeStateSchemaVersion -le 0) {
        throw "Installed runtime reported invalid state schema version '$runtimeStateSchemaVersion'."
    }

    $liveUninstallRefused = $false
    try {
        & $uninstallScript -InstallRoot $installRoot -All | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*is using this installation*") {
            $liveUninstallRefused = $true
        }
        else {
            throw
        }
    }

    if (-not $liveUninstallRefused) {
        throw "Packaged uninstaller did not refuse removal while RuntimeHost was live."
    }
    if (-not (Test-Path -LiteralPath ([string]$installed.versionRoot) -PathType Container)) {
        throw "Live-runtime uninstall refusal was destructive."
    }

    Stop-Process -Id $runtime.Id -Force
    $runtime.WaitForExit()
    $runtime = $null

    $installedManifest = Join-Path ([string]$installed.versionRoot) "release-manifest.json"
    Remove-Item -LiteralPath $installedManifest -Force

    $damagedRefused = $false
    try {
        Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*failed integrity verification*") {
            $damagedRefused = $true
        }
        else {
            throw
        }
    }

    if (-not $damagedRefused) {
        throw "Damaged installed version was accepted without -Repair."
    }

    $repaired = Read-JsonObject ((Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage -Repair | Out-String))
    if (-not (Test-Path -LiteralPath (Join-Path ([string]$repaired.versionRoot) "release-manifest.json") -PathType Leaf)) {
        throw "Packaged installer -Repair did not restore the installed manifest."
    }

    $currentLink = Join-Path $installRoot "current"
    $danglingTarget = Join-Path (Join-Path $installRoot "versions") "9.9.9-dangling"
    New-Item -ItemType Directory -Force -Path $danglingTarget | Out-Null
    [IO.Directory]::Delete($currentLink)
    New-Item -ItemType Junction -Path $currentLink -Target $danglingTarget | Out-Null
    Remove-Item -LiteralPath $danglingTarget -Recurse -Force

    $danglingRecovered = Read-JsonObject ((Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage | Out-String))
    if (-not (Test-Path -LiteralPath ([string]$danglingRecovered.runtimeHost) -PathType Leaf)) {
        throw "Packaged installer did not recover a dangling managed current junction."
    }

    $overlapSource = Join-Path $base "overlap-source"
    Copy-Item -LiteralPath $sourceRoot -Destination $overlapSource -Recurse
    $nestedInstallRoot = Join-Path $overlapSource "nested-install"
    $installUnderSourceRefused = $false
    try {
        Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $overlapSource -DestinationRoot $nestedInstallRoot -AllowDirty $allowDirtyPackage | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*must be disjoint*") {
            $installUnderSourceRefused = $true
        }
        else {
            throw
        }
    }
    if (-not $installUnderSourceRefused -or (Test-Path -LiteralPath $nestedInstallRoot)) {
        throw "Packaged installer did not reject an install root nested beneath its release source before mutation."
    }

    $sourceUnderInstallRoot = Join-Path $base "source-under-install"
    $nestedSource = Join-Path $sourceUnderInstallRoot "package"
    New-Item -ItemType Directory -Force -Path $sourceUnderInstallRoot | Out-Null
    Copy-Item -LiteralPath $sourceRoot -Destination $nestedSource -Recurse
    $sourceUnderInstallRefused = $false
    try {
        Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $nestedSource -DestinationRoot $sourceUnderInstallRoot -AllowDirty $allowDirtyPackage | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*must be disjoint*") {
            $sourceUnderInstallRefused = $true
        }
        else {
            throw
        }
    }
    if (-not $sourceUnderInstallRefused -or
        (Test-Path -LiteralPath (Join-Path $sourceUnderInstallRoot "versions")) -or
        (Test-Path -LiteralPath (Join-Path $sourceUnderInstallRoot "current"))) {
        throw "Packaged installer did not reject a release source nested beneath its install root before mutation."
    }

    [IO.Directory]::Delete($currentLink)
    New-Item -ItemType Directory -Path $currentLink | Out-Null

    $poisonedCurrentRefused = $false
    try {
        & $uninstallScript -InstallRoot $installRoot -All | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*current-version junction*") {
            $poisonedCurrentRefused = $true
        }
        else {
            throw
        }
    }

    if (-not $poisonedCurrentRefused) {
        throw "Packaged uninstaller did not reject a non-junction current path."
    }
    if (-not (Test-Path -LiteralPath ([string]$repaired.versionRoot) -PathType Container)) {
        throw "Non-junction current-path rejection was destructive."
    }

    Remove-Item -LiteralPath $currentLink -Recurse -Force
    $repaired = Read-JsonObject ((Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage | Out-String))

    $outsideTarget = Join-Path $base "outside-current-target"
    New-Item -ItemType Directory -Force -Path $outsideTarget | Out-Null
    $outsideSentinel = Join-Path $outsideTarget "sentinel.txt"
    [IO.File]::WriteAllText($outsideSentinel, "preserve", (New-Object Text.UTF8Encoding($false)))
    [IO.Directory]::Delete($currentLink)
    New-Item -ItemType Junction -Path $currentLink -Target $outsideTarget | Out-Null

    $poisonedInstallRefused = $false
    try {
        Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*outside the managed versions root*") {
            $poisonedInstallRefused = $true
        }
        else {
            throw
        }
    }
    if (-not $poisonedInstallRefused) {
        throw "Packaged installer accepted a current junction targeting outside the managed versions root."
    }

    $poisonedUninstallRefused = $false
    try {
        & $uninstallScript -InstallRoot $installRoot -All | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*outside the managed versions root*") {
            $poisonedUninstallRefused = $true
        }
        else {
            throw
        }
    }
    if (-not $poisonedUninstallRefused -or
        -not (Test-Path -LiteralPath $outsideSentinel -PathType Leaf) -or
        -not (Test-Path -LiteralPath ([string]$repaired.versionRoot) -PathType Container)) {
        throw "Poisoned current-junction rejection was destructive or incomplete."
    }

    [IO.Directory]::Delete($currentLink)
    Remove-Item -LiteralPath $outsideTarget -Recurse -Force
    $repaired = Read-JsonObject ((Invoke-PackagedInstall -ScriptPath $installScript -PackageSource $sourceRoot -DestinationRoot $installRoot -AllowDirty $allowDirtyPackage | Out-String))

    $danglingTarget = Join-Path (Join-Path $installRoot "versions") "9.9.9-dangling"
    New-Item -ItemType Directory -Force -Path $danglingTarget | Out-Null
    [IO.Directory]::Delete($currentLink)
    New-Item -ItemType Junction -Path $currentLink -Target $danglingTarget | Out-Null
    Remove-Item -LiteralPath $danglingTarget -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $installRoot "current.json"), "{broken", (New-Object Text.UTF8Encoding($false)))

    $uninstalled = Read-JsonObject ((& $uninstallScript -InstallRoot $installRoot -All | Out-String))
    if (-not [bool]$uninstalled.ok) {
        throw "Packaged uninstall -All did not report success."
    }
    if (Test-Path -LiteralPath $installRoot) {
        throw "Packaged uninstall -All left the install root behind."
    }

    [pscustomobject]@{
        ok = $true
        psVersion = $PSVersionTable.PSVersion.ToString()
        version = $ExpectedVersion
        sourceCommit = $sourceCommit.ToLowerInvariant()
        runtimeInformationalVersion = $runtimeInfoVersion
        protocolVersion = $runtimeProtocolVersion
        stateSchemaVersion = $runtimeStateSchemaVersion
        stableEntrypoints = $true
        stableNodeSdk = $true
        stableAgentBundle = $true
        stableNativeHelper = $true
        liveRuntimeUninstallRefused = $true
        damagedInstallRepair = $true
        sourceInstallDisjointPreflight = $true
        danglingCurrentInstallRecovered = $true
        nonJunctionCurrentRefusedBeforeMutation = $true
        poisonedCurrentJunctionRefusedBeforeMutation = $true
        danglingCurrentUninstallAll = $true
        corruptPointerUninstallAll = $true
    } | ConvertTo-Json -Compress
}
finally {
    if ($null -ne $runtime -and -not $runtime.HasExited) {
        Stop-Process -Id $runtime.Id -Force -ErrorAction SilentlyContinue
        $runtime.WaitForExit()
    }

    $env:LOCALAPPDATA = $oldLocalAppData

    if (Test-Path -LiteralPath $base) {
        Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
    }
}
