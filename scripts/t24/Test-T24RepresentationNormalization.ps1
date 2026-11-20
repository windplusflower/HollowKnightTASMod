[CmdletBinding()]
param(
    [string]$CandidateScriptPath = '',

    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 representation-normalization test requires PowerShell 7.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
if ([string]::IsNullOrWhiteSpace($CandidateScriptPath)) {
    $CandidateScriptPath = Join-Path `
        $PSScriptRoot `
        'Invoke-T24CandidateSmoke.ps1'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path `
        $repoRoot `
        ('artifacts\vanilla-equivalence\t24-representation-normalization-' `
            + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') `
            + '.json')
}
$CandidateScriptPath = [IO.Path]::GetFullPath($CandidateScriptPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $CandidateScriptPath -PathType Leaf)) {
    throw "Candidate script is missing: $CandidateScriptPath"
}
if (Test-Path -LiteralPath $OutputPath) {
    throw "Output already exists: $OutputPath"
}

$tokens = $null
$parseErrors = $null
$candidateAst = [Management.Automation.Language.Parser]::ParseFile(
    $CandidateScriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw 'Candidate smoke script has PowerShell parse errors.'
}
$requiredFunctions = @(
    'Get-FieldMap',
    'Convert-T24Float32Hex',
    'Get-T24Float32UlpDistance',
    'Get-T24Float32FieldValue',
    'Get-T24Float32SubtractionHex',
    'Test-T24RigidbodyAxisWitnessTrace',
    'Test-T24DerivedRenderDeltaNormalization',
    'Test-T24ProcessAgeClockResidualNormalization')
foreach ($name in $requiredFunctions) {
    $functionAst = @(
        $candidateAst.EndBlock.Statements |
            Where-Object {
                $_ -is `
                    [Management.Automation.Language.FunctionDefinitionAst] `
                -and $_.Name -eq $name
            })
    if ($functionAst.Count -ne 1) {
        throw "Candidate function is missing or duplicated: $name"
    }
    . ([scriptblock]::Create($functionAst[0].Extent.Text))
}

function New-T24SyntheticField {
    param(
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Hex,
        [bool]$Comparable = $true
    )

    return [pscustomobject][ordered]@{
        key = $Key
        kind = $Kind
        canonicalHex = $Hex
        displayValue = ''
        comparable = $Comparable
    }
}

function New-T24SyntheticMap {
    param([Parameter(Mandatory)][object[]]$Fields)

    return Get-FieldMap -Frame ([pscustomobject]@{ fields = $Fields })
}

function New-T24RenderMap {
    param(
        [Parameter(Mandatory)][string]$Position,
        [Parameter(Mandatory)][string]$Delta,
        [string]$RigidbodyPosition = '3f800000',
        [string]$RigidbodyDelta = '3f800000'
    )

    return New-T24SyntheticMap -Fields @(
        (New-T24SyntheticField `
            -Key 'hero.position.x' `
            -Kind 'Float32Bits' `
            -Hex $Position),
        (New-T24SyntheticField `
            -Key 'hero.positionDelta.x' `
            -Kind 'Float32Bits' `
            -Hex $Delta),
        (New-T24SyntheticField `
            -Key 'hero.rigidbody.position.x' `
            -Kind 'Float32Bits' `
            -Hex $RigidbodyPosition),
        (New-T24SyntheticField `
            -Key 'hero.rigidbody.positionDelta.x' `
            -Kind 'Float32Bits' `
            -Hex $RigidbodyDelta))
}

function New-T24ClockMap {
    param(
        [Parameter(Mandatory)][string]$Raw,
        [Parameter(Mandatory)][string]$FixedRaw,
        [Parameter(Mandatory)][string]$Residual,
        [string]$Relative = '3ca3d70a',
        [string]$FixedRelative = '3ca3d70a',
        [bool]$IncludeFixedRaw = $true
    )

    $fields = [Collections.Generic.List[object]]::new()
    foreach ($field in @(
            (New-T24SyntheticField `
                -Key 'time.relative' `
                -Kind 'Float32Bits' `
                -Hex $Relative),
            (New-T24SyntheticField `
                -Key 'time.fixedRelative' `
                -Kind 'Float32Bits' `
                -Hex $FixedRelative),
            (New-T24SyntheticField `
                -Key 'time.deltaTime' `
                -Kind 'Float32Bits' `
                -Hex '3ca3d70a'),
            (New-T24SyntheticField `
                -Key 'time.fixedDeltaTime' `
                -Kind 'Float32Bits' `
                -Hex '3ca3d70a'),
            (New-T24SyntheticField `
                -Key 'time.captureDeltaTime' `
                -Kind 'Float32Bits' `
                -Hex '3ca3d70a'),
            (New-T24SyntheticField `
                -Key 'time.timeScale' `
                -Kind 'Float32Bits' `
                -Hex '3f800000'),
            (New-T24SyntheticField `
                -Key 'time.timeMinusFixed' `
                -Kind 'Float32Bits' `
                -Hex $Residual),
            (New-T24SyntheticField `
                -Key 'diagnostic.time.raw' `
                -Kind 'Float32Bits' `
                -Hex $Raw `
                -Comparable $false),
            (New-T24SyntheticField `
                -Key 'tick.fixedSteps' `
                -Kind 'Int32' `
                -Hex '00000001'))) {
        $fields.Add($field)
    }
    if ($IncludeFixedRaw) {
        $fields.Add(
            (New-T24SyntheticField `
                -Key 'diagnostic.time.fixedRaw' `
                -Kind 'Float32Bits' `
                -Hex $FixedRaw `
                -Comparable $false))
    }
    return New-T24SyntheticMap -Fields @($fields)
}

function New-T24RigidbodyFrameLine {
    param(
        [Parameter(Mandatory)][long]$Tick,
        [Parameter(Mandatory)][string]$Position,
        [Parameter(Mandatory)][string]$Delta
    )

    return ([pscustomobject][ordered]@{
            logicalTick = $Tick
            fields = @(
                (New-T24SyntheticField `
                    -Key 'hero.rigidbody.position.x' `
                    -Kind 'Float32Bits' `
                    -Hex $Position),
                (New-T24SyntheticField `
                    -Key 'hero.rigidbody.positionDelta.x' `
                    -Kind 'Float32Bits' `
                    -Hex $Delta))
        } | ConvertTo-Json -Depth 10 -Compress)
}

$renderPolicy = [pscustomobject]@{
    fields = [ordered]@{
        'hero.position.x' = [pscustomobject]@{
            maxAbsoluteDifference = 2.0e-7
        }
        'hero.positionDelta.x' = [pscustomobject]@{
            maxAbsoluteDifference = 1.0e-8
        }
    }
}
$referenceOrigin = New-T24RenderMap `
    -Position '00000000' `
    -Delta '00000000' `
    -RigidbodyPosition '00000000' `
    -RigidbodyDelta '00000000'
$candidateOrigin = New-T24RenderMap `
    -Position '00000000' `
    -Delta '00000000' `
    -RigidbodyPosition '00000000' `
    -RigidbodyDelta '00000000'
$referenceRender = New-T24RenderMap `
    -Position '3f800000' `
    -Delta '3f800000'
$validRender = New-T24RenderMap `
    -Position '3f800001' `
    -Delta '3f800001'
$tamperedRender = New-T24RenderMap `
    -Position '3f800001' `
    -Delta '3f800002'
$rigidbodyDivergentRender = New-T24RenderMap `
    -Position '3f800001' `
    -Delta '3f800001' `
    -RigidbodyPosition '3f800001'
$rigidbodyWitnessMismatch = New-T24RenderMap `
    -Position '3f800001' `
    -Delta '3f800001' `
    -RigidbodyPosition '3f800002'

$renderPositive = Test-T24DerivedRenderDeltaNormalization `
    -ReferenceMap $referenceRender `
    -CandidateMap $validRender `
    -ReferenceOriginMap $referenceOrigin `
    -CandidateOriginMap $candidateOrigin `
    -EnvelopePolicy $renderPolicy `
    -DeltaKey 'hero.positionDelta.x'
$renderTampered = Test-T24DerivedRenderDeltaNormalization `
    -ReferenceMap $referenceRender `
    -CandidateMap $tamperedRender `
    -ReferenceOriginMap $referenceOrigin `
    -CandidateOriginMap $candidateOrigin `
    -EnvelopePolicy $renderPolicy `
    -DeltaKey 'hero.positionDelta.x'
$renderRigidbodyDivergent = `
    Test-T24DerivedRenderDeltaNormalization `
        -ReferenceMap $referenceRender `
        -CandidateMap $rigidbodyDivergentRender `
        -ReferenceOriginMap $referenceOrigin `
        -CandidateOriginMap $candidateOrigin `
        -EnvelopePolicy $renderPolicy `
        -DeltaKey 'hero.positionDelta.x'
$renderWitnessPositive = Test-T24DerivedRenderDeltaNormalization `
    -ReferenceMap $referenceRender `
    -CandidateMap $rigidbodyDivergentRender `
    -ReferenceOriginMap $referenceOrigin `
    -CandidateOriginMap $candidateOrigin `
    -EnvelopePolicy $renderPolicy `
    -DeltaKey 'hero.positionDelta.x' `
    -RigidbodyWitnessMap $rigidbodyDivergentRender `
    -RigidbodyWitnessOriginMap $candidateOrigin `
    -RigidbodyWitnessTraceEquivalent $true
$renderWitnessMismatch = Test-T24DerivedRenderDeltaNormalization `
    -ReferenceMap $referenceRender `
    -CandidateMap $rigidbodyDivergentRender `
    -ReferenceOriginMap $referenceOrigin `
    -CandidateOriginMap $candidateOrigin `
    -EnvelopePolicy $renderPolicy `
    -DeltaKey 'hero.positionDelta.x' `
    -RigidbodyWitnessMap $rigidbodyWitnessMismatch `
    -RigidbodyWitnessOriginMap $candidateOrigin `
    -RigidbodyWitnessTraceEquivalent $true

$axisCandidateLines = @(
    (New-T24RigidbodyFrameLine `
        -Tick 0 `
        -Position '00000000' `
        -Delta '00000000'),
    (New-T24RigidbodyFrameLine `
        -Tick 1 `
        -Position '3f800001' `
        -Delta '3f800001'))
$axisExactWitnessLines = @($axisCandidateLines)
$axisMixedWitnessLines = @(
    $axisCandidateLines[0],
    (New-T24RigidbodyFrameLine `
        -Tick 1 `
        -Position '3f800002' `
        -Delta '3f800001'))
$axisExactWitness = Test-T24RigidbodyAxisWitnessTrace `
    -CandidateLines $axisCandidateLines `
    -WitnessLines $axisExactWitnessLines `
    -Axis 'x'
$axisMixedWitness = Test-T24RigidbodyAxisWitnessTrace `
    -CandidateLines $axisCandidateLines `
    -WitnessLines $axisMixedWitnessLines `
    -Axis 'x'

$clockReference = New-T24ClockMap `
    -Raw '42800000' `
    -FixedRaw '42800000' `
    -Residual '00000000'
$clockValid = New-T24ClockMap `
    -Raw '42800001' `
    -FixedRaw '42800000' `
    -Residual '37000000'
$clockTwoUlp = New-T24ClockMap `
    -Raw '42800002' `
    -FixedRaw '42800000' `
    -Residual '37800000'
$clockTamperedResidual = New-T24ClockMap `
    -Raw '42800001' `
    -FixedRaw '42800000' `
    -Residual '37800000'
$clockNonzeroReference = New-T24ClockMap `
    -Raw '42800000' `
    -FixedRaw '42800000' `
    -Residual '00000000' `
    -Relative '3d23d70a' `
    -FixedRelative '3ca3d70a'
$clockNonzeroCandidate = New-T24ClockMap `
    -Raw '42800001' `
    -FixedRaw '42800000' `
    -Residual '37000000' `
    -Relative '3d23d70a' `
    -FixedRelative '3ca3d70a'
$clockMissingRaw = New-T24ClockMap `
    -Raw '42800001' `
    -FixedRaw '42800000' `
    -Residual '37000000' `
    -IncludeFixedRaw $false

$clockPositive = Test-T24ProcessAgeClockResidualNormalization `
    -ReferenceMap $clockReference `
    -CandidateMap $clockValid
$clockTwoUlpResult = Test-T24ProcessAgeClockResidualNormalization `
    -ReferenceMap $clockReference `
    -CandidateMap $clockTwoUlp
$clockTamperedResult = Test-T24ProcessAgeClockResidualNormalization `
    -ReferenceMap $clockReference `
    -CandidateMap $clockTamperedResidual
$clockNonzeroResult = Test-T24ProcessAgeClockResidualNormalization `
    -ReferenceMap $clockNonzeroReference `
    -CandidateMap $clockNonzeroCandidate
$clockMissingResult = Test-T24ProcessAgeClockResidualNormalization `
    -ReferenceMap $clockReference `
    -CandidateMap $clockMissingRaw

if (-not [bool]$renderPositive.accepted `
        -or -not [bool]$renderWitnessPositive.accepted `
        -or [string]$renderWitnessPositive.authoritativeRigidbodySource `
            -ne 'full-no-mod-axis-witness' `
        -or [bool]$renderTampered.accepted `
        -or [string]$renderTampered.rejection `
            -ne 'recorded-render-delta-is-not-bitwise-derived' `
        -or [bool]$renderRigidbodyDivergent.accepted `
        -or [string]$renderRigidbodyDivergent.rejection `
            -ne 'authoritative-rigidbody-axis-is-not-bitwise-exact' `
        -or [bool]$renderWitnessMismatch.accepted `
        -or [string]$renderWitnessMismatch.rejection `
            -ne 'authoritative-rigidbody-axis-witness-does-not-match' `
        -or -not [bool]$axisExactWitness.equivalent `
        -or [bool]$axisMixedWitness.equivalent `
        -or [string]$axisMixedWitness.firstMismatch.reason `
            -ne 'rigidbody-axis-witness-bitwise-mismatch' `
        -or -not [bool]$clockPositive.accepted `
        -or [bool]$clockTwoUlpResult.accepted `
        -or [string]$clockTwoUlpResult.rejection `
            -ne 'raw-clock-distance-exceeds-one-ulp' `
        -or [bool]$clockTamperedResult.accepted `
        -or [string]$clockTamperedResult.rejection `
            -ne 'recorded-clock-residual-is-not-bitwise-derived' `
        -or [bool]$clockNonzeroResult.accepted `
        -or [string]$clockNonzeroResult.rejection `
            -ne 'logical-time-minus-fixed-is-nonzero' `
        -or [bool]$clockMissingResult.accepted `
        -or [string]$clockMissingResult.rejection `
            -ne 'required-raw-clock-field-missing') {
    throw 'T24 representation normalization negative control failed.'
}

$report = [ordered]@{
    schemaVersion = 1
    verdict = 'PASS'
    candidateScript = $CandidateScriptPath
    candidateScriptSha256 = (Get-FileHash `
        -LiteralPath $CandidateScriptPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    positive = [ordered]@{
        derivedRenderDelta = $renderPositive
        derivedRenderDeltaWithFullTraceRigidbodyWitness =
            $renderWitnessPositive
        fullTraceRigidbodyAxisWitness = $axisExactWitness
        processAgeClockResidual = $clockPositive
    }
    negative = [ordered]@{
        tamperedRenderDelta = $renderTampered
        authoritativeRigidbodyDivergence = `
            $renderRigidbodyDivergent
        authoritativeRigidbodyWitnessCurrentMismatch =
            $renderWitnessMismatch
        mixedRigidbodyAxisWitnessTrace = $axisMixedWitness
        rawClockTwoUlp = $clockTwoUlpResult
        tamperedClockResidual = $clockTamperedResult
        logicalClockPhaseNonzero = $clockNonzeroResult
        rawClockFieldMissing = $clockMissingResult
    }
}
$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force |
        Out-Null
}
$report |
    ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "T24 representation normalization PASS: $OutputPath"
