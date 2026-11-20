[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$NegativeControlEnvelopePath,

    [Parameter(Mandatory)]
    [string[]]$ReferenceRoots,

    [Parameter(Mandatory)]
    [string]$SourceMatrixPath,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'T24ClockHarness.ps1')

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 supplemental reference catalog requires PowerShell 7 or newer.'
}
if ($ReferenceRoots.Count -eq 0) {
    throw 'At least one supplemental reference root is required.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$helperScript = Join-Path $PSScriptRoot 'Measure-T24ReferenceEnvelope.ps1'
$NegativeControlEnvelopePath = [IO.Path]::GetFullPath(
    $NegativeControlEnvelopePath)
$SourceMatrixPath = [IO.Path]::GetFullPath($SourceMatrixPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
foreach ($required in @(
        $helperScript,
        $NegativeControlEnvelopePath,
        $SourceMatrixPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required T24 artifact is missing: $required"
    }
}

$sourceMatrix = Get-Content -LiteralPath $SourceMatrixPath -Raw |
    ConvertFrom-Json
$sourceAttempts = @($sourceMatrix.attempts)
if ([int]$sourceMatrix.schemaVersion -ne 2 `
        -or [string]$sourceMatrix.verdict -ne 'CAPTURED' `
        -or [string]$sourceMatrix.mode -ne 'vanilla-reference' `
        -or -not [bool]$sourceMatrix.strictFirstAttemptCohort `
        -or -not [bool]$sourceMatrix.stoppedAtFirstFailure `
        -or [int]$sourceMatrix.requiredSuccessfulRuns `
            -ne $sourceAttempts.Count `
        -or [int]$sourceMatrix.successfulRuns -ne $sourceAttempts.Count `
        -or [int]$sourceMatrix.attemptedRuns -ne $sourceAttempts.Count `
        -or $sourceAttempts.Count -ne $ReferenceRoots.Count) {
    throw 'Supplemental source matrix is not a complete first-attempt cohort.'
}
$matrixRoots = @(
    $sourceAttempts |
        ForEach-Object { [IO.Path]::GetFullPath([string]$_.evidenceRoot) })
$providedRoots = @(
    $ReferenceRoots |
        ForEach-Object { [IO.Path]::GetFullPath($_) })
for ($index = 0; $index -lt $matrixRoots.Count; $index++) {
    if (-not [string]::Equals(
            $matrixRoots[$index],
            $providedRoots[$index],
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Reference roots do not exactly match source-matrix order.'
    }
}
if (Test-Path -LiteralPath $OutputPath) {
    throw "Supplemental reference catalog already exists: $OutputPath"
}

function Get-T24HelperFunctionText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Cannot parse the T24 helper script: $Path"
    }
    $function = $ast.Find(
        {
            param($node)
            $node -is `
                [Management.Automation.Language.FunctionDefinitionAst] `
                -and $node.Name -eq $Name
        },
        $true)
    if ($null -eq $function) {
        throw "T24 helper function is missing: $Name"
    }
    return $function.Extent.Text
}

foreach ($functionName in @(
        'Get-OrAddCanonicalValueId',
        'Get-OrAddAuthoritativeFrameId',
        'Get-OrAddTraceSchema',
        'Get-Sha256Text',
        'Assert-T24BaselineAbsoluteTimeContract',
        'Get-BaselineSignature',
        'Get-AssemblyFingerprint',
        'Get-ClockFingerprint',
        'Test-T24RecordingPhaseContract',
        'Get-FieldMap',
        'Read-T24ValidatedTrace',
        'Get-CommonInputPrefix',
        'Get-CanonicalPhaseTimeline')) {
    Invoke-Expression (Get-T24HelperFunctionText `
        -Path $helperScript `
        -Name $functionName)
}
$toleranceCandidateKeys = @(
    'hero.position.x',
    'hero.position.y',
    'hero.positionDelta.x',
    'hero.positionDelta.y',
    'time.timeMinusFixed',
    'time.unscaledDeltaTime',
    'time.unscaledRelative'
)
$inputKeys = @(
    'input.axisX',
    'input.axisY',
    'input.held',
    'input.pressed',
    'input.released'
)
$baselineExcludedKeys = @(
    'runId',
    'mode',
    'visualTick',
    'fixedTick'
)
$toleranceCandidateKeySet =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($toleranceCandidateKey in $toleranceCandidateKeys) {
    [void]$toleranceCandidateKeySet.Add($toleranceCandidateKey)
}
$script:traceSchemaIds =
    [Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal)
$script:traceSchemas = [Collections.Generic.List[object]]::new()
$script:canonicalValueIds =
    [Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal)
$script:canonicalValues = [Collections.Generic.List[string]]::new()
$script:authoritativeFrameIds =
    [Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal)
$script:authoritativeFrameCanonical =
    [Collections.Generic.List[string]]::new()

$envelope = Get-Content `
    -LiteralPath $NegativeControlEnvelopePath `
    -Raw |
    ConvertFrom-Json
    if ([int]$envelope.schemaVersion -ne 2 `
        -or [string]$envelope.policyId `
            -ne 't24-vanilla-synchronized-negative-control-v13-exact-post-root-request-first-phase' `
        -or [string]$envelope.verdict -ne 'ELIGIBLE' `
        -or [int]$envelope.violationCount -ne 0 `
        -or -not [bool]$envelope.compatibility.physicalInputSynchronized `
        -or [int]$envelope.compatibility.physicalInputSynchronizationPrimeFrames `
            -ne 1 `
        -or [int]$envelope.compatibility.minimumAllRunInputPrefixFrames `
            -ne [int]$envelope.compatibility.frameCount `
        -or -not [bool]$envelope.compatibility.canonicalInputPhaseEquivalentAcrossAllRuns `
        -or [string]$envelope.requirements.fixtureReadinessBoundary `
            -ne 'semantic-idle' `
        -or [int]$envelope.requirements.fixtureReadinessRequiredUpdates `
            -ne 10 `
        -or [string]$envelope.requirements.fixtureReadinessAbsoluteTimeTargetCanonicalHex `
            -ne '44000000' `
        -or [double]$envelope.requirements.fixtureReadinessAbsoluteTimeTarget `
            -ne 512d `
        -or [string]$envelope.requirements.recordingAbsoluteTimeTargetCanonicalHex `
            -ne '44400000' `
        -or [double]$envelope.requirements.recordingAbsoluteTimeTarget `
            -ne 768d `
        -or [string]$envelope.requirements.recordingArmPolicyId `
            -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
        -or [string]$envelope.requirements.recordingPhaseContractId `
            -ne 'exact-recording-root-global-phase-zero-v1' `
        -or [int]$envelope.requirements.recordingFramePhaseModulo -ne 4 `
        -or [int]$envelope.requirements.recordingFramePhaseTarget -ne 0 `
        -or [int]$envelope.requirements.recordingRootRequestFrameOffset -ne 1 `
        -or -not [bool]$envelope.requirements.recordingPhaseNormalizationRequired `
        -or [int]$envelope.requirements.recordingPhaseNormalizationMaximumHoldFrames -ne 8 `
        -or [double]$envelope.requirements.fixtureReadinessMaximumPhaseErrorFractionOfFixedStep `
            -ne 0.001d `
        -or [int]$envelope.requirements.baselineSchemaVersion -ne 2 `
        -or -not [bool]$envelope.requirements.absoluteTimeBaselineRequired `
        -or -not [bool]$envelope.requirements.absoluteTimeTraceFieldsBitwiseExact `
        -or [string]$envelope.requirements.doublePhaseCalibrationProfile `
            -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
        -or -not [bool]$envelope.requirements.doublePhaseCalibrationRequired `
        -or [double]$envelope.requirements.doublePhaseMaximumAbsoluteResidualSeconds `
            -ne 0d `
        -or [int]$envelope.compatibility.baselineSchemaVersion -ne 2 `
        -or -not [bool]$envelope.compatibility.absoluteTimeTraceFieldsBitwiseExact `
        -or -not [bool]$envelope.compatibility.doublePhaseCalibrationRequired `
        -or [double]$envelope.compatibility.doublePhaseMaximumAbsoluteResidualSeconds `
            -ne 0d `
        -or [string]$envelope.compatibility.fixtureReadinessAbsoluteTimeTargetCanonicalHex `
            -ne '44000000' `
        -or [string]$envelope.compatibility.recordingAbsoluteTimeTargetCanonicalHex `
            -ne '44400000' `
        -or [string]$envelope.compatibility.recordingArmPolicyId `
            -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
        -or [string]$envelope.compatibility.recordingPhaseContractId `
            -ne 'exact-recording-root-global-phase-zero-v1' `
        -or [int]$envelope.compatibility.recordingFramePhaseModulo -ne 4 `
        -or [int]$envelope.compatibility.recordingFramePhaseTarget -ne 0 `
        -or [int]$envelope.compatibility.recordingRootRequestFrameOffset -ne 1 `
        -or -not [bool]$envelope.compatibility.recordingPhaseNormalizationRequired `
        -or [int]$envelope.compatibility.recordingPhaseNormalizationMaximumHoldFrames -ne 8 `
        -or [string]$envelope.requirements.samplingBoundary `
            -ne 'post-render-completed-frame-sampling-v1' `
        -or [string]$envelope.compatibility.samplingBoundary `
            -ne 'post-render-completed-frame-sampling-v1' `
        -or -not [bool]$envelope.fieldPolicy.exactFixtureBaselineRequired `
        -or -not [bool]$envelope.fieldPolicy.authoritativePhysicsRemainsBitwiseExact) {
    throw 'The primary T24 negative-control envelope is not eligible.'
}
$toleratedFrameClockFields = @(
    $envelope.fieldPolicy.toleratedFields |
        Where-Object {
            [string]$_.key -eq 'time.unscaledDeltaTime' `
                -or [string]$_.key -eq 'time.unscaledRelative'
        })
if ($toleratedFrameClockFields.Count -ne 0) {
    throw 'The primary frame-clock fields must remain bitwise exact.'
}
$envelopeGenerator = [IO.Path]::GetFullPath(
    [string]$envelope.generatorScript)
if (-not [string]::Equals(
        $envelopeGenerator,
        $helperScript,
        [StringComparison]::OrdinalIgnoreCase) `
        -or (Get-FileHash `
            -LiteralPath $helperScript `
            -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ne [string]$envelope.generatorScriptSha256) {
    throw 'The immutable T24 envelope generator changed.'
}

$primaryMember = @(
    $envelope.exactBaselineCatalog |
        ForEach-Object { $_.members } |
        Sort-Object attempt)[0]
$primaryTracePath = [IO.Path]::GetFullPath(
    [string]$primaryMember.tracePath)
$primaryPhasePath = [IO.Path]::GetFullPath(
    [string]$primaryMember.phasePath)
foreach ($required in @($primaryTracePath, $primaryPhasePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Primary reference artifact is missing: $required"
    }
}
$primaryFrames = @(Read-T24ValidatedTrace -Path $primaryTracePath)
$primaryPhase = Get-CanonicalPhaseTimeline -Path $primaryPhasePath
if (-not [bool]$primaryPhase.valid `
        -or $primaryFrames.Count `
            -ne [int]$envelope.compatibility.frameCount `
        -or @($primaryPhase.events).Count `
            -ne [int]$envelope.compatibility.canonicalInputPhaseEventCount) {
    throw 'The primary reference timeline no longer satisfies its envelope.'
}

$seenRoots = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$members = [Collections.Generic.List[object]]::new()
$rejectedMembers = [Collections.Generic.List[object]]::new()
$ordinal = 0
foreach ($rawRoot in $ReferenceRoots) {
    $ordinal++
    $root = [IO.Path]::GetFullPath($rawRoot)
    if (-not $seenRoots.Add($root)) {
        throw "Duplicate supplemental reference root: $root"
    }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Supplemental reference root is missing: $root"
    }

    $summaryPath = Join-Path $root 'reference-smoke.json'
    $auditRoot = Join-Path $root 'observer-audit'
    $assembliesPath = Join-Path $auditRoot 'assemblies.json'
    $clockPath = Join-Path $auditRoot 'clock-injection.json'
    $clockProfilePath = Join-Path $auditRoot 'clock-profile.json'
    $preClockMenuDwellAuditPath = Join-Path `
        $auditRoot `
        'pre-clock-menu-dwell.json'
    $traceRoot = Join-Path $root 'traces\vanilla-reference'
    $baselinePath = Join-Path $traceRoot 'baseline.json'
    $tracePath = Join-Path $traceRoot 'trace.jsonl'
    $phasePath = Join-Path $traceRoot 'input-phase.jsonl'
    $resultPath = Join-Path $traceRoot 'result.json'
    foreach ($required in @(
            $summaryPath,
            $assembliesPath,
            $clockPath,
            $clockProfilePath,
            $baselinePath,
            $tracePath,
            $phasePath,
            $resultPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Supplemental reference artifact is missing: $required"
        }
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw |
        ConvertFrom-Json
    $result = Get-Content -LiteralPath $resultPath -Raw |
        ConvertFrom-Json
    if (-not (Test-T24NativeSceneLifecycleContract -Telemetry $result)) {
        throw "Supplemental reference violates the native scene lifecycle contract: $resultPath"
    }
    $baseline = Get-Content -LiteralPath $baselinePath -Raw |
        ConvertFrom-Json
    $clockProfile = Get-Content -LiteralPath $clockProfilePath -Raw |
        ConvertFrom-Json
    $hasDwellSummary = $summary.PSObject.Properties.Match(
            'preClockMenuDwellPolicyId').Count -ne 0
    $hasDwellAudit = Test-Path `
        -LiteralPath $preClockMenuDwellAuditPath `
        -PathType Leaf
    if ($hasDwellSummary -ne $hasDwellAudit) {
        throw "Supplemental reference dwell audit is incomplete: $root"
    }
    $preClockMenuDwellAudit = if ($hasDwellAudit) {
        Get-Content -LiteralPath $preClockMenuDwellAuditPath -Raw |
            ConvertFrom-Json
    }
    else {
        $null
    }
    if ($hasDwellAudit `
            -and ([string]$summary.preClockMenuDwellPolicyId `
                    -ne 'wall-clock-wait-in-menu-title-v1' `
                -or [int]$summary.preClockMenuDwellRequestedMilliseconds `
                    -ne [int]$preClockMenuDwellAudit.requestedMilliseconds `
                -or [double]$summary.preClockMenuDwellActualMilliseconds `
                    -ne [double]$preClockMenuDwellAudit.actualElapsedMilliseconds `
                -or [string]$summary.preClockMenuDwellAuditSha256 `
                    -ne (Get-FileHash `
                        -LiteralPath $preClockMenuDwellAuditPath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                -or [int]$preClockMenuDwellAudit.schemaVersion -ne 1 `
                -or [string]$preClockMenuDwellAudit.policyId `
                    -ne 'wall-clock-wait-in-menu-title-v1' `
                -or [int]$preClockMenuDwellAudit.requestedMilliseconds `
                    -lt 0 `
                -or [int]$preClockMenuDwellAudit.requestedMilliseconds `
                    -gt 60000 `
                -or [double]$preClockMenuDwellAudit.actualElapsedMilliseconds `
                    -lt [int]$preClockMenuDwellAudit.requestedMilliseconds `
                -or [string]$preClockMenuDwellAudit.sceneBefore `
                    -ne 'Menu_Title' `
                -or [string]$preClockMenuDwellAudit.sceneAfter `
                    -ne 'Menu_Title' `
                -or [bool]$preClockMenuDwellAudit.inputInjected `
                -or [bool]$preClockMenuDwellAudit.timeWritten `
                -or [bool]$preClockMenuDwellAudit.gameplayStateWritten `
                -or [bool]$preClockMenuDwellAudit.visualRecognitionUsed)) {
        throw "Supplemental reference dwell audit contract failed: $root"
    }
    $baselineHash = (Get-FileHash `
        -LiteralPath $baselinePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $traceHash = (Get-FileHash `
        -LiteralPath $tracePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$summary.verdict `
            -ne 'REFERENCE_CLOCK_CONTROLLED_CAPTURED' `
            -or [string]$summary.mode -ne 'vanilla-reference' `
            -or -not [bool]$summary.tasRuntimeAbsent `
            -or -not [bool]$summary.observerOnlyManagedMod `
            -or [bool]$summary.tasControlStarted `
            -or -not [bool]$summary.externalClockPrototype `
            -or [string]$summary.clockCapabilityId `
                -ne 'native.clock-rng-pause.override.experimental.v31' `
            -or [string]$summary.clockProfile `
                -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
            -or -not [bool]$summary.externalRngSynchronized `
            -or -not [bool]$summary.externalRngSynchronizationOriginCaptured `
            -or [string]$summary.externalRngSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$summary.externalRngSeed -ne 1212896321 `
            -or [string]$summary.externalRngPayloadSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$summary.externalRngPayloadSeed -ne 1212896321 `
            -or [uint32]$summary.externalClockBridgeAbi -ne 10 `
            -or [int]$summary.externalClockBridgeStatus -ne 2 `
            -or [bool]$summary.externalRuntimeVirtualClockRegistered `
            -or [int]$summary.externalVirtualClockPauseCount -ne 0 `
            -or [int]$summary.externalVirtualClockResumeRequestCount -ne 0 `
            -or [int]$summary.externalVirtualClockResumeCount -ne 0 `
            -or -not [bool]$summary.externalDeterministicClockEnabled `
            -or [long]$summary.externalDeterministicClockFrequency -le 0 `
            -or [long]$summary.externalDeterministicClockStepTicks -le 0 `
            -or [long]$summary.externalDeterministicClockFrequency `
                / [long]$summary.externalDeterministicClockStepTicks -ne 50 `
            -or [long]$summary.externalDeterministicClockAnchor -le 0 `
            -or [int]$summary.externalDeterministicClockFrameAdvanceCount -le 0 `
            -or [int]$summary.externalDeterministicClockEnableFaultCode -ne 0 `
            -or [int]$summary.externalDeterministicClockAdvanceFaultCode -ne 0 `
            -or -not [bool]$summary.externalDoublePhaseCalibrationApplied `
            -or [int]$summary.externalDoublePhaseCalibrationAttempts -le 0 `
            -or [int]$summary.externalDoublePhaseCalibrationFaultCode -ne 0 `
            -or [string]$summary.externalDoublePhaseFinalResidualBits `
                -ne '0000000000000000' `
            -or [string]$summary.externalDoublePhaseFinalResidual.canonicalHex `
                -ne '0000000000000000' `
            -or [string]$summary.externalDoublePhaseLastCorrectionBits `
                -eq '00000000' `
            -or [string]$summary.externalRealtimeEpochNormalizationPolicyId `
                -ne 'root-game-minus-rounded-startup-offset-qpc-grid-v3' `
            -or -not [bool]$summary.externalRealtimeEpochNormalizationApplied `
            -or [int]$summary.externalRealtimeEpochNormalizationCount -le 0 `
            -or [string]$summary.externalRealtimeEpochNormalizationAfterBits `
                -ne [string]$summary.externalRealtimeEpochNormalizationTargetBits `
            -or [int]$summary.externalRealtimeEpochNormalizationFaultCode -ne 0 `
            -or -not [bool]$summary.externalStartupClockHookInstalled `
            -or -not [bool]$summary.externalStartupClockLatchEnabled `
            -or [uint32]$summary.externalStartupClockHookThreadId -eq 0 `
            -or -not [bool]$summary.externalStartupClockPrimaryThreadMatched `
            -or [int]$summary.externalStartupClockVirtualQpcCallCount -le 0 `
            -or [int]$summary.externalStartupClockHandoffAdoptCount -ne 1 `
            -or [int]$summary.externalStartupClockFaultCode -ne 0 `
            -or [string]$summary.externalStartupPolicy `
                -ne 'create-suspended-early-apc-unity-then-bridge-v1' `
            -or [string]$summary.recordingArmPolicyId `
                -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
            -or [string]$summary.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or -not [bool]$summary.recordingArmBoundaryReached `
            -or -not [bool]$summary.recordingArmReleased `
            -or [string]$summary.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne [string]$summary.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -or -not (Test-T24RecordingPhaseContract `
                -Telemetry $summary `
                -RootRequestApplied $true) `
            -or -not [bool]$summary.externalPhysicalInput `
            -or -not [bool]$summary.externalPhysicalInputSynchronized `
            -or [int]$summary.externalInputSynchronizationPrimeFrames -ne 1 `
            -or [string]$summary.physicalInputSourceTraceSha256 `
                -ne [string]$envelope.compatibility.physicalInputSourceTraceSha256 `
            -or [bool]$summary.visualRecognitionUsed `
            -or [bool]$summary.observerInputInjected `
            -or [bool]$summary.observerGameplayStateWritten `
            -or $baselineHash -ne [string]$summary.baselineSha256 `
            -or $traceHash -ne [string]$summary.traceSha256) {
        throw "Supplemental reference summary contract failed: $root"
    }
    if (-not [bool]$result.success `
            -or [string]$result.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1' `
            -or -not [bool]$result.baselineCaptured `
            -or [int]$result.frameCount `
                -ne [int]$envelope.compatibility.frameCount `
            -or [bool]$result.inputInjected `
            -or [bool]$result.timeWritten `
            -or [bool]$result.gameplayStateWritten `
            -or [bool]$result.saveLoadedByObserver `
            -or -not [bool]$result.externalInputSynchronized `
            -or -not [bool]$result.externalInputSynchronizationRequired `
            -or [int]$result.externalInputSynchronizedFrames `
                -ne [int]$envelope.compatibility.frameCount `
            -or [int]$result.externalInputSynchronizationPrimeFrames -ne 1 `
            -or -not [bool]$result.externalRngSynchronized `
            -or -not [bool]$result.externalRngSynchronizationOriginCaptured `
            -or [string]$result.externalRngSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$result.externalRngSeed -ne 1212896321 `
            -or [uint32]$result.externalClockBridgeAbi -ne 10 `
            -or [int]$result.externalClockBridgeStatus -ne 2 `
            -or [bool]$result.externalRuntimeVirtualClockRegistered `
            -or -not [bool]$result.externalDeterministicClockEnabled `
            -or [long]$result.externalDeterministicClockFrequency -le 0 `
            -or [long]$result.externalDeterministicClockStepTicks -le 0 `
            -or [long]$result.externalDeterministicClockFrequency `
                / [long]$result.externalDeterministicClockStepTicks -ne 50 `
            -or [long]$result.externalDeterministicClockAnchor -le 0 `
            -or [int]$result.externalDeterministicClockFrameAdvanceCount -le 0 `
            -or [int]$result.externalDeterministicClockEnableFaultCode -ne 0 `
            -or [int]$result.externalDeterministicClockAdvanceFaultCode -ne 0 `
            -or -not [bool]$result.externalDoublePhaseCalibrationApplied `
            -or [int]$result.externalDoublePhaseCalibrationAttempts -le 0 `
            -or [int]$result.externalDoublePhaseCalibrationFaultCode -ne 0 `
            -or [string]$result.externalDoublePhaseFinalResidualBits `
                -ne '0000000000000000' `
            -or [string]$result.externalDoublePhaseFinalResidual.canonicalHex `
                -ne '0000000000000000' `
            -or [string]$result.externalDoublePhaseLastCorrectionBits `
                -ne [string]$summary.externalDoublePhaseLastCorrectionBits `
            -or [string]$result.externalRealtimeEpochNormalizationPolicyId `
                -ne [string]$summary.externalRealtimeEpochNormalizationPolicyId `
            -or -not [bool]$result.externalRealtimeEpochNormalizationApplied `
            -or [int]$result.externalRealtimeEpochNormalizationCount `
                -ne [int]$summary.externalRealtimeEpochNormalizationCount `
            -or [string]$result.externalRealtimeEpochNormalizationAfterBits `
                -ne [string]$result.externalRealtimeEpochNormalizationTargetBits `
            -or [int]$result.externalRealtimeEpochNormalizationFaultCode -ne 0 `
            -or -not [bool]$result.externalStartupClockHookInstalled `
            -or -not [bool]$result.externalStartupClockLatchEnabled `
            -or [uint32]$result.externalStartupClockHookThreadId `
                -ne [uint32]$summary.externalStartupClockHookThreadId `
            -or [int]$result.externalStartupClockVirtualQpcCallCount -le 0 `
            -or [int]$result.externalStartupClockHandoffAdoptCount -ne 1 `
            -or [int]$result.externalStartupClockFaultCode -ne 0 `
            -or [string]$result.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or -not [bool]$result.recordingArmBoundaryReached `
            -or -not [bool]$result.recordingArmReleased `
            -or [string]$result.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne [string]$result.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -or -not (Test-T24RecordingPhaseContract `
                -Telemetry $result `
                -RootRequestApplied $true) `
            -or -not [string]::IsNullOrEmpty([string]$result.error) `
            -or -not [string]::IsNullOrEmpty(
                [string]$result.inputPhaseObservationError)) {
        throw "Supplemental reference observer contract failed: $root"
    }
    if ([string]$baseline.samplingBoundary `
            -ne 'post-render-completed-frame-sampling-v1' `
            -or [string]$baseline.fixtureReadinessBoundary -ne 'semantic-idle' `
            -or [int]$baseline.fixtureReadinessRequiredUpdates -ne 10 `
            -or [string]$baseline.fixtureReadinessAbsoluteTimeTarget.canonicalHex `
                -ne '44000000' `
            -or [int]$baseline.schemaVersion -ne 2 `
            -or [string]$baseline.timeRaw.canonicalHex `
                -ne [string]$baseline.fixedTimeRaw.canonicalHex `
            -or [string]$baseline.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or [string]$clockProfile.phase `
                -ne 'waiting-for-recording-arm-release' `
            -or [string]$clockProfile.readinessBoundary `
                -ne 'semantic-idle' `
            -or [int]$clockProfile.readinessRequiredUpdates -ne 10 `
            -or [int]$clockProfile.readinessStableUpdates -ne 10 `
            -or [string]$clockProfile.readinessAbsoluteTimeTarget.canonicalHex `
                -ne '44000000' `
            -or [string]$clockProfile.readinessTimeRaw.canonicalHex `
                -ne [string]$clockProfile.readinessFixedTimeRaw.canonicalHex `
            -or [string]$clockProfile.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or -not [bool]$clockProfile.recordingArmBoundaryReached `
            -or [bool]$clockProfile.recordingArmReleased `
            -or [string]$clockProfile.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne [string]$clockProfile.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -or -not (Test-T24RecordingPhaseContract `
                -Telemetry $clockProfile `
                -RootRequestApplied $false) `
            -or -not [bool]$clockProfile.externalDoublePhaseCalibrationApplied `
            -or [int]$clockProfile.externalDoublePhaseCalibrationAttempts -le 0 `
            -or [int]$clockProfile.externalDoublePhaseCalibrationFaultCode -ne 0 `
            -or [string]$clockProfile.externalDoublePhaseFinalResidualBits `
                -ne '0000000000000000' `
            -or [string]$clockProfile.externalDoublePhaseFinalResidual.canonicalHex `
                -ne '0000000000000000' `
            -or [string]$clockProfile.externalDoublePhaseLastCorrectionBits `
                -ne [string]$summary.externalDoublePhaseLastCorrectionBits `
            -or [string]$clockProfile.externalRealtimeEpochNormalizationPolicyId `
                -ne [string]$summary.externalRealtimeEpochNormalizationPolicyId `
            -or -not [bool]$clockProfile.externalRealtimeEpochNormalizationApplied `
            -or [int]$clockProfile.externalRealtimeEpochNormalizationCount -le 0 `
            -or [string]$clockProfile.externalRealtimeEpochNormalizationAfterBits `
                -ne [string]$clockProfile.externalRealtimeEpochNormalizationTargetBits `
            -or [int]$clockProfile.externalRealtimeEpochNormalizationFaultCode -ne 0 `
            -or [string]$clockProfile.timeRawDouble.canonicalHex `
                -ne [string]$clockProfile.fixedTimeRawDouble.canonicalHex `
            -or [int]$clockProfile.readinessFixedSteps -ne 1 `
            -or [bool]$clockProfile.readinessGameplayInputActive `
            -or -not [string]::IsNullOrEmpty(
                [string]$clockProfile.readinessBlocker)) {
        throw "Supplemental reference readiness contract failed: $root"
    }

    $readinessPhaseFraction = [double](
        $envelope.requirements.fixtureReadinessMaximumPhaseErrorFractionOfFixedStep)
    $readinessFixedDeltaTime = [single]$clockProfile.fixedDeltaTime
    $readinessTimeMinusFixed = [single]$clockProfile.timeMinusFixed
    $readinessPhaseLimit =
        [Math]::Abs([double]$readinessFixedDeltaTime) `
            * $readinessPhaseFraction
    $readinessPhaseValid =
        -not [single]::IsNaN($readinessFixedDeltaTime) `
        -and -not [single]::IsInfinity($readinessFixedDeltaTime) `
        -and [double]$readinessFixedDeltaTime -gt 0d `
        -and -not [single]::IsNaN($readinessTimeMinusFixed) `
        -and -not [single]::IsInfinity($readinessTimeMinusFixed) `
        -and [Math]::Abs([double]$readinessTimeMinusFixed) `
            -le $readinessPhaseLimit
    if (-not $readinessPhaseValid) {
        $rejectedMembers.Add([ordered]@{
            sourceOrdinal = $ordinal
            sourceAttempt = [int]$sourceAttempts[$ordinal - 1].attempt
            evidenceRoot = $root
            reason = 'fixture-readiness-phase-outside-contract'
            clockProfilePath = $clockProfilePath
            clockProfileSha256 = (Get-FileHash `
                -LiteralPath $clockProfilePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            fixedDeltaTime = [double]$readinessFixedDeltaTime
            timeMinusFixed = [double]$readinessTimeMinusFixed
            maximumAbsoluteTimeMinusFixed = $readinessPhaseLimit
            maximumPhaseErrorFractionOfFixedStep = $readinessPhaseFraction
        })
        continue
    }

    $assemblyHash = Get-AssemblyFingerprint -Path $assembliesPath
    $samplingHash = Get-AssemblyFingerprint `
        -Path $assembliesPath `
        -SamplingOnly
    $clock = Get-ClockFingerprint -Path $clockPath
    if ($assemblyHash `
            -ne [string]$envelope.compatibility.observerAssemblySetSha256 `
            -or $samplingHash `
                -ne [string]$envelope.compatibility.samplingAssemblySetSha256 `
            -or [string]$clock.sha256 `
                -ne [string]$envelope.compatibility.externalClockContractSha256) {
        throw "Supplemental reference tooling differs from the primary envelope: $root"
    }

    $frames = @(Read-T24ValidatedTrace -Path $tracePath)
    $phase = Get-CanonicalPhaseTimeline -Path $phasePath
    $inputPrefix = Get-CommonInputPrefix `
        -LeftFrames $primaryFrames `
        -RightFrames $frames
    $phaseEquivalent = [bool]$phase.valid `
        -and @($phase.events).Count -eq @($primaryPhase.events).Count `
        -and [string]::Join("`n", @($phase.events)) `
            -ceq [string]::Join("`n", @($primaryPhase.events))
    if ($frames.Count -ne [int]$envelope.compatibility.frameCount `
            -or $inputPrefix -ne [int]$envelope.compatibility.frameCount `
            -or -not $phaseEquivalent) {
        $rejectedMembers.Add([ordered]@{
            sourceOrdinal = $ordinal
            sourceAttempt = [int]$sourceAttempts[$ordinal - 1].attempt
            evidenceRoot = $root
            reason = if ($inputPrefix `
                    -ne [int]$envelope.compatibility.frameCount) {
                'captured-input-timeline-mismatch'
            }
            elseif (-not [bool]$phase.valid) {
                'canonical-phase-invalid'
            }
            else { 'canonical-phase-timeline-mismatch' }
            frameCount = $frames.Count
            commonInputPrefixFrames = $inputPrefix
            phaseValid = [bool]$phase.valid
            phaseEventCount = @($phase.events).Count
        })
        continue
    }

    $firstFrame = ([IO.File]::ReadLines($tracePath) |
        Select-Object -First 1) |
        ConvertFrom-Json
    $eligibleOrdinal = $members.Count + 1
    $members.Add([ordered]@{
        ordinal = $eligibleOrdinal
        sourceOrdinal = $ordinal
        sourceAttempt = [int]$sourceAttempts[$ordinal - 1].attempt
        catalogAttempt = 1000 + $eligibleOrdinal
        name = "supplemental-{0:D2}" -f $eligibleOrdinal
        evidenceRoot = $root
        baselinePath = $baselinePath
        baselineSha256 = $baselineHash
        baselineSignatureSha256 = [string](
            Get-BaselineSignature -Path $baselinePath).sha256
        tracePath = $tracePath
        traceSha256 = $traceHash
        entryComparisonSha256 = [string]$firstFrame.comparisonSha256
        phasePath = $phasePath
        phaseSha256 = (Get-FileHash `
            -LiteralPath $phasePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        resultPath = $resultPath
        resultSha256 = (Get-FileHash `
            -LiteralPath $resultPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        summaryPath = $summaryPath
        summarySha256 = (Get-FileHash `
            -LiteralPath $summaryPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        assembliesPath = $assembliesPath
        assembliesSha256 = (Get-FileHash `
            -LiteralPath $assembliesPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        clockPath = $clockPath
        clockSha256 = (Get-FileHash `
            -LiteralPath $clockPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        clockProfilePath = $clockProfilePath
        clockProfileSha256 = (Get-FileHash `
            -LiteralPath $clockProfilePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        preClockMenuDwellAudited = $hasDwellAudit
        preClockMenuDwellAuditPath = if ($hasDwellAudit) {
            $preClockMenuDwellAuditPath
        }
        else { '' }
        preClockMenuDwellAuditSha256 = if ($hasDwellAudit) {
            (Get-FileHash `
                -LiteralPath $preClockMenuDwellAuditPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        preClockMenuDwellRequestedMilliseconds = if ($hasDwellAudit) {
            [int]$preClockMenuDwellAudit.requestedMilliseconds
        }
        else { -1 }
        preClockMenuDwellActualMilliseconds = if ($hasDwellAudit) {
            [double]$preClockMenuDwellAudit.actualElapsedMilliseconds
        }
        else { -1 }
        baselineVisualFixedGap =
            [int]$baseline.visualTick - [int]$baseline.fixedTick
    })
}

if ($members.Count -eq 0 `
        -or $members.Count + $rejectedMembers.Count `
            -ne $sourceAttempts.Count) {
    throw 'Supplemental catalog has no eligible members or lost source roots.'
}

$catalog = [ordered]@{
    schemaVersion = 2
    policyId = 't24-supplemental-reference-catalog-v7-exact-recording-phase'
    verdict = 'ELIGIBLE'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    generatorScript = $PSCommandPath
    generatorScriptSha256 = (Get-FileHash `
        -LiteralPath $PSCommandPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    helperScript = $helperScript
    helperScriptSha256 = (Get-FileHash `
        -LiteralPath $helperScript `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    negativeControlEnvelope = $NegativeControlEnvelopePath
    negativeControlEnvelopeSha256 = (Get-FileHash `
        -LiteralPath $NegativeControlEnvelopePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    selectionPolicy =
        'complete-source-matrix-exact-recording-phase-readiness-input-phase-contract-v6'
    sourceMatrix = $SourceMatrixPath
    sourceMatrixSha256 = (Get-FileHash `
        -LiteralPath $SourceMatrixPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceCohortCount = $sourceAttempts.Count
    compatibility = [ordered]@{
        frameCount = [int]$envelope.compatibility.frameCount
        physicalInputSourceTraceSha256 = `
            [string]$envelope.compatibility.physicalInputSourceTraceSha256
        canonicalInputPhaseEventCount = `
            [int]$envelope.compatibility.canonicalInputPhaseEventCount
        observerAssemblySetSha256 = `
            [string]$envelope.compatibility.observerAssemblySetSha256
        samplingAssemblySetSha256 = `
            [string]$envelope.compatibility.samplingAssemblySetSha256
        externalClockContractSha256 = `
            [string]$envelope.compatibility.externalClockContractSha256
        fixtureReadinessBoundary = 'semantic-idle'
        fixtureReadinessRequiredUpdates = 10
        fixtureReadinessMaximumPhaseErrorFractionOfFixedStep =
            [double]$envelope.requirements.fixtureReadinessMaximumPhaseErrorFractionOfFixedStep
        fixtureReadinessAbsoluteTimeTargetCanonicalHex = '44000000'
        recordingAbsoluteTimeTargetCanonicalHex = '44400000'
        recordingArmPolicyId =
            'exact-absolute-time-post-root-request-first-global-phase-host-release-v11'
        recordingPhaseContractId =
            'exact-recording-root-global-phase-zero-v1'
        recordingFramePhaseModulo = 4
        recordingFramePhaseTarget = 0
        recordingRootRequestFrameOffset = 1
        recordingPhaseNormalizationRequired = $true
        recordingPhaseNormalizationMaximumHoldFrames = 8
        baselineSchemaVersion = 2
        absoluteTimeTraceFieldsBitwiseExact = $true
        doublePhaseCalibrationRequired = $true
        doublePhaseMaximumAbsoluteResidualSeconds = 0d
        samplingBoundary = 'post-render-completed-frame-sampling-v1'
        preClockMenuDwellAuditedMemberCount = @(
            $members |
                Where-Object { [bool]$_.preClockMenuDwellAudited }).Count
    }
    memberCount = $members.Count
    members = @($members)
    rejectedMemberCount = $rejectedMembers.Count
    rejectedMembers = @($rejectedMembers)
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$catalog |
    ConvertTo-Json -Depth 30 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "T24 supplemental reference catalog eligible: $OutputPath"
