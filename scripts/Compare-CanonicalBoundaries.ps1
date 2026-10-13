param(
    [Parameter(Mandatory=$true)][string] $Recording,
    [Parameter(Mandatory=$true)][string[]] $ColdRuns,
    [switch] $LogicalEpoch,
    [Parameter(Mandatory=$true)][string] $Output
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repo 'tests/HollowKnightTAS.Core.Tests/bin/Release/net8.0'
[void][Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'Newtonsoft.Json.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'HollowKnightTAS.Core.Tests.dll'))
$snapshotType = $assembly.GetType('HollowKnightTAS.Runtime.FullRun.BoundaryDigestSnapshot', $true)
$constructor = $snapshotType.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance')[0]
$comparisonType = $assembly.GetType('HollowKnightTAS.Runtime.FullRun.BoundaryDigestComparison', $true)
$compare = $comparisonType.GetMethod('Compare', [Reflection.BindingFlags]'NonPublic,Static')
$epochType = $assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalInputEpoch', $true)
$project = $assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalBoundaryEpochObservation', $true).GetMethod('Project', [Reflection.BindingFlags]'NonPublic,Static')
function Read-Snapshot([string] $Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 20MB) { throw 'Boundary file exceeds its diagnostic bound.' }
    $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new([IO.File]::ReadAllText($Path)))
    $reader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
    try { $data = [Newtonsoft.Json.Linq.JObject]::Load($reader) } finally { $reader.Close() }
    if ([int]$data['Schema'] -ne 2 -or [bool]$data['CompleteStateCoverage']) { throw 'Unexpected diagnostic boundary schema/coverage.' }
    $groups = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($property in $data['Groups'].Properties()) { $groups.Add($property.Name, [string]$property.Value) }
    $parameters = [object[]]@($groups,
        [object[]]@($data['Roots'].Children()), [string[]]@($data['Unsupported'].Children() | ForEach-Object { [string]$_ }),
        [object[]]@($data['NativeScheduler'].Children()), [object[]]@($data['NativePhysics'].Children()),
        [string[]]@($data['CanonicalWaitIssues'].Children() | ForEach-Object { [string]$_ }))
    $snapshot = $constructor.Invoke($parameters)
    if ($snapshot.FullSha256 -ne [string]$data['FullSha256']) { throw "Boundary self-hash differs: $Path" }
    return ,$snapshot
}
function Read-Route([string] $Directory) {
    if (-not ([IO.File]::ReadAllText((Join-Path $Directory 'scenario.log')).Contains('ALL SCENARIOS PASSED'))) {
        throw "Route did not finish: $Directory"
    }
    $state = Get-Content -LiteralPath (Join-Path $Directory 'target-state.json') -Raw | ConvertFrom-Json
    if ($state.mismatchCount -ne '0' -or $state.error -ne '') { throw 'Runtime input mismatch/fault is present.' }
    $rows = @(Get-Content -LiteralPath (Join-Path $Directory 'canonical-boundary-progress.jsonl') |
        ForEach-Object { $_ | ConvertFrom-Json } | Where-Object phase -eq 'phase1-route-observed-unqualified')
    if ($rows.Count -lt 3 -or @($rows | Where-Object { $_.detail.d0Passed -or $_.detail.restoreQualified }).Count) {
        throw 'Phase 1 comparison requires at least three unqualified boundary observations.'
    }
    for ($i=0; $i -lt $rows.Count; $i++) { if ($rows[$i].ordinal -ne $i+1) { throw 'Boundary sequence is incomplete.' } }
    return ,$rows
}
function Read-LogicalSnapshot([string] $Directory, [int] $Ordinal, $Raw) {
    $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new([IO.File]::ReadAllText(
        (Join-Path $Directory ("boundary-epoch-{0}.json" -f $Ordinal)))))
    $reader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
    try { $data = [Newtonsoft.Json.Linq.JObject]::Load($reader) } finally { $reader.Close() }
    if ([int]$data['schema'] -ne 1 -or -not [bool]$data['diagnosticOnly'] -or [bool]$data['d2ProjectionAllowed']) {
        throw 'Unexpected D0-only epoch observation.'
    }
    $epoch = [Activator]::CreateInstance($epochType, $true)
    foreach ($field in $epochType.GetFields([Reflection.BindingFlags]'Public,Instance')) {
        $value = $data['epoch'][$field.Name]
        if ($null -eq $value) { throw "Missing epoch field: $($field.Name)" }
        $field.SetValue($epoch, $value.ToObject($field.FieldType))
    }
    $recomputed = $project.Invoke($null, [object[]]@($Raw,$epoch))
    $stored = Read-Snapshot (Join-Path $Directory ("boundary-logical-digest-{0}.json" -f $Ordinal))
    if ($recomputed.FullSha256 -ne $stored.FullSha256) { throw 'Logical digest differs from its raw proof and actual epoch.' }
    return ,$stored
}
if ($ColdRuns.Count -ne 2 -or @($ColdRuns + $Recording | ForEach-Object { [IO.Path]::GetFullPath($_) } | Select-Object -Unique).Count -ne 3) {
    throw 'Supply a recording and two distinct cold-run directories.'
}
$source = Read-Route $Recording
$results = foreach ($run in $ColdRuns) {
    $actual = Read-Route $run
    if ($source.Count -ne $actual.Count) { throw 'Cold run has a different number of transitions.' }
    for ($i=0; $i -lt $source.Count; $i++) {
        $left = $source[$i]; $right = $actual[$i]
        $expected = Read-Snapshot (Join-Path $Recording ("boundary-digest-{0}.json" -f $left.ordinal))
        $observed = Read-Snapshot (Join-Path $run ("boundary-digest-{0}.json" -f $right.ordinal))
        $result = $compare.Invoke($null, [object[]]@($expected, $observed))
        $logicalResult = $null
        if ($LogicalEpoch) {
            $logicalExpected = Read-LogicalSnapshot $Recording $left.ordinal $expected
            $logicalActual = Read-LogicalSnapshot $run $right.ordinal $observed
            $logicalResult = $compare.Invoke($null, [object[]]@($logicalExpected, $logicalActual))
        }
        [pscustomobject][ordered]@{ run=$run; ordinal=$left.ordinal; source=$left.source; target=$left.target; movie=$left.movie;
            routeEqual=($left.movie -eq $right.movie -and $left.source -eq $right.source -and $left.target -eq $right.target);
            observedEqual=$result.ObservedEqual; firstDifference=$result.FirstDifference;
            expectedSha256=$expected.FullSha256; actualSha256=$observed.FullSha256;
            openDomains=$result.OpenDomains; completeStateCoverage=$result.CompleteStateCoverage;
            logicalEpochObservedEqual=if ($logicalResult) { $logicalResult.ObservedEqual } else { $null };
            logicalEpochFirstDifference=if ($logicalResult) { $logicalResult.FirstDifference } else { $null };
            canonMilliseconds=$right.detail.canonMilliseconds; observerMilliseconds=$right.detail.observerMilliseconds }
    }
}
$report = [ordered]@{ schema='hktas-canonical-phase1-comparison-v1'; diagnosticOnly=$true;
    d0Passed=$false; restoreQualified=$false; completeStateCoverage=$false;
    observedEqual=(@($results | Where-Object { -not $_.routeEqual -or -not $_.observedEqual }).Count -eq 0);
    logicalEpochObservedEqual=if ($LogicalEpoch) { @($results | Where-Object { -not $_.routeEqual -or -not $_.logicalEpochObservedEqual }).Count -eq 0 } else { $null };
    comparisons=@($results) }
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Output -Encoding utf8
$results | Select-Object run,ordinal,routeEqual,observedEqual,firstDifference,logicalEpochObservedEqual,logicalEpochFirstDifference,canonMilliseconds | ConvertTo-Json -Compress
