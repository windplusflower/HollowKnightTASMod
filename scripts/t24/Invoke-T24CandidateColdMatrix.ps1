[CmdletBinding()]
param(
    [ValidateSet(
        'tas-continuous',
        'tas-sequential',
        'tas-batch',
        'tas-manual-ui')]
    [string]$Mode = 'tas-continuous',

    [ValidateRange(1, 20)]
    [int]$RequiredSuccessfulRuns = 10,

    [ValidateRange(1, 40)]
    [int]$MaximumAttempts = 15,

    [ValidateRange(120, 10000)]
    [int]$MaxTicks = 120,

    [ValidateRange(1, 4)]
    [int]$FixtureSlot = 2,

    [Parameter(Mandatory)]
    [string]$ReferenceTracePath,

    [Parameter(Mandatory)]
    [string]$NegativeControlEnvelopePath,

    [string]$SupplementalReferenceCatalogPath = '',

    [Parameter(Mandatory)]
    [string]$MoviePath,

    [string]$ScenarioContractPath = '',

    [Parameter(Mandatory)]
    [string]$ClockBundleRoot,

    [ValidateRange(30, 300)]
    [int]$MaxLaunchSeconds = 180,

    [ValidateRange(30, 600)]
    [int]$RunTimeoutSeconds = 300,

    [ValidateRange(60, 300)]
    [int]$FixtureReadyTimeoutSeconds = 240,

    [ValidateRange(0, 60)]
    [int]$InterAttemptCooldownSeconds = 10,

    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 candidate cold matrix requires PowerShell 7 or newer.'
}
if ($MaximumAttempts -lt $RequiredSuccessfulRuns) {
    throw 'MaximumAttempts must be at least RequiredSuccessfulRuns.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$captureScript = Join-Path $PSScriptRoot 'Invoke-T24CandidateSmoke.ps1'
$filesystemIsolationScript = Join-Path `
    $PSScriptRoot `
    'T24FilesystemIsolation.ps1'
$manualUiAutomationScript = Join-Path `
    $PSScriptRoot `
    'T24CompanionUiAutomation.ps1'
$ReferenceTracePath = [IO.Path]::GetFullPath($ReferenceTracePath)
$NegativeControlEnvelopePath =
    [IO.Path]::GetFullPath($NegativeControlEnvelopePath)
if (-not [string]::IsNullOrWhiteSpace(
        $SupplementalReferenceCatalogPath)) {
    $SupplementalReferenceCatalogPath = [IO.Path]::GetFullPath(
        $SupplementalReferenceCatalogPath)
}
$MoviePath = [IO.Path]::GetFullPath($MoviePath)
if (-not [string]::IsNullOrWhiteSpace($ScenarioContractPath)) {
    $ScenarioContractPath = [IO.Path]::GetFullPath($ScenarioContractPath)
}
$ClockBundleRoot = [IO.Path]::GetFullPath($ClockBundleRoot)
foreach ($requiredFile in @(
        $captureScript,
        $filesystemIsolationScript,
        $manualUiAutomationScript,
        $ReferenceTracePath,
        $NegativeControlEnvelopePath,
        $MoviePath
    )) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file is missing: $requiredFile"
    }
}
if (-not [string]::IsNullOrWhiteSpace(
        $SupplementalReferenceCatalogPath) `
        -and -not (Test-Path `
            -LiteralPath $SupplementalReferenceCatalogPath `
            -PathType Leaf)) {
    throw (
        'Supplemental reference catalog is missing: ' `
        + $SupplementalReferenceCatalogPath)
}
if (-not [string]::IsNullOrWhiteSpace($ScenarioContractPath) `
        -and -not (Test-Path `
            -LiteralPath $ScenarioContractPath `
            -PathType Leaf)) {
    throw "Scenario contract is missing: $ScenarioContractPath"
}
if (-not (Test-Path -LiteralPath $ClockBundleRoot -PathType Container)) {
    throw "Clock bundle directory is missing: $ClockBundleRoot"
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
function Write-CandidateMatrixArtifact {
    param([Parameter(Mandatory)][string]$Verdict)

    [ordered]@{
        schemaVersion = 3
        verdict = $Verdict
        mode = $Mode
        requiredSuccessfulRuns = $RequiredSuccessfulRuns
        successfulRuns = $successCount
        attemptedRuns = $attempts.Count
        strictFirstAttemptCohort = $true
        stoppedAtFirstFailure = $true
        maxTicks = $MaxTicks
        fixtureSlot = $FixtureSlot
        maxLaunchSeconds = $MaxLaunchSeconds
        runTimeoutSeconds = $RunTimeoutSeconds
        fixtureReadyTimeoutSeconds = $FixtureReadyTimeoutSeconds
        interAttemptCooldownSeconds = $InterAttemptCooldownSeconds
        matrixGeneratorScript = $PSCommandPath
        matrixGeneratorScriptSha256 = (Get-FileHash `
            -LiteralPath $PSCommandPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        captureScript = $captureScript
        captureScriptSha256 = (Get-FileHash `
            -LiteralPath $captureScript `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        filesystemIsolationScript = $filesystemIsolationScript
        filesystemIsolationScriptSha256 = (Get-FileHash `
            -LiteralPath $filesystemIsolationScript `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        manualUiAutomationScript = $manualUiAutomationScript
        manualUiAutomationScriptSha256 = (Get-FileHash `
            -LiteralPath $manualUiAutomationScript `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        referenceTracePath = $ReferenceTracePath
        referenceTraceSha256 = (Get-FileHash `
            -LiteralPath $ReferenceTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        negativeControlEnvelopePath = $NegativeControlEnvelopePath
        negativeControlEnvelopeSha256 = (Get-FileHash `
            -LiteralPath $NegativeControlEnvelopePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        supplementalReferenceCatalogPath = `
            $SupplementalReferenceCatalogPath
        supplementalReferenceCatalogSha256 = if (
            [string]::IsNullOrWhiteSpace(
                $SupplementalReferenceCatalogPath)) {
            ''
        }
        else {
            (Get-FileHash `
                -LiteralPath $SupplementalReferenceCatalogPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        moviePath = $MoviePath
        movieSha256 = (Get-FileHash `
            -LiteralPath $MoviePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
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
        clockBundleRoot = $ClockBundleRoot
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
    $arguments = @(
        '-NoProfile',
        '-File', $captureScript,
        '-Mode', $Mode,
        '-ReferenceTracePath', $ReferenceTracePath,
        '-NegativeControlEnvelopePath', $NegativeControlEnvelopePath,
        '-MoviePath', $MoviePath,
        '-MaxTicks', [string]$MaxTicks,
        '-FixtureSlot', [string]$FixtureSlot,
        '-ClockBundleRoot', $ClockBundleRoot,
        '-MaxLaunchSeconds', [string]$MaxLaunchSeconds,
        '-RunTimeoutSeconds', [string]$RunTimeoutSeconds,
        '-FixtureReadyTimeoutSeconds', [string]$FixtureReadyTimeoutSeconds,
        '-EvidenceRoot', $runRoot
    )
    if (-not [string]::IsNullOrWhiteSpace(
            $SupplementalReferenceCatalogPath)) {
        $arguments += @(
            '-SupplementalReferenceCatalogPath',
            $SupplementalReferenceCatalogPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($ScenarioContractPath)) {
        $arguments += @(
            '-ScenarioContractPath',
            $ScenarioContractPath)
    }
    $output = @(& pwsh @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $verdictPath = Join-Path $runRoot 'final-verdict.json'
    $comparisonPath = Join-Path $runRoot 'comparison.json'
    $cleanupAuditPath = Join-Path $runRoot 'cleanup-audit.json'
    $success = $exitCode -eq 0 `
        -and (Test-Path -LiteralPath $verdictPath -PathType Leaf) `
        -and (Test-Path -LiteralPath $comparisonPath -PathType Leaf) `
        -and (Test-Path -LiteralPath $cleanupAuditPath -PathType Leaf)
    $final = $null
    $cleanupAudit = $null
    if ($success) {
        $final = Get-Content -LiteralPath $verdictPath -Raw |
            ConvertFrom-Json
        $cleanupAudit = Get-Content -LiteralPath $cleanupAuditPath -Raw |
            ConvertFrom-Json
        $success = [string]$final.verdict -eq 'PASS' `
            -and [string]$final.mode -eq $Mode `
            -and [bool]$final.referenceBaselineCatalogMatch `
            -and [bool]$final.baselineSemanticExactEquivalent `
            -and [bool]$final.baselineHeroZVanillaVisualRandomEquivalent `
            -and [bool]$final.baselineRigidbodyComponentsObserved `
            -and [bool]$final.baselineRenderTransformEnvelopeEquivalent `
            -and [bool]$final.baselineEquivalent `
            -and [bool]$final.phaseEquivalent `
            -and [bool]$final.inputEquivalent `
            -and [bool]$final.baselineNormalizedRigidbodyEquivalent `
            -and [bool]$final.rigidbodyAxisWitnessesAvailable `
            -and [bool]$final.rigidbodyAxisWitnessEquivalent `
            -and [string]$final.filesystemIsolationScriptSha256 `
                -ceq (Get-FileHash `
                    -LiteralPath $filesystemIsolationScript `
                    -Algorithm SHA256).Hash.ToLowerInvariant() `
            -and ($Mode -ne 'tas-manual-ui' `
                -or [string]$final.manualUiAutomationScriptSha256 `
                    -ceq (Get-FileHash `
                        -LiteralPath $manualUiAutomationScript `
                        -Algorithm SHA256).Hash.ToLowerInvariant()) `
             -and [bool]$final.gameplayEquivalent `
             -and [bool]$final.controlSurfaceClean `
             -and [bool]$final.startupClockControlClean `
             -and [bool]$final.recordingArmControlClean `
             -and [string]$final.recordingArmPolicyId `
                -eq 'existing-manual-reset-one-neutral-completed-frame-preroll-v2' `
             -and [int]$final.recordingArmControl.
                neutralPreRollCompletedFrameCountBeforeHost -eq 0 `
             -and [int]$final.recordingArmControl.
                neutralPreRollCompletedFrameCountAfterHost -eq 1 `
             -and -not [string]::IsNullOrWhiteSpace(
                [string]$final.recordingArmAuditSha256) `
             -and [bool]$final.mutationClean `
             -and [string]$cleanupAudit.verdict -eq 'PASS' `
             -and [bool]$cleanupAudit.modsDirectoryExistsAfter `
             -and [bool]$cleanupAudit.tasInstallExistsAfter `
             -and [bool]$cleanupAudit.modsBackupAbsent `
             -and [bool]$cleanupAudit.modsIsolatedAbsent `
             -and [bool]$cleanupAudit.observerInstallEquivalent `
             -and [bool]$cleanupAudit.automationBackupAbsent `
             -and [bool]$cleanupAudit.modsTopLevelEquivalent `
             -and [bool]$cleanupAudit.tasInstallEquivalent `
             -and [int]$cleanupAudit.nestedTasDirectoryCount -eq 0
        if ($success `
                -and -not [string]::IsNullOrWhiteSpace(
                    $ScenarioContractPath)) {
            $success = [bool]$final.scenarioCoverageClean `
                -and [string]$final.candidateScenarioCoverageVerdict `
                    -eq 'PASS' `
                -and [string]$final.referenceScenarioCoverageVerdict `
                    -eq 'PASS'
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
        verdictSha256 = if (Test-Path -LiteralPath $verdictPath) {
            (Get-FileHash `
                -LiteralPath $verdictPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        comparisonSha256 = if (Test-Path -LiteralPath $comparisonPath) {
            (Get-FileHash `
                -LiteralPath $comparisonPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        cleanupAuditSha256 = if (
            Test-Path -LiteralPath $cleanupAuditPath -PathType Leaf) {
            (Get-FileHash `
                -LiteralPath $cleanupAuditPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        cleanupVerdict = if ($null -ne $cleanupAudit) {
            [string]$cleanupAudit.verdict
        }
        else { 'MISSING' }
        cleanupModsTopLevelEquivalent = if ($null -ne $cleanupAudit) {
            [bool]$cleanupAudit.modsTopLevelEquivalent
        }
        else { $false }
        cleanupTasInstallEquivalent = if ($null -ne $cleanupAudit) {
            [bool]$cleanupAudit.tasInstallEquivalent
        }
        else { $false }
        cleanupNestedTasDirectoryCount = if ($null -ne $cleanupAudit) {
            [int]$cleanupAudit.nestedTasDirectoryCount
        }
        else { -1 }
        candidateTraceSha256 = if ($null -ne $final) {
            [string]$final.candidateTraceSha256
        }
        else { '' }
        candidateBaselineSha256 = if ($null -ne $final) {
            [string]$final.candidateBaselineSha256
        }
        else { '' }
        referenceBaselineSignatureSha256 = if ($null -ne $final) {
            [string]$final.referenceBaselineSignatureSha256
        }
        else { '' }
        selectedReferenceAttempt = if ($null -ne $final) {
            [int]$final.selectedReferenceAttempt
        }
        else { 0 }
        referenceBaselineMatchKind = if ($null -ne $final) {
            [string]$final.referenceBaselineMatchKind
        }
        else { '' }
        baselineStrictEquivalent = if ($null -ne $final) {
            [bool]$final.baselineStrictEquivalent
        }
        else { $false }
        controlSurfaceClean = if ($null -ne $final) {
            [bool]$final.controlSurfaceClean
        }
        else { $false }
        controlSurface = if ($null -ne $final) {
            [string]$final.controlSurface
        }
        else { '' }
        manualUiStepCommandCount = if (
            $null -ne $final `
                -and $null -ne $final.manualUiAudit) {
            [int]$final.manualUiAudit.stepCommandCount
        }
        else { 0 }
        manualUiExternalWriteCommandCount = if (
            $null -ne $final `
                -and $null -ne $final.manualUiAudit) {
            [int]$final.manualUiAudit.externalWriteCommandCount
        }
        else { 0 }
        baselineAuthoritativeExactEquivalent = if ($null -ne $final) {
            [bool]$final.baselineAuthoritativeExactEquivalent
        }
        else { $false }
        baselineSemanticExactEquivalent = if ($null -ne $final) {
            [bool]$final.baselineSemanticExactEquivalent
        }
        else { $false }
        baselineHeroZVanillaVisualRandomEquivalent = if ($null -ne $final) {
            [bool]$final.baselineHeroZVanillaVisualRandomEquivalent
        }
        else { $false }
        baselineHeroZVanillaVisualRandomExact = if ($null -ne $final) {
            [bool]$final.baselineHeroZVanillaVisualRandomExact
        }
        else { $false }
        baselineHeroZVanillaVisualRandomPolicyId = if ($null -ne $final) {
            [string]$final.baselineHeroZVanillaVisualRandomPolicyId
        }
        else { '' }
        baselineHeroZCandidateCanonicalHex = if ($null -ne $final) {
            [string]$final.baselineHeroZCandidateCanonicalHex
        }
        else { '' }
        baselineHeroZReferenceCanonicalHex = if ($null -ne $final) {
            [string]$final.baselineHeroZReferenceCanonicalHex
        }
        else { '' }
        baselineHeroZMinimumCanonicalHex = if ($null -ne $final) {
            [string]$final.baselineHeroZMinimumCanonicalHex
        }
        else { '' }
        baselineHeroZMaximumCanonicalHex = if ($null -ne $final) {
            [string]$final.baselineHeroZMaximumCanonicalHex
        }
        else { '' }
        baselineRigidbodyComponentsObserved = if ($null -ne $final) {
            [bool]$final.baselineRigidbodyComponentsObserved
        }
        else { $false }
        baselineRigidbodyPositionDifferenceCount = if ($null -ne $final) {
            [int]$final.baselineRigidbodyPositionDifferenceCount
        }
        else { 0 }
        baselineRenderTransformEnvelopeEquivalent = if ($null -ne $final) {
            [bool]$final.baselineRenderTransformEnvelopeEquivalent
        }
        else { $false }
        baselineNormalizedRigidbodyEquivalent = if ($null -ne $final) {
            [bool]$final.baselineNormalizedRigidbodyEquivalent
        }
        else { $false }
        rigidbodyAxisWitnessesAvailable = if ($null -ne $final) {
            [bool]$final.rigidbodyAxisWitnessesAvailable
        }
        else { $false }
        rigidbodyAxisWitnessEquivalent = if ($null -ne $final) {
            [bool]$final.rigidbodyAxisWitnessEquivalent
        }
        else { $false }
        rigidbodyXWitnessAttempt = if ($null -ne $final) {
            [int]$final.rigidbodyXWitnessAttempt
        }
        else { 0 }
        rigidbodyXWitnessTraceSha256 = if ($null -ne $final) {
            [string]$final.rigidbodyXWitnessTraceSha256
        }
        else { '' }
        rigidbodyYWitnessAttempt = if ($null -ne $final) {
            [int]$final.rigidbodyYWitnessAttempt
        }
        else { 0 }
        rigidbodyYWitnessTraceSha256 = if ($null -ne $final) {
            [string]$final.rigidbodyYWitnessTraceSha256
        }
        else { '' }
        acceptedBaselineNormalizedRigidbodyDifferenceCount = if (
            $null -ne $final) {
            [long]$final.acceptedBaselineNormalizedRigidbodyDifferenceCount
        }
        else { 0L }
        firstUnacceptedDifferenceTick = if ($null -ne $final) {
            $final.firstUnacceptedDifferenceTick
        }
        else { $null }
        firstUnacceptedDifferenceKey = if ($null -ne $final) {
            $final.firstUnacceptedDifferenceKey
        }
        else { $null }
        output = @($output | ForEach-Object { [string]$_ })
    })
    Write-Output (
        'T24 candidate cold matrix {0}/{1}: mode={2}; success={3}; totalSuccess={4}' -f `
            $attempt, `
            $MaximumAttempts, `
            $Mode, `
            $success, `
            $successCount)
    Write-CandidateMatrixArtifact -Verdict 'IN_PROGRESS'
    if (-not $success) {
        break
    }
    if ($InterAttemptCooldownSeconds -gt 0 `
            -and $attempt -lt $RequiredSuccessfulRuns) {
        Start-Sleep -Seconds $InterAttemptCooldownSeconds
    }
}

# A later successful retry must not hide a failed cold process. The strict
# cohort is exactly the first RequiredSuccessfulRuns attempts.
$verdict = if ($successCount -eq $RequiredSuccessfulRuns `
        -and $attempts.Count -eq $RequiredSuccessfulRuns) {
    'PASS'
}
else {
    'FAIL'
}
Write-CandidateMatrixArtifact -Verdict $verdict

if ($verdict -ne 'PASS') {
    throw (
        'T24 candidate cold matrix failed strict cohort: ' `
        + $successCount `
        + '/' `
        + $RequiredSuccessfulRuns `
        + '; attempts=' `
        + $attempts.Count)
}

Write-Output "T24 candidate cold matrix passed: $EvidenceRoot"
