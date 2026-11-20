[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidateBaselinePath,

    [Parameter(Mandatory)]
    [string]$NegativeControlEnvelopePath,

    [Parameter(Mandatory)]
    [string]$SupplementalReferenceCatalogPath,

    [Parameter(Mandatory)]
    [string]$EvidencePath,

    [string]$CandidateScriptPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 baseline-catalog verification requires PowerShell 7.'
}
if ([string]::IsNullOrWhiteSpace($CandidateScriptPath)) {
    $CandidateScriptPath = Join-Path `
        $PSScriptRoot `
        'Invoke-T24CandidateSmoke.ps1'
}

$CandidateScriptPath = [IO.Path]::GetFullPath($CandidateScriptPath)
$CandidateBaselinePath = [IO.Path]::GetFullPath($CandidateBaselinePath)
$NegativeControlEnvelopePath =
    [IO.Path]::GetFullPath($NegativeControlEnvelopePath)
$SupplementalReferenceCatalogPath =
    [IO.Path]::GetFullPath($SupplementalReferenceCatalogPath)
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)

foreach ($requiredPath in @(
        $CandidateScriptPath,
        $CandidateBaselinePath,
        $NegativeControlEnvelopePath,
        $SupplementalReferenceCatalogPath
    )) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required T24 baseline-catalog input is missing: $requiredPath"
    }
}
$evidenceDirectory = Split-Path -Parent $EvidencePath
if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
    New-Item `
        -ItemType Directory `
        -Path $evidenceDirectory `
        -Force |
        Out-Null
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
$topLevelFunctions = @(
    $candidateAst.EndBlock.Statements |
        Where-Object {
            $_ -is [Management.Automation.Language.FunctionDefinitionAst]
        })
foreach ($requiredFunction in @(
        'Get-T24BaselineSignature',
        'Get-T24AuthoritativeBaselineSignature',
        'Get-T24SemanticBaselineSignature',
        'Get-T24BaselineFloatHex',
        'Convert-T24Float32Hex',
        'Test-T24BaselineHeroZVanillaContract',
        'Test-T24BaselineRigidbodyComponentCatalog',
        'Resolve-T24ReferenceForBaseline'
    )) {
    if (@(
            $topLevelFunctions |
                Where-Object Name -eq $requiredFunction).Count -ne 1) {
        throw "Candidate function is missing or duplicated: $requiredFunction"
    }
}
foreach ($functionAst in $topLevelFunctions) {
    . ([scriptblock]::Create($functionAst.Extent.Text))
}

$envelope = Get-Content `
    -LiteralPath $NegativeControlEnvelopePath `
    -Raw |
    ConvertFrom-Json
$supplemental = Get-Content `
    -LiteralPath $SupplementalReferenceCatalogPath `
    -Raw |
    ConvertFrom-Json
$sources = [Collections.Generic.List[object]]::new()
foreach ($cohort in @($envelope.exactBaselineCatalog)) {
    foreach ($member in @($cohort.members)) {
        $sources.Add([pscustomobject][ordered]@{
            attempt = [int]$member.attempt
            name = [string]$member.name
            baselinePath = [IO.Path]::GetFullPath(
                [string]$member.baselinePath)
            baselineSha256 = [string]$member.baselineSha256
            tracePath = [IO.Path]::GetFullPath([string]$member.tracePath)
            traceSha256 = [string]$member.traceSha256
            phasePath = [IO.Path]::GetFullPath([string]$member.phasePath)
        })
    }
}
foreach ($member in @($supplemental.members)) {
    $sources.Add([pscustomobject][ordered]@{
        attempt = [int]$member.catalogAttempt
        name = [string]$member.name
        baselinePath = [IO.Path]::GetFullPath(
            [string]$member.baselinePath)
        baselineSha256 = [string]$member.baselineSha256
        tracePath = [IO.Path]::GetFullPath([string]$member.tracePath)
        traceSha256 = [string]$member.traceSha256
        phasePath = [IO.Path]::GetFullPath([string]$member.phasePath)
    })
}
if ($sources.Count -eq 0) {
    throw 'The frozen T24 catalogs contain no baseline sources.'
}

$references = @(
    foreach ($source in $sources) {
        if (-not (Test-Path `
                -LiteralPath $source.baselinePath `
                -PathType Leaf) `
                -or (Get-FileHash `
                    -LiteralPath $source.baselinePath `
                    -Algorithm SHA256).Hash.ToLowerInvariant() `
                    -ne [string]$source.baselineSha256) {
            throw 'A frozen T24 catalog baseline is missing or changed.'
        }
        [pscustomobject][ordered]@{
            signatureSha256 = Get-T24BaselineSignature `
                -Path $source.baselinePath
            authoritativeSignatureSha256 =
                Get-T24AuthoritativeBaselineSignature `
                    -Path $source.baselinePath
            semanticSignatureSha256 = Get-T24SemanticBaselineSignature `
                -Path $source.baselinePath
            rigidbodyPositionXCanonicalHex = Get-T24BaselineFloatHex `
                -Path $source.baselinePath `
                -Key 'rigidbodyPositionX'
            rigidbodyPositionYCanonicalHex = Get-T24BaselineFloatHex `
                -Path $source.baselinePath `
                -Key 'rigidbodyPositionY'
            attempt = [int]$source.attempt
            name = [string]$source.name
            baselinePath = [string]$source.baselinePath
            tracePath = [string]$source.tracePath
            traceSha256 = [string]$source.traceSha256
            phasePath = [string]$source.phasePath
        }
    })
$policy = [pscustomobject][ordered]@{
    fields = [ordered]@{}
    referenceCatalog = $references
}

$syntheticDirectory = Join-Path $evidenceDirectory 'synthetic-inputs'
New-Item -ItemType Directory -Path $syntheticDirectory -Force |
    Out-Null
$semanticMissPath = Join-Path $syntheticDirectory 'semantic-miss.json'
$semanticMiss = Get-Content -LiteralPath $CandidateBaselinePath -Raw |
    ConvertFrom-Json -AsHashtable
$semanticMiss['heroAnimationClip'] = 'T24 Synthetic Semantic Miss'
$semanticMiss |
    ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $semanticMissPath -Encoding utf8NoBOM
$outOfRangePath = Join-Path $syntheticDirectory 'hero-z-out-of-range.json'
$outOfRange = Get-Content -LiteralPath $CandidateBaselinePath -Raw |
    ConvertFrom-Json -AsHashtable
$outOfRange['heroPositionZ'] = [ordered]@{
    value = [single]0
    canonicalHex = '00000000'
}
$outOfRange |
    ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $outOfRangePath -Encoding utf8NoBOM

$candidateSemanticSignature = Get-T24SemanticBaselineSignature `
    -Path $CandidateBaselinePath
$semanticMissSignature = Get-T24SemanticBaselineSignature `
    -Path $semanticMissPath
$emptyComparison = Test-T24BaselineRigidbodyComponentCatalog `
    -CandidatePath $semanticMissPath `
    -EnvelopePolicy $policy
$emptyResolution = Resolve-T24ReferenceForBaseline `
    -EnvelopePolicy $policy `
    -CandidateBaselinePath $semanticMissPath
$emptyCatalogPass = -not [bool]$emptyComparison.equivalent `
    -and [int]$emptyComparison.semanticReferenceCount -eq 0 `
    -and @($emptyComparison.observedXCanonicalHex).Count -eq 0 `
    -and @($emptyComparison.observedYCanonicalHex).Count -eq 0 `
    -and $null -eq $emptyResolution

$candidateHeroZContract = Test-T24BaselineHeroZVanillaContract `
    -CandidatePath $CandidateBaselinePath
$candidateResolution = Resolve-T24ReferenceForBaseline `
    -EnvelopePolicy $policy `
    -CandidateBaselinePath $CandidateBaselinePath
$candidateHeroZPass = [bool]$candidateHeroZContract.equivalent `
    -and $null -ne $candidateResolution `
    -and [bool]$candidateResolution.heroZVanillaVisualRandomEquivalent `
    -and -not [bool]$candidateResolution.heroZVanillaVisualRandomExact `
    -and [string]$candidateResolution.heroZVanillaVisualRandomPolicyId `
        -ceq 'hero-z-vanilla-setz-random-v1' `
    -and [string]$candidateResolution.matchKind `
        -like '*hero-z-vanilla-visual-random'

$outOfRangeContract = Test-T24BaselineHeroZVanillaContract `
    -CandidatePath $outOfRangePath
$outOfRangeResolution = Resolve-T24ReferenceForBaseline `
    -EnvelopePolicy $policy `
    -CandidateBaselinePath $outOfRangePath
$outOfRangePass = -not [bool]$outOfRangeContract.equivalent `
    -and [string]$outOfRangeContract.candidateFailureReason `
        -ceq 'outside-vanilla-setz-range' `
    -and $null -eq $outOfRangeResolution

$missingPolicy = [pscustomobject][ordered]@{
    fields = [ordered]@{}
    referenceCatalog = @(
        [pscustomobject][ordered]@{
            semanticSignatureSha256 = $candidateSemanticSignature
            rigidbodyPositionYCanonicalHex = '421469ce'
        })
}
$missingRejected = $false
$missingMessage = ''
try {
    $null = Test-T24BaselineRigidbodyComponentCatalog `
        -CandidatePath $CandidateBaselinePath `
        -EnvelopePolicy $missingPolicy
}
catch {
    $missingMessage = $_.Exception.Message
    $missingRejected = $missingMessage -like `
        'T24 baseline reference catalog integrity failure:*'
}

$malformedPolicy = [pscustomobject][ordered]@{
    fields = [ordered]@{}
    referenceCatalog = @(
        [pscustomobject][ordered]@{
            semanticSignatureSha256 = $candidateSemanticSignature
            rigidbodyPositionXCanonicalHex = 'NOTHEX'
            rigidbodyPositionYCanonicalHex = '421469ce'
        })
}
$malformedRejected = $false
$malformedMessage = ''
try {
    $null = Test-T24BaselineRigidbodyComponentCatalog `
        -CandidatePath $CandidateBaselinePath `
        -EnvelopePolicy $malformedPolicy
}
catch {
    $malformedMessage = $_.Exception.Message
    $malformedRejected = $malformedMessage -like `
        'T24 baseline reference catalog integrity failure:*'
}

$knownBaselinePath = [string]$sources[0].baselinePath
$knownComparison = Test-T24BaselineRigidbodyComponentCatalog `
    -CandidatePath $knownBaselinePath `
    -EnvelopePolicy $policy
$knownResolution = Resolve-T24ReferenceForBaseline `
    -EnvelopePolicy $policy `
    -CandidateBaselinePath $knownBaselinePath
$knownBaselinePass = [bool]$knownComparison.equivalent `
    -and $null -ne $knownResolution `
    -and [string]$knownResolution.matchKind -eq 'exact' `
    -and [bool]$knownResolution.heroZVanillaVisualRandomEquivalent `
    -and [bool]$knownResolution.heroZVanillaVisualRandomExact `
    -and [string]$knownResolution.heroZVanillaVisualRandomPolicyId `
        -ceq 'hero-z-vanilla-setz-random-v1'

$verdict = if ($emptyCatalogPass `
        -and $candidateHeroZPass `
        -and $outOfRangePass `
        -and $missingRejected `
        -and $malformedRejected `
        -and $knownBaselinePass) {
    'PASS'
}
else {
    'FAIL'
}
$artifact = [ordered]@{
    schemaVersion = 2
    verdict = $verdict
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    candidateScript = $CandidateScriptPath
    candidateScriptSha256 = (Get-FileHash `
        -LiteralPath $CandidateScriptPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    candidateBaseline = $CandidateBaselinePath
    candidateBaselineSha256 = (Get-FileHash `
        -LiteralPath $CandidateBaselinePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    negativeControlEnvelope = $NegativeControlEnvelopePath
    negativeControlEnvelopeSha256 = (Get-FileHash `
        -LiteralPath $NegativeControlEnvelopePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    supplementalReferenceCatalog = $SupplementalReferenceCatalogPath
    supplementalReferenceCatalogSha256 = (Get-FileHash `
        -LiteralPath $SupplementalReferenceCatalogPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    catalogCount = $references.Count
    candidateSemanticSignatureSha256 = $candidateSemanticSignature
    syntheticSemanticMissSignatureSha256 = $semanticMissSignature
    emptySemanticCatalog = [ordered]@{
        pass = $emptyCatalogPass
        semanticReferenceCount =
            [int]$emptyComparison.semanticReferenceCount
        observedXCount = @($emptyComparison.observedXCanonicalHex).Count
        observedYCount = @($emptyComparison.observedYCanonicalHex).Count
        resolverReturnedNull = $null -eq $emptyResolution
        syntheticBaseline = $semanticMissPath
        syntheticBaselineSha256 = (Get-FileHash `
            -LiteralPath $semanticMissPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    candidateHeroZVanillaVisualRandom = [ordered]@{
        pass = $candidateHeroZPass
        contract = $candidateHeroZContract
        resolverReturnedReference = $null -ne $candidateResolution
        matchKind = if ($null -eq $candidateResolution) { '' }
            else { [string]$candidateResolution.matchKind }
        equivalent = if ($null -eq $candidateResolution) { $false }
            else {
                [bool]$candidateResolution.heroZVanillaVisualRandomEquivalent
            }
        exact = if ($null -eq $candidateResolution) { $false }
            else {
                [bool]$candidateResolution.heroZVanillaVisualRandomExact
            }
        policyId = if ($null -eq $candidateResolution) { '' }
            else {
                [string]$candidateResolution.heroZVanillaVisualRandomPolicyId
            }
    }
    outOfRangeHeroZ = [ordered]@{
        pass = $outOfRangePass
        contract = $outOfRangeContract
        resolverReturnedNull = $null -eq $outOfRangeResolution
        syntheticBaseline = $outOfRangePath
        syntheticBaselineSha256 = (Get-FileHash `
            -LiteralPath $outOfRangePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    malformedCatalog = [ordered]@{
        missingComponentRejected = $missingRejected
        missingComponentMessage = $missingMessage
        malformedCanonicalHexRejected = $malformedRejected
        malformedCanonicalHexMessage = $malformedMessage
    }
    knownEligibleBaseline = [ordered]@{
        pass = $knownBaselinePass
        path = $knownBaselinePath
        matchKind = if ($null -eq $knownResolution) {
            ''
        }
        else {
            [string]$knownResolution.matchKind
        }
        heroZEquivalent = if ($null -eq $knownResolution) { $false }
            else {
                [bool]$knownResolution.heroZVanillaVisualRandomEquivalent
            }
        heroZExact = if ($null -eq $knownResolution) { $false }
            else { [bool]$knownResolution.heroZVanillaVisualRandomExact }
        heroZPolicyId = if ($null -eq $knownResolution) { '' }
            else {
                [string]$knownResolution.heroZVanillaVisualRandomPolicyId
            }
    }
}

$artifact |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM

Write-Output "T24 baseline catalog fail-closed verification: $verdict; $EvidencePath"
if ($verdict -ne 'PASS') {
    exit 1
}
