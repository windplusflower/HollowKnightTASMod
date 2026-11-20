[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceEvidenceRoot,

    [Parameter(Mandatory)][string]$NegativeControlEnvelopePath,

    [Parameter(Mandatory)][string]$OutputRoot,

    [string]$CandidateScriptPath = '',

    [string]$ObserverBuildRoot = '',

    [string]$SupplementalReferenceCatalogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 captured-candidate offline comparison requires PowerShell 7.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
if ([string]::IsNullOrWhiteSpace($CandidateScriptPath)) {
    $CandidateScriptPath = Join-Path `
        $PSScriptRoot `
        'Invoke-T24CandidateSmoke.ps1'
}
if ([string]::IsNullOrWhiteSpace($ObserverBuildRoot)) {
    $ObserverBuildRoot = Join-Path `
        $repoRoot `
        'src\HollowKnightTAS.ReferenceObserver\bin\Debug'
}

$SourceEvidenceRoot = [IO.Path]::GetFullPath($SourceEvidenceRoot)
$NegativeControlEnvelopePath =
    [IO.Path]::GetFullPath($NegativeControlEnvelopePath)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$CandidateScriptPath = [IO.Path]::GetFullPath($CandidateScriptPath)
$ObserverBuildRoot = [IO.Path]::GetFullPath($ObserverBuildRoot)
if (-not [string]::IsNullOrWhiteSpace(
        $SupplementalReferenceCatalogPath)) {
    $SupplementalReferenceCatalogPath = [IO.Path]::GetFullPath(
        $SupplementalReferenceCatalogPath)
}

foreach ($path in @(
        $SourceEvidenceRoot,
        $ObserverBuildRoot)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required directory is missing: $path"
    }
}
foreach ($path in @(
        $CandidateScriptPath,
        $NegativeControlEnvelopePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $path"
    }
}
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Output root already exists: $OutputRoot"
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

$sourceVerdictPath = Join-Path $SourceEvidenceRoot 'final-verdict.json'
if (-not (Test-Path -LiteralPath $sourceVerdictPath -PathType Leaf)) {
    throw "Source final verdict is missing: $sourceVerdictPath"
}
$sourceVerdict = Get-Content -LiteralPath $sourceVerdictPath -Raw |
    ConvertFrom-Json
$Mode = [string]$sourceVerdict.mode
if ($Mode -notin @(
        'tas-continuous',
        'tas-sequential',
        'tas-batch')) {
    throw "Source verdict is not an active TAS mode: $Mode"
}
$maxTicks = [int]$sourceVerdict.frameCount
if ($maxTicks -le 0) {
    throw 'Source verdict frame count is invalid.'
}

$candidateDirectory = Join-Path $SourceEvidenceRoot "traces\$Mode"
$candidateTracePath = Join-Path $candidateDirectory 'trace.jsonl'
$candidateBaselinePath = Join-Path $candidateDirectory 'baseline.json'
$candidatePhasePath = Join-Path $candidateDirectory 'input-phase.jsonl'
$referenceTracePath = [IO.Path]::GetFullPath(
    ([string]$sourceVerdict.referenceTrace))
$referenceBaselinePath = [IO.Path]::GetFullPath(
    ([string]$sourceVerdict.referenceBaseline))
$referencePhasePath = Join-Path `
    (Split-Path -Parent $referenceTracePath) `
    'input-phase.jsonl'
foreach ($path in @(
        $candidateTracePath,
        $candidateBaselinePath,
        $candidatePhasePath,
        $referenceTracePath,
        $referenceBaselinePath,
        $referencePhasePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Source comparison artifact is missing: $path"
    }
}

function Assert-T24RecordedHash {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Expected -cnotmatch '\A[0-9a-f]{64}\z') {
        throw "Recorded $Label hash is missing or malformed."
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
        Hash.ToLowerInvariant()
    if ($actual -cne $Expected) {
        throw (
            "$Label hash mismatch: expected $Expected; actual $actual")
    }
    return $actual
}

$candidateTraceSha256 = Assert-T24RecordedHash `
    -Path $candidateTracePath `
    -Expected ([string]$sourceVerdict.candidateTraceSha256) `
    -Label 'candidate trace'
$candidateBaselineSha256 = Assert-T24RecordedHash `
    -Path $candidateBaselinePath `
    -Expected ([string]$sourceVerdict.candidateBaselineSha256) `
    -Label 'candidate baseline'
$referenceTraceSha256 = Assert-T24RecordedHash `
    -Path $referenceTracePath `
    -Expected ([string]$sourceVerdict.referenceTraceSha256) `
    -Label 'reference trace'
$referenceBaselineSha256 = Assert-T24RecordedHash `
    -Path $referenceBaselinePath `
    -Expected ([string]$sourceVerdict.referenceBaselineSha256) `
    -Label 'reference baseline'

$tokens = $null
$parseErrors = $null
$candidateAst = [Management.Automation.Language.Parser]::ParseFile(
    $CandidateScriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw 'Candidate smoke script has PowerShell parse errors.'
}
$functionAsts = @(
    $candidateAst.EndBlock.Statements |
        Where-Object {
            $_ -is [Management.Automation.Language.FunctionDefinitionAst]
        })
if ($functionAsts.Count -lt 1) {
    throw 'Candidate smoke script exposes no reusable comparison functions.'
}
foreach ($functionAst in $functionAsts) {
    . ([scriptblock]::Create($functionAst.Extent.Text))
}
foreach ($requiredFunction in @(
        'Import-T24NegativeControlEnvelope',
        'Import-T24SupplementalReferenceCatalog',
        'Resolve-T24ReferenceForBaseline',
        'Compare-T24Baselines',
        'Compare-T24PhaseTraces',
        'Compare-T24TracesWithEnvelope',
        'Test-T24RigidbodyAxisWitnessTrace')) {
    if (-not (Get-Command `
            -Name $requiredFunction `
            -CommandType Function `
            -ErrorAction SilentlyContinue)) {
        throw "Candidate comparison function is missing: $requiredFunction"
    }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$firstDifferenceDirectory = Join-Path $OutputRoot 'first-differences'
New-Item `
    -ItemType Directory `
    -Path $firstDifferenceDirectory `
    -Force |
    Out-Null

$observerFiles = @(
    'HollowKnightTAS.ReferenceObserver.dll',
    'HollowKnightTAS.GameObservation.dll',
    'HollowKnightTAS.Core.dll')
$negativeControlPolicy = Import-T24NegativeControlEnvelope `
    -Path $NegativeControlEnvelopePath `
    -ReferenceBaseline $referenceBaselinePath `
    -ObserverBuildRoot $ObserverBuildRoot `
    -ObserverAssemblyFiles $observerFiles `
    -ExpectedFrameCount $maxTicks
$supplementalReferences = @(
    Import-T24SupplementalReferenceCatalog `
        -Path $SupplementalReferenceCatalogPath `
        -EnvelopePolicy $negativeControlPolicy `
        -EnvelopePath $NegativeControlEnvelopePath `
        -ObserverBuildRoot $ObserverBuildRoot `
        -ObserverAssemblyFiles $observerFiles `
        -ExpectedFrameCount $maxTicks)
if ($supplementalReferences.Count -ne 0) {
    $negativeControlPolicy.referenceCatalog = @(
        @($negativeControlPolicy.referenceCatalog) `
            + $supplementalReferences)
}

$matchedReference = Resolve-T24ReferenceForBaseline `
    -EnvelopePolicy $negativeControlPolicy `
    -CandidateBaselinePath $candidateBaselinePath
if ($null -eq $matchedReference) {
    throw 'Candidate baseline does not match the no-Mod reference catalog.'
}
$referenceTracePath = [string]$matchedReference.tracePath
$referenceBaselinePath = [string]$matchedReference.baselinePath
$referencePhasePath = [string]$matchedReference.phasePath

$baselineComparison = Compare-T24Baselines `
    -ReferencePath $referenceBaselinePath `
    -CandidatePath $candidateBaselinePath `
    -EnvelopePolicy $negativeControlPolicy
$phaseComparison = Compare-T24PhaseTraces `
    -ReferencePath $referencePhasePath `
    -CandidatePath $candidatePhasePath
$allowBaselineNormalizedRigidbody =
    [bool]$baselineComparison.equivalent `
    -and [int]$baselineComparison.rigidbodyPositionDifferenceCount -gt 0
$rigidbodyXWitnessPath = if ($allowBaselineNormalizedRigidbody) {
    [string]$matchedReference.rigidbodyXWitnessTracePath
}
else { '' }
$rigidbodyYWitnessPath = if ($allowBaselineNormalizedRigidbody) {
    [string]$matchedReference.rigidbodyYWitnessTracePath
}
else { '' }
$traceComparison = Compare-T24TracesWithEnvelope `
    -ReferencePath $referenceTracePath `
    -CandidatePath $candidateTracePath `
    -EnvelopePolicy $negativeControlPolicy `
    -AllowBaselineNormalizedRigidbody:$allowBaselineNormalizedRigidbody `
    -RigidbodyXWitnessPath $rigidbodyXWitnessPath `
    -RigidbodyYWitnessPath $rigidbodyYWitnessPath

$pass = [bool]$baselineComparison.equivalent `
    -and [bool]$phaseComparison.equivalent `
    -and [bool]$traceComparison.inputEquivalent `
    -and [bool]$traceComparison.gameplayEquivalent `
    -and [bool]$traceComparison.baselineNormalizedRigidbodyEquivalent
$generatedArtifacts = @(
    Get-ChildItem `
        -LiteralPath $firstDifferenceDirectory `
        -File `
        -ErrorAction SilentlyContinue |
        Sort-Object Name |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName
                sha256 = (Get-FileHash `
                    -LiteralPath $_.FullName `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
$report = [ordered]@{
    schemaVersion = 1
    verdict = if ($pass) { 'PASS' } else { 'FAIL' }
    evidenceClass = 'OFFLINE_RECOMPUTED_DIAGNOSTIC'
    eligibleForFreshMatrix = $false
    reason = `
        'Recomputes immutable captured evidence with the current comparator; does not replace a fresh cold-start run.'
    sourceEvidenceRoot = $SourceEvidenceRoot
    sourceFinalVerdictPath = $sourceVerdictPath
    sourceFinalVerdictSha256 = (Get-FileHash `
        -LiteralPath $sourceVerdictPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceOriginalVerdict = [string]$sourceVerdict.verdict
    mode = $Mode
    frameCount = $maxTicks
    candidateScript = $CandidateScriptPath
    candidateScriptSha256 = (Get-FileHash `
        -LiteralPath $CandidateScriptPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    negativeControlEnvelopePath = $NegativeControlEnvelopePath
    negativeControlEnvelopeSha256 = $negativeControlPolicy.sha256
    immutableInputs = [ordered]@{
        candidateTracePath = $candidateTracePath
        candidateTraceSha256 = $candidateTraceSha256
        candidateBaselinePath = $candidateBaselinePath
        candidateBaselineSha256 = $candidateBaselineSha256
        originallySelectedReferenceTraceSha256 = $referenceTraceSha256
        originallySelectedReferenceBaselineSha256 =
            $referenceBaselineSha256
    }
    selectedReference = [ordered]@{
        attempt = [int]$matchedReference.attempt
        name = [string]$matchedReference.name
        matchKind = [string]$matchedReference.matchKind
        tracePath = $referenceTracePath
        traceSha256 = (Get-FileHash `
            -LiteralPath $referenceTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        baselinePath = $referenceBaselinePath
        baselineSha256 = (Get-FileHash `
            -LiteralPath $referenceBaselinePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        rigidbodyXWitnessAttempt =
            [int]$matchedReference.rigidbodyXWitnessAttempt
        rigidbodyXWitnessTraceSha256 =
            [string]$matchedReference.rigidbodyXWitnessTraceSha256
        rigidbodyYWitnessAttempt =
            [int]$matchedReference.rigidbodyYWitnessAttempt
        rigidbodyYWitnessTraceSha256 =
            [string]$matchedReference.rigidbodyYWitnessTraceSha256
    }
    baseline = $baselineComparison
    phase = $phaseComparison
    trace = $traceComparison
    generatedArtifacts = $generatedArtifacts
}
$reportPath = Join-Path $OutputRoot 'offline-verdict.json'
$report |
    ConvertTo-Json -Depth 30 |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM

Write-Output (
    'T24 captured-candidate offline verdict: {0}; {1}' -f `
        $report.verdict, `
        $reportPath)
if (-not $pass) {
    exit 1
}
