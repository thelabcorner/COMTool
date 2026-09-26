param(
    [string]$Source = $PSScriptRoot,

    [string]$InstallRoot = (
        Join-Path $env:LOCALAPPDATA "Programs\ComToolV2"
    ),

    [switch]$AllowDirtyPackage,

    [switch]$Repair
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$VersionPattern = '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'

if (-not ("ComToolV2NativePath" -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class ComToolV2NativePath
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathSize,
        uint flags);

    public static string GetJunctionTarget(string path)
    {
        using (SafeFileHandle handle = OpenPath(path, true))
        {
            byte[] buffer = new byte[16384];
            int bytesReturned;
            if (!DeviceIoControl(
                    handle,
                    FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    buffer,
                    buffer.Length,
                    out bytesReturned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the directory reparse point.");
            }

            if (bytesReturned < 16 ||
                BitConverter.ToUInt32(buffer, 0) != IoReparseTagMountPoint)
            {
                throw new InvalidDataException(
                    "The directory reparse point is not a junction.");
            }

            int substituteOffset = BitConverter.ToUInt16(buffer, 8);
            int substituteLength = BitConverter.ToUInt16(buffer, 10);
            int printOffset = BitConverter.ToUInt16(buffer, 12);
            int printLength = BitConverter.ToUInt16(buffer, 14);
            const int pathBufferOffset = 16;

            int offset = printLength > 0 ? printOffset : substituteOffset;
            int length = printLength > 0 ? printLength : substituteLength;
            if (length <= 0 ||
                pathBufferOffset + offset + length > bytesReturned)
            {
                throw new InvalidDataException(
                    "The junction target is malformed.");
            }

            string target = Encoding.Unicode.GetString(
                buffer,
                pathBufferOffset + offset,
                length);
            return NormalizeNtPath(target);
        }
    }

    public static string GetFinalPath(string path)
    {
        using (SafeFileHandle handle = OpenPath(path, false))
        {
            StringBuilder buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve the final filesystem path.");
            }

            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    0);
                if (length == 0 || length >= buffer.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not resolve the final filesystem path.");
                }
            }

            return NormalizeNtPath(buffer.ToString());
        }
    }

    private static SafeFileHandle OpenPath(string path, bool openReparsePoint)
    {
        uint flags = FileFlagBackupSemantics;
        if (openReparsePoint)
            flags |= FileFlagOpenReparsePoint;

        SafeFileHandle handle = CreateFileW(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "Could not open filesystem path.");
        }

        return handle;
    }

    private static string NormalizeNtPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(4);
        if (path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(4);
        return path;
    }
}
'@
}

function Get-RelativePathCompat {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    $rootUri = New-Object System.Uri($rootFull)
    $pathUri = New-Object System.Uri($pathFull)
    return [Uri]::UnescapeDataString(
        $rootUri.MakeRelativeUri($pathUri).ToString()
    ).Replace("/", [IO.Path]::DirectorySeparatorChar)
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Move-FileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if ([IO.File]::Exists($Destination)) {
        $backup = $Destination + "." + [Guid]::NewGuid().ToString("N") + ".bak"
        try {
            [IO.File]::Replace($Source, $Destination, $backup)
        }
        finally {
            if ([IO.File]::Exists($backup)) {
                [IO.File]::Delete($backup)
            }
        }
    }
    else {
        [IO.File]::Move($Source, $Destination)
    }
}

function Get-PathItemEvenIfDangling {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
}

function Remove-DirectoryJunction {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-PathItemEvenIfDangling -Path $Path
    if ($null -eq $item) {
        return
    }
    if (-not $item.PSIsContainer -or
        -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace '$Path' because it is not a directory junction."
    }

    [IO.Directory]::Delete($Path)
}

function Get-CanonicalPathForComparison {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $probe = $full
    $suffix = New-Object System.Collections.Generic.List[string]

    while (-not (Test-Path -LiteralPath $probe)) {
        $leaf = [IO.Path]::GetFileName($probe)
        $parent = [IO.Path]::GetDirectoryName($probe)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            $parent -eq $probe) {
            return $full
        }
        if (-not [string]::IsNullOrWhiteSpace($leaf)) {
            $suffix.Insert(0, $leaf)
        }
        $probe = $parent
    }

    $canonical = [ComToolV2NativePath]::GetFinalPath($probe)
    foreach ($segment in $suffix) {
        $canonical = Join-Path $canonical $segment
    }

    return [IO.Path]::GetFullPath($canonical).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-PlainDirectoryOrAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $item = Get-PathItemEvenIfDangling -Path $Path
    if ($null -eq $item) {
        return
    }
    if (-not $item.PSIsContainer) {
        throw "$Description '$Path' exists but is not a directory."
    }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "$Description '$Path' must not be a reparse point."
    }
}

function Assert-NoReparsePointsUnderRoot {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer) {
        throw "Release root '$Root' is not a directory."
    }

    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push($rootItem.FullName)
    while ($stack.Count -gt 0) {
        $directory = $stack.Pop()
        $directoryItem = Get-Item -LiteralPath $directory -Force
        if ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Release package contains reparse point '$directory'."
        }

        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Release package contains reparse point '$($child.FullName)'."
            }
            if ($child.PSIsContainer) {
                $stack.Push($child.FullName)
            }
        }
    }
}

function Remove-DirectoryTreeSafe {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-PathItemEvenIfDangling -Path $Path
    if ($null -eq $item) {
        return
    }
    if (-not $item.PSIsContainer) {
        throw "Refusing to remove '$Path' because it is not a directory."
    }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        [IO.Directory]::Delete($Path)
        return
    }

    foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
        if ($child.PSIsContainer) {
            Remove-DirectoryTreeSafe -Path $child.FullName
        }
        else {
            if ($child.Attributes -band [IO.FileAttributes]::ReadOnly) {
                $child.Attributes = $child.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
            }
            [IO.File]::Delete($child.FullName)
        }
    }

    [IO.Directory]::Delete($Path)
}

function Assert-CurrentLinkSafe {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$VersionsRoot
    )

    $item = Get-PathItemEvenIfDangling -Path $Path
    if ($null -eq $item) {
        return
    }

    if (-not $item.PSIsContainer -or
        -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to use '$Path' because it exists but is not the COM Tool V2 current-version junction."
    }

    try {
        $target = [ComToolV2NativePath]::GetJunctionTarget($Path)
    }
    catch {
        throw "Refusing to use '$Path' because it is not a valid directory junction: $($_.Exception.Message)"
    }
    if (-not [IO.Path]::IsPathRooted($target)) {
        $target = Join-Path (Split-Path -Parent $Path) $target
    }
    $targetFull = [IO.Path]::GetFullPath($target).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $versionsFull = [IO.Path]::GetFullPath($VersionsRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $versionsPrefix = $versionsFull + [IO.Path]::DirectorySeparatorChar
    if (-not $targetFull.StartsWith(
            $versionsPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use '$Path' because its junction target '$targetFull' is outside the managed versions root '$versionsFull'."
    }

    $version = [IO.Path]::GetFileName($targetFull)
    $expectedTarget = if ($version -match $VersionPattern) {
        [IO.Path]::GetFullPath((Join-Path $versionsFull $version)).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
    } else {
        $null
    }
    if ($null -eq $expectedTarget -or
        -not $targetFull.Equals(
            $expectedTarget,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use '$Path' because its junction target '$targetFull' is not a direct managed version directory."
    }

    $targetItem = Get-PathItemEvenIfDangling -Path $targetFull
    if ($null -ne $targetItem -and -not $targetItem.PSIsContainer) {
        throw "Refusing to use '$Path' because its managed version target '$targetFull' is not a directory."
    }
    if ($null -ne $targetItem -and
        ($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to use '$Path' because managed version target '$targetFull' is itself a reparse point."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $hash = $sha.ComputeHash($stream)
        return ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        $sha.Dispose()
    }
}

function Assert-RootsDisjoint {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$StateRoot
    )

    $install = Get-CanonicalPathForComparison -Path $InstallRoot
    $state = Get-CanonicalPathForComparison -Path $StateRoot
    $installPrefix = $install + [IO.Path]::DirectorySeparatorChar
    $statePrefix = $state + [IO.Path]::DirectorySeparatorChar

    if ($install.Equals($state, [StringComparison]::OrdinalIgnoreCase) -or
        $install.StartsWith($statePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $state.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Install root '$install' and durable state root '$state' must be disjoint."
    }
}

function Assert-SourceInstallDisjoint {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$InstallRoot
    )

    $source = Get-CanonicalPathForComparison -Path $SourceRoot
    $install = Get-CanonicalPathForComparison -Path $InstallRoot
    $sourcePrefix = $source + [IO.Path]::DirectorySeparatorChar
    $installPrefix = $install + [IO.Path]::DirectorySeparatorChar

    if ($source.Equals($install, [StringComparison]::OrdinalIgnoreCase) -or
        $source.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $install.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release source '$source' and install root '$install' must be disjoint."
    }
}

function Get-InstallMutexName {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(
            [IO.Path]::GetFullPath($InstallRoot).ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        return "Local\ComToolV2Install_" +
            ([BitConverter]::ToString($hash, 0, 12)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-NoInstalledComToolProcesses {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar

    foreach ($name in @(
        "ComTool.RuntimeHost",
        "ComTool.Worker",
        "ComTool.Transport.Mcp",
        "ComTool.Cli"
    )) {
        foreach ($process in [Diagnostics.Process]::GetProcessesByName($name)) {
            try {
                $path = $process.MainModule.FileName
                if (-not [string]::IsNullOrWhiteSpace($path) -and
                    [IO.Path]::GetFullPath($path).StartsWith(
                        $prefix,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw "COM Tool V2 process $($process.Id) ('$path') is using this installation. Stop it before repairing installed files."
                }
            }
            finally {
                $process.Dispose()
            }
        }
    }
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

function Set-CurrentSelection {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)]$Pointer
    )

    $currentLink = Join-Path $InstallRoot "current"
    $currentPointer = Join-Path $InstallRoot "current.json"
    $token = [Guid]::NewGuid().ToString("N")
    $linkTemp = Join-Path $InstallRoot ".current-$token"
    $linkBackup = Join-Path $InstallRoot ".previous-current-$token"
    $pointerTemp = $currentPointer + "." + $token + ".tmp"
    $oldMoved = $false
    $newActivated = $false

    $existing = Get-PathItemEvenIfDangling -Path $currentLink
    Assert-CurrentLinkSafe -Path $currentLink -VersionsRoot (Join-Path $InstallRoot "versions")

    try {
        Write-Utf8NoBom -Path $pointerTemp -Text (
            $Pointer | ConvertTo-Json -Depth 4
        )
        New-Item -ItemType Junction -Path $linkTemp -Target $VersionRoot |
            Out-Null
        $versionsRoot = Join-Path $InstallRoot "versions"
        Assert-CurrentLinkSafe -Path $linkTemp -VersionsRoot $versionsRoot
        $resolvedTempTarget = [IO.Path]::GetFullPath(
            [ComToolV2NativePath]::GetJunctionTarget($linkTemp)).TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        $expectedVersionRoot = [IO.Path]::GetFullPath(
            $VersionRoot).TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        if (-not $resolvedTempTarget.Equals(
                $expectedVersionRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Transactional current junction resolved to '$resolvedTempTarget' instead of expected version root '$expectedVersionRoot'."
        }

        if ($null -ne $existing) {
            [IO.Directory]::Move($currentLink, $linkBackup)
            $oldMoved = $true
        }

        [IO.Directory]::Move($linkTemp, $currentLink)
        $newActivated = $true

        try {
            Move-FileAtomically -Source $pointerTemp -Destination $currentPointer
        }
        catch {
            Remove-DirectoryJunction -Path $currentLink
            $newActivated = $false
            if ($oldMoved) {
                [IO.Directory]::Move($linkBackup, $currentLink)
                $oldMoved = $false
            }
            throw
        }

        if ($oldMoved) {
            try {
                Remove-DirectoryJunction -Path $linkBackup
            }
            catch {
                # Activation and metadata are already committed. A stale
                # backup junction is harmless and can be removed later.
            }
            $oldMoved = $false
        }
    }
    finally {
        Remove-DirectoryJunction -Path $linkTemp
        if (Test-Path -LiteralPath $pointerTemp) {
            Remove-Item -LiteralPath $pointerTemp -Force
        }
        if (-not $newActivated -and $oldMoved -and
            $null -eq (Get-PathItemEvenIfDangling -Path $currentLink)) {
            [IO.Directory]::Move($linkBackup, $currentLink)
        }
    }

    return $currentLink
}

function Assert-ReleasePayload {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Manifest
    )

    Assert-NoReparsePointsUnderRoot -Root $Root

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
        $relative = (Get-RelativePathCompat -Root $Root -Path $file.FullName).Replace("\", "/")
        if (-not $expected.Contains($relative) -and
            -not $controlFiles.Contains($relative)) {
            throw "Release package contains unlisted file '$relative'."
        }
    }

    foreach ($entrypointName in @("cli", "runtimeHost", "worker", "mcp")) {
        $property = $Manifest.entrypoints.PSObject.Properties[$entrypointName]
        if ($null -eq $property -or
            [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "Release manifest is missing required entry point '$entrypointName'."
        }

        $relative = ([string]$property.Value).Replace("\", "/")
        $path = Resolve-ContainedPath -Root $Root -Relative $relative
        if (-not $expected.Contains($relative) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release entry point '$entrypointName' is not an inventoried payload file."
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
if ($manifest.product -ne "COM Tool V2" -or
    $manifest.configuration -ne "Release" -or
    $manifest.runtimeIdentifier -ne "win-x64") {
    throw "Release manifest does not describe a supported COM Tool V2 win-x64 production package."
}
if ([string]::IsNullOrWhiteSpace($manifest.version)) {
    throw "Release manifest does not contain a version."
}
Assert-SafeVersion -Version ([string]$manifest.version)
$dirtyProperty = if ($null -ne $manifest.source) {
    $manifest.source.PSObject.Properties["dirty"]
} else {
    $null
}
if ($null -eq $dirtyProperty -or $dirtyProperty.Value -isnot [bool]) {
    throw "Release manifest does not contain an explicit boolean source.dirty provenance value."
}
if ([bool]$dirtyProperty.Value -and -not $AllowDirtyPackage) {
    throw "This package was produced from a dirty source tree. Refusing a production install; use -AllowDirtyPackage only for validation."
}

$signingProperty = if ($null -ne $manifest.signing) {
    $manifest.signing.PSObject.Properties["authenticode"]
} else {
    $null
}
if ($null -eq $signingProperty -or
    $signingProperty.Value -isnot [bool]) {
    throw "Release manifest does not contain an explicit boolean signing.authenticode value."
}

Assert-ReleasePayload -Root $sourceRoot -Manifest $manifest

if ([bool]$signingProperty.Value) {
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
$stateRootFull = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "ComToolV2"))
Assert-RootsDisjoint -InstallRoot $installRootFull -StateRoot $stateRootFull
Assert-SourceInstallDisjoint -SourceRoot $sourceRoot -InstallRoot $installRootFull
Assert-PlainDirectoryOrAbsent -Path $installRootFull -Description "Install root"
$versionsRoot = Join-Path $installRootFull "versions"
$versionRoot = Resolve-ContainedPath -Root $versionsRoot -Relative ([string]$manifest.version)
$currentPath = Join-Path $installRootFull "current.json"
$cliPath = Resolve-ContainedPath -Root $versionRoot -Relative ([string]$manifest.entrypoints.cli)
$runtimeHostPath = Resolve-ContainedPath -Root $versionRoot -Relative ([string]$manifest.entrypoints.runtimeHost)
$workerPath = Resolve-ContainedPath -Root $versionRoot -Relative ([string]$manifest.entrypoints.worker)
$mcpPath = Resolve-ContainedPath -Root $versionRoot -Relative ([string]$manifest.entrypoints.mcp)
New-Item -ItemType Directory -Force -Path $installRootFull | Out-Null

$installMutex = [Threading.Mutex]::new(
    $false,
    (Get-InstallMutexName -InstallRoot $installRootFull))
$mutexHeld = $false
try {
    try {
        $mutexHeld = $installMutex.WaitOne(30000)
    }
    catch [Threading.AbandonedMutexException] {
        $mutexHeld = $true
    }
    if (-not $mutexHeld) {
        throw "Timed out waiting for another COM Tool V2 install/uninstall operation to finish."
    }

    Assert-CurrentLinkSafe -Path (Join-Path $installRootFull "current") -VersionsRoot $versionsRoot
    Assert-PlainDirectoryOrAbsent -Path $versionsRoot -Description "Versions root"
    New-Item -ItemType Directory -Force -Path $versionsRoot | Out-Null

    $needsInstall = -not (Test-Path -LiteralPath $versionRoot)
    $repairExisting = $false
    $reused = $false
    if (-not $needsInstall) {
        $validationFailure = $null
        try {
            $installedManifest = Join-Path $versionRoot "release-manifest.json"
            if (-not (Test-Path -LiteralPath $installedManifest -PathType Leaf)) {
                throw "release-manifest.json is missing"
            }
            if ((Get-Sha256 $installedManifest) -ne (Get-Sha256 $manifestPath)) {
                throw "release manifest differs from the requested package"
            }
            Assert-ReleasePayload -Root $versionRoot -Manifest $manifest
        }
        catch {
            $validationFailure = $_.Exception.Message
        }

        if ($null -eq $validationFailure) {
            $reused = $true
        }
        elseif (-not $Repair) {
            throw "Version '$($manifest.version)' is already installed but failed integrity verification: $validationFailure. Re-run with -Repair to replace that damaged version."
        }
        else {
            Assert-NoInstalledComToolProcesses -InstallRoot $installRootFull
            $repairExisting = $true
            $needsInstall = $true
        }
    }

    if ($needsInstall) {
        $stage = Join-Path $installRootFull (".install-" + [Guid]::NewGuid().ToString("N"))
        $repairBackup = $null
        $oldMoved = $false
        $newMoved = $false
        try {
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            Get-ChildItem -LiteralPath $sourceRoot -Force |
                Copy-Item -Destination $stage -Recurse
            Assert-ReleasePayload -Root $stage -Manifest $manifest

            if ($repairExisting) {
                $repairBackup = Join-Path $installRootFull (
                    ".repair-$($manifest.version)-" +
                    [Guid]::NewGuid().ToString("N"))
                [IO.Directory]::Move($versionRoot, $repairBackup)
                $oldMoved = $true
            }

            [IO.Directory]::Move($stage, $versionRoot)
            $newMoved = $true
            Assert-ReleasePayload -Root $versionRoot -Manifest $manifest

            if ($oldMoved) {
                Remove-DirectoryTreeSafe -Path $repairBackup
                $oldMoved = $false
            }
        }
        catch {
            if ($newMoved -and
                $null -ne (Get-PathItemEvenIfDangling -Path $versionRoot)) {
                Remove-DirectoryTreeSafe -Path $versionRoot
                $newMoved = $false
            }
            if ($oldMoved -and
                $null -eq (Get-PathItemEvenIfDangling -Path $versionRoot)) {
                [IO.Directory]::Move($repairBackup, $versionRoot)
                $oldMoved = $false
            }
            throw
        }
        finally {
            if ($null -ne (Get-PathItemEvenIfDangling -Path $stage)) {
                Remove-DirectoryTreeSafe -Path $stage
            }
            if ($oldMoved -and
                $null -eq (Get-PathItemEvenIfDangling -Path $versionRoot)) {
                [IO.Directory]::Move($repairBackup, $versionRoot)
                $oldMoved = $false
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
$currentRoot = Set-CurrentSelection -InstallRoot $installRootFull -VersionRoot $versionRoot -Pointer $pointer

$stableCliPath = Resolve-ContainedPath -Root $currentRoot -Relative ([string]$manifest.entrypoints.cli)
$stableRuntimeHostPath = Resolve-ContainedPath -Root $currentRoot -Relative ([string]$manifest.entrypoints.runtimeHost)
$stableWorkerPath = Resolve-ContainedPath -Root $currentRoot -Relative ([string]$manifest.entrypoints.worker)
$stableMcpPath = Resolve-ContainedPath -Root $currentRoot -Relative ([string]$manifest.entrypoints.mcp)

[pscustomobject]@{
    ok = $true
    version = [string]$manifest.version
    reusedExistingVersion = $reused
    installRoot = $installRootFull
    versionRoot = $versionRoot
    currentRoot = $currentRoot
    stateRoot = $stateRootFull
    cli = $stableCliPath
    runtimeHost = $stableRuntimeHostPath
    worker = $stableWorkerPath
    mcp = $stableMcpPath
    versionCli = $cliPath
    versionRuntimeHost = $runtimeHostPath
    versionWorker = $workerPath
    versionMcp = $mcpPath
} | ConvertTo-Json -Compress
}
finally {
    if ($mutexHeld) {
        try {
            $installMutex.ReleaseMutex()
        }
        catch [ApplicationException] {
        }
    }
    $installMutex.Dispose()
}