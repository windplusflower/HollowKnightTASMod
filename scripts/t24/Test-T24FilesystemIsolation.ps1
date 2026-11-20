[CmdletBinding()]
param([string]$OutputDirectory = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
. (Join-Path $PSScriptRoot 'T24FilesystemIsolation.ps1')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path `
        $repoRoot `
        ('artifacts\vanilla-equivalence\t24-filesystem-isolation-selftest-' `
            + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd('\', '/') `
    + [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith(
        $repoPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Filesystem-isolation self-test output must stay in the repository.'
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Self-test output already exists: $OutputDirectory"
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$casesRoot = Join-Path $OutputDirectory 'cases'
New-Item -ItemType Directory -Path $casesRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$results = [Collections.Generic.List[object]]::new()

function Write-CaseFile {
    param([string]$Path, [string]$Text)

    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Assert-Case {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

try {
    $case = Join-Path $casesRoot 'destination-absent'
    $source = Join-Path $case 'source'
    $destination = Join-Path $case 'destination'
    Write-CaseFile (Join-Path $source 'one.txt') 'one'
    Write-CaseFile (Join-Path $source 'sub\two.txt') 'two'
    Move-T24PathWithRetry -Source $source -Destination $destination
    Assert-Case (-not (Test-Path $source)) 'Normal move left its source.'
    Assert-Case (Test-Path (Join-Path $destination 'one.txt')) `
        'Normal move lost a root file.'
    Assert-Case (Test-Path (Join-Path $destination 'sub\two.txt')) `
        'Normal move lost a nested file.'
    $results.Add([ordered]@{ caseId = 'destination-absent'; verdict = 'PASS' })

    $case = Join-Path $casesRoot 'partial-destination'
    $source = Join-Path $case 'source'
    $destination = Join-Path $case 'destination'
    Write-CaseFile (Join-Path $source 'source-only.txt') 'source-only'
    Write-CaseFile (Join-Path $source 'shared.txt') 'same'
    Write-CaseFile (Join-Path $source 'sub\source.txt') 'source-sub'
    Write-CaseFile (Join-Path $destination 'destination-only.txt') `
        'destination-only'
    Write-CaseFile (Join-Path $destination 'shared.txt') 'same'
    Write-CaseFile (Join-Path $destination 'sub\destination.txt') `
        'destination-sub'
    Move-T24PathWithRetry -Source $source -Destination $destination
    Assert-Case (-not (Test-Path $source)) `
        'Partial-destination recovery left its source.'
    foreach ($relative in @(
            'source-only.txt',
            'destination-only.txt',
            'shared.txt',
            'sub\source.txt',
            'sub\destination.txt')) {
        Assert-Case (Test-Path (Join-Path $destination $relative)) `
            "Partial-destination recovery lost $relative."
    }
    Assert-Case (-not (Test-Path (Join-Path $destination 'source'))) `
        'Partial-destination recovery nested the source directory.'
    $results.Add([ordered]@{ caseId = 'partial-destination'; verdict = 'PASS' })

    $case = Join-Path $casesRoot 'conflicting-file'
    $source = Join-Path $case 'source'
    $destination = Join-Path $case 'destination'
    Write-CaseFile (Join-Path $source 'conflict.txt') 'source'
    Write-CaseFile (Join-Path $destination 'conflict.txt') 'destination'
    $conflictRejected = $false
    try {
        Move-T24PathWithRetry `
            -Source $source `
            -Destination $destination `
            -TimeoutSeconds 1
    }
    catch {
        $conflictRejected = $_.Exception.Message `
            -match 'conflicting files'
    }
    Assert-Case $conflictRejected `
        'Conflicting duplicate files were not rejected fail-closed.'
    Assert-Case ([IO.File]::ReadAllText(
            (Join-Path $source 'conflict.txt')) -ceq 'source') `
        'Conflict rejection changed the source file.'
    Assert-Case ([IO.File]::ReadAllText(
            (Join-Path $destination 'conflict.txt')) -ceq 'destination') `
        'Conflict rejection changed the destination file.'
    $results.Add([ordered]@{ caseId = 'conflicting-file'; verdict = 'PASS' })

    $report = [ordered]@{
        schemaVersion = 1
        testId = 't24-filesystem-isolation-self-test-v1'
        verdict = 'PASS'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        helperSha256 = (Get-FileHash `
            -LiteralPath (Join-Path $PSScriptRoot 'T24FilesystemIsolation.ps1') `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        cases = $results
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputDirectory 'filesystem-isolation-self-test.json'),
        ($report | ConvertTo-Json -Depth 10) + "`n",
        $utf8)
}
finally {
    if (Test-Path -LiteralPath $casesRoot) {
        $casesFull = [IO.Path]::GetFullPath($casesRoot)
        $outputPrefix = $OutputDirectory.TrimEnd('\', '/') `
            + [IO.Path]::DirectorySeparatorChar
        if (-not $casesFull.StartsWith(
                $outputPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing self-test cleanup outside its output directory.'
        }
        Remove-Item -LiteralPath $casesFull -Recurse -Force
    }
}

Write-Output "T24 filesystem-isolation self-test verdict=PASS"
Write-Output (
    'Report: ' `
    + (Join-Path $OutputDirectory 'filesystem-isolation-self-test.json'))
