[CmdletBinding()]
param(
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$measureScript = Join-Path `
    $PSScriptRoot `
    'Measure-T24ReferenceEnvelope.ps1'
if (-not (Test-Path -LiteralPath $measureScript -PathType Leaf)) {
    throw "Phase canonicalizer source is missing: $measureScript"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path `
        $repoRoot `
        ('artifacts\vanilla-equivalence\t24-phase-normalization-' `
            + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') `
            + '\phase-normalization-self-test.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    throw "Phase-normalization output already exists: $OutputPath"
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $measureScript,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw 'Cannot parse the phase canonicalizer source.'
}
$function = $ast.Find(
    {
        param($node)
        $node -is `
            [Management.Automation.Language.FunctionDefinitionAst] `
            -and $node.Name -eq 'Get-CanonicalPhaseTimeline'
    },
    $true)
if ($null -eq $function) {
    throw 'Get-CanonicalPhaseTimeline is missing.'
}
Invoke-Expression $function.Extent.Text

function New-PhaseEvent {
    param(
        [int]$FrameIndex,
        [string]$Phase,
        [long]$VisualTick,
        [long]$UpdateTick,
        [bool]$Held,
        [bool]$Pressed,
        [bool]$Released,
        [long]$ActionSetTick,
        [long]$ActionTick
    )

    return [ordered]@{
        schemaVersion = 1
        sequence = 0
        frameIndex = $FrameIndex
        phase = $Phase
        held = $Held
        pressed = $Pressed
        released = $Released
        gameObject = ''
        fsm = ''
        state = ''
        visualTick = $VisualTick
        updateTick = $UpdateTick
        actionSetTick = $ActionSetTick
        actionTick = $ActionTick
    }
}

function Write-PhaseTrace {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object[]]$Events
    )

    @($Events | ForEach-Object { $_ | ConvertTo-Json -Compress }) |
        Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Get-CanonicalText {
    param([Parameter(Mandatory)][string]$Path)

    $timeline = Get-CanonicalPhaseTimeline -Path $Path
    if (-not [bool]$timeline.valid) {
        throw "Phase fixture is invalid: $($timeline.failure)"
    }
    return @($timeline.events) -join "`n"
}

$baseEvents = @(
    (New-PhaseEvent 0 'PlayerActionSet.before' 50 100 $true $true $false 99 99),
    (New-PhaseEvent 0 'PlayerActionSet.after' 50 100 $true $true $false 100 100),
    (New-PhaseEvent 1 'PlayerActionSet.before' 51 101 $true $false $false 100 100),
    (New-PhaseEvent 1 'PlayerActionSet.after' 51 101 $true $false $false 101 101)
)

$temporaryRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ('hktas-phase-normalization-' + [Guid]::NewGuid().ToString('N'))
$temporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $temporaryRoot.StartsWith(
        $tempPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Phase-normalization temporary root escaped the system temp directory.'
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $basePath = Join-Path $temporaryRoot 'base.jsonl'
    $frameOnlyPath = Join-Path $temporaryRoot 'frame-only.jsonl'
    $visualPath = Join-Path $temporaryRoot 'visual.jsonl'
    $phasePath = Join-Path $temporaryRoot 'phase.jsonl'
    $inputPath = Join-Path $temporaryRoot 'input.jsonl'
    Write-PhaseTrace -Path $basePath -Events $baseEvents

    $frameOnly = @($baseEvents | ForEach-Object { [ordered]@{} + $_ })
    $frameOnly[2]['frameIndex'] = 0
    $frameOnly[3]['frameIndex'] = 0
    Write-PhaseTrace -Path $frameOnlyPath -Events $frameOnly

    $visual = @($baseEvents | ForEach-Object { [ordered]@{} + $_ })
    $visual[2]['visualTick'] = 52
    $visual[3]['visualTick'] = 52
    Write-PhaseTrace -Path $visualPath -Events $visual

    $phase = @($baseEvents | ForEach-Object { [ordered]@{} + $_ })
    $phase[2]['phase'] = 'Observer.LateUpdate'
    Write-PhaseTrace -Path $phasePath -Events $phase

    $input = @($baseEvents | ForEach-Object { [ordered]@{} + $_ })
    $input[2]['pressed'] = $true
    Write-PhaseTrace -Path $inputPath -Events $input

    $base = Get-CanonicalText -Path $basePath
    $frameOnlyEquivalent =
        $base -ceq (Get-CanonicalText -Path $frameOnlyPath)
    $visualMutationRejected =
        $base -cne (Get-CanonicalText -Path $visualPath)
    $phaseMutationRejected =
        $base -cne (Get-CanonicalText -Path $phasePath)
    $inputMutationRejected =
        $base -cne (Get-CanonicalText -Path $inputPath)
    $pass = $frameOnlyEquivalent `
        -and $visualMutationRejected `
        -and $phaseMutationRejected `
        -and $inputMutationRejected

    $outputDirectory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force |
        Out-Null
    [ordered]@{
        schemaVersion = 1
        verdict = if ($pass) { 'PASS' } else { 'FAIL' }
        canonicalizerScript = $measureScript
        canonicalizerScriptSha256 = (Get-FileHash `
            -LiteralPath $measureScript `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        derivedFrameIndexEquivalent = $frameOnlyEquivalent
        visualTickMutationRejected = $visualMutationRejected
        phaseMutationRejected = $phaseMutationRejected
        inputEdgeMutationRejected = $inputMutationRejected
    } |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
    if (-not $pass) {
        throw 'Phase-normalization self-test failed.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "T24 phase normalization self-test passed: $OutputPath"
