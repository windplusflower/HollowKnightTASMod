[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$GameExecutable =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe',

    [string]$GccExecutable =
        'D:\software\mingw64\bin\gcc.exe',

    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$GameExecutable = [IO.Path]::GetFullPath($GameExecutable)
$GccExecutable = [IO.Path]::GetFullPath($GccExecutable)

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $bundleId = '{0}-{1}' -f `
        [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), `
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputRoot = Join-Path `
        $repoRoot `
        "artifacts\clock-prototype\bundles\$bundleId"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$gameDirectory = Split-Path -Parent $GameExecutable
$unityPlayer = Join-Path $gameDirectory 'UnityPlayer.dll'
$assemblyCSharp = Join-Path `
    $gameDirectory `
    'hollow_knight_Data\Managed\Assembly-CSharp.dll'
$payloadProject = Join-Path `
    $repoRoot `
    'src\HollowKnightTAS.ClockPayload\HollowKnightTAS.ClockPayload.csproj'
$injectorProject = Join-Path `
    $repoRoot `
    'src\HollowKnightTAS.ClockInjector\HollowKnightTAS.ClockInjector.csproj'
$bridgeSource = Join-Path `
    $repoRoot `
    'native\HollowKnightTAS.ClockBridge\clock_bridge.c'

foreach ($file in @(
        $GameExecutable,
        $unityPlayer,
        $assemblyCSharp,
        $GccExecutable,
        $payloadProject,
        $injectorProject,
        $bridgeSource
    )) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required clock-prototype input is missing: $file"
    }
}
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Clock bundle output already exists: $OutputRoot"
}

New-Item -ItemType Directory -Path $OutputRoot | Out-Null

& dotnet build $payloadProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Clock Payload build failed with exit code $LASTEXITCODE."
}

& dotnet publish `
    $injectorProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $OutputRoot
if ($LASTEXITCODE -ne 0) {
    throw "Clock Injector publish failed with exit code $LASTEXITCODE."
}

$payloadBuild = Join-Path `
    $repoRoot `
    "src\HollowKnightTAS.ClockPayload\bin\$Configuration\HollowKnightTAS.ClockPayload.dll"
if (-not (Test-Path -LiteralPath $payloadBuild -PathType Leaf)) {
    throw "Clock Payload output is missing: $payloadBuild"
}
Copy-Item `
    -LiteralPath $payloadBuild `
    -Destination (Join-Path $OutputRoot 'HollowKnightTAS.ClockPayload.dll')

$gccDirectory = Split-Path -Parent $GccExecutable
$originalPath = $env:Path
try {
    $env:Path = $gccDirectory + [IO.Path]::PathSeparator + $env:Path
    & $GccExecutable `
        -shared `
        -O2 `
        -Wall `
        -Wextra `
        -Werror `
        -o (Join-Path $OutputRoot 'HollowKnightTAS.ClockBridge.dll') `
        $bridgeSource
    if ($LASTEXITCODE -ne 0) {
        throw "Clock Bridge build failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:Path = $originalPath
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash `
        -LiteralPath $Path `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256([string]$Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return -join @(
            $sha256.ComputeHash($bytes) |
                ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha256.Dispose()
    }
}

$bridgePath = Join-Path $OutputRoot 'HollowKnightTAS.ClockBridge.dll'
$payloadPath = Join-Path $OutputRoot 'HollowKnightTAS.ClockPayload.dll'
$injectorPath = Join-Path $OutputRoot 'HollowKnightTAS.ClockInjector.exe'
$injectorManagedPath = Join-Path `
    $OutputRoot `
    'HollowKnightTAS.ClockInjector.dll'
$injectorDepsPath = Join-Path `
    $OutputRoot `
    'HollowKnightTAS.ClockInjector.deps.json'
$injectorRuntimeConfigPath = Join-Path `
    $OutputRoot `
    'HollowKnightTAS.ClockInjector.runtimeconfig.json'
foreach ($file in @(
        $bridgePath,
        $payloadPath,
        $injectorPath,
        $injectorManagedPath,
        $injectorDepsPath,
        $injectorRuntimeConfigPath
    )) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Clock bundle output is missing: $file"
    }
}

$runtimeFileNames = @(
    'HollowKnightTAS.ClockBridge.dll',
    'HollowKnightTAS.ClockInjector.deps.json',
    'HollowKnightTAS.ClockInjector.dll',
    'HollowKnightTAS.ClockInjector.exe',
    'HollowKnightTAS.ClockInjector.runtimeconfig.json',
    'HollowKnightTAS.ClockPayload.dll'
)
$runtimeFileSet = @(
    $runtimeFileNames |
        Sort-Object |
        ForEach-Object {
            $path = Join-Path $OutputRoot $_
            [ordered]@{
                file = $_
                length = (Get-Item -LiteralPath $path).Length
                sha256 = Get-Sha256 $path
            }
        }
)
$runtimeFileSetSha256 = Get-TextSha256 (
    $runtimeFileSet | ConvertTo-Json -Compress -Depth 5)
$whitelist = [ordered]@{
    schemaVersion = 1
    processImageSha256 = Get-Sha256 $GameExecutable
    unityPlayerSha256 = Get-Sha256 $unityPlayer
    assemblyCSharpSha256 = Get-Sha256 $assemblyCSharp
    bridgeSha256 = Get-Sha256 $bridgePath
    payloadSha256 = Get-Sha256 $payloadPath
}
$whitelistPath = Join-Path $OutputRoot 'clock-build-whitelist-v1.json'
$whitelist |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $whitelistPath -Encoding utf8NoBOM

$manifest = [ordered]@{
    schemaVersion = 2
    capabilityId = 'native.clock-rng-pause.override.experimental.v31'
    profile = 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle'
    bridgeAbi = 10
    startupFrameGateAbi = 1
    startupPolicy = 'create-suspended-early-apc-unity-then-bridge-v1'
    randomSynchronizationPolicy =
        'unity-init-state-at-root-only-native-scene-lifecycle-v19'
    randomSynchronizationSeed = 1212896321
    configuration = $Configuration
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    gameExecutable = $GameExecutable
    gameExecutableSha256 = $whitelist.processImageSha256
    unityPlayerSha256 = $whitelist.unityPlayerSha256
    assemblyCSharpSha256 = $whitelist.assemblyCSharpSha256
    injectorSha256 = Get-Sha256 $injectorPath
    injectorManagedSha256 = Get-Sha256 $injectorManagedPath
    injectorDepsSha256 = Get-Sha256 $injectorDepsPath
    injectorRuntimeConfigSha256 = Get-Sha256 $injectorRuntimeConfigPath
    bridgeSha256 = $whitelist.bridgeSha256
    payloadSha256 = $whitelist.payloadSha256
    runtimeFileSetSha256 = $runtimeFileSetSha256
    runtimeFileSet = $runtimeFileSet
    bridgeSourceSha256 = Get-Sha256 $bridgeSource
    payloadSourceSha256 = Get-Sha256 (
        Join-Path `
            $repoRoot `
            'src\HollowKnightTAS.ClockPayload\ClockController.cs')
    injectorSourceSha256 = Get-Sha256 (
        Join-Path `
            $repoRoot `
            'src\HollowKnightTAS.ClockInjector\Program.cs')
}
$manifestPath = Join-Path $OutputRoot 'clock-build-manifest-v1.json'
$manifest |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

[pscustomobject]@{
    BundleRoot = $OutputRoot
    Injector = $injectorPath
    Manifest = $manifestPath
    InjectorSha256 = $manifest.injectorSha256
    BridgeSha256 = $manifest.bridgeSha256
    PayloadSha256 = $manifest.payloadSha256
}
