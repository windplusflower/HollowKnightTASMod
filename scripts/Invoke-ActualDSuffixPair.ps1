param(
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [Parameter(Mandatory=$true)][string] $Movie,
    [Parameter(Mandatory=$true)][string] $ClockBridgePath,
    [switch] $PhysicsFollowOn,
    [switch] $ContactImpulseProbe,
    [switch] $DiscardInstallation,
    [switch] $ActualDSuffixToEnd,
    [ValidateRange(1,64)][int] $SourceBoundaryOrdinal = 1,
    [string] $DerivedFixtureManifest = '',
    [ValidateSet('p3', 'p3a', 'p3b', 'p3c')][string] $EvidencePhase = 'p3',
    [ValidateRange(0, 100000)][int] $MovieStartFrame = 0,
    [ValidateRange(1, 100000)][int] $MovieEndFrame = 3150,
    [string] $S0ObservationTarget = 'GG_Workshop',
    [int] $S0ObservationSlot = 4,
    [string] $SaveSourcePacketTo = '',
    [string] $ReuseSourceDirectory = ''
)
$ErrorActionPreference = 'Stop'
if ($PhysicsFollowOn -or $ContactImpulseProbe) { throw 'Resident transfer does not use the retired first-boundary physics follow-on script.' }
if ($ContactImpulseProbe -and -not $PhysicsFollowOn) { throw 'Contact impulse diagnostics require the bounded follow-on.' }
if ($SourceBoundaryOrdinal -ne 1 -and $PhysicsFollowOn) { throw 'The existing physics follow-on belongs only to its first-boundary route.' }
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskOwned = [IO.Path]::GetFullPath((Join-Path $taskRepo "artifacts/scene-checkpoints/$EvidencePhase")) + [IO.Path]::DirectorySeparatorChar
if (-not $taskOutput.StartsWith($taskOwned, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $taskOutput)) {
    throw "Choose a fresh owned $EvidencePhase evidence directory."
}
# Hold only the existing candidate and fixed source identities across the two
# independent protected runs. Neither this script nor its relay writes D bytes.
$taskCandidates = @(
    'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll',
    'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll',
    'src/HollowKnightTAS.ClockPayload/bin/Release/HollowKnightTAS.ClockPayload.dll'
)
$taskCandidates += @(Get-ChildItem -LiteralPath (Join-Path $taskRepo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64') -File |
    Where-Object { $_.Name -in @('StudioScenarioHarness.exe','StudioScenarioHarness.dll','HollowKnightTAS.Core.dll','HollowKnightTAS.Companion.dll','HollowKnightTAS.Automation.Client.dll') } |
    ForEach-Object { $_.FullName })
$taskCandidates += @([IO.Path]::GetFullPath($Movie), [IO.Path]::GetFullPath($ClockBridgePath))
$taskIdentities = @($taskCandidates | ForEach-Object {
    $taskPath = if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $taskRepo $_ }
    [pscustomobject]@{ path=$taskPath; sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash }
})
$taskRelayState = @{}
$taskOptions = @{
    Movie=$Movie; FirstFrame=$MovieStartFrame; EndFrame=$MovieEndFrame; Runs=1; DisableLegacyCalibration=$true;
    SynchronousFetch=$true; CanonicalStartupClock=$true; ModWallClock=$true; SerialScenes=$true;
    GameplayOracle=$true; SerialBoundaryOnly=$true; NativeScheduler=$true; NativePhysics=$true;
    VirtualAudio=$true; IsolatedOnline=$true; CanonicalParticles=$true; CanonicalBoundary=$true;
    CanonicalPhaseOneTrace=$true; S0ObservationTarget=$S0ObservationTarget; S0ObservationSlot=$S0ObservationSlot; ClockCarryExperiment=$true;
    ActualDExperiment=$true; ActualDSuffix=$true; ClockBridgePath=$ClockBridgePath; EvidencePhase=$EvidencePhase;
    ActualDRelayState=$taskRelayState
    SourceBoundaryOrdinal=$SourceBoundaryOrdinal
    PhysicsFollowOn=$PhysicsFollowOn
    DiscardInstallation=$DiscardInstallation
    ActualDSuffixToEnd=$ActualDSuffixToEnd
    DerivedFixtureManifest=$DerivedFixtureManifest
}
$taskPriorImpulse = $env:HKTAS_CONTACT_IMPULSE_PROBE
try {
    if (-not ('HollowKnightTAS.Diagnostics.ActualDMemoryRelay' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'StudioScenarioHarness/ActualDMemoryRelay.cs')
    }
    if ($ContactImpulseProbe) { $env:HKTAS_CONTACT_IMPULSE_PROBE = '1' }
    if ($ReuseSourceDirectory) {
        $reuseDir = [IO.Path]::GetFullPath($ReuseSourceDirectory)
        $packetBin = Join-Path $reuseDir 'packet.bin'
        $packetMeta = Join-Path $reuseDir 'packet-meta.json'
        $headerPath = Join-Path $reuseDir 'actual-d-source-header.json'
        $endPath = Join-Path $reuseDir 'actual-d-original-suffix-state.json'
        $continuationPath = Join-Path $reuseDir 'resident-continuation.jsonl'
        if (-not (Test-Path -LiteralPath $packetBin) -or -not (Test-Path -LiteralPath $packetMeta) -or
            -not (Test-Path -LiteralPath $headerPath) -or -not (Test-Path -LiteralPath $endPath) -or
            -not (Test-Path -LiteralPath $continuationPath)) {
            throw "ReuseSourceDirectory is missing required cached packet files: $reuseDir"
        }
        $meta = Get-Content -Raw -LiteralPath $packetMeta | ConvertFrom-Json
        $data = [IO.File]::ReadAllBytes($packetBin)
        $actualSha256 = (Get-FileHash -LiteralPath $packetBin -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSha256 -ne [string]$meta.Sha256.ToLowerInvariant()) {
            throw "Cached packet data hash mismatch: act=$actualSha256 exp=$($meta.Sha256)"
        }
        $taskRelayState.Packet = [HollowKnightTAS.Diagnostics.ActualDRelayPacket]@{
            Data = $data
            MovieFrame = [long]$meta.MovieFrame
            NativeFrame = [long]$meta.NativeFrame
            Sha256 = [string]$meta.Sha256
        }
        $sourceDest = Join-Path $taskOutput 'source/run-01'
        New-Item -ItemType Directory -Force -Path $sourceDest | Out-Null
        Copy-Item -LiteralPath $headerPath -Destination (Join-Path $sourceDest 'actual-d-source-header.json') -Force
        Copy-Item -LiteralPath $endPath -Destination (Join-Path $sourceDest 'actual-d-original-suffix-state.json') -Force
        Copy-Item -LiteralPath $continuationPath -Destination (Join-Path $sourceDest 'resident-continuation.jsonl') -Force
        Write-Output "Reused sealed Source D packet from $reuseDir (length=$($data.Length), sha256=$($meta.Sha256)). Skipping Source execution."
    } else {
        & (Join-Path $PSScriptRoot 'Invoke-LoadingProbe.ps1') @taskOptions -ActualDStage Source -OutputDirectory (Join-Path $taskOutput 'source')
        if (-not $taskRelayState.ContainsKey('Packet')) { throw 'Source did not hand off its packet in memory.' }
        if ($SaveSourcePacketTo) {
            $saveDir = [IO.Path]::GetFullPath($SaveSourcePacketTo)
            New-Item -ItemType Directory -Force -Path $saveDir | Out-Null
            [IO.File]::WriteAllBytes((Join-Path $saveDir 'packet.bin'), $taskRelayState.Packet.Data)
            @{
                MovieFrame = $taskRelayState.Packet.MovieFrame
                NativeFrame = $taskRelayState.Packet.NativeFrame
                Sha256 = $taskRelayState.Packet.Sha256
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $saveDir 'packet-meta.json') -Encoding utf8
            $srcRun = Join-Path $taskOutput 'source/run-01'
            foreach ($item in @('actual-d-source-header.json', 'actual-d-original-suffix-state.json', 'resident-continuation.jsonl')) {
                $srcFile = Join-Path $srcRun $item
                if (Test-Path -LiteralPath $srcFile) {
                    Copy-Item -LiteralPath $srcFile -Destination (Join-Path $saveDir $item) -Force
                }
            }
            Write-Output "Cached sealed Source D packet and metadata to $saveDir"
        }
    }
    # A replay can reach the requested ordinal by a different door or on an earlier
    # Movie frame. Bind a recorded fixture to its original transition before import.
    $taskMovieDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($Movie))
    $taskRecordingProof = Join-Path $taskMovieDirectory 'input-route-recording-proof.json'
    if (Test-Path -LiteralPath $taskRecordingProof) {
        $proof = Get-Content -Raw -LiteralPath $taskRecordingProof | ConvertFrom-Json
        if ($proof.recordingSha256 -ne (Get-FileHash -LiteralPath $Movie).Hash) {
            throw 'Selected movie does not match the recording proof.'
        }
        $journals = @(Get-ChildItem -LiteralPath $taskMovieDirectory -File | Where-Object Name -Match '^serial-scenes-[0-9]+\.jsonl$')
        if ($journals.Count -ne 1) { throw 'Recorded serial boundary journal is absent or ambiguous.' }
        $expected = @(Get-Content -LiteralPath $journals[0].FullName | ConvertFrom-Json |
            Where-Object { $_.phase -eq 'native-empty-boundary' -and $_.ordinal -eq $SourceBoundaryOrdinal })
        $header = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'source/run-01/actual-d-source-header.json') | ConvertFrom-Json
        if ($expected.Count -ne 1 -or $header.movieFrame -ne [string]$expected[0].data.movie -or
            $header.sourceScene -cne $expected[0].data.source -or $header.targetScene -cne $expected[0].data.target) {
            throw 'Actual source frame or scene transition differs from the recorded fixture.'
        }
    }
    foreach ($taskIdentity in $taskIdentities) {
        if ((Get-FileHash -LiteralPath $taskIdentity.path -Algorithm SHA256).Hash -ne $taskIdentity.sha256) {
            throw 'Candidate or source changed between the independent protected stages.'
        }
    }
    & (Join-Path $PSScriptRoot 'Invoke-LoadingProbe.ps1') @taskOptions -ActualDStage Target -OutputDirectory (Join-Path $taskOutput 'target')
    if ($taskRelayState.ContainsKey('Packet')) { throw 'Target did not consume its single-use packet.' }
    $taskIdentities | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskOutput 'fixed-stage-identities.json') -Encoding utf8
    $taskReceipt = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'target/run-01/actual-d-receipt.json') | ConvertFrom-Json
    $taskSourceHeader = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'source/run-01/actual-d-source-header.json') | ConvertFrom-Json
    $taskApplication = $taskReceipt.result
    foreach ($taskKey in @('schema','status','movieFrame','nativeFrame','packetSha256','packetBytes','d1Passed','d2Passed',
        'elapsedMilliseconds','noMovieTickDuringApply','consumedSamples','restoreQualified','completeStateCoverage')) {
        if ($taskApplication.PSObject.Properties.Name -notcontains $taskKey) { throw "Resident application receipt is missing $taskKey." }
    }
    foreach ($taskKey in @('schema','length','sha256','movieFrame','nativeFrame','sourceOrdinal','consumedSamples','restoreQualified','completeStateCoverage')) {
        if ($taskSourceHeader.PSObject.Properties.Name -notcontains $taskKey) { throw "Resident source IPC header is missing $taskKey." }
    }
    if ($taskReceipt.schema -ne 'resident-actual-d-receipt-v1' -or $taskSourceHeader.schema -ne 'resident-boundary-v2' -or
        $taskReceipt.source.sha256 -ne $taskSourceHeader.sha256 -or $taskReceipt.source.length -ne $taskSourceHeader.length -or
        $taskReceipt.source.movieFrame -ne $taskSourceHeader.movieFrame -or $taskReceipt.source.nativeFrame -ne $taskSourceHeader.nativeFrame -or
        $taskSourceHeader.sourceOrdinal -ne "$SourceBoundaryOrdinal") { throw 'Resident receipt does not bind the independently sealed source IPC metadata.' }
    [long] $taskElapsed = 0
    [long] $taskPacketBytes = 0
    if (-not [long]::TryParse([string]$taskApplication.elapsedMilliseconds, [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$taskElapsed) -or
        -not [long]::TryParse([string]$taskApplication.packetBytes, [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$taskPacketBytes)) { throw 'Resident application budget metadata is malformed.' }
    $taskSourceEnd = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'source/run-01/actual-d-original-suffix-state.json') | ConvertFrom-Json
    $taskTargetEnd = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'target/run-01/actual-d-suffix-state.json') | ConvertFrom-Json
    $taskReadback = $taskApplication.schema -eq 'resident-boundary-v2' -and $taskApplication.status -eq 'ResidentDAppliedPaused' -and
        $taskApplication.d1Passed -eq 'true' -and $taskApplication.d2Passed -eq 'true' -and $taskApplication.noMovieTickDuringApply -eq 'true' -and
        $taskApplication.packetSha256 -eq $taskSourceHeader.sha256 -and $taskApplication.packetBytes -eq $taskSourceHeader.length -and
        $taskApplication.movieFrame -eq $taskSourceHeader.movieFrame -and $taskApplication.nativeFrame -eq $taskSourceHeader.nativeFrame -and
        $taskApplication.consumedSamples -eq $taskSourceHeader.consumedSamples -and
        $taskPacketBytes -gt 0 -and $taskPacketBytes -le (256*1024*1024) -and $taskElapsed -lt 360000 -and
        $taskApplication.restoreQualified -eq 'false' -and $taskApplication.completeStateCoverage -eq 'false'
    $taskEndpoint = ($taskSourceEnd | ConvertTo-Json -Compress) -eq ($taskTargetEnd | ConvertTo-Json -Compress) -and
        $taskTargetEnd.mismatchCount -eq '0' -and $taskTargetEnd.faultCode -eq '0' -and $taskTargetEnd.error -eq ''
    $taskFullSuffixExecution = $ActualDSuffixToEnd -and $taskSourceEnd.movieFrame -eq "$MovieEndFrame" -and $taskTargetEnd.movieFrame -eq "$MovieEndFrame"
    $taskExpectedEnd = if ($ActualDSuffixToEnd) { [long]$MovieEndFrame } else { ([long]$taskSourceHeader.movieFrame) + 32 }
    & python (Join-Path $PSScriptRoot 'Compare-ResidentContinuation.py') `
        --source (Join-Path $taskOutput 'source/run-01/resident-continuation.jsonl') `
        --target (Join-Path $taskOutput 'target/run-01/resident-continuation.jsonl') `
        --packet-sha256 $taskSourceHeader.sha256 --packet-bytes $taskPacketBytes --end-movie $taskExpectedEnd `
        --source-header (Join-Path $taskOutput 'source/run-01/actual-d-source-header.json') `
        --source-endpoint (Join-Path $taskOutput 'source/run-01/actual-d-original-suffix-state.json') `
        --target-endpoint (Join-Path $taskOutput 'target/run-01/actual-d-suffix-state.json') `
        --output (Join-Path $taskOutput 'suffix-comparison.json')
    $taskComparatorExit = $LASTEXITCODE
    $taskComparison = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'suffix-comparison.json') | ConvertFrom-Json
    $taskPassed = $taskComparatorExit -eq 0 -and $taskReadback -and $taskEndpoint -and $taskFullSuffixExecution -and !$DerivedFixtureManifest
    [ordered]@{ comparisonContract=$taskComparison.schema; comparisonPassed=$taskPassed;
        activeK3Passed=$taskComparison.k3ExactPassed; loadingExactPassed=$taskComparison.loadingExactPassed; fullMovieSuffixPassed=$taskComparison.fullSuffixExactPassed;
        completeDReadbackPassed=$taskReadback; suffixEndpointPassed=$taskEndpoint;
        fullMovieSuffixExecutionPassed=$taskFullSuffixExecution; applicationAndEndpointPassed=($taskReadback -and $taskEndpoint);
        targetRestoreAndSuffixPassed=$taskPassed;
        derivedDiagnostic=[bool]$DerivedFixtureManifest; diagnosticOnly=(!$taskPassed);
        qualificationStatus='IndividualPairOnly'; completeStateCoverage=$taskComparison.completeStateCoverage; restoreQualified=$false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'active-k3-acceptance.json') -Encoding utf8
    if ($taskComparatorExit -gt 1 -or !$taskComparison.tracesVerified -or ($ActualDSuffixToEnd -and !$DerivedFixtureManifest -and !$taskPassed)) {
        throw 'Resident continuation comparison did not verify the requested pair; see suffix-comparison.json and active-k3-acceptance.json.'
    }
}
finally {
    $env:HKTAS_CONTACT_IMPULSE_PROBE = $taskPriorImpulse
    if ($taskRelayState.ContainsKey('Packet')) { [Array]::Clear($taskRelayState.Packet.Data, 0, $taskRelayState.Packet.Data.Length) }
    $taskRelayState.Clear()
}
