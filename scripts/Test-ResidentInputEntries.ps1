param(
    [Parameter(Mandatory=$true)][string]$UnityPlayerPath,
    [string]$CompilerPath = 'gcc'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$player = (Resolve-Path -LiteralPath $UnityPlayerPath).Path
$cc = (Get-Command $CompilerPath -CommandType Application -ErrorAction Stop).Source
$build = Join-Path $repo ('scratch/resident-input-entries-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $build
$layout = Join-Path $repo 'native/HollowKnightTAS.ClockBridge/resident_input_entry_layout.h'
# Generate in the private output directory and require the checked-in candidate
# to match. Tests do not silently rewrite production sources.
$generated = Join-Path $build 'resident_input_entry_layout.h'
& python (Join-Path $PSScriptRoot 'Generate-ResidentInputEntryLayout.py') --player $player --output $generated
if ($LASTEXITCODE) { throw "Input layout generation failed ($LASTEXITCODE)." }
if ([IO.File]::ReadAllText($layout) -cne [IO.File]::ReadAllText($generated)) { throw 'Input entry layout is stale.' }

# The joint Director entry fixture checks the Windows unwind result at every
# instruction address in both of these linked thunks. Exercise the actual
# producer wrappers and epoch receipt with their own native call paths here.
$exe = Join-Path $build 'input.exe'
& $cc -std=c11 -O2 -Wall -Wextra -Werror (Join-Path $repo 'tests/native/ResidentInputProducerHarness.c') -o $exe -municode -lpsapi -luser32 -ladvapi32
if ($LASTEXITCODE) { throw "Input producer harness build failed ($LASTEXITCODE)." }
foreach ($case in @('normal','prefix-drift','owner-drift')) {
    & $exe $player $case
    if ($LASTEXITCODE) { throw "Input producer fixture $case failed ($LASTEXITCODE)." }
}
