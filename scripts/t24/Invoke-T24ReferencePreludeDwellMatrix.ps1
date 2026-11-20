[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DwellSchedulePath,

    [Parameter(Mandatory)]
    [string]$ClockBundleRoot,

    [Parameter(Mandatory)]
    [string]$PhysicalInputTracePath,

    [Parameter(Mandatory)]
    [string]$ScenarioContractPath,

    [ValidateRange(60, 300)]
    [int]$FixtureReadyTimeoutSeconds = 240,

    [ValidateRange(0, 60)]
    [int]$InterAttemptCooldownSeconds = 5,

    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 pre-clock dwell matrix requires PowerShell 7 or newer.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$captureScript = Join-Path $PSScriptRoot 'Invoke-T24ReferenceSmoke.ps1'
$DwellSchedulePath = [IO.Path]::GetFullPath($DwellSchedulePath)
$ClockBundleRoot = [IO.Path]::GetFullPath($ClockBundleRoot)
$PhysicalInputTracePath = [IO.Path]::GetFullPath($PhysicalInputTracePath)
$ScenarioContractPath = [IO.Path]::GetFullPath($ScenarioContractPath)

foreach ($requiredFile in @(
        $captureScript,
        $DwellSchedulePath,
        $PhysicalInputTracePath,
        $ScenarioContractPath
    )) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file is missing: $requiredFile"
    }
}
if (-not (Test-Path -LiteralPath $ClockBundleRoot -PathType Container)) {
    throw "Clock bundle directory is missing: $ClockBundleRoot"
}

$schedule = Get-Content -LiteralPath $DwellSchedulePath -Raw |
    ConvertFrom-Json -AsHashtable -Depth 20
$dwellTargets = @(
    @($schedule['dwellMilliseconds']) |
        ForEach-Object { [int]$_ })
if ([int]$schedule['schemaVersion'] -ne 1 `
        -or [string]$schedule['scenarioId'] `
            -ne 't24.menu-title-preclock-dwell-grid.v1' `
        -or [string]$schedule['selectionPolicy'] `
            -ne 'all-scheduled-points-no-filter' `
        -or [int]$schedule['requiredRuns'] -ne $dwellTargets.Count `
        -or [int]$schedule['fixtureSlot'] -ne 2 `
        -or [int]$schedule['maxTicks'] -ne 1200 `
        -or $dwellTargets.Count -le 0) {
    throw 'The T24 pre-clock dwell schedule contract is invalid.'
}
$seenTargets = [Collections.Generic.HashSet[int]]::new()
$previousTarget = -1
foreach ($target in $dwellTargets) {
    if ($target -lt 0 `
            -or $target -gt 60000 `
            -or $target -le $previousTarget `
            -or -not $seenTargets.Add($target)) {
        throw 'Dwell targets must be unique, increasing, and within 0..60000 ms.'
    }
    $previousTarget = $target
}

$maxTicks = [int]$schedule['maxTicks']
$fixtureSlot = [int]$schedule['fixtureSlot']
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $campaign = 't24-vanilla-preclock-dwell-{0}-{1}' -f `
        [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), `
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidenceRoot = Join-Path `
        $repoRoot `
        "artifacts\vanilla-equivalence\$campaign"
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$atomicWriteProbe = Join-Path `
    (Join-Path `
        $EvidenceRoot `
        'runs\dwell-10000ms\traces\vanilla-reference') `
    ('status.json.tmp-' + [string]::new('0', 32))
if ($atomicWriteProbe.Length -gt 240) {
    throw (
        'T24 evidence path exceeds the Mono-safe atomic path limit: ' `
        + $atomicWriteProbe.Length `
        + ' > 240; choose a shorter EvidenceRoot.')
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "Evidence root already exists: $EvidenceRoot"
}

New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$runsRoot = Join-Path $EvidenceRoot 'runs'
New-Item -ItemType Directory -Path $runsRoot | Out-Null
$matrixPath = Join-Path $EvidenceRoot 'run-matrix.json'
$matrixScriptHash = (Get-FileHash `
    -LiteralPath $PSCommandPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$captureScriptHash = (Get-FileHash `
    -LiteralPath $captureScript `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$scheduleHash = (Get-FileHash `
    -LiteralPath $DwellSchedulePath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$inputHash = (Get-FileHash `
    -LiteralPath $PhysicalInputTracePath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$scenarioHash = (Get-FileHash `
    -LiteralPath $ScenarioContractPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$attempts = [Collections.Generic.List[object]]::new()
$successCount = 0

function Write-DwellMatrixArtifact {
    param([Parameter(Mandatory)][string]$Verdict)

    [ordered]@{
        schemaVersion = 1
        policyId = 't24-menu-title-preclock-dwell-negative-control-v1'
        verdict = $Verdict
        mode = 'vanilla-reference'
        selectionPolicy = 'all-scheduled-points-no-filter'
        strictScheduledCohort = $true
        stoppedAtBaselineMatch = $false
        requiredRuns = $dwellTargets.Count
        successfulRuns = $successCount
        attemptedRuns = $attempts.Count
        dwellMilliseconds = $dwellTargets
        maxTicks = $maxTicks
        fixtureSlot = $fixtureSlot
        fixtureReadyTimeoutSeconds = $FixtureReadyTimeoutSeconds
        interAttemptCooldownSeconds = $InterAttemptCooldownSeconds
        matrixGeneratorScript = $PSCommandPath
        matrixGeneratorScriptSha256 = $matrixScriptHash
        captureScript = $captureScript
        captureScriptSha256 = $captureScriptHash
        dwellSchedulePath = $DwellSchedulePath
        dwellScheduleSha256 = $scheduleHash
        clockBundleRoot = $ClockBundleRoot
        physicalInputTracePath = $PhysicalInputTracePath
        physicalInputTraceSha256 = $inputHash
        scenarioContractPath = $ScenarioContractPath
        scenarioContractSha256 = $scenarioHash
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        attempts = $attempts
    } |
        ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath $matrixPath -Encoding utf8NoBOM
}

# Write the complete immutable schedule before the first game process starts.
Write-DwellMatrixArtifact -Verdict 'PRECOMMITTED'

for ($index = 0; $index -lt $dwellTargets.Count; $index++) {
    if ((Get-FileHash `
                -LiteralPath $PSCommandPath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ne $matrixScriptHash `
            -or (Get-FileHash `
                -LiteralPath $captureScript `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne $captureScriptHash `
            -or (Get-FileHash `
                -LiteralPath $DwellSchedulePath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne $scheduleHash) {
        throw 'A precommitted dwell matrix input changed during execution.'
    }

    $target = $dwellTargets[$index]
    $runName = 'dwell-{0:D5}ms' -f $target
    $runRoot = Join-Path $runsRoot $runName
    $startedUtc = [DateTimeOffset]::UtcNow
    $captureArguments = @(
        '-NoProfile',
        '-File', $captureScript,
        '-Mode', 'vanilla-reference',
        '-MaxTicks', [string]$maxTicks,
        '-FixtureSlot', [string]$fixtureSlot,
        '-ClockBundleRoot', $ClockBundleRoot,
        '-FixtureReadyTimeoutSeconds',
            [string]$FixtureReadyTimeoutSeconds,
        '-PhysicalInputTracePath', $PhysicalInputTracePath,
        '-ScenarioContractPath', $ScenarioContractPath,
        '-PreClockMenuDwellMilliseconds', [string]$target,
        '-EvidenceRoot', $runRoot
    )
    $output = @(& pwsh @captureArguments 2>&1)
    $exitCode = $LASTEXITCODE

    $summaryPath = Join-Path $runRoot 'reference-smoke.json'
    $auditPath = Join-Path `
        $runRoot `
        'observer-audit\pre-clock-menu-dwell.json'
    $traceRoot = Join-Path $runRoot 'traces\vanilla-reference'
    $baselinePath = Join-Path $traceRoot 'baseline.json'
    $tracePath = Join-Path $traceRoot 'trace.jsonl'
    $phasePath = Join-Path $traceRoot 'input-phase.jsonl'
    $resultPath = Join-Path $traceRoot 'result.json'
    $coveragePath = Join-Path $runRoot 'scenario-coverage.json'
    $requiredArtifacts = @(
        $summaryPath,
        $auditPath,
        $baselinePath,
        $tracePath,
        $phasePath,
        $resultPath,
        $coveragePath)
    $success = $exitCode -eq 0 `
        -and @(
            $requiredArtifacts |
                Where-Object {
                    -not (Test-Path -LiteralPath $_ -PathType Leaf)
                }).Count -eq 0

    $summary = $null
    $audit = $null
    $baseline = $null
    $result = $null
    $coverage = $null
    if ($success) {
        $summary = Get-Content -LiteralPath $summaryPath -Raw |
            ConvertFrom-Json
        $audit = Get-Content -LiteralPath $auditPath -Raw |
            ConvertFrom-Json
        $baseline = Get-Content -LiteralPath $baselinePath -Raw |
            ConvertFrom-Json
        $result = Get-Content -LiteralPath $resultPath -Raw |
            ConvertFrom-Json
        $coverage = Get-Content -LiteralPath $coveragePath -Raw |
            ConvertFrom-Json
        $success = [string]$summary.verdict `
                -eq 'REFERENCE_CLOCK_CONTROLLED_CAPTURED' `
            -and [bool]$summary.tasRuntimeAbsent `
            -and [bool]$summary.observerOnlyManagedMod `
            -and -not [bool]$summary.tasControlStarted `
            -and [bool]$summary.externalPhysicalInputSynchronized `
            -and [bool]$summary.externalRngSynchronized `
            -and [bool]$summary.externalRngSynchronizationOriginCaptured `
            -and -not [bool]$summary.visualRecognitionUsed `
            -and -not [bool]$summary.observerInputInjected `
            -and -not [bool]$summary.observerGameplayStateWritten `
            -and [string]$summary.preClockMenuDwellPolicyId `
                -eq 'wall-clock-wait-in-menu-title-v1' `
            -and [int]$summary.preClockMenuDwellRequestedMilliseconds `
                -eq $target `
            -and [int]$audit.schemaVersion -eq 1 `
            -and [string]$audit.policyId `
                -eq 'wall-clock-wait-in-menu-title-v1' `
            -and [int]$audit.requestedMilliseconds -eq $target `
            -and [double]$audit.actualElapsedMilliseconds -ge $target `
            -and [string]$audit.sceneBefore -eq 'Menu_Title' `
            -and [string]$audit.sceneAfter -eq 'Menu_Title' `
            -and -not [bool]$audit.inputInjected `
            -and -not [bool]$audit.timeWritten `
            -and -not [bool]$audit.gameplayStateWritten `
            -and -not [bool]$audit.visualRecognitionUsed `
            -and [bool]$result.success `
            -and [bool]$result.baselineCaptured `
            -and [int]$result.frameCount -eq $maxTicks `
            -and -not [bool]$result.inputInjected `
            -and -not [bool]$result.timeWritten `
            -and -not [bool]$result.gameplayStateWritten `
            -and [bool]$result.externalInputSynchronized `
            -and [bool]$result.externalInputSynchronizationRequired `
            -and [int]$result.externalInputSynchronizedFrames -eq $maxTicks `
            -and [int]$result.externalInputSynchronizationPrimeFrames -eq 1 `
            -and [bool]$result.externalRngSynchronized `
            -and [bool]$result.externalRngSynchronizationOriginCaptured `
            -and [string]$coverage.verdict -eq 'PASS' `
            -and (Get-FileHash `
                -LiteralPath $baselinePath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -eq [string]$summary.baselineSha256 `
            -and (Get-FileHash `
                -LiteralPath $tracePath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -eq [string]$summary.traceSha256 `
            -and (Get-FileHash `
                -LiteralPath $auditPath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -eq [string]$summary.preClockMenuDwellAuditSha256
    }
    if ($success) {
        $successCount++
    }

    $attempts.Add([ordered]@{
        ordinal = $index + 1
        run = $runName
        requestedDwellMilliseconds = $target
        success = $success
        exitCode = $exitCode
        startedUtc = $startedUtc.ToString('O')
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        evidenceRoot = $runRoot
        actualDwellMilliseconds = if ($null -eq $audit) {
            -1
        }
        else {
            [double]$audit.actualElapsedMilliseconds
        }
        baselineVisualTick = if ($null -eq $baseline) {
            -1
        }
        else { [int]$baseline.visualTick }
        baselineFixedTick = if ($null -eq $baseline) {
            -1
        }
        else { [int]$baseline.fixedTick }
        baselineVisualFixedGap = if ($null -eq $baseline) {
            -1
        }
        else { [int]$baseline.visualTick - [int]$baseline.fixedTick }
        rigidbodyPositionXCanonicalHex = if ($null -eq $baseline) {
            ''
        }
        else { [string]$baseline.rigidbodyPositionX.canonicalHex }
        rigidbodyPositionYCanonicalHex = if ($null -eq $baseline) {
            ''
        }
        else { [string]$baseline.rigidbodyPositionY.canonicalHex }
        baselineSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $baselinePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        traceSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $tracePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        phaseSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $phasePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        resultSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $resultPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        preClockMenuDwellAuditSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $auditPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        output = @($output | ForEach-Object { [string]$_ })
    })
    Write-DwellMatrixArtifact -Verdict 'RUNNING'

    if ($index + 1 -lt $dwellTargets.Count `
            -and $InterAttemptCooldownSeconds -gt 0) {
        Start-Sleep -Seconds $InterAttemptCooldownSeconds
    }
}

$finalVerdict = if ($successCount -eq $dwellTargets.Count) {
    'CAPTURED'
}
else {
    'FAILED'
}
Write-DwellMatrixArtifact -Verdict $finalVerdict
if ($finalVerdict -ne 'CAPTURED') {
    throw (
        'T24 pre-clock dwell matrix failed: ' `
        + $successCount `
        + '/' `
        + $dwellTargets.Count `
        + ' scheduled runs passed.')
}

Write-Output "T24 pre-clock dwell matrix captured: $matrixPath"
