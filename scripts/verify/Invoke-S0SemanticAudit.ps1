<#
.SYNOPSIS
    Audits S0 boundary semantic snapshots between clean and dirty histories, or compares two snapshot JSON files offline.

.DESCRIPTION
    Supports two modes:
      1. Offline compare mode (-CompareOnly): compares -LeftSnapshot and -RightSnapshot via Compare-S0SemanticSnapshots.py,
         emitting semantic-diff.json and semantic-diff.md.
      2. Live clean-vs-dirty audit mode: runs either (-CleanPackage + -DirtyPackage) or single (-Package) baseline vs
         in-process S0 restore under full process/Mod-isolation/save-hash/backup guards with HKTAS_S0_SEMANTIC_PROBE=1,
         then pairs matching S0 boundary snapshots and runs Compare-S0SemanticSnapshots.py.
#>
[CmdletBinding()]
param(
    [switch] $CompareOnly,
    [string] $LeftSnapshot = '',
    [string] $RightSnapshot = '',
    [string] $CleanPackage = '',
    [string] $DirtyPackage = '',
    [string] $Package = '',
    [int] $TargetBoundaryOrdinal = 0,
    [string] $OutputDirectory = '',
    [switch] $IgnoreKnownTimeFields,
    [switch] $FailOnDiff,
    [string] $InstallDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compareScript = Join-Path $PSScriptRoot 'Compare-S0SemanticSnapshots.py'
if (-not (Test-Path -LiteralPath $compareScript -PathType Leaf)) {
    throw "Required compare script not found: $compareScript"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $repo ("artifacts/semantic-audit/s0-semantic-audit-$stamp")
}

function Invoke-SnapshotCompare {
    param(
        [string] $LeftPath,
        [string] $RightPath,
        [string] $JsonOut,
        [string] $MdOut,
        [bool] $IgnoreTime,
        [bool] $FailOnAnyDiff
    )
    $compareArgs = @(
        $compareScript,
        '--left', $LeftPath,
        '--right', $RightPath,
        '--output', $JsonOut,
        '--markdown-output', $MdOut
    )
    if ($IgnoreTime) {
        $compareArgs += '--ignore-known-time-fields'
    }
    if ($FailOnAnyDiff) {
        $compareArgs += '--fail-on-diff'
    }
    & python @compareArgs | Out-Host
    return $LASTEXITCODE
}

# ---------------------------------------------------------------------------
# Mode 1: Offline compare-only
# ---------------------------------------------------------------------------
if ($CompareOnly) {
    if ($Package -or $CleanPackage -or $DirtyPackage) {
        throw '-CompareOnly cannot be combined with -Package, -CleanPackage, or -DirtyPackage.'
    }
    if ([string]::IsNullOrWhiteSpace($LeftSnapshot) -or [string]::IsNullOrWhiteSpace($RightSnapshot)) {
        throw '-CompareOnly requires both -LeftSnapshot and -RightSnapshot.'
    }
    $resolvedLeft = [IO.Path]::GetFullPath($LeftSnapshot)
    $resolvedRight = [IO.Path]::GetFullPath($RightSnapshot)
    if (-not (Test-Path -LiteralPath $resolvedLeft -PathType Leaf)) {
        throw "Left snapshot not found: $resolvedLeft"
    }
    if (-not (Test-Path -LiteralPath $resolvedRight -PathType Leaf)) {
        throw "Right snapshot not found: $resolvedRight"
    }

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $jsonOut = Join-Path $outputRoot 'semantic-diff.json'
    $mdOut = Join-Path $outputRoot 'semantic-diff.md'

    $exitCode = Invoke-SnapshotCompare `
        -LeftPath $resolvedLeft `
        -RightPath $resolvedRight `
        -JsonOut $jsonOut `
        -MdOut $mdOut `
        -IgnoreTime ([bool]$IgnoreKnownTimeFields) `
        -FailOnAnyDiff ([bool]$FailOnDiff)

    if ($exitCode -eq 1 -and $FailOnDiff) {
        Write-Output "Semantic diff detected with -FailOnDiff enabled. See $mdOut"
        exit 1
    }
    if ($exitCode -ne 0) {
        throw "Compare-S0SemanticSnapshots.py failed with exit code $exitCode."
    }
    Write-Output "=== S0 Semantic Audit (CompareOnly) Complete ==="
    Write-Output "Json=$jsonOut"
    Write-Output "Markdown=$mdOut"
    exit 0
}

# ---------------------------------------------------------------------------
# Mode 2: Live clean-vs-dirty or single-package baseline-vs-restore audit
# ---------------------------------------------------------------------------
$isSinglePackage = -not [string]::IsNullOrWhiteSpace($Package)
$isDualPackage = (-not [string]::IsNullOrWhiteSpace($CleanPackage)) -or (-not [string]::IsNullOrWhiteSpace($DirtyPackage))

if ($isSinglePackage -and $isDualPackage) {
    throw 'Specify either -Package (single-package baseline vs restore) OR both -CleanPackage and -DirtyPackage, not both.'
}
if (-not $isSinglePackage -and -not $isDualPackage) {
    throw 'Specify -CompareOnly (with -LeftSnapshot/-RightSnapshot), or -Package, or both -CleanPackage and -DirtyPackage.'
}
if ($isDualPackage -and ([string]::IsNullOrWhiteSpace($CleanPackage) -or [string]::IsNullOrWhiteSpace($DirtyPackage))) {
    throw 'Dual-package mode requires both -CleanPackage and -DirtyPackage.'
}
if ($TargetBoundaryOrdinal -lt 0) {
    throw '-TargetBoundaryOrdinal must be positive when specified.'
}

$resolvedSingle = if ($isSinglePackage) { [IO.Path]::GetFullPath($Package) } else { '' }
$resolvedClean = if ($isDualPackage) { [IO.Path]::GetFullPath($CleanPackage) } else { '' }
$resolvedDirty = if ($isDualPackage) { [IO.Path]::GetFullPath($DirtyPackage) } else { '' }

foreach ($pkg in @($resolvedSingle, $resolvedClean, $resolvedDirty) | Where-Object { $_ }) {
    if (-not (Test-Path -LiteralPath $pkg -PathType Leaf)) {
        throw "Sequence package not found: $pkg"
    }
}

# Preflight process guard
$lingering = @(Get-Process hollow_knight, HollowKnightTAS.Companion, StudioScenarioHarness, WorldObservationHarness -ErrorAction SilentlyContinue)
if ($lingering.Count -gt 0) {
    throw "Preflight failed: lingering game or Studio processes found: $(($lingering | Select-Object -ExpandProperty ProcessName) -join ', ')"
}

$install = if ($InstallDirectory) {
    [IO.Path]::GetFullPath($InstallDirectory)
} else {
    'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
}
if (-not (Test-Path -LiteralPath $install -PathType Container)) {
    throw "Installed mod directory not found: $install"
}

$companion = Join-Path $install 'Companion/win-x64'
$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$privateKey = Join-Path $repo '.local/signing/companion-private.json'
$publicKey = Join-Path $repo '.local/signing/companion-public.json'
$runtimeDll = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'
$runtimeCoreDll = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'
$harnessBuild = Join-Path $repo 'scripts/harnesses/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64'
$harnessCoreDll = Join-Path $harnessBuild 'HollowKnightTAS.Core.dll'
$harnessCompanionDll = Join-Path $harnessBuild 'HollowKnightTAS.Companion.dll'
$hostFiles = @('.exe', '.dll', '.deps.json', '.runtimeconfig.json' | ForEach-Object { 'StudioScenarioHarness' + $_ })

$requiredPaths = @($bundleTool, $privateKey, $publicKey, $runtimeDll, $runtimeCoreDll, $harnessCoreDll, $harnessCompanionDll) +
    @($hostFiles | ForEach-Object { Join-Path $harnessBuild $_ })
foreach ($req in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $req -PathType Leaf)) {
        throw "Preflight failed: required build or signing asset missing: $req"
    }
}

$hashRuntimeCore = (Get-FileHash -LiteralPath $runtimeCoreDll -Algorithm SHA256).Hash
$hashHarnessCore = (Get-FileHash -LiteralPath $harnessCoreDll -Algorithm SHA256).Hash
if ($hashRuntimeCore -ne $hashHarnessCore) {
    throw "Preflight failed: HollowKnightTAS.Core.dll SHA256 mismatch between Runtime ($hashRuntimeCore) and StudioScenarioHarness ($hashHarnessCore). Rebuild both in Release."
}

Add-Type -AssemblyName Microsoft.VisualBasic
function Move-ToRecycleBin([string]$filePath) {
    if (Test-Path -LiteralPath $filePath -PathType Leaf) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
            $filePath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    }
}

$cleanedLeftover = $false
foreach ($testHost in @('StudioScenarioHarness', 'WorldObservationHarness')) {
    foreach ($suffix in @('.exe', '.dll', '.deps.json', '.runtimeconfig.json')) {
        $leftover = Join-Path $companion ($testHost + $suffix)
        if (Test-Path -LiteralPath $leftover -PathType Leaf) {
            Move-ToRecycleBin $leftover
            $cleanedLeftover = $true
        }
    }
}
if ($cleanedLeftover -and (Test-Path -LiteralPath (Join-Path $install 'companion.manifest.json') -PathType Leaf)) {
    & dotnet $bundleTool sign $privateKey $install (Join-Path $install 'companion.manifest.json') | Out-Null
}
if (Test-Path -LiteralPath (Join-Path $install 'companion.manifest.json') -PathType Leaf) {
    & dotnet $bundleTool verify $publicKey $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) {
        throw 'Preflight failed: installed companion.manifest.json signature is invalid before audit run.'
    }
}

if (Test-Path -LiteralPath $outputRoot) {
    throw "Output directory already exists; specify a fresh OutputDirectory: $outputRoot"
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$OnlyTasMods = $true
$modRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($install))
$modQuarantine = Join-Path (Join-Path $modRoot 'disabled') ('__HKTAS_S0SemanticAudit_' + [Guid]::NewGuid().ToString('N'))
$modMoves = [Collections.Generic.List[object]]::new()
$modIsolationStatus = 'not-requested'
. (Join-Path $PSScriptRoot 'LoadingProbeModIsolation.ps1')

$saveRoot = Join-Path $env:USERPROFILE 'AppData/LocalLow/Team Cherry/Hollow Knight'
function Read-SaveAudit {
    $files = if (Test-Path -LiteralPath $saveRoot -PathType Container) {
        @(Get-ChildItem -LiteralPath $saveRoot -File -Filter 'user*' | Sort-Object Name)
    } else {
        @()
    }
    @($files | ForEach-Object {
        [ordered]@{ name = $_.Name; length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) | ConvertTo-Json -Depth 4 -Compress
}

$backup = Join-Path $outputRoot 'installation-backup'
New-Item -ItemType Directory -Force -Path $backup | Out-Null
$managedTargets = @(
    @{ backupName = 'HollowKnightTAS.dll'; source = $runtimeDll; destination = (Join-Path $install 'HollowKnightTAS.dll') },
    @{ backupName = 'runtime-core.dll'; source = $runtimeCoreDll; destination = (Join-Path $install 'HollowKnightTAS.Core.dll') },
    @{ backupName = 'studio-core.dll'; source = $harnessCoreDll; destination = (Join-Path $companion 'HollowKnightTAS.Core.dll') },
    @{ backupName = 'studio.dll'; source = $harnessCompanionDll; destination = (Join-Path $companion 'HollowKnightTAS.Companion.dll') }
)
foreach ($item in $managedTargets) {
    if (-not (Test-Path -LiteralPath $item.destination -PathType Leaf)) {
        throw "Installed target missing for backup: $($item.destination)"
    }
    Copy-Item -LiteralPath $item.destination -Destination (Join-Path $backup $item.backupName)
}
Copy-Item -LiteralPath (Join-Path $install 'companion.manifest.json') -Destination (Join-Path $backup 'companion.manifest.json')

$beforeSaves = Read-SaveAudit
[IO.File]::WriteAllText((Join-Path $outputRoot 'saves-before.json'), $beforeSaves, [Text.UTF8Encoding]::new($false))

$prevSemanticProbe = $env:HKTAS_S0_SEMANTIC_PROBE
$prevSemanticDir = $env:HKTAS_S0_SEMANTIC_PROBE_DIR
$prevSemanticSegment = $env:HKTAS_S0_SEMANTIC_PROBE_SEGMENT
$prevTraceEnv = $env:HKTAS_ACCEPTANCE_FRAME_TRACE
$hostProcess = $null

function Invoke-HarnessRun {
    param(
        [string] $RunLabel,
        [string] $PackagePath,
        [string] $ProbeDir,
        [string] $ProbeSegment
    )
    $runDir = Join-Path $outputRoot ("run-$RunLabel")
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    New-Item -ItemType Directory -Force -Path $ProbeDir | Out-Null

    $env:HKTAS_S0_SEMANTIC_PROBE = '1'
    $env:HKTAS_S0_SEMANTIC_PROBE_DIR = $ProbeDir
    $env:HKTAS_S0_SEMANTIC_PROBE_SEGMENT = $ProbeSegment
    $env:HKTAS_ACCEPTANCE_FRAME_TRACE = '1'

    Write-Output "=== Running S0 Semantic Audit Harness ($RunLabel, segment=$ProbeSegment) ==="
    $harnessArgs = @(
        '--headless',
        '--s0-acceptance',
        ('--scenario-output="{0}"' -f $runDir),
        ('--acceptance-package="{0}"' -f $PackagePath),
        ('--acceptance-suite="{0}"' -f ("semantic-audit-$RunLabel"))
    )
    $stdoutPath = Join-Path $runDir 'harness-stdout.log'
    $stderrPath = Join-Path $runDir 'harness-stderr.log'
    $script:hostProcess = Start-Process -FilePath (Join-Path $companion 'StudioScenarioHarness.exe') `
        -ArgumentList $harnessArgs `
        -WorkingDirectory $companion `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath

    $timeoutMs = 3800000
    $timedOut = -not $script:hostProcess.WaitForExit($timeoutMs)
    if ($timedOut) {
        try { $script:hostProcess.Kill() } catch {}
        $script:hostProcess.WaitForExit()
    }
    $script:hostProcess.Refresh()
    $exitCode = if ($timedOut) { -1 } else { $script:hostProcess.ExitCode }
    $script:hostProcess = $null

    foreach ($logName in @('Player.log', 'ModLog.txt')) {
        $logSrc = Join-Path $saveRoot $logName
        if (Test-Path -LiteralPath $logSrc -PathType Leaf) {
            Copy-Item -LiteralPath $logSrc -Destination (Join-Path $runDir $logName) -Force
        }
    }

    return [ordered]@{
        runLabel = $RunLabel
        runDir = $runDir
        probeDir = $ProbeDir
        segment = $ProbeSegment
        exitCode = $exitCode
        timedOut = $timedOut
    }
}

$runRecords = [Collections.Generic.List[object]]::new()
$probeRoot = Join-Path $outputRoot 'semantic-snapshots'

try {
    Isolate-OtherMods
    foreach ($item in $managedTargets) {
        Copy-Item -LiteralPath $item.source -Destination $item.destination -Force
    }
    foreach ($name in $hostFiles) {
        Copy-Item -LiteralPath (Join-Path $harnessBuild $name) -Destination (Join-Path $companion $name) -Force
    }
    & dotnet $bundleTool sign $privateKey $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signing failed.' }
    & dotnet $bundleTool verify $publicKey $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature verification failed.' }

    if ($isDualPackage) {
        $runRecords.Add((Invoke-HarnessRun -RunLabel 'clean' -PackagePath $resolvedClean -ProbeDir (Join-Path $probeRoot 'clean') -ProbeSegment 'clean'))
        $runRecords.Add((Invoke-HarnessRun -RunLabel 'dirty' -PackagePath $resolvedDirty -ProbeDir (Join-Path $probeRoot 'dirty') -ProbeSegment 'dirty'))
    } else {
        $runRecords.Add((Invoke-HarnessRun -RunLabel 'single' -PackagePath $resolvedSingle -ProbeDir (Join-Path $probeRoot 'single') -ProbeSegment 'baseline'))
    }
}
finally {
    $env:HKTAS_S0_SEMANTIC_PROBE = $prevSemanticProbe
    $env:HKTAS_S0_SEMANTIC_PROBE_DIR = $prevSemanticDir
    $env:HKTAS_S0_SEMANTIC_PROBE_SEGMENT = $prevSemanticSegment
    $env:HKTAS_ACCEPTANCE_FRAME_TRACE = $prevTraceEnv

    if ($hostProcess -and -not $hostProcess.HasExited) {
        try { $hostProcess.Kill() } catch {}
        $hostProcess.WaitForExit()
    }

    $remainingGame = @(Get-Process hollow_knight -ErrorAction SilentlyContinue)
    if ($remainingGame.Count -gt 0) {
        if ($OnlyTasMods) {
            $script:modIsolationStatus = 'retained-game-running'
            Write-ModIsolation
        }
        Write-Warning "Game process remains running (PIDs: $(($remainingGame | Select-Object -ExpandProperty Id) -join ', ')); skipping installation restore to avoid corrupting active files."
    } else {
        Restore-OtherMods
        foreach ($item in $managedTargets) {
            Copy-Item -LiteralPath (Join-Path $backup $item.backupName) -Destination $item.destination -Force
        }
        Copy-Item -LiteralPath (Join-Path $backup 'companion.manifest.json') -Destination (Join-Path $install 'companion.manifest.json') -Force
        foreach ($name in $hostFiles) {
            Move-ToRecycleBin (Join-Path $companion $name)
        }
        & dotnet $bundleTool verify $publicKey $install (Join-Path $install 'companion.manifest.json')
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'Final installation signature verification failed after restore!'
        }
    }

    $afterSaves = Read-SaveAudit
    [IO.File]::WriteAllText((Join-Path $outputRoot 'saves-after.json'), $afterSaves, [Text.UTF8Encoding]::new($false))
}

$savesClean = ($afterSaves -eq $beforeSaves)
if (-not $savesClean) {
    throw "Player save audit detected unexpected save file changes! See $outputRoot"
}

function Read-SnapshotEntries([string]$dirPath) {
    if (-not (Test-Path -LiteralPath $dirPath -PathType Container)) {
        return @()
    }
    $files = @(Get-ChildItem -LiteralPath $dirPath -File -Filter '*s0-semantic-boundary-*.json' | Sort-Object Name)
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($f in $files) {
        $obj = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json -AsHashtable
        $meta = if ($obj -and $obj.ContainsKey('meta') -and $null -ne $obj['meta']) { $obj['meta'] } else { @{} }
        $entries.Add([pscustomobject]@{
            path = $f.FullName
            name = $f.Name
            ordinal = if ($null -ne $meta['ordinal']) { [int]$meta['ordinal'] } else { -1 }
            segment = [string]$meta['segment']
            sourceScene = [string]$meta['sourceScene']
            targetScene = [string]$meta['targetScene']
            entryGate = [string]$meta['entryGate']
            movieFrame = [long]$meta['movieFrame']
            nativeFrame = [long]$meta['nativeFrame']
        })
    }
    return @($entries)
}

function Get-TransitionKey($entry) {
    return "$($entry.sourceScene)|$($entry.targetScene)|$($entry.entryGate)"
}

$pairs = [Collections.Generic.List[object]]::new()

if ($isDualPackage) {
    $cleanEntries = @(Read-SnapshotEntries (Join-Path $probeRoot 'clean') | Where-Object { $_.segment -eq 'clean' -or $_.name.StartsWith('clean-') })
    $dirtyEntries = @(Read-SnapshotEntries (Join-Path $probeRoot 'dirty') | Where-Object { $_.segment -eq 'dirty' -or $_.name.StartsWith('dirty-') })

    if ($TargetBoundaryOrdinal -gt 0) {
        $dirtyTargets = @($dirtyEntries | Where-Object { $_.ordinal -eq $TargetBoundaryOrdinal })
        $cleanTargets = @($cleanEntries | Where-Object { $_.ordinal -eq $TargetBoundaryOrdinal })
        if ($cleanTargets.Count -gt 0 -and $dirtyTargets.Count -gt 0) {
            $pairs.Add([pscustomobject]@{ left = $cleanTargets[0]; right = $dirtyTargets[0]; pairId = ("ordinal-{0:d2}" -f $TargetBoundaryOrdinal) })
        } elseif ($dirtyTargets.Count -gt 0) {
            $tKey = Get-TransitionKey $dirtyTargets[0]
            $byScene = @($cleanEntries | Where-Object { (Get-TransitionKey $_) -eq $tKey })
            if ($byScene.Count -gt 0) {
                $pairs.Add([pscustomobject]@{ left = $byScene[0]; right = $dirtyTargets[0]; pairId = ("ordinal-{0:d2}" -f $TargetBoundaryOrdinal) })
            }
        } elseif ($cleanTargets.Count -gt 0) {
            $tKey = Get-TransitionKey $cleanTargets[0]
            $byScene = @($dirtyEntries | Where-Object { (Get-TransitionKey $_) -eq $tKey })
            if ($byScene.Count -gt 0) {
                $pairs.Add([pscustomobject]@{ left = $cleanTargets[0]; right = $byScene[0]; pairId = ("ordinal-{0:d2}" -f $TargetBoundaryOrdinal) })
            }
        }
    } else {
        $usedDirty = [Collections.Generic.HashSet[string]]::new()
        foreach ($c in $cleanEntries) {
            $cKey = Get-TransitionKey $c
            $match = @($dirtyEntries | Where-Object { -not $usedDirty.Contains($_.path) -and $_.ordinal -eq $c.ordinal -and (Get-TransitionKey $_) -eq $cKey }) | Select-Object -First 1
            if ($null -eq $match) {
                $match = @($dirtyEntries | Where-Object { -not $usedDirty.Contains($_.path) -and (Get-TransitionKey $_) -eq $cKey }) | Select-Object -First 1
            }
            if ($null -eq $match) {
                $match = @($dirtyEntries | Where-Object { -not $usedDirty.Contains($_.path) -and $_.ordinal -eq $c.ordinal }) | Select-Object -First 1
            }
            if ($null -ne $match) {
                [void]$usedDirty.Add($match.path)
                $pairs.Add([pscustomobject]@{ left = $c; right = $match; pairId = ("boundary-{0:d2}-{1}-to-{2}" -f $c.ordinal, $c.sourceScene, $c.targetScene) })
            }
        }
    }
} else {
    $allSingle = @(Read-SnapshotEntries (Join-Path $probeRoot 'single'))
    $baselineEntries = @($allSingle | Where-Object { $_.segment -eq 'baseline' -or $_.name.StartsWith('baseline-') })
    $restoreEntries = @($allSingle | Where-Object { $_.segment.StartsWith('s0-restore-') -or $_.name.StartsWith('s0-restore-') })

    $filteredRestores = if ($TargetBoundaryOrdinal -gt 0) {
        @($restoreEntries | Where-Object { $_.ordinal -eq $TargetBoundaryOrdinal })
    } else {
        $restoreEntries
    }

    $pairIdx = 0
    foreach ($r in $filteredRestores) {
        $rKey = Get-TransitionKey $r
        $baseMatch = @($baselineEntries | Where-Object { $_.ordinal -eq $r.ordinal -and (Get-TransitionKey $_) -eq $rKey }) | Select-Object -First 1
        if ($null -eq $baseMatch) {
            $baseMatch = @($baselineEntries | Where-Object { $_.ordinal -eq $r.ordinal }) | Select-Object -First 1
        }
        if ($null -eq $baseMatch) {
            $baseMatch = @($baselineEntries | Where-Object { (Get-TransitionKey $_) -eq $rKey }) | Select-Object -First 1
        }
        if ($null -ne $baseMatch) {
            $pairIdx++
            $safeSeg = ($r.segment -replace '[:/\\\s]+', '-')
            $pairs.Add([pscustomobject]@{ left = $baseMatch; right = $r; pairId = ("{0:d2}-{1}-ord-{2:d2}" -f $pairIdx, $safeSeg, $r.ordinal) })
        }
    }
}

if ($pairs.Count -eq 0) {
    throw "No matching S0 semantic boundary pairs found under $probeRoot (TargetBoundaryOrdinal=$TargetBoundaryOrdinal)."
}

$comparisonsDir = Join-Path $outputRoot 'comparisons'
New-Item -ItemType Directory -Force -Path $comparisonsDir | Out-Null

$pairResults = [Collections.Generic.List[object]]::new()
$anyDiffs = $false

foreach ($pair in $pairs) {
    $pairDir = Join-Path $comparisonsDir $pair.pairId
    New-Item -ItemType Directory -Force -Path $pairDir | Out-Null
    $pairJson = Join-Path $pairDir 'semantic-diff.json'
    $pairMd = Join-Path $pairDir 'semantic-diff.md'

    $cmpCode = Invoke-SnapshotCompare `
        -LeftPath $pair.left.path `
        -RightPath $pair.right.path `
        -JsonOut $pairJson `
        -MdOut $pairMd `
        -IgnoreTime ([bool]$IgnoreKnownTimeFields) `
        -FailOnAnyDiff $false
    if ($cmpCode -ne 0) {
        throw "Compare-S0SemanticSnapshots.py failed for $($pair.pairId) with exit code $cmpCode."
    }

    $diffObj = Get-Content -LiteralPath $pairJson -Raw | ConvertFrom-Json -AsHashtable
    $diffCount = [int]$diffObj['totalDiffCount']
    if ($diffCount -gt 0) {
        $anyDiffs = $true
    }
    $pairResults.Add([ordered]@{
        pairId = $pair.pairId
        leftSnapshot = $pair.left.path
        rightSnapshot = $pair.right.path
        ordinalLeft = $pair.left.ordinal
        ordinalRight = $pair.right.ordinal
        transition = "$($pair.left.sourceScene) -> $($pair.left.targetScene)"
        totalDiffCount = $diffCount
        ignoredDiffCount = [int]$diffObj['ignoredDiffCount']
        diffCountByDomain = $diffObj['diffCountByDomain']
        passed = ($diffCount -eq 0)
        jsonPath = $pairJson
        markdownPath = $pairMd
    })
}

if ($pairResults.Count -eq 1) {
    Copy-Item -LiteralPath $pairResults[0].jsonPath -Destination (Join-Path $outputRoot 'semantic-diff.json') -Force
    Copy-Item -LiteralPath $pairResults[0].markdownPath -Destination (Join-Path $outputRoot 'semantic-diff.md') -Force
}

$allClean = (-not $anyDiffs) -and $savesClean
$auditSummary = [ordered]@{
    mode = if ($isDualPackage) { 'dual-package' } else { 'single-package-restore' }
    targetBoundaryOrdinal = $TargetBoundaryOrdinal
    ignoreKnownTimeFields = [bool]$IgnoreKnownTimeFields
    savesUnchanged = $savesClean
    modIsolationStatus = $modIsolationStatus
    passed = $allClean
    runs = $runRecords
    comparisons = $pairResults
}
$auditSummaryPath = Join-Path $outputRoot 'audit-summary.json'
$auditSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $auditSummaryPath -Encoding utf8

$mdLines = [Collections.Generic.List[string]]::new()
$mdLines.Add('# S0 Semantic Audit Summary')
$mdLines.Add('')
$mdLines.Add("- **Result**: **$(if ($allClean) { 'PASS' } else { 'FAIL' })**")
$mdLines.Add("- **Mode**: ``$($auditSummary.mode)``")
$mdLines.Add("- **Ignore Known Time Fields**: ``$([bool]$IgnoreKnownTimeFields)``")
$mdLines.Add("- **Saves Unchanged**: ``$savesClean``")
$mdLines.Add("- **Mod Isolation Status**: ``$modIsolationStatus``")
$mdLines.Add('')
$mdLines.Add('| Pair ID | Transition | Left Ord | Right Ord | Diffs | Ignored | Status | Report |')
$mdLines.Add('| --- | --- | ---: | ---: | ---: | ---: | --- | --- |')
foreach ($pr in $pairResults) {
    $st = if ($pr.passed) { 'PASS' } else { 'FAIL' }
    $mdLines.Add("| ``$($pr.pairId)`` | ``$($pr.transition)`` | $($pr.ordinalLeft) | $($pr.ordinalRight) | $($pr.totalDiffCount) | $($pr.ignoredDiffCount) | **$st** | ``comparisons/$($pr.pairId)/semantic-diff.md`` |")
}
$mdLines.Add('')
$auditMdPath = Join-Path $outputRoot 'audit-summary.md'
[IO.File]::WriteAllText($auditMdPath, ($mdLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

Write-Output "=== S0 Semantic Audit Complete ==="
Write-Output "Passed=$allClean Pairs=$($pairResults.Count) SavesUnchanged=$savesClean Summary=$auditMdPath"

if ($FailOnDiff -and $anyDiffs) {
    exit 1
}
exit 0
