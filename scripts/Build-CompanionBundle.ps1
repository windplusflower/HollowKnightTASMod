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

function Get-LowercaseSha256 {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-OptionalObjectProperty {
    param(
        [Parameter(Mandatory)]
        $Object,

        [Parameter(Mandatory)]
        [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) {
        return $null
    }

    return $prop.Value
}

function Set-ObjectProperty {
    param(
        [Parameter(Mandatory)]
        $Object,

        [Parameter(Mandatory)]
        [string] $Name,

        $Value
    )

    $prop = $Object.PSObject.Properties[$Name]
    if ($null -ne $prop) {
        $prop.Value = $Value
    }
    else {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
}

function Write-UnsignedCompanionManifest {
    param(
        [Parameter(Mandatory)]
        [string] $BundleRoot,

        [Parameter(Mandatory)]
        [string] $ManifestPath
    )

    $companionRoot = Join-Path $BundleRoot 'Companion\win-x64'
    $fileRecords = @(
        Get-ChildItem -LiteralPath $companionRoot -Recurse -File |
            ForEach-Object {
                $rel = [System.IO.Path]::GetRelativePath($BundleRoot, $_.FullName).Replace('\', '/')
                [pscustomobject]@{
                    path = $rel
                    sha256 = Get-LowercaseSha256 -Path $_.FullName
                }
            }
    )
    [Array]::Sort(
        $fileRecords,
        [System.Comparison[object]]{
            param($left, $right)
            return [StringComparer]::Ordinal.Compare($left.path, $right.path)
        })

    $unsignedManifest = [ordered]@{
        schemaVersion = 1
        product = 'HollowKnightTAS.Companion'
        version = '1.0.0'
        rid = 'win-x64'
        runtimeProtocolMin = 1
        runtimeProtocolMax = 1
        entrypoint = 'Companion/win-x64/HollowKnightTAS.Companion.exe'
        files = $fileRecords
        signature = ''
    }

    $unsignedManifest |
        ConvertTo-Json -Compress -Depth 10 |
        Set-Content -LiteralPath $ManifestPath -Encoding utf8NoBOM
}

$clockNames = @(
    'HollowKnightTAS.ClockBridge.dll',
    'HollowKnightTAS.ClockInjector.deps.json',
    'HollowKnightTAS.ClockInjector.dll',
    'HollowKnightTAS.ClockInjector.exe',
    'HollowKnightTAS.ClockInjector.runtimeconfig.json',
    'HollowKnightTAS.ClockPayload.dll'
)

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
    $clockManifestSourcePath = Join-Path $ClockBundleRoot 'clock-build-manifest-v1.json'
    if (Test-Path -LiteralPath $clockManifestSourcePath -PathType Leaf) {
        $clockManifest = Get-Content -LiteralPath $clockManifestSourcePath -Raw | ConvertFrom-Json
        $manifestConfig = [string](Get-OptionalObjectProperty -Object $clockManifest -Name 'configuration')
        if ($manifestConfig -ne $Configuration) {
            Write-Warning "Reusable clock configuration '$manifestConfig' differs from current '$Configuration'; staged manifest will be synchronized."
        }
        $manifestGameSha = [string](Get-OptionalObjectProperty -Object $clockManifest -Name 'gameExecutableSha256')
        if (Test-Path -LiteralPath $GameExecutable -PathType Leaf) {
            $actualGameSha = Get-LowercaseSha256 -Path $GameExecutable
            if ($manifestGameSha -ne $actualGameSha) {
                Write-Warning "Reusable clock gameExecutableSha256 ($manifestGameSha) differs from current game ($actualGameSha); staged manifest will be synchronized."
            }
        }
        else {
            Write-Warning "GameExecutable not found at '$GameExecutable'; keeping existing gameExecutableSha256 in staged clock manifest."
        }
        $injectorSourcePath = Join-Path $repoRoot 'src\HollowKnightTAS.ClockInjector\Program.cs'
        $actualInjectorSourceSha = Get-LowercaseSha256 -Path $injectorSourcePath
        $manifestInjectorSourceSha = [string](Get-OptionalObjectProperty -Object $clockManifest -Name 'injectorSourceSha256')
        if ($manifestInjectorSourceSha -ne $actualInjectorSourceSha) {
            Write-Warning "Reusable clock injectorSourceSha256 ($manifestInjectorSourceSha) differs from current source ($actualInjectorSourceSha); staged manifest will be synchronized."
        }
        $runtimeConfigPath = Join-Path $ClockBundleRoot 'HollowKnightTAS.ClockInjector.runtimeconfig.json'
        if (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) {
            $clockConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
            if ($clockConfig.runtimeOptions.PSObject.Properties.Name -notcontains 'includedFrameworks') {
                Write-Warning 'Reusable ClockInjector runtimeconfig.json does not declare includedFrameworks (continuing anyway).'
            }
        }
        $manifestRuntimeSet = @(Get-OptionalObjectProperty -Object $clockManifest -Name 'runtimeFileSet' | Where-Object { $null -ne $_ })
        if ($manifestRuntimeSet.Count -ne $clockNames.Count -or
            @($manifestRuntimeSet | ForEach-Object { $_.file } | Sort-Object -Unique).Count -ne $clockNames.Count) {
            Write-Warning 'Reusable clock runtimeFileSet is incomplete or duplicated; staged manifest runtimeFileSet will be rebuilt from staged files.'
        }
        foreach ($file in $manifestRuntimeSet) {
            $entryName = [string](Get-OptionalObjectProperty -Object $file -Name 'file')
            $entrySha = [string](Get-OptionalObjectProperty -Object $file -Name 'sha256')
            $entryPath = Join-Path $ClockBundleRoot $entryName
            if ($entryName -cnotin $clockNames) {
                Write-Warning "Reusable clock runtimeFileSet contains unexpected entry '$entryName'; staged manifest will be synchronized."
            }
            elseif (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
                Write-Warning "Reusable clock runtimeFileSet entry is missing on disk: $entryPath"
            }
            else {
                $actualEntrySha = Get-LowercaseSha256 -Path $entryPath
                if ($entrySha -ne $actualEntrySha) {
                    Write-Warning "Reusable clock runtimeFileSet hash mismatch for '$entryName' (manifest=$entrySha, actual=$actualEntrySha); staged manifest will be synchronized."
                }
            }
        }
    }
    else {
        Write-Warning "Reusable clock manifest not found at '$clockManifestSourcePath'."
    }
}

$hasPrivateKey = Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf
if (-not $hasPrivateKey) {
    Write-Warning "Private signing key is missing (manifest will be written unsigned): $PrivateKeyPath"
}

$hasPublicKey = Test-Path -LiteralPath $PublicKeyPath -PathType Leaf
if (-not $hasPublicKey) {
    Write-Warning "Public verification key is missing (signature verification will be skipped): $PublicKeyPath"
}

$hasNativeWhitelist = Test-Path -LiteralPath $nativeWhitelist -PathType Leaf
if (-not $hasNativeWhitelist) {
    Write-Warning "Native build whitelist is missing (skipping whitelist copy): $nativeWhitelist"
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
$clockInjectorDll = Join-Path $clockStage 'HollowKnightTAS.ClockInjector.dll'
$clockInjectorDeps = Join-Path $clockStage 'HollowKnightTAS.ClockInjector.deps.json'
$clockInjectorRuntimeConfig = Join-Path $clockStage 'HollowKnightTAS.ClockInjector.runtimeconfig.json'
$clockBridgePath = Join-Path $clockStage 'HollowKnightTAS.ClockBridge.dll'
$clockPayloadPath = Join-Path $clockStage 'HollowKnightTAS.ClockPayload.dll'

Set-ObjectProperty -Object $clockManifest -Name 'configuration' -Value $Configuration
if (Test-Path -LiteralPath $GameExecutable -PathType Leaf) {
    $resolvedGameExecutable = [System.IO.Path]::GetFullPath($GameExecutable)
    $gameDir = Split-Path -Parent $resolvedGameExecutable
    Set-ObjectProperty -Object $clockManifest -Name 'gameExecutable' -Value $resolvedGameExecutable
    Set-ObjectProperty -Object $clockManifest -Name 'gameExecutableSha256' -Value (Get-LowercaseSha256 -Path $resolvedGameExecutable)
    $unityPlayerPath = Join-Path $gameDir 'UnityPlayer.dll'
    if (Test-Path -LiteralPath $unityPlayerPath -PathType Leaf) {
        Set-ObjectProperty -Object $clockManifest -Name 'unityPlayerSha256' -Value (Get-LowercaseSha256 -Path $unityPlayerPath)
    }
    $assemblyCSharpPath = Join-Path $gameDir 'hollow_knight_Data\Managed\Assembly-CSharp.dll'
    if (Test-Path -LiteralPath $assemblyCSharpPath -PathType Leaf) {
        Set-ObjectProperty -Object $clockManifest -Name 'assemblyCSharpSha256' -Value (Get-LowercaseSha256 -Path $assemblyCSharpPath)
    }
}
$injectorSourceFile = Join-Path $repoRoot 'src\HollowKnightTAS.ClockInjector\Program.cs'
if (Test-Path -LiteralPath $injectorSourceFile -PathType Leaf) {
    Set-ObjectProperty -Object $clockManifest -Name 'injectorSourceSha256' -Value (Get-LowercaseSha256 -Path $injectorSourceFile)
}
$bridgeSourceFile = Join-Path $repoRoot 'native\HollowKnightTAS.ClockBridge\clock_bridge.c'
if (Test-Path -LiteralPath $bridgeSourceFile -PathType Leaf) {
    Set-ObjectProperty -Object $clockManifest -Name 'bridgeSourceSha256' -Value (Get-LowercaseSha256 -Path $bridgeSourceFile)
}
$payloadSourceFile = Join-Path $repoRoot 'src\HollowKnightTAS.ClockPayload\ClockController.cs'
if (Test-Path -LiteralPath $payloadSourceFile -PathType Leaf) {
    Set-ObjectProperty -Object $clockManifest -Name 'payloadSourceSha256' -Value (Get-LowercaseSha256 -Path $payloadSourceFile)
}

Set-ObjectProperty -Object $clockManifest -Name 'injectorSha256' -Value (Get-LowercaseSha256 -Path $clockInjector.FullName)
Set-ObjectProperty -Object $clockManifest -Name 'injectorManagedSha256' -Value (Get-LowercaseSha256 -Path $clockInjectorDll)
Set-ObjectProperty -Object $clockManifest -Name 'injectorDepsSha256' -Value (Get-LowercaseSha256 -Path $clockInjectorDeps)
Set-ObjectProperty -Object $clockManifest -Name 'injectorRuntimeConfigSha256' -Value (Get-LowercaseSha256 -Path $clockInjectorRuntimeConfig)
Set-ObjectProperty -Object $clockManifest -Name 'bridgeSha256' -Value (Get-LowercaseSha256 -Path $clockBridgePath)
Set-ObjectProperty -Object $clockManifest -Name 'payloadSha256' -Value (Get-LowercaseSha256 -Path $clockPayloadPath)

$syncedRuntimeFileSet = @(
    $clockNames |
        Sort-Object |
        ForEach-Object {
            $stagedClockFile = Join-Path $clockStage $_
            $item = Get-Item -LiteralPath $stagedClockFile
            [pscustomobject]@{
                file = $_
                length = $item.Length
                sha256 = Get-LowercaseSha256 -Path $item.FullName
            }
        }
)
Set-ObjectProperty -Object $clockManifest -Name 'runtimeFileSet' -Value $syncedRuntimeFileSet
$clockFileSetJson = $syncedRuntimeFileSet | ConvertTo-Json -Compress -Depth 5
Set-ObjectProperty -Object $clockManifest -Name 'runtimeFileSetSha256' -Value ([Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($clockFileSetJson))).ToLowerInvariant())
$clockManifest | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $clockManifestPath -Encoding utf8NoBOM

$clockWhitelistPath = Join-Path $clockStage 'clock-build-whitelist-v1.json'
if (Test-Path -LiteralPath $clockWhitelistPath -PathType Leaf) {
    $clockWhitelist = Get-Content -LiteralPath $clockWhitelistPath -Raw | ConvertFrom-Json
    $gameSha = Get-OptionalObjectProperty -Object $clockManifest -Name 'gameExecutableSha256'
    if ($null -ne $gameSha) {
        Set-ObjectProperty -Object $clockWhitelist -Name 'processImageSha256' -Value $gameSha
    }
    $unitySha = Get-OptionalObjectProperty -Object $clockManifest -Name 'unityPlayerSha256'
    if ($null -ne $unitySha) {
        Set-ObjectProperty -Object $clockWhitelist -Name 'unityPlayerSha256' -Value $unitySha
    }
    $asmSha = Get-OptionalObjectProperty -Object $clockManifest -Name 'assemblyCSharpSha256'
    if ($null -ne $asmSha) {
        Set-ObjectProperty -Object $clockWhitelist -Name 'assemblyCSharpSha256' -Value $asmSha
    }
    Set-ObjectProperty -Object $clockWhitelist -Name 'bridgeSha256' -Value (Get-OptionalObjectProperty -Object $clockManifest -Name 'bridgeSha256')
    Set-ObjectProperty -Object $clockWhitelist -Name 'payloadSha256' -Value (Get-OptionalObjectProperty -Object $clockManifest -Name 'payloadSha256')
    $clockWhitelist | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $clockWhitelistPath -Encoding utf8NoBOM
}

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
if ($hasNativeWhitelist) {
    Copy-Item -LiteralPath $nativeWhitelist `
        -Destination (
            Join-Path $nativeStage 'native-build-whitelist-v1.json'
        ) `
        -Force
}

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

if ($hasPrivateKey) {
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
        Write-Warning "Companion manifest signing failed with exit code $LASTEXITCODE; continuing with unsigned manifest."
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            Write-UnsignedCompanionManifest -BundleRoot $bundleRoot -ManifestPath $manifestPath
        }
    }
}
else {
    Write-UnsignedCompanionManifest -BundleRoot $bundleRoot -ManifestPath $manifestPath
}

if ($hasPublicKey -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
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
        Write-Warning "Staged Companion verification did not pass (exit code $LASTEXITCODE); continuing."
    }
}
else {
    Write-Warning "Skipping staged Companion verification (public key or manifest missing)."
}

if ($StageOnly) {
    [pscustomobject]@{
        BundleRoot = $bundleRoot
        ManifestPath = $manifestPath
        ManifestSha256 = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { Get-LowercaseSha256 -Path $manifestPath } else { '' }
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

if ($hasPublicKey -and (Test-Path -LiteralPath $installedManifest -PathType Leaf)) {
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
        Write-Warning "Installed Companion verification did not pass (exit code $LASTEXITCODE); continuing."
    }
}
else {
    Write-Warning "Skipping installed Companion verification (public key or manifest missing)."
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
