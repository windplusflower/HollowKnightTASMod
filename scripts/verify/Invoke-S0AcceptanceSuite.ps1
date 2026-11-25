<#
.SYNOPSIS
    Final S0 full-run vs. per-S0 manual-restore live-game acceptance suite (Tas1 & Tas2).

.DESCRIPTION
    Do NOT run during routine development or regular regression checks.
    Run ONLY when a task is completely finished and ready for final verification.
    A failure does NOT automatically mean the code has a bug; inspect the generated
    summary.md and report.json and present the root-cause findings for user decision.
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Tas1', 'Tas2', 'Custom')]
    [string] $Suite = 'All',
    [int] $Seed = 0,
    [string] $Tas1Path = '',
    [string] $Tas2Path = '',
    [string] $CustomPackage = '',
    [string] $CustomSuiteName = '',
    [int] $ChainRestoreRounds = 0,
    [int] $ChainRestoreSeed = 1337,
    [string] $OutputDirectory = '',
    [string] $InstallDirectory = '',
    [string[]] $KeepMods = @()
)

$ErrorActionPreference = 'Stop'

if ($ChainRestoreRounds -lt 0) {
    throw "ChainRestoreRounds must be non-negative: $ChainRestoreRounds"
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

# 1. Resolve inputs & preflight checks
$lingering = @(Get-Process hollow_knight, HollowKnightTAS.Companion, StudioScenarioHarness, WorldObservationHarness -ErrorAction SilentlyContinue)
if ($lingering.Count -gt 0) {
    throw "Preflight failed: lingering game or Studio processes found: $(($lingering | Select-Object -ExpandProperty ProcessName) -join ', ')"
}

$resolvedTas1 = if ($Tas1Path) { [IO.Path]::GetFullPath($Tas1Path) } else { Join-Path $repo '.local/acceptance-tas/tas1_pantheon1_3bosses.hktaspack' }
$resolvedTas2 = if ($Tas2Path) { [IO.Path]::GetFullPath($Tas2Path) } else { Join-Path $repo '.local/acceptance-tas/tas2_newsave_dirtmouth_bench.hktaspack' }

$selectedSuites = [Collections.Generic.List[hashtable]]::new()
if ($Suite -in @('All', 'Tas1')) {
    $selectedSuites.Add(@{ key = 'tas1_pantheon1_3bosses'; path = $resolvedTas1 })
}
if ($Suite -in @('All', 'Tas2')) {
    $selectedSuites.Add(@{ key = 'tas2_newsave_dirtmouth_bench'; path = $resolvedTas2 })
}
if ($Suite -eq 'Custom') {
    if ([string]::IsNullOrWhiteSpace($CustomPackage)) {
        throw "CustomPackage is required when -Suite Custom is specified."
    }
    $resolvedCustom = [IO.Path]::GetFullPath($CustomPackage)
    if (-not (Test-Path -LiteralPath $resolvedCustom -PathType Leaf)) {
        throw "Acceptance sequence package not found: $resolvedCustom"
    }
    $rawCustomName = if (-not [string]::IsNullOrWhiteSpace($CustomSuiteName)) {
        $CustomSuiteName.Trim()
    } else {
        [IO.Path]::GetFileNameWithoutExtension($resolvedCustom)
    }
    $sanitizedCustomKey = ([regex]::Replace($rawCustomName, '[^A-Za-z0-9._-]+', '_')).Trim('_', '.')
    if ([string]::IsNullOrWhiteSpace($sanitizedCustomKey)) {
        $sanitizedCustomKey = 'custom'
    }
    $selectedSuites.Add(@{ key = $sanitizedCustomKey; path = $resolvedCustom })
}
foreach ($s in $selectedSuites) {
    if (-not (Test-Path -LiteralPath $s.path -PathType Leaf)) {
        throw "Acceptance sequence package not found: $($s.path)"
    }
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
$compareScript = Join-Path $PSScriptRoot 'Compare-S0Acceptance.py'

$requiredPaths = @($bundleTool, $privateKey, $publicKey, $runtimeDll, $runtimeCoreDll, $harnessCoreDll, $harnessCompanionDll, $compareScript) +
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

# Clean any leftover test harness files in Companion before verifying initial install state
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
        throw "Preflight failed: installed companion.manifest.json signature is invalid before acceptance run."
    }
}

# 2. Prepare output directory, seed & installation backup
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $repo ("artifacts/acceptance/s0-acceptance-$stamp")
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Output directory already exists; specify a fresh OutputDirectory: $outputRoot"
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$actualSeed = if ($Seed -gt 0) { $Seed } else { Get-Random -Minimum 1 -Maximum 2147483647 }

$OnlyTasMods = $true
$modRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($install))
$modQuarantine = Join-Path (Join-Path $modRoot 'disabled') ('__HKTAS_S0Acceptance_' + [Guid]::NewGuid().ToString('N'))
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

$suiteReports = [Collections.Generic.List[object]]::new()
$prevTraceEnv = $env:HKTAS_ACCEPTANCE_FRAME_TRACE
$hostProcess = $null

try {
    if ($KeepMods -and $KeepMods.Count -gt 0) {
        Isolate-OtherMods -KeepMods $KeepMods
    } else {
        Isolate-OtherMods
    }
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

    foreach ($s in $selectedSuites) {
        $suiteKey = [string]$s.key
        $pkgPath = [string]$s.path
        $suiteDir = Join-Path $outputRoot $suiteKey
        New-Item -ItemType Directory -Force -Path $suiteDir | Out-Null

        Write-Output "=== Running S0 Acceptance Suite: $suiteKey (Seed=$actualSeed) ==="
        $args = @(
            '--headless',
            '--s0-acceptance',
            ('--scenario-output="{0}"' -f $suiteDir),
            ('--acceptance-package="{0}"' -f $pkgPath),
            ('--acceptance-suite="{0}"' -f $suiteKey)
        )
        if ($ChainRestoreRounds -gt 0) {
            $args += @(
                ('--chain-restore-rounds={0}' -f $ChainRestoreRounds.ToString([Globalization.CultureInfo]::InvariantCulture)),
                ('--chain-restore-seed={0}' -f $ChainRestoreSeed.ToString([Globalization.CultureInfo]::InvariantCulture))
            )
        }
        $stdoutPath = Join-Path $suiteDir 'harness-stdout.log'
        $stderrPath = Join-Path $suiteDir 'harness-stderr.log'
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
                Copy-Item -LiteralPath $logSrc -Destination (Join-Path $suiteDir $logName) -Force
            }
        }

        $traceCsv = Join-Path $suiteDir 'acceptance-frame-trace.csv'
        $checkpointsJson = Join-Path $suiteDir 'baseline-checkpoints.json'
        $runsJson = Join-Path $suiteDir 's0-runs.json'

        if ((Test-Path -LiteralPath $traceCsv -PathType Leaf) -or (Test-Path -LiteralPath $runsJson -PathType Leaf)) {
            $compareArgs = @(
                $compareScript,
                '--trace', $traceCsv,
                '--suite', $suiteKey,
                '--seed', $actualSeed.ToString([Globalization.CultureInfo]::InvariantCulture),
                '--out-dir', $suiteDir
            )
            if (Test-Path -LiteralPath $checkpointsJson -PathType Leaf) {
                $compareArgs += @('--checkpoints', $checkpointsJson)
            }
            if (Test-Path -LiteralPath $runsJson -PathType Leaf) {
                $compareArgs += @('--runs', $runsJson)
            }
            & python @compareArgs | Out-Host
            $compareExit = $LASTEXITCODE
        } else {
            $compareExit = 1
            $fallbackDetail = if ($timedOut) {
                "StudioScenarioHarness timed out after ${timeoutMs}ms without producing trace or metadata."
            } else {
                "StudioScenarioHarness exited with code $harnessExit without producing acceptance-frame-trace.csv or s0-runs.json."
            }
            $fallbackReport = [ordered]@{
                suite = $suiteKey
                passed = $false
                seed = $actualSeed
                reason = if ($timedOut) { 'harness_timeout' } else { 'harness_crashed_before_output' }
                detail = $fallbackDetail
                harnessExitCode = $harnessExit
                baseline = @{}
                baselineTransitions = @()
                sceneIntervals = @()
                sampledFrames = @()
                s0Results = @()
            }
            $fallbackReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $suiteDir 'report.json') -Encoding utf8
            $fallbackMd = @(
                "# S0 实机验收对比报告 - ``$suiteKey``",
                "",
                "> [!IMPORTANT]",
                "> **注意：本实机验收未通过不代表代码必然存在问题。** 请结合下方执行日志与退出原因由用户决策后续处理方式。",
                "",
                "- 验收结论：**FAIL**",
                "- 原因代码：``$($fallbackReport.reason)``",
                "- 详情：$fallbackDetail",
                "- Harness 退出码：``$harnessExit``",
                "- 请查看日志：``scenario.log`` / ``harness-stderr.log`` / ``Player.log`` / ``ModLog.txt``。"
            ) -join "`n"
            [IO.File]::WriteAllText((Join-Path $suiteDir 'summary.md'), $fallbackMd + "`n", [Text.UTF8Encoding]::new($false))
        }

        $suiteReport = Get-Content -Raw -LiteralPath (Join-Path $suiteDir 'report.json') | ConvertFrom-Json
        $suiteReports.Add([ordered]@{
            suite = $suiteKey
            package = $pkgPath
            passed = [bool]$suiteReport.passed -and ($harnessExit -eq 0) -and ($compareExit -eq 0)
            reason = [string]$suiteReport.reason
            detail = [string]$suiteReport.detail
            harnessExitCode = $harnessExit
            compareExitCode = $compareExit
            seed = $actualSeed
            reportPath = (Join-Path $suiteDir 'report.json')
            summaryPath = (Join-Path $suiteDir 'summary.md')
        })
    }
}
finally {
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
            Write-Warning "Final installation signature verification failed after restore!"
        }
    }

    $afterSaves = Read-SaveAudit
    [IO.File]::WriteAllText((Join-Path $outputRoot 'saves-after.json'), $afterSaves, [Text.UTF8Encoding]::new($false))
}

$savesClean = ($afterSaves -eq $beforeSaves)
$allSuitesPassed = ($suiteReports.Count -gt 0) -and (@($suiteReports | Where-Object { -not $_.passed }).Count -eq 0) -and $savesClean

$topReport = [ordered]@{
    suiteSelection = $Suite
    seed = $actualSeed
    passed = $allSuitesPassed
    savesUnchanged = $savesClean
    modIsolationStatus = $modIsolationStatus
    outputDirectory = $outputRoot
    suites = $suiteReports
}
$topReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'report.json') -Encoding utf8

$mdLines = [Collections.Generic.List[string]]::new()
$mdLines.Add("# S0 实机最终验收总报告")
$mdLines.Add("")
if (-not $allSuitesPassed) {
    $mdLines.Add("> [!IMPORTANT]")
    $mdLines.Add("> **注意：本实机验收未通过不代表代码必然存在问题。** 请勿擅自修改判断条件，请结合各套件子报告中的差异明细与日志说明原因，交由用户决策。")
    $mdLines.Add("")
}
$mdLines.Add("- 总结论：**$(if ($allSuitesPassed) { 'PASS (全部通过)' } else { 'FAIL (存在未通过项)' })**")
$mdLines.Add("- 随机抽样 Seed：``$actualSeed``")
$mdLines.Add("- 原版存档保护检查：``$(if ($savesClean) { 'UNCHANGED' } else { 'CHANGED!' })``")
$mdLines.Add("- Mod 隔离恢复状态：``$modIsolationStatus``")
$mdLines.Add("")
$mdLines.Add("| 套件 | 状态 | 原因代码 | Harness Exit | 子报告 |")
$mdLines.Add("| --- | --- | --- | --- | --- |")
foreach ($sr in $suiteReports) {
    $st = if ($sr.passed) { 'PASS' } else { 'FAIL' }
    $mdLines.Add("| ``$($sr.suite)`` | **$st** | ``$($sr.reason)`` | ``$($sr.harnessExitCode)`` | ``$($sr.suite)/summary.md`` |")
}
$mdLines.Add("")
[IO.File]::WriteAllText((Join-Path $outputRoot 'summary.md'), ($mdLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

Write-Output "=== S0 Acceptance Suite Summary ==="
Write-Output "Passed=$allSuitesPassed Seed=$actualSeed SavesUnchanged=$savesClean Output=$outputRoot"

if (-not $savesClean) {
    throw "Player save audit detected unexpected save file changes! See $outputRoot"
}
if (-not $allSuitesPassed) {
    exit 1
}
exit 0
