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

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$stateRootFull = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "ComToolV2"))
$versionsRoot = Join-Path $installRootFull "versions"
$currentPath = Join-Path $installRootFull "current.json"
$currentLinkPath = Join-Path $installRootFull "current"

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
        throw "Installed version root '$Root' is not a directory."
    }

    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push($rootItem.FullName)
    while ($stack.Count -gt 0) {
        $directory = $stack.Pop()
        $directoryItem = Get-Item -LiteralPath $directory -Force
        if ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Installed version contains reparse point '$directory'."
        }

        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Installed version contains reparse point '$($child.FullName)'."
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

    return $targetFull
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
                    throw "COM Tool V2 process $($process.Id) ('$path') is using this installation. Stop it before uninstalling."
                }
            }
            finally {
                $process.Dispose()
            }
        }
    }
}

function Get-StateOwnerSemaphoreName {
    param([Parameter(Mandatory = $true)][string]$StateRoot)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(
            [IO.Path]::GetFullPath($StateRoot).ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        return "Local\ComToolV2State_" +
            ([BitConverter]::ToString($hash, 0, 12)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Enter-StateRemovalGuard {
    param([Parameter(Mandatory = $true)][string]$StateRoot)

    $semaphore = [Threading.Semaphore]::new(
        1,
        1,
        (Get-StateOwnerSemaphoreName -StateRoot $StateRoot))
    $held = $false
    try {
        $held = $semaphore.WaitOne(0)
        if (-not $held) {
            throw "Durable COM Tool V2 state '$StateRoot' is currently owned by a running runtime. Stop it before using -RemoveState."
        }

        if (Test-Path -LiteralPath $StateRoot -PathType Container) {
            $lockPath = Join-Path $StateRoot ".runtime-owner.lock"
            $probe = $null
            try {
                $probe = New-Object IO.FileStream(
                    $lockPath,
                    [IO.FileMode]::OpenOrCreate,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::None)
            }
            catch [IO.IOException] {
                throw "Durable COM Tool V2 state '$StateRoot' is currently owned by a running runtime. Stop it before using -RemoveState."
            }
            finally {
                if ($null -ne $probe) {
                    $probe.Dispose()
                }
            }
        }

        return $semaphore
    }
    catch {
        if ($held) {
            try {
                [void]$semaphore.Release()
            }
            catch [Threading.SemaphoreFullException] {
            }
        }
        $semaphore.Dispose()
        throw
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

    Assert-NoReparsePointsUnderRoot -Root $VersionRoot

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
        $relative = (Get-RelativePathCompat -Root $VersionRoot -Path $file.FullName).Replace("\", "/")
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

function Get-CurrentSelection {
    param(
        [Parameter(Mandatory = $true)][string]$PointerPath,
        [Parameter(Mandatory = $true)][string]$LinkPath,
        [Parameter(Mandatory = $true)][string]$VersionsRoot
    )

    $pointerFailure = $null
    if (Test-Path -LiteralPath $PointerPath -PathType Leaf) {
        try {
            $pointer = Get-Content -LiteralPath $PointerPath -Raw | ConvertFrom-Json
            if ($pointer.format -ne "comtool-v2-current" -or
                $pointer.schemaVersion -ne 1 -or
                [string]::IsNullOrWhiteSpace([string]$pointer.version)) {
                throw "pointer format, schema, or version is invalid"
            }

            $versionRoot = Resolve-VersionRoot -Root $VersionsRoot -Version ([string]$pointer.version)
            if (-not [string]::IsNullOrWhiteSpace([string]$pointer.path) -and
                -not [IO.Path]::GetFullPath([string]$pointer.path).Equals(
                    $versionRoot,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "pointer path does not match its version"
            }

            return [pscustomobject]@{
                format = "comtool-v2-current"
                schemaVersion = 1
                version = [string]$pointer.version
                path = $versionRoot
                recoveredFromJunction = $false
            }
        }
        catch {
            $pointerFailure = $_.Exception.Message
        }
    }

    $link = Get-PathItemEvenIfDangling -Path $LinkPath
    if ($null -ne $link) {
        try {
            $targetFull = Assert-CurrentLinkSafe -Path $LinkPath -VersionsRoot $VersionsRoot
            $version = [IO.Path]::GetFileName($targetFull)
            return [pscustomobject]@{
                format = "comtool-v2-current"
                schemaVersion = 1
                version = $version
                path = $targetFull
                recoveredFromJunction = $true
            }
        }
        catch {
            if ($null -ne $pointerFailure) {
                throw "COM Tool V2 current-version metadata is corrupt and the stable current junction is invalid: $pointerFailure; $($_.Exception.Message)"
            }
            throw
        }
    }

    if ($null -ne $pointerFailure) {
        throw "COM Tool V2 current-version metadata is corrupt and could not be recovered from the stable current junction: $pointerFailure"
    }

    return $null
}

function Set-CurrentSelection {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string]$Version
    )

    [void](Assert-InstalledRelease -VersionRoot $VersionRoot -ExpectedVersion $Version)

    $currentLink = Join-Path $InstallRoot "current"
    $currentPointer = Join-Path $InstallRoot "current.json"
    $token = [Guid]::NewGuid().ToString("N")
    $linkTemp = Join-Path $InstallRoot ".current-$token"
    $linkBackup = Join-Path $InstallRoot ".previous-current-$token"
    $pointerTemp = $currentPointer + "." + $token + ".tmp"
    $oldMoved = $false
    $newActivated = $false

    $existing = Get-PathItemEvenIfDangling -Path $currentLink
    $null = Assert-CurrentLinkSafe -Path $currentLink -VersionsRoot (Join-Path $InstallRoot "versions")

    $pointer = [ordered]@{
        format = "comtool-v2-current"
        schemaVersion = 1
        version = $Version
        path = $VersionRoot
        selectedAt = [DateTimeOffset]::UtcNow.ToString("O")
    }

    try {
        Write-Utf8NoBom -Path $pointerTemp -Text (
            $pointer | ConvertTo-Json -Depth 4
        )
        New-Item -ItemType Junction -Path $linkTemp -Target $VersionRoot |
            Out-Null
        [void](Assert-CurrentLinkSafe -Path $linkTemp -VersionsRoot (Join-Path $InstallRoot "versions"))
        [void](Assert-InstalledRelease -VersionRoot $VersionRoot -ExpectedVersion $Version)

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
}

if ($All -and -not [string]::IsNullOrWhiteSpace($Version)) {
    throw "-All and -Version are mutually exclusive."
}
if ($RemoveState -and
    $StateRemovalToken -ne "DELETE_COMTOOL_V2_STATE") {
    throw "Deleting durable runtime state requires -StateRemovalToken DELETE_COMTOOL_V2_STATE."
}
Assert-RootsDisjoint -InstallRoot $installRootFull -StateRoot $stateRootFull
Assert-PlainDirectoryOrAbsent -Path $installRootFull -Description "Install root"
Assert-PlainDirectoryOrAbsent -Path $versionsRoot -Description "Versions root"

$installMutex = [Threading.Mutex]::new(
    $false,
    (Get-InstallMutexName -InstallRoot $installRootFull))
$mutexHeld = $false
$stateRemovalSemaphore = $null
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

    $null = Assert-CurrentLinkSafe -Path $currentLinkPath -VersionsRoot $versionsRoot

    if ($RemoveState) {
        $stateRemovalSemaphore =
            Enter-StateRemovalGuard -StateRoot $stateRootFull
    }

    $current = $null
    if (-not $All) {
        $current = Get-CurrentSelection -PointerPath $currentPath -LinkPath $currentLinkPath -VersionsRoot $versionsRoot
    }

    if (-not $All -and [string]::IsNullOrWhiteSpace($Version)) {
        if ($null -eq $current -or
            [string]::IsNullOrWhiteSpace([string]$current.version)) {
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
                Sort-Object CreationTimeUtc -Descending
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

    $versionRoot = $null
    $hasVersionsToRemove = $false
    if ($All) {
        $hasVersionsToRemove = Test-Path -LiteralPath $versionsRoot -PathType Container
    }
    else {
        $versionRoot = Resolve-VersionRoot -Root $versionsRoot -Version $Version
        $hasVersionsToRemove = Test-Path -LiteralPath $versionRoot -PathType Container
    }
    if ($hasVersionsToRemove) {
        Assert-NoInstalledComToolProcesses -InstallRoot $installRootFull
    }

    $removed = @()
    if ($All) {
        if (Test-Path -LiteralPath $versionsRoot) {
            foreach ($dir in Get-ChildItem -LiteralPath $versionsRoot -Directory) {
                $removed += $dir.Name
            }
            Remove-DirectoryTreeSafe -Path $versionsRoot
        }
    }
    elseif (Test-Path -LiteralPath $versionRoot) {
        Remove-DirectoryTreeSafe -Path $versionRoot
        $removed += $Version
    }

    $remaining = @()
    if (Test-Path -LiteralPath $versionsRoot) {
        $remaining = @(
            Get-ChildItem -LiteralPath $versionsRoot -Directory |
                Where-Object { $_.Name -match $VersionPattern } |
                Sort-Object CreationTimeUtc -Descending
        )
    }

    if ($currentRemoved) {
        if ($All -or $null -eq $fallback) {
            Remove-DirectoryJunction -Path $currentLinkPath
            if (Test-Path -LiteralPath $currentPath) {
                Remove-Item -LiteralPath $currentPath -Force
            }
        }
        else {
            Set-CurrentSelection -InstallRoot $installRootFull -VersionRoot $fallback.FullName -Version $fallback.Name
        }
    }

    $stateRemoved = $false
    if ($RemoveState) {
        if (Test-Path -LiteralPath $stateRootFull) {
            Remove-DirectoryTreeSafe -Path $stateRootFull
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
}
finally {
    if ($null -ne $stateRemovalSemaphore) {
        try {
            [void]$stateRemovalSemaphore.Release()
        }
        catch [Threading.SemaphoreFullException] {
        }
        $stateRemovalSemaphore.Dispose()
    }
    if ($mutexHeld) {
        try {
            $installMutex.ReleaseMutex()
        }
        catch [ApplicationException] {
        }
    }
    $installMutex.Dispose()
}