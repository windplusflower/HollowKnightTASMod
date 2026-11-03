param(
    [Parameter(Mandatory=$true)][string]$UnityPlayerPath,
    [string]$CompilerPath = 'gcc'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$player = (Resolve-Path -LiteralPath $UnityPlayerPath).Path
$cc = (Get-Command $CompilerPath -CommandType Application -ErrorAction Stop).Source
$build = Join-Path $repo ('scratch/resident-clock-offline-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $build
$priorPath = $env:PATH
try {
    $env:PATH = [IO.Path]::GetDirectoryName($cc) + [IO.Path]::PathSeparator + $priorPath
    function Build-Binary([string]$File, [string]$Name, [string[]]$Options) {
        $out = Join-Path $build $Name
        & $cc -std=c11 -O2 -Wall -Wextra -Werror (Join-Path $repo $File) -o $out @Options
        if ($LASTEXITCODE -ne 0) { throw "Could not compile $File (exit $LASTEXITCODE)." }
    }
    function Run-Harness([string]$Name, [string[]]$Options) {
        & (Join-Path $build $Name) @Options
        if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
    }
    $libs = @('-lpsapi','-luser32','-ladvapi32')
    Build-Binary 'native/HollowKnightTAS.ClockBridge/clock_bridge.c' 'HollowKnightTAS.ClockBridge.dll' (@('-shared') + $libs)
    . (Join-Path $PSScriptRoot 'Assert-ClockBridgeExports.ps1')
    Assert-ClockBridgeExports -Path (Join-Path $build 'HollowKnightTAS.ClockBridge.dll') -RequireResidentBoundary -RequireContactImpulsePolicy
    Build-Binary 'tests/native/ResidentAudioFenceHarness.c' 'audio.exe' $libs
    Build-Binary 'tests/native/ResidentParticleProducerHarness.c' 'particles.exe' $libs
    Build-Binary 'tests/native/FullRunContinuationHarness.c' 'clock.exe' $libs
    Run-Harness 'audio.exe' @()
    foreach ($scenario in @('normal','drain','orphan-main','orphan-worker','wrong-owner','wrong-handle','wrong-readback','escaped','unpaired-set','abort','stale','tamper','drift')) {
        Run-Harness 'particles.exe' @($player,$scenario)
    }
    & (Join-Path $PSScriptRoot 'Test-ResidentDirectorFence.ps1') -UnityPlayerPath $player -CompilerPath $cc
    Run-Harness 'clock.exe' @($player)
    Write-Output "Verified offline clock bridge: $(Join-Path $build 'HollowKnightTAS.ClockBridge.dll')"
} finally { $env:PATH = $priorPath }
