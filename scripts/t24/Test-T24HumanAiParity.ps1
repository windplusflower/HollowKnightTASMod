[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManualMatrixPath,

    [Parameter(Mandatory)]
    [string[]]$AiMatrixPaths,

    [Parameter(Mandatory)]
    [string]$NegativeControlEnvelopePath,

    [ValidateRange(1, 20)]
    [int]$RequiredPairs = 10,

    [string]$OutputPath = '',

    [switch]$NoThrowOnFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 Human/AI parity verification requires PowerShell 7 or newer.'
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$repositoryPrefix = $repositoryRoot.TrimEnd('\', '/') `
    + [IO.Path]::DirectorySeparatorChar
$utf8NoBom = [Text.UTF8Encoding]::new($false, $true)
$failures = [Collections.Generic.List[object]]::new()

function Resolve-ExistingFile {
    param([Parameter(Mandatory)][string]$Path)

    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Required file is missing: $full"
    }
    return $full
}

function Get-Sha256File {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)

    $bytes = $script:utf8NoBom.GetBytes($Text)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Add-Failure {
    param(
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$Detail,
        [object]$Context = $null
    )

    $script:failures.Add([ordered]@{
        code = $Code
        detail = $Detail
        context = $Context
    }) | Out-Null
}

function Assert-EqualValue {
    param(
        [Parameter(Mandatory)][string]$Code,
        [object]$Expected,
        [object]$Actual,
        [Parameter(Mandatory)][string]$Context
    )

    if ([string]$Expected -ne [string]$Actual) {
        Add-Failure `
            -Code $Code `
            -Detail "$Context expected='$Expected' actual='$Actual'"
    }
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name,
        [object]$Default = $null
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $Default
    }
    return $property.Value
}

function Get-FieldMap {
    param([Parameter(Mandatory)]$Frame)

    $map = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($field in @($Frame.fields)) {
        $key = [string]$field.key
        if (-not $map.TryAdd($key, $field)) {
            throw "Trace contains duplicate field '$key'."
        }
    }
    return $map
}

function Add-HashText {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.IncrementalHash]$Hasher,
        [Parameter(Mandatory)][string]$Text
    )

    $Hasher.AppendData($script:utf8NoBom.GetBytes($Text))
}

function Complete-Hash {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.IncrementalHash]$Hasher
    )

    try {
        return [Convert]::ToHexString(
            $Hasher.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $Hasher.Dispose()
    }
}

function Get-TraceSummary {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.HashSet[string]]$ToleratedKeys
    )

    $lines = [IO.File]::ReadAllLines($Path)
    $inputKeys = @(
        'input.axisX',
        'input.axisY',
        'input.held',
        'input.pressed',
        'input.released')
    $inputHasher = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $authoritativeHasher =
        [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
    $inputFrameHashes = [Collections.Generic.List[string]]::new()
    $authoritativeFrameHashes = [Collections.Generic.List[string]]::new()
    $tolerated = [ordered]@{}
    foreach ($key in $ToleratedKeys) {
        $tolerated[$key] = [Collections.Generic.List[string]]::new()
    }

    for ($index = 0; $index -lt $lines.Count; $index++) {
        $frame = $lines[$index] | ConvertFrom-Json
        if ([int]$frame.logicalTick -ne $index) {
            throw (
                "Trace logical tick mismatch at line $index in $Path; " `
                + "actual=$($frame.logicalTick)")
        }
        $map = Get-FieldMap -Frame $frame
        $inputBuilder = [Text.StringBuilder]::new(192)
        [void]$inputBuilder.Append($index).Append('|')
        foreach ($key in $inputKeys) {
            if (-not $map.ContainsKey($key)) {
                throw "Trace is missing input field '$key': $Path"
            }
            $field = $map[$key]
            [void]$inputBuilder.Append($key)
            [void]$inputBuilder.Append('=')
            [void]$inputBuilder.Append([string]$field.kind)
            [void]$inputBuilder.Append(':')
            [void]$inputBuilder.Append([string]$field.canonicalHex)
            [void]$inputBuilder.Append(';')
        }
        $inputLine = $inputBuilder.ToString()
        Add-HashText -Hasher $inputHasher -Text ($inputLine + "`n")
        $inputFrameHashes.Add((Get-Sha256Text -Text $inputLine)) |
            Out-Null

        $authoritativeBuilder = [Text.StringBuilder]::new(8192)
        [void]$authoritativeBuilder.Append($index).Append('|')
        $lastKey = ''
        foreach ($field in @($frame.fields)) {
            $key = [string]$field.key
            if ([string]::CompareOrdinal($lastKey, $key) -gt 0) {
                throw "Trace fields are not in canonical key order: $Path"
            }
            $lastKey = $key
            if (-not [bool]$field.comparable) {
                continue
            }
            if ($ToleratedKeys.Contains($key)) {
                $tolerated[$key].Add([string]$field.canonicalHex) |
                    Out-Null
                continue
            }
            [void]$authoritativeBuilder.Append($key)
            [void]$authoritativeBuilder.Append('=')
            [void]$authoritativeBuilder.Append([string]$field.kind)
            [void]$authoritativeBuilder.Append(':')
            [void]$authoritativeBuilder.Append(
                [string]$field.canonicalHex)
            [void]$authoritativeBuilder.Append(';')
        }
        $authoritativeLine = $authoritativeBuilder.ToString()
        Add-HashText `
            -Hasher $authoritativeHasher `
            -Text ($authoritativeLine + "`n")
        $authoritativeFrameHashes.Add(
            (Get-Sha256Text -Text $authoritativeLine)) | Out-Null
    }

    return [pscustomobject][ordered]@{
        path = $Path
        sha256 = Get-Sha256File -Path $Path
        frameCount = $lines.Count
        inputSha256 = Complete-Hash -Hasher $inputHasher
        authoritativeGameplaySha256 =
            Complete-Hash -Hasher $authoritativeHasher
        inputFrameHashes = $inputFrameHashes
        authoritativeFrameHashes = $authoritativeFrameHashes
        toleratedValues = $tolerated
        lines = $lines
    }
}

function Get-PhaseSummary {
    param([Parameter(Mandatory)][string]$Path)

    $allLines = [IO.File]::ReadAllLines($Path)
    $start = -1
    for ($lineIndex = 0; $lineIndex -lt $allLines.Count; $lineIndex++) {
        $observation = $allLines[$lineIndex] | ConvertFrom-Json
        if ([string]$observation.phase -ne 'PlayerActionSet.after' `
                -or -not (
                    [bool]$observation.held `
                    -or [bool]$observation.pressed `
                    -or [bool]$observation.released)) {
            continue
        }
        $start = $lineIndex
        for ($beforeIndex = $lineIndex - 1; `
                $beforeIndex -ge 0; `
                $beforeIndex--) {
            $before = $allLines[$beforeIndex] | ConvertFrom-Json
            if ([long]$before.visualTick `
                    -ne [long]$observation.visualTick) {
                break
            }
            if ([string]$before.phase -eq 'PlayerActionSet.before') {
                $start = $beforeIndex
                break
            }
        }
        break
    }
    if ($start -lt 0) {
        throw "Gameplay phase start is missing: $Path"
    }
    $lines = @($allLines[$start..($allLines.Count - 1)])
    $commitIndex = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $item = $lines[$index] | ConvertFrom-Json
        if ([string]$item.phase -eq 'PlayerActionSet.after') {
            $commitIndex = $index
            break
        }
    }
    if ($commitIndex -lt 0) {
        throw "Action commit is missing: $Path"
    }

    $first = $lines[0] | ConvertFrom-Json
    $commit = $lines[$commitIndex] | ConvertFrom-Json
    $origin = [ordered]@{
        visualTick = [long]$first.visualTick
        updateTick = [long]$first.updateTick
        actionSetTick = [long]$commit.actionSetTick
        actionTick = [long]$commit.actionTick
    }
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $eventHashes = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $item = $lines[$index] | ConvertFrom-Json
        $canonical = [ordered]@{
            phase = [string]$item.phase
            held = [bool]$item.held
            pressed = [bool]$item.pressed
            released = [bool]$item.released
            gameObject = [string]$item.gameObject
            fsm = [string]$item.fsm
            state = [string]$item.state
            visualTickRelative =
                [long]$item.visualTick - [long]$origin.visualTick
            updateTickRelative =
                [long]$item.updateTick - [long]$origin.updateTick
            actionSetTickRelative = if ($index -lt $commitIndex) {
                -1L
            }
            else {
                [long]$item.actionSetTick - [long]$origin.actionSetTick
            }
            actionTickRelative = if ($index -lt $commitIndex) {
                -1L
            }
            else {
                [long]$item.actionTick - [long]$origin.actionTick
            }
        }
        $json = $canonical | ConvertTo-Json -Compress
        Add-HashText -Hasher $hasher -Text ($json + "`n")
        $eventHashes.Add((Get-Sha256Text -Text $json)) | Out-Null
    }
    return [pscustomobject][ordered]@{
        path = $Path
        sha256 = Get-Sha256File -Path $Path
        totalEventCount = $allLines.Count
        gameplayStartIndex = $start
        comparedEventCount = $lines.Count
        canonicalSha256 = Complete-Hash -Hasher $hasher
        eventHashes = $eventHashes
    }
}

function Convert-Float32Hex {
    param([Parameter(Mandatory)][string]$Hex)

    if ($Hex -notmatch '\A[0-9a-f]{8}\z') {
        throw "Invalid Float32 canonical hex: $Hex"
    }
    return [BitConverter]::UInt32BitsToSingle(
        [Convert]::ToUInt32($Hex, 16))
}

function Get-FirstHashDifference {
    param(
        [Parameter(Mandatory)]$Left,
        [Parameter(Mandatory)]$Right
    )

    $limit = [Math]::Min($Left.Count, $Right.Count)
    for ($index = 0; $index -lt $limit; $index++) {
        if ([string]$Left[$index] -ne [string]$Right[$index]) {
            return $index
        }
    }
    if ($Left.Count -ne $Right.Count) {
        return $limit
    }
    return $null
}

function Read-RunRecord {
    param(
        [Parameter(Mandatory)]$Matrix,
        [Parameter(Mandatory)]$Attempt,
        [Parameter(Mandatory)][bool]$Manual
    )

    $runRoot = [IO.Path]::GetFullPath([string]$Attempt.evidenceRoot)
    $verdictPath = Resolve-ExistingFile `
        -Path (Join-Path $runRoot 'final-verdict.json')
    $verdict = Get-Content -LiteralPath $verdictPath -Raw |
        ConvertFrom-Json
    $mode = [string]$verdict.mode
    $traceRoot = Join-Path $runRoot "traces\$mode"
    $tracePath = Resolve-ExistingFile `
        -Path (Join-Path $traceRoot 'trace.jsonl')
    $phasePath = Resolve-ExistingFile `
        -Path (Join-Path $traceRoot 'input-phase.jsonl')
    $verdictSha = Get-Sha256File -Path $verdictPath
    $traceSha = Get-Sha256File -Path $tracePath
    $context = "$mode attempt $($Attempt.attempt)"

    Assert-EqualValue -Code 'verdict-hash-mismatch' `
        -Expected ([string]$Attempt.verdictSha256) `
        -Actual $verdictSha -Context $context
    Assert-EqualValue -Code 'trace-hash-mismatch' `
        -Expected ([string]$Attempt.candidateTraceSha256) `
        -Actual $traceSha -Context $context
    Assert-EqualValue -Code 'final-trace-hash-mismatch' `
        -Expected ([string]$verdict.candidateTraceSha256) `
        -Actual $traceSha -Context $context

    # Keep run eligibility identical to the candidate matrix. A predeclared
    # vanilla render-transform baseline delta can make authoritative-exact
    # false while semantic state, observed Rigidbody components, and the
    # frozen render envelope all remain exact. Gameplay fields are still
    # checked tick-by-tick below and may not use that baseline exception.
    $requiredTrue = @(
        'referenceBaselineCatalogMatch',
        'baselineSemanticExactEquivalent',
        'baselineRigidbodyComponentsObserved',
        'baselineRenderTransformEnvelopeEquivalent',
        'baselineEquivalent',
        'phaseEquivalent',
        'inputEquivalent',
        'baselineNormalizedRigidbodyEquivalent',
        'rigidbodyAxisWitnessesAvailable',
        'rigidbodyAxisWitnessEquivalent',
        'gameplayEquivalent',
        'controlSurfaceClean',
        'scenarioCoverageClean',
        'mutationClean',
        'externalRngSynchronized',
        'externalRngSynchronizationOriginCaptured')
    if ([string]$verdict.verdict -ne 'PASS') {
        Add-Failure -Code 'run-verdict-not-pass' `
            -Detail "$context verdict=$($verdict.verdict)"
    }
    foreach ($name in $requiredTrue) {
        $property = $verdict.PSObject.Properties[$name]
        if ($null -eq $property -or -not [bool]$property.Value) {
            Add-Failure -Code 'run-required-gate-failed' `
                -Detail "$context gate=$name"
        }
    }
    if ($null -ne $verdict.firstUnacceptedDifferenceTick `
            -or -not [string]::IsNullOrEmpty(
                [string]$verdict.firstUnacceptedDifferenceKey)) {
        Add-Failure -Code 'run-has-unaccepted-difference' `
            -Detail "$context first=$($verdict.firstUnacceptedDifferenceTick)/$($verdict.firstUnacceptedDifferenceKey)"
    }
    if ([bool](Get-OptionalProperty `
            -Object $verdict `
            -Name 'visualRecognitionUsed' `
            -Default $true)) {
        Add-Failure -Code 'visual-recognition-used' -Detail $context
    }

    if ($Manual) {
        if ([string](Get-OptionalProperty `
                -Object $verdict `
                -Name 'controlSurface' `
                -Default '') -ne 'companion-ui') {
            Add-Failure -Code 'manual-control-surface-mismatch' `
                -Detail $context
        }
        $controlPath = Resolve-ExistingFile -Path (
            Join-Path $runRoot 'environment\manual-ui-control.json')
        $auditPath = Resolve-ExistingFile -Path (
            Join-Path `
                $runRoot `
                'environment\manual-ui-automation-audit.json')
        $control = Get-Content -LiteralPath $controlPath -Raw |
            ConvertFrom-Json
        $audit = Get-Content -LiteralPath $auditPath -Raw |
            ConvertFrom-Json
        if ($null -eq $control `
                -or [string]$control.verdict -ne 'PASS' `
                -or [bool]$control.visualRecognitionUsed `
                -or [bool]$control.aiWriteCommandsUsed `
                -or [int]$control.exactStepCount `
                    -ne [int]$Matrix.maxTicks) {
            Add-Failure -Code 'manual-control-audit-failed' `
                -Detail $context
        }
        if ($null -eq $audit `
                -or [string]$audit.verdict -ne 'PASS' `
                -or [int]$audit.stepCommandCount `
                    -ne [int]$Matrix.maxTicks `
                -or [int]$audit.externalWriteCommandCount -ne 0 `
                -or -not [bool]$audit.allResultsAccepted) {
            Add-Failure -Code 'manual-command-audit-failed' `
                -Detail $context
        }
    }
    elseif ([string](Get-OptionalProperty `
            -Object $verdict `
            -Name 'controlSurface' `
            -Default '') -ne 'automation-sdk') {
        Add-Failure -Code 'ai-control-surface-mismatch' -Detail $context
    }

    return [pscustomobject][ordered]@{
        mode = $mode
        attempt = [int]$Attempt.attempt
        runRoot = $runRoot
        verdictPath = $verdictPath
        verdictSha256 = $verdictSha
        tracePath = $tracePath
        traceSha256 = $traceSha
        phasePath = $phasePath
        referenceTraceSha256 = [string]$verdict.referenceTraceSha256
        referenceBaselineSignatureSha256 =
            [string]$verdict.referenceBaselineSignatureSha256
        selectedReferenceAttempt = [int]$verdict.selectedReferenceAttempt
        referenceBaselineMatchKind =
            [string]$verdict.referenceBaselineMatchKind
        frameCount = [int]$verdict.frameCount
        controlSurface = [string](Get-OptionalProperty `
            -Object $verdict `
            -Name 'controlSurface' `
            -Default '')
    }
}

$ManualMatrixPath = Resolve-ExistingFile -Path $ManualMatrixPath
$NegativeControlEnvelopePath = Resolve-ExistingFile `
    -Path $NegativeControlEnvelopePath
$AiMatrixPaths = @($AiMatrixPaths | ForEach-Object {
        Resolve-ExistingFile -Path $_
    })
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path `
        $repositoryRoot `
        ('artifacts\vanilla-equivalence\t24-human-ai-parity-' `
            + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') `
            + '\human-ai-parity.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $OutputPath.StartsWith(
        $repositoryPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Parity output must stay inside the repository.'
}

$envelope = Get-Content -LiteralPath $NegativeControlEnvelopePath -Raw |
    ConvertFrom-Json
$envelopeSha = Get-Sha256File -Path $NegativeControlEnvelopePath
if ([string]$envelope.verdict -ne 'ELIGIBLE') {
    Add-Failure -Code 'envelope-not-eligible' `
        -Detail ([string]$envelope.verdict)
}
$toleratedKeys = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$toleratedPolicy = [ordered]@{}
$toleratedFieldsProperty =
    $envelope.fieldPolicy.PSObject.Properties['toleratedFields']
if ($null -eq $toleratedFieldsProperty) {
    Add-Failure -Code 'missing-tolerated-fields-policy' `
        -Detail 'Envelope fieldPolicy.toleratedFields is required.'
    $toleratedFields = @()
}
else {
    $toleratedFields = @($toleratedFieldsProperty.Value)
}
if (-not [bool](Get-OptionalProperty `
        -Object $envelope.fieldPolicy `
        -Name 'exactByDefault' `
        -Default $false)) {
    Add-Failure -Code 'field-policy-not-exact-by-default' `
        -Detail 'Envelope must compare every undeclared field exactly.'
}
foreach ($field in $toleratedFields) {
    $key = [string](Get-OptionalProperty `
        -Object $field `
        -Name 'key' `
        -Default '')
    if ([string]::IsNullOrWhiteSpace($key)) {
        Add-Failure -Code 'invalid-tolerated-field-key' `
            -Detail 'A tolerated field has an empty key.'
        continue
    }
    if (-not $toleratedKeys.Add($key)) {
        Add-Failure -Code 'duplicate-tolerated-field' `
            -Detail $key
        continue
    }
    $boundProperty = $field.PSObject.Properties['maxAbsoluteDifference']
    if ($null -eq $boundProperty) {
        Add-Failure -Code 'missing-tolerated-field-bound' `
            -Detail $key
    }
    else {
        $boundValue = [double]$boundProperty.Value
        if ([double]::IsNaN($boundValue) `
                -or [double]::IsInfinity($boundValue) `
                -or $boundValue -lt 0.0) {
            Add-Failure -Code 'invalid-tolerated-field-bound' `
                -Detail "$key=$boundValue"
        }
    }
    $toleratedPolicy[$key] = $field
}

$manualMatrix = Get-Content -LiteralPath $ManualMatrixPath -Raw |
    ConvertFrom-Json
$aiMatrices = @($AiMatrixPaths | ForEach-Object {
    Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json
})
if ([string]$manualMatrix.verdict -ne 'PASS' `
        -or [string]$manualMatrix.mode -ne 'tas-manual-ui') {
    Add-Failure -Code 'manual-matrix-ineligible' `
        -Detail "verdict=$($manualMatrix.verdict) mode=$($manualMatrix.mode)"
}
foreach ($matrix in $aiMatrices) {
    if ([string]$matrix.verdict -ne 'PASS' `
            -or @('tas-sequential', 'tas-continuous', 'tas-batch') `
                -notcontains [string]$matrix.mode) {
        Add-Failure -Code 'ai-matrix-ineligible' `
            -Detail "verdict=$($matrix.verdict) mode=$($matrix.mode)"
    }
    foreach ($property in @(
            'maxTicks',
            'fixtureSlot',
            'movieSha256',
            'scenarioContractSha256',
            'negativeControlEnvelopeSha256')) {
        Assert-EqualValue -Code 'matrix-contract-mismatch' `
            -Expected $manualMatrix.$property `
            -Actual $matrix.$property `
            -Context ([string]$matrix.mode + ':' + $property)
    }
}
Assert-EqualValue -Code 'envelope-hash-mismatch' `
    -Expected ([string]$manualMatrix.negativeControlEnvelopeSha256) `
    -Actual $envelopeSha -Context 'manual matrix'

$manualRuns = @($manualMatrix.attempts | ForEach-Object {
    Read-RunRecord -Matrix $manualMatrix -Attempt $_ -Manual $true
})
$aiRuns = @()
foreach ($matrix in $aiMatrices) {
    $aiRuns += @($matrix.attempts | ForEach-Object {
        Read-RunRecord -Matrix $matrix -Attempt $_ -Manual $false
    })
}

if ($manualRuns.Count -lt $RequiredPairs) {
    Add-Failure -Code 'insufficient-manual-runs' `
        -Detail "required=$RequiredPairs actual=$($manualRuns.Count)"
}
if ($aiRuns.Count -lt $RequiredPairs) {
    Add-Failure -Code 'insufficient-ai-runs' `
        -Detail "required=$RequiredPairs actual=$($aiRuns.Count)"
}

$manualSelection = @($manualRuns | Select-Object -First $RequiredPairs)
$manualSelection = @($manualSelection | Sort-Object `
    @{ Expression = {
        $manual = $_
        @($aiRuns | Where-Object {
            $_.referenceTraceSha256 -eq $manual.referenceTraceSha256 `
                -and $_.referenceBaselineSignatureSha256 `
                    -eq $manual.referenceBaselineSignatureSha256
        }).Count
    } },
    attempt)
$usedAi = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$pairSeeds = [Collections.Generic.List[object]]::new()
foreach ($manual in $manualSelection) {
    $ai = $aiRuns |
        Where-Object {
            $key = $_.mode + ':' + $_.attempt
            -not $usedAi.Contains($key) `
                -and $_.referenceTraceSha256 `
                    -eq $manual.referenceTraceSha256 `
                -and $_.referenceBaselineSignatureSha256 `
                    -eq $manual.referenceBaselineSignatureSha256
        } |
        Sort-Object `
            @{ Expression = {
                if ($_.referenceBaselineMatchKind -eq 'exact') { 0 }
                else { 1 }
            } },
            mode,
            attempt |
        Select-Object -First 1
    if ($null -eq $ai) {
        Add-Failure -Code 'no-distinct-ai-baseline-pair' `
            -Detail (
                "manual attempt=$($manual.attempt) reference=" `
                + $manual.referenceBaselineSignatureSha256)
        continue
    }
    [void]$usedAi.Add($ai.mode + ':' + $ai.attempt)
    $pairSeeds.Add([pscustomobject]@{
        manual = $manual
        ai = $ai
    }) | Out-Null
}

$traceCache = @{}
$phaseCache = @{}
function Get-CachedTraceSummary([string]$Path) {
    if (-not $script:traceCache.ContainsKey($Path)) {
        $script:traceCache[$Path] = Get-TraceSummary `
            -Path $Path `
            -ToleratedKeys $script:toleratedKeys
    }
    return $script:traceCache[$Path]
}
function Get-CachedPhaseSummary([string]$Path) {
    if (-not $script:phaseCache.ContainsKey($Path)) {
        $script:phaseCache[$Path] = Get-PhaseSummary -Path $Path
    }
    return $script:phaseCache[$Path]
}

$pairs = [Collections.Generic.List[object]]::new()
$pairIndex = 0
foreach ($seed in $pairSeeds) {
    $pairIndex++
    $manualTrace = Get-CachedTraceSummary -Path $seed.manual.tracePath
    $aiTrace = Get-CachedTraceSummary -Path $seed.ai.tracePath
    $manualPhase = Get-CachedPhaseSummary -Path $seed.manual.phasePath
    $aiPhase = Get-CachedPhaseSummary -Path $seed.ai.phasePath
    $pairFailures = [Collections.Generic.List[object]]::new()

    if ($manualTrace.inputSha256 -ne $aiTrace.inputSha256) {
        $pairFailures.Add([ordered]@{
            code = 'canonical-input-ledger-mismatch'
            firstTick = Get-FirstHashDifference `
                -Left $manualTrace.inputFrameHashes `
                -Right $aiTrace.inputFrameHashes
        }) | Out-Null
    }
    if ($manualTrace.authoritativeGameplaySha256 `
            -ne $aiTrace.authoritativeGameplaySha256) {
        $pairFailures.Add([ordered]@{
            code = 'authoritative-gameplay-mismatch'
            firstTick = Get-FirstHashDifference `
                -Left $manualTrace.authoritativeFrameHashes `
                -Right $aiTrace.authoritativeFrameHashes
        }) | Out-Null
    }
    if ($manualPhase.canonicalSha256 -ne $aiPhase.canonicalSha256) {
        $pairFailures.Add([ordered]@{
            code = 'canonical-input-phase-mismatch'
            firstEvent = Get-FirstHashDifference `
                -Left $manualPhase.eventHashes `
                -Right $aiPhase.eventHashes
        }) | Out-Null
    }

    $toleratedComparisons = @()
    foreach ($key in $toleratedKeys) {
        $left = $manualTrace.toleratedValues[$key]
        $right = $aiTrace.toleratedValues[$key]
        $bound = $toleratedPolicy[$key]
        $differenceCount = 0
        $maxAbsolute = 0.0
        $firstDifferenceTick = $null
        $withinBound = $left.Count -eq $right.Count
        $limit = [Math]::Min($left.Count, $right.Count)
        for ($index = 0; $index -lt $limit; $index++) {
            if ([string]$left[$index] -eq [string]$right[$index]) {
                continue
            }
            $differenceCount++
            if ($null -eq $firstDifferenceTick) {
                $firstDifferenceTick = $index
            }
            $leftFloat = Convert-Float32Hex -Hex ([string]$left[$index])
            $rightFloat = Convert-Float32Hex -Hex ([string]$right[$index])
            $absolute = [Math]::Abs(
                [double]$leftFloat - [double]$rightFloat)
            if ($absolute -gt $maxAbsolute) {
                $maxAbsolute = $absolute
            }
            if ([single]::IsNaN($leftFloat) `
                    -or [single]::IsNaN($rightFloat) `
                    -or [single]::IsInfinity($leftFloat) `
                    -or [single]::IsInfinity($rightFloat) `
                    -or $absolute `
                        -gt [double]$bound.maxAbsoluteDifference) {
                $withinBound = $false
            }
        }
        if (-not $withinBound) {
            $pairFailures.Add([ordered]@{
                code = 'no-mod-envelope-bound-exceeded'
                key = $key
                firstDifferenceTick = $firstDifferenceTick
                maxAbsoluteDifference = $maxAbsolute
                allowedMaxAbsoluteDifference =
                    [double]$bound.maxAbsoluteDifference
            }) | Out-Null
        }
        $toleratedComparisons += [ordered]@{
            key = $key
            frameCount = $left.Count
            differenceCount = $differenceCount
            firstDifferenceTick = $firstDifferenceTick
            maxAbsoluteDifference = $maxAbsolute
            allowedMaxAbsoluteDifference =
                [double]$bound.maxAbsoluteDifference
            withinFrozenNoModBound = $withinBound
        }
    }

    foreach ($failure in $pairFailures) {
        Add-Failure -Code ([string]$failure.code) `
            -Detail "pair=$pairIndex" -Context $failure
    }
    $pairs.Add([ordered]@{
        pair = $pairIndex
        verdict = if ($pairFailures.Count -eq 0) { 'PASS' } else { 'FAIL' }
        referenceTraceSha256 = $seed.manual.referenceTraceSha256
        referenceBaselineSignatureSha256 =
            $seed.manual.referenceBaselineSignatureSha256
        manual = [ordered]@{
            mode = $seed.manual.mode
            attempt = $seed.manual.attempt
            verdictSha256 = $seed.manual.verdictSha256
            traceSha256 = $seed.manual.traceSha256
            selectedReferenceAttempt =
                $seed.manual.selectedReferenceAttempt
            referenceBaselineMatchKind =
                $seed.manual.referenceBaselineMatchKind
            controlSurface = $seed.manual.controlSurface
        }
        ai = [ordered]@{
            mode = $seed.ai.mode
            attempt = $seed.ai.attempt
            verdictSha256 = $seed.ai.verdictSha256
            traceSha256 = $seed.ai.traceSha256
            selectedReferenceAttempt = $seed.ai.selectedReferenceAttempt
            referenceBaselineMatchKind =
                $seed.ai.referenceBaselineMatchKind
            controlSurface = $seed.ai.controlSurface
        }
        frameCount = $manualTrace.frameCount
        canonicalInputLedgerSha256 = $manualTrace.inputSha256
        authoritativeGameplaySha256 =
            $manualTrace.authoritativeGameplaySha256
        canonicalInputPhaseSha256 = $manualPhase.canonicalSha256
        canonicalInputPhaseEventCount = $manualPhase.comparedEventCount
        toleratedFieldComparisons = $toleratedComparisons
        failures = $pairFailures
    }) | Out-Null
}

if ($pairs.Count -ne $RequiredPairs) {
    Add-Failure -Code 'pair-count-mismatch' `
        -Detail "required=$RequiredPairs actual=$($pairs.Count)"
}
$verdict = if ($failures.Count -eq 0 `
        -and $pairs.Count -eq $RequiredPairs) { 'PASS' }
else { 'FAIL' }
$report = [ordered]@{
    schemaVersion = 1
    testId = 't24-human-ai-authoritative-parity-v1'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    verdict = $verdict
    requiredPairs = $RequiredPairs
    completedPairs = $pairs.Count
    pairingRule =
        'distinct-runs-with-identical-no-mod-reference-trace-and-authoritative-baseline-signature'
    comparisonRule = [ordered]@{
        canonicalInputLedger = 'bitwise-exact-every-logical-tick'
        authoritativeGameplay =
            'bitwise-exact-for-all-comparable-fields-except-frozen-no-mod-tolerated-fields'
        canonicalInputPhase =
            'exact-after-first-gameplay-input-with-relative-runtime-counters'
        toleratedFields =
            'direct-Human-vs-AI-absolute-difference-within-frozen-no-mod-pairwise-maximum'
    }
    verifierScript = $PSCommandPath
    verifierScriptSha256 = Get-Sha256File -Path $PSCommandPath
    manualMatrix = [ordered]@{
        path = $ManualMatrixPath
        sha256 = Get-Sha256File -Path $ManualMatrixPath
    }
    aiMatrices = @($AiMatrixPaths | ForEach-Object {
        [ordered]@{
            path = $_
            sha256 = Get-Sha256File -Path $_
        }
    })
    negativeControlEnvelope = [ordered]@{
        path = $NegativeControlEnvelopePath
        sha256 = $envelopeSha
        policyId = [string]$envelope.policyId
        verdict = [string]$envelope.verdict
        toleratedKeys = @($toleratedKeys | Sort-Object)
    }
    commonContract = [ordered]@{
        movieSha256 = [string]$manualMatrix.movieSha256
        scenarioContractSha256 =
            [string]$manualMatrix.scenarioContractSha256
        maxTicks = [int]$manualMatrix.maxTicks
        fixtureSlot = [int]$manualMatrix.fixtureSlot
        samplingBoundary =
            [string]$envelope.compatibility.samplingBoundary
        randomSynchronizationPolicy =
            [string]$envelope.compatibility.externalClockContract.randomSynchronizationPolicy
        randomSynchronizationSeed =
            [long]$envelope.compatibility.externalClockContract.randomSynchronizationSeed
    }
    pairs = $pairs
    failures = $failures
}

$outputDirectory = Split-Path -Parent $OutputPath
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[IO.File]::WriteAllText(
    $OutputPath,
    ($report | ConvertTo-Json -Depth 20) + "`n",
    $utf8NoBom)
Write-Output (
    "T24 Human/AI parity verdict=$verdict pairs=$($pairs.Count)/$RequiredPairs")
Write-Output "Report: $OutputPath"
if ($verdict -ne 'PASS' -and -not $NoThrowOnFailure) {
    throw "T24 Human/AI parity failed. See $OutputPath"
}
$report
