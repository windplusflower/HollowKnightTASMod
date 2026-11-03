param(
    [Parameter(Mandatory=$true)][string]$UnityPlayerPath,
    [string]$CompilerPath = 'gcc'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$player = (Resolve-Path -LiteralPath $UnityPlayerPath).Path
$cc = (Get-Command $CompilerPath -CommandType Application -ErrorAction Stop).Source
$build = Join-Path $repo ('scratch/resident-director-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $build
$priorPath = $env:PATH
try {
    $env:PATH = [IO.Path]::GetDirectoryName($cc) + [IO.Path]::PathSeparator + $priorPath
    function Build([string]$file, [string]$artifact, [string[]]$options) {
        & $cc -std=c11 -O2 -Wall -Wextra -Werror (Join-Path $repo "tests/native/$file") -o (Join-Path $build $artifact) @options
        if ($LASTEXITCODE) { throw "Compilation of $artifact failed ($LASTEXITCODE)." }
    }
    Build 'ResidentDirectorProducerHarness.c' 'director.exe' @('-municode')
    Build 'ResidentDirectorEntryHarness.c' 'director-entry.exe' @('-municode')
    Build 'ResidentDirectorJobHarness.c' 'director-jobs.exe' @('-municode')
    Build 'ResidentDirectorLineageHarness.c' 'director-lineage.exe' @('-municode')
    Build 'ResidentDirectorStartupHarness.c' 'director-startup.exe' @('-municode')
    Build 'ResidentDirectorStartupHarness.c' 'director-startup-probe.dll' @('-shared','-DHKTAS_DIRECTOR_STARTUP_DLL')
    & (Join-Path $build 'director-startup.exe') (Join-Path $build 'director-startup-probe.dll')
    if ($LASTEXITCODE) { throw "Protected startup fixture failed ($LASTEXITCODE)." }
    foreach ($case in @('v1-bypass','entries')) {
        & (Join-Path $build 'director-entry.exe') $player $case
        if ($LASTEXITCODE) { throw "Director entry fixture $case failed ($LASTEXITCODE)." }
    }
    foreach ($case in @('async','sync','native-before-return','lazy-handle','unrelated','unissued','duplicate','missing','generation','wrong-group','high-generation','raw-live','unpublished-group','reused')) {
        & (Join-Path $build 'director-jobs.exe') $player $case
        $expected = if ($case -in @('async','sync','native-before-return','lazy-handle','unrelated')) { 0 } else { 5023 }
        if ($LASTEXITCODE -ne $expected) { throw "Director job fixture $case returned $LASTEXITCODE, expected $expected." }
    }
    foreach ($case in @('normal','batch','multi','pending','substitute','wrong-generation','duplicate')) {
        & (Join-Path $build 'director-lineage.exe') $player $case
        $expected = if ($case -in @('substitute','wrong-generation','duplicate')) { 5023 } else { 0 }
        if ($LASTEXITCODE -ne $expected) { throw "Director lineage fixture $case returned $LASTEXITCODE, expected $expected." }
    }
    foreach ($case in @('normal','startup-worker','timeout','orphan','abort','registration','tamper','drift','owner-entry')) {
        & (Join-Path $build 'director.exe') $player $case
        $expected = if ($case -eq 'owner-entry') { 5023 } else { 0 }
        if ($LASTEXITCODE -ne $expected) { throw "Director fixture $case returned $LASTEXITCODE, expected $expected." }
    }
    Write-Output "Director producer fixtures passed in $build"
} finally { $env:PATH = $priorPath }
