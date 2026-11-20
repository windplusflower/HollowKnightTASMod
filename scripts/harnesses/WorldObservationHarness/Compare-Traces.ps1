[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $BaselineReport,
    [Parameter(Mandatory = $true, Position = 1)]
    [string] $ObserveReport,
    [string] $Output = (Join-Path (Get-Location) 'artifacts/world-observation-trace-compare.json')
)

$ErrorActionPreference = 'Stop'
$TraceFields = @(
    'scene', 'heroX', 'heroY', 'heroHealth',
    'bossHp', 'bossX', 'bossY', 'bossDead',
    'deltaTime', 'rngSha256'
)
$TimingFields = @('nativeFrame', 'time', 'fixedTime', 'frameCount')
$ComparedFields = @($TraceFields + $TimingFields)
$RequiredFields = @('movieFrame') + $TraceFields + $TimingFields

function Read-RequiredReport([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Report not found: $Path"
    }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.success -ne $true) { throw "Acceptance run did not succeed: $Path" }
    foreach ($name in @('runId', 'shadowRoot', 'runtimeAssemblySha256', 'coreAssemblySha256')) {
        if ([string]::IsNullOrWhiteSpace([string] $report.$name)) {
            throw "Report $Path is missing $name."
        }
    }
    return $report
}

function Resolve-TracePath($Report, [string] $ReportPath) {
    $shadow = [IO.Path]::GetFullPath([string] $Report.shadowRoot)
    $runId = [string] $Report.runId
    if ($runId -notmatch '^[A-Za-z0-9._:-]{1,96}$') {
        throw "Unsafe runId in $ReportPath."
    }
    $path = Join-Path $shadow (Join-Path 'HollowKnightTAS\sessions' ("full-run-{0}\boss-trace.csv" -f $runId))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Boss trace not found for $($ReportPath): $path"
    }
    return [IO.Path]::GetFullPath($path)
}

function Read-Trace([string] $Path) {
    $rows = @(Import-Csv -LiteralPath $Path)
    if ($rows.Count -eq 0) { throw "Boss trace is empty: $Path" }
    $headers = @($rows[0].PSObject.Properties.Name)
    foreach ($name in $RequiredFields) {
        if ($headers -notcontains $name) { throw "Boss trace $Path is missing column $name." }
    }
    $invalid = @($rows | Where-Object { [string]::IsNullOrWhiteSpace([string] $_.movieFrame) })
    if ($invalid.Count -gt 0) { throw "Boss trace $Path contains a row without movieFrame." }
    $duplicates = @($rows | Group-Object movieFrame | Where-Object { $_.Count -gt 1 })
    return [pscustomobject]@{ Path = $Path; Rows = $rows; Duplicates = $duplicates }
}

function Convert-Number([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $number = 0.0
    if ([double]::TryParse($Value, [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref] $number)) { return $number }
    return $null
}

function New-TraceMap($Trace) {
    $map = @{}
    foreach ($row in $Trace.Rows) { $map[[string] $row.movieFrame] = $row }
    return $map
}

$result = [ordered]@{
    baselineReport = [IO.Path]::GetFullPath($BaselineReport)
    observeReport = [IO.Path]::GetFullPath($ObserveReport)
    output = [IO.Path]::GetFullPath($Output)
    comparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    hashMatch = $false
    rowSetMatch = $false
    duplicateMovieFrames = @()
    acceptance = [ordered]@{
        semanticFields = $TraceFields
        informationalTimingFields = $TimingFields
        requiresMatchingAssemblyHashes = $true
        requiresIdenticalMovieFrameRows = $true
        timingDifferencesAllowed = $true
    }
    semanticDifferences = @()
    timingDifferences = @()
    differences = @()
    rows = @()
    semanticEqual = $false
    allColumnsEqual = $false
    success = $false
    equal = $false
}

try {
    $baseline = Read-RequiredReport $BaselineReport
    $observe = Read-RequiredReport $ObserveReport
    $result.baseline = [ordered]@{
        runId = [string] $baseline.runId
        shadowRoot = [string] $baseline.shadowRoot
        runtimeAssemblySha256 = [string] $baseline.runtimeAssemblySha256
        coreAssemblySha256 = [string] $baseline.coreAssemblySha256
    }
    $result.observe = [ordered]@{
        runId = [string] $observe.runId
        shadowRoot = [string] $observe.shadowRoot
        runtimeAssemblySha256 = [string] $observe.runtimeAssemblySha256
        coreAssemblySha256 = [string] $observe.coreAssemblySha256
    }
    $result.hashMatch = ([string] $baseline.runtimeAssemblySha256 -ceq [string] $observe.runtimeAssemblySha256) -and
        ([string] $baseline.coreAssemblySha256 -ceq [string] $observe.coreAssemblySha256)

    $baselineTrace = Read-Trace (Resolve-TracePath $baseline $BaselineReport)
    $observeTrace = Read-Trace (Resolve-TracePath $observe $ObserveReport)
    $result.tracePaths = @($baselineTrace.Path, $observeTrace.Path)
    $duplicateFrames = @($baselineTrace.Duplicates.Name + $observeTrace.Duplicates.Name | Sort-Object -Unique)
    $result.duplicateMovieFrames = $duplicateFrames

    $baselineMap = New-TraceMap $baselineTrace
    $observeMap = New-TraceMap $observeTrace
    $baselineKeys = @($baselineMap.Keys | Sort-Object)
    $observeKeys = @($observeMap.Keys | Sort-Object)
    $missingInObserve = @($baselineKeys | Where-Object { -not $observeMap.ContainsKey($_) })
    $missingInBaseline = @($observeKeys | Where-Object { -not $baselineMap.ContainsKey($_) })
    $result.rowSetMatch = ($missingInObserve.Count -eq 0 -and $missingInBaseline.Count -eq 0 -and
        $baselineKeys.Count -eq $observeKeys.Count -and $duplicateFrames.Count -eq 0)
    $result.rowSet = [ordered]@{
        baselineCount = $baselineKeys.Count
        observeCount = $observeKeys.Count
        missingInObserve = $missingInObserve
        missingInBaseline = $missingInBaseline
    }

    $allKeys = @($baselineKeys + $observeKeys | Sort-Object -Unique)
    foreach ($movieFrame in $allKeys) {
        $left = $baselineMap[$movieFrame]
        $right = $observeMap[$movieFrame]
        $semanticFieldDifferences = @()
        foreach ($field in $TraceFields) {
            $leftValue = if ($null -eq $left) { $null } else { [string] $left.$field }
            $rightValue = if ($null -eq $right) { $null } else { [string] $right.$field }
            if ($leftValue -cne $rightValue) {
                $semanticFieldDifferences += [ordered]@{ field = $field; baseline = $leftValue; observe = $rightValue }
            }
        }
        $timingFieldDifferences = @()
        foreach ($field in $TimingFields) {
            $leftValue = if ($null -eq $left) { $null } else { [string] $left.$field }
            $rightValue = if ($null -eq $right) { $null } else { [string] $right.$field }
            if ($leftValue -cne $rightValue) {
                $timingFieldDifferences += [ordered]@{ field = $field; baseline = $leftValue; observe = $rightValue }
            }
        }
        $fieldDifferences = @($semanticFieldDifferences + $timingFieldDifferences)
        $nativeLeft = if ($null -eq $left) { $null } else { Convert-Number ([string] $left.nativeFrame) }
        $nativeRight = if ($null -eq $right) { $null } else { Convert-Number ([string] $right.nativeFrame) }
        $timeLeft = if ($null -eq $left) { $null } else { Convert-Number ([string] $left.time) }
        $timeRight = if ($null -eq $right) { $null } else { Convert-Number ([string] $right.time) }
        $fixedLeft = if ($null -eq $left) { $null } else { Convert-Number ([string] $left.fixedTime) }
        $fixedRight = if ($null -eq $right) { $null } else { Convert-Number ([string] $right.fixedTime) }
        $row = [ordered]@{
            movieFrame = $movieFrame
            nativeFrame = [ordered]@{ baseline = $nativeLeft; observe = $nativeRight; delta = if ($null -ne $nativeLeft -and $null -ne $nativeRight) { $nativeRight - $nativeLeft } else { $null } }
            time = [ordered]@{ baseline = $timeLeft; observe = $timeRight; delta = if ($null -ne $timeLeft -and $null -ne $timeRight) { $timeRight - $timeLeft } else { $null } }
            fixedTime = [ordered]@{ baseline = $fixedLeft; observe = $fixedRight; delta = if ($null -ne $fixedLeft -and $null -ne $fixedRight) { $fixedRight - $fixedLeft } else { $null } }
            deltaTime = [ordered]@{ baseline = if ($null -eq $left) { $null } else { [string] $left.deltaTime }; observe = if ($null -eq $right) { $null } else { [string] $right.deltaTime } }
            frameCount = [ordered]@{ baseline = if ($null -eq $left) { $null } else { [string] $left.frameCount }; observe = if ($null -eq $right) { $null } else { [string] $right.frameCount } }
            semanticDifferences = $semanticFieldDifferences
            timingDifferences = $timingFieldDifferences
            differences = $fieldDifferences
            semanticEqual = ($semanticFieldDifferences.Count -eq 0 -and $null -ne $left -and $null -ne $right)
            allColumnsEqual = ($fieldDifferences.Count -eq 0 -and $null -ne $left -and $null -ne $right)
        }
        $result.rows += $row
        if ($semanticFieldDifferences.Count -gt 0) { $result.semanticDifferences += $row }
        if ($timingFieldDifferences.Count -gt 0) { $result.timingDifferences += $row }
        if ($fieldDifferences.Count -gt 0) { $result.differences += $row }
    }
    $result.semanticEqual = ($result.hashMatch -and $result.rowSetMatch -and $result.semanticDifferences.Count -eq 0)
    $result.allColumnsEqual = ($result.hashMatch -and $result.rowSetMatch -and $result.differences.Count -eq 0)
    $result.success = $result.semanticEqual
    $result.equal = $result.semanticEqual
}
catch {
    $result.error = $_.Exception.Message
    $result.success = $false
    $result.semanticEqual = $false
    $result.allColumnsEqual = $false
    $result.equal = $false
}

$outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
if ($outputDirectory) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
if ($result.success) {
    Write-Host "Trace comparison PASS: $Output"
    exit 0
}
$errorText = if ($result.error) { "Trace comparison FAIL: $($result.error)" } else { "Trace comparison FAIL: hashes/rows/values differ." }
[Console]::Error.WriteLine($errorText)
exit 2
