param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Read-JsonObject {
    param([Parameter(Mandatory = $true)][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw "Expected JSON output but received no content."
    }

    return ($Text | ConvertFrom-Json)
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

$base = Join-Path ([IO.Path]::GetTempPath()) ("comtool-v2-package-verify-" + [Guid]::NewGuid().ToString("N"))
$installRoot = Join-Path $base "install"
$stateRoot = Join-Path $base "state"
$localAppData = Join-Path $base "localappdata"
$oldLocalAppData = $env:LOCALAPPDATA
$runtime = $null

try {
    New-Item -ItemType Directory -Force -Path $base, $localAppData | Out-Null
    $env:LOCALAPPDATA = $localAppData

    $installArgs = @{
        Source = $sourceRoot
        InstallRoot = $installRoot
    }
    if ([bool]$dirtyProperty.Value) {
        $installArgs["AllowDirtyPackage"] = $true
        $installOutput = & $installScript -Source $sourceRoot -InstallRoot $installRoot -AllowDirtyPackage
    }
    else {
        $installOutput = & $installScript -Source $sourceRoot -InstallRoot $installRoot
    }

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
    if (-not $runtimeInfoVersion.StartsWith($ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Installed runtime reports informational version '$runtimeInfoVersion'; expected '$ExpectedVersion'."
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
        & $installScript @installArgs | Out-Null
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

    $repairArgs = @{}
    foreach ($key in $installArgs.Keys) {
        $repairArgs[$key] = $installArgs[$key]
    }
    $repairArgs.Repair = $true

    $repaired = Read-JsonObject ((& $installScript @repairArgs | Out-String))
    if (-not (Test-Path -LiteralPath (Join-Path ([string]$repaired.versionRoot) "release-manifest.json") -PathType Leaf)) {
        throw "Packaged installer -Repair did not restore the installed manifest."
    }

    $overlapSource = Join-Path $base "overlap-source"
    Copy-Item -LiteralPath $sourceRoot -Destination $overlapSource -Recurse
    $nestedInstallRoot = Join-Path $overlapSource "nested-install"
    $installUnderSourceRefused = $false
    try {
        & $installScript -Source $overlapSource -InstallRoot $nestedInstallRoot | Out-Null
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
        & $installScript -Source $nestedSource -InstallRoot $sourceUnderInstallRoot | Out-Null
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

    $currentLink = Join-Path $installRoot "current"
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
    $repaired = Read-JsonObject ((& $installScript @installArgs | Out-String))

    $outsideTarget = Join-Path $base "outside-current-target"
    New-Item -ItemType Directory -Force -Path $outsideTarget | Out-Null
    $outsideSentinel = Join-Path $outsideTarget "sentinel.txt"
    [IO.File]::WriteAllText($outsideSentinel, "preserve", (New-Object Text.UTF8Encoding($false)))
    [IO.Directory]::Delete($currentLink)
    New-Item -ItemType Junction -Path $currentLink -Target $outsideTarget | Out-Null

    $poisonedInstallRefused = $false
    try {
        & $installScript @installArgs | Out-Null
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
    $repaired = Read-JsonObject ((& $installScript @installArgs | Out-String))

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
        runtimeInformationalVersion = $runtimeInfoVersion
        stableEntrypoints = $true
        liveRuntimeUninstallRefused = $true
        damagedInstallRepair = $true
        sourceInstallDisjointPreflight = $true
        nonJunctionCurrentRefusedBeforeMutation = $true
        poisonedCurrentJunctionRefusedBeforeMutation = $true
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
