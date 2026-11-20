function Test-T24NativeSceneLifecycleContract {
    param([Parameter(Mandatory)][object]$Telemetry)

    $expected = @{
        externalRngTransitionStartCount = 0
        externalRngGameplayReadyCount = 0
        externalRngSceneEpoch = 0
        externalRngGameplayReadyAlignmentHoldUpdateCount = 0
        externalSceneClockExclusionPreSynchronizationCount = 0
        externalSceneClockExclusionBeginCount = 0
        externalSceneClockExclusionFinishCount = 0
        externalSceneClockExclusionFrozenTimeUpdateCount = 0
        externalSceneClockExclusionFaultCode = 0
        externalSceneActivationAlignmentBeginCount = 0
        externalSceneActivationAlignmentHoldFrameCount = 0
        externalSceneActivationAlignmentReleaseCount = 0
        externalSceneFinishAlignmentBeginCount = 0
        externalSceneFinishAlignmentHoldFrameCount = 0
        externalSceneFinishAlignmentReleaseCount = 0
        externalSceneFrameAlignmentFaultCode = 0
        externalDeterministicClockSceneLoadFrameSkipCount = 0
        externalSceneActivationAlignmentFirstFrameCount = -1
        externalSceneActivationAlignmentFirstFramePhase = -1
        externalSceneActivationAlignmentLastFrameCount = -1
        externalSceneActivationAlignmentLastFramePhase = -1
        externalSceneFinishAlignmentLastFrameCount = -1
        externalSceneFinishAlignmentLastFramePhase = -1
        externalRngFirstGameplayReadyFrameCount = -1
        externalRngFirstGameplayReadyFramePhase = -1
        externalSceneRngPending = $false
        externalSceneClockExclusionActive = $false
    }
    foreach ($name in $expected.Keys) {
        $property = $Telemetry.PSObject.Properties[$name]
        if ($null -eq $property -or $null -eq $property.Value) { return $false }
        $value = $property.Value
        if ($expected[$name] -is [bool]) {
            if ($value -isnot [bool] -or $value -ne $expected[$name]) { return $false }
        }
        elseif (($value -isnot [int] -and $value -isnot [long]) -or $value -ne $expected[$name]) {
            return $false
        }
    }
    return $true
}

function Read-T24JsonShared {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $reader = [IO.StreamReader]::new(
            $stream,
            [Text.UTF8Encoding]::new($false, $true),
            $true,
            4096,
            $true)
        try {
            return $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-T24ClockTextSha256 {
    param([Parameter(Mandatory = $true)][string]$Value)

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

function Start-T24StartupClockedGame {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$GameExecutable,
        [Parameter(Mandatory = $true)][string[]]$GameArguments,
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)][string]$EvidenceDirectory
    )

    $GameExecutable = [IO.Path]::GetFullPath($GameExecutable)
    $BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
    $EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
    $injector = Join-Path $BundleRoot 'HollowKnightTAS.ClockInjector.exe'
    $injectorManaged = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.dll'
    $injectorDeps = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.deps.json'
    $injectorRuntimeConfig = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.runtimeconfig.json'
    $bridge = Join-Path $BundleRoot 'HollowKnightTAS.ClockBridge.dll'
    $payload = Join-Path $BundleRoot 'HollowKnightTAS.ClockPayload.dll'
    $manifestPath = Join-Path $BundleRoot 'clock-build-manifest-v1.json'
    $runtimeFiles = @(
        $bridge,
        $injectorDeps,
        $injectorManaged,
        $injector,
        $injectorRuntimeConfig,
        $payload)
    foreach ($file in @($runtimeFiles + $manifestPath + $GameExecutable)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Required startup-clock file is missing: $file"
        }
    }
    if (-not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) {
        throw "Clock evidence directory is missing: $EvidenceDirectory"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    $actualRuntimeFileSet = @(
        $runtimeFiles |
            Sort-Object { [IO.Path]::GetFileName($_) } |
            ForEach-Object {
                [ordered]@{
                    file = [IO.Path]::GetFileName($_)
                    length = (Get-Item -LiteralPath $_).Length
                    sha256 = (Get-FileHash `
                        -LiteralPath $_ `
                        -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            })
    $actualRuntimeFileSetSha256 = Get-T24ClockTextSha256 -Value (
        $actualRuntimeFileSet | ConvertTo-Json -Compress -Depth 5)
    $manifestRuntimeFileSet = @($manifest.runtimeFileSet)
    $runtimeFileSetExact =
        $manifestRuntimeFileSet.Count -eq $actualRuntimeFileSet.Count
    foreach ($actualFile in $actualRuntimeFileSet) {
        $matches = @(
            $manifestRuntimeFileSet |
                Where-Object { [string]$_.file -ceq [string]$actualFile.file })
        if ($matches.Count -ne 1 `
                -or [long]$matches[0].length -ne [long]$actualFile.length `
                -or [string]$matches[0].sha256 `
                    -cne [string]$actualFile.sha256) {
            $runtimeFileSetExact = $false
        }
    }
    $runtimeByName = @{}
    foreach ($file in $actualRuntimeFileSet) {
        $runtimeByName[[string]$file.file] = $file
    }
    if ([int]$manifest.schemaVersion -ne 2 `
            -or [string]$manifest.capabilityId `
                -ne 'native.clock-rng-pause.override.experimental.v31' `
            -or [string]$manifest.profile `
                -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
            -or [int]$manifest.bridgeAbi -ne 10 `
            -or [string]$manifest.startupPolicy `
                -ne 'create-suspended-early-apc-unity-then-bridge-v1' `
            -or [string]$manifest.randomSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$manifest.randomSynchronizationSeed -ne 1212896321 `
            -or -not [string]::Equals(
                [IO.Path]::GetFullPath([string]$manifest.gameExecutable),
                $GameExecutable,
                [StringComparison]::OrdinalIgnoreCase) `
            -or [string]$manifest.gameExecutableSha256 `
                -cne (Get-FileHash `
                    -LiteralPath $GameExecutable `
                    -Algorithm SHA256).Hash.ToLowerInvariant() `
            -or [string]$manifest.injectorManagedSha256 `
                -cne [string]$runtimeByName['HollowKnightTAS.ClockInjector.dll'].sha256 `
            -or [string]$manifest.bridgeSha256 `
                -cne [string]$runtimeByName['HollowKnightTAS.ClockBridge.dll'].sha256 `
            -or [string]$manifest.payloadSha256 `
                -cne [string]$runtimeByName['HollowKnightTAS.ClockPayload.dll'].sha256 `
            -or [string]$manifest.runtimeFileSetSha256 `
                -cne $actualRuntimeFileSetSha256 `
            -or -not $runtimeFileSetExact) {
        throw 'Startup clock bundle manifest or runtime file set is invalid.'
    }

    $runArgument = @(
        $GameArguments |
            Where-Object { $_ -like '--hktas-reference-run=*' })
    if ($runArgument.Count -ne 1) {
        throw 'Startup game arguments require exactly one T24 run id.'
    }
    $expectedRunId = [string]$runArgument[0].Substring(
        '--hktas-reference-run='.Length)
    $encodedArguments = [Convert]::ToBase64String(
        [Text.UTF8Encoding]::new($false).GetBytes(
            ($GameArguments | ConvertTo-Json -Compress)))
    $stdoutPath = Join-Path $EvidenceDirectory 'clock-injector.stdout.jsonl'
    $stderrPath = Join-Path $EvidenceDirectory 'clock-injector.stderr.jsonl'
    $launchStartedUtc = [DateTimeOffset]::UtcNow
    $game = $null
    try {
        $launcher = Start-Process `
            -FilePath $injector `
            -ArgumentList @(
                ('"--launch-game={0}"' -f $GameExecutable),
                "--launch-arguments-base64=$encodedArguments") `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
        if (-not $launcher.WaitForExit(15000)) {
            throw 'Startup Clock Launcher did not return within 15 seconds.'
        }
        $stdout = Get-Content -LiteralPath $stdoutPath -Raw
        $stderr = Get-Content -LiteralPath $stderrPath -Raw
        if ($launcher.ExitCode -ne 0) {
            throw "Startup Clock Launcher rejected the process: $stderr"
        }
        $result = $stdout | ConvertFrom-Json
        if ([int]$result.schemaVersion -ne 2 `
                -or [string]$result.status -ne 'startup-bridge-loaded' `
                -or [string]$result.capabilityId `
                    -ne 'native.clock-rng-pause.override.experimental.v31' `
                -or [string]$result.profile `
                    -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
                -or [int]$result.bridgeAbi -ne 10 `
                -or [string]$result.startupPolicy `
                    -ne 'create-suspended-early-apc-unity-then-bridge-v1' `
                -or [string]$result.runId -cne $expectedRunId `
                -or [int]$result.processId -le 0 `
                -or [long]$result.processStartUtcTicks -le 0 `
                -or [uint32]$result.primaryThreadId -eq 0 `
                -or [int]$result.queuedApcCount -ne 2 `
                -or [string]::Join('|', @($result.queuedApcOrder)) `
                    -cne 'UnityPlayer.dll|HollowKnightTAS.ClockBridge.dll' `
                -or -not [bool]$result.loadLibraryAddressEquivalent `
                -or [long]$result.localLoadLibraryAddress `
                    -ne [long]$result.verifiedRemoteLoadLibraryAddress `
                -or [string]$result.bridgeSha256 `
                    -cne [string]$manifest.bridgeSha256 `
                -or [string]$result.payloadSha256 `
                    -cne [string]$manifest.payloadSha256) {
            throw "Startup Clock Launcher returned an invalid contract: $stdout"
        }
        $game = Get-Process -Id ([int]$result.processId) -ErrorAction Stop
        $game.Refresh()
        if ($game.HasExited `
                -or $game.StartTime.ToUniversalTime().Ticks `
                    -ne [long]$result.processStartUtcTicks) {
            throw 'Startup-clock process identity changed before handoff.'
        }

        $audit = [ordered]@{
            schemaVersion = 2
            capabilityId = [string]$result.capabilityId
            profile = [string]$result.profile
            bridgeAbi = [int]$result.bridgeAbi
            startupPolicy = [string]$result.startupPolicy
            launchStartedUtc = $launchStartedUtc.ToString('o')
            launchCompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
            processId = $game.Id
            processStartUtcTicks = [long]$result.processStartUtcTicks
            primaryThreadId = [uint32]$result.primaryThreadId
            queuedApcCount = [int]$result.queuedApcCount
            queuedApcOrder = @($result.queuedApcOrder)
            loadLibraryAddressEquivalent =
                [bool]$result.loadLibraryAddressEquivalent
            bundleRoot = $BundleRoot
            bundleManifestSha256 = (Get-FileHash `
                -LiteralPath $manifestPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            injectorSha256 = (Get-FileHash `
                -LiteralPath $injector `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            injectorManagedSha256 = [string]$manifest.injectorManagedSha256
            injectorDepsSha256 = [string]$manifest.injectorDepsSha256
            injectorRuntimeConfigSha256 =
                [string]$manifest.injectorRuntimeConfigSha256
            runtimeFileSetSha256 = $actualRuntimeFileSetSha256
            bridgeSha256 = [string]$result.bridgeSha256
            payloadSha256 = [string]$result.payloadSha256
            randomSynchronizationPolicy =
                [string]$result.randomSynchronizationPolicy
            randomSynchronizationSeed =
                [int]$result.randomSynchronizationSeed
            processImageSha256 = [string]$result.processImageSha256
            unityPlayerSha256 = [string]$result.unityPlayerSha256
            assemblyCSharpSha256 = [string]$result.assemblyCSharpSha256
            stderrEmpty = [string]::IsNullOrWhiteSpace($stderr)
        }
        $auditPath = Join-Path $EvidenceDirectory 'clock-injection.json'
        $audit |
            ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath $auditPath -Encoding utf8NoBOM
        return [pscustomobject]@{
            GameProcess = $game
            Audit = $audit
        }
    }
    catch {
        if ($null -ne $game `
                -and -not $game.HasExited) {
            Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Invoke-T24ClockInjection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process]$GameProcess,

        [Parameter(Mandatory = $true)]
        [string]$BundleRoot,

        [Parameter(Mandatory = $true)]
        [string]$EvidenceDirectory
    )

    $BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
    $EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
    $injector = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.exe'
    $injectorManaged = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.dll'
    $injectorDeps = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.deps.json'
    $injectorRuntimeConfig = Join-Path `
        $BundleRoot `
        'HollowKnightTAS.ClockInjector.runtimeconfig.json'
    $bridge = Join-Path $BundleRoot 'HollowKnightTAS.ClockBridge.dll'
    $payload = Join-Path $BundleRoot 'HollowKnightTAS.ClockPayload.dll'
    $manifestPath = Join-Path `
        $BundleRoot `
        'clock-build-manifest-v1.json'
    $runtimeFiles = @(
        $bridge,
        $injectorDeps,
        $injectorManaged,
        $injector,
        $injectorRuntimeConfig,
        $payload
    )
    foreach ($file in @($runtimeFiles + $manifestPath)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Required fixed clock bundle file is missing: $file"
        }
    }
    if (-not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) {
        throw "Clock evidence directory is missing: $EvidenceDirectory"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    $actualRuntimeFileSet = @(
        $runtimeFiles |
            Sort-Object { [IO.Path]::GetFileName($_) } |
            ForEach-Object {
                [ordered]@{
                    file = [IO.Path]::GetFileName($_)
                    length = (Get-Item -LiteralPath $_).Length
                    sha256 = (Get-FileHash `
                        -LiteralPath $_ `
                        -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $actualRuntimeFileSetSha256 = Get-T24ClockTextSha256 -Value (
        $actualRuntimeFileSet | ConvertTo-Json -Compress -Depth 5)
    $manifestRuntimeFileSet = @($manifest.runtimeFileSet)
    $runtimeFileSetExact =
        $manifestRuntimeFileSet.Count -eq $actualRuntimeFileSet.Count
    foreach ($actualFile in $actualRuntimeFileSet) {
        $matches = @(
            $manifestRuntimeFileSet |
                Where-Object { [string]$_.file -ceq [string]$actualFile.file })
        if ($matches.Count -ne 1 `
                -or [long]$matches[0].length -ne [long]$actualFile.length `
                -or [string]$matches[0].sha256 `
                    -cne [string]$actualFile.sha256) {
            $runtimeFileSetExact = $false
        }
    }
    if ([int]$manifest.schemaVersion -ne 2 `
            -or [string]$manifest.profile `
                -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
            -or [string]$manifest.capabilityId `
                -ne 'native.clock-rng-pause.override.experimental.v31' `
            -or [int]$manifest.bridgeAbi -ne 10 `
            -or [string]$manifest.randomSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$manifest.randomSynchronizationSeed -ne 1212896321 `
            -or [string]$manifest.injectorSha256 `
                -cne [string]$actualRuntimeFileSet[3].sha256 `
            -or [string]$manifest.injectorManagedSha256 `
                -cne [string]$actualRuntimeFileSet[2].sha256 `
            -or [string]$manifest.injectorDepsSha256 `
                -cne [string]$actualRuntimeFileSet[1].sha256 `
            -or [string]$manifest.injectorRuntimeConfigSha256 `
                -cne [string]$actualRuntimeFileSet[4].sha256 `
            -or [string]$manifest.bridgeSha256 `
                -cne [string]$actualRuntimeFileSet[0].sha256 `
            -or [string]$manifest.payloadSha256 `
                -cne [string]$actualRuntimeFileSet[5].sha256 `
            -or [string]$manifest.runtimeFileSetSha256 `
                -cne $actualRuntimeFileSetSha256 `
            -or -not $runtimeFileSetExact) {
        throw 'Clock bundle manifest or loaded runtime file set is invalid.'
    }

    $GameProcess.Refresh()
    if ($GameProcess.HasExited) {
        throw 'Hollow Knight exited before clock injection.'
    }
    $startTicks = $GameProcess.StartTime.ToUniversalTime().Ticks
    $stdoutPath = Join-Path $EvidenceDirectory 'clock-injector.stdout.jsonl'
    $stderrPath = Join-Path $EvidenceDirectory 'clock-injector.stderr.jsonl'
    $injectionStartedUtc = [DateTimeOffset]::UtcNow
    $process = Start-Process `
        -FilePath $injector `
        -ArgumentList @(
            "--pid=$($GameProcess.Id)",
            "--start-time-utc-ticks=$startTicks"
        ) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    $stderr = Get-Content -LiteralPath $stderrPath -Raw
    if ($process.ExitCode -ne 0) {
        throw (
            'Clock Injector rejected the exact Hollow Knight process: ' `
            + $stderr)
    }
    $result = $stdout | ConvertFrom-Json
    if ([string]$result.status -ne 'bridge-loaded' `
            -or [string]$result.capabilityId `
                -ne 'native.clock-rng-pause.override.experimental.v31' `
            -or [string]$result.profile `
                -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
            -or [int]$result.bridgeAbi -ne 10 `
            -or [string]$result.randomSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$result.randomSynchronizationSeed -ne 1212896321 `
            -or [int]$result.processId -ne $GameProcess.Id `
            -or [long]$result.processStartUtcTicks -ne $startTicks) {
        throw (
            'Clock Injector returned an invalid success contract: ' `
            + $stdout)
    }
    if ([string]$result.bridgeSha256 -cne [string]$manifest.bridgeSha256 `
            -or [string]$result.payloadSha256 `
                -cne [string]$manifest.payloadSha256) {
        throw 'Clock Injector output does not match its hash-complete manifest.'
    }

    $audit = [ordered]@{
        schemaVersion = 1
        capabilityId = [string]$result.capabilityId
        profile = [string]$result.profile
        bridgeAbi = [int]$result.bridgeAbi
        injectionStartedUtc = $injectionStartedUtc.ToString('o')
        injectionCompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        processId = $GameProcess.Id
        processStartUtcTicks = $startTicks
        bundleRoot = $BundleRoot
        bundleManifestSha256 = (Get-FileHash `
            -LiteralPath $manifestPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        injectorSha256 = (Get-FileHash `
            -LiteralPath $injector `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        injectorManagedSha256 =
            [string]$manifest.injectorManagedSha256
        injectorDepsSha256 = [string]$manifest.injectorDepsSha256
        injectorRuntimeConfigSha256 =
            [string]$manifest.injectorRuntimeConfigSha256
        runtimeFileSetSha256 = $actualRuntimeFileSetSha256
        bridgeSha256 = [string]$result.bridgeSha256
        payloadSha256 = [string]$result.payloadSha256
        randomSynchronizationPolicy =
            [string]$result.randomSynchronizationPolicy
        randomSynchronizationSeed =
            [int]$result.randomSynchronizationSeed
        processImageSha256 = [string]$result.processImageSha256
        stderrEmpty = [string]::IsNullOrWhiteSpace($stderr)
    }
    $auditPath = Join-Path $EvidenceDirectory 'clock-injection.json'
    $audit |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $auditPath -Encoding utf8NoBOM
    return $audit
}

function Test-T24ClockStatus {
    param([object]$Status)

    if ($null -eq $Status `
            -or [string]$Status.phase -notin @(
                'waiting-for-external-ui-load',
                'ready-for-input',
                'waiting-for-recording-time-target',
                'waiting-for-recording-arm-release') `
            -or [int]$Status.vSyncCount -ne 0) {
        return $false
    }
    $fixed = [single]$Status.fixedDeltaTime
    $capture = [single]$Status.captureDeltaTime
    if ([single]::IsNaN($fixed) `
            -or [single]::IsInfinity($fixed) `
            -or $fixed -le 0) {
        return $false
    }
    $expectedRate = [int][Math]::Round(
        1.0 / [double]$fixed,
        [MidpointRounding]::AwayFromZero)
    $baseValid = [int]$Status.targetFrameRate -eq $expectedRate `
        -and [BitConverter]::SingleToInt32Bits($capture) `
            -eq [BitConverter]::SingleToInt32Bits($fixed)
    if (-not $baseValid) {
        return $false
    }
    if ([string]$Status.phase -ne 'waiting-for-recording-arm-release') {
        return $true
    }
    $timeRawDouble = [double]$Status.timeRawDouble.value
    $fixedTimeRawDouble = [double]$Status.fixedTimeRawDouble.value
    return [bool]$Status.externalDoublePhaseCalibrationApplied `
        -and [single]$Status.timeMinusFixed -eq [single]0.0 `
        -and [int]$Status.externalDoublePhaseCalibrationAttempts -gt 0 `
        -and [int]$Status.externalDoublePhaseCalibrationFaultCode -eq 0 `
        -and [string]$Status.externalDoublePhaseFinalResidualBits `
            -eq '0000000000000000' `
        -and [string]$Status.externalDoublePhaseFinalResidual.canonicalHex `
            -eq '0000000000000000' `
        -and [string]$Status.externalDoublePhaseLastCorrectionBits `
            -ne '00000000' `
        -and [string]$Status.timeRawDouble.canonicalHex `
            -eq [string]$Status.fixedTimeRawDouble.canonicalHex `
        -and $timeRawDouble -eq $fixedTimeRawDouble
}

function Wait-T24ClockProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$GetStatus,

        [ValidateRange(1, 30)]
        [int]$TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        $last = & $GetStatus
        if (Test-T24ClockStatus -Status $last) {
            return $last
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw (
        'Clock payload did not establish the required Unity frame profile: ' `
        + ($last | ConvertTo-Json -Compress))
}
