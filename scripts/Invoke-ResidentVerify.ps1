param(
    [ValidateSet('P3B', 'P3C')][string] $Suite = 'P3B',
    [ValidateSet('QuickTarget', 'QuickPair', 'FullAcceptance')][string] $Mode = 'QuickTarget',
    [string] $CustomOutputDirectory = ''
)
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# 1. Preflight checks
$lingering = Get-Process hollow_knight, StudioScenarioHarness -ErrorAction SilentlyContinue
if ($lingering) {
    throw "Preflight failed: found lingering processes: $($lingering.ProcessName -join ', '). Stop them before launching."
}

$coreRuntime = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'
$coreHarness = Join-Path $repo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64/HollowKnightTAS.Core.dll'
if ((Test-Path -LiteralPath $coreRuntime) -and (Test-Path -LiteralPath $coreHarness)) {
    $hashRuntime = (Get-FileHash -LiteralPath $coreRuntime -Algorithm SHA256).Hash
    $hashHarness = (Get-FileHash -LiteralPath $coreHarness -Algorithm SHA256).Hash
    if ($hashRuntime -ne $hashHarness) {
        throw "Preflight failed: HollowKnightTAS.Core.dll SHA256 mismatch between Runtime ($hashRuntime) and Harness ($hashHarness). Rebuild both projects."
    }
}

$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$install = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
$companion = Join-Path $install 'Companion/win-x64'
$lingeringHost = if (Test-Path -LiteralPath $companion) {
    @(Get-ChildItem -LiteralPath $companion -File | Where-Object { $_.Name -like 'StudioScenarioHarness*' -or $_.Name -like 'WorldObservationHarness*' })
} else { @() }
if ($lingeringHost.Count -gt 0) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    foreach ($f in $lingeringHost) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
            $f.FullName,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    }
    & dotnet $bundleTool sign (Join-Path $repo '.local/signing/companion-private.json') $install (Join-Path $install 'companion.manifest.json')
}
if (Test-Path -LiteralPath (Join-Path $install 'companion.manifest.json')) {
    & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) {
        throw "Preflight failed: Companion bundle signature is invalid. Re-sign bundle before launching."
    }
}

# 2. Bind constants by suite
$clockBridge = Join-Path $repo 'scratch/resident-clock-offline-074c843437b94c879d0f47756cdf6bf3/HollowKnightTAS.ClockBridge.dll'
if (-not (Test-Path -LiteralPath $clockBridge)) {
    throw "ClockBridge DLL not found: $clockBridge"
}

if ($Suite -eq 'P3B') {
    $evidencePhase = 'p3b'
    $movie = Join-Path $repo 'artifacts/scene-checkpoints/p3b/resident-recording-20261103-02/run-01/serial-scenes-canonical-boundary-input-route-recording-unqualified.hktaspack'
    $sourceBoundaryOrdinal = 8
    $movieEndFrame = 6738
} elseif ($Suite -eq 'P3C') {
    $evidencePhase = 'p3c'
    throw "Suite P3C is not yet active."
}

# 3. Resolve next evidence directory
$phaseDir = Join-Path $repo "artifacts/scene-checkpoints/$evidencePhase"
if (-not (Test-Path -LiteralPath $phaseDir)) { New-Item -ItemType Directory -Force -Path $phaseDir | Out-Null }

if ($CustomOutputDirectory) {
    $outDir = [IO.Path]::GetFullPath($CustomOutputDirectory)
} else {
    $maxIndex = 0
    Get-ChildItem -LiteralPath $phaseDir -Directory -Filter 'resident-pair-20261103-*' | ForEach-Object {
        if ($_.Name -match '^resident-pair-20261103-(\d+)$') {
            $idx = [int]$matches[1]
            if ($idx -gt $maxIndex) { $maxIndex = $idx }
        }
    }
    $nextIndex = $maxIndex + 1
    $outDir = Join-Path $phaseDir ('resident-pair-20261103-{0:d3}' -f $nextIndex)
}

$cacheDir = Join-Path $phaseDir 'cached-b8-source'

# 4. Dispatch by mode
$pairScript = Join-Path $repo 'scripts/Invoke-ActualDSuffixPair.ps1'
$baseArgs = @{
    OutputDirectory = $outDir
    Movie = $movie
    ClockBridgePath = $clockBridge
    SourceBoundaryOrdinal = $sourceBoundaryOrdinal
    MovieEndFrame = $movieEndFrame
    EvidencePhase = $evidencePhase
}

Write-Output "=== HollowKnightTAS Resident Verify ==="
Write-Output "Suite:  $Suite"
Write-Output "Mode:   $Mode"
Write-Output "Target: $outDir"

if ($Mode -eq 'QuickTarget') {
    if (-not (Test-Path -LiteralPath (Join-Path $cacheDir 'packet.bin'))) {
        Write-Warning "Cached source packet not found in $cacheDir. Falling back to QuickPair to capture and cache Source D first."
        $baseArgs['SaveSourcePacketTo'] = $cacheDir
    } else {
        $baseArgs['ReuseSourceDirectory'] = $cacheDir
        Write-Output "Reusing cached Source D packet from $cacheDir (QuickTarget: ~2-4 min)."
    }
} elseif ($Mode -eq 'QuickPair') {
    $baseArgs['SaveSourcePacketTo'] = $cacheDir
    Write-Output "Running QuickPair (Source B8 capture + 32-frame suffix + Target Restore + 32-frame suffix: ~10-12 min). Will cache Source to $cacheDir."
} elseif ($Mode -eq 'FullAcceptance') {
    $baseArgs['ActualDSuffixToEnd'] = $true
    Write-Output "Running FullAcceptance (Full 126-frame suffix dual-process acceptance: ~80 min)."
}

& pwsh -NoProfile -File $pairScript @baseArgs
