param(
    [Parameter(Mandatory=$true)][string] $Recording,
    [Parameter(Mandatory=$true)][string[]] $ZeroMovieRuns,
    [Parameter(Mandatory=$true)][string] $Output,
    [switch] $ClockRebound
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repo 'tests/HollowKnightTAS.Core.Tests/bin/Release/net8.0'
[void][Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'Newtonsoft.Json.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'HollowKnightTAS.Core.Tests.dll'))
$type = $assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalS0ObservationSnapshot', $true)
$compare = $assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalS0Comparison', $true).GetMethod('Compare', [Reflection.BindingFlags]'NonPublic,Static')
function Read-S0([string] $Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 16MB) { throw 'S0 observation exceeds the diagnostic bound.' }
    $sample = [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText($Path), $type)
    if ($sample.Schema -ne 1 -or $sample.CompleteStateCoverage -or $sample.RestoreQualified -or $sample.D1Passed) {
        throw 'Unexpected S0 diagnostic schema or qualification.'
    }
    $pieces = [Collections.Generic.List[string]]::new()
    foreach ($pair in $sample.Fields.GetEnumerator()) { $pieces.Add($pair.Key.Length.ToString() + ':' + $pair.Key + $pair.Value.Length.ToString() + ':' + $pair.Value) }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $pieces)))).ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($hash -ne $sample.Sha256) { throw "S0 self-hash differs: $Path" }
    return ,$sample
}
$sources = @(1..3 | ForEach-Object { Read-S0 (Join-Path $Recording "s0-observation-$_.json") })
$rows = @()
foreach ($run in $ZeroMovieRuns) {
    $receipt = Get-Content -LiteralPath (Join-Path $run 's0-receipt.json') -Raw | ConvertFrom-Json
    if ($receipt.status -ne 'PausedAtB' -or $receipt.movieFrame -ne '0' -or $receipt.consumedSamplesBefore -ne '0' -or
        $receipt.consumedSamplesAfter -ne '0' -or $receipt.d1Passed -ne 'false' -or $receipt.restoreQualified -ne 'false') {
        throw 'Zero Movie preparation did not reach its guarded canonical B.'
    }
    if ($ClockRebound) {
        $rebound = Get-Content -LiteralPath (Join-Path $run 'clock-canon-receipt.json') -Raw | ConvertFrom-Json
        if ($rebound.status -ne 'RecanonicalizedClockPaused' -or $rebound.movieFrame -ne '0' -or $rebound.consumedSamplesAfter -ne '0' -or
            $rebound.restoreQualified -ne 'false' -or $rebound.boundaries -ne $receipt.boundaries) { throw 'No guarded clock-rebound Canon receipt.' }
    }
    $sampleRoot = if ($ClockRebound) { Join-Path $run 'clock-rebound' } else { $run }
    $candidate = Read-S0 (Join-Path $sampleRoot ("s0-observation-{0}.json" -f $receipt.boundaries))
    for ($i=0; $i -lt 3; $i++) {
        $comparison = $compare.Invoke($null, [object[]]@($sources[$i], $candidate))
        $rows += [pscustomobject][ordered]@{ run=$run; sourceOrdinal=$i+1; observedS0Equal=$comparison.ObservedS0Equal;
            strictObservedEqual=$comparison.ObservedEqual; firstS0Difference=$comparison.FirstS0Difference;
            clockDependencyDifferences=$comparison.ClockDependencyDifferences;
            firstDifference=$comparison.FirstDifference; expected=$comparison.Expected; actual=$comparison.Actual;
            sourceInvariantFailures=$sources[$i].InvariantFailures; candidateInvariantFailures=$candidate.InvariantFailures;
            invariantsHeld=($sources[$i].InvariantFailures.Length -eq 0 -and $candidate.InvariantFailures.Length -eq 0);
            openUnclassified=$comparison.OpenUnclassified; openUnread=$comparison.OpenUnread; openDependencies=$comparison.OpenDependencies }
    }
}
$report = [ordered]@{ schema='hktas-canonical-s0-comparison-v2'; diagnosticOnly=$true;
    observationStage=$(if ($ClockRebound) { 'clock-rebound' } else { 'zero-Movie-canonical-B' });
    d1Passed=$false; d2Passed=$false; d3Passed=$false; restoreQualified=$false; completeStateCoverage=$false; rows=$rows }
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding utf8
$rows | Select-Object run,sourceOrdinal,observedS0Equal,firstDifference | ConvertTo-Json -Compress
