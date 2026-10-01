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
    [switch] $NativeScheduler,
    [switch] $DisablePrimaryProbe,
    [switch] $RelaxedResources
)
$ErrorActionPreference = 'Stop'
if ($CanonicalStartupClock -and (-not $SynchronousFetch -or -not $DisableLegacyCalibration)) { throw 'The v4 experiment requires synchronous fetch and disabled legacy calibration.' }
if ($RecordInputs -and (-not $CanonicalStartupClock -or $Runs -ne 1)) { throw 'RecordInputs requires the v4 profile and one run.' }
if ($DisablePrimaryProbe -and (-not $GameplayOracle -or $RecordInputs)) { throw 'DisablePrimaryProbe requires the independent oracle and a replay run.' }
if ($NativeScheduler -and -not $GameplayOracle) { throw 'NativeScheduler requires the independent gameplay oracle.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/scene-checkpoints/p0')) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be under artifacts/scene-checkpoints/p0/.' }
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a fresh evidence directory.' }
if (Get-Process hollow_knight,HollowKnightTAS.Companion,StudioScenarioHarness -ErrorAction SilentlyContinue) { throw 'A game or Studio session is already running.' }
$install = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
$companion = Join-Path $install 'Companion/win-x64'
$clockRoot = Join-Path $companion 'ClockStartup'
$harnessBuild = Join-Path $repo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64'
$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$runtime = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'
$clockPayload = Join-Path $repo 'src/HollowKnightTAS.ClockPayload/bin/Release/HollowKnightTAS.ClockPayload.dll'
$clockBridge = Join-Path $repo 'artifacts/scene-checkpoints/p0/native-clock-build/HollowKnightTAS.ClockBridge.dll'
$injectorBuild = Join-Path $repo 'src/HollowKnightTAS.ClockInjector/bin/Release/net8.0-windows/win-x64'
$injectorFiles = @('HollowKnightTAS.ClockInjector.exe','HollowKnightTAS.ClockInjector.dll','HollowKnightTAS.ClockInjector.deps.json','HollowKnightTAS.ClockInjector.runtimeconfig.json')
$usesClockCandidate = $DisableLegacyCalibration -or $CanonicalStartupClock
$profileCandidateFiles = @()
if ($CanonicalStartupClock) {
    $profileCandidateFiles = @(
        @{ name='runtime-core.dll'; source=(Join-Path $harnessBuild 'HollowKnightTAS.Core.dll'); destination=(Join-Path $install 'HollowKnightTAS.Core.dll') },
        @{ name='studio-core.dll'; source=(Join-Path $harnessBuild 'HollowKnightTAS.Core.dll'); destination=(Join-Path $companion 'HollowKnightTAS.Core.dll') },
        @{ name='studio.dll'; source=(Join-Path $harnessBuild 'HollowKnightTAS.Companion.dll'); destination=(Join-Path $companion 'HollowKnightTAS.Companion.dll') }
    )
}
$clockCandidateFiles = @('HollowKnightTAS.ClockPayload.dll','clock-build-manifest-v1.json','clock-build-whitelist-v1.json')
if ($CanonicalStartupClock) { $clockCandidateFiles += @('HollowKnightTAS.ClockBridge.dll') + $injectorFiles }
$saveRoot = Join-Path $env:USERPROFILE 'AppData/LocalLow/Team Cherry/Hollow Knight'
$hostFiles = @('StudioScenarioHarness.exe','StudioScenarioHarness.dll','StudioScenarioHarness.deps.json','StudioScenarioHarness.runtimeconfig.json')
foreach ($name in $hostFiles) { if (Test-Path -LiteralPath (Join-Path $companion $name)) { throw "Existing test host: $name" } }
foreach ($path in @($runtime,$bundleTool,$Movie) + @($hostFiles | ForEach-Object { Join-Path $harnessBuild $_ })) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file missing: $path" }
}
foreach ($entry in $profileCandidateFiles) {
    if (-not (Test-Path -LiteralPath $entry.source -PathType Leaf)) { throw "Required profile assembly missing: $($entry.source)" }
}
if ($CanonicalStartupClock) {
    foreach ($name in $injectorFiles) { if (-not (Test-Path -LiteralPath (Join-Path $injectorBuild $name) -PathType Leaf)) { throw "Required injector file missing: $name" } }
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$backup = Join-Path $outputRoot 'installation-backup'
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($name in @('HollowKnightTAS.dll','companion.manifest.json')) { Copy-Item -LiteralPath (Join-Path $install $name) -Destination (Join-Path $backup $name) }
foreach ($entry in $profileCandidateFiles) { Copy-Item -LiteralPath $entry.destination -Destination (Join-Path $backup $entry.name) }
if ($usesClockCandidate) {
    if (-not (Test-Path -LiteralPath $clockPayload -PathType Leaf)) { throw 'Built clock payload missing.' }
    if ($CanonicalStartupClock -and -not (Test-Path -LiteralPath $clockBridge -PathType Leaf)) { throw 'Built clock bridge missing.' }
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
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $install 'HollowKnightTAS.dll')
    foreach ($entry in $profileCandidateFiles) { Copy-Item -LiteralPath $entry.source -Destination $entry.destination }
    if ($usesClockCandidate) {
        Copy-Item -LiteralPath $clockPayload -Destination (Join-Path $clockRoot 'HollowKnightTAS.ClockPayload.dll')
        if ($CanonicalStartupClock) {
            Copy-Item -LiteralPath $clockBridge -Destination (Join-Path $clockRoot 'HollowKnightTAS.ClockBridge.dll')
            foreach ($name in $injectorFiles) { Copy-Item -LiteralPath (Join-Path $injectorBuild $name) -Destination (Join-Path $clockRoot $name) }
        }
        $clockManifestPath = Join-Path $clockRoot 'clock-build-manifest-v1.json'
        $clockManifest = Get-Content -LiteralPath $clockManifestPath -Raw | ConvertFrom-Json
        $payloadIdentity = Get-Item -LiteralPath $clockPayload
        $payloadHash = (Get-FileHash -LiteralPath $clockPayload).Hash.ToLowerInvariant()
        $clockManifest.payloadSha256 = $payloadHash
        if ($CanonicalStartupClock) { $bridgeIdentity = Get-Item -LiteralPath $clockBridge; $bridgeHash = (Get-FileHash -LiteralPath $clockBridge).Hash.ToLowerInvariant(); $clockManifest.bridgeSha256 = $bridgeHash }
        foreach ($entry in $clockManifest.runtimeFileSet) {
            if ($entry.file -eq 'HollowKnightTAS.ClockPayload.dll') { $entry.length = $payloadIdentity.Length; $entry.sha256 = $payloadHash }
            if ($CanonicalStartupClock -and $entry.file -eq 'HollowKnightTAS.ClockBridge.dll') { $entry.length = $bridgeIdentity.Length; $entry.sha256 = $bridgeHash }
            if ($CanonicalStartupClock -and $entry.file -in $injectorFiles) {
                $file = Join-Path $injectorBuild $entry.file
                $entry.length = (Get-Item -LiteralPath $file).Length
                $entry.sha256 = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
            }
        }
        if ($CanonicalStartupClock) {
            $clockManifest.injectorSha256 = (Get-FileHash -LiteralPath (Join-Path $injectorBuild $injectorFiles[0])).Hash.ToLowerInvariant()
            $clockManifest.injectorManagedSha256 = (Get-FileHash -LiteralPath (Join-Path $injectorBuild $injectorFiles[1])).Hash.ToLowerInvariant()
            $clockManifest.injectorDepsSha256 = (Get-FileHash -LiteralPath (Join-Path $injectorBuild $injectorFiles[2])).Hash.ToLowerInvariant()
            $clockManifest.injectorRuntimeConfigSha256 = (Get-FileHash -LiteralPath (Join-Path $injectorBuild $injectorFiles[3])).Hash.ToLowerInvariant()
        }
        $fileSetText = $clockManifest.runtimeFileSet | ConvertTo-Json -Compress -Depth 5
        $digest = [Security.Cryptography.SHA256]::Create()
        try { $clockManifest.runtimeFileSetSha256 = [Convert]::ToHexString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($fileSetText))).ToLowerInvariant() } finally { $digest.Dispose() }
        [IO.File]::WriteAllText($clockManifestPath, ($clockManifest | ConvertTo-Json -Depth 8))
        $whitelistPath = Join-Path $clockRoot 'clock-build-whitelist-v1.json'
        $whitelist = Get-Content -LiteralPath $whitelistPath -Raw | ConvertFrom-Json
        $whitelist.payloadSha256 = $payloadHash
        if ($CanonicalStartupClock) { $whitelist.bridgeSha256 = $bridgeHash }
        [IO.File]::WriteAllText($whitelistPath, ($whitelist | ConvertTo-Json -Depth 8))
    }
    foreach ($name in $hostFiles) { Copy-Item -LiteralPath (Join-Path $harnessBuild $name) -Destination (Join-Path $companion $name) }
    & dotnet $bundleTool sign (Join-Path $repo '.local/signing/companion-private.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature failed.' }
    & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature verification failed.' }
    @('HollowKnightTAS.dll','HollowKnightTAS.Core.dll','companion.manifest.json',
        'Companion/win-x64/HollowKnightTAS.Core.dll','Companion/win-x64/HollowKnightTAS.Companion.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockPayload.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockBridge.dll',
        'Companion/win-x64/ClockStartup/HollowKnightTAS.ClockInjector.dll',
        'Companion/win-x64/ClockStartup/clock-build-manifest-v1.json',
        'Companion/win-x64/ClockStartup/clock-build-whitelist-v1.json') | ForEach-Object {
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
        if ($NativeScheduler) { $arguments += '--probe-native-scheduler' }
        if ($DisablePrimaryProbe) { $arguments += '--probe-disable-primary' }
        if ($RelaxedResources) { $arguments += '--probe-relaxed-resources' }
        if ($DisableLegacyCalibration) { $arguments += '--probe-disable-legacy-calibration' }
        if ($RandomCalls) { $arguments += @('--probe-random-calls', "--probe-call-first=$CallFirst", "--probe-call-end=$CallEnd") }
        $hostProcess = Start-Process -FilePath (Join-Path $companion 'StudioScenarioHarness.exe') -ArgumentList $arguments `
            -WorkingDirectory $companion -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $destination 'stdout.log') -RedirectStandardError (Join-Path $destination 'stderr.log')
        [ordered]@{ run=$run; pid=$hostProcess.Id; status='running' } | ConvertTo-Json -Compress | Write-Output
        if (-not $hostProcess.WaitForExit(270000)) { $hostProcess.Kill(); throw 'Probe host timed out.' }
        $hostProcess.Refresh()
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
        Write-Warning 'Game remains running. Candidate installation retained; restore after identifying and closing the owned process.'
    } else {
        foreach ($name in @('HollowKnightTAS.dll','companion.manifest.json')) {
            Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $install $name)
        }
        foreach ($entry in $profileCandidateFiles) { Copy-Item -LiteralPath (Join-Path $backup $entry.name) -Destination $entry.destination }
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
