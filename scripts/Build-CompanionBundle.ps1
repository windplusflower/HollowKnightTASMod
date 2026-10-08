[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $PrivateKeyPath,

    [string] $PublicKeyPath,

    [string] $InstallRoot,

    [switch] $StageOnly,

    [string] $ClockBundleRoot = '',

    [string] $GameExecutable =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$publishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\publish\win-x64'))
$nativePublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\publish\native-win-x64'))
$agentPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\publish\agent-win-x64'))
$cliPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\publish\cli-win-x64'))
$sdkPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\publish\sdk-net8.0'))
$clockPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\publish\clock-startup'))
$bundleRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'packaging\staging\bundle'))
$companionStage = Join-Path $bundleRoot 'Companion\win-x64'
$nativeStage = Join-Path $companionStage 'Native'
$toolsStage = Join-Path $companionStage 'Tools'
$sdkStage = Join-Path $toolsStage 'SDK'
$clockStage = Join-Path $companionStage 'ClockStartup'
$nativeWhitelist = Join-Path `
    $repoRoot `
    'packaging\native-build-whitelist-v1.json'
$manifestPath = Join-Path $bundleRoot 'companion.manifest.json'
$companionProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.Companion\HollowKnightTAS.Companion.csproj'
$nativeProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.NativeHost\HollowKnightTAS.NativeHost.csproj'
$agentProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.AgentBridge\HollowKnightTAS.AgentBridge.csproj'
$cliProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.Cli\HollowKnightTAS.Cli.csproj'
$sdkProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.Automation.Client\HollowKnightTAS.Automation.Client.csproj'
$bundleToolProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.BundleTool\HollowKnightTAS.BundleTool.csproj'
$runtimeProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.Runtime\HollowKnightTAS.Runtime.csproj'
$clockInjectorProject = Join-Path $repoRoot `
    'src\HollowKnightTAS.ClockInjector\HollowKnightTAS.ClockInjector.csproj'
$clockBuildScript = Join-Path $repoRoot `
    'scripts\Build-T24ClockPrototype.ps1'

if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $PrivateKeyPath = Join-Path $repoRoot `
        '.local\signing\companion-private.json'
}

if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $PublicKeyPath = Join-Path $repoRoot `
        '.local\signing\companion-public.json'
}

$PrivateKeyPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$PublicKeyPath = [System.IO.Path]::GetFullPath($PublicKeyPath)

function Assert-UnderDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Candidate,

        [Parameter(Mandatory)]
        [string] $Parent
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $prefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside expected directory: $candidateFull"
    }
}

Assert-UnderDirectory -Candidate $publishRoot -Parent $repoRoot
Assert-UnderDirectory -Candidate $nativePublishRoot -Parent $repoRoot
Assert-UnderDirectory -Candidate $agentPublishRoot -Parent $repoRoot
Assert-UnderDirectory -Candidate $cliPublishRoot -Parent $repoRoot
Assert-UnderDirectory -Candidate $sdkPublishRoot -Parent $repoRoot
Assert-UnderDirectory -Candidate $clockPublishRoot -Parent $repoRoot
Assert-UnderDirectory -Candidate $bundleRoot -Parent $repoRoot

function Assert-NoRunningTasApplications {
    # Do not terminate applications: Studio may contain unsaved user edits.
    $running = @(Get-Process -Name 'hollow_knight', 'HollowKnightTAS.Companion' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $summary = ($running | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
        throw "Close the game and save/close Studio before installation: $summary"
    }
}

if (-not $StageOnly) {
    Assert-NoRunningTasApplications
}

if ($ClockBundleRoot) {
    $ClockBundleRoot = (Resolve-Path -LiteralPath $ClockBundleRoot).Path
    foreach ($output in @($publishRoot, $nativePublishRoot, $agentPublishRoot,
            $cliPublishRoot, $sdkPublishRoot, $clockPublishRoot, $bundleRoot)) {
        if ($ClockBundleRoot -eq $output -or $ClockBundleRoot.StartsWith(
                $output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Reusable clock source must not be inside a directory cleared by this build.'
        }
    }
    $clockManifest = Get-Content -LiteralPath (Join-Path $ClockBundleRoot 'clock-build-manifest-v1.json') -Raw | ConvertFrom-Json
    if ($clockManifest.configuration -ne $Configuration -or
        $clockManifest.gameExecutableSha256 -ne (Get-FileHash -LiteralPath $GameExecutable).Hash) {
        throw 'Reusable clock configuration or game fingerprint does not match.'
    }
    if ($clockManifest.injectorSourceSha256 -ne
        (Get-FileHash -LiteralPath (Join-Path $repoRoot 'src\HollowKnightTAS.ClockInjector\Program.cs')).Hash) {
        throw 'Reusable ClockInjector source does not match. Rebuild the clock bundle with the current source.'
    }
    $clockConfig = Get-Content -LiteralPath (Join-Path $ClockBundleRoot 'HollowKnightTAS.ClockInjector.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($clockConfig.runtimeOptions.PSObject.Properties.Name -notcontains 'includedFrameworks') {
        throw 'Reusable ClockInjector must be self-contained. Rebuild the clock bundle with the current script.'
    }
    $clockNames = @('HollowKnightTAS.ClockBridge.dll', 'HollowKnightTAS.ClockInjector.deps.json',
        'HollowKnightTAS.ClockInjector.dll', 'HollowKnightTAS.ClockInjector.exe',
        'HollowKnightTAS.ClockInjector.runtimeconfig.json', 'HollowKnightTAS.ClockPayload.dll')
    if (@($clockManifest.runtimeFileSet).Count -ne 6 -or
        @($clockManifest.runtimeFileSet.file | Sort-Object -Unique).Count -ne 6) {
        throw 'Reusable clock file set is incomplete or duplicated.'
    }
    foreach ($file in $clockManifest.runtimeFileSet) {
        if ($file.file -cnotin $clockNames -or $file.sha256 -ne
            (Get-FileHash -LiteralPath (Join-Path $ClockBundleRoot $file.file)).Hash) {
            throw 'Reusable clock file name or hash is invalid.'
        }
    }
}

if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Private signing key is missing: $PrivateKeyPath"
}

if (-not (Test-Path -LiteralPath $PublicKeyPath -PathType Leaf)) {
    throw "Public verification key is missing: $PublicKeyPath"
}
if (-not (Test-Path -LiteralPath $nativeWhitelist -PathType Leaf)) {
    throw "Native build whitelist is missing: $nativeWhitelist"
}

foreach ($directory in @(
        $publishRoot,
        $nativePublishRoot,
        $agentPublishRoot,
        $cliPublishRoot,
        $sdkPublishRoot,
        $clockPublishRoot,
        $bundleRoot
    )) {
    if (Test-Path -LiteralPath $directory) {
        Assert-UnderDirectory -Candidate $directory -Parent $repoRoot
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $companionStage -Force | Out-Null
New-Item -ItemType Directory -Path $nativePublishRoot -Force |
    Out-Null
New-Item -ItemType Directory -Path $nativeStage -Force |
    Out-Null
New-Item -ItemType Directory -Path $agentPublishRoot -Force |
    Out-Null
New-Item -ItemType Directory -Path $cliPublishRoot -Force |
    Out-Null
New-Item -ItemType Directory -Path $toolsStage -Force |
    Out-Null
New-Item -ItemType Directory -Path $sdkPublishRoot -Force |
    Out-Null
New-Item -ItemType Directory -Path $sdkStage -Force |
    Out-Null

if (-not $ClockBundleRoot) {
& $clockBuildScript `
    -Configuration $Configuration `
    -GameExecutable $GameExecutable `
    -OutputRoot $clockPublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "ClockStartup build failed with exit code $LASTEXITCODE."
}
}

New-Item -ItemType Directory -Path $clockStage -Force |
    Out-Null
$clockRuntimeFiles = @(
    'clock-build-manifest-v1.json',
    'clock-build-whitelist-v1.json',
    'HollowKnightTAS.ClockBridge.dll',
    'HollowKnightTAS.ClockInjector.deps.json',
    'HollowKnightTAS.ClockInjector.dll',
    'HollowKnightTAS.ClockInjector.exe',
    'HollowKnightTAS.ClockInjector.runtimeconfig.json',
    'HollowKnightTAS.ClockPayload.dll'
)
foreach ($clockFile in $clockRuntimeFiles) {
    $source = Join-Path $(if ($ClockBundleRoot) { $ClockBundleRoot } else { $clockPublishRoot }) $clockFile
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "ClockStartup output is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination $clockStage -Force
}

& dotnet publish $companionProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -p:UseAppHost=true `
    -o $publishRoot `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Companion publish failed with exit code $LASTEXITCODE."
}

Copy-Item -Path (Join-Path $publishRoot '*') `
    -Destination $companionStage `
    -Recurse `
    -Force

# Keep the six ClockStartup files for startup-profile verification, while the
# injector's managed assembly/deps/config also live beside the shared runtime.
# Only the final EXE is rewritten; refresh its inner manifest before signing.
$clockSource = if ($ClockBundleRoot) { $ClockBundleRoot } else { $clockPublishRoot }
& (Join-Path $PSScriptRoot 'Stage-SharedDotnetApp.ps1') -Project $clockInjectorProject `
    -PublishDirectory $clockSource -SharedDirectory $companionStage `
    -EntrypointDirectory $clockStage -ApplicationName 'HollowKnightTAS.ClockInjector' `
    -Configuration $Configuration -ExcludedFiles @(
        'clock-build-manifest-v1.json', 'clock-build-whitelist-v1.json',
        'HollowKnightTAS.ClockBridge.dll', 'HollowKnightTAS.ClockPayload.dll')
$clockManifestPath = Join-Path $clockStage 'clock-build-manifest-v1.json'
$clockManifest = Get-Content -LiteralPath $clockManifestPath -Raw | ConvertFrom-Json
$clockInjector = Get-Item -LiteralPath (Join-Path $clockStage 'HollowKnightTAS.ClockInjector.exe')
$clockManifest.injectorSha256 = (Get-FileHash -LiteralPath $clockInjector.FullName).Hash.ToLowerInvariant()
$clockEntry = $clockManifest.runtimeFileSet | Where-Object file -eq 'HollowKnightTAS.ClockInjector.exe'
$clockEntry.length = $clockInjector.Length
$clockEntry.sha256 = $clockManifest.injectorSha256
$clockFileSetJson = $clockManifest.runtimeFileSet | ConvertTo-Json -Compress -Depth 5
$clockManifest.runtimeFileSetSha256 = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($clockFileSetJson))).ToLowerInvariant()
$clockManifest | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $clockManifestPath -Encoding utf8NoBOM

& dotnet publish $nativeProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -p:UseAppHost=true `
    -o $nativePublishRoot `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "NativeHost publish failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'Stage-SharedDotnetApp.ps1') -Project $nativeProject `
    -PublishDirectory $nativePublishRoot -SharedDirectory $companionStage `
    -EntrypointDirectory $nativeStage -ApplicationName 'HollowKnightTAS.NativeHost' -Configuration $Configuration
Copy-Item -LiteralPath $nativeWhitelist `
    -Destination (
        Join-Path $nativeStage 'native-build-whitelist-v1.json'
    ) `
    -Force

& dotnet publish $agentProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -p:UseAppHost=true `
    -o $agentPublishRoot `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "AgentBridge publish failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'Stage-SharedDotnetApp.ps1') -Project $agentProject `
    -PublishDirectory $agentPublishRoot -SharedDirectory $companionStage `
    -EntrypointDirectory $toolsStage -ApplicationName 'HollowKnightTAS.AgentBridge' -Configuration $Configuration

& dotnet publish $cliProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -p:UseAppHost=true `
    -o $cliPublishRoot `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "CLI publish failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'Stage-SharedDotnetApp.ps1') -Project $cliProject `
    -PublishDirectory $cliPublishRoot -SharedDirectory $companionStage `
    -EntrypointDirectory $toolsStage -ApplicationName 'HollowKnightTAS.Cli' -Configuration $Configuration

& dotnet publish $sdkProject `
    -c $Configuration `
    -f net8.0 `
    --self-contained false `
    -o $sdkPublishRoot `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Automation SDK publish failed with exit code $LASTEXITCODE."
}

Copy-Item -Path (Join-Path $sdkPublishRoot '*') `
    -Destination $sdkStage `
    -Recurse `
    -Force

& (Join-Path $PSScriptRoot 'Stage-BundledFfmpeg.ps1') -Destination (Join-Path $toolsStage 'ffmpeg')

& dotnet build $bundleToolProject `
    -c $Configuration `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "BundleTool build failed with exit code $LASTEXITCODE."
}

& dotnet run `
    --project $bundleToolProject `
    -c $Configuration `
    --no-build `
    -- `
    sign `
    $PrivateKeyPath `
    $bundleRoot `
    $manifestPath
if ($LASTEXITCODE -ne 0) {
    throw "Companion manifest signing failed with exit code $LASTEXITCODE."
}

& dotnet run `
    --project $bundleToolProject `
    -c $Configuration `
    --no-build `
    -- `
    verify `
    $PublicKeyPath `
    $bundleRoot `
    $manifestPath
if ($LASTEXITCODE -ne 0) {
    throw "Staged Companion verification failed with exit code $LASTEXITCODE."
}

if ($StageOnly) {
    [pscustomobject]@{
        BundleRoot = $bundleRoot
        ManifestPath = $manifestPath
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Installed = $false
    } | Format-List
    return
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $localPropertiesPath = Join-Path $repoRoot 'LocalBuildProperties.props'
    if (-not (Test-Path -LiteralPath $localPropertiesPath -PathType Leaf)) {
        throw 'InstallRoot was not supplied and LocalBuildProperties.props is missing.'
    }

    [xml] $localProperties = Get-Content `
        -LiteralPath $localPropertiesPath `
        -Raw
    $hkModsDir = [string](
        $localProperties.Project.PropertyGroup.HKModsDir |
            Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($hkModsDir)) {
        throw 'HKModsDir is missing from LocalBuildProperties.props.'
    }

    $InstallRoot = Join-Path $hkModsDir 'HollowKnightTAS'
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$installParent = Split-Path -Parent $InstallRoot
Assert-UnderDirectory -Candidate $InstallRoot -Parent $installParent
if (-not [string]::Equals(
        (Split-Path -Leaf $InstallRoot),
        'HollowKnightTAS',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallRoot must end in HollowKnightTAS: $InstallRoot"
}

$installedCompanion = Join-Path $InstallRoot 'Companion'
# Recheck after publishing; an application may have started during the build.
Assert-NoRunningTasApplications
if (Test-Path -LiteralPath $installedCompanion) {
    Assert-UnderDirectory -Candidate $installedCompanion -Parent $InstallRoot
    Remove-Item -LiteralPath $installedCompanion -Recurse -Force
}

$installedManifest = Join-Path $InstallRoot 'companion.manifest.json'
if (Test-Path -LiteralPath $installedManifest) {
    Assert-UnderDirectory -Candidate $installedManifest -Parent $InstallRoot
    Remove-Item -LiteralPath $installedManifest -Force
}

& dotnet build $runtimeProject `
    -c $Configuration `
    --nologo `
    "-p:CompanionBundleRoot=$bundleRoot"
if ($LASTEXITCODE -ne 0) {
    throw "Runtime package build failed with exit code $LASTEXITCODE."
}

& dotnet run `
    --project $bundleToolProject `
    -c $Configuration `
    --no-build `
    -- `
    verify `
    $PublicKeyPath `
    $InstallRoot `
    $installedManifest
if ($LASTEXITCODE -ne 0) {
    throw "Installed Companion verification failed with exit code $LASTEXITCODE."
}

$entrypoint = Join-Path $InstallRoot `
    'Companion\win-x64\HollowKnightTAS.Companion.exe'
$agentEntrypoint = Join-Path $InstallRoot `
    'Companion\win-x64\Tools\HollowKnightTAS.AgentBridge.exe'
$cliEntrypoint = Join-Path $InstallRoot `
    'Companion\win-x64\Tools\HollowKnightTAS.Cli.exe'
$sdkEntrypoint = Join-Path $InstallRoot `
    'Companion\win-x64\Tools\SDK\HollowKnightTAS.Automation.Client.dll'
$zipPath = Join-Path $InstallRoot 'HollowKnightTAS.zip'
if (-not (Test-Path -LiteralPath $entrypoint -PathType Leaf)) {
    throw "Installed Companion entrypoint is missing: $entrypoint"
}

if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "Runtime package archive is missing: $zipPath"
}
if (-not (Test-Path -LiteralPath $agentEntrypoint -PathType Leaf)) {
    throw "Installed AgentBridge entrypoint is missing: $agentEntrypoint"
}
if (-not (Test-Path -LiteralPath $cliEntrypoint -PathType Leaf)) {
    throw "Installed CLI entrypoint is missing: $cliEntrypoint"
}
if (-not (Test-Path -LiteralPath $sdkEntrypoint -PathType Leaf)) {
    throw "Installed Automation SDK is missing: $sdkEntrypoint"
}

$manifestHash = (
    Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256
).Hash.ToLowerInvariant()
$zipHash = (
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
).Hash.ToLowerInvariant()

[pscustomobject]@{
    BundleRoot = $bundleRoot
    InstallRoot = $InstallRoot
    Entrypoint = $entrypoint
    AgentBridge = $agentEntrypoint
    Cli = $cliEntrypoint
    AutomationSdk = $sdkEntrypoint
    ManifestSha256 = $manifestHash
    PackageSha256 = $zipHash
} | Format-List
