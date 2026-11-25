<#
.SYNOPSIS
    Sandbox runner for testing third-party Mod compatibility with S0 acceptance suites.

.DESCRIPTION
    Temporarily stages requested Mods (from Mods/disabled or the mod-compat cache) into Mods/,
    executes Invoke-S0AcceptanceSuite.ps1 with -KeepMods, and moves ONLY newly staged directories
    to the Windows Recycle Bin in the finally block so the original Mods/ directory is restored.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $ModNames,

    [string] $CacheDir = 'artifacts/mod-compat/cache',

    [ValidateSet('All', 'Tas1', 'Tas2', 'Custom')]
    [string] $Suite = 'Tas1',

    [string] $CustomPackage = '',

    [string] $CustomSuiteName = '',

    [string] $OutputDirectory = '',

    [string] $ModsRoot = 'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed\Mods'
)

$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedModsRoot = [IO.Path]::GetFullPath($ModsRoot)
if (-not (Test-Path -LiteralPath $resolvedModsRoot -PathType Container)) {
    throw "ModsRoot directory not found: $resolvedModsRoot"
}

$resolvedCacheDir = if ([IO.Path]::IsPathRooted($CacheDir)) {
    [IO.Path]::GetFullPath($CacheDir)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $CacheDir))
}

$disabledRoot = Join-Path $resolvedModsRoot 'disabled'
$acceptanceScript = Join-Path $PSScriptRoot 'Invoke-S0AcceptanceSuite.ps1'
if (-not (Test-Path -LiteralPath $acceptanceScript -PathType Leaf)) {
    throw "Acceptance suite script not found: $acceptanceScript"
}

Add-Type -AssemblyName Microsoft.VisualBasic

function Move-DirectoryToRecycleBin([string]$dirPath) {
    if (Test-Path -LiteralPath $dirPath -PathType Container) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
            $dirPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    }
}

function Find-MatchingModDirectory([string]$parentDir, [string]$targetName) {
    if (-not (Test-Path -LiteralPath $parentDir -PathType Container)) {
        return $null
    }
    $dirs = @(Get-ChildItem -LiteralPath $parentDir -Directory -Force | Where-Object {
        $_.Name -ne 'disabled' -and $_.Name -notlike '__HKTAS_*'
    })
    foreach ($d in $dirs) {
        if ([string]::Equals($d.Name, $targetName, [StringComparison]::OrdinalIgnoreCase)) {
            return $d
        }
    }
    $normalizedTarget = $targetName -replace '\s+', ''
    foreach ($d in $dirs) {
        if ([string]::Equals(($d.Name -replace '\s+', ''), $normalizedTarget, [StringComparison]::OrdinalIgnoreCase)) {
            return $d
        }
    }
    return $null
}

# 1. Snapshot original top-level directories in Mods/
$originalTopDirs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($d in @(Get-ChildItem -LiteralPath $resolvedModsRoot -Directory -Force)) {
    [void]$originalTopDirs.Add($d.Name)
}

$requestedMods = [System.Collections.Generic.List[string]]::new()
$seenRequested = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($rawName in $ModNames) {
    if ([string]::IsNullOrWhiteSpace($rawName)) { continue }
    $trimmed = $rawName.Trim()
    if ([string]::Equals($trimmed, 'disabled', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($trimmed, 'HollowKnightTAS', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing reserved directory name in -ModNames: $trimmed"
    }
    if ($seenRequested.Add($trimmed)) {
        $requestedMods.Add($trimmed)
    }
}
if ($requestedMods.Count -eq 0) {
    throw "At least one mod name must be specified in -ModNames."
}

$stagedDirs = [System.Collections.Generic.List[string]]::new()
$keepList = [System.Collections.Generic.List[string]]::new()
$keepSeen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$exitCode = 0

try {
    # 2. Stage only mods that are not already present in Mods/
    foreach ($modName in $requestedMods) {
        $existingInMods = Find-MatchingModDirectory -parentDir $resolvedModsRoot -targetName $modName
        if ($null -ne $existingInMods) {
            if ($keepSeen.Add($existingInMods.Name)) {
                $keepList.Add($existingInMods.Name)
            }
            Write-Host "[ModCompat] Already present in Mods/: $($existingInMods.Name) (will keep without modifying)"
            continue
        }

        $sourceDir = Find-MatchingModDirectory -parentDir $disabledRoot -targetName $modName
        $sourceKind = 'Mods/disabled'
        if ($null -eq $sourceDir) {
            $sourceDir = Find-MatchingModDirectory -parentDir $resolvedCacheDir -targetName $modName
            $sourceKind = 'cache'
        }
        if ($null -eq $sourceDir) {
            throw "Requested mod '$modName' was not found in Mods/, '$disabledRoot', or '$resolvedCacheDir'."
        }

        $destPath = Join-Path $resolvedModsRoot $sourceDir.Name
        if ($originalTopDirs.Contains($sourceDir.Name) -or (Test-Path -LiteralPath $destPath)) {
            throw "Refusing to overwrite existing entry in Mods/: $destPath"
        }

        Copy-Item -LiteralPath $sourceDir.FullName -Destination $destPath -Recurse -Force
        $stagedDirs.Add($destPath)
        if ($keepSeen.Add($sourceDir.Name)) {
            $keepList.Add($sourceDir.Name)
        }
        Write-Host "[ModCompat] Staged $($sourceDir.Name) from $sourceKind -> $destPath"
    }

    # 3. Execute S0 acceptance suite with -KeepMods
    $invokeParams = @{
        Suite            = $Suite
        InstallDirectory = (Join-Path $resolvedModsRoot 'HollowKnightTAS')
        KeepMods         = @($keepList.ToArray())
    }
    if (-not [string]::IsNullOrWhiteSpace($CustomPackage)) {
        $invokeParams['CustomPackage'] = $CustomPackage
    }
    if (-not [string]::IsNullOrWhiteSpace($CustomSuiteName)) {
        $invokeParams['CustomSuiteName'] = $CustomSuiteName
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $invokeParams['OutputDirectory'] = $OutputDirectory
    }

    & $acceptanceScript @invokeParams
    if ($null -ne $LASTEXITCODE) {
        $exitCode = [int]$LASTEXITCODE
    }
}
finally {
    # 4. Recycle ONLY directories staged by this invocation
    foreach ($staged in $stagedDirs) {
        $stagedFull = [IO.Path]::GetFullPath($staged)
        $parentFull = [IO.Path]::GetDirectoryName($stagedFull)
        $leafName = [IO.Path]::GetFileName($stagedFull)

        if (-not [string]::Equals($parentFull, $resolvedModsRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Refusing to clean path outside ModsRoot: $stagedFull"
            continue
        }
        if ($originalTopDirs.Contains($leafName) -or
            [string]::Equals($leafName, 'disabled', [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($leafName, 'HollowKnightTAS', [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Refusing to clean pre-existing or protected directory: $stagedFull"
            continue
        }

        if (Test-Path -LiteralPath $stagedFull -PathType Container) {
            Move-DirectoryToRecycleBin $stagedFull
            Write-Host "[ModCompat] Recycled staged directory: $stagedFull"
        }
    }
}

exit $exitCode
