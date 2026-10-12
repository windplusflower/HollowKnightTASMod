param(
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [Parameter(Mandatory=$true)][string] $Movie,
    [long] $FirstFrame = 0,
    [long] $EndFrame = 3800,
    [ValidateRange(1,3)][int] $Runs = 1,
    [switch] $Light,
    [switch] $RandomCalls,
    [long] $CallFirst = 1421,
    [long] $CallEnd = 1460,
    [switch] $EngineLayout,
    [switch] $DisableLegacyCalibration,
    [switch] $ClockRoundTrip,
    [switch] $SynchronousFetch,
    [switch] $CanonicalStartupClock,
    [switch] $RecordInputs,
    [switch] $GameplayOracle,
    [string] $OracleWindows = '',
    [switch] $NativeScheduler,
    [switch] $NativePhysics,
    [switch] $NativePhysicsOnly,
    [switch] $NativePhysicsDetails,
    [switch] $DisablePrimaryProbe,
    [switch] $RelaxedResources, # Compatibility option; loading probes now disable all memory gates.
    [switch] $ModWallClock,
    [switch] $FieldDetails,
    [switch] $SerialScenes,
    [switch] $SerialBoundaryOnly,
    [switch] $VirtualAudio,
    [switch] $IsolatedOnline,
    [switch] $CanonicalParticles,
    [switch] $CanonicalBoundary,
    [switch] $BoundaryInventory,
    [switch] $OnlyTasMods,
    [switch] $ExportVideo,
    [switch] $CheckpointPrototype,
    [long] $CheckpointAfterFrame = 0,
    [string] $CheckpointWindows = '',
    [string] $ClockBridgePath = '',
    [ValidateSet('', 'p0', 'p3', 'p3a', 'p3b', 'p3c')][string] $EvidencePhase = '',
    [ValidateRange(0,120)][int] $EndpointHoldSeconds = 0
)
$ErrorActionPreference = 'Stop'
if ($SerialScenes -and -not $ModWallClock) { throw 'SerialScenes requires the complete v5 policies and uses the distinct v6 profile.' }
if ($SerialBoundaryOnly -and (-not $SerialScenes -or -not $GameplayOracle)) { throw 'SerialBoundaryOnly requires SerialScenes and GameplayOracle.' }
if ($VirtualAudio -and -not $SerialScenes) { throw 'VirtualAudio requires SerialScenes.' }
if ($IsolatedOnline -and -not $VirtualAudio) { throw 'IsolatedOnline requires VirtualAudio.' }
if ($CanonicalParticles -and -not $IsolatedOnline) { throw 'CanonicalParticles requires IsolatedOnline.' }
if ($CanonicalBoundary -and -not $CanonicalParticles) { throw 'CanonicalBoundary requires CanonicalParticles.' }
if ($CanonicalBoundary) { $OnlyTasMods = $true }
if ($BoundaryInventory -and -not $SerialScenes) { throw 'BoundaryInventory requires SerialScenes.' }
if ($CanonicalStartupClock -and (-not $SynchronousFetch -or -not $DisableLegacyCalibration)) { throw 'The v4 experiment requires synchronous fetch and disabled legacy calibration.' }
if ($ModWallClock -and -not $CanonicalStartupClock) { throw 'The v5 Mod wall-clock experiment requires canonical startup clocks.' }
if ($RecordInputs -and $Runs -ne 1) { throw 'RecordInputs requires one run.' }
if ($CheckpointPrototype -and ($Runs -ne 1 -or $RecordInputs -or -not $SerialScenes -or $ExportVideo)) { throw 'CheckpointPrototype requires one serial-scene replay without recording or video.' }
if ($CheckpointWindows -and -not $CheckpointPrototype) { throw 'CheckpointWindows requires CheckpointPrototype.' }
if ($CheckpointAfterFrame -lt 0 -or $CheckpointAfterFrame -ge $EndFrame -or
    ($CheckpointAfterFrame -gt 0 -and -not $CheckpointPrototype)) { throw 'CheckpointAfterFrame must select a boundary before the prototype endpoint.' }
if ($CheckpointWindows) {
    $windowEnd = $FirstFrame
    $windowFrames = 0L
    $intervals = $CheckpointWindows.Split(',')
    foreach ($interval in $intervals) {
        $bounds = $interval.Split(':')
        $start = 0L; $stop = 0L
        if ($interval -notmatch '^[0-9]+:[0-9]+$' -or $bounds.Count -ne 2 -or
            -not [long]::TryParse($bounds[0], [ref] $start) -or -not [long]::TryParse($bounds[1], [ref] $stop) -or
            $start -lt $windowEnd -or $stop -gt $EndFrame -or $stop -le $start -or $stop - $start -gt 16) {
            throw 'CheckpointWindows must contain ordered short first:end intervals inside the run.'
        }
        $windowFrames += $stop - $start
        $windowEnd = $stop
    }
    if ($intervals.Count -gt 16 -or $windowFrames -gt 128 -or [long]$intervals[0].Split(':')[0] -ne $FirstFrame) {
        throw 'CheckpointWindows exceeds its short-window budget or differs from FirstFrame.'
    }
}
if ($DisablePrimaryProbe -and (-not $GameplayOracle -or $RecordInputs)) { throw 'DisablePrimaryProbe requires the independent oracle and a replay run.' }
if ($NativeScheduler -and -not $GameplayOracle) { throw 'NativeScheduler requires the independent gameplay oracle.' }
if ($NativePhysics -and -not $GameplayOracle) { throw 'NativePhysics requires the independent gameplay oracle.' }
if ($NativePhysicsOnly -and (-not $NativePhysics -or -not $GameplayOracle)) { throw 'NativePhysicsOnly requires NativePhysics and GameplayOracle; it is a diagnostic scope, not P0 acceptance.' }
if ($NativePhysicsDetails -and -not $NativePhysics) { throw 'NativePhysicsDetails requires NativePhysics.' }
if ($FieldDetails -and -not $GameplayOracle) { throw 'FieldDetails requires the independent gameplay oracle.' }
if ($OracleWindows -and (-not $GameplayOracle -or $SerialBoundaryOnly)) { throw 'OracleWindows requires GameplayOracle and cannot be combined with SerialBoundaryOnly.' }
if ($OracleWindows) {
    $priorOracleEnd = $FirstFrame
    foreach ($interval in $OracleWindows.Split(',')) {
        $oracleFirst = 0L
        $oracleEnd = 0L
        $bounds = $interval.Split(':')
        if ($bounds.Count -ne 2 -or $interval -notmatch '^[0-9]+:[0-9]+$' -or
            -not [long]::TryParse($bounds[0], [ref] $oracleFirst) -or
            -not [long]::TryParse($bounds[1], [ref] $oracleEnd) -or
            $oracleFirst -lt $priorOracleEnd -or $oracleEnd -gt $EndFrame -or $oracleFirst -ge $oracleEnd) {
            throw 'OracleWindows must contain ordered non-overlapping first:end intervals within the run.'
        }
        $priorOracleEnd = $oracleEnd
    }
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$phaseName = if ($EvidencePhase) { $EvidencePhase } elseif ($CheckpointPrototype) { 'p3' } else { 'p0' }
$phaseRoot = 'artifacts/scene-checkpoints/' + $phaseName
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $repo $phaseRoot)) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Output must be under $phaseRoot/." }
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a fresh evidence directory.' }
if (Get-Process hollow_knight,HollowKnightTAS.Companion,StudioScenarioHarness -ErrorAction SilentlyContinue) { throw 'A game or Studio session is already running.' }
$install = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
$modRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($install))
$modQuarantine = Join-Path (Join-Path $modRoot 'disabled') ('__HKTAS_VanillaProbe_' + [Guid]::NewGuid().ToString('N'))
$modMoves = [Collections.Generic.List[object]]::new()
$modIsolationStatus = 'not-requested'
. (Join-Path $PSScriptRoot 'LoadingProbeModIsolation.ps1')
$companion = Join-Path $install 'Companion/win-x64'
$clockRoot = Join-Path $companion 'ClockStartup'
$harnessBuild = Join-Path $repo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64'
$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$runtime = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'
$runtimeCore = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'
$clockPayload = Join-Path $repo 'src/HollowKnightTAS.ClockPayload/bin/Release/HollowKnightTAS.ClockPayload.dll'
$clockBridge = if ($ClockBridgePath) { [IO.Path]::GetFullPath($ClockBridgePath) }
    else { Join-Path $repo 'artifacts/scene-checkpoints/p0/native-clock-build/HollowKnightTAS.ClockBridge.dll' }
$injectorBuild = Join-Path $repo 'src/HollowKnightTAS.ClockInjector/bin/Release/net8.0-windows/win-x64'
$injectorFiles = @('HollowKnightTAS.ClockInjector.exe','HollowKnightTAS.ClockInjector.dll','HollowKnightTAS.ClockInjector.deps.json','HollowKnightTAS.ClockInjector.runtimeconfig.json')
$injectorConfig = Get-Content -LiteralPath (Join-Path $injectorBuild 'HollowKnightTAS.ClockInjector.runtimeconfig.json') -Raw | ConvertFrom-Json
$usesSharedInjector = $injectorConfig.runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks'
$sharedInjectorFiles = if ($usesSharedInjector) { @($injectorFiles | Where-Object { $_ -ne 'HollowKnightTAS.ClockInjector.exe' }) } else { @() }
if ($usesSharedInjector) {
    $studioConfig = Get-Content -LiteralPath (Join-Path $companion 'HollowKnightTAS.Companion.runtimeconfig.json') -Raw | ConvertFrom-Json
    $injectorVersion = ($injectorConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $studioVersion = ($studioConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    if (-not $injectorVersion -or $injectorVersion -ne $studioVersion) { throw 'Candidate injector and installed shared runtime versions differ.' }
    foreach ($name in $sharedInjectorFiles + @('hostpolicy.dll','hostfxr.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $companion $name) -PathType Leaf)) { throw "Installed shared injector dependency missing: $name" }
    }
}
$stageCompleteCandidate = $true
$usesClockCandidate = $true
# Runtime and harness reference the selected-profile API even for V3 controls.
# Always stage a coherent managed set, with the same backup/restore audit.
    $profileCandidateFiles = @(
        @{ name='runtime-core.dll'; source=(Join-Path $harnessBuild 'HollowKnightTAS.Core.dll'); destination=(Join-Path $install 'HollowKnightTAS.Core.dll') },
        @{ name='studio-core.dll'; source=(Join-Path $harnessBuild 'HollowKnightTAS.Core.dll'); destination=(Join-Path $companion 'HollowKnightTAS.Core.dll') },
        @{ name='studio.dll'; source=(Join-Path $harnessBuild 'HollowKnightTAS.Companion.dll'); destination=(Join-Path $companion 'HollowKnightTAS.Companion.dll') }
    )
$clockCandidateFiles = @('HollowKnightTAS.ClockPayload.dll','clock-build-manifest-v1.json','clock-build-whitelist-v1.json')
if ($stageCompleteCandidate) { $clockCandidateFiles += @('HollowKnightTAS.ClockBridge.dll') + $injectorFiles }
$saveRoot = Join-Path $env:USERPROFILE 'AppData/LocalLow/Team Cherry/Hollow Knight'
$hostFiles = @('StudioScenarioHarness.exe','StudioScenarioHarness.dll','StudioScenarioHarness.deps.json','StudioScenarioHarness.runtimeconfig.json')
foreach ($name in $hostFiles) { if (Test-Path -LiteralPath (Join-Path $companion $name)) { throw "Existing test host: $name" } }
foreach ($path in @($runtime,$runtimeCore,$bundleTool,$Movie) + @($hostFiles | ForEach-Object { Join-Path $harnessBuild $_ })) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file missing: $path" }
}
foreach ($entry in $profileCandidateFiles) {
    if (-not (Test-Path -LiteralPath $entry.source -PathType Leaf)) { throw "Required profile assembly missing: $($entry.source)" }
}
if ((Get-FileHash -LiteralPath $runtimeCore -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $harnessBuild 'HollowKnightTAS.Core.dll') -Algorithm SHA256).Hash) {
    throw 'Runtime and Studio harness Core assemblies differ. Build both candidates before the protected probe.'
}
if ($stageCompleteCandidate) {
    foreach ($name in $injectorFiles) { if (-not (Test-Path -LiteralPath (Join-Path $injectorBuild $name) -PathType Leaf)) { throw "Required injector file missing: $name" } }
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$backup = Join-Path $outputRoot 'installation-backup'
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($name in @('HollowKnightTAS.dll','companion.manifest.json')) { Copy-Item -LiteralPath (Join-Path $install $name) -Destination (Join-Path $backup $name) }
foreach ($entry in $profileCandidateFiles) { Copy-Item -LiteralPath $entry.destination -Destination (Join-Path $backup $entry.name) }
foreach ($name in $sharedInjectorFiles) { Copy-Item -LiteralPath (Join-Path $companion $name) -Destination (Join-Path $backup ('shared-' + $name)) }
if ($usesClockCandidate) {
    if (-not (Test-Path -LiteralPath $clockPayload -PathType Leaf)) { throw 'Built clock payload missing.' }
    if ($stageCompleteCandidate -and -not (Test-Path -LiteralPath $clockBridge -PathType Leaf)) { throw 'Built clock bridge missing.' }
    foreach ($name in $clockCandidateFiles) {
        Copy-Item -LiteralPath (Join-Path $clockRoot $name) -Destination (Join-Path $backup $name)
    }
}
function Read-SaveAudit {
    @(Get-ChildItem -LiteralPath $saveRoot -File -Filter 'user*' | Sort-Object Name | ForEach-Object {
        [ordered]@{ name=$_.Name; length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) | ConvertTo-Json -Depth 4 -Compress
}
$before = Read-SaveAudit
[IO.File]::WriteAllText((Join-Path $outputRoot 'saves-before.json'), $before)
$hostProcess = $null
try {
    Isolate-OtherMods
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $install 'HollowKnightTAS.dll')
    foreach ($entry in $profileCandidateFiles) { Copy-Item -LiteralPath $entry.source -Destination $entry.destination }
    if ($usesClockCandidate) {
        Copy-Item -LiteralPath $clockPayload -Destination (Join-Path $clockRoot 'HollowKnightTAS.ClockPayload.dll')
        if ($stageCompleteCandidate) {
            Copy-Item -LiteralPath $clockBridge -Destination (Join-Path $clockRoot 'HollowKnightTAS.ClockBridge.dll')
            foreach ($name in $injectorFiles) { Copy-Item -LiteralPath (Join-Path $injectorBuild $name) -Destination (Join-Path $clockRoot $name) }
            if ($usesSharedInjector) {
                foreach ($name in $sharedInjectorFiles) { Copy-Item -LiteralPath (Join-Path $injectorBuild $name) -Destination (Join-Path $companion $name) }
                # Match the production bundle's SDK apphost binding to the
                # managed injector beside Studio's shared runtime. Do not copy
                # another runtime, fall back to a machine installation or
                # rewrite the self-contained runtimeconfig as a workaround.
                $injectorProject = Join-Path $repo 'src/HollowKnightTAS.ClockInjector/HollowKnightTAS.ClockInjector.csproj'
                $resolved = & dotnet msbuild $injectorProject -t:ResolveFrameworkReferences -p:Configuration=Release `
                    -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -getProperty:AppHostSourcePath,NetCoreRoot
                if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve candidate apphost template.' }
                $properties = ($resolved -join "`n" | ConvertFrom-Json).Properties
                $sdkVersion = (& dotnet --version).Trim()
                if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve candidate SDK version.' }
                [Reflection.Assembly]::LoadFrom((Join-Path $properties.NetCoreRoot "sdk/$sdkVersion/Microsoft.NET.HostModel.dll")) | Out-Null
                $writer = [Microsoft.NET.HostModel.AppHost.HostWriter].GetMethods() |
                    Where-Object Name -eq 'CreateAppHost' | Select-Object -First 1
                $parameters = $writer.GetParameters()
                $hostArguments = [object[]]::new($parameters.Length)
                for ($i = 0; $i -lt $hostArguments.Length; $i++) { $hostArguments[$i] = $parameters[$i].DefaultValue }
                $hostArguments[0] = [string]$properties.AppHostSourcePath
                $hostArguments[1] = [string](Join-Path $clockRoot 'HollowKnightTAS.ClockInjector.exe')
                $hostArguments[2] = [IO.Path]::GetRelativePath($clockRoot, (Join-Path $companion 'HollowKnightTAS.ClockInjector.dll'))
                $hostArguments[3] = $false
                $hostArguments[4] = [string](Join-Path $companion 'HollowKnightTAS.ClockInjector.dll')
                $writer.Invoke($null, $hostArguments) | Out-Null
            }
        }
        $clockManifestPath = Join-Path $clockRoot 'clock-build-manifest-v1.json'
        $clockManifest = Get-Content -LiteralPath $clockManifestPath -Raw | ConvertFrom-Json
        $payloadIdentity = Get-Item -LiteralPath $clockPayload
        $payloadHash = (Get-FileHash -LiteralPath $clockPayload).Hash.ToLowerInvariant()
        $clockManifest.payloadSha256 = $payloadHash
        if ($stageCompleteCandidate) { $bridgeIdentity = Get-Item -LiteralPath $clockBridge; $bridgeHash = (Get-FileHash -LiteralPath $clockBridge).Hash.ToLowerInvariant(); $clockManifest.bridgeSha256 = $bridgeHash }
        foreach ($entry in $clockManifest.runtimeFileSet) {
            if ($entry.file -eq 'HollowKnightTAS.ClockPayload.dll') { $entry.length = $payloadIdentity.Length; $entry.sha256 = $payloadHash }
            if ($stageCompleteCandidate -and $entry.file -eq 'HollowKnightTAS.ClockBridge.dll') { $entry.length = $bridgeIdentity.Length; $entry.sha256 = $bridgeHash }
            if ($stageCompleteCandidate -and $entry.file -in $injectorFiles) {
                $file = Join-Path $clockRoot $entry.file
                $entry.length = (Get-Item -LiteralPath $file).Length
                $entry.sha256 = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
            }
        }
        if ($stageCompleteCandidate) {
            $clockManifest.injectorSha256 = (Get-FileHash -LiteralPath (Join-Path $clockRoot $injectorFiles[0])).Hash.ToLowerInvariant()
            $clockManifest.injectorManagedSha256 = (Get-FileHash -LiteralPath (Join-Path $clockRoot $injectorFiles[1])).Hash.ToLowerInvariant()
            $clockManifest.injectorDepsSha256 = (Get-FileHash -LiteralPath (Join-Path $clockRoot $injectorFiles[2])).Hash.ToLowerInvariant()
            $clockManifest.injectorRuntimeConfigSha256 = (Get-FileHash -LiteralPath (Join-Path $clockRoot $injectorFiles[3])).Hash.ToLowerInvariant()
        }
        $fileSetText = $clockManifest.runtimeFileSet | ConvertTo-Json -Compress -Depth 5
        $digest = [Security.Cryptography.SHA256]::Create()
        try { $clockManifest.runtimeFileSetSha256 = [Convert]::ToHexString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($fileSetText))).ToLowerInvariant() } finally { $digest.Dispose() }
        [IO.File]::WriteAllText($clockManifestPath, ($clockManifest | ConvertTo-Json -Depth 8))
        $whitelistPath = Join-Path $clockRoot 'clock-build-whitelist-v1.json'
        $whitelist = Get-Content -LiteralPath $whitelistPath -Raw | ConvertFrom-Json
        $whitelist.payloadSha256 = $payloadHash
        if ($stageCompleteCandidate) { $whitelist.bridgeSha256 = $bridgeHash }
        [IO.File]::WriteAllText($whitelistPath, ($whitelist | ConvertTo-Json -Depth 8))
    }
    foreach ($name in $hostFiles) { Copy-Item -LiteralPath (Join-Path $harnessBuild $name) -Destination (Join-Path $companion $name) }
    & dotnet $bundleTool sign (Join-Path $repo '.local/signing/companion-private.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature failed.' }
    & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature verification failed.' }
    $candidateIdentityFiles = @('HollowKnightTAS.dll','HollowKnightTAS.Core.dll','companion.manifest.json',
        'Companion/win-x64/HollowKnightTAS.Core.dll','Companion/win-x64/HollowKnightTAS.Companion.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockPayload.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockBridge.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockInjector.exe',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockInjector.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockInjector.deps.json',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockInjector.runtimeconfig.json',
        'Companion/win-x64/ClockStartup/clock-build-manifest-v1.json',
        'Companion/win-x64/ClockStartup/clock-build-whitelist-v1.json')
    $candidateIdentityFiles += @($sharedInjectorFiles | ForEach-Object { 'Companion/win-x64/' + $_ })
    $candidateIdentityFiles | ForEach-Object {
        $path = Join-Path $install $_
        [ordered]@{ name=$_; length=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'candidate-identity.json') -Encoding utf8
    for ($run=1; $run -le $Runs; $run++) {
        $destination = Join-Path $outputRoot ('run-{0:d2}' -f $run)
        New-Item -ItemType Directory -Path $destination | Out-Null
        $arguments = @('--headless','--loading-probe', ('--scenario-output="{0}"' -f $destination),
            ('--probe-movie="{0}"' -f ([IO.Path]::GetFullPath($Movie))),
            ('--probe-first={0}' -f $FirstFrame), ('--probe-end={0}' -f $EndFrame))
        if ($Light) { $arguments += '--probe-light' }
        if ($EngineLayout) { $arguments += '--probe-engine-layout' }
        if ($ClockRoundTrip) { $arguments += '--probe-clock-roundtrip' }
        if ($SynchronousFetch) { $arguments += '--probe-synchronous-fetch' }
        if ($CanonicalStartupClock) { $arguments += '--probe-canonical-startup-clock' }
        if ($RecordInputs) { $arguments += '--probe-record-inputs' }
        if ($GameplayOracle) { $arguments += '--probe-gameplay-oracle' }
        if ($OracleWindows) { $arguments += ('--probe-oracle-windows={0}' -f $OracleWindows) }
        if ($NativeScheduler) { $arguments += '--probe-native-scheduler' }
        if ($NativePhysics) { $arguments += '--probe-native-physics' }
        if ($NativePhysicsOnly) { $arguments += '--probe-native-physics-only' }
        if ($NativePhysicsDetails) { $arguments += '--probe-native-physics-details' }
        if ($FieldDetails) { $arguments += '--probe-field-details' }
        if ($DisablePrimaryProbe) { $arguments += '--probe-disable-primary' }
        if ($ModWallClock) { $arguments += '--probe-mod-wall-clock' }
        if ($SerialScenes) { $arguments += '--probe-serial-scenes' }
        if ($SerialBoundaryOnly) { $arguments += '--probe-serial-boundary-only' }
        if ($VirtualAudio) { $arguments += '--probe-virtual-audio' }
        if ($IsolatedOnline) { $arguments += '--probe-isolated-online' }
        if ($CanonicalParticles) { $arguments += '--probe-canonical-particles' }
        if ($CanonicalBoundary) { $arguments += '--probe-canonical-boundary' }
        if ($BoundaryInventory) { $arguments += '--probe-boundary-inventory' }
        if ($ExportVideo) { $arguments += '--probe-export-video' }
        if ($CheckpointPrototype) { $arguments += '--checkpoint-prototype' }
        if ($CheckpointWindows) { $arguments += ('--checkpoint-windows={0}' -f $CheckpointWindows) }
        if ($CheckpointPrototype) { $arguments += ('--checkpoint-after={0}' -f $CheckpointAfterFrame) }
        if ($EndpointHoldSeconds -gt 0) { $arguments += ('--probe-hold-seconds={0}' -f $EndpointHoldSeconds) }
        if ($DisableLegacyCalibration) { $arguments += '--probe-disable-legacy-calibration' }
        if ($RandomCalls) { $arguments += @('--probe-random-calls', "--probe-call-first=$CallFirst", "--probe-call-end=$CallEnd") }
        $hostProcess = Start-Process -FilePath (Join-Path $companion 'StudioScenarioHarness.exe') -ArgumentList $arguments `
            -WorkingDirectory $companion -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $destination 'stdout.log') -RedirectStandardError (Join-Path $destination 'stderr.log')
        [ordered]@{ run=$run; pid=$hostProcess.Id; status='running' } | ConvertTo-Json -Compress | Write-Output
        if (-not $hostProcess.WaitForExit(270000)) { $hostProcess.Kill(); throw 'Probe host timed out.' }
        $hostProcess.Refresh()
        foreach ($logName in @('Player.log','ModLog.txt')) {
            $logSource = Join-Path $saveRoot $logName
            if (Test-Path -LiteralPath $logSource -PathType Leaf) { Copy-Item -LiteralPath $logSource -Destination (Join-Path $destination $logName) }
        }
        $shadowReference = Join-Path $destination 'probe-shadows.json'
        if (Test-Path -LiteralPath $shadowReference -PathType Leaf) {
            $shadowParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'HollowKnightTAS/save-shadows')) + [IO.Path]::DirectorySeparatorChar
            $shadows = @((Get-Content -LiteralPath $shadowReference -Raw | ConvertFrom-Json).shadows)
            if ($shadows.Count -gt 8) { throw 'Probe owned-shadow budget exceeded.' }
            $shadowOrdinal = 0
            foreach ($shadow in $shadows) {
                $resolvedShadow = [IO.Path]::GetFullPath($shadow)
                if (-not $resolvedShadow.StartsWith($shadowParent, [StringComparison]::OrdinalIgnoreCase) -or
                    [IO.Path]::GetFileName($resolvedShadow) -notmatch '^full-run-[0-9a-f]{32}$') { throw 'Probe shadow reference escaped its owned boundary.' }
                $shadowOrdinal++
                $diagnosticNames = @('loading-boundary-probe.jsonl','loading-gameplay-oracle.jsonl','loading-state-inventory.json',
                    'loading-field-digests.json','loading-oracle-progress.json','loading-oracle-stage-progress.json',
                    'engine-clock-layout.json','engine-clock-roundtrip.json','full-run-fault.txt','boundary-inventory.json',
                    'canonical-boundary-progress.jsonl','boundary-observer-audit.json','boundary-field-schema.json',
                    'canonical-asset-origins.json','canonical-event-binding-audit.json','canonical-event-globals.json','canonical-event-data-audit.json')
                foreach ($file in (Get-ChildItem -LiteralPath $resolvedShadow -Recurse -File | Where-Object {
                    ($_.Name -in $diagnosticNames -or $_.Name -match '^loading-candidate-[0-9]+-inventory\.json$' -or
                        $_.Name -match '^boundary-digest-[0-9]+(?:-repeat)?\.json$' -or
                        $_.Name -match '^canonical-carry-[0-9]+\.bin$')
                })) {
                    $relative = [IO.Path]::GetRelativePath($resolvedShadow, $file.FullName)
                    $copy = Join-Path $destination (Join-Path ('recovered-shadow-{0:d2}' -f $shadowOrdinal) $relative)
                    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force | Out-Null
                    Copy-Item -LiteralPath $file.FullName -Destination $copy
                }
            }
        }
        if ($hostProcess.ExitCode -ne 0) { throw "Probe run $run failed with exit $($hostProcess.ExitCode)." }
        if ((Read-SaveAudit) -ne $before) { throw 'Original save audit mismatch.' }
        [ordered]@{ run=$run; status='trace-collected'; phaseP0Passed=$false } | ConvertTo-Json -Compress | Write-Output
    }
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { $hostProcess.Kill(); $hostProcess.WaitForExit() }
    # This runner admitted no existing game; only terminate a child carrying our probe environment
    # through its owned host if App shutdown has not already completed. Do not kill unrelated PIDs.
    $remainingGame = Get-Process hollow_knight -ErrorAction SilentlyContinue
    if ($remainingGame) {
        if ($OnlyTasMods) { $script:modIsolationStatus = 'retained-game-running'; Write-ModIsolation }
        Write-Warning 'Game remains running. Candidate installation retained; restore after identifying and closing the owned process.'
    } else {
        Restore-OtherMods
        foreach ($name in @('HollowKnightTAS.dll','companion.manifest.json')) {
            Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $install $name)
        }
        foreach ($entry in $profileCandidateFiles) { Copy-Item -LiteralPath (Join-Path $backup $entry.name) -Destination $entry.destination }
        foreach ($name in $sharedInjectorFiles) { Copy-Item -LiteralPath (Join-Path $backup ('shared-' + $name)) -Destination (Join-Path $companion $name) }
        if ($usesClockCandidate) {
            foreach ($name in $clockCandidateFiles) {
                Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $clockRoot $name)
            }
        }
        $withdrawn = Join-Path $outputRoot 'withdrawn-test-host'
        New-Item -ItemType Directory -Path $withdrawn | Out-Null
        foreach ($name in $hostFiles) {
            $source = [IO.Path]::GetFullPath((Join-Path $companion $name))
            if (-not $source.StartsWith([IO.Path]::GetFullPath($companion) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test-host path escaped installation.' }
            if (Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination (Join-Path $withdrawn $name) }
        }
        & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
        if ($LASTEXITCODE -ne 0) { throw 'Original installation verification failed.' }
    }
    $after = Read-SaveAudit
    [IO.File]::WriteAllText((Join-Path $outputRoot 'saves-after.json'), $after)
    if ($after -ne $before) { throw 'Original save audit mismatch on cleanup.' }
}
