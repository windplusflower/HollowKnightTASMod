[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ContractPath,

    [Parameter(Mandatory)]
    [string]$TracePath,

    [string]$OutputPath = '',

    [ValidateSet(
        '',
        'VanillaReference',
        'TasPassive',
        'ManualTas',
        'AiTas',
        'SequentialStep',
        'BatchStep')]
    [string]$RunMode = '',

    [switch]$NoThrow
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 scenario coverage validation requires PowerShell 7 or newer.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ContractPath = [IO.Path]::GetFullPath($ContractPath)
$TracePath = [IO.Path]::GetFullPath($TracePath)
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
}

function Get-T24Sha256Text {
    param([Parameter(Mandatory)][string]$Text)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Text))
    ).ToLowerInvariant()
}

function Get-T24Sha256File {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.
        ToLowerInvariant()
}

function Assert-T24ExactKeys {
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Value,

        [Parameter(Mandatory)]
        [string[]]$Keys,

        [Parameter(Mandatory)]
        [string]$Context
    )

    $actual = @($Value.Keys | ForEach-Object { [string]$_ })
    $unknown = @($actual | Where-Object { $Keys -cnotcontains $_ })
    $missing = @($Keys | Where-Object { $actual -cnotcontains $_ })
    if ($unknown.Count -ne 0 -or $missing.Count -ne 0) {
        throw (
            "$Context has an invalid property set; missing=[{0}]; unknown=[{1}]." `
            -f ($missing -join ','), ($unknown -join ','))
    }
}

function Assert-T24NonEmptyString {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Context
    )

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Context must be a non-empty string."
    }
}

function Assert-T24PositiveInt32 {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Context
    )

    if ($Value -isnot [int] -and $Value -isnot [long]) {
        throw "$Context must be an integer."
    }
    $numeric = [long]$Value
    if ($numeric -lt 1 -or $numeric -gt [int]::MaxValue) {
        throw "$Context must be within 1..Int32.MaxValue."
    }
    return [int]$numeric
}

function Assert-T24NonNegativeInt32 {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Context
    )

    if ($Value -isnot [int] -and $Value -isnot [long]) {
        throw "$Context must be an integer."
    }
    $numeric = [long]$Value
    if ($numeric -lt 0 -or $numeric -gt [int]::MaxValue) {
        throw "$Context must be within 0..Int32.MaxValue."
    }
    return [int]$numeric
}

function ConvertFrom-T24CanonicalValue {
    param(
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Hex,
        [Parameter(Mandatory)][string]$Context
    )

    if ($Hex -cnotmatch '\A(?:[0-9a-f]{2})*\z') {
        throw "$Context has non-canonical hexadecimal data."
    }
    try {
        $bytes = [Convert]::FromHexString($Hex)
    }
    catch {
        throw "$Context has invalid hexadecimal data."
    }

    switch -CaseSensitive ($Kind) {
        'Boolean' {
            if ($bytes.Length -ne 1 -or $bytes[0] -gt 1) {
                throw "$Context must encode Boolean as exactly 00 or 01."
            }
            return [bool]($bytes[0] -eq 1)
        }
        'Int32' {
            if ($bytes.Length -ne 4) {
                throw "$Context must contain four Int32 bytes."
            }
            [Array]::Reverse($bytes)
            return [BitConverter]::ToInt32($bytes, 0)
        }
        'Int64' {
            if ($bytes.Length -ne 8) {
                throw "$Context must contain eight Int64 bytes."
            }
            [Array]::Reverse($bytes)
            return [BitConverter]::ToInt64($bytes, 0)
        }
        'Float32Bits' {
            if ($bytes.Length -ne 4) {
                throw "$Context must contain four Float32 bytes."
            }
            [Array]::Reverse($bytes)
            $value = [BitConverter]::ToSingle($bytes, 0)
            if ([float]::IsNaN($value)) {
                throw "$Context cannot use NaN in a coverage assertion."
            }
            return $value
        }
        'Utf8String' {
            try {
                return [Text.UTF8Encoding]::new($false, $true).
                    GetString($bytes)
            }
            catch {
                throw "$Context contains invalid UTF-8."
            }
        }
        default {
            throw "$Context uses unsupported kind '$Kind'."
        }
    }
}

function Test-T24ExpectedType {
    param(
        [Parameter(Mandatory)][string]$Kind,
        [AllowNull()]$Value
    )

    switch -CaseSensitive ($Kind) {
        'Boolean' { return $Value -is [bool] }
        'Int32' {
            return ($Value -is [int] -or $Value -is [long]) `
                -and [long]$Value -ge [int]::MinValue `
                -and [long]$Value -le [int]::MaxValue
        }
        'Int64' { return $Value -is [int] -or $Value -is [long] }
        'Float32Bits' {
            return $Value -is [int] `
                -or $Value -is [long] `
                -or $Value -is [float] `
                -or $Value -is [double] `
                -or $Value -is [decimal]
        }
        'Utf8String' { return $Value -is [string] }
        default { return $false }
    }
}

function Test-T24Condition {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Condition,
        [Parameter(Mandatory)][Collections.IDictionary]$Frame,
        [Parameter(Mandatory)][Collections.IDictionary]$KindMap
    )

    $field = [string]$Condition['field']
    $kind = [string]$KindMap[$field]
    $actual = $Frame[$field]
    $operator = [string]$Condition['op']
    $expected = $Condition['value']

    switch -CaseSensitive ($operator) {
        'eq' { return $actual -ceq $expected }
        'ne' { return $actual -cne $expected }
        'gt' { return $actual -gt $expected }
        'ge' { return $actual -ge $expected }
        'lt' { return $actual -lt $expected }
        'le' { return $actual -le $expected }
        'regex' {
            return [regex]::IsMatch(
                [string]$actual,
                [string]$expected,
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        }
        'in' {
            foreach ($candidate in @($expected)) {
                if ($actual -ceq $candidate) {
                    return $true
                }
            }
            return $false
        }
        default {
            throw "Condition operator '$operator' is not supported."
        }
    }
}

function Assert-T24ConditionContract {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Condition,
        [Parameter(Mandatory)][Collections.IDictionary]$KindMap,
        [Parameter(Mandatory)][string]$Context
    )

    Assert-T24ExactKeys `
        -Value $Condition `
        -Keys @('field', 'op', 'value') `
        -Context $Context
    Assert-T24NonEmptyString -Value $Condition['field'] -Context "$Context.field"
    $field = [string]$Condition['field']
    if (-not $KindMap.Contains($field)) {
        throw "$Context references undeclared required field '$field'."
    }
    $operator = [string]$Condition['op']
    if (@('eq', 'ne', 'gt', 'ge', 'lt', 'le', 'regex', 'in') `
            -cnotcontains $operator) {
        throw "$Context uses unsupported operator '$operator'."
    }
    $kind = [string]$KindMap[$field]
    $value = $Condition['value']
    if ($operator -ceq 'regex') {
        if ($kind -cne 'Utf8String' `
                -or $value -isnot [string] `
                -or -not ([string]$value).StartsWith('\A') `
                -or -not ([string]$value).EndsWith('\z')) {
            throw "$Context regex requires Utf8String and full \\A...\\z anchoring."
        }
        try {
            [void][regex]::new(
                [string]$value,
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        }
        catch {
            throw "$Context contains an invalid regular expression."
        }
        return
    }
    if ($operator -ceq 'in') {
        if ($value -isnot [Collections.IList] -or @($value).Count -eq 0) {
            throw "$Context in operator requires a non-empty JSON array."
        }
        foreach ($candidate in @($value)) {
            if (-not (Test-T24ExpectedType -Kind $kind -Value $candidate)) {
                throw "$Context in element does not match field kind '$kind'."
            }
        }
        return
    }
    if (-not (Test-T24ExpectedType -Kind $kind -Value $value)) {
        throw "$Context value does not match field kind '$kind'."
    }
    if (@('gt', 'ge', 'lt', 'le') -ccontains $operator `
            -and $kind -notin @('Int32', 'Int64', 'Float32Bits')) {
        throw "$Context operator '$operator' requires a numeric field."
    }
}

function Get-T24MovieFacts {
    param([Parameter(Mandatory)][string]$Path)

    $actionBits = @{
        left = 1
        right = 2
        up = 4
        down = 8
        jump = 16
        attack = 32
        dash = 64
        cast = 128
        quickcast = 256
        superdash = 512
        dreamnail = 1024
    }
    $ticks = 0L
    $mask = 0
    $lineNumber = 0
    foreach ($raw in [IO.File]::ReadAllLines($Path)) {
        $lineNumber++
        $line = $raw.Trim()
        if ([string]::IsNullOrWhiteSpace($line) `
                -or $line.StartsWith('#') `
                -or $line.StartsWith('marker ') `
                -or $line.StartsWith('checkpoint ') `
                -or $line -cmatch `
                    '\Aassert scene\.name == "[A-Za-z0-9_]+"\z' `
                -or $line -in @(
                    'hktas 1',
                    'game 1.5.78.11833',
                    'api 1.5.78.11833-77',
                    'baseline none none',
                    'tick-unit input',
                    '---') `
                -or $line -match '\Amanifest-sha256 [0-9a-f]{64}\z') {
            continue
        }
        if ($line -cnotmatch '\Aframes ([1-9][0-9]*) hold=([a-z,\-]+)\z') {
            throw "Movie line $lineNumber is not a frozen digital frames instruction: $line"
        }
        $count = [long]$Matches[1]
        if ($count -gt ([long]::MaxValue - $ticks)) {
            throw "Movie line $lineNumber overflows the expanded tick count."
        }
        $ticks += $count
        $hold = $Matches[2]
        if ($hold -ceq '-') {
            continue
        }
        $seen = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($action in $hold.Split(',')) {
            if (-not $actionBits.ContainsKey($action)) {
                throw "Movie line $lineNumber uses unknown digital action '$action'."
            }
            if (-not $seen.Add($action)) {
                throw "Movie line $lineNumber repeats digital action '$action'."
            }
            $mask = $mask -bor [int]$actionBits[$action]
        }
    }
    return [ordered]@{
        expandedTicks = $ticks
        digitalActionMask = $mask
    }
}

function Read-T24Trace {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][Collections.IDictionary]$KindMap
    )

    $lines = @([IO.File]::ReadAllLines($Path))
    if ($lines.Count -eq 0) {
        throw 'Trace must contain at least one frame.'
    }
    $frames = [Collections.Generic.List[object]]::new($lines.Count)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $context = "Trace line $($index + 1)"
        try {
            $frame = $lines[$index] | ConvertFrom-Json -AsHashtable -Depth 50
        }
        catch {
            throw "$context is not valid JSON: $($_.Exception.Message)"
        }
        if ($frame -isnot [Collections.IDictionary]) {
            throw "$context must be a JSON object."
        }
        Assert-T24ExactKeys `
            -Value $frame `
            -Keys @(
                'schemaVersion',
                'sequence',
                'logicalTick',
                'comparisonSha256',
                'fields') `
            -Context $context
        if ($frame['schemaVersion'] -isnot [int] `
                -and $frame['schemaVersion'] -isnot [long]) {
            throw "$context schemaVersion must be an integer."
        }
        if ([long]$frame['schemaVersion'] -ne 1) {
            throw "$context has unsupported schemaVersion."
        }
        if ([long]$frame['sequence'] -ne [long]($index + 1)) {
            throw "$context has non-contiguous sequence."
        }
        if ([long]$frame['logicalTick'] -ne [long]$index) {
            throw "$context has non-contiguous logicalTick."
        }
        if ($frame['comparisonSha256'] -isnot [string] `
                -or [string]$frame['comparisonSha256'] `
                    -cnotmatch '\A[0-9a-f]{64}\z') {
            throw "$context has invalid comparisonSha256."
        }
        if ($frame['fields'] -isnot [Collections.IList]) {
            throw "$context fields must be a JSON array."
        }

        $allFields = [ordered]@{}
        $typed = [ordered]@{}
        $sourceKeys = [Collections.Generic.List[string]]::new()
        foreach ($field in @($frame['fields'])) {
            if ($field -isnot [Collections.IDictionary]) {
                throw "$context contains a non-object field."
            }
            Assert-T24ExactKeys `
                -Value $field `
                -Keys @(
                    'key',
                    'kind',
                    'canonicalHex',
                    'displayValue',
                    'comparable') `
                -Context "$context field"
            Assert-T24NonEmptyString `
                -Value $field['key'] `
                -Context "$context field.key"
            Assert-T24NonEmptyString `
                -Value $field['kind'] `
                -Context "$context field.kind"
            $key = [string]$field['key']
            if ($allFields.Contains($key)) {
                throw "$context contains duplicate field '$key'."
            }
            if ($field['comparable'] -isnot [bool]) {
                throw "$context field '$key' comparable must be Boolean."
            }
            if ($field['canonicalHex'] -isnot [string]) {
                throw "$context field '$key' canonicalHex must be a string."
            }
            $allFields[$key] = $field
            $sourceKeys.Add($key)
        }
        $sortedKeys = [string[]]@($allFields.Keys)
        [Array]::Sort($sortedKeys, [StringComparer]::Ordinal)
        for ($fieldIndex = 0; $fieldIndex -lt $sortedKeys.Length; $fieldIndex++) {
            if ($sourceKeys[$fieldIndex] -cne $sortedKeys[$fieldIndex]) {
                throw "$context fields are not in ordinal key order."
            }
        }
        $canonicalFields = @(
            foreach ($key in $sortedKeys) {
                $field = $allFields[$key]
                if (-not [bool]$field['comparable']) {
                    continue
                }
                [ordered]@{
                    key = $key
                    kind = [string]$field['kind']
                    canonicalHex = [string]$field['canonicalHex']
                }
            })
        $canonical = [ordered]@{
            logicalTick = [long]$frame['logicalTick']
            fields = $canonicalFields
        } | ConvertTo-Json -Compress -Depth 10
        if ((Get-T24Sha256Text -Text $canonical) `
                -cne [string]$frame['comparisonSha256']) {
            throw "$context comparisonSha256 does not match its fields."
        }
        foreach ($key in $KindMap.Keys) {
            if (-not $allFields.Contains([string]$key)) {
                throw "$context lacks required field '$key'."
            }
            $field = $allFields[[string]$key]
            if (-not [bool]$field['comparable']) {
                throw "$context required field '$key' is non-comparable."
            }
            if ([string]$field['kind'] -cne [string]$KindMap[$key]) {
                throw (
                    "$context required field '$key' kind mismatch; expected=" `
                    + [string]$KindMap[$key] + '; actual=' `
                    + [string]$field['kind'])
            }
            $typed[[string]$key] = ConvertFrom-T24CanonicalValue `
                -Kind ([string]$field['kind']) `
                -Hex ([string]$field['canonicalHex']) `
                -Context "$context field '$key'"
        }
        $frames.Add($typed)
    }
    return $frames.ToArray()
}

function New-T24AssertionResult {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Assertion,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$HitTicks,
        [AllowNull()]$FirstFailureTick,
        [Parameter(Mandatory)][Collections.IDictionary]$Actual,
        [Parameter(Mandatory)][string]$Message
    )

    return [ordered]@{
        id = [string]$Assertion['id']
        type = [string]$Assertion['type']
        verdict = if ($Passed) { 'PASS' } else { 'FAIL' }
        hitCount = $HitTicks.Count
        hitTicks = $HitTicks
        firstFailureTick = $FirstFailureTick
        actual = $Actual
        message = $Message
    }
}

function Invoke-T24Assertion {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Assertion,
        [Parameter(Mandatory)][object[]]$Frames,
        [Parameter(Mandatory)][Collections.IDictionary]$KindMap
    )

    $type = [string]$Assertion['type']
    switch -CaseSensitive ($type) {
        'seen' {
            $ticks = @(
                for ($index = 0; $index -lt $Frames.Length; $index++) {
                    if (Test-T24Condition `
                            -Condition $Assertion['condition'] `
                            -Frame $Frames[$index] `
                            -KindMap $KindMap) {
                        $index
                    }
                })
            $minimum = [int]$Assertion['minCount']
            $passed = $ticks.Count -ge $minimum
            return New-T24AssertionResult `
                -Assertion $Assertion `
                -Passed $passed `
                -HitTicks ([int[]]$ticks) `
                -FirstFailureTick $(if ($passed) { $null } else { 0 }) `
                -Actual ([ordered]@{ observed = $ticks.Count; minimum = $minimum }) `
                -Message $(if ($passed) { 'Condition was observed.' } else { 'Condition was not observed often enough.' })
        }
        'coincident' {
            $ticks = @(
                for ($index = 0; $index -lt $Frames.Length; $index++) {
                    $matches = $true
                    foreach ($condition in @($Assertion['conditions'])) {
                        if (-not (Test-T24Condition `
                                -Condition $condition `
                                -Frame $Frames[$index] `
                                -KindMap $KindMap)) {
                            $matches = $false
                            break
                        }
                    }
                    if ($matches) { $index }
                })
            $minimum = [int]$Assertion['minCount']
            $passed = $ticks.Count -ge $minimum
            return New-T24AssertionResult `
                -Assertion $Assertion `
                -Passed $passed `
                -HitTicks ([int[]]$ticks) `
                -FirstFailureTick $(if ($passed) { $null } else { 0 }) `
                -Actual ([ordered]@{ observed = $ticks.Count; minimum = $minimum }) `
                -Message $(if ($passed) { 'Conditions coincided.' } else { 'Conditions did not coincide often enough.' })
        }
        'transition' {
            $ticks = @(
                for ($index = 1; $index -lt $Frames.Length; $index++) {
                    if ((Test-T24Condition `
                            -Condition $Assertion['from'] `
                            -Frame $Frames[$index - 1] `
                            -KindMap $KindMap) `
                            -and (Test-T24Condition `
                                -Condition $Assertion['to'] `
                                -Frame $Frames[$index] `
                                -KindMap $KindMap)) {
                        $index
                    }
                })
            $minimum = [int]$Assertion['minCount']
            $passed = $ticks.Count -ge $minimum
            return New-T24AssertionResult `
                -Assertion $Assertion `
                -Passed $passed `
                -HitTicks ([int[]]$ticks) `
                -FirstFailureTick $(if ($passed) { $null } else { 0 }) `
                -Actual ([ordered]@{ observed = $ticks.Count; minimum = $minimum }) `
                -Message $(if ($passed) { 'Adjacent transition was observed.' } else { 'Adjacent transition was not observed often enough.' })
        }
        'delta' {
            $field = [string]$Assertion['field']
            $direction = [string]$Assertion['direction']
            $magnitude = [double]$Assertion['minMagnitude']
            $ticks = @(
                for ($index = 1; $index -lt $Frames.Length; $index++) {
                    $before = [double]$Frames[$index - 1][$field]
                    $after = [double]$Frames[$index][$field]
                    $delta = if ($direction -ceq 'increase') {
                        $after - $before
                    }
                    else {
                        $before - $after
                    }
                    if ($delta -ge $magnitude) { $index }
                })
            $minimum = [int]$Assertion['minCount']
            $passed = $ticks.Count -ge $minimum
            return New-T24AssertionResult `
                -Assertion $Assertion `
                -Passed $passed `
                -HitTicks ([int[]]$ticks) `
                -FirstFailureTick $(if ($passed) { $null } else { 0 }) `
                -Actual ([ordered]@{
                    observed = $ticks.Count
                    minimum = $minimum
                    minMagnitude = $magnitude
                    direction = $direction
                }) `
                -Message $(if ($passed) { 'Required adjacent delta was observed.' } else { 'Required adjacent delta was not observed often enough.' })
        }
        'ordered-match' {
            $field = [string]$Assertion['field']
            $patterns = @($Assertion['patterns'])
            $next = 0
            $completed = [Collections.Generic.List[int]]::new()
            for ($index = 0; $index -lt $Frames.Length; $index++) {
                if ([regex]::IsMatch(
                        [string]$Frames[$index][$field],
                        [string]$patterns[$next],
                        [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
                    $next++
                    if ($next -eq $patterns.Count) {
                        $completed.Add($index)
                        $next = 0
                    }
                }
            }
            $minimum = [int]$Assertion['minCount']
            $passed = $completed.Count -ge $minimum
            return New-T24AssertionResult `
                -Assertion $Assertion `
                -Passed $passed `
                -HitTicks ([int[]]$completed.ToArray()) `
                -FirstFailureTick $(if ($passed) { $null } else { 0 }) `
                -Actual ([ordered]@{
                    completedSequences = $completed.Count
                    minimum = $minimum
                    partialPatternIndex = $next
                }) `
                -Message $(if ($passed) { 'Ordered pattern sequence was observed.' } else { 'Ordered pattern sequence was incomplete.' })
        }
        'all-when' {
            $triggerTicks = [Collections.Generic.List[int]]::new()
            $failureTicks = [Collections.Generic.List[int]]::new()
            for ($index = 0; $index -lt $Frames.Length; $index++) {
                if (-not (Test-T24Condition `
                        -Condition $Assertion['when'] `
                        -Frame $Frames[$index] `
                        -KindMap $KindMap)) {
                    continue
                }
                $triggerTicks.Add($index)
                foreach ($condition in @($Assertion['then'])) {
                    if (-not (Test-T24Condition `
                            -Condition $condition `
                            -Frame $Frames[$index] `
                            -KindMap $KindMap)) {
                        $failureTicks.Add($index)
                        break
                    }
                }
            }
            $minimum = [int]$Assertion['minTriggered']
            $passed = $triggerTicks.Count -ge $minimum `
                -and $failureTicks.Count -eq 0
            return New-T24AssertionResult `
                -Assertion $Assertion `
                -Passed $passed `
                -HitTicks ([int[]]$triggerTicks.ToArray()) `
                -FirstFailureTick $(
                    if ($failureTicks.Count -ne 0) {
                        $failureTicks[0]
                    }
                    elseif ($triggerTicks.Count -lt $minimum) {
                        0
                    }
                    else {
                        $null
                    }) `
                -Actual ([ordered]@{
                    triggered = $triggerTicks.Count
                    minimum = $minimum
                    failedCorrelations = $failureTicks.Count
                    failureTicks = [int[]]$failureTicks.ToArray()
                }) `
                -Message $(if ($passed) { 'Every triggered frame satisfied its correlations.' } else { 'Trigger coverage or a same-frame correlation failed.' })
        }
        default {
            throw "Assertion type '$type' is not supported."
        }
    }
}

$startedUtc = [DateTimeOffset]::UtcNow
$contractHash = $null
$traceHash = $null
$movieHash = $null
$scenarioId = $null
$assertionResults = @()
$errors = [Collections.Generic.List[string]]::new()
$contract = $null
$moviePath = $null
$frameCount = 0

try {
    foreach ($path in @($ContractPath, $TracePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required file is missing: $path"
        }
    }
    $contractHash = Get-T24Sha256File -Path $ContractPath
    $traceHash = Get-T24Sha256File -Path $TracePath
    try {
        $contract = Get-Content -LiteralPath $ContractPath -Raw |
            ConvertFrom-Json -AsHashtable -Depth 50
    }
    catch {
        throw "Scenario contract is not valid JSON: $($_.Exception.Message)"
    }
    if ($contract -isnot [Collections.IDictionary]) {
        throw 'Scenario contract must be a JSON object.'
    }
    Assert-T24ExactKeys `
        -Value $contract `
        -Keys @(
            'schemaVersion',
            'scenarioId',
            'description',
            'fixture',
            'movie',
            'requiredFields',
            'assertions',
            'requiredModes',
            'terminal') `
        -Context 'Scenario contract'
    if ([long]$contract['schemaVersion'] -ne 1) {
        throw 'Scenario contract schemaVersion must be 1.'
    }
    Assert-T24NonEmptyString `
        -Value $contract['scenarioId'] `
        -Context 'scenarioId'
    $scenarioId = [string]$contract['scenarioId']
    if ($scenarioId -cnotmatch '\At24\.[a-z0-9-]+\.v[1-9][0-9]*\z') {
        throw 'scenarioId must match t24.<lowercase-id>.v<positive-integer>.'
    }
    Assert-T24NonEmptyString `
        -Value $contract['description'] `
        -Context 'description'

    foreach ($objectName in @('fixture', 'movie', 'terminal')) {
        if ($contract[$objectName] -isnot [Collections.IDictionary]) {
            throw "$objectName must be a JSON object."
        }
    }
    Assert-T24ExactKeys `
        -Value $contract['fixture'] `
        -Keys @('slot', 'startScene') `
        -Context 'fixture'
    $fixtureSlot = Assert-T24PositiveInt32 `
        -Value $contract['fixture']['slot'] `
        -Context 'fixture.slot'
    if ($fixtureSlot -gt 4) {
        throw 'fixture.slot must be within 1..4.'
    }
    Assert-T24NonEmptyString `
        -Value $contract['fixture']['startScene'] `
        -Context 'fixture.startScene'

    Assert-T24ExactKeys `
        -Value $contract['movie'] `
        -Keys @(
            'path',
            'sha256',
            'expandedTicks',
            'inputMode',
            'digitalActionMask') `
        -Context 'movie'
    Assert-T24NonEmptyString `
        -Value $contract['movie']['path'] `
        -Context 'movie.path'
    if ([IO.Path]::IsPathRooted([string]$contract['movie']['path'])) {
        throw 'movie.path must be repository-relative.'
    }
    $moviePath = [IO.Path]::GetFullPath(
        (Join-Path $repoRoot ([string]$contract['movie']['path'])))
    $repoPrefix = $repoRoot.TrimEnd('\') + '\'
    if (-not $moviePath.StartsWith(
            $repoPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'movie.path resolves outside the repository.'
    }
    if (-not (Test-Path -LiteralPath $moviePath -PathType Leaf)) {
        throw "Frozen movie is missing: $moviePath"
    }
    if ($contract['movie']['sha256'] -isnot [string] `
            -or [string]$contract['movie']['sha256'] `
                -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'movie.sha256 must be lowercase SHA-256.'
    }
    $movieHash = Get-T24Sha256File -Path $moviePath
    if ($movieHash -cne [string]$contract['movie']['sha256']) {
        throw (
            'Frozen movie SHA-256 mismatch; expected=' `
            + [string]$contract['movie']['sha256'] `
            + '; actual=' + $movieHash)
    }
    if ([string]$contract['movie']['inputMode'] -cne 'digital-only') {
        throw 'movie.inputMode must be digital-only.'
    }
    $expandedTicks = Assert-T24PositiveInt32 `
        -Value $contract['movie']['expandedTicks'] `
        -Context 'movie.expandedTicks'
    $actionMask = Assert-T24NonNegativeInt32 `
        -Value $contract['movie']['digitalActionMask'] `
        -Context 'movie.digitalActionMask'
    if (($actionMask -band (-bnot 0x7ff)) -ne 0) {
        throw 'movie.digitalActionMask contains unsupported gameplay bits.'
    }
    $movieFacts = Get-T24MovieFacts -Path $moviePath
    if ([long]$movieFacts.expandedTicks -ne $expandedTicks) {
        throw (
            'Frozen movie expanded tick mismatch; expected=' `
            + $expandedTicks + '; actual=' + $movieFacts.expandedTicks)
    }
    if ([int]$movieFacts.digitalActionMask -ne $actionMask) {
        throw (
            'Frozen movie digital action mask mismatch; expected=' `
            + $actionMask + '; actual=' + $movieFacts.digitalActionMask)
    }

    if ($contract['requiredFields'] -isnot [Collections.IList] `
            -or @($contract['requiredFields']).Count -eq 0) {
        throw 'requiredFields must be a non-empty JSON array.'
    }
    $kindMap = [ordered]@{}
    foreach ($field in @($contract['requiredFields'])) {
        if ($field -isnot [Collections.IDictionary]) {
            throw 'requiredFields entries must be JSON objects.'
        }
        Assert-T24ExactKeys `
            -Value $field `
            -Keys @('key', 'kind') `
            -Context 'requiredFields entry'
        Assert-T24NonEmptyString `
            -Value $field['key'] `
            -Context 'requiredFields.key'
        $key = [string]$field['key']
        $kind = [string]$field['kind']
        if (@(
                'Boolean',
                'Int32',
                'Int64',
                'Float32Bits',
                'Utf8String') -cnotcontains $kind) {
            throw "requiredFields '$key' has unsupported kind '$kind'."
        }
        if ($kindMap.Contains($key)) {
            throw "requiredFields repeats '$key'."
        }
        $kindMap[$key] = $kind
    }

    if ($contract['requiredModes'] -isnot [Collections.IList]) {
        throw 'requiredModes must be a JSON array.'
    }
    $allowedModes = @(
        'VanillaReference',
        'TasPassive',
        'ManualTas',
        'AiTas',
        'SequentialStep',
        'BatchStep')
    $modeSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($mode in @($contract['requiredModes'])) {
        if ($mode -isnot [string] -or $allowedModes -cnotcontains $mode) {
            throw "requiredModes contains unsupported value '$mode'."
        }
        if (-not $modeSet.Add([string]$mode)) {
            throw "requiredModes repeats '$mode'."
        }
    }
    foreach ($mode in $allowedModes) {
        if (-not $modeSet.Contains($mode)) {
            throw "requiredModes must include '$mode'."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($RunMode) `
            -and -not $modeSet.Contains($RunMode)) {
        throw "RunMode '$RunMode' is not declared by the contract."
    }

    Assert-T24ExactKeys `
        -Value $contract['terminal'] `
        -Keys @('allowedScenes', 'maxTicks') `
        -Context 'terminal'
    if ($contract['terminal']['allowedScenes'] `
            -isnot [Collections.IList] `
            -or @($contract['terminal']['allowedScenes']).Count -eq 0) {
        throw 'terminal.allowedScenes must be a non-empty JSON array.'
    }
    $allowedScenes = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($scene in @($contract['terminal']['allowedScenes'])) {
        Assert-T24NonEmptyString `
            -Value $scene `
            -Context 'terminal.allowedScenes entry'
        if (-not $allowedScenes.Add([string]$scene)) {
            throw "terminal.allowedScenes repeats '$scene'."
        }
    }
    $maxTicks = Assert-T24PositiveInt32 `
        -Value $contract['terminal']['maxTicks'] `
        -Context 'terminal.maxTicks'
    if ($maxTicks -lt $expandedTicks) {
        throw 'terminal.maxTicks cannot be lower than movie.expandedTicks.'
    }

    if ($contract['assertions'] -isnot [Collections.IList] `
            -or @($contract['assertions']).Count -eq 0) {
        throw 'assertions must be a non-empty JSON array.'
    }
    $assertionIds = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($assertion in @($contract['assertions'])) {
        if ($assertion -isnot [Collections.IDictionary]) {
            throw 'assertions entries must be JSON objects.'
        }
        foreach ($common in @('id', 'type', 'description')) {
            if (-not $assertion.Contains($common)) {
                throw "Assertion lacks '$common'."
            }
        }
        Assert-T24NonEmptyString `
            -Value $assertion['id'] `
            -Context 'assertion.id'
        Assert-T24NonEmptyString `
            -Value $assertion['description'] `
            -Context "assertion '$($assertion['id'])'.description"
        if (-not $assertionIds.Add([string]$assertion['id'])) {
            throw "Duplicate assertion id '$($assertion['id'])'."
        }
        $assertionContext = "assertion '$($assertion['id'])'"
        switch -CaseSensitive ([string]$assertion['type']) {
            'seen' {
                Assert-T24ExactKeys `
                    -Value $assertion `
                    -Keys @('id', 'type', 'description', 'condition', 'minCount') `
                    -Context $assertionContext
                if ($assertion['condition'] -isnot [Collections.IDictionary]) {
                    throw "$assertionContext.condition must be an object."
                }
                Assert-T24ConditionContract `
                    -Condition $assertion['condition'] `
                    -KindMap $kindMap `
                    -Context "$assertionContext.condition"
                [void](Assert-T24PositiveInt32 `
                    -Value $assertion['minCount'] `
                    -Context "$assertionContext.minCount")
            }
            'coincident' {
                Assert-T24ExactKeys `
                    -Value $assertion `
                    -Keys @('id', 'type', 'description', 'conditions', 'minCount') `
                    -Context $assertionContext
                if ($assertion['conditions'] -isnot [Collections.IList] `
                        -or @($assertion['conditions']).Count -lt 2) {
                    throw "$assertionContext.conditions needs at least two entries."
                }
                for ($i = 0; $i -lt @($assertion['conditions']).Count; $i++) {
                    Assert-T24ConditionContract `
                        -Condition $assertion['conditions'][$i] `
                        -KindMap $kindMap `
                        -Context "$assertionContext.conditions[$i]"
                }
                [void](Assert-T24PositiveInt32 `
                    -Value $assertion['minCount'] `
                    -Context "$assertionContext.minCount")
            }
            'transition' {
                Assert-T24ExactKeys `
                    -Value $assertion `
                    -Keys @('id', 'type', 'description', 'from', 'to', 'minCount') `
                    -Context $assertionContext
                foreach ($name in @('from', 'to')) {
                    if ($assertion[$name] -isnot [Collections.IDictionary]) {
                        throw "$assertionContext.$name must be an object."
                    }
                    Assert-T24ConditionContract `
                        -Condition $assertion[$name] `
                        -KindMap $kindMap `
                        -Context "$assertionContext.$name"
                }
                if ([string]$assertion['from']['field'] `
                        -cne [string]$assertion['to']['field']) {
                    throw "$assertionContext from/to must reference the same field."
                }
                [void](Assert-T24PositiveInt32 `
                    -Value $assertion['minCount'] `
                    -Context "$assertionContext.minCount")
            }
            'delta' {
                Assert-T24ExactKeys `
                    -Value $assertion `
                    -Keys @(
                        'id',
                        'type',
                        'description',
                        'field',
                        'direction',
                        'minMagnitude',
                        'minCount') `
                    -Context $assertionContext
                Assert-T24NonEmptyString `
                    -Value $assertion['field'] `
                    -Context "$assertionContext.field"
                $field = [string]$assertion['field']
                if (-not $kindMap.Contains($field) `
                        -or [string]$kindMap[$field] `
                            -notin @('Int32', 'Int64', 'Float32Bits')) {
                    throw "$assertionContext.field must be a required numeric field."
                }
                if ([string]$assertion['direction'] `
                        -notin @('increase', 'decrease')) {
                    throw "$assertionContext.direction must be increase or decrease."
                }
                if (-not (Test-T24ExpectedType `
                        -Kind 'Float32Bits' `
                        -Value $assertion['minMagnitude']) `
                        -or [double]$assertion['minMagnitude'] -le 0) {
                    throw "$assertionContext.minMagnitude must be a positive number."
                }
                [void](Assert-T24PositiveInt32 `
                    -Value $assertion['minCount'] `
                    -Context "$assertionContext.minCount")
            }
            'ordered-match' {
                Assert-T24ExactKeys `
                    -Value $assertion `
                    -Keys @('id', 'type', 'description', 'field', 'patterns', 'minCount') `
                    -Context $assertionContext
                $field = [string]$assertion['field']
                if (-not $kindMap.Contains($field) `
                        -or [string]$kindMap[$field] -cne 'Utf8String') {
                    throw "$assertionContext.field must be a required Utf8String field."
                }
                if ($assertion['patterns'] -isnot [Collections.IList] `
                        -or @($assertion['patterns']).Count -lt 2) {
                    throw "$assertionContext.patterns needs at least two entries."
                }
                foreach ($pattern in @($assertion['patterns'])) {
                    if ($pattern -isnot [string] `
                            -or -not ([string]$pattern).StartsWith('\A') `
                            -or -not ([string]$pattern).EndsWith('\z')) {
                        throw "$assertionContext patterns require full \\A...\\z anchoring."
                    }
                    try {
                        [void][regex]::new(
                            [string]$pattern,
                            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
                    }
                    catch {
                        throw "$assertionContext contains an invalid pattern."
                    }
                }
                [void](Assert-T24PositiveInt32 `
                    -Value $assertion['minCount'] `
                    -Context "$assertionContext.minCount")
            }
            'all-when' {
                Assert-T24ExactKeys `
                    -Value $assertion `
                    -Keys @('id', 'type', 'description', 'when', 'then', 'minTriggered') `
                    -Context $assertionContext
                if ($assertion['when'] -isnot [Collections.IDictionary]) {
                    throw "$assertionContext.when must be an object."
                }
                Assert-T24ConditionContract `
                    -Condition $assertion['when'] `
                    -KindMap $kindMap `
                    -Context "$assertionContext.when"
                if ($assertion['then'] -isnot [Collections.IList] `
                        -or @($assertion['then']).Count -eq 0) {
                    throw "$assertionContext.then must be a non-empty array."
                }
                for ($i = 0; $i -lt @($assertion['then']).Count; $i++) {
                    Assert-T24ConditionContract `
                        -Condition $assertion['then'][$i] `
                        -KindMap $kindMap `
                        -Context "$assertionContext.then[$i]"
                }
                [void](Assert-T24PositiveInt32 `
                    -Value $assertion['minTriggered'] `
                    -Context "$assertionContext.minTriggered")
            }
            default {
                throw "$assertionContext has unsupported type '$($assertion['type'])'."
            }
        }
    }

    $frames = @(Read-T24Trace -Path $TracePath -KindMap $kindMap)
    $frameCount = $frames.Count
    if ($frameCount -ne $expandedTicks) {
        throw (
            'Trace frame count does not match movie.expandedTicks; expected=' `
            + $expandedTicks + '; actual=' + $frameCount)
    }
    if ($frameCount -gt $maxTicks) {
        throw 'Trace exceeds terminal.maxTicks.'
    }
    if ([string]$frames[0]['scene.name'] `
            -cne [string]$contract['fixture']['startScene']) {
        throw (
            'Trace first scene does not match fixture.startScene; expected=' `
            + [string]$contract['fixture']['startScene'] `
            + '; actual=' + [string]$frames[0]['scene.name'])
    }
    $lastScene = [string]$frames[$frameCount - 1]['scene.name']
    if (-not $allowedScenes.Contains($lastScene)) {
        throw "Trace terminal scene '$lastScene' is not allowed."
    }

    $assertionResults = @(
        foreach ($assertion in @($contract['assertions'])) {
            Invoke-T24Assertion `
                -Assertion $assertion `
                -Frames $frames `
                -KindMap $kindMap
        })
    foreach ($result in $assertionResults) {
        if ([string]$result.verdict -cne 'PASS') {
            $errors.Add(
                "Assertion '$($result.id)' failed: $($result.message)")
        }
    }
}
catch {
    $errors.Add($_.Exception.Message)
}

$verdict = if ($errors.Count -eq 0) { 'PASS' } else { 'FAIL' }
$report = [ordered]@{
    schemaVersion = 1
    scenarioId = $scenarioId
    verdict = $verdict
    runMode = if ([string]::IsNullOrWhiteSpace($RunMode)) { $null } else { $RunMode }
    startedUtc = $startedUtc.ToString('O')
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    inputs = [ordered]@{
        contractPath = $ContractPath
        contractSha256 = $contractHash
        moviePath = $moviePath
        movieSha256 = $movieHash
        tracePath = $TracePath
        traceSha256 = $traceHash
        frameCount = $frameCount
    }
    assertions = $assertionResults
    errors = [string[]]$errors.ToArray()
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json = $report | ConvertTo-Json -Depth 50
    [IO.File]::WriteAllText(
        $OutputPath,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

if ($verdict -cne 'PASS' -and -not $NoThrow) {
    throw ('T24 scenario coverage failed: ' + ($errors -join ' | '))
}

[pscustomobject]$report
