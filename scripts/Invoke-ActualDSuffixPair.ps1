param(
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [Parameter(Mandatory=$true)][string] $Movie,
    [Parameter(Mandatory=$true)][string] $ClockBridgePath,
    [switch] $PhysicsFollowOn,
    [switch] $ContactImpulseProbe,
    [switch] $DiscardInstallation,
    [ValidateRange(1,64)][int] $SourceBoundaryOrdinal = 1,
    [string] $DerivedFixtureManifest = ''
)
$ErrorActionPreference = 'Stop'
if ($ContactImpulseProbe -and -not $PhysicsFollowOn) { throw 'Contact impulse diagnostics require the bounded follow-on.' }
if ($SourceBoundaryOrdinal -ne 1 -and $PhysicsFollowOn) { throw 'The existing physics follow-on belongs only to its first-boundary route.' }
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskOwned = [IO.Path]::GetFullPath((Join-Path $taskRepo 'artifacts/scene-checkpoints/p3')) + [IO.Path]::DirectorySeparatorChar
if (-not $taskOutput.StartsWith($taskOwned, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $taskOutput)) {
    throw 'Choose a fresh owned P3 evidence directory.'
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
    Movie=$Movie; FirstFrame=0; EndFrame=3150; Runs=1; DisableLegacyCalibration=$true;
    SynchronousFetch=$true; CanonicalStartupClock=$true; ModWallClock=$true; SerialScenes=$true;
    GameplayOracle=$true; SerialBoundaryOnly=$true; NativeScheduler=$true; NativePhysics=$true;
    VirtualAudio=$true; IsolatedOnline=$true; CanonicalParticles=$true; CanonicalBoundary=$true;
    CanonicalPhaseOneTrace=$true; S0ObservationTarget='Town'; ClockCarryExperiment=$true;
    ActualDExperiment=$true; ActualDSuffix=$true; ClockBridgePath=$ClockBridgePath; EvidencePhase='p3';
    ActualDRelayState=$taskRelayState
    SourceBoundaryOrdinal=$SourceBoundaryOrdinal
    PhysicsFollowOn=$PhysicsFollowOn
    DiscardInstallation=$DiscardInstallation
    DerivedFixtureManifest=$DerivedFixtureManifest
}
$taskPriorImpulse = $env:HKTAS_CONTACT_IMPULSE_PROBE
try {
    if ($ContactImpulseProbe) { $env:HKTAS_CONTACT_IMPULSE_PROBE = '1' }
    & (Join-Path $PSScriptRoot 'Invoke-LoadingProbe.ps1') @taskOptions -ActualDStage Source -OutputDirectory (Join-Path $taskOutput 'source')
    if (-not $taskRelayState.ContainsKey('Packet')) { throw 'Source did not hand off its packet in memory.' }
    foreach ($taskIdentity in $taskIdentities) {
        if ((Get-FileHash -LiteralPath $taskIdentity.path -Algorithm SHA256).Hash -ne $taskIdentity.sha256) {
            throw 'Candidate or source changed between the independent protected stages.'
        }
    }
    & (Join-Path $PSScriptRoot 'Invoke-LoadingProbe.ps1') @taskOptions -ActualDStage Target -OutputDirectory (Join-Path $taskOutput 'target')
    if ($taskRelayState.ContainsKey('Packet')) { throw 'Target did not consume its single-use packet.' }
    $taskIdentities | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskOutput 'fixed-stage-identities.json') -Encoding utf8
    & python (Join-Path $PSScriptRoot 'Compare-LoadingState.py') `
        (Join-Path $taskOutput 'source/run-01/actual-d-source-suffix-oracle/loading-gameplay-oracle.jsonl') `
        (Join-Path $taskOutput 'target/run-01/actual-d-target-suffix-oracle/loading-gameplay-oracle.jsonl') `
        --output (Join-Path $taskOutput 'suffix-comparison.json')
    if ($LASTEXITCODE -ne 0) { throw 'The strict suffix diagnostic comparison failed to validate its traces.' }
}
finally {
    $env:HKTAS_CONTACT_IMPULSE_PROBE = $taskPriorImpulse
    if ($taskRelayState.ContainsKey('Packet')) { [Array]::Clear($taskRelayState.Packet.Data, 0, $taskRelayState.Packet.Data.Length) }
    $taskRelayState.Clear()
}
