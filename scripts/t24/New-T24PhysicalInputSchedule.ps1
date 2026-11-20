[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ScenarioContractPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$AuditPath = '',

    [ValidateRange(0, 10000)]
    [int]$PrefixTicks = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 physical input schedule generation requires PowerShell 7.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$ScenarioContractPath = [IO.Path]::GetFullPath($ScenarioContractPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([string]::IsNullOrWhiteSpace($AuditPath)) {
    $AuditPath = $OutputPath + '.audit.json'
}
$AuditPath = [IO.Path]::GetFullPath($AuditPath)
foreach ($path in @($OutputPath, $AuditPath)) {
    if (-not $path.StartsWith(
            $repoPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated schedule artifacts must remain inside the repository.'
    }
    if (Test-Path -LiteralPath $path) {
        throw "Generated schedule artifact already exists: $path"
    }
}
if (-not (Test-Path `
        -LiteralPath $ScenarioContractPath `
        -PathType Leaf)) {
    throw "Scenario contract is missing: $ScenarioContractPath"
}

function Get-T24Sha256Text {
    param([Parameter(Mandatory)][string]$Text)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Text))
    ).ToLowerInvariant()
}

function Get-T24Int32Hex {
    param([Parameter(Mandatory)][int]$Value)

    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

function New-T24InputField {
    param(
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][int]$Value
    )

    return [ordered]@{
        key = $Key
        kind = 'Int32'
        canonicalHex = Get-T24Int32Hex -Value $Value
        displayValue = $Value.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
        comparable = $true
    }
}

try {
    $contract = Get-Content -LiteralPath $ScenarioContractPath -Raw |
        ConvertFrom-Json -AsHashtable -Depth 50
}
catch {
    throw "Scenario contract is not valid JSON: $($_.Exception.Message)"
}
if ($contract -isnot [Collections.IDictionary] `
        -or [long]$contract['schemaVersion'] -ne 1 `
        -or [string]$contract['movie']['inputMode'] -cne 'digital-only') {
    throw 'Scenario contract does not declare schema v1 digital-only input.'
}
if ([string]$contract['scenarioId'] -cnotmatch '\At24\.[a-z0-9-]+\.v[1-9][0-9]*\z') {
    throw 'scenarioId must match t24.<lowercase-id>.v<positive-integer>.'
}
$movieRelative = [string]$contract['movie']['path']
if ([IO.Path]::IsPathRooted($movieRelative)) {
    throw 'Scenario movie path must be repository-relative.'
}
$moviePath = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot $movieRelative))
if (-not $moviePath.StartsWith(
        $repoPrefix,
        [StringComparison]::OrdinalIgnoreCase) `
        -or -not (Test-Path -LiteralPath $moviePath -PathType Leaf)) {
    throw 'Scenario movie path is missing or outside the repository.'
}
$movieSha256 = (Get-FileHash `
    -LiteralPath $moviePath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
if ($movieSha256 -cne [string]$contract['movie']['sha256']) {
    throw 'Scenario movie SHA-256 does not match the frozen contract.'
}

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
$heldSchedule = [Collections.Generic.List[int]]::new()
$usedMask = 0
$lineNumber = 0
foreach ($raw in [IO.File]::ReadAllLines($moviePath)) {
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
            -or $line -cmatch `
                '\Amanifest-sha256 [0-9a-f]{64}\z') {
        continue
    }
    if ($line -cnotmatch `
            '\Aframes ([1-9][0-9]*) hold=([a-z,\-]+)\z') {
        throw "Movie line $lineNumber is not a frozen digital frame run: $line"
    }
    $count = [int]$Matches[1]
    $hold = $Matches[2]
    $held = 0
    if ($hold -cne '-') {
        $seen = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($action in $hold.Split(',')) {
            if (-not $actionBits.ContainsKey($action)) {
                throw "Movie line $lineNumber uses unknown action '$action'."
            }
            if (-not $seen.Add($action)) {
                throw "Movie line $lineNumber repeats action '$action'."
            }
            $held = $held -bor [int]$actionBits[$action]
        }
    }
    $usedMask = $usedMask -bor $held
    for ($index = 0; $index -lt $count; $index++) {
        $heldSchedule.Add($held)
    }
}

$expectedTicks = [int]$contract['movie']['expandedTicks']
$expectedMask = [int]$contract['movie']['digitalActionMask']
if ($heldSchedule.Count -ne $expectedTicks `
        -or $usedMask -ne $expectedMask) {
    throw (
        'Expanded movie input differs from its contract; ticks=' `
        + $heldSchedule.Count + '/' + $expectedTicks `
        + '; mask=' + $usedMask + '/' + $expectedMask)
}

$outputTickCount = if ($PrefixTicks -eq 0) {
    $heldSchedule.Count
}
else {
    $PrefixTicks
}
if ($outputTickCount -gt $heldSchedule.Count) {
    throw (
        'Requested physical-input prefix exceeds the expanded movie: ' `
        + $outputTickCount + ' > ' + $heldSchedule.Count)
}

$lines = [Collections.Generic.List[string]]::new($outputTickCount)
$previous = 0
$outputUsedMask = 0
for ($tick = 0; $tick -lt $outputTickCount; $tick++) {
    $held = $heldSchedule[$tick]
    $outputUsedMask = $outputUsedMask -bor $held
    $pressed = $held -band (-bnot $previous) -band 0x7ff
    $released = $previous -band (-bnot $held) -band 0x7ff
    $axisX = 0
    if (($held -band $actionBits['right']) -ne 0) {
        $axisX += 10000
    }
    if (($held -band $actionBits['left']) -ne 0) {
        $axisX -= 10000
    }
    $axisY = 0
    if (($held -band $actionBits['up']) -ne 0) {
        $axisY += 10000
    }
    if (($held -band $actionBits['down']) -ne 0) {
        $axisY -= 10000
    }
    $fields = @(
        New-T24InputField -Key 'input.axisX' -Value $axisX
        New-T24InputField -Key 'input.axisY' -Value $axisY
        New-T24InputField -Key 'input.held' -Value $held
        New-T24InputField -Key 'input.pressed' -Value $pressed
        New-T24InputField -Key 'input.released' -Value $released)
    $canonicalFields = @(
        foreach ($field in $fields) {
            [ordered]@{
                key = [string]$field.key
                kind = [string]$field.kind
                canonicalHex = [string]$field.canonicalHex
            }
        })
    $canonical = [ordered]@{
        logicalTick = [long]$tick
        fields = $canonicalFields
    } | ConvertTo-Json -Compress -Depth 10
    $frame = [ordered]@{
        schemaVersion = 1
        sequence = [long]($tick + 1)
        logicalTick = [long]$tick
        comparisonSha256 = Get-T24Sha256Text -Text $canonical
        fields = $fields
    }
    $frameJson = $frame | ConvertTo-Json -Compress -Depth 10
    $lines.Add($frameJson)
    $previous = $held
}

foreach ($path in @($OutputPath, $AuditPath)) {
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
}
[IO.File]::WriteAllLines(
    $OutputPath,
    $lines,
    [Text.UTF8Encoding]::new($false))
$scheduleSha256 = (Get-FileHash `
    -LiteralPath $OutputPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$audit = [ordered]@{
    schemaVersion = 1
    generator = $PSCommandPath
    generatorSha256 = (Get-FileHash `
        -LiteralPath $PSCommandPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    scenarioId = [string]$contract['scenarioId']
    contractPath = $ScenarioContractPath
    contractSha256 = (Get-FileHash `
        -LiteralPath $ScenarioContractPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    moviePath = $moviePath
    movieSha256 = $movieSha256
    outputPath = $OutputPath
    outputSha256 = $scheduleSha256
    sourceExpandedTicks = $heldSchedule.Count
    prefixTicks = $PrefixTicks
    frameCount = $outputTickCount
    digitalActionMask = $outputUsedMask
    firstHeld = $heldSchedule[0]
    finalHeld = $heldSchedule[$outputTickCount - 1]
    edgeDerivation = 'pressed=held&~previous;released=previous&~held'
    axisDerivation =
        'axisX=10000*(right-left);axisY=10000*(up-down)'
    visualRecognitionUsed = $false
    gameProcessUsed = $false
}
[IO.File]::WriteAllText(
    $AuditPath,
    ($audit | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

[pscustomobject]$audit
