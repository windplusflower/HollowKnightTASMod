[CmdletBinding()]
param(
    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$validatorPath = Join-Path $PSScriptRoot 'Test-T24ScenarioCoverage.ps1'
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $campaign = 't24-scenario-coverage-self-test-{0}-{1}' -f `
        [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), `
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidenceRoot = Join-Path `
        $repoRoot `
        "artifacts\vanilla-equivalence\$campaign"
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $EvidenceRoot.StartsWith(
        $repoPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Self-test evidence root must remain inside the repository.'
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "Self-test evidence root already exists: $EvidenceRoot"
}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null

function Get-T24Sha256Text {
    param([Parameter(Mandatory)][string]$Text)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Text))
    ).ToLowerInvariant()
}

function Get-T24BigEndianHex {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($Bytes)
    }
    return [Convert]::ToHexString($Bytes).ToLowerInvariant()
}

function New-T24Field {
    param(
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][ValidateSet(
            'Boolean',
            'Int32',
            'Int64',
            'Float32Bits',
            'Utf8String')]
        [string]$Kind,
        [Parameter(Mandatory)][AllowEmptyString()]$Value
    )

    $hex = switch -CaseSensitive ($Kind) {
        'Boolean' {
            if ([bool]$Value) { '01' } else { '00' }
        }
        'Int32' {
            Get-T24BigEndianHex -Bytes ([BitConverter]::GetBytes([int]$Value))
        }
        'Int64' {
            Get-T24BigEndianHex -Bytes ([BitConverter]::GetBytes([long]$Value))
        }
        'Float32Bits' {
            Get-T24BigEndianHex -Bytes ([BitConverter]::GetBytes([float]$Value))
        }
        'Utf8String' {
            [Convert]::ToHexString(
                [Text.UTF8Encoding]::new($false, $true).
                    GetBytes([string]$Value)
            ).ToLowerInvariant()
        }
    }
    return [ordered]@{
        key = $Key
        kind = $Kind
        canonicalHex = $hex
        displayValue = [string]$Value
        comparable = $true
    }
}

function New-T24FrameLine {
    param(
        [Parameter(Mandatory)][int]$Tick,
        [Parameter(Mandatory)][bool]$Flag,
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][int]$Value,
        [Parameter(Mandatory)][float]$Height,
        [Parameter(Mandatory)][long]$Counter,
        [bool]$IncludeCounter = $true
    )

    $fieldMap = [ordered]@{
        'empty' = New-T24Field -Key 'empty' -Kind Utf8String -Value ''
        'flag' = New-T24Field -Key 'flag' -Kind Boolean -Value $Flag
        'height' = New-T24Field -Key 'height' -Kind Float32Bits -Value $Height
        'mode' = New-T24Field -Key 'mode' -Kind Utf8String -Value $Mode
        'scene.name' = New-T24Field `
            -Key 'scene.name' `
            -Kind Utf8String `
            -Value 'GG_Workshop'
        'value' = New-T24Field -Key 'value' -Kind Int32 -Value $Value
    }
    if ($IncludeCounter) {
        $fieldMap['counter'] = New-T24Field `
            -Key 'counter' `
            -Kind Int64 `
            -Value $Counter
    }
    $fields = @(
        foreach ($key in @($fieldMap.Keys | Sort-Object)) {
            $fieldMap[$key]
        })
    $canonicalFields = @(
        foreach ($field in $fields) {
            [ordered]@{
                key = [string]$field.key
                kind = [string]$field.kind
                canonicalHex = [string]$field.canonicalHex
            }
        })
    $canonical = [ordered]@{
        logicalTick = [long]$Tick
        fields = $canonicalFields
    } | ConvertTo-Json -Compress -Depth 10
    $frame = [ordered]@{
        schemaVersion = 1
        sequence = [long]($Tick + 1)
        logicalTick = [long]$Tick
        comparisonSha256 = Get-T24Sha256Text -Text $canonical
        fields = $fields
    }
    return $frame | ConvertTo-Json -Compress -Depth 10
}

function Write-T24Trace {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$MissingCounter,
        [switch]$MechanismAbsent
    )

    $flags = if ($MechanismAbsent) {
        @($false, $false, $false, $false)
    }
    else {
        @($false, $true, $true, $false)
    }
    $modes = if ($MechanismAbsent) {
        @('Idle', 'Idle', 'Idle', 'Idle')
    }
    else {
        @('Idle', 'Slash', 'Slash', 'Idle')
    }
    $values = @(10, 9, 7, 7)
    $heights = @([float]0, [float]1, [float]2, [float]0)
    $lines = @(
        for ($tick = 0; $tick -lt 4; $tick++) {
            New-T24FrameLine `
                -Tick $tick `
                -Flag $flags[$tick] `
                -Mode $modes[$tick] `
                -Value $values[$tick] `
                -Height $heights[$tick] `
                -Counter ([long]$tick) `
                -IncludeCounter (-not ($MissingCounter -and $tick -eq 2))
        })
    [IO.File]::WriteAllLines(
        $Path,
        $lines,
        [Text.UTF8Encoding]::new($false))
}

function Write-T24Json {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Assert-T24Verdict {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL')][string]$Expected,
        [Parameter(Mandatory)][string]$Case,
        [string]$ErrorPattern = ''
    )

    if ([string]$Report.verdict -cne $Expected) {
        throw (
            "Self-test '$Case' expected $Expected but got " `
            + [string]$Report.verdict + ': ' + ($Report.errors -join ' | '))
    }
    if (-not [string]::IsNullOrWhiteSpace($ErrorPattern) `
            -and (($Report.errors -join ' | ') -notmatch $ErrorPattern)) {
        throw "Self-test '$Case' did not report expected pattern '$ErrorPattern'."
    }
}

$moviePath = Join-Path $EvidenceRoot 'self-test.hktas'
$movieLines = @(
    'hktas 1',
    'game 1.5.78.11833',
    'api 1.5.78.11833-77',
    'manifest-sha256 0000000000000000000000000000000000000000000000000000000000000000',
    'baseline none none',
    'tick-unit input',
    '---',
    'frames 1 hold=right',
    'frames 1 hold=right,attack',
    'frames 1 hold=right',
    'frames 1 hold=-')
[IO.File]::WriteAllLines(
    $moviePath,
    $movieLines,
    [Text.UTF8Encoding]::new($false))
$movieHash = (Get-FileHash -LiteralPath $moviePath -Algorithm SHA256).Hash.
    ToLowerInvariant()
$movieRelative = $moviePath.Substring($repoRoot.Length).
    TrimStart('\').Replace('\', '/')

$allModes = @(
    'VanillaReference',
    'TasPassive',
    'ManualTas',
    'AiTas',
    'SequentialStep',
    'BatchStep')
$contract = [ordered]@{
    schemaVersion = 1
    scenarioId = 't24.scenario-validator-self-test.v1'
    description = 'Synthetic contract covering every frozen assertion type.'
    fixture = [ordered]@{
        slot = 2
        startScene = 'GG_Workshop'
    }
    movie = [ordered]@{
        path = $movieRelative
        sha256 = $movieHash
        expandedTicks = 4
        inputMode = 'digital-only'
        digitalActionMask = 34
    }
    requiredFields = @(
        [ordered]@{ key = 'counter'; kind = 'Int64' },
        [ordered]@{ key = 'empty'; kind = 'Utf8String' },
        [ordered]@{ key = 'flag'; kind = 'Boolean' },
        [ordered]@{ key = 'height'; kind = 'Float32Bits' },
        [ordered]@{ key = 'mode'; kind = 'Utf8String' },
        [ordered]@{ key = 'scene.name'; kind = 'Utf8String' },
        [ordered]@{ key = 'value'; kind = 'Int32' })
    assertions = @(
        [ordered]@{
            id = 'flag-seen'
            type = 'seen'
            description = 'The synthetic action becomes active.'
            condition = [ordered]@{ field = 'flag'; op = 'eq'; value = $true }
            minCount = 2
        },
        [ordered]@{
            id = 'action-coincident'
            type = 'coincident'
            description = 'Action and clip coincide.'
            conditions = @(
                [ordered]@{ field = 'flag'; op = 'eq'; value = $true },
                [ordered]@{ field = 'mode'; op = 'eq'; value = 'Slash' })
            minCount = 2
        },
        [ordered]@{
            id = 'action-transition'
            type = 'transition'
            description = 'Action rises on an adjacent frame.'
            from = [ordered]@{ field = 'flag'; op = 'eq'; value = $false }
            to = [ordered]@{ field = 'flag'; op = 'eq'; value = $true }
            minCount = 1
        },
        [ordered]@{
            id = 'value-delta'
            type = 'delta'
            description = 'Value decreases by two in one frame.'
            field = 'value'
            direction = 'decrease'
            minMagnitude = 2
            minCount = 1
        },
        [ordered]@{
            id = 'mode-order'
            type = 'ordered-match'
            description = 'Idle, Slash and Idle occur in order.'
            field = 'mode'
            patterns = @('\AIdle\z', '\ASlash\z', '\AIdle\z')
            minCount = 1
        },
        [ordered]@{
            id = 'action-correlated'
            type = 'all-when'
            description = 'Every active action has the correct clip and height.'
            when = [ordered]@{ field = 'flag'; op = 'eq'; value = $true }
            then = @(
                [ordered]@{ field = 'mode'; op = 'regex'; value = '\ASlash\z' },
                [ordered]@{ field = 'height'; op = 'gt'; value = 0 })
            minTriggered = 2
        })
    requiredModes = $allModes
    terminal = [ordered]@{
        allowedScenes = @('GG_Workshop')
        maxTicks = 4
    }
}

$contractPath = Join-Path $EvidenceRoot 'contract.json'
$positiveTracePath = Join-Path $EvidenceRoot 'positive.trace.jsonl'
Write-T24Json -Path $contractPath -Value $contract
Write-T24Trace -Path $positiveTracePath
$positive = & $validatorPath `
    -ContractPath $contractPath `
    -TracePath $positiveTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'positive.report.json') `
    -RunMode AiTas `
    -NoThrow
Assert-T24Verdict -Report $positive -Expected PASS -Case positive

$caseResults = [Collections.Generic.List[object]]::new()
$caseResults.Add([ordered]@{ id = 'positive'; verdict = 'PASS' })

$versionedContract = $contract | ConvertTo-Json -Depth 50 |
    ConvertFrom-Json -AsHashtable -Depth 50
$versionedContract['scenarioId'] = 't24.scenario-validator-self-test.v2'
$versionedContractPath = Join-Path `
    $EvidenceRoot `
    'positive-versioned.contract.json'
Write-T24Json -Path $versionedContractPath -Value $versionedContract
$versioned = & $validatorPath `
    -ContractPath $versionedContractPath `
    -TracePath $positiveTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'positive-versioned.report.json') `
    -RunMode AiTas `
    -NoThrow
Assert-T24Verdict `
    -Report $versioned `
    -Expected PASS `
    -Case positive-versioned
$caseResults.Add([ordered]@{ id = 'positive-versioned-id'; verdict = 'PASS' })

$invalidVersionContract = $contract | ConvertTo-Json -Depth 50 |
    ConvertFrom-Json -AsHashtable -Depth 50
$invalidVersionContract['scenarioId'] =
    't24.scenario-validator-self-test.v0'
$invalidVersionContractPath = Join-Path `
    $EvidenceRoot `
    'negative-scenario-version.contract.json'
Write-T24Json `
    -Path $invalidVersionContractPath `
    -Value $invalidVersionContract
$invalidVersion = & $validatorPath `
    -ContractPath $invalidVersionContractPath `
    -TracePath $positiveTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'negative-scenario-version.report.json') `
    -NoThrow
Assert-T24Verdict `
    -Report $invalidVersion `
    -Expected FAIL `
    -Case invalid-scenario-version `
    -ErrorPattern 'scenarioId must match'
$caseResults.Add([ordered]@{ id = 'invalid-scenario-version'; verdict = 'PASS' })

$missingTracePath = Join-Path $EvidenceRoot 'negative-missing-field.trace.jsonl'
Write-T24Trace -Path $missingTracePath -MissingCounter
$missing = & $validatorPath `
    -ContractPath $contractPath `
    -TracePath $missingTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'negative-missing-field.report.json') `
    -NoThrow
Assert-T24Verdict `
    -Report $missing `
    -Expected FAIL `
    -Case missing-field `
    -ErrorPattern "lacks required field 'counter'"
$caseResults.Add([ordered]@{ id = 'missing-required-field'; verdict = 'PASS' })

$badHashContract = $contract | ConvertTo-Json -Depth 50 |
    ConvertFrom-Json -AsHashtable -Depth 50
$badHashContract['movie']['sha256'] = '0' * 64
$badHashContractPath = Join-Path $EvidenceRoot 'negative-movie-hash.contract.json'
Write-T24Json -Path $badHashContractPath -Value $badHashContract
$badHash = & $validatorPath `
    -ContractPath $badHashContractPath `
    -TracePath $positiveTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'negative-movie-hash.report.json') `
    -NoThrow
Assert-T24Verdict `
    -Report $badHash `
    -Expected FAIL `
    -Case movie-hash `
    -ErrorPattern 'Frozen movie SHA-256 mismatch'
$caseResults.Add([ordered]@{ id = 'changed-movie-hash'; verdict = 'PASS' })

$absentTracePath = Join-Path $EvidenceRoot 'negative-absent-mechanism.trace.jsonl'
Write-T24Trace -Path $absentTracePath -MechanismAbsent
$absent = & $validatorPath `
    -ContractPath $contractPath `
    -TracePath $absentTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'negative-absent-mechanism.report.json') `
    -NoThrow
Assert-T24Verdict `
    -Report $absent `
    -Expected FAIL `
    -Case absent-mechanism `
    -ErrorPattern "Assertion 'flag-seen' failed"
$caseResults.Add([ordered]@{ id = 'equal-but-mechanism-absent'; verdict = 'PASS' })

$unknownAssertionContract = $contract | ConvertTo-Json -Depth 50 |
    ConvertFrom-Json -AsHashtable -Depth 50
$unknownAssertionContract['assertions'][0]['type'] = 'mystery'
$unknownAssertionPath = Join-Path `
    $EvidenceRoot `
    'negative-unknown-assertion.contract.json'
Write-T24Json `
    -Path $unknownAssertionPath `
    -Value $unknownAssertionContract
$unknownAssertion = & $validatorPath `
    -ContractPath $unknownAssertionPath `
    -TracePath $positiveTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'negative-unknown-assertion.report.json') `
    -NoThrow
Assert-T24Verdict `
    -Report $unknownAssertion `
    -Expected FAIL `
    -Case unknown-assertion `
    -ErrorPattern "unsupported type 'mystery'"
$caseResults.Add([ordered]@{ id = 'unknown-assertion-type'; verdict = 'PASS' })

$unknownPropertyContract = $contract | ConvertTo-Json -Depth 50 |
    ConvertFrom-Json -AsHashtable -Depth 50
$unknownPropertyContract['unexpected'] = $true
$unknownPropertyPath = Join-Path `
    $EvidenceRoot `
    'negative-unknown-property.contract.json'
Write-T24Json `
    -Path $unknownPropertyPath `
    -Value $unknownPropertyContract
$unknownProperty = & $validatorPath `
    -ContractPath $unknownPropertyPath `
    -TracePath $positiveTracePath `
    -OutputPath (Join-Path $EvidenceRoot 'negative-unknown-property.report.json') `
    -NoThrow
Assert-T24Verdict `
    -Report $unknownProperty `
    -Expected FAIL `
    -Case unknown-property `
    -ErrorPattern 'unknown=\[unexpected\]'
$caseResults.Add([ordered]@{ id = 'unknown-contract-property'; verdict = 'PASS' })

$summary = [ordered]@{
    schemaVersion = 1
    verdict = 'PASS'
    evidenceRoot = $EvidenceRoot
    validatorSha256 = (
        Get-FileHash -LiteralPath $validatorPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    cases = $caseResults.ToArray()
}
Write-T24Json `
    -Path (Join-Path $EvidenceRoot 'self-test-report.json') `
    -Value $summary

[pscustomobject]$summary
