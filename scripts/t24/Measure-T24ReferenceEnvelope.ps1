[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MatrixRoot,

    [string]$OutputPath = '',

    [ValidateRange(10, 100)]
    [int]$MinimumSuccessfulRuns = 10,

    [ValidateRange(5, 100)]
    [int]$MinimumStrictBaselineGroupRuns = 5,

    [ValidateRange(60, 2000)]
    [int]$MinimumCommonInputPrefixFrames = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'T24ClockHarness.ps1')

function Test-T24RecordingPhaseContract {
    param(
        [Parameter(Mandatory)][object]$Telemetry,
        [Parameter(Mandatory)][bool]$RootRequestApplied
    )

    $armFrameCount = [int]$Telemetry.recordingArmBoundaryFrameCount
    $remainingNormalFrames =
        [int]$Telemetry.externalRecordingPhaseNormalizationRemainingNormalFrameCount
    $restoreFramePhase =
        [int]$Telemetry.externalRecordingPhaseNormalizationRestoreFramePhase
    $expectedRestoreFramePhase =
        ((-$remainingNormalFrames % 4) + 4) % 4
    $rootRequestValid = if ($RootRequestApplied) {
        [int]$Telemetry.externalRecordingRootRequestObservedFrameCount `
            -eq ($armFrameCount + 1) `
        -and [int]$Telemetry.externalRecordingRootFramePhase -eq 0
    }
    else {
        [int]$Telemetry.externalRecordingRootRequestObservedFrameCount -eq -1 `
        -and [int]$Telemetry.externalRecordingRootFramePhase -eq -1
    }

    return [string]$Telemetry.recordingAbsoluteTimeTarget.canonicalHex `
            -eq '44400000' `
        -and [bool]$Telemetry.recordingArmBoundaryReached `
        -and $armFrameCount -gt 0 `
        -and [string]$Telemetry.recordingArmBoundaryTimeRaw.canonicalHex `
            -eq '44400000' `
        -and [string]$Telemetry.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -eq '44400000' `
        -and [int]$Telemetry.recordingFramePhaseModulo -eq 4 `
        -and [int]$Telemetry.recordingFramePhaseTarget -eq 0 `
        -and [int]$Telemetry.recordingArmBoundaryFramePhase -eq 0 `
        -and $rootRequestValid `
        -and -not [bool]$Telemetry.externalRecordingPhaseNormalizationActive `
        -and [bool]$Telemetry.externalRecordingPhaseNormalizationCompleted `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationBeginCount -eq 1 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationHoldFrameCount -ge 1 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationHoldFrameCount -le 8 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationReleaseCount -eq 1 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationLastFrameCount -gt 0 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationLastFramePhase `
            -eq $restoreFramePhase `
        -and [string]$Telemetry.externalRecordingPhaseNormalizationHeldTimeBits `
            -ne '00000000' `
        -and [string]$Telemetry.externalRecordingPhaseNormalizationHeldTimeDoubleBits `
            -ne '0000000000000000' `
        -and $remainingNormalFrames -ge 1 `
        -and $remainingNormalFrames -le 16 `
        -and $restoreFramePhase -eq $expectedRestoreFramePhase `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationFaultCode -eq 0
}

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 reference-envelope measurement requires PowerShell 7 or newer.'
}

$MatrixRoot = [IO.Path]::GetFullPath($MatrixRoot)
$matrixPath = Join-Path $MatrixRoot 'run-matrix.json'
if (-not (Test-Path -LiteralPath $matrixPath -PathType Leaf)) {
    throw "T24 run matrix is missing: $matrixPath"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $MatrixRoot 'negative-control-envelope.json'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    throw "T24 reference envelope already exists: $OutputPath"
}

$toleranceCandidateKeys = @(
    'hero.position.x',
    'hero.position.y',
    'hero.positionDelta.x',
    'hero.positionDelta.y',
    'time.timeMinusFixed'
)
$inputKeys = @(
    'input.axisX',
    'input.axisY',
    'input.held',
    'input.pressed',
    'input.released'
)
$toleranceCandidateKeySet =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($toleranceCandidateKey in $toleranceCandidateKeys) {
    [void]$toleranceCandidateKeySet.Add($toleranceCandidateKey)
}
$baselineExcludedKeys = @(
    'runId',
    'mode',
    'visualTick',
    'fixedTick'
)

# Keep validated traces compact. A 1200-frame trace contains roughly two
# hundred fields per frame; retaining every ConvertFrom-Json field hashtable
# multiplies a 50-run matrix into millions of heavyweight PowerShell objects.
# Schemas and canonical encodings are immutable, so intern both and retain only
# integer value IDs per frame. The comparison below remains pairwise and exact.
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

function Get-OrAddCanonicalValueId {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$CanonicalHex
    )

    $valueId = 0
    if ($script:canonicalValueIds.TryGetValue(
            $CanonicalHex,
            [ref]$valueId)) {
        return $valueId
    }
    $valueId = $script:canonicalValues.Count
    $script:canonicalValues.Add($CanonicalHex)
    $script:canonicalValueIds.Add($CanonicalHex, $valueId)
    return $valueId
}

function Get-OrAddAuthoritativeFrameId {
    param([Parameter(Mandatory)][string]$Canonical)

    $sha256 = Get-Sha256Text -Text $Canonical
    $frameId = 0
    if ($script:authoritativeFrameIds.TryGetValue(
            $sha256,
            [ref]$frameId)) {
        if ($script:authoritativeFrameCanonical[$frameId] -cne $Canonical) {
            throw 'SHA-256 collision while interning authoritative trace fields.'
        }
        return $frameId
    }
    $frameId = $script:authoritativeFrameCanonical.Count
    $script:authoritativeFrameCanonical.Add($Canonical)
    $script:authoritativeFrameIds.Add($sha256, $frameId)
    return $frameId
}

function Get-OrAddTraceSchema {
    param(
        [Parameter(Mandatory)][string[]]$Keys,
        [Parameter(Mandatory)][string[]]$Kinds,
        [Parameter(Mandatory)][bool[]]$Comparable
    )

    $builder = [Text.StringBuilder]::new()
    for ($index = 0; $index -lt $Keys.Count; $index++) {
        [void]$builder.Append($Keys[$index])
        [void]$builder.Append([char]0)
        [void]$builder.Append($Kinds[$index])
        [void]$builder.Append([char]0)
        [void]$builder.Append($(if ($Comparable[$index]) { '1' } else { '0' }))
        [void]$builder.Append("`n")
    }
    $signature = $builder.ToString()
    $schemaId = 0
    if ($script:traceSchemaIds.TryGetValue($signature, [ref]$schemaId)) {
        return $schemaId
    }

    $keyIndex = [Collections.Generic.Dictionary[string, int]]::new(
        [StringComparer]::Ordinal)
    for ($index = 0; $index -lt $Keys.Count; $index++) {
        $keyIndex.Add($Keys[$index], $index)
    }
    $schemaId = $script:traceSchemas.Count
    $script:traceSchemas.Add([pscustomobject][ordered]@{
        keys = $Keys
        kinds = $Kinds
        comparable = $Comparable
        keyIndex = $keyIndex
    })
    $script:traceSchemaIds.Add($signature, $schemaId)
    return $schemaId
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)
    ).ToLowerInvariant()
}

function Assert-T24BaselineAbsoluteTimeContract {
    param([Parameter(Mandatory)][string]$Path)

    $baseline = Get-Content -LiteralPath $Path -Raw |
        ConvertFrom-Json -AsHashtable
    if ([int]$baseline['schemaVersion'] -ne 2) {
        throw 'T24 baseline must use absolute-time schema version 2.'
    }
    foreach ($key in @('timeRaw', 'fixedTimeRaw')) {
        if (-not $baseline.Contains($key) `
                -or $baseline[$key] -isnot [Collections.IDictionary] `
                -or -not $baseline[$key].Contains('value') `
                -or -not $baseline[$key].Contains('canonicalHex') `
                -or [string]$baseline[$key]['canonicalHex'] `
                    -cnotmatch '\A[0-9a-f]{8}\z') {
            throw "T24 baseline absolute-time field is malformed: $key"
        }
    }
    return $baseline
}

function Get-BaselineSignature {
    param([Parameter(Mandatory)][string]$Path)

    $baseline = Assert-T24BaselineAbsoluteTimeContract -Path $Path
    $normalized = [ordered]@{}
    foreach ($key in @($baseline.Keys | Sort-Object)) {
        if ($baselineExcludedKeys -contains $key) {
            continue
        }
        $value = $baseline[$key]
        if ($value -is [Collections.IDictionary] `
                -and $value.Contains('canonicalHex')) {
            $normalized[[string]$key] = [ordered]@{
                canonicalHex = [string]$value['canonicalHex']
            }
        }
        else {
            $normalized[[string]$key] = $value
        }
    }
    $json = $normalized | ConvertTo-Json -Compress -Depth 20
    return [ordered]@{
        sha256 = Get-Sha256Text -Text $json
        normalizedJson = $json
    }
}

function Get-AuthoritativeBaselineSignature {
    param([Parameter(Mandatory)][string]$Path)

    $excluded = @(
        'runId',
        'mode',
        'visualTick',
        'fixedTick',
        'heroPositionX',
        'heroPositionY'
    )
    $baseline = Assert-T24BaselineAbsoluteTimeContract -Path $Path
    $normalized = [ordered]@{}
    foreach ($key in @($baseline.Keys | Sort-Object)) {
        if ($excluded -contains $key) {
            continue
        }
        $value = $baseline[$key]
        if ($value -is [Collections.IDictionary] `
                -and $value.Contains('canonicalHex')) {
            $normalized[[string]$key] = [ordered]@{
                canonicalHex = [string]$value['canonicalHex']
            }
        }
        else {
            $normalized[[string]$key] = $value
        }
    }
    $json = $normalized | ConvertTo-Json -Compress -Depth 20
    return [ordered]@{
        sha256 = Get-Sha256Text -Text $json
        normalizedJson = $json
    }
}

function Get-AssemblyFingerprint {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$SamplingOnly
    )

    $assemblies = @(Get-Content -LiteralPath $Path -Raw |
        ConvertFrom-Json)
    if ($SamplingOnly) {
        $assemblies = @(
            $assemblies |
                Where-Object {
                    [IO.Path]::GetFileName([string]$_.file) -in @(
                        'HollowKnightTAS.Core.dll',
                        'HollowKnightTAS.GameObservation.dll')
                })
        if ($assemblies.Count -ne 2) {
            throw 'Observer audit does not contain both sampling assemblies.'
        }
    }
    $normalized = @(
        $assemblies |
            Sort-Object file |
            ForEach-Object {
                [ordered]@{
                    file = [string]$_.file
                    length = [long]$_.length
                    sha256 = [string]$_.sha256
                }
            }
    )
    return Get-Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 10)
}

function Get-ClockFingerprint {
    param([Parameter(Mandatory)][string]$Path)

    $clock = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $normalized = [ordered]@{
        capabilityId = [string]$clock.capabilityId
        profile = [string]$clock.profile
        bridgeAbi = [int]$clock.bridgeAbi
        startupPolicy = [string]$clock.startupPolicy
        bundleManifestSha256 = [string]$clock.bundleManifestSha256
        injectorSha256 = [string]$clock.injectorSha256
        injectorManagedSha256 = [string]$clock.injectorManagedSha256
        injectorDepsSha256 = [string]$clock.injectorDepsSha256
        injectorRuntimeConfigSha256 =
            [string]$clock.injectorRuntimeConfigSha256
        runtimeFileSetSha256 = [string]$clock.runtimeFileSetSha256
        bridgeSha256 = [string]$clock.bridgeSha256
        payloadSha256 = [string]$clock.payloadSha256
        processImageSha256 = [string]$clock.processImageSha256
        randomSynchronizationPolicy =
            [string]$clock.randomSynchronizationPolicy
        randomSynchronizationSeed =
            [int]$clock.randomSynchronizationSeed
        stderrEmpty = [bool]$clock.stderrEmpty
    }
    return [ordered]@{
        sha256 = Get-Sha256Text -Text (
            $normalized | ConvertTo-Json -Compress)
        normalized = $normalized
    }
}

function Get-FieldMap {
    param([Parameter(Mandatory)]$Frame)

    $map = [ordered]@{}
    foreach ($field in $Frame.fields) {
        $map[[string]$field.key] = $field
    }
    return $map
}

function Read-T24ValidatedTrace {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath ($Path + '.incomplete')) { throw "Incomplete reference cannot define an envelope: $Path" }
    $frames = [Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($line in [IO.File]::ReadLines($Path)) {
        try {
            $frame = $line |
                ConvertFrom-Json -AsHashtable -Depth 50
        }
        catch {
            throw "Trace line $($index + 1) is not valid JSON: $($_.Exception.Message)"
        }

        if ($frame -isnot [Collections.IDictionary]) {
            throw "Trace line $($index + 1) must be a JSON object."
        }
        foreach ($requiredProperty in @(
                'schemaVersion',
                'sequence',
                'logicalTick',
                'comparisonSha256',
                'fields')) {
            if (-not $frame.Contains($requiredProperty)) {
                throw "Trace line $($index + 1) lacks '$requiredProperty'."
            }
        }
        if ([int]$frame['schemaVersion'] -ne 1) {
            throw "Trace line $($index + 1) has an unsupported schema version."
        }
        if ([long]$frame['sequence'] -ne [long]($index + 1)) {
            throw "Trace line $($index + 1) has a non-contiguous sequence."
        }
        if ([long]$frame['logicalTick'] -ne [long]$index) {
            throw "Trace line $($index + 1) has a non-contiguous logical tick."
        }
        if ([string]$frame['comparisonSha256'] `
                -cnotmatch '\A[0-9a-f]{64}\z') {
            throw "Trace line $($index + 1) has an invalid comparison SHA-256."
        }

        $fieldMap = [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::Ordinal)
        $sourceKeys = [Collections.Generic.List[string]]::new()
        foreach ($field in @($frame['fields'])) {
            if ($field -isnot [Collections.IDictionary]) {
                throw "Trace line $($index + 1) contains a non-object field."
            }
            foreach ($requiredProperty in @(
                    'key', 'kind', 'canonicalHex', 'comparable')) {
                if (-not $field.Contains($requiredProperty)) {
                    throw "Trace line $($index + 1) contains an incomplete field."
                }
            }
            $key = [string]$field['key']
            if ([string]::IsNullOrEmpty($key) `
                    -or [string]::IsNullOrEmpty([string]$field['kind']) `
                    -or [string]$field['canonicalHex'] `
                        -cnotmatch '\A(?:[0-9a-f]{2})*\z' `
                    -or $field['comparable'] -isnot [bool]) {
                throw "Trace line $($index + 1) contains an invalid field encoding."
            }
            if (-not $fieldMap.TryAdd($key, $field)) {
                throw "Trace line $($index + 1) contains duplicate field '$key'."
            }
            $sourceKeys.Add($key)
        }
        foreach ($absoluteTimeKey in @('time.raw', 'time.fixedRaw')) {
            if (-not $fieldMap.ContainsKey($absoluteTimeKey) `
                    -or [string]$fieldMap[$absoluteTimeKey]['kind'] `
                        -ne 'Float32Bits' `
                    -or -not [bool]$fieldMap[$absoluteTimeKey]['comparable'] `
                    -or [string]$fieldMap[$absoluteTimeKey]['canonicalHex'] `
                        -cnotmatch '\A[0-9a-f]{8}\z') {
                throw "Trace line $($index + 1) lacks comparable absolute-time field '$absoluteTimeKey'."
            }
        }

        $sortedKeys = [string[]]@($fieldMap.Keys)
        [Array]::Sort($sortedKeys, [StringComparer]::Ordinal)
        for ($fieldIndex = 0; `
                $fieldIndex -lt $sourceKeys.Count; `
                $fieldIndex++) {
            if ($sourceKeys[$fieldIndex] -cne $sortedKeys[$fieldIndex]) {
                throw "Trace line $($index + 1) fields are not in ordinal key order."
            }
        }
        $canonicalFields = @(
            foreach ($key in $sortedKeys) {
                $field = $fieldMap[$key]
                if (-not [bool]$field['comparable']) {
                    continue
                }
                [ordered]@{
                    key = $key
                    kind = [string]$field['kind']
                    canonicalHex = [string]$field['canonicalHex']
                }
            })
        $authoritativeBuilder = [Text.StringBuilder]::new()
        foreach ($field in $canonicalFields) {
            if ($toleranceCandidateKeySet.Contains([string]$field.key)) {
                continue
            }
            foreach ($part in @(
                    [string]$field.key,
                    [string]$field.kind,
                    [string]$field.canonicalHex)) {
                [void]$authoritativeBuilder.Append($part.Length)
                [void]$authoritativeBuilder.Append(':')
                [void]$authoritativeBuilder.Append($part)
            }
            [void]$authoritativeBuilder.Append(';')
        }
        $canonical = [ordered]@{
            logicalTick = [long]$frame['logicalTick']
            fields = $canonicalFields
        } | ConvertTo-Json -Compress -Depth 10
        $computedHash = Get-Sha256Text -Text $canonical
        if ($computedHash -cne [string]$frame['comparisonSha256']) {
            throw "Trace line $($index + 1) comparison SHA-256 does not match its fields."
        }
        $authoritativeCanonical = $authoritativeBuilder.ToString()
        $authoritativeFrameId = Get-OrAddAuthoritativeFrameId `
            -Canonical $authoritativeCanonical

        $kinds = [string[]]::new($sortedKeys.Count)
        $comparable = [bool[]]::new($sortedKeys.Count)
        $valueIds = [int[]]::new($sortedKeys.Count)
        for ($fieldIndex = 0; $fieldIndex -lt $sortedKeys.Count; $fieldIndex++) {
            $field = $fieldMap[$sortedKeys[$fieldIndex]]
            $kinds[$fieldIndex] = [string]$field['kind']
            $comparable[$fieldIndex] = [bool]$field['comparable']
            $valueIds[$fieldIndex] = Get-OrAddCanonicalValueId `
                -CanonicalHex ([string]$field['canonicalHex'])
        }
        $schemaId = Get-OrAddTraceSchema `
            -Keys $sortedKeys `
            -Kinds $kinds `
            -Comparable $comparable
        $frames.Add([pscustomobject][ordered]@{
            logicalTick = [long]$frame['logicalTick']
            schemaId = $schemaId
            valueIds = $valueIds
            authoritativeFrameId = $authoritativeFrameId
        })
        $index++
    }
    return @($frames)
}

function Get-CommonInputPrefix {
    param(
        [Parameter(Mandatory)][object[]]$LeftFrames,
        [Parameter(Mandatory)][object[]]$RightFrames
    )

    $limit = [Math]::Min($LeftFrames.Count, $RightFrames.Count)
    for ($index = 0; $index -lt $limit; $index++) {
        $leftFrame = $LeftFrames[$index]
        $rightFrame = $RightFrames[$index]
        $leftSchema = $script:traceSchemas[[int]$leftFrame.schemaId]
        $rightSchema = $script:traceSchemas[[int]$rightFrame.schemaId]
        foreach ($key in $inputKeys) {
            $leftFieldIndex = 0
            $rightFieldIndex = 0
            if (-not $leftSchema.keyIndex.TryGetValue(
                        $key,
                        [ref]$leftFieldIndex) `
                    -or -not $rightSchema.keyIndex.TryGetValue(
                        $key,
                        [ref]$rightFieldIndex) `
                    -or [string]$leftSchema.kinds[$leftFieldIndex] `
                        -ne [string]$rightSchema.kinds[$rightFieldIndex] `
                    -or [int]$leftFrame.valueIds[$leftFieldIndex] `
                        -ne [int]$rightFrame.valueIds[$rightFieldIndex]) {
                return $index
            }
        }
    }
    return $limit
}

function Get-CanonicalPhaseTimeline {
    param([Parameter(Mandatory)][string]$Path)

    $lines = @([IO.File]::ReadAllLines($Path))
    $gameplayStart = -1
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $observation = $lines[$lineIndex] | ConvertFrom-Json
        if ([string]$observation.phase -ne 'PlayerActionSet.after' `
                -or -not (
                    [bool]$observation.held `
                    -or [bool]$observation.pressed `
                    -or [bool]$observation.released)) {
            continue
        }
        for ($beforeIndex = $lineIndex - 1; `
                $beforeIndex -ge 0; `
                $beforeIndex--) {
            $before = $lines[$beforeIndex] | ConvertFrom-Json
            if ([long]$before.visualTick `
                    -ne [long]$observation.visualTick) {
                break
            }
            if ([string]$before.phase -eq 'PlayerActionSet.before') {
                $gameplayStart = $beforeIndex
                break
            }
        }
        if ($gameplayStart -lt 0) {
            $gameplayStart = $lineIndex
        }
        break
    }
    if ($gameplayStart -lt 0) {
        return [pscustomobject][ordered]@{
            valid = $false
            failure = 'gameplay-phase-start-missing'
            rawEventCount = $lines.Count
            events = @()
        }
    }

    $trimmed = @($lines[$gameplayStart..($lines.Count - 1)])
    $commitIndex = -1
    for ($lineIndex = 0; $lineIndex -lt $trimmed.Count; $lineIndex++) {
        $observation = $trimmed[$lineIndex] | ConvertFrom-Json
        if ([string]$observation.phase -eq 'PlayerActionSet.after') {
            $commitIndex = $lineIndex
            break
        }
    }
    if ($commitIndex -lt 0) {
        return [pscustomobject][ordered]@{
            valid = $false
            failure = 'action-commit-missing'
            rawEventCount = $lines.Count
            events = @()
        }
    }

    $commit = $trimmed[$commitIndex] | ConvertFrom-Json
    $first = $trimmed[0] | ConvertFrom-Json
    $events = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $trimmed.Count; $index++) {
        $observation = $trimmed[$index] | ConvertFrom-Json
        $canonical = [ordered]@{
            phase = [string]$observation.phase
            held = [bool]$observation.held
            pressed = [bool]$observation.pressed
            released = [bool]$observation.released
            gameObject = [string]$observation.gameObject
            fsm = [string]$observation.fsm
            state = [string]$observation.state
            visualTickRelative = [long]$observation.visualTick `
                - [long]$first.visualTick
            updateTickRelative = [long]$observation.updateTick `
                - [long]$first.updateTick
            actionSetTickRelative = if ($index -lt $commitIndex) {
                -1L
            }
            else {
                [long]$observation.actionSetTick `
                    - [long]$commit.actionSetTick
            }
            actionTickRelative = if ($index -lt $commitIndex) {
                -1L
            }
            else {
                [long]$observation.actionTick `
                    - [long]$commit.actionTick
            }
        }
        $events.Add(($canonical | ConvertTo-Json -Compress))
    }
    return [pscustomobject][ordered]@{
        valid = $true
        failure = ''
        rawEventCount = $lines.Count
        events = @($events)
    }
}

function Convert-Float32Hex {
    param([Parameter(Mandatory)][string]$Hex)

    if ($Hex -notmatch '^[0-9a-fA-F]{8}$') {
        throw "Invalid Float32 canonical hex: $Hex"
    }
    $bits = [Convert]::ToUInt32($Hex, 16)
    return [ordered]@{
        bits = [uint64]$bits
        value = [BitConverter]::UInt32BitsToSingle($bits)
    }
}

function Get-Float32UlpDistance {
    param(
        [Parameter(Mandatory)][uint64]$LeftBits,
        [Parameter(Mandatory)][uint64]$RightBits
    )

    function Get-OrderedKey([uint64]$Bits) {
        if (($Bits -band 0x80000000L) -ne 0) {
            return [uint64](0xffffffffL - $Bits)
        }
        return [uint64](0x80000000L + $Bits)
    }
    $leftKey = Get-OrderedKey -Bits $LeftBits
    $rightKey = Get-OrderedKey -Bits $RightBits
    if ($leftKey -ge $rightKey) {
        return [uint64]($leftKey - $rightKey)
    }
    return [uint64]($rightKey - $leftKey)
}

$violations = [Collections.Generic.List[object]]::new()
$violationCount = 0
function Add-Violation {
    param(
        [Parameter(Mandatory)][string]$Category,
        [Parameter(Mandatory)][string]$Detail,
        [hashtable]$Data = @{}
    )

    $script:violationCount++
    if ($violations.Count -ge 200) {
        return
    }
    $entry = [ordered]@{
        category = $Category
        detail = $Detail
    }
    foreach ($key in @($Data.Keys | Sort-Object)) {
        $entry[[string]$key] = $Data[$key]
    }
    $violations.Add($entry)
}

$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
if ([int]$matrix.schemaVersion -ne 2) {
    Add-Violation `
        -Category 'matrix-contract' `
        -Detail 'The source matrix schema is not the hash-complete v2 contract.'
}
$matrixGeneratorPath = [IO.Path]::GetFullPath(
    [string]$matrix.matrixGeneratorScript)
$captureScriptPath = [IO.Path]::GetFullPath([string]$matrix.captureScript)
$clockHarnessPath = Join-Path `
    (Split-Path -Parent $captureScriptPath) `
    'T24ClockHarness.ps1'
foreach ($scriptContract in @(
        [pscustomobject]@{
            path = $matrixGeneratorPath
            sha256 = [string]$matrix.matrixGeneratorScriptSha256
            name = 'matrix generator'
        },
        [pscustomobject]@{
            path = $captureScriptPath
            sha256 = [string]$matrix.captureScriptSha256
            name = 'reference capture script'
        })) {
    if (-not (Test-Path `
            -LiteralPath $scriptContract.path `
            -PathType Leaf) `
            -or (Get-FileHash `
                -LiteralPath $scriptContract.path `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne $scriptContract.sha256) {
        Add-Violation `
            -Category 'matrix-contract' `
            -Detail (
                'The ' + $scriptContract.name + ' is missing or changed.')
    }
}
if ([string]$matrix.verdict -ne 'CAPTURED') {
    Add-Violation `
        -Category 'matrix-contract' `
        -Detail 'The source matrix verdict is not CAPTURED.'
}
if (-not [bool]$matrix.strictFirstAttemptCohort `
        -or [int]$matrix.attemptedRuns -ne [int]$matrix.requiredSuccessfulRuns `
        -or [int]$matrix.successfulRuns -ne [int]$matrix.requiredSuccessfulRuns) {
    Add-Violation `
        -Category 'matrix-contract' `
        -Detail 'The synchronized vanilla matrix is not a strict first-attempt cohort.'
}
$physicalInputTraceRaw = [string]$matrix.physicalInputTracePath
$physicalInputTracePath = if (
    [string]::IsNullOrWhiteSpace($physicalInputTraceRaw)) {
    ''
}
else {
    [IO.Path]::GetFullPath($physicalInputTraceRaw)
}
$physicalInputTraceSha256 = [string]$matrix.physicalInputTraceSha256
if ([string]::IsNullOrWhiteSpace($physicalInputTraceSha256) `
        -or -not (Test-Path `
            -LiteralPath $physicalInputTracePath `
            -PathType Leaf) `
        -or (Get-FileHash `
            -LiteralPath $physicalInputTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ne $physicalInputTraceSha256) {
    Add-Violation `
        -Category 'matrix-contract' `
        -Detail 'The synchronized physical-input source is missing or changed.'
}
if ([string]$matrix.mode -ne 'vanilla-reference') {
    Add-Violation `
        -Category 'matrix-contract' `
        -Detail 'Only vanilla-reference runs can establish a tolerance envelope.'
}
if ([int]$matrix.successfulRuns -lt $MinimumSuccessfulRuns) {
    Add-Violation `
        -Category 'matrix-contract' `
        -Detail 'The matrix has too few successful cold processes.' `
        -Data @{ actual = [int]$matrix.successfulRuns; required = $MinimumSuccessfulRuns }
}

$runs = [Collections.Generic.List[object]]::new()
foreach ($attempt in @($matrix.attempts)) {
    if (-not [bool]$attempt.success) {
        continue
    }
    $runRoot = [IO.Path]::GetFullPath([string]$attempt.evidenceRoot)
    $summaryPath = Join-Path $runRoot 'reference-smoke.json'
    $auditRoot = Join-Path $runRoot 'observer-audit'
    $assembliesPath = Join-Path $auditRoot 'assemblies.json'
    $clockPath = Join-Path $auditRoot 'clock-injection.json'
    $clockProfilePath = Join-Path $auditRoot 'clock-profile.json'
    $traceRoot = Join-Path $runRoot 'traces\vanilla-reference'
    $baselinePath = Join-Path $traceRoot 'baseline.json'
    $tracePath = Join-Path $traceRoot 'trace.jsonl'
    $phasePath = Join-Path $traceRoot 'input-phase.jsonl'
    $resultPath = Join-Path $traceRoot 'result.json'
    $requiredFiles = @(
        $summaryPath,
        $assembliesPath,
        $clockPath,
        $clockProfilePath,
        $baselinePath,
        $tracePath,
        $phasePath,
        $resultPath
    )
    $missing = @(
        $requiredFiles |
            Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }
    )
    if ($missing.Count -ne 0) {
        Add-Violation `
            -Category 'run-artifact' `
            -Detail 'A successful run is missing required evidence.' `
            -Data @{ attempt = [int]$attempt.attempt; missing = $missing }
        continue
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw |
        ConvertFrom-Json
    $result = Get-Content -LiteralPath $resultPath -Raw |
        ConvertFrom-Json
    if (-not (Test-T24NativeSceneLifecycleContract -Telemetry $result)) {
        Add-Violation -Category 'native-scene-lifecycle' `
            -Detail 'Reference evidence contains scene intervention or lacks its required telemetry.' `
            -Data @{ attempt = [int]$attempt.attempt }
        continue
    }
    $clockProfile = Get-Content -LiteralPath $clockProfilePath -Raw |
        ConvertFrom-Json
    $clockProfileFixedDeltaTime = [single]$clockProfile.fixedDeltaTime
    $clockProfileTimeMinusFixed = [single]$clockProfile.timeMinusFixed
    $clockProfilePhaseErrorLimit =
        [Math]::Abs([double]$clockProfileFixedDeltaTime) / 1000d
    $clockProfilePhaseErrorValid =
        -not [single]::IsNaN($clockProfileTimeMinusFixed) `
        -and -not [single]::IsInfinity($clockProfileTimeMinusFixed) `
        -and [Math]::Abs([double]$clockProfileTimeMinusFixed) `
            -le $clockProfilePhaseErrorLimit
    $runContractValid = [string]$summary.verdict `
            -eq 'REFERENCE_CLOCK_CONTROLLED_CAPTURED' `
        -and [string]$summary.mode -eq 'vanilla-reference' `
        -and [bool]$summary.tasRuntimeAbsent `
        -and [bool]$summary.observerOnlyManagedMod `
        -and -not [bool]$summary.tasControlStarted `
        -and [bool]$summary.externalClockPrototype `
        -and [string]::Equals(
            [IO.Path]::GetFullPath([string]$summary.clockHarnessScript),
            [IO.Path]::GetFullPath($clockHarnessPath),
            [StringComparison]::OrdinalIgnoreCase) `
        -and (Get-FileHash `
            -LiteralPath $clockHarnessPath `
            -Algorithm SHA256).Hash.ToLowerInvariant() `
            -eq [string]$summary.clockHarnessScriptSha256 `
        -and [string]$summary.samplingBoundary `
            -eq 'post-render-completed-frame-sampling-v1' `
        -and [string]$summary.clockCapabilityId `
            -eq 'native.clock-rng-pause.override.experimental.v31' `
        -and [string]$summary.clockProfile `
            -eq 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
        -and [bool]$summary.externalRngSynchronized `
        -and [bool]$summary.externalRngSynchronizationOriginCaptured `
        -and [string]$summary.externalRngSynchronizationPolicy `
            -eq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [int]$summary.externalRngSeed -eq 1212896321 `
        -and [uint32]$summary.externalClockBridgeAbi -eq 10 `
        -and [int]$summary.externalClockBridgeStatus -eq 2 `
        -and -not [bool]$summary.externalRuntimeVirtualClockRegistered `
        -and [int]$summary.externalVirtualClockPaused -eq 0 `
        -and [int]$summary.externalVirtualClockPauseCount -eq 0 `
        -and [int]$summary.externalVirtualClockResumePending -eq 0 `
        -and [int]$summary.externalVirtualClockResumeRequestCount -eq 0 `
        -and [int]$summary.externalVirtualClockResumeCount -eq 0 `
        -and [bool]$summary.externalDeterministicClockEnabled `
        -and [long]$summary.externalDeterministicClockFrequency -gt 0 `
        -and [long]$summary.externalDeterministicClockStepTicks -gt 0 `
        -and [long]$summary.externalDeterministicClockFrequency `
            % [long]$summary.externalDeterministicClockStepTicks -eq 0 `
        -and [long]$summary.externalDeterministicClockFrequency `
            / [long]$summary.externalDeterministicClockStepTicks -eq 50 `
        -and [long]$summary.externalDeterministicClockAnchor -gt 0 `
        -and [int]$summary.externalDeterministicClockFrameAdvanceCount -gt 0 `
        -and [int]$summary.externalDeterministicClockEnableFaultCode -eq 0 `
        -and [int]$summary.externalDeterministicClockAdvanceFaultCode -eq 0 `
        -and [bool]$summary.externalDoublePhaseCalibrationApplied `
        -and [int]$summary.externalDoublePhaseCalibrationAttempts -gt 0 `
        -and [int]$summary.externalDoublePhaseCalibrationFaultCode -eq 0 `
        -and [string]$summary.externalDoublePhaseFinalResidualBits `
            -eq '0000000000000000' `
        -and [string]$summary.externalDoublePhaseFinalResidual.canonicalHex `
            -eq '0000000000000000' `
        -and [string]$summary.externalDoublePhaseLastCorrectionBits `
            -ne '00000000' `
        -and [string]$summary.externalRealtimeEpochNormalizationPolicyId `
            -eq 'root-game-minus-rounded-startup-offset-qpc-grid-v3' `
        -and [bool]$summary.externalRealtimeEpochNormalizationApplied `
        -and [int]$summary.externalRealtimeEpochNormalizationCount -gt 0 `
        -and [string]$summary.externalRealtimeEpochNormalizationAfterBits `
            -eq [string]$summary.externalRealtimeEpochNormalizationTargetBits `
        -and [int]$summary.externalRealtimeEpochNormalizationFaultCode -eq 0 `
        -and [bool]$summary.externalStartupClockHookInstalled `
        -and [bool]$summary.externalStartupClockLatchEnabled `
        -and [uint32]$summary.externalStartupClockHookThreadId -gt 0 `
        -and [bool]$summary.externalStartupClockPrimaryThreadMatched `
        -and [int]$summary.externalStartupClockVirtualQpcCallCount -gt 0 `
        -and [int]$summary.externalStartupClockHandoffAdoptCount -eq 1 `
        -and [int]$summary.externalStartupClockFaultCode -eq 0 `
        -and [string]$summary.externalStartupPolicy `
            -eq 'create-suspended-early-apc-unity-then-bridge-v1' `
        -and [string]$summary.recordingArmPolicyId `
            -eq 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
        -and [string]$summary.recordingAbsoluteTimeTarget.canonicalHex `
            -eq '44400000' `
        -and [bool]$summary.recordingArmBoundaryReached `
        -and [bool]$summary.recordingArmReleased `
        -and [string]$summary.recordingArmBoundaryTimeRaw.canonicalHex `
            -eq [string]$summary.recordingArmBoundaryFixedTimeRaw.canonicalHex `
        -and [bool]$summary.externalTimeUpdateResumeBoundaryInstalled `
        -and [int]$summary.externalTimeUpdateResumeBoundaryInstallCount -eq 1 `
        -and [int]$summary.externalTimeUpdateResumeBoundaryCallbackCount -gt 0 `
        -and [int]$summary.externalDeterministicClockFrameAdvanceCount `
            -le [int]$summary.externalTimeUpdateResumeBoundaryCallbackCount `
        -and [int]$summary.externalTimeUpdateResumeBoundaryCommitCount -eq 0 `
        -and [int]$summary.externalTimeUpdateResumeCommitFaultCode -eq 0 `
        -and [int]$summary.externalPlayerLoopPostLateUpdateIndex -ge 0 `
        -and [int]$summary.externalPlayerLoopTimeUpdateIndex -ge 0 `
        -and [int]$summary.externalPlayerLoopResumeBoundaryIndex -ge 0 `
        -and [int]$summary.externalPlayerLoopWaitForPresentationIndex `
            -eq ([int]$summary.externalPlayerLoopResumeBoundaryIndex + 1) `
        -and [string]::IsNullOrEmpty(
            [string]$summary.externalPlayerLoopBoundaryError) `
        -and [string]$summary.externalRngPayloadSynchronizationPolicy `
            -eq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [int]$summary.externalRngPayloadSeed -eq 1212896321 `
        -and [bool]$summary.externalPhysicalInput `
        -and [bool]$summary.externalPhysicalInputSynchronized `
        -and [int]$summary.externalInputSynchronizationPrimeFrames -eq 1 `
        -and [string]$summary.physicalInputSourceTraceSha256 `
            -eq $physicalInputTraceSha256 `
        -and -not [bool]$summary.visualRecognitionUsed `
        -and -not [bool]$summary.observerInputInjected `
        -and -not [bool]$summary.observerGameplayStateWritten `
        -and [bool]$result.success `
        -and [string]$result.samplingBoundary `
            -eq 'post-render-completed-frame-sampling-v1' `
        -and [bool]$result.baselineCaptured `
        -and -not [bool]$result.inputInjected `
        -and -not [bool]$result.timeWritten `
        -and -not [bool]$result.gameplayStateWritten `
        -and -not [bool]$result.saveLoadedByObserver `
        -and [bool]$result.externalInputSynchronized `
        -and [bool]$result.externalInputSynchronizationRequired `
        -and [int]$result.externalInputSynchronizedFrames `
            -eq [int]$matrix.maxTicks `
        -and [int]$result.externalInputSynchronizationPrimeFrames -eq 1 `
        -and [bool]$result.externalRngSynchronized `
        -and [bool]$result.externalRngSynchronizationOriginCaptured `
        -and [string]$result.externalRngSynchronizationPolicy `
            -eq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [int]$result.externalRngSeed -eq 1212896321 `
        -and [uint32]$result.externalClockBridgeAbi -eq 10 `
        -and [int]$result.externalClockBridgeStatus -eq 2 `
        -and -not [bool]$result.externalRuntimeVirtualClockRegistered `
        -and [int]$result.externalVirtualClockPaused -eq 0 `
        -and [int]$result.externalVirtualClockPauseCount -eq 0 `
        -and [int]$result.externalVirtualClockResumePending -eq 0 `
        -and [int]$result.externalVirtualClockResumeRequestCount -eq 0 `
        -and [int]$result.externalVirtualClockResumeCount -eq 0 `
        -and [bool]$result.externalDeterministicClockEnabled `
        -and [long]$result.externalDeterministicClockFrequency -gt 0 `
        -and [long]$result.externalDeterministicClockStepTicks -gt 0 `
        -and [long]$result.externalDeterministicClockFrequency `
            % [long]$result.externalDeterministicClockStepTicks -eq 0 `
        -and [long]$result.externalDeterministicClockFrequency `
            / [long]$result.externalDeterministicClockStepTicks -eq 50 `
        -and [long]$result.externalDeterministicClockAnchor -gt 0 `
        -and [int]$result.externalDeterministicClockFrameAdvanceCount -gt 0 `
        -and [int]$result.externalDeterministicClockEnableFaultCode -eq 0 `
        -and [int]$result.externalDeterministicClockAdvanceFaultCode -eq 0 `
        -and [bool]$result.externalDoublePhaseCalibrationApplied `
        -and [int]$result.externalDoublePhaseCalibrationAttempts -gt 0 `
        -and [int]$result.externalDoublePhaseCalibrationFaultCode -eq 0 `
        -and [string]$result.externalDoublePhaseFinalResidualBits `
            -eq '0000000000000000' `
        -and [string]$result.externalDoublePhaseFinalResidual.canonicalHex `
            -eq '0000000000000000' `
        -and [string]$result.externalDoublePhaseLastCorrectionBits `
            -eq [string]$summary.externalDoublePhaseLastCorrectionBits `
        -and [string]$result.externalRealtimeEpochNormalizationPolicyId `
            -eq [string]$summary.externalRealtimeEpochNormalizationPolicyId `
        -and [bool]$result.externalRealtimeEpochNormalizationApplied `
        -and [int]$result.externalRealtimeEpochNormalizationCount `
            -eq [int]$summary.externalRealtimeEpochNormalizationCount `
        -and [string]$result.externalRealtimeEpochNormalizationAfterBits `
            -eq [string]$result.externalRealtimeEpochNormalizationTargetBits `
        -and [int]$result.externalRealtimeEpochNormalizationFaultCode -eq 0 `
        -and [bool]$result.externalStartupClockHookInstalled `
        -and [bool]$result.externalStartupClockLatchEnabled `
        -and [uint32]$result.externalStartupClockHookThreadId `
            -eq [uint32]$summary.externalStartupClockHookThreadId `
        -and [int]$result.externalStartupClockVirtualQpcCallCount -gt 0 `
        -and [int]$result.externalStartupClockHandoffAdoptCount -eq 1 `
        -and [int]$result.externalStartupClockFaultCode -eq 0 `
        -and [string]$result.recordingAbsoluteTimeTarget.canonicalHex `
            -eq '44400000' `
        -and [bool]$result.recordingArmBoundaryReached `
        -and [bool]$result.recordingArmReleased `
        -and [string]$result.recordingArmBoundaryTimeRaw.canonicalHex `
            -eq [string]$result.recordingArmBoundaryFixedTimeRaw.canonicalHex `
        -and [bool]$result.externalTimeUpdateResumeBoundaryInstalled `
        -and [int]$result.externalTimeUpdateResumeBoundaryInstallCount -eq 1 `
        -and [int]$result.externalTimeUpdateResumeBoundaryCallbackCount -gt 0 `
        -and [int]$result.externalDeterministicClockFrameAdvanceCount `
            -le [int]$result.externalTimeUpdateResumeBoundaryCallbackCount `
        -and [int]$result.externalTimeUpdateResumeBoundaryCommitCount -eq 0 `
        -and [int]$result.externalTimeUpdateResumeCommitFaultCode -eq 0 `
        -and [int]$result.externalPlayerLoopPostLateUpdateIndex -ge 0 `
        -and [int]$result.externalPlayerLoopTimeUpdateIndex -ge 0 `
        -and [int]$result.externalPlayerLoopResumeBoundaryIndex -ge 0 `
        -and [int]$result.externalPlayerLoopWaitForPresentationIndex `
            -eq ([int]$result.externalPlayerLoopResumeBoundaryIndex + 1) `
        -and [string]::IsNullOrEmpty(
            [string]$result.externalPlayerLoopBoundaryError) `
        -and [string]::IsNullOrEmpty([string]$result.error) `
        -and [string]::IsNullOrEmpty(
            [string]$result.inputPhaseObservationError) `
        -and [int]$result.frameCount -eq [int]$matrix.maxTicks `
        -and [string]$clockProfile.phase `
            -eq 'waiting-for-recording-arm-release' `
        -and [int]$clockProfile.frames -eq 0 `
        -and [int]$clockProfile.targetFrameRate -eq 50 `
        -and [int]$clockProfile.vSyncCount -eq 0 `
        -and [single]$clockProfile.captureDeltaTime -eq [single]0.02 `
        -and [single]$clockProfile.fixedDeltaTime -eq [single]0.02 `
        -and $clockProfilePhaseErrorValid `
        -and [string]::IsNullOrEmpty([string]$clockProfile.readinessBlocker) `
        -and [string]$clockProfile.readinessBoundary `
            -eq 'semantic-idle' `
        -and [int]$clockProfile.readinessRequiredUpdates -eq 10 `
        -and [string]$clockProfile.readinessAbsoluteTimeTarget.canonicalHex `
            -eq '44000000' `
        -and [string]$clockProfile.readinessTimeRaw.canonicalHex `
            -eq [string]$clockProfile.readinessFixedTimeRaw.canonicalHex `
        -and [string]$clockProfile.recordingAbsoluteTimeTarget.canonicalHex `
            -eq '44400000' `
        -and [bool]$clockProfile.recordingArmBoundaryReached `
        -and -not [bool]$clockProfile.recordingArmReleased `
        -and [string]$clockProfile.recordingArmBoundaryTimeRaw.canonicalHex `
            -eq [string]$clockProfile.recordingArmBoundaryFixedTimeRaw.canonicalHex `
        -and [bool]$clockProfile.externalDoublePhaseCalibrationApplied `
        -and [int]$clockProfile.externalDoublePhaseCalibrationAttempts -gt 0 `
        -and [int]$clockProfile.externalDoublePhaseCalibrationFaultCode -eq 0 `
        -and [string]$clockProfile.externalDoublePhaseFinalResidualBits `
            -eq '0000000000000000' `
        -and [string]$clockProfile.externalDoublePhaseFinalResidual.canonicalHex `
            -eq '0000000000000000' `
        -and [string]$clockProfile.externalDoublePhaseLastCorrectionBits `
            -eq [string]$summary.externalDoublePhaseLastCorrectionBits `
        -and [string]$clockProfile.externalRealtimeEpochNormalizationPolicyId `
            -eq [string]$summary.externalRealtimeEpochNormalizationPolicyId `
        -and [bool]$clockProfile.externalRealtimeEpochNormalizationApplied `
        -and [int]$clockProfile.externalRealtimeEpochNormalizationCount -gt 0 `
        -and [string]$clockProfile.externalRealtimeEpochNormalizationAfterBits `
            -eq [string]$clockProfile.externalRealtimeEpochNormalizationTargetBits `
        -and [int]$clockProfile.externalRealtimeEpochNormalizationFaultCode -eq 0 `
        -and [string]$clockProfile.timeRawDouble.canonicalHex `
            -eq [string]$clockProfile.fixedTimeRawDouble.canonicalHex `
        -and [int]$clockProfile.readinessStableUpdates `
            -eq [int]$clockProfile.readinessRequiredUpdates `
        -and [int]$clockProfile.readinessFixedSteps -eq 1 `
        -and -not [bool]$clockProfile.readinessGameplayInputActive `
        -and (Test-T24RecordingPhaseContract `
            -Telemetry $summary `
            -RootRequestApplied $true) `
        -and (Test-T24RecordingPhaseContract `
            -Telemetry $result `
            -RootRequestApplied $true) `
        -and (Test-T24RecordingPhaseContract `
            -Telemetry $clockProfile `
            -RootRequestApplied $false)
    if (-not $runContractValid) {
        Add-Violation `
            -Category 'run-contract' `
            -Detail 'A successful run does not satisfy the read-only vanilla clock contract.' `
            -Data @{ attempt = [int]$attempt.attempt }
    }

    $actualBaselineHash = (Get-FileHash `
        -LiteralPath $baselinePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualTraceHash = (Get-FileHash `
        -LiteralPath $tracePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualPhaseHash = (Get-FileHash `
        -LiteralPath $phasePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualResultHash = (Get-FileHash `
        -LiteralPath $resultPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $baseline = Get-Content -LiteralPath $baselinePath -Raw |
        ConvertFrom-Json
    if ([string]$baseline.samplingBoundary `
            -ne 'post-render-completed-frame-sampling-v1' `
            -or [string]$baseline.fixtureReadinessBoundary -ne 'semantic-idle' `
            -or [int]$baseline.fixtureReadinessRequiredUpdates -ne 10 `
            -or [string]$baseline.fixtureReadinessAbsoluteTimeTarget.canonicalHex `
                -ne '44000000' `
            -or [string]$baseline.timeRaw.canonicalHex `
                -ne [string]$baseline.fixedTimeRaw.canonicalHex `
            -or [string]$baseline.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000') {
        Add-Violation `
            -Category 'fixture-readiness' `
            -Detail 'The baseline was not captured at the audited 10-tick semantic-idle boundary.' `
            -Data @{ attempt = [int]$attempt.attempt }
        $runContractValid = $false
    }
    if ($actualBaselineHash -ne [string]$attempt.baselineSha256 `
            -or $actualBaselineHash -ne [string]$summary.baselineSha256) {
        Add-Violation `
            -Category 'artifact-integrity' `
            -Detail 'Baseline hash does not match the matrix and run summary.' `
            -Data @{ attempt = [int]$attempt.attempt }
        $runContractValid = $false
    }
    if ($actualTraceHash -ne [string]$attempt.traceSha256 `
            -or $actualTraceHash -ne [string]$summary.traceSha256) {
        Add-Violation `
            -Category 'artifact-integrity' `
            -Detail 'Trace hash does not match the matrix and run summary.' `
            -Data @{ attempt = [int]$attempt.attempt }
        $runContractValid = $false
    }
    if ($actualPhaseHash -ne [string]$attempt.phaseSha256) {
        Add-Violation `
            -Category 'artifact-integrity' `
            -Detail 'Input-phase hash does not match the source matrix.' `
            -Data @{ attempt = [int]$attempt.attempt }
        $runContractValid = $false
    }
    if ($actualResultHash -ne [string]$attempt.resultSha256) {
        Add-Violation `
            -Category 'artifact-integrity' `
            -Detail 'Observer-result hash does not match the source matrix.' `
            -Data @{ attempt = [int]$attempt.attempt }
        $runContractValid = $false
    }

    $baselineSignature = Get-BaselineSignature -Path $baselinePath
    $authoritativeBaselineSignature =
        Get-AuthoritativeBaselineSignature -Path $baselinePath
    $clockFingerprint = Get-ClockFingerprint -Path $clockPath
    try {
        $frames = @(Read-T24ValidatedTrace -Path $tracePath)
    }
    catch {
        Add-Violation `
            -Category 'trace-integrity' `
            -Detail $_.Exception.Message `
            -Data @{ attempt = [int]$attempt.attempt }
        $runContractValid = $false
        $frames = @()
    }
    if ($frames.Count -ne [int]$matrix.maxTicks) {
        Add-Violation `
            -Category 'trace-contract' `
            -Detail 'Trace frame count differs from the matrix contract.' `
            -Data @{
                attempt = [int]$attempt.attempt
                actual = $frames.Count
                expected = [int]$matrix.maxTicks
            }
        $runContractValid = $false
    }
    $phaseTimeline = Get-CanonicalPhaseTimeline -Path $phasePath
    if (-not [bool]$phaseTimeline.valid) {
        Add-Violation `
            -Category 'input-phase-contract' `
            -Detail 'A synchronized no-TAS run has no canonical gameplay input phase timeline.' `
            -Data @{
                attempt = [int]$attempt.attempt
                failure = [string]$phaseTimeline.failure
            }
        $runContractValid = $false
    }

    $runs.Add([pscustomobject][ordered]@{
        attempt = [int]$attempt.attempt
        name = [string]$attempt.run
        runRoot = $runRoot
        contractValid = $runContractValid
        baselinePath = $baselinePath
        tracePath = $tracePath
        phasePath = $phasePath
        baselineSha256 = $actualBaselineHash
        traceSha256 = $actualTraceHash
        phaseSha256 = $actualPhaseHash
        resultSha256 = $actualResultHash
        baselineSignatureSha256 = [string]$baselineSignature.sha256
        authoritativeBaselineSignatureSha256 =
            [string]$authoritativeBaselineSignature.sha256
        assemblySetSha256 = Get-AssemblyFingerprint -Path $assembliesPath
        samplingAssemblySetSha256 = Get-AssemblyFingerprint `
            -Path $assembliesPath `
            -SamplingOnly
        clockContractSha256 = [string]$clockFingerprint.sha256
        clockContract = $clockFingerprint.normalized
        frames = $frames
        phaseTimeline = $phaseTimeline
    })
    Write-Output (
        'T24 envelope trace {0}/{1} validated: {2}' -f `
            $runs.Count,
            [int]$matrix.successfulRuns,
            [string]$attempt.run)
}

$validRuns = @($runs | Where-Object contractValid)
if ($validRuns.Count -lt $MinimumSuccessfulRuns) {
    Add-Violation `
        -Category 'run-contract' `
        -Detail 'Too few runs satisfy the full read-only vanilla contract.' `
        -Data @{ actual = $validRuns.Count; required = $MinimumSuccessfulRuns }
}
$assemblyFingerprints = @(
    $validRuns |
        ForEach-Object { $_.assemblySetSha256 } |
        Sort-Object -Unique)
$samplingAssemblyFingerprints = @(
    $validRuns |
        ForEach-Object { $_.samplingAssemblySetSha256 } |
        Sort-Object -Unique)
$clockFingerprints = @(
    $validRuns |
        ForEach-Object { $_.clockContractSha256 } |
        Sort-Object -Unique)
if ($assemblyFingerprints.Count -ne 1) {
    Add-Violation `
        -Category 'tool-compatibility' `
        -Detail 'Observer assembly sets differ across cold processes.'
}
if ($samplingAssemblyFingerprints.Count -ne 1) {
    Add-Violation `
        -Category 'tool-compatibility' `
        -Detail 'Core/GameObservation sampling assemblies differ across cold processes.'
}
if ($clockFingerprints.Count -ne 1) {
    Add-Violation `
        -Category 'tool-compatibility' `
        -Detail 'External clock contracts differ across cold processes.'
}

$exactGroups = @(
    $validRuns |
        Group-Object { $_.baselineSignatureSha256 } |
        Sort-Object @{ Expression = 'Count'; Descending = $true }, Name
)
$groups = @(
    $validRuns |
        Group-Object { $_.authoritativeBaselineSignatureSha256 } |
        Sort-Object @{ Expression = 'Count'; Descending = $true }, Name
)
$minimumAllRunInputPrefix = if ($validRuns.Count -eq 0) {
    0
}
else {
    [int]$matrix.maxTicks
}
if ($validRuns.Count -gt 1) {
    $inputReference = @($validRuns | Sort-Object attempt)[0]
    foreach ($other in @(
            $validRuns |
                Where-Object { $_.attempt -ne $inputReference.attempt } |
                Sort-Object attempt)) {
        $prefix = Get-CommonInputPrefix `
            -LeftFrames $inputReference.frames `
            -RightFrames $other.frames
        $minimumAllRunInputPrefix = [Math]::Min(
            $minimumAllRunInputPrefix,
            $prefix)
        if ($prefix -ne [int]$matrix.maxTicks) {
            Add-Violation `
                -Category 'synchronized-input' `
                -Detail 'Synchronized no-TAS runs do not contain the same complete canonical input timeline.' `
                -Data @{
                    left = [string]$inputReference.name
                    right = [string]$other.name
                    commonInputPrefixFrames = $prefix
                    expected = [int]$matrix.maxTicks
                }
        }
    }
}
$phaseEquivalentAcrossAllRuns = $validRuns.Count -gt 0
$canonicalPhaseEventCount = if ($validRuns.Count -eq 0) { 0 } else {
    @($validRuns | Sort-Object attempt)[0].phaseTimeline.events.Count
}
if ($validRuns.Count -gt 1) {
    $phaseReference = @($validRuns | Sort-Object attempt)[0]
    foreach ($other in @(
            $validRuns |
                Where-Object { $_.attempt -ne $phaseReference.attempt } |
                Sort-Object attempt)) {
        $leftEvents = @($phaseReference.phaseTimeline.events)
        $rightEvents = @($other.phaseTimeline.events)
        $limit = [Math]::Min($leftEvents.Count, $rightEvents.Count)
        $firstDifferenceIndex = $null
        for ($index = 0; $index -lt $limit; $index++) {
            if ([string]$leftEvents[$index] -ne [string]$rightEvents[$index]) {
                $firstDifferenceIndex = $index
                break
            }
        }
        if ($null -eq $firstDifferenceIndex `
                -and $leftEvents.Count -ne $rightEvents.Count) {
            $firstDifferenceIndex = $limit
        }
        if ($null -ne $firstDifferenceIndex) {
            $phaseEquivalentAcrossAllRuns = $false
            Add-Violation `
                -Category 'input-phase-negative-control' `
                -Detail 'Synchronized no-TAS runs have different canonical input phase timelines.' `
                -Data @{
                    left = [string]$phaseReference.name
                    right = [string]$other.name
                    firstDifferenceIndex = [int]$firstDifferenceIndex
                    leftEventCount = $leftEvents.Count
                    rightEventCount = $rightEvents.Count
                }
        }
    }
}
$strictGroup = if ($groups.Count -eq 0) { $null } else { $groups[0] }
if ($null -eq $strictGroup `
        -or $strictGroup.Count -lt $MinimumStrictBaselineGroupRuns) {
    Add-Violation `
        -Category 'baseline-cohort' `
        -Detail 'No authoritative-baseline cohort is large enough for a negative control.' `
        -Data @{
            actual = if ($null -eq $strictGroup) { 0 } else { $strictGroup.Count }
            required = $MinimumStrictBaselineGroupRuns
        }
}

$metrics = [ordered]@{}
foreach ($key in $toleranceCandidateKeys) {
    $metrics[$key] = [ordered]@{
        key = $key
        kind = 'Float32Bits'
        comparisonCount = 0L
        differenceCount = 0L
        maxAbsoluteDifference = 0.0
        maxUlpDistance = 0L
        witness = $null
    }
}
$pairSummaries = [Collections.Generic.List[object]]::new()
$authoritativeDifferenceWitnessKeys =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$authoritativeDifferenceObservationCount = 0L
$minimumObservedPrefix = [int]::MaxValue
$pairCount = 0
$pairTickComparisons = 0L
foreach ($measuredGroup in @($groups | Where-Object { $_.Count -ge 2 })) {
    $cohort = @($measuredGroup.Group | Sort-Object attempt)
    for ($leftIndex = 0; $leftIndex -lt $cohort.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; `
                $rightIndex -lt $cohort.Count; `
                $rightIndex++) {
            $leftRun = $cohort[$leftIndex]
            $rightRun = $cohort[$rightIndex]
            $prefix = Get-CommonInputPrefix `
                -LeftFrames $leftRun.frames `
                -RightFrames $rightRun.frames
            $pairCount++
            $pairTickComparisons += $prefix
            $minimumObservedPrefix = [Math]::Min(
                $minimumObservedPrefix,
                $prefix)
            $pairDifferenceCount = 0

            for ($tick = 0; $tick -lt $prefix; $tick++) {
                $leftFrame = $leftRun.frames[$tick]
                $rightFrame = $rightRun.frames[$tick]
                $leftSchema =
                    $script:traceSchemas[[int]$leftFrame.schemaId]
                $rightSchema =
                    $script:traceSchemas[[int]$rightFrame.schemaId]
                if ([int]$leftFrame.authoritativeFrameId `
                        -ne [int]$rightFrame.authoritativeFrameId) {
                    $leftKeys = $leftSchema.keys
                    $rightKeys = $rightSchema.keys
                    $sameSchema = [int]$leftFrame.schemaId `
                        -eq [int]$rightFrame.schemaId
                    $keys = if ($sameSchema) {
                        $leftKeys
                    }
                    else {
                        @($leftKeys + $rightKeys | Sort-Object -Unique)
                    }
                    for ($keyIndex = 0; `
                            $keyIndex -lt $keys.Count; `
                            $keyIndex++) {
                        $key = [string]$keys[$keyIndex]
                        if ($toleranceCandidateKeySet.Contains($key)) {
                            continue
                        }
                        $leftFieldIndex = 0
                        $rightFieldIndex = 0
                        $leftPresent = if ($sameSchema) {
                            $leftFieldIndex = $keyIndex
                            $true
                        }
                        else {
                            $leftSchema.keyIndex.TryGetValue(
                                $key,
                                [ref]$leftFieldIndex)
                        }
                        $rightPresent = if ($sameSchema) {
                            $rightFieldIndex = $keyIndex
                            $true
                        }
                        else {
                            $rightSchema.keyIndex.TryGetValue(
                                $key,
                                [ref]$rightFieldIndex)
                        }
                        $leftComparable = $leftPresent `
                            -and [bool]$leftSchema.comparable[$leftFieldIndex]
                        $rightComparable = $rightPresent `
                            -and [bool]$rightSchema.comparable[$rightFieldIndex]
                        if (-not $leftComparable -and -not $rightComparable) {
                            continue
                        }
                        $leftKind = if ($leftPresent) {
                            [string]$leftSchema.kinds[$leftFieldIndex]
                        }
                        else { '' }
                        $rightKind = if ($rightPresent) {
                            [string]$rightSchema.kinds[$rightFieldIndex]
                        }
                        else { '' }
                        if (-not $leftPresent `
                                -or -not $rightPresent `
                                -or $leftComparable -ne $rightComparable `
                                -or $leftKind -ne $rightKind) {
                            Add-Violation `
                                -Category 'field-schema' `
                                -Detail 'Comparable field schema differs within an authoritative-baseline cohort.' `
                                -Data @{
                                    pair = "$($leftRun.name):$($rightRun.name)"
                                    tick = $tick
                                    key = $key
                                }
                            continue
                        }
                        $leftValueId =
                            [int]$leftFrame.valueIds[$leftFieldIndex]
                        $rightValueId =
                            [int]$rightFrame.valueIds[$rightFieldIndex]
                        if ($leftValueId -eq $rightValueId) {
                            continue
                        }
                        $leftCanonicalHex =
                            [string]$script:canonicalValues[$leftValueId]
                        $rightCanonicalHex =
                            [string]$script:canonicalValues[$rightValueId]
                        $pairDifferenceCount++
                        $authoritativeDifferenceObservationCount++
                        $witnessKey = '{0}:{1}|{2}' -f `
                            $leftRun.name,
                            $rightRun.name,
                            $key
                        if ($authoritativeDifferenceWitnessKeys.Add(
                                $witnessKey)) {
                            Add-Violation `
                                -Category 'authoritative-difference' `
                                -Detail 'Pure vanilla runs differ in a field that must remain bitwise exact.' `
                                -Data @{
                                    pair = "$($leftRun.name):$($rightRun.name)"
                                    tick = $tick
                                    key = $key
                                    kind = $leftKind
                                    left = $leftCanonicalHex
                                    right = $rightCanonicalHex
                                }
                        }
                    }
                }

                foreach ($key in $toleranceCandidateKeys) {
                    $leftFieldIndex = 0
                    $rightFieldIndex = 0
                    $leftPresent = $leftSchema.keyIndex.TryGetValue(
                        $key,
                        [ref]$leftFieldIndex)
                    $rightPresent = $rightSchema.keyIndex.TryGetValue(
                        $key,
                        [ref]$rightFieldIndex)
                    $leftComparable = $leftPresent `
                        -and [bool]$leftSchema.comparable[$leftFieldIndex]
                    $rightComparable = $rightPresent `
                        -and [bool]$rightSchema.comparable[$rightFieldIndex]
                    if (-not $leftComparable -and -not $rightComparable) {
                        continue
                    }
                    $leftKind = if ($leftPresent) {
                        [string]$leftSchema.kinds[$leftFieldIndex]
                    }
                    else { '' }
                    $rightKind = if ($rightPresent) {
                        [string]$rightSchema.kinds[$rightFieldIndex]
                    }
                    else { '' }
                    if (-not $leftPresent `
                            -or -not $rightPresent `
                            -or $leftComparable -ne $rightComparable `
                            -or $leftKind -ne $rightKind) {
                        Add-Violation `
                            -Category 'field-schema' `
                            -Detail 'Comparable field schema differs within an authoritative-baseline cohort.' `
                            -Data @{
                                pair = "$($leftRun.name):$($rightRun.name)"
                                tick = $tick
                                key = [string]$key
                            }
                        continue
                    }
                    $metrics[$key].comparisonCount++
                    $leftValueId =
                        [int]$leftFrame.valueIds[$leftFieldIndex]
                    $rightValueId =
                        [int]$rightFrame.valueIds[$rightFieldIndex]
                    if ($leftValueId -eq $rightValueId) {
                        continue
                    }
                    $leftCanonicalHex =
                        [string]$script:canonicalValues[$leftValueId]
                    $rightCanonicalHex =
                        [string]$script:canonicalValues[$rightValueId]
                    $pairDifferenceCount++
                    if ($leftKind -ne 'Float32Bits') {
                        $authoritativeDifferenceObservationCount++
                        $witnessKey = '{0}:{1}|{2}' -f `
                            $leftRun.name,
                            $rightRun.name,
                            [string]$key
                        if ($authoritativeDifferenceWitnessKeys.Add(
                                $witnessKey)) {
                            Add-Violation `
                                -Category 'authoritative-difference' `
                                -Detail 'Pure vanilla runs differ in a field that must remain bitwise exact.' `
                                -Data @{
                                    pair = "$($leftRun.name):$($rightRun.name)"
                                    tick = $tick
                                    key = [string]$key
                                    kind = $leftKind
                                    left = $leftCanonicalHex
                                    right = $rightCanonicalHex
                                }
                        }
                        continue
                    }

                    $leftFloat = Convert-Float32Hex `
                        -Hex $leftCanonicalHex
                    $rightFloat = Convert-Float32Hex `
                        -Hex $rightCanonicalHex
                    if ([single]::IsNaN($leftFloat.value) `
                            -or [single]::IsNaN($rightFloat.value) `
                            -or [single]::IsInfinity($leftFloat.value) `
                            -or [single]::IsInfinity($rightFloat.value)) {
                        Add-Violation `
                            -Category 'float-contract' `
                            -Detail 'A tolerance candidate contains a non-finite value.' `
                            -Data @{ key = [string]$key; tick = $tick }
                        continue
                    }
                    $absolute = [Math]::Abs(
                        [double]$leftFloat.value `
                            - [double]$rightFloat.value)
                    $ulp = Get-Float32UlpDistance `
                        -LeftBits $leftFloat.bits `
                        -RightBits $rightFloat.bits
                    $metric = $metrics[$key]
                    $metric.differenceCount++
                    if ($absolute -gt [double]$metric.maxAbsoluteDifference `
                            -or $ulp -gt [uint64]$metric.maxUlpDistance) {
                        if ($absolute -gt [double]$metric.maxAbsoluteDifference) {
                            $metric.maxAbsoluteDifference = $absolute
                        }
                        if ($ulp -gt [uint64]$metric.maxUlpDistance) {
                            $metric.maxUlpDistance = $ulp
                        }
                        $metric.witness = [ordered]@{
                            pair = "$($leftRun.name):$($rightRun.name)"
                            logicalTick = $tick
                            leftCanonicalHex = $leftCanonicalHex
                            rightCanonicalHex = $rightCanonicalHex
                            absoluteDifference = $absolute
                            ulpDistance = $ulp
                        }
                    }
                }
            }
            $pairSummaries.Add([ordered]@{
                authoritativeBaselineSignatureSha256 =
                    [string]$measuredGroup.Name
                left = $leftRun.name
                right = $rightRun.name
                commonInputPrefixFrames = $prefix
                differingFieldObservations = $pairDifferenceCount
            })
            if (($pairCount % 10) -eq 0) {
                Write-Output (
                    'T24 envelope measured {0} reference pairs.' -f `
                        $pairCount)
            }
        }
    }
}
if ($minimumObservedPrefix -eq [int]::MaxValue) {
    $minimumObservedPrefix = 0
}
if ($minimumObservedPrefix -lt $MinimumCommonInputPrefixFrames) {
    Add-Violation `
        -Category 'input-overlap' `
        -Detail 'The exact-baseline vanilla cohort has too little common input history.' `
        -Data @{
            actual = $minimumObservedPrefix
            required = $MinimumCommonInputPrefixFrames
        }
}

$toleratedFields = @(
    $metrics.Values |
        Where-Object { $_.differenceCount -gt 0 } |
        ForEach-Object {
            [ordered]@{
                key = [string]$_.key
                kind = [string]$_.kind
                classification = 'render-transform-or-clock-phase-sample'
                comparisonRule = 'absolute-only-ulp-diagnostic'
                reason = 'Observed between synchronized read-only no-TAS cold processes with an exact fixture baseline and an identical complete canonical input timeline.'
                comparisonCount = [long]$_.comparisonCount
                differenceCount = [long]$_.differenceCount
                maxAbsoluteDifference = [double]$_.maxAbsoluteDifference
                maxUlpDistance = [uint64]$_.maxUlpDistance
                witness = $_.witness
            }
        }
)

$eligible = $violationCount -eq 0 `
    -and $validRuns.Count -ge $MinimumSuccessfulRuns `
    -and $null -ne $strictGroup `
    -and $strictGroup.Count -ge $MinimumStrictBaselineGroupRuns `
    -and $pairCount -gt 0 `
    -and $minimumObservedPrefix -ge $MinimumCommonInputPrefixFrames `
    -and $minimumAllRunInputPrefix -eq [int]$matrix.maxTicks `
    -and $phaseEquivalentAcrossAllRuns
$envelope = [ordered]@{
    schemaVersion = 2
    policyId = 't24-vanilla-synchronized-negative-control-v13-exact-post-root-request-first-phase'
    verdict = if ($eligible) { 'ELIGIBLE' } else { 'INELIGIBLE' }
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    generatorScript = $PSCommandPath
    generatorScriptSha256 = (Get-FileHash `
        -LiteralPath $PSCommandPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceMatrix = $matrixPath
    sourceMatrixSha256 = (Get-FileHash `
        -LiteralPath $matrixPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    requirements = [ordered]@{
        minimumSuccessfulRuns = $MinimumSuccessfulRuns
        minimumStrictBaselineGroupRuns = `
            $MinimumStrictBaselineGroupRuns
        minimumCommonInputPrefixFrames = `
            $MinimumCommonInputPrefixFrames
        fixtureReadinessBoundary = 'semantic-idle'
        fixtureReadinessRequiredUpdates = 10
        fixtureReadinessAbsoluteTimeTargetCanonicalHex = '44000000'
        fixtureReadinessAbsoluteTimeTarget = 512.0
        recordingAbsoluteTimeTargetCanonicalHex = '44400000'
        recordingAbsoluteTimeTarget = 768.0
        recordingArmPolicyId =
            'exact-absolute-time-post-root-request-first-global-phase-host-release-v11'
        recordingPhaseContractId =
            'exact-recording-root-global-phase-zero-v1'
        recordingFramePhaseModulo = 4
        recordingFramePhaseTarget = 0
        recordingRootRequestFrameOffset = 1
        recordingPhaseNormalizationRequired = $true
        recordingPhaseNormalizationMaximumHoldFrames = 8
        fixtureReadinessMaximumPhaseErrorFractionOfFixedStep = 0.001
        baselineSchemaVersion = 2
        absoluteTimeBaselineRequired = $true
        absoluteTimeTraceFieldsBitwiseExact = $true
        doublePhaseCalibrationProfile =
            'external-unity-startup-continuous-clock-v40-native-scene-lifecycle'
        doublePhaseCalibrationRequired = $true
        doublePhaseMaximumAbsoluteResidualSeconds = 0d
        samplingBoundary = 'post-render-completed-frame-sampling-v1'
    }
    compatibility = [ordered]@{
        successfulRuns = [int]$matrix.successfulRuns
        validReadOnlyVanillaRuns = $validRuns.Count
        frameCount = [int]$matrix.maxTicks
        samplingBoundary = 'post-render-completed-frame-sampling-v1'
        baselineSchemaVersion = 2
        absoluteTimeTraceFieldsBitwiseExact = $true
        doublePhaseCalibrationRequired = $true
        doublePhaseMaximumAbsoluteResidualSeconds = 0d
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
        physicalInputSynchronized = $true
        physicalInputSynchronizationPrimeFrames = 1
        physicalInputSourceTrace = $physicalInputTracePath
        physicalInputSourceTraceSha256 = $physicalInputTraceSha256
        minimumAllRunInputPrefixFrames = $minimumAllRunInputPrefix
        canonicalInputPhaseEquivalentAcrossAllRuns = `
            $phaseEquivalentAcrossAllRuns
        canonicalInputPhaseEventCount = $canonicalPhaseEventCount
        observerAssemblySetSha256 = if ($assemblyFingerprints.Count -eq 1) {
            $assemblyFingerprints[0]
        }
        else { '' }
        samplingAssemblySetSha256 = if (
            $samplingAssemblyFingerprints.Count -eq 1) {
            $samplingAssemblyFingerprints[0]
        }
        else { '' }
        externalClockContractSha256 = if ($clockFingerprints.Count -eq 1) {
            $clockFingerprints[0]
        }
        else { '' }
        externalClockContract = if ($validRuns.Count -eq 0) {
            $null
        }
        else { $validRuns[0].clockContract }
    }
    strictBaselineCohort = [ordered]@{
        signatureSha256 = if ($null -eq $strictGroup) {
            ''
        }
        else { [string]$strictGroup.Name }
        runCount = if ($null -eq $strictGroup) { 0 } else { $strictGroup.Count }
        pairCount = if ($null -eq $strictGroup) {
            0
        }
        else {
            [int](
                $strictGroup.Count * ($strictGroup.Count - 1) / 2)
        }
        pairTickComparisons = if ($null -eq $strictGroup) {
            0
        }
        else {
            [long](
                $strictGroup.Count * ($strictGroup.Count - 1) / 2) `
                * [long]$matrix.maxTicks
        }
        minimumCommonInputPrefixFrames = if ($null -eq $strictGroup) {
            0
        }
        else { [int]$matrix.maxTicks }
        members = if ($null -eq $strictGroup) {
            @()
        }
        else {
            @(
                $strictGroup.Group |
                    Sort-Object attempt |
                    ForEach-Object {
                        [ordered]@{
                            attempt = [int]$_.attempt
                            name = [string]$_.name
                            baselinePath = [string]$_.baselinePath
                            baselineSha256 = [string]$_.baselineSha256
                            tracePath = [string]$_.tracePath
                            traceSha256 = [string]$_.traceSha256
                            phasePath = [string]$_.phasePath
                            phaseSha256 = [string]$_.phaseSha256
                        }
                    })
        }
    }
    exactBaselineCatalog = @(
        $exactGroups |
            ForEach-Object {
                [ordered]@{
                    signatureSha256 = [string]$_.Name
                    runCount = [int]$_.Count
                    members = @(
                        $_.Group |
                            Sort-Object attempt |
                            ForEach-Object {
                                [ordered]@{
                                    attempt = [int]$_.attempt
                                    name = [string]$_.name
                                    baselinePath = [string]$_.baselinePath
                                    baselineSha256 = [string]$_.baselineSha256
                                    tracePath = [string]$_.tracePath
                                    traceSha256 = [string]$_.traceSha256
                                    phasePath = [string]$_.phasePath
                                    phaseSha256 = [string]$_.phaseSha256
                                }
                            })
                }
            })
    negativeControlMeasurement = [ordered]@{
        authoritativeBaselineCohortsWithPairs = @(
            $groups |
                Where-Object { $_.Count -ge 2 }).Count
        pairCount = $pairCount
        pairTickComparisons = $pairTickComparisons
        minimumCommonInputPrefixFrames = $minimumObservedPrefix
        authoritativeDifferenceObservationCount =
            $authoritativeDifferenceObservationCount
        authoritativeDifferenceWitnessCount =
            $authoritativeDifferenceWitnessKeys.Count
    }
    fieldPolicy = [ordered]@{
        exactByDefault = $true
        exactFixtureBaselineRequired = $true
        measurementCohortExcludesOnlyHeroRenderTransformXY = $true
        authoritativePhysicsRemainsBitwiseExact = $true
        toleratedFields = $toleratedFields
    }
    pairSummaries = @($pairSummaries)
    violationCount = $violationCount
    violations = @($violations)
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$envelope |
    ConvertTo-Json -Depth 30 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM

if (-not $eligible) {
    throw "T24 reference envelope is ineligible: $OutputPath"
}

Write-Output "T24 reference envelope eligible: $OutputPath"
