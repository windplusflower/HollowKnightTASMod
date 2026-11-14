$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gcc = 'D:\software\mingw64\bin\gcc.exe'
if (-not (Test-Path $gcc)) { throw "gcc not found at $gcc" }

$gccDir = [IO.Path]::GetDirectoryName($gcc)
$priorPath = $env:PATH
try {
    $env:PATH = $gccDir + [IO.Path]::PathSeparator + $priorPath
    $source = Join-Path $repo 'native/HollowKnightTAS.ClockBridge/clock_bridge.c'
    $target = Join-Path $repo 'scratch/resident-clock-offline-074c843437b94c879d0f47756cdf6bf3/HollowKnightTAS.ClockBridge.dll'
    $libs = @('-lpsapi', '-luser32', '-ladvapi32')
    
    Write-Host "Compiling $source -> $target..."
    & $gcc -std=c11 -O2 -Wall -Wextra -Werror $source -o $target -shared @libs
    if ($LASTEXITCODE -ne 0) { throw "gcc failed with exit code $LASTEXITCODE" }

    Write-Host "Verifying exports..."
    . (Join-Path $PSScriptRoot 'Assert-ClockBridgeExports.ps1')
    Assert-ClockBridgeExports -Path $target -RequireResidentBoundary -RequireContactImpulsePolicy

    $info = Get-Item $target
    Write-Host "Successfully built: $($info.FullName) ($($info.Length) bytes, $($info.LastWriteTime))"
} finally {
    $env:PATH = $priorPath
}
