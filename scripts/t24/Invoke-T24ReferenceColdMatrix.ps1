[CmdletBinding()]
param(
    [ValidateSet('vanilla-reference', 'tas-passive')]
    [string]$Mode = 'vanilla-reference',

    [ValidateRange(1, 20)]
    [int]$RequiredSuccessfulRuns = 10,

    [ValidateRange(1, 40)]
    [int]$MaximumAttempts = 15,

    [ValidateRange(120, 10000)]
    [int]$MaxTicks = 120,

    [ValidateRange(1, 4)]
    [int]$FixtureSlot = 2,

    [Parameter(Mandatory = $true)]
    [string]$ClockBundleRoot,

    [ValidateRange(60, 300)]
    [int]$FixtureReadyTimeoutSeconds = 240,

    [string]$PhysicalInputTracePath = '',

    [string]$ScenarioContractPath = '',

    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($MaximumAttempts -lt $RequiredSuccessfulRuns) {
    throw 'MaximumAttempts must be at least RequiredSuccessfulRuns.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$captureScript = Join-Path $PSScriptRoot 'Invoke-T24ReferenceSmoke.ps1'
$ClockBundleRoot = [IO.Path]::GetFullPath($ClockBundleRoot)
if (-not [string]::IsNullOrWhiteSpace($PhysicalInputTracePath)) {
    $PhysicalInputTracePath =
        [IO.Path]::GetFullPath($PhysicalInputTracePath)
    if (-not (Test-Path `
            -LiteralPath $PhysicalInputTracePath `
            -PathType Leaf)) {
        throw "Physical input trace is missing: $PhysicalInputTracePath"
    }
}
if (-not [string]::IsNullOrWhiteSpace($ScenarioContractPath)) {
    $ScenarioContractPath = [IO.Path]::GetFullPath($ScenarioContractPath)
    if (-not (Test-Path `
            -LiteralPath $ScenarioContractPath `
            -PathType Leaf)) {
        throw "Scenario contract is missing: $ScenarioContractPath"
    }
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $campaign = 't24-{0}-cold-matrix-{1}-{2}' -f `
        $Mode, `
        [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), `
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidenceRoot = Join-Path `
        $repoRoot `
        "artifacts\vanilla-equivalence\$campaign"
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$atomicWriteProbe = Join-Path `
    (Join-Path $EvidenceRoot "runs\attempt-01\traces\$Mode") `
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

$attempts = [Collections.Generic.List[object]]::new()
$successCount = 0
function Write-RunMatrixArtifact {
    param([Parameter(Mandatory)][string]$Verdict)

    [ordered]@{
        schemaVersion = 2
        verdict = $Verdict
        mode = $Mode
        requiredSuccessfulRuns = $RequiredSuccessfulRuns
        successfulRuns = $successCount
        attemptedRuns = $attempts.Count
        strictFirstAttemptCohort = $true
        stoppedAtFirstFailure = $true
        maxTicks = $MaxTicks
        fixtureSlot = $FixtureSlot
        fixtureReadyTimeoutSeconds = $FixtureReadyTimeoutSeconds
        matrixGeneratorScript = $PSCommandPath
        matrixGeneratorScriptSha256 = (Get-FileHash `
            -LiteralPath $PSCommandPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        captureScript = $captureScript
        captureScriptSha256 = (Get-FileHash `
            -LiteralPath $captureScript `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        clockBundleRoot = $ClockBundleRoot
        physicalInputTracePath = $PhysicalInputTracePath
        physicalInputTraceSha256 = if (
            -not [string]::IsNullOrWhiteSpace($PhysicalInputTracePath)) {
            (Get-FileHash `
                -LiteralPath $PhysicalInputTracePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        scenarioContractPath = $ScenarioContractPath
        scenarioContractSha256 = if (
            [string]::IsNullOrWhiteSpace($ScenarioContractPath)) {
            ''
        }
        else {
            (Get-FileHash `
                -LiteralPath $ScenarioContractPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        attempts = $attempts
    } |
        ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath $matrixPath -Encoding utf8NoBOM
}

for ($attempt = 1; `
        $attempt -le $MaximumAttempts `
        -and $successCount -lt $RequiredSuccessfulRuns; `
        $attempt++) {
    $runName = 'attempt-{0:D2}' -f $attempt
    $runRoot = Join-Path $runsRoot $runName
    $startedUtc = [DateTimeOffset]::UtcNow
    $captureArguments = @(
        '-NoProfile',
        '-File', $captureScript,
        '-Mode', $Mode,
        '-MaxTicks', [string]$MaxTicks,
        '-FixtureSlot', [string]$FixtureSlot,
        '-ClockBundleRoot', $ClockBundleRoot,
        '-FixtureReadyTimeoutSeconds',
            [string]$FixtureReadyTimeoutSeconds,
        '-EvidenceRoot', $runRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($PhysicalInputTracePath)) {
        $captureArguments += @(
            '-PhysicalInputTracePath',
            $PhysicalInputTracePath)
    }
    if (-not [string]::IsNullOrWhiteSpace($ScenarioContractPath)) {
        $captureArguments += @(
            '-ScenarioContractPath',
            $ScenarioContractPath)
    }
    $output = @(& pwsh @captureArguments 2>&1)
    $exitCode = $LASTEXITCODE
    $summaryPath = Join-Path $runRoot 'reference-smoke.json'
    $traceDirectory = Join-Path $runRoot "traces\$Mode"
    $baselinePath = Join-Path $traceDirectory 'baseline.json'
    $tracePath = Join-Path $traceDirectory 'trace.jsonl'
    $phasePath = Join-Path $traceDirectory 'input-phase.jsonl'
    $resultPath = Join-Path $traceDirectory 'result.json'
    $scenarioCoveragePath = Join-Path `
        $runRoot `
        'scenario-coverage.json'
    $success = $exitCode -eq 0 `
        -and (Test-Path -LiteralPath $summaryPath -PathType Leaf) `
        -and (Test-Path -LiteralPath $baselinePath -PathType Leaf) `
        -and (Test-Path -LiteralPath $tracePath -PathType Leaf) `
        -and (Test-Path -LiteralPath $phasePath -PathType Leaf) `
        -and (Test-Path -LiteralPath $resultPath -PathType Leaf)
    if ($success) {
        $result = Get-Content -LiteralPath $resultPath -Raw |
            ConvertFrom-Json
        $success = [bool]$result.success `
            -and [int]$result.frameCount -eq $MaxTicks `
            -and [bool]$result.baselineCaptured
        if ($success `
                -and -not [string]::IsNullOrWhiteSpace(
                    $PhysicalInputTracePath)) {
            $summary = Get-Content -LiteralPath $summaryPath -Raw |
                ConvertFrom-Json
            $expectedInputHash = (Get-FileHash `
                -LiteralPath $PhysicalInputTracePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            $success = [bool]$result.externalInputSynchronized `
                -and [bool]$result.externalInputSynchronizationRequired `
                -and [int]$result.externalInputSynchronizedFrames `
                    -eq $MaxTicks `
                -and [int]$result.externalInputSynchronizationPrimeFrames `
                    -eq 1 `
                -and [bool]$result.externalRngSynchronizationOriginCaptured `
                -and [bool]$summary.externalRngSynchronizationOriginCaptured `
                -and [bool]$summary.externalPhysicalInputSynchronized `
                -and [int]$summary.externalInputSynchronizationPrimeFrames `
                    -eq 1 `
                -and [string]$summary.physicalInputSourceTraceSha256 `
                    -eq $expectedInputHash
        }
        if ($success `
                -and -not [string]::IsNullOrWhiteSpace(
                    $ScenarioContractPath)) {
            $summary = Get-Content -LiteralPath $summaryPath -Raw |
                ConvertFrom-Json
            $success = (Test-Path `
                    -LiteralPath $scenarioCoveragePath `
                    -PathType Leaf) `
                -and [string]$summary.scenarioCoverageVerdict -eq 'PASS'
        }
    }
    if ($success) {
        $successCount++
    }
    $attempts.Add([ordered]@{
        attempt = $attempt
        run = $runName
        success = $success
        exitCode = $exitCode
        startedUtc = $startedUtc.ToString('O')
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        evidenceRoot = $runRoot
        baselineSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $baselinePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else {
            ''
        }
        traceSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $tracePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else {
            ''
        }
        phaseSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $phasePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else {
            ''
        }
        resultSha256 = if ($success) {
            (Get-FileHash `
                -LiteralPath $resultPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else {
            ''
        }
        output = @($output | ForEach-Object { [string]$_ })
    })
    Write-Output (
        'T24 cold matrix {0}/{1}: success={2}; totalSuccess={3}' -f `
            $attempt, `
            $MaximumAttempts, `
            $success, `
            $successCount)
    Write-RunMatrixArtifact -Verdict 'IN_PROGRESS'
    if (-not $success) {
        break
    }
}

$verdict = if ($successCount -eq $RequiredSuccessfulRuns `
        -and $attempts.Count -eq $RequiredSuccessfulRuns) {
    'CAPTURED'
}
else {
    'INCOMPLETE'
}
Write-RunMatrixArtifact -Verdict $verdict

if ($verdict -ne 'CAPTURED') {
    throw (
        'T24 cold matrix did not capture enough successful runs: ' `
        + $successCount `
        + '/' `
        + $RequiredSuccessfulRuns `
        + '; attempts=' `
        + $attempts.Count `
        + '; strict first-attempt cohort required')
}

Write-Output "T24 cold matrix captured: $EvidenceRoot"
