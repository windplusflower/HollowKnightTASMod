<#
.SYNOPSIS
    Diagnose the first divergence of a failed S0 acceptance run by re-running with targeted
    RNG/state diagnostics or diffing an existing acceptance-rng-diag.jsonl.

.DESCRIPTION
    Reads report.json from a failed acceptance output directory (top-level or single-suite),
    extracts the firstDivergence (scene, movieFrame) of the failing S0 checkpoint, and either:
      - Reproduces the run via StudioScenarioHarness.exe with HKTAS_ACCEPTANCE_RNG_DIAG_SCENE
        and HKTAS_ACCEPTANCE_RNG_DIAG_FRAMES set (under full Mod isolation & save protection),
        then runs Diff-AcceptanceRngDiag.py; or
      - With -DiffOnly, directly runs Diff-AcceptanceRngDiag.py on an existing
        acceptance-rng-diag.jsonl in the acceptance directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AcceptanceDir,
    [string] $Suite = '',
    [string] $CheckpointId = '',
    [int] $FramePaddingBefore = 2,
    [int] $FramePaddingAfter = 1,
    [string] $OutputDirectory = '',
    [switch] $DiffOnly,
    [string] $InstallDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$diffScript = Join-Path $PSScriptRoot 'Diff-AcceptanceRngDiag.py'
if (-not (Test-Path -LiteralPath $diffScript -PathType Leaf)) {
    throw "Required script not found: $diffScript"
}

$acceptanceRoot = [IO.Path]::GetFullPath($AcceptanceDir)
if (-not (Test-Path -LiteralPath $acceptanceRoot -PathType Container)) {
    throw "Acceptance directory not found: $acceptanceRoot"
}

$topReportPath = Join-Path $acceptanceRoot 'report.json'
if (-not (Test-Path -LiteralPath $topReportPath -PathType Leaf)) {
    throw "report.json not found in AcceptanceDir: $topReportPath"
}

$topReport = Get-Content -Raw -LiteralPath $topReportPath | ConvertFrom-Json
$suiteDir = $acceptanceRoot
$suiteReport = $null
$suiteKey = ''
$pkgPath = ''

if ($null -ne $topReport.PSObject.Properties['suites']) {
    $suites = @($topReport.suites)
    $selectedSuite = if ($Suite) {
        $suites | Where-Object { $_.suite -eq $Suite } | Select-Object -First 1
    } else {
        $suites | Where-Object { -not [bool]$_.passed } | Select-Object -First 1
    }
    if ($null -eq $selectedSuite) {
        if ($Suite) {
            throw "Suite '$Suite' not found in $topReportPath"
        }
        throw "No failed suite found in $topReportPath"
    }
    $suiteKey = [string]$selectedSuite.suite
    $pkgPath = [string]$selectedSuite.package
    $candidateReportPath = if ($selectedSuite.reportPath) { [string]$selectedSuite.reportPath } else { '' }
    if ($candidateReportPath -and (Test-Path -LiteralPath $candidateReportPath -PathType Leaf)) {
        $suiteDir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($candidateReportPath))
    } else {
        $suiteDir = Join-Path $acceptanceRoot $suiteKey
    }
    $suiteReportPath = Join-Path $suiteDir 'report.json'
    if (-not (Test-Path -LiteralPath $suiteReportPath -PathType Leaf)) {
        throw "Suite report.json not found: $suiteReportPath"
    }
    $suiteReport = Get-Content -Raw -LiteralPath $suiteReportPath | ConvertFrom-Json
} else {
    $suiteReport = $topReport
    $suiteKey = [string]$suiteReport.suite
    if ($Suite -and $suiteKey -ne $Suite) {
        throw "Single-suite report is for '$suiteKey', which does not match -Suite '$Suite'."
    }
}

if (-not $pkgPath) {
    foreach ($metaName in @('s0-runs.json', 'baseline-checkpoints.json')) {
        $metaPath = Join-Path $suiteDir $metaName
        if (Test-Path -LiteralPath $metaPath -PathType Leaf) {
            $metaObj = Get-Content -Raw -LiteralPath $metaPath | ConvertFrom-Json
            if ($metaObj.packagePath) {
                $pkgPath = [string]$metaObj.packagePath
                break
            }
        }
    }
}
if (-not $pkgPath -and $suiteKey) {
    $fallbackPkg = Join-Path $repo (".local/acceptance-tas/$suiteKey.hktaspack")
    if (Test-Path -LiteralPath $fallbackPkg -PathType Leaf) {
        $pkgPath = $fallbackPkg
    }
}

$s0Results = @($suiteReport.s0Results)
if ($s0Results.Count -eq 0) {
    throw "No s0Results entries found in $(Join-Path $suiteDir 'report.json')."
}

$targetS0 = if ($CheckpointId) {
    $exact = $s0Results | Where-Object { $_.checkpointId -eq $CheckpointId } | Select-Object -First 1
    if ($null -ne $exact) {
        $exact
    } else {
        $s0Results | Where-Object { [string]$_.checkpointId -like "$CheckpointId*" } | Select-Object -First 1
    }
} else {
    $s0Results | Where-Object { -not [bool]$_.passed } | Select-Object -First 1
}

if ($null -eq $targetS0) {
    if ($CheckpointId) {
        throw "CheckpointId '$CheckpointId' not found in $(Join-Path $suiteDir 'report.json')."
    }
    throw "No failed S0 checkpoint found in $(Join-Path $suiteDir 'report.json')."
}

$fd = $targetS0.firstDivergence
$divFrame = [long]-1
$diagScene = ''
if ($null -ne $fd -and $null -ne $fd.movieFrame) {
    $divFrame = [long]$fd.movieFrame
    $diagScene = if (-not [string]::IsNullOrWhiteSpace([string]$fd.baselineScene)) {
        [string]$fd.baselineScene
    } else {
        [string]$fd.scene
    }
} elseif ($targetS0.sampleFailures -and @($targetS0.sampleFailures).Count -gt 0) {
    $sf0 = @($targetS0.sampleFailures)[0]
    $divFrame = [long]$sf0.movieFrame
    $diagScene = [string]$sf0.scene
} else {
    throw "Target S0 '$($targetS0.checkpointId)' does not contain firstDivergence or sampleFailures. Re-run Compare-S0Acceptance.py first."
}

$minFrame = [Math]::Max([long]0, $divFrame - [long]$FramePaddingBefore)
$maxFrame = $divFrame + [long]$FramePaddingAfter
$diagFramesSpec = "$minFrame-$maxFrame"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $acceptanceRoot ("diagnose-$stamp")
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$targetRestoreSegment = if ($targetS0.resolvedSegmentName) {
    [string]$targetS0.resolvedSegmentName
} elseif ($targetS0.segmentName) {
    [string]$targetS0.segmentName
} else {
    ''
}

function Invoke-DiffRngDiag {
    param(
        [string] $DiagPath,
        [string] $TracePath,
        [string] $PreferredRestoreSegment,
        [string] $OutDir
    )
    $diffArgs = @(
        $diffScript,
        '--diag', $DiagPath,
        '--out-dir', $OutDir
    )
    if ($TracePath -and (Test-Path -LiteralPath $TracePath -PathType Leaf)) {
        $diffArgs += @('--trace', $TracePath)
    }
    if ($PreferredRestoreSegment) {
        $hasPreferred = $false
        foreach ($line in [IO.File]::ReadLines($DiagPath)) {
            if ($line.Contains("`"$PreferredRestoreSegment`"")) {
                $hasPreferred = $true
                break
            }
        }
        if ($hasPreferred) {
            $diffArgs += @('--restore-segment', $PreferredRestoreSegment)
        }
    }
    & python @diffArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Diff-AcceptanceRngDiag.py failed with exit code $LASTEXITCODE."
    }
}

$diagnoseMeta = [ordered]@{
    suite = $suiteKey
    checkpointId = [string]$targetS0.checkpointId
    targetRestoreSegment = $targetRestoreSegment
    firstDivergenceFrame = $divFrame
    diagScene = $diagScene
    diagFrames = $diagFramesSpec
    diffOnly = [bool]$DiffOnly
    acceptanceDir = $acceptanceRoot
    suiteDir = $suiteDir
    outputDir = $outputRoot
}

if ($DiffOnly) {
    $existingDiag = @($suiteDir, $acceptanceRoot, $outputRoot) |
        ForEach-Object { Join-Path $_ 'acceptance-rng-diag.jsonl' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $existingDiag) {
        throw "DiffOnly requested, but acceptance-rng-diag.jsonl was not found in '$suiteDir' or '$acceptanceRoot'."
    }
    $existingTrace = @($suiteDir, $acceptanceRoot, $outputRoot) |
        ForEach-Object { Join-Path $_ 'acceptance-frame-trace.csv' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    $diagnoseMeta['diagPath'] = $existingDiag
    $diagnoseMeta['tracePath'] = $existingTrace
    $diagnoseMeta | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'diagnose-meta.json') -Encoding utf8

    Invoke-DiffRngDiag -DiagPath $existingDiag -TracePath $existingTrace -PreferredRestoreSegment $targetRestoreSegment -OutDir $outputRoot
    Write-Output "=== S0 Divergence Diagnose (DiffOnly) Complete ==="
    Write-Output "Report=$(Join-Path $outputRoot 'divergence-report.md')"
    exit 0
}

# Full targeted reproduction run (same process check, Mod isolation & save protection as Invoke-S0AcceptanceSuite.ps1)
$lingering = @(Get-Process hollow_knight, HollowKnightTAS.Companion, StudioScenarioHarness, WorldObservationHarness -ErrorAction SilentlyContinue)
if ($lingering.Count -gt 0) {
    throw "Preflight failed: lingering game or Studio processes found: $(($lingering | Select-Object -ExpandProperty ProcessName) -join ', ')"
}

if (-not $pkgPath -or -not (Test-Path -LiteralPath $pkgPath -PathType Leaf)) {
    throw "Acceptance sequence package not found: '$pkgPath'"
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
        throw "Preflight failed: installed companion.manifest.json signature is invalid before diagnose run."
    }
}

$OnlyTasMods = $true
$modRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($install))
$modQuarantine = Join-Path (Join-Path $modRoot 'disabled') ('__HKTAS_S0Diagnose_' + [Guid]::NewGuid().ToString('N'))
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
$diagnoseMeta['packagePath'] = $pkgPath
$diagnoseMeta | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'diagnose-meta.json') -Encoding utf8

$prevTraceEnv = $env:HKTAS_ACCEPTANCE_FRAME_TRACE
$prevDiagSceneEnv = $env:HKTAS_ACCEPTANCE_RNG_DIAG_SCENE
$prevDiagFramesEnv = $env:HKTAS_ACCEPTANCE_RNG_DIAG_FRAMES
$hostProcess = $null
$harnessExit = -1

try {
    Isolate-OtherMods
    foreach ($item in $managedTargets) {
        Copy-Item -LiteralPath $item.source -Destination $item.destination -Force
    }
    foreach ($name in $hostFiles) {
        Copy-Item -LiteralPath (Join-Path $harnessBuild $name) -Destination (Join-Path $companion $name) -Force
    }
    & dotnet $bundleTool sign $privateKey $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw "Candidate signing failed." }
    & dotnet $bundleTool verify $publicKey $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw "Candidate signature verification failed." }

    $env:HKTAS_ACCEPTANCE_FRAME_TRACE = '1'
    $env:HKTAS_ACCEPTANCE_RNG_DIAG_SCENE = $diagScene
    $env:HKTAS_ACCEPTANCE_RNG_DIAG_FRAMES = $diagFramesSpec

    Write-Output "=== Running S0 Divergence Diagnose: $suiteKey (Checkpoint=$($targetS0.checkpointId), Scene=$diagScene, Frames=$diagFramesSpec) ==="
    $args = @(
        '--headless',
        '--s0-acceptance',
        ('--scenario-output="{0}"' -f $outputRoot),
        ('--acceptance-package="{0}"' -f $pkgPath),
        ('--acceptance-suite="{0}"' -f $suiteKey)
    )
    $stdoutPath = Join-Path $outputRoot 'harness-stdout.log'
    $stderrPath = Join-Path $outputRoot 'harness-stderr.log'
    $hostProcess = Start-Process -FilePath (Join-Path $companion 'StudioScenarioHarness.exe') `
        -ArgumentList $args `
        -WorkingDirectory $companion `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath

    $timeoutMs = 3800000
    $timedOut = -not $hostProcess.WaitForExit($timeoutMs)
    if ($timedOut) {
        try { $hostProcess.Kill() } catch {}
        $hostProcess.WaitForExit()
    }
    $hostProcess.Refresh()
    $harnessExit = if ($timedOut) { -1 } else { $hostProcess.ExitCode }
    $hostProcess = $null

    foreach ($logName in @('Player.log', 'ModLog.txt')) {
        $logSrc = Join-Path $saveRoot $logName
        if (Test-Path -LiteralPath $logSrc -PathType Leaf) {
            Copy-Item -LiteralPath $logSrc -Destination (Join-Path $outputRoot $logName) -Force
        }
    }
}
finally {
    $env:HKTAS_ACCEPTANCE_FRAME_TRACE = $prevTraceEnv
    $env:HKTAS_ACCEPTANCE_RNG_DIAG_SCENE = $prevDiagSceneEnv
    $env:HKTAS_ACCEPTANCE_RNG_DIAG_FRAMES = $prevDiagFramesEnv
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
            Write-Warning "Final installation signature verification failed after restore!"
        }
    }

    $afterSaves = Read-SaveAudit
    [IO.File]::WriteAllText((Join-Path $outputRoot 'saves-after.json'), $afterSaves, [Text.UTF8Encoding]::new($false))
}

$savesClean = ($afterSaves -eq $beforeSaves)
if (-not $savesClean) {
    throw "Player save audit detected unexpected save file changes! See $outputRoot"
}

$diagJsonl = Join-Path $outputRoot 'acceptance-rng-diag.jsonl'
$traceCsv = Join-Path $outputRoot 'acceptance-frame-trace.csv'
if (-not (Test-Path -LiteralPath $diagJsonl -PathType Leaf)) {
    throw "StudioScenarioHarness exited ($harnessExit) without producing acceptance-rng-diag.jsonl at $diagJsonl"
}

Invoke-DiffRngDiag -DiagPath $diagJsonl -TracePath $traceCsv -PreferredRestoreSegment $targetRestoreSegment -OutDir $outputRoot
Write-Output "=== S0 Divergence Diagnose Complete ==="
Write-Output "HarnessExit=$harnessExit Report=$(Join-Path $outputRoot 'divergence-report.md')"
exit 0
