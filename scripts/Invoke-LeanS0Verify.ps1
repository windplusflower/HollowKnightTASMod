param(
    [ValidateSet('QuickPair', 'QuickTarget')][string] $Mode = 'QuickPair',
    [ValidateRange(1, 64)][int] $SourceBoundaryOrdinal = 8,
    [ValidateRange(1, 10000)][int] $SuffixFrames = 32,
    [switch] $SuffixToEnd,
    [ValidateRange(1, 4)][int] $TargetSaveSlot = 4,
    [string] $CustomOutputDirectory = '',
    [string] $MoviePath = '',
    [int] $EndFrame = 6738,
    [string] $EvidencePhase = 'p3b',
    [string] $CacheTag = '',
    [string] $S0ObservationTarget = 'GG_Workshop'
)
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# 1. Preflight checks
$lingering = Get-Process hollow_knight, StudioScenarioHarness -ErrorAction SilentlyContinue
if ($lingering) {
    throw "Preflight failed: lingering processes found: $($lingering.ProcessName -join ', ')"
}

$coreRuntime = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'
$coreHarness = Join-Path $repo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64/HollowKnightTAS.Core.dll'
if (-not (Test-Path -LiteralPath $coreRuntime) -or -not (Test-Path -LiteralPath $coreHarness)) {
    throw "Preflight failed: build HollowKnightTAS.Runtime and StudioScenarioHarness first with -p:SkipHKTASInstall=true."
}
$hashRuntime = (Get-FileHash -LiteralPath $coreRuntime -Algorithm SHA256).Hash
$hashHarness = (Get-FileHash -LiteralPath $coreHarness -Algorithm SHA256).Hash
if ($hashRuntime -ne $hashHarness) {
    throw "Preflight failed: HollowKnightTAS.Core.dll SHA256 mismatch between Runtime ($hashRuntime) and Harness ($hashHarness)."
}

$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$install = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
$companion = Join-Path $install 'Companion/win-x64'
if (Test-Path -LiteralPath $companion) {
    $lingeringHost = @(Get-ChildItem -LiteralPath $companion -File | Where-Object { $_.Name -like 'StudioScenarioHarness*' -or $_.Name -like 'WorldObservationHarness*' })
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
}
if (Test-Path -LiteralPath (Join-Path $install 'companion.manifest.json')) {
    & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) {
        throw "Preflight failed: Companion bundle signature is invalid."
    }
}

# 2. Default assets
$clockBridge = Join-Path $repo 'scratch/resident-clock-offline-074c843437b94c879d0f47756cdf6bf3/HollowKnightTAS.ClockBridge.dll'
if (-not (Test-Path -LiteralPath $clockBridge)) {
    throw "ClockBridge DLL not found: $clockBridge"
}
$movie = if ($MoviePath) {
    [IO.Path]::GetFullPath($MoviePath)
} else {
    Join-Path $repo 'artifacts/scene-checkpoints/p3b/resident-recording-20261103-02/run-01/serial-scenes-canonical-boundary-input-route-recording-unqualified.hktaspack'
}
if (-not (Test-Path -LiteralPath $movie)) {
    throw "Movie package not found: $movie"
}
$movieEndFrame = $EndFrame

# 3. Resolve output and cache directories
$phaseDir = Join-Path $repo ("artifacts/scene-checkpoints/$EvidencePhase")
if (-not (Test-Path -LiteralPath $phaseDir)) { New-Item -ItemType Directory -Force -Path $phaseDir | Out-Null }

if ($CustomOutputDirectory) {
    $outDir = [IO.Path]::GetFullPath($CustomOutputDirectory)
} else {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $tagPart = if ($CacheTag) { "$CacheTag-" } else { '' }
    $outDir = Join-Path $phaseDir ("lean-s0-verify-$tagPart$stamp")
}
if (Test-Path -LiteralPath $outDir) { throw "Choose a fresh output directory: $outDir" }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$cacheSuffix = if ($CacheTag) { "$CacheTag-b$SourceBoundaryOrdinal" } else { "b$SourceBoundaryOrdinal" }
$cacheDir = Join-Path $phaseDir "cached-lean-s0-$cacheSuffix"
$probeScript = Join-Path $PSScriptRoot 'Invoke-LoadingProbe.ps1'
$commonProbeArgs = @{
    Movie = $movie
    FirstFrame = 0
    EndFrame = $movieEndFrame
    Runs = 1
    DisableLegacyCalibration = $true
    SynchronousFetch = $true
    CanonicalStartupClock = $true
    ModWallClock = $true
    SerialScenes = $true
    GameplayOracle = $true
    SerialBoundaryOnly = $true
    NativeScheduler = $true
    NativePhysics = $true
    VirtualAudio = $true
    IsolatedOnline = $true
    CanonicalParticles = $true
    CanonicalBoundary = $true
    CanonicalPhaseOneTrace = $true
    S0ObservationTarget = $S0ObservationTarget
    S0ObservationSlot = $TargetSaveSlot
    ClockBridgePath = $clockBridge
    EvidencePhase = $EvidencePhase
    SourceBoundaryOrdinal = $SourceBoundaryOrdinal
    LeanS0 = $true
    LeanS0Slot = $TargetSaveSlot
}

$sourceOutDir = Join-Path $outDir 'source'
$targetOutDir = Join-Path $outDir 'target'

$cachedPacket = Join-Path $cacheDir 'lean-s0-packet.bin'
$cachedSourceK3 = Join-Path $cacheDir 'lean-s0-k3.jsonl'
$cachedSourceSummary = Join-Path $cacheDir 'lean-s0-capture-summary.json'

if ($Mode -eq 'QuickTarget' -and (Test-Path -LiteralPath $cachedPacket) -and (Test-Path -LiteralPath $cachedSourceK3)) {
    Write-Output "=== Lean S0 QuickTarget (reusing cached B$SourceBoundaryOrdinal packet from $cacheDir) ==="
    New-Item -ItemType Directory -Force -Path (Join-Path $sourceOutDir 'run-01') | Out-Null
    Copy-Item -LiteralPath $cachedPacket -Destination (Join-Path $sourceOutDir 'run-01/lean-s0-packet.bin') -Force
    Copy-Item -LiteralPath $cachedSourceK3 -Destination (Join-Path $sourceOutDir 'run-01/lean-s0-k3.jsonl') -Force
    if (Test-Path -LiteralPath $cachedSourceSummary) {
        Copy-Item -LiteralPath $cachedSourceSummary -Destination (Join-Path $sourceOutDir 'run-01/lean-s0-capture-summary.json') -Force
    }
    $packetToUse = $cachedPacket
} else {
    Write-Output "=== Lean S0 QuickPair Stage 1: Source B$SourceBoundaryOrdinal Capture + Suffix ==="
    $sourceArgs = @{}
    foreach ($kv in $commonProbeArgs.GetEnumerator()) { $sourceArgs[$kv.Key] = $kv.Value }
    $sourceArgs['OutputDirectory'] = $sourceOutDir
    $sourceArgs['LeanS0Role'] = 'source'
    if ($SuffixToEnd) { $sourceArgs['LeanS0SuffixToEnd'] = $true }
    & pwsh -NoProfile -File $probeScript @sourceArgs
    if ($LASTEXITCODE -ne 0) { throw "Source LeanS0 stage failed with exit code $LASTEXITCODE" }

    $srcRun = Join-Path $sourceOutDir 'run-01'
    $srcPacket = Join-Path $srcRun 'lean-s0-source-ipc-packet.bin'
    $srcK3 = Join-Path $srcRun 'lean-s0-k3.jsonl'
    if (-not (Test-Path -LiteralPath $srcPacket) -or -not (Test-Path -LiteralPath $srcK3)) {
        throw "Source stage did not emit packet or K3 trace in $srcRun"
    }
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    Copy-Item -LiteralPath $srcPacket -Destination $cachedPacket -Force
    Copy-Item -LiteralPath $srcK3 -Destination $cachedSourceK3 -Force
    $srcCapSummary = Join-Path $srcRun 'lean-s0-capture-summary.json'
    if (Test-Path -LiteralPath $srcCapSummary) {
        Copy-Item -LiteralPath $srcCapSummary -Destination $cachedSourceSummary -Force
    }
    $packetToUse = $srcPacket
}

# Read source K3 header/summary to bind exact endMovie
$srcHeaderJson = (Get-Content -LiteralPath (Join-Path $sourceOutDir 'run-01/lean-s0-k3.jsonl') -TotalCount 1 | ConvertFrom-Json).metadata
$expectedEndMovie = [string]$srcHeaderJson.endMovie

Write-Output "=== Lean S0 Stage 2: Target S0 Restore (endMovie=$expectedEndMovie) ==="
$targetArgs = @{}
foreach ($kv in $commonProbeArgs.GetEnumerator()) { $targetArgs[$kv.Key] = $kv.Value }
$targetArgs['OutputDirectory'] = $targetOutDir
$targetArgs['LeanS0Role'] = 'target'
$targetArgs['LeanS0PacketPath'] = $packetToUse
$targetArgs['LeanS0EndMovie'] = $expectedEndMovie
if ($SuffixToEnd) { $targetArgs['LeanS0SuffixToEnd'] = $true }
& pwsh -NoProfile -File $probeScript @targetArgs
if ($LASTEXITCODE -ne 0) { throw "Target LeanS0 stage failed with exit code $LASTEXITCODE" }

Write-Output "=== Lean S0 Stage 3: K3 Trajectory Equivalence Comparison ==="
$k3ReportPath = Join-Path $outDir 'k3-compare-report.json'
& python (Join-Path $PSScriptRoot 'Compare-LeanS0K3.py') `
    --source (Join-Path $sourceOutDir 'run-01/lean-s0-k3.jsonl') `
    --target (Join-Path $targetOutDir 'run-01/lean-s0-k3.jsonl') `
    --output $k3ReportPath
$k3Exit = $LASTEXITCODE

# 4. Post-run hygiene audit
function Test-SaveAudit([string]$stageDir) {
    $beforePath = Join-Path $stageDir 'saves-before.json'
    $afterPath = Join-Path $stageDir 'saves-after.json'
    if (-not (Test-Path -LiteralPath $beforePath) -or -not (Test-Path -LiteralPath $afterPath)) {
        return @{ present = $false; matched = $false; count = 0 }
    }
    $beforeArr = @(Get-Content -Raw -LiteralPath $beforePath | ConvertFrom-Json)
    $afterArr = @(Get-Content -Raw -LiteralPath $afterPath | ConvertFrom-Json)
    $bMap = @{}; foreach ($e in $beforeArr) { $bMap[[string]$e.name] = "$($e.length):$($e.sha256)" }
    $aMap = @{}; foreach ($e in $afterArr) { $aMap[[string]$e.name] = "$($e.length):$($e.sha256)" }
    $mismatchCount = @($bMap.Keys | Where-Object { -not $aMap.ContainsKey($_) -or $aMap[$_] -ne $bMap[$_] }).Count
    $matched = ($bMap.Count -gt 0) -and ($bMap.Count -eq $aMap.Count) -and ($mismatchCount -eq 0)
    return @{ present = $true; matched = $matched; count = $bMap.Count }
}

function Test-ModIsolation([string]$stageDir) {
    $isoPath = Join-Path $stageDir 'mods-isolation.json'
    if (-not (Test-Path -LiteralPath $isoPath)) { return 'missing' }
    $iso = Get-Content -Raw -LiteralPath $isoPath | ConvertFrom-Json
    return [string]$iso.status
}

$lingeringAfter = @(Get-Process hollow_knight, StudioScenarioHarness -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName)
& dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
$sigValid = ($LASTEXITCODE -eq 0)

$hygiene = @{
    sourceSaveAudit = if (Test-Path -LiteralPath (Join-Path $sourceOutDir 'saves-before.json')) { Test-SaveAudit $sourceOutDir } else { $null }
    targetSaveAudit = Test-SaveAudit $targetOutDir
    sourceModIsolation = if (Test-Path -LiteralPath (Join-Path $sourceOutDir 'mods-isolation.json')) { Test-ModIsolation $sourceOutDir } else { 'skipped-cached' }
    targetModIsolation = Test-ModIsolation $targetOutDir
    lingeringProcesses = $lingeringAfter
    companionSignatureValid = $sigValid
}
$hygiene | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outDir 'hygiene-report.json') -Encoding utf8

Write-Output "=== Hygiene Summary ==="
Write-Output ($hygiene | ConvertTo-Json -Compress)

if ($k3Exit -ne 0) {
    throw "K3 behavioral comparison failed (see $k3ReportPath)."
}
if (-not $hygiene.targetSaveAudit.matched -or $hygiene.targetModIsolation -ne 'restored' -or $hygiene.lingeringProcesses.Count -gt 0 -or -not $sigValid) {
    throw "Post-run hygiene check failed."
}

Write-Output "Lean S0 Verification PASSED cleanly! Output: $outDir"
