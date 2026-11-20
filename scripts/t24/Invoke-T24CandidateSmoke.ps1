[CmdletBinding()]
param(
    [ValidateSet(
        'tas-passive',
        'tas-continuous',
        'tas-sequential',
        'tas-batch',
        'tas-manual-ui')]
    [string]$Mode = 'tas-continuous',

    [Parameter(Mandatory)]
    [string]$ReferenceTracePath,

    [Parameter(Mandatory)]
    [string]$NegativeControlEnvelopePath,

    [string]$SupplementalReferenceCatalogPath = '',

    [string]$MoviePath = '',

    [string]$ScenarioContractPath = '',

    [string]$CompareOnlyCandidateTracePath = '',

    [ValidateRange(120, 10000)]
    [int]$MaxTicks = 1200,

    [ValidateRange(1, 4)]
    [int]$FixtureSlot = 2,

    [string]$ManagedDirectory =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed',

    [string]$PersistentDataDirectory =
        'C:\Users\33361\AppData\LocalLow\Team Cherry\Hollow Knight',

    [string]$EvidenceRoot = '',

    [Parameter(Mandatory = $true)]
    [string]$ClockBundleRoot,

    [ValidateRange(30, 300)]
    [int]$MaxLaunchSeconds = 180,

    [ValidateRange(30, 600)]
    [int]$RunTimeoutSeconds = 300,

    [ValidateRange(0, 5000)]
    [int]$PauseDwellMilliseconds = 0,

    [ValidateRange(60, 300)]
    [int]$FixtureReadyTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-T24RecordingPhaseContract {
    param(
        [Parameter(Mandatory)][object]$Telemetry,
        [Parameter(Mandatory)][bool]$RootRequestApplied
    )

    $armFrameCount = [int]$Telemetry.recordingArmBoundaryFrameCount
    $remainingNormalFrames =
        [int]$Telemetry.externalRecordingPhaseNormalizationRemainingNormalFrameCount
    $restoreFramePhase =
        [int]$Telemetry.externalRecordingPhaseNormalizationRestoreFramePhase
    $expectedRestoreFramePhase =
        ((-$remainingNormalFrames % 4) + 4) % 4
    $rootRequestValid = if ($RootRequestApplied) {
        [int]$Telemetry.externalRecordingRootRequestObservedFrameCount `
            -eq ($armFrameCount + 1) `
        -and [int]$Telemetry.externalRecordingRootFramePhase -eq 0
    }
    else {
        [int]$Telemetry.externalRecordingRootRequestObservedFrameCount -eq -1 `
        -and [int]$Telemetry.externalRecordingRootFramePhase -eq -1
    }

    return [string]$Telemetry.recordingAbsoluteTimeTarget.canonicalHex `
            -eq '44400000' `
        -and [bool]$Telemetry.recordingArmBoundaryReached `
        -and $armFrameCount -gt 0 `
        -and [string]$Telemetry.recordingArmBoundaryTimeRaw.canonicalHex `
            -eq '44400000' `
        -and [string]$Telemetry.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -eq '44400000' `
        -and [int]$Telemetry.recordingFramePhaseModulo -eq 4 `
        -and [int]$Telemetry.recordingFramePhaseTarget -eq 0 `
        -and [int]$Telemetry.recordingArmBoundaryFramePhase -eq 0 `
        -and $rootRequestValid `
        -and -not [bool]$Telemetry.externalRecordingPhaseNormalizationActive `
        -and [bool]$Telemetry.externalRecordingPhaseNormalizationCompleted `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationBeginCount -eq 1 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationHoldFrameCount -ge 1 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationHoldFrameCount -le 8 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationReleaseCount -eq 1 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationLastFrameCount -gt 0 `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationLastFramePhase `
            -eq $restoreFramePhase `
        -and [string]$Telemetry.externalRecordingPhaseNormalizationHeldTimeBits `
            -ne '00000000' `
        -and [string]$Telemetry.externalRecordingPhaseNormalizationHeldTimeDoubleBits `
            -ne '0000000000000000' `
        -and $remainingNormalFrames -ge 1 `
        -and $remainingNormalFrames -le 16 `
        -and $restoreFramePhase -eq $expectedRestoreFramePhase `
        -and [int]$Telemetry.externalRecordingPhaseNormalizationFaultCode -eq 0
}

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 candidate harness requires PowerShell 7 or newer.'
}
if ($Mode -eq 'tas-passive' `
        -and [string]::IsNullOrWhiteSpace(
            $CompareOnlyCandidateTracePath)) {
    throw (
        'Live tas-passive capture must be produced by ' `
        + 'Invoke-T24ReferenceSmoke.ps1 -Mode tas-passive and then ' `
        + 'validated here with -CompareOnlyCandidateTracePath.')
}
if ($PauseDwellMilliseconds -ne 0 -and $Mode -ne 'tas-sequential') {
    throw 'PauseDwellMilliseconds is only valid for tas-sequential.'
}

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
. (Join-Path $PSScriptRoot 'T24ClockHarness.ps1')
$filesystemIsolationScriptPath = Join-Path `
    $PSScriptRoot `
    'T24FilesystemIsolation.ps1'
. $filesystemIsolationScriptPath
$manualUiAutomationScriptPath = Join-Path `
    $PSScriptRoot `
    'T24CompanionUiAutomation.ps1'
. $manualUiAutomationScriptPath
$ManagedDirectory = [IO.Path]::GetFullPath($ManagedDirectory)
$GameExecutable = Join-Path `
    (Split-Path -Parent (Split-Path -Parent $ManagedDirectory)) `
    'hollow_knight.exe'
$ClockBundleRoot = [IO.Path]::GetFullPath($ClockBundleRoot)
$PersistentDataDirectory =
    [IO.Path]::GetFullPath($PersistentDataDirectory)
$ReferenceTracePath = [IO.Path]::GetFullPath($ReferenceTracePath)
$NegativeControlEnvelopePath =
    [IO.Path]::GetFullPath($NegativeControlEnvelopePath)
if (-not [string]::IsNullOrWhiteSpace(
        $SupplementalReferenceCatalogPath)) {
    $SupplementalReferenceCatalogPath = [IO.Path]::GetFullPath(
        $SupplementalReferenceCatalogPath)
}
$ReferenceBaselinePath = Join-Path `
    (Split-Path -Parent $ReferenceTracePath) `
    'baseline.json'
$ReferencePhasePath = Join-Path `
    (Split-Path -Parent $ReferenceTracePath) `
    'input-phase.jsonl'
if (-not [string]::IsNullOrWhiteSpace($CompareOnlyCandidateTracePath)) {
    $CompareOnlyCandidateTracePath =
        [IO.Path]::GetFullPath($CompareOnlyCandidateTracePath)
}
if ([string]::IsNullOrWhiteSpace($MoviePath)) {
    $MoviePath = Join-Path `
        $repoRoot `
        'fixtures\t24\attack-double-jump-v1.hktas'
}
$MoviePath = [IO.Path]::GetFullPath($MoviePath)
$scenarioContract = $null
if (-not [string]::IsNullOrWhiteSpace($ScenarioContractPath)) {
    $ScenarioContractPath = [IO.Path]::GetFullPath($ScenarioContractPath)
    if (-not (Test-Path `
            -LiteralPath $ScenarioContractPath `
            -PathType Leaf)) {
        throw "Scenario contract is missing: $ScenarioContractPath"
    }
    $scenarioContract = Get-Content `
        -LiteralPath $ScenarioContractPath `
        -Raw |
        ConvertFrom-Json -AsHashtable -Depth 50
    $declaredMoviePath = [IO.Path]::GetFullPath(
        (Join-Path `
            $repoRoot `
            ([string]$scenarioContract['movie']['path'])))
    if ([int]$scenarioContract['fixture']['slot'] -ne $FixtureSlot `
            -or [int]$scenarioContract['movie']['expandedTicks'] `
                -ne $MaxTicks `
            -or -not [string]::Equals(
                $declaredMoviePath,
                $MoviePath,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Scenario contract fixture slot, expanded ticks, or movie path ' `
            + 'does not match the candidate run.')
    }
}

$modsDirectory = Join-Path $ManagedDirectory 'Mods'
$tasInstall = Join-Path $modsDirectory 'HollowKnightTAS'
$observerBuild = Join-Path `
    $repoRoot `
    'src\HollowKnightTAS.ReferenceObserver\bin\Debug'
$observerFiles = @(
    'HollowKnightTAS.ReferenceObserver.dll',
    'HollowKnightTAS.GameObservation.dll',
    'HollowKnightTAS.Core.dll'
)
$settingsPath = Join-Path `
    $PersistentDataDirectory `
    'HollowKnightTASMod.GlobalSettings.json'
$settingsBackupPath = $settingsPath + '.bak'
$localApplicationData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
$automationParent = Join-Path $localApplicationData 'HollowKnightTAS'
$automationRoot = Join-Path $automationParent 'automation'
$bootstrapPath = Join-Path $automationRoot 'automation-v1.json'
$sessionRoot = Join-Path `
    $PersistentDataDirectory `
    'HollowKnightTAS\sessions'

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $campaign = 't24-{0}-{1}-{2}' -f `
        $Mode, `
        [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), `
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidenceRoot = Join-Path `
        $repoRoot `
        "artifacts\vanilla-equivalence\$campaign"
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$atomicWriteProbe = Join-Path `
    (Join-Path $EvidenceRoot "traces\$Mode") `
    ('status.json.tmp-' + [string]::new('0', 32))
if ($atomicWriteProbe.Length -gt 240) {
    throw (
        'T24 evidence path exceeds the Mono-safe atomic path limit: ' `
        + $atomicWriteProbe.Length `
        + ' > 240; choose a shorter EvidenceRoot.')
}
$traceDirectory = Join-Path $EvidenceRoot "traces\$Mode"
$firstDifferenceDirectory = Join-Path `
    $EvidenceRoot `
    'first-differences'
$environmentDirectory = Join-Path $EvidenceRoot 'environment'
$cleanupAuditPath = Join-Path $EvidenceRoot 'cleanup-audit.json'
$pauseDwellAuditPath = Join-Path `
    $environmentDirectory `
    'pause-dwell.json'
$recordingArmAuditPath = Join-Path `
    $environmentDirectory `
    'recording-arm-control.json'
$pauseArmDiagnosticPath = Join-Path `
    $environmentDirectory `
    'deferred-pause-arm-state.json'

foreach ($path in @(
        $ManagedDirectory,
        $modsDirectory,
        $tasInstall,
        $PersistentDataDirectory,
        $observerBuild,
        $sessionRoot,
        $ClockBundleRoot
    )) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required directory is missing: $path"
    }
}
foreach ($path in @(
        $GameExecutable,
        $settingsPath,
        $ReferenceTracePath,
        $ReferenceBaselinePath,
        $ReferencePhasePath,
        $NegativeControlEnvelopePath,
        $MoviePath
    )) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $path"
    }
}
if (-not [string]::IsNullOrWhiteSpace(
        $SupplementalReferenceCatalogPath) `
        -and -not (Test-Path `
            -LiteralPath $SupplementalReferenceCatalogPath `
            -PathType Leaf)) {
    throw (
        'Supplemental reference catalog is missing: ' `
        + $SupplementalReferenceCatalogPath)
}
if (-not [string]::IsNullOrWhiteSpace($CompareOnlyCandidateTracePath) `
        -and -not (Test-Path `
            -LiteralPath $CompareOnlyCandidateTracePath `
            -PathType Leaf)) {
    throw "Compare-only candidate trace is missing: $CompareOnlyCandidateTracePath"
}
foreach ($file in $observerFiles) {
    $path = Join-Path $observerBuild $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Reference observer build output is missing: $path"
    }
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "Evidence root already exists: $EvidenceRoot"
}
if (@(
        Get-Process `
            -Name `
                hollow_knight,
                HollowKnightTAS.Companion,
                HollowKnightTAS.AgentBridge,
                HollowKnightTAS.NativeHost `
            -ErrorAction SilentlyContinue
    ).Count -ne 0) {
    throw 'Hollow Knight and TAS helper processes must be stopped.'
}

$managedPrefix = $ManagedDirectory.TrimEnd('\') + '\'
$persistentPrefix = $PersistentDataDirectory.TrimEnd('\') + '\'
$localPrefix = [IO.Path]::GetFullPath($localApplicationData).TrimEnd('\') + '\'
if (-not $modsDirectory.StartsWith(
        $managedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Mods directory is outside the intended Managed directory.'
}
if (-not $automationRoot.StartsWith(
        $localPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Automation root is outside LocalApplicationData.'
}

New-Item -ItemType Directory -Path $traceDirectory -Force | Out-Null
New-Item `
    -ItemType Directory `
    -Path $firstDifferenceDirectory `
    -Force |
    Out-Null
New-Item `
    -ItemType Directory `
    -Path $environmentDirectory `
    -Force |
    Out-Null

$runId = 'candidate-{0}-{1}' -f `
    $Mode, `
    [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$encodedOutput = [Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes($traceDirectory))
$recordingArmRelease = [Threading.EventWaitHandle]::new(
    $false,
    [Threading.EventResetMode]::ManualReset,
    "HollowKnightTAS.T24.RecordingArm.$runId")
$swapId = [Guid]::NewGuid().ToString('N')
$modsBackup = Join-Path `
    $ManagedDirectory `
    "Mods.HKTAS-T24-$swapId.backup"
$modsIsolated = Join-Path `
    $ManagedDirectory `
    "Mods.HKTAS-T24-$swapId.isolated"
$automationBackup = Join-Path `
    $automationParent `
    "automation.HKTAS-T24-$swapId.backup"
$observerInstall = Join-Path `
    $modsDirectory `
    'HollowKnightTAS.ReferenceObserver'
$slotPattern = '^user{0}(?:[._].*)?$' -f $FixtureSlot
$slotFilesBefore = @{}
$slotNamesBefore = @()
$slotCaptured = $false
$settingsOriginal = [IO.File]::ReadAllBytes($settingsPath)
$settingsBackupOriginallyExisted =
    Test-Path -LiteralPath $settingsBackupPath -PathType Leaf
$settingsBackupOriginal = if ($settingsBackupOriginallyExisted) {
    [IO.File]::ReadAllBytes($settingsBackupPath)
}
else {
    $null
}
$automationOriginallyExisted =
    Test-Path -LiteralPath $automationRoot -PathType Container
$script:game = $null
$script:ownedHelperPids = @()
$script:beforeHelperPids = @()
$script:resolvedKeys = [Collections.Generic.HashSet[byte]]::new()
$modsMoved = $false
$isolationMoved = $false
$automationMoved = $false
$automationIsolationEstablished = $false
$client = $null
$leaseId = ''
$leaseReleased = $false
$runFailure = $null
$cleanupFailure = $null
$finalState = $null
$manualUiControl = $null
$manualUiAudit = $null
$pauseDwellEntries = [Collections.Generic.List[object]]::new()
$leaseRenewalEntries = [Collections.Generic.List[object]]::new()
$pauseDwellClean = $Mode -ne 'tas-sequential'
$pauseDwellAuditSha256 = ''
$script:leaseExpiresAtUtc = [DateTimeOffset]::MinValue
$script:initialLeaseExpiresAtUtc = [DateTimeOffset]::MinValue
$matchedReference = $null
$referenceBaselineCatalogMatch = $false
$candidateBaselinePath = ''
$script:rngSynchronizationArmed = $false
$script:recordingArmEvidence = $null
$startupOrderAuditPath = Join-Path `
    $environmentDirectory `
    'candidate-startup-order.json'
$menuReadyAtUtc = $null
$fixtureReadyAtUtc = $null
$bootstrapWaitStartedUtc = $null
$bootstrapObservedAtUtc = $null
$sdkConnectStartedAtUtc = $null
$sdkConnectedAtUtc = $null
$bootstrapPresentAtMenu = $false
$candidateSourcePath = Join-Path `
    $EvidenceRoot `
    'inputs\candidate-applied.hktas'
New-Item `
    -ItemType Directory `
    -Path (Split-Path -Parent $candidateSourcePath) `
    -Force |
    Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class HktasCandidatePhysicalKeyboard
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    public static bool Focus(IntPtr window)
    {
        ShowWindow(window, 9);
        return SetForegroundWindow(window);
    }

    public static void Down(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
    }

    public static void Up(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero);
    }
}
'@

function Invoke-KeyTap {
    param(
        [Parameter(Mandatory)][byte]$VirtualKey,
        [ValidateRange(10, 500)][int]$HoldMilliseconds = 60
    )

    [HktasCandidatePhysicalKeyboard]::Down($VirtualKey)
    Start-Sleep -Milliseconds $HoldMilliseconds
    [HktasCandidatePhysicalKeyboard]::Up($VirtualKey)
}

function Release-GameplayKeys {
    foreach ($key in @(
            [byte[]]@(
                0x25,
                0x26,
                0x27,
                0x28,
                0x58,
                0x5A,
                0x41,
                0x44,
                0x53,
                0x57),
            [byte[]]@($script:resolvedKeys)
        ) | ForEach-Object { $_ }) {
        [HktasCandidatePhysicalKeyboard]::Up($key)
    }
}

function Resolve-VirtualKey {
    param([Parameter(Mandatory)][string]$Bindings)

    $known = @{
        Backspace = [byte]0x08
        Tab = [byte]0x09
        Return = [byte]0x0D
        Enter = [byte]0x0D
        Escape = [byte]0x1B
        Space = [byte]0x20
        LeftArrow = [byte]0x25
        UpArrow = [byte]0x26
        RightArrow = [byte]0x27
        DownArrow = [byte]0x28
    }
    foreach ($name in $Bindings -split '\|') {
        if ($name.Length -eq 1 `
                -and $name[0] -ge 'A' `
                -and $name[0] -le 'Z') {
            $key = [byte][char]$name[0]
            [void]$script:resolvedKeys.Add($key)
            return $key
        }
        if ($known.ContainsKey($name)) {
            $key = [byte]$known[$name]
            [void]$script:resolvedKeys.Add($key)
            return $key
        }
    }
    throw "No supported keyboard binding was found in: $Bindings"
}

function Get-ObserverStatus {
    $path = Join-Path $traceDirectory 'status.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }
    try {
        return Read-T24JsonShared -Path $path
    }
    catch {
        return $null
    }
}

function Wait-ObserverPhase {
    param(
        [Parameter(Mandatory)][string[]]$Allowed,
        [ValidateRange(5, 600)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        $last = Get-ObserverStatus
        if ($null -ne $last -and $Allowed -contains [string]$last.phase) {
            return $last
        }
        if ($null -ne $script:game -and $script:game.HasExited) {
            break
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $lastText = if ($null -eq $last) {
        '<missing>'
    }
    else {
        $last | ConvertTo-Json -Compress
    }
    throw "Observer phase timeout; expected=$($Allowed -join ','); last=$lastText"
}

function Focus-Game {
    $script:game.Refresh()
    if ($script:game.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Hollow Knight main window handle is unavailable.'
    }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ([HktasCandidatePhysicalKeyboard]::Focus(
                $script:game.MainWindowHandle)) {
            Start-Sleep -Milliseconds 250
            return
        }
        Start-Sleep -Milliseconds 100
    }
    try {
        $shell = New-Object -ComObject WScript.Shell
        if ($shell.AppActivate($script:game.Id)) {
            Start-Sleep -Milliseconds 250
            return
        }
    }
    catch {
    }
    throw 'Failed to focus the Hollow Knight window after retries.'
}

function Invoke-ExternalUiSlotLoad {
    Focus-Game
    $initialStatus = Wait-ObserverMenuControlsStatus -TimeoutSeconds 5
    $menuUpKey = Resolve-VirtualKey `
        -Bindings ([string]$initialStatus.menuUpBindings)
    $menuDownKey = Resolve-VirtualKey `
        -Bindings ([string]$initialStatus.menuDownBindings)
    $menuLeftKey = Resolve-VirtualKey `
        -Bindings ([string]$initialStatus.leftBindings)
    $menuSubmitKey = Resolve-VirtualKey `
        -Bindings ([string]$initialStatus.menuSubmitBindings)

    $status = $initialStatus
    $visitedMainMenuObjects = [Collections.Generic.List[string]]::new()
    $mainMenuTargetFound = $false
    for ($navigationAttempt = 0; $navigationAttempt -lt 16; $navigationAttempt++) {
        $selectedObject = [string]$status.selectedObject
        if (-not $visitedMainMenuObjects.Contains($selectedObject)) {
            $visitedMainMenuObjects.Add($selectedObject)
        }
        if ($selectedObject -match `
                '(?i)(continue|startgame|start_game|newgame|playgame|game.*start)') {
            $mainMenuTargetFound = $true
            break
        }

        for ($navigationRetry = 0; $navigationRetry -lt 5; $navigationRetry++) {
            Focus-Game
            Invoke-KeyTap -VirtualKey $menuUpKey -HoldMilliseconds 100
            $navigationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(1)
            do {
                Start-Sleep -Milliseconds 50
                $status = Get-ObserverStatus
            } while ($null -ne $status `
                -and [string]$status.selectedObject -eq $selectedObject `
                -and [DateTimeOffset]::UtcNow -lt $navigationDeadline)
            if ($null -ne $status `
                    -and [string]$status.selectedObject -ne $selectedObject) {
                break
            }
        }
    }
    if (-not $mainMenuTargetFound) {
        throw (
            'Could not identify the Continue/Start Game button; visited=' `
            + ($visitedMainMenuObjects -join ','))
    }

    for ($submitAttempt = 0; $submitAttempt -lt 10; $submitAttempt++) {
        Focus-Game
        Invoke-KeyTap -VirtualKey $menuSubmitKey -HoldMilliseconds 200
        $slotDeadline = [DateTimeOffset]::UtcNow.AddSeconds(3)
        do {
            $status = Get-ObserverStatus
            if ($null -ne $status `
                    -and [int]$status.selectedSaveSlot -gt 0) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $slotDeadline)
        if ($null -ne $status `
                -and [int]$status.selectedSaveSlot -gt 0) {
            break
        }
    }
    if ($null -eq $status -or [int]$status.selectedSaveSlot -le 0) {
        throw 'Save-slot menu did not expose a selected SaveSlotButton.'
    }

    while ([int]$status.selectedSaveSlot -ne $FixtureSlot) {
        if ([string]$status.selectedObject -match `
                '(?i)^ClearSaveButton(?:One|Two|Three|Four)$') {
            for ($columnRecoveryAttempt = 0; `
                    $columnRecoveryAttempt -lt 5; `
                    $columnRecoveryAttempt++) {
                Focus-Game
                Invoke-KeyTap `
                    -VirtualKey $menuLeftKey `
                    -HoldMilliseconds 60
                $columnRecoveryDeadline = `
                    [DateTimeOffset]::UtcNow.AddSeconds(1)
                do {
                    Start-Sleep -Milliseconds 50
                    $status = Get-ObserverStatus
                } while ($null -ne $status `
                    -and [int]$status.selectedSaveSlot -le 0 `
                    -and [DateTimeOffset]::UtcNow `
                        -lt $columnRecoveryDeadline)
                if ($null -ne $status `
                        -and [int]$status.selectedSaveSlot -gt 0) {
                    break
                }
            }
            if ($null -eq $status `
                    -or [int]$status.selectedSaveSlot -le 0) {
                throw (
                    'Save-slot selection entered the clear-button column ' `
                    + 'and could not return to a SaveSlotButton.')
            }
            continue
        }

        $key = if ([int]$status.selectedSaveSlot -lt $FixtureSlot) {
            $menuDownKey
        }
        else {
            $menuUpKey
        }
        $before = [int]$status.selectedSaveSlot
        for ($moveAttempt = 0; $moveAttempt -lt 5; $moveAttempt++) {
            Focus-Game
            Invoke-KeyTap -VirtualKey $key -HoldMilliseconds 100
            $moveDeadline = [DateTimeOffset]::UtcNow.AddSeconds(1)
            do {
                Start-Sleep -Milliseconds 50
                $status = Get-ObserverStatus
            } while ($null -ne $status `
                -and [int]$status.selectedSaveSlot -eq $before `
                -and [DateTimeOffset]::UtcNow -lt $moveDeadline)
            if ($null -ne $status `
                    -and [int]$status.selectedSaveSlot -ne $before) {
                break
            }
        }
        if ($null -eq $status `
                -or [int]$status.selectedSaveSlot -eq $before) {
            throw 'Save-slot selection did not move.'
        }
    }
    Focus-Game
    Invoke-KeyTap -VirtualKey $menuSubmitKey -HoldMilliseconds 200
}

function Test-ObserverMenuControlsReady {
    param($Status)

    if ($null -eq $Status) {
        return $false
    }
    foreach ($name in @(
            'menuUpBindings',
            'menuDownBindings',
            'menuSubmitBindings')) {
        $property = $Status.PSObject.Properties[$name]
        if ($null -eq $property `
                -or [string]::IsNullOrWhiteSpace(
                    [string]$property.Value)) {
            return $false
        }
    }
    return $true
}

function Wait-ObserverMenuControlsStatus {
    param(
        [ValidateRange(1, 240)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        $last = Get-ObserverStatus
        if ($null -ne $last `
                -and [string]$last.scene -eq 'Menu_Title' `
                -and (Test-ObserverMenuControlsReady -Status $last)) {
            return $last
        }
        if ($null -ne $script:game -and $script:game.HasExited) {
            break
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $lastText = if ($null -eq $last) {
        '<missing>'
    }
    else {
        $last | ConvertTo-Json -Compress
    }
    throw "Candidate observer did not reach Menu_Title; last=$lastText"
}

function Write-TestSettings {
    $settings = Get-Content -LiteralPath $settingsPath -Raw |
        ConvertFrom-Json -AsHashtable -Depth 50
    $settings['VerificationModeRequested'] = $false
    $settings['CompanionEnabled'] = $true
    $settings['AutoStartCompanion'] = $true
    $settings['ExitCompanionWithGame'] = $true
    $settings['CompanionOverlayEnabled'] = $false
    $settings['EnableNativeCapabilities'] = $false
    $settings['EnableSemanticKeyframes'] = $false
    $settings['InspectorEnabled'] = $false
    $settings['InspectorOverlayEnabled'] = $false
    $settings['InspectorExportEnabled'] = $false
    $settings['ReplaySaveEnabled'] = $true
    $settings['ReplaySaveAutoEnabled'] = $false
    $settings['ReplaySaveDeterministicTimingEnabled'] = $false
    $settings['ReplayDeterministicRngEnabled'] = $false
    $settings['DedicatedTasSaveSlot'] = $FixtureSlot
    $settings['ExternalAutomationMode'] = 'ApprovedControl'
    $settings['DebugMutationEnabled'] = $false
    $settings |
        ConvertTo-Json -Depth 50 |
        Set-Content `
            -LiteralPath $settingsPath `
            -Encoding utf8NoBOM
}

function New-StringMap {
    param([hashtable]$Values = @{})

    $result =
        [Collections.Generic.Dictionary[string, string]]::new(
            [StringComparer]::Ordinal)
    foreach ($pair in $Values.GetEnumerator()) {
        $result.Add([string]$pair.Key, [string]$pair.Value)
    }
    return $result
}

function Invoke-SdkCommand {
    param(
        [Parameter(Mandatory)]$Client,
        [Parameter(Mandatory)][string]$CommandId,
        [Parameter(Mandatory)][string]$Scope,
        [hashtable]$Arguments = @{},
        [string]$LeaseId = '',
        [string]$ExpectedRuntimeMode = '',
        [Nullable[long]]$ExpectedMovieTick = $null,
        [switch]$AllowFailure
    )

    $command = $Client.CreateCommand(
        $CommandId,
        $Scope,
        (New-StringMap -Values $Arguments),
        $LeaseId,
        $ExpectedRuntimeMode,
        $ExpectedMovieTick,
        ('request-' + [Guid]::NewGuid().ToString('N')),
        ('idempotency-' + [Guid]::NewGuid().ToString('N')))
    $result = $Client.ExecuteAsync(
            $command,
            [Threading.CancellationToken]::None
        ).GetAwaiter().GetResult()
    if (-not $AllowFailure -and -not $result.Success) {
        throw "SDK $CommandId failed: $($result.ResultCode): $($result.Detail)"
    }
    return $result
}

function Convert-AutomationResult {
    param([Parameter(Mandatory)]$Value)

    $data = [ordered]@{}
    foreach ($pair in $Value.Data.GetEnumerator() | Sort-Object Key) {
        $data[$pair.Key] = $pair.Value
    }
    return [ordered]@{
        requestId = $Value.RequestId
        success = $Value.Success
        resultCode = $Value.ResultCode
        detail = $Value.Detail
        acceptedAtMovieTick = $Value.AcceptedAtMovieTick
        data = $data
    }
}

function Acquire-SdkLease {
    param(
        [Parameter(Mandatory)]$Client,
        [Parameter(Mandatory)][string[]]$Scopes
    )

    $result = Invoke-SdkCommand `
        -Client $Client `
        -CommandId 'acquireControl' `
        -Scope $Scopes[0] `
        -Arguments @{
            scopes = $Scopes -join ','
            ttlSeconds = '300'
        }
    if (-not $result.Data.ContainsKey('leaseId')) {
        throw 'SDK acquireControl omitted leaseId.'
    }
    if (-not $result.Data.ContainsKey('expiresAtUtc')) {
        throw 'SDK acquireControl omitted expiresAtUtc.'
    }
    $script:leaseExpiresAtUtc = [DateTimeOffset]::Parse(
        [string]$result.Data['expiresAtUtc'],
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    if ($script:leaseExpiresAtUtc -le [DateTimeOffset]::UtcNow) {
        throw 'SDK acquireControl returned an expired lease.'
    }
    $script:initialLeaseExpiresAtUtc = $script:leaseExpiresAtUtc
    return [string]$result.Data['leaseId']
}

function Renew-SdkLease {
    param(
        [Parameter(Mandatory)]$Client,
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$LeaseId,
        [ValidateRange(1, 300)][int]$TtlSeconds = 300
    )

    $result = Invoke-SdkCommand `
        -Client $Client `
        -CommandId 'renewControl' `
        -Scope $Scope `
        -Arguments @{ ttlSeconds = [string]$TtlSeconds } `
        -LeaseId $LeaseId
    if (-not $result.Data.ContainsKey('leaseId') `
            -or [string]$result.Data['leaseId'] -ne $LeaseId `
            -or -not $result.Data.ContainsKey('expiresAtUtc')) {
        throw 'SDK renewControl returned an invalid lease contract.'
    }
    $renewedExpiry = [DateTimeOffset]::Parse(
        [string]$result.Data['expiresAtUtc'],
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    if ($renewedExpiry -le [DateTimeOffset]::UtcNow) {
        throw 'SDK renewControl returned an expired lease.'
    }
    $script:leaseExpiresAtUtc = $renewedExpiry
    return $result
}

function Get-SdkState {
    return $client.GetSemanticStateAsync(
            [Threading.CancellationToken]::None
        ).GetAwaiter().GetResult()
}

function Wait-SdkState {
    param(
        [string]$RuntimeMode = '',
        [Nullable[long]]$MinimumMovieTick = $null,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        $last = Get-SdkState
        $modeMatches = [string]::IsNullOrEmpty($RuntimeMode) `
            -or [string]$last.State.RuntimeMode -eq $RuntimeMode
        $tickMatches = $null -eq $MinimumMovieTick `
            -or $last.State.MovieTick -ge [long]$MinimumMovieTick
        if ($modeMatches -and $tickMatches) {
            return $last
        }
        Start-Sleep -Milliseconds 10
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw (
        'SDK state timeout: expectedMode=' `
        + $RuntimeMode `
        + '; minimumMovieTick=' `
        + $MinimumMovieTick `
        + '; actual=' `
        + $last.ToFriendlyJson())
}

function Complete-T24DeferredRecordingArm {
    $armedState = Get-SdkState
    $armedFields = $armedState.State.Fields
    if ([string]$armedState.State.RuntimeMode -ne 'Running' `
            -or [string]$armedFields['playbackMode'] -ne 'Idle' `
            -or [string]$armedFields[
                'deferredRecordingArmEnabled'] -ne 'true' `
            -or [string]$armedFields[
                'deferredRecordingArmRunId'] -ne $runId `
            -or [string]$armedFields[
                'deferredRecordingArmPauseArmed'] -ne 'true' `
            -or [string]$armedFields[
                'deferredRecordingArmReplayArmed'] -ne 'true' `
            -or [string]$armedFields[
                'deferredRecordingArmReleaseConsumed'] -ne 'false' `
            -or [string]$armedFields[
                'deferredRecordingArmActivationAttempted'] -ne 'false' `
            -or [int]$armedFields[
                'deferredRecordingArmNeutralPreRollCompletedFrameCount'] -ne 0 `
            -or [long]$armedFields['replayObservationCount'] -ne 0) {
        throw 'Candidate replay was not neutrally armed before recording release.'
    }

    $armStatus = Wait-ObserverPhase `
        -Allowed @('waiting-for-recording-arm-release') `
        -TimeoutSeconds $FixtureReadyTimeoutSeconds
    $profile = Wait-T24ClockProfile `
        -GetStatus { Get-ObserverStatus } `
        -TimeoutSeconds 15
    $profile |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (
                Join-Path $environmentDirectory 'clock-profile.json') `
            -Encoding utf8NoBOM
    if (-not [bool]$profile.recordingArmBoundaryReached `
            -or [bool]$profile.recordingArmReleased `
            -or [string]$profile.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or [string]$profile.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne '44400000' `
            -or [string]$profile.recordingArmBoundaryFixedTimeRaw.canonicalHex `
                -ne '44400000' `
            -or [string]$profile.externalDoublePhaseFinalResidualBits `
                -ne '0000000000000000' `
            -or [string]$profile.externalDoublePhaseFinalResidual.canonicalHex `
                -ne '0000000000000000' `
            -or [string]$profile.timeRawDouble.canonicalHex `
                -ne [string]$profile.fixedTimeRawDouble.canonicalHex) {
        throw 'Candidate recording arm did not reach the exact v11 boundary.'
    }

    $hostReleaseSetAtUtc = [DateTimeOffset]::UtcNow
    [void]$recordingArmRelease.Set()
    $pausedState = Wait-SdkState `
        -RuntimeMode 'Paused' `
        -TimeoutSeconds 30
    $pausedFields = $pausedState.State.Fields
    if ([string]$pausedFields['playbackMode'] -ne 'Replaying' `
            -or [string]$pausedFields[
                'deferredRecordingArmReleaseConsumed'] -ne 'true' `
            -or [string]$pausedFields[
                'deferredRecordingArmActivationAttempted'] -ne 'true' `
            -or [string]$pausedFields[
                'deferredRecordingArmActivationSucceeded'] -ne 'true' `
            -or [int]$pausedFields[
                'deferredRecordingArmActivationCount'] -ne 1 `
            -or [int]$pausedFields[
                'deferredRecordingArmNeutralPreRollCompletedFrameCount'] -ne 1 `
            -or -not [string]::IsNullOrEmpty(
                [string]$pausedFields[
                    'deferredRecordingArmActivationError']) `
            -or [long]$pausedFields['replayObservationCount'] -ne 0 `
            -or [long]$pausedFields['lastReplayMovieTick'] -ne -1) {
        throw 'Candidate recording-arm activation was not atomic and neutral.'
    }

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        policyId =
            'existing-manual-reset-one-neutral-completed-frame-preroll-v2'
        runId = $runId
        armedRuntimeMode = [string]$armedState.State.RuntimeMode
        armedPlaybackMode = [string]$armedFields['playbackMode']
        armedPause = [string]$armedFields[
            'deferredRecordingArmPauseArmed'] -eq 'true'
        armedReplay = [string]$armedFields[
            'deferredRecordingArmReplayArmed'] -eq 'true'
        releaseConsumedBeforeHost = [string]$armedFields[
            'deferredRecordingArmReleaseConsumed'] -eq 'true'
        activationAttemptedBeforeHost = [string]$armedFields[
            'deferredRecordingArmActivationAttempted'] -eq 'true'
        neutralPreRollCompletedFrameCountBeforeHost = [int]$armedFields[
            'deferredRecordingArmNeutralPreRollCompletedFrameCount']
        observerPhaseBeforeHost = [string]$armStatus.phase
        observerBoundaryReachedBeforeHost =
            [bool]$profile.recordingArmBoundaryReached
        observerReleasedBeforeHost = [bool]$profile.recordingArmReleased
        observerBoundaryTimeRawHex =
            [string]$profile.recordingArmBoundaryTimeRaw.canonicalHex
        observerBoundaryFixedTimeRawHex =
            [string]$profile.recordingArmBoundaryFixedTimeRaw.canonicalHex
        observerDoublePhaseFinalResidualBits =
            [string]$profile.externalDoublePhaseFinalResidualBits
        hostReleaseSet = $true
        hostReleaseSetAtUtc = $hostReleaseSetAtUtc.ToString('O')
        pausedRuntimeMode = [string]$pausedState.State.RuntimeMode
        pausedPlaybackMode = [string]$pausedFields['playbackMode']
        releaseConsumedAfterHost = [string]$pausedFields[
            'deferredRecordingArmReleaseConsumed'] -eq 'true'
        activationAttemptedAfterHost = [string]$pausedFields[
            'deferredRecordingArmActivationAttempted'] -eq 'true'
        activationSucceededAfterHost = [string]$pausedFields[
            'deferredRecordingArmActivationSucceeded'] -eq 'true'
        activationCountAfterHost = [int]$pausedFields[
            'deferredRecordingArmActivationCount']
        neutralPreRollCompletedFrameCountAfterHost = [int]$pausedFields[
            'deferredRecordingArmNeutralPreRollCompletedFrameCount']
        activationErrorAfterHost = [string]$pausedFields[
            'deferredRecordingArmActivationError']
        replayObservationCountAfterHost = [long]$pausedFields[
            'replayObservationCount']
        lastReplayMovieTickAfterHost = [long]$pausedFields[
            'lastReplayMovieTick']
    }
}

function Get-FieldMap {
    param([Parameter(Mandatory)]$Frame)

    $map = [ordered]@{}
    foreach ($field in $Frame.fields) {
        $map[[string]$field.key] = $field
    }
    return $map
}

function Get-T24Sha256Text {
    param([Parameter(Mandatory)][string]$Text)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Text))
    ).ToLowerInvariant()
}

function Get-T24TopLevelFingerprint {
    param([Parameter(Mandatory)][string]$Root)

    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Fingerprint root is missing: $rootFull"
    }

    $normalized = @(
        Get-ChildItem -LiteralPath $rootFull -Force |
            Sort-Object Name |
            ForEach-Object {
                if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) `
                        -ne 0) {
                    throw "Fingerprint does not follow reparse points: $($_.FullName)"
                }
                if ($_.PSIsContainer) {
                    [ordered]@{
                        name = $_.Name
                        kind = 'directory'
                    }
                }
                else {
                    [ordered]@{
                        name = $_.Name
                        kind = 'file'
                        length = $_.Length
                        sha256 = (Get-FileHash `
                            -LiteralPath $_.FullName `
                            -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                }
            })
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 10)
}

function Get-T24DirectoryFingerprint {
    param([Parameter(Mandatory)][string]$Root)

    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Fingerprint root is missing: $rootFull"
    }

    $normalized = @(
        Get-ChildItem -LiteralPath $rootFull -Recurse -Force |
            ForEach-Object {
                if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) `
                        -ne 0) {
                    throw "Fingerprint does not follow reparse points: $($_.FullName)"
                }
                $relativePath = [IO.Path]::GetRelativePath(
                    $rootFull,
                    $_.FullName).Replace('\', '/')
                if ($_.PSIsContainer) {
                    [ordered]@{
                        path = $relativePath
                        kind = 'directory'
                    }
                }
                else {
                    [ordered]@{
                        path = $relativePath
                        kind = 'file'
                        length = $_.Length
                        sha256 = (Get-FileHash `
                            -LiteralPath $_.FullName `
                            -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                }
            } |
            Sort-Object { $_.path })
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 10)
}

function Assert-T24TraceContract {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath ($Path + '.incomplete')) { throw "Incomplete trace cannot be verified: $Path" }
    $lines = @([IO.File]::ReadAllLines($Path))
    for ($index = 0; $index -lt $lines.Count; $index++) {
        try {
            $frame = $lines[$index] | ConvertFrom-Json
        }
        catch {
            throw "Trace line $($index + 1) is not valid JSON: $($_.Exception.Message)"
        }

        $frameProperties = @($frame.PSObject.Properties.Name)
        foreach ($requiredProperty in @(
                'schemaVersion',
                'sequence',
                'logicalTick',
                'comparisonSha256',
                'fields')) {
            if ($frameProperties -notcontains $requiredProperty) {
                throw "Trace line $($index + 1) lacks '$requiredProperty'."
            }
        }
        if ([int]$frame.schemaVersion -ne 1) {
            throw "Trace line $($index + 1) has an unsupported schema version."
        }
        if ([long]$frame.sequence -ne [long]($index + 1)) {
            throw "Trace line $($index + 1) has a non-contiguous sequence."
        }
        if ([long]$frame.logicalTick -ne [long]$index) {
            throw "Trace line $($index + 1) has a non-contiguous logical tick."
        }
        if ([string]$frame.comparisonSha256 `
                -cnotmatch '\A[0-9a-f]{64}\z') {
            throw "Trace line $($index + 1) has an invalid comparison SHA-256."
        }

        $fieldMap = [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::Ordinal)
        $sourceKeys = [Collections.Generic.List[string]]::new()
        foreach ($field in @($frame.fields)) {
            $fieldProperties = @($field.PSObject.Properties.Name)
            foreach ($requiredProperty in @(
                    'key', 'kind', 'canonicalHex', 'comparable')) {
                if ($fieldProperties -notcontains $requiredProperty) {
                    throw "Trace line $($index + 1) contains an incomplete field."
                }
            }
            $key = [string]$field.key
            if ([string]::IsNullOrEmpty($key) `
                    -or [string]::IsNullOrEmpty([string]$field.kind) `
                    -or [string]$field.canonicalHex `
                        -cnotmatch '\A(?:[0-9a-f]{2})*\z' `
                    -or $field.comparable -isnot [bool]) {
                throw "Trace line $($index + 1) contains an invalid field encoding."
            }
            if (-not $fieldMap.TryAdd($key, $field)) {
                throw "Trace line $($index + 1) contains duplicate field '$key'."
            }
            $sourceKeys.Add($key)
        }
        foreach ($absoluteTimeKey in @('time.raw', 'time.fixedRaw')) {
            if (-not $fieldMap.ContainsKey($absoluteTimeKey) `
                    -or [string]$fieldMap[$absoluteTimeKey].kind `
                        -ne 'Float32Bits' `
                    -or -not [bool]$fieldMap[$absoluteTimeKey].comparable `
                    -or [string]$fieldMap[$absoluteTimeKey].canonicalHex `
                        -cnotmatch '\A[0-9a-f]{8}\z') {
                throw "Trace line $($index + 1) lacks comparable absolute-time field '$absoluteTimeKey'."
            }
        }

        $sortedKeys = [string[]]@($fieldMap.Keys)
        [Array]::Sort($sortedKeys, [StringComparer]::Ordinal)
        for ($fieldIndex = 0; `
                $fieldIndex -lt $sourceKeys.Count; `
                $fieldIndex++) {
            if ($sourceKeys[$fieldIndex] -cne $sortedKeys[$fieldIndex]) {
                throw "Trace line $($index + 1) fields are not in ordinal key order."
            }
        }
        $canonicalFields = @(
            foreach ($key in $sortedKeys) {
                $field = $fieldMap[$key]
                if (-not [bool]$field.comparable) {
                    continue
                }
                [ordered]@{
                    key = $key
                    kind = [string]$field.kind
                    canonicalHex = [string]$field.canonicalHex
                }
            })
        $canonical = [ordered]@{
            logicalTick = [long]$frame.logicalTick
            fields = $canonicalFields
        } | ConvertTo-Json -Compress -Depth 10
        if ((Get-T24Sha256Text -Text $canonical) `
                -cne [string]$frame.comparisonSha256) {
            throw "Trace line $($index + 1) comparison SHA-256 does not match its fields."
        }
    }
}

function Assert-T24ExternalRngBoundaryTelemetry {
    param(
        [Parameter(Mandatory)][object]$Telemetry,
        [Parameter(Mandatory)][string]$TracePath
    )

    $scenePattern = [regex]::new(
        '"key":"scene\.name","kind":"Utf8String",' `
        + '"canonicalHex":"[0-9a-f]*",' `
        + '"displayValue":"(?<scene>[^"]*)","comparable":true')
    $frameCount = 0
    $traceTransitionCount = 0
    $firstScene = ''
    $lastScene = ''
    foreach ($line in [IO.File]::ReadLines($TracePath)) {
        $match = $scenePattern.Match($line)
        if (-not $match.Success) {
            throw "Trace frame $frameCount has no canonical scene.name field."
        }
        $scene = [string]$match.Groups['scene'].Value
        if ($frameCount -eq 0) {
            $firstScene = $scene
        }
        elseif ($scene -cne $lastScene) {
            $traceTransitionCount++
        }
        $lastScene = $scene
        $frameCount++
    }

    $clean =
        [string]$Telemetry.externalClockProfileId `
            -ceq 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
        -and [string]$Telemetry.externalRngPayloadPolicyId `
            -ceq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [int]$Telemetry.externalRngResetCount -eq 2 `
        -and [int]$Telemetry.externalRngFaultCode -eq 0 `
        -and [string]$Telemetry.externalRngLastBoundary -ceq 'recording-root' `
        -and [string]$Telemetry.externalRngLastScene -ceq $firstScene `
        -and (Test-T24NativeSceneLifecycleContract -Telemetry $Telemetry) `
        -and (Test-T24RecordingPhaseContract -Telemetry $Telemetry -RootRequestApplied $true)
    if (-not $clean) {
        throw 'External clock violated the root-only RNG/native scene lifecycle contract.'
    }
    if ($Telemetry.PSObject.Properties.Name `
            -contains 'traceSceneTransitionCount' `
            -and [int]$Telemetry.traceSceneTransitionCount `
                -ne $traceTransitionCount) {
        throw 'External scene RNG summary transition count is invalid.'
    }

    return [pscustomobject]@{
        frameCount = $frameCount
        transitionCount = $traceTransitionCount
        firstScene = $firstScene
        lastScene = $lastScene
    }
}

function Assert-T24BaselineAbsoluteTimeContract {
    param([Parameter(Mandatory)][string]$Path)

    $baseline = Get-Content -LiteralPath $Path -Raw |
        ConvertFrom-Json -AsHashtable
    if ([int]$baseline['schemaVersion'] -ne 2) {
        throw 'T24 baseline must use absolute-time schema version 2.'
    }
    foreach ($key in @('timeRaw', 'fixedTimeRaw')) {
        if (-not $baseline.Contains($key) `
                -or $baseline[$key] -isnot [Collections.IDictionary] `
                -or -not $baseline[$key].Contains('value') `
                -or -not $baseline[$key].Contains('canonicalHex') `
                -or [string]$baseline[$key]['canonicalHex'] `
                    -cnotmatch '\A[0-9a-f]{8}\z') {
            throw "T24 baseline absolute-time field is malformed: $key"
        }
    }
    return $baseline
}

function Get-T24BaselineSignature {
    param([Parameter(Mandatory)][string]$Path)

    $excluded = @('runId', 'mode', 'visualTick', 'fixedTick')
    $baseline = Assert-T24BaselineAbsoluteTimeContract -Path $Path
    $normalized = [ordered]@{}
    foreach ($key in @($baseline.Keys | Sort-Object)) {
        if ($excluded -contains $key) {
            continue
        }
        $value = $baseline[$key]
        if ($value -is [Collections.IDictionary] `
                -and $value.Contains('canonicalHex')) {
            $normalized[[string]$key] = [ordered]@{
                canonicalHex = [string]$value['canonicalHex']
            }
        }
        else {
            $normalized[[string]$key] = $value
        }
    }
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 20)
}

function Get-T24AuthoritativeBaselineSignature {
    param([Parameter(Mandatory)][string]$Path)

    $excluded = @(
        'runId',
        'mode',
        'visualTick',
        'fixedTick',
        'heroPositionX',
        'heroPositionY',
        'heroPositionZ'
    )
    $baseline = Assert-T24BaselineAbsoluteTimeContract -Path $Path
    $normalized = [ordered]@{}
    foreach ($key in @($baseline.Keys | Sort-Object)) {
        if ($excluded -contains $key) {
            continue
        }
        $value = $baseline[$key]
        if ($value -is [Collections.IDictionary] `
                -and $value.Contains('canonicalHex')) {
            $normalized[[string]$key] = [ordered]@{
                canonicalHex = [string]$value['canonicalHex']
            }
        }
        else {
            $normalized[[string]$key] = $value
        }
    }
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 20)
}

function Get-T24SemanticBaselineSignature {
    param([Parameter(Mandatory)][string]$Path)

    $excluded = @(
        'runId',
        'mode',
        'visualTick',
        'fixedTick',
        'heroPositionX',
        'heroPositionY',
        'heroPositionZ',
        'rigidbodyPositionX',
        'rigidbodyPositionY'
    )
    $baseline = Assert-T24BaselineAbsoluteTimeContract -Path $Path
    $normalized = [ordered]@{}
    foreach ($key in @($baseline.Keys | Sort-Object)) {
        if ($excluded -contains $key) {
            continue
        }
        $value = $baseline[$key]
        if ($value -is [Collections.IDictionary] `
                -and $value.Contains('canonicalHex')) {
            $normalized[[string]$key] = [ordered]@{
                canonicalHex = [string]$value['canonicalHex']
            }
        }
        else {
            $normalized[[string]$key] = $value
        }
    }
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 20)
}

function Get-T24BaselineFloatHex {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Key
    )

    $baseline = Assert-T24BaselineAbsoluteTimeContract -Path $Path
    if (-not $baseline.ContainsKey($Key) `
            -or $baseline[$Key] -isnot [Collections.IDictionary] `
            -or -not $baseline[$Key].Contains('canonicalHex')) {
        throw "Baseline float field is missing or malformed: $Key"
    }
    $hex = [string]$baseline[$Key]['canonicalHex']
    if ($hex -cnotmatch '\A[0-9a-f]{8}\z') {
        throw "Baseline float field has invalid canonical hex: $Key"
    }
    return $hex
}

function Get-T24ObserverAssemblySetSha256 {
    param(
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][string[]]$Files,
        [switch]$SamplingOnly
    )

    if ($SamplingOnly) {
        $Files = @(
            $Files |
                Where-Object {
                    $_ -in @(
                        'HollowKnightTAS.Core.dll',
                        'HollowKnightTAS.GameObservation.dll')
                })
        if ($Files.Count -ne 2) {
            throw 'Current observer build lacks the two sampling assemblies.'
        }
    }
    $normalized = @(
        $Files |
            Sort-Object |
            ForEach-Object {
                $path = Join-Path $BuildRoot $_
                [ordered]@{
                    file = "HollowKnightTAS.ReferenceObserver\$_"
                    length = (Get-Item -LiteralPath $path).Length
                    sha256 = (Get-FileHash `
                        -LiteralPath $path `
                        -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            })
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress -Depth 10)
}

function Get-T24ClockContractSha256 {
    param([Parameter(Mandatory)]$ClockAudit)

    $normalized = [ordered]@{
        capabilityId = [string]$ClockAudit.capabilityId
        profile = [string]$ClockAudit.profile
        bridgeAbi = [int]$ClockAudit.bridgeAbi
        startupPolicy = [string]$ClockAudit.startupPolicy
        bundleManifestSha256 = [string]$ClockAudit.bundleManifestSha256
        injectorSha256 = [string]$ClockAudit.injectorSha256
        injectorManagedSha256 = [string]$ClockAudit.injectorManagedSha256
        injectorDepsSha256 = [string]$ClockAudit.injectorDepsSha256
        injectorRuntimeConfigSha256 =
            [string]$ClockAudit.injectorRuntimeConfigSha256
        runtimeFileSetSha256 = [string]$ClockAudit.runtimeFileSetSha256
        bridgeSha256 = [string]$ClockAudit.bridgeSha256
        payloadSha256 = [string]$ClockAudit.payloadSha256
        processImageSha256 = [string]$ClockAudit.processImageSha256
        randomSynchronizationPolicy =
            [string]$ClockAudit.randomSynchronizationPolicy
        randomSynchronizationSeed =
            [int]$ClockAudit.randomSynchronizationSeed
        stderrEmpty = [bool]$ClockAudit.stderrEmpty
    }
    return Get-T24Sha256Text -Text (
        $normalized | ConvertTo-Json -Compress)
}

function Convert-T24Float32Hex {
    param([Parameter(Mandatory)][string]$Hex)

    if ($Hex -notmatch '^[0-9a-fA-F]{8}$') {
        throw "Invalid Float32 canonical hex: $Hex"
    }
    $bits = [Convert]::ToUInt32($Hex, 16)
    return [ordered]@{
        bits = [uint64]$bits
        value = [BitConverter]::UInt32BitsToSingle($bits)
    }
}

function Get-T24Float32UlpDistance {
    param(
        [Parameter(Mandatory)][uint64]$LeftBits,
        [Parameter(Mandatory)][uint64]$RightBits
    )

    function Get-OrderedKey([uint64]$Bits) {
        if (($Bits -band 0x80000000L) -ne 0) {
            return [uint64](0xffffffffL - $Bits)
        }
        return [uint64](0x80000000L + $Bits)
    }
    $leftKey = Get-OrderedKey -Bits $LeftBits
    $rightKey = Get-OrderedKey -Bits $RightBits
    if ($leftKey -ge $rightKey) {
        return [uint64]($leftKey - $rightKey)
    }
    return [uint64]($rightKey - $leftKey)
}

function Get-T24Float32FieldValue {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Key,
        [switch]$RequireComparable,
        [switch]$RequireDiagnostic
    )

    if (-not $Map.Contains($Key)) {
        return $null
    }
    $field = $Map[$Key]
    if ($null -eq $field `
            -or [string]$field.kind -ne 'Float32Bits' `
            -or [string]$field.canonicalHex `
                -cnotmatch '\A[0-9a-f]{8}\z' `
            -or $field.comparable -isnot [bool] `
            -or ($RequireComparable -and -not [bool]$field.comparable) `
            -or ($RequireDiagnostic -and [bool]$field.comparable)) {
        return $null
    }
    $decoded = Convert-T24Float32Hex `
        -Hex ([string]$field.canonicalHex)
    if ([single]::IsNaN($decoded.value) `
            -or [single]::IsInfinity($decoded.value)) {
        return $null
    }
    return [pscustomobject][ordered]@{
        field = $field
        bits = [uint64]$decoded.bits
        value = [single]$decoded.value
    }
}

function Get-T24Float32SubtractionHex {
    param(
        [Parameter(Mandatory)][single]$Left,
        [Parameter(Mandatory)][single]$Right
    )

    $difference = [single]($Left - $Right)
    return [BitConverter]::SingleToUInt32Bits($difference).ToString(
        'x8',
        [Globalization.CultureInfo]::InvariantCulture)
}

function Test-T24RigidbodyAxisWitnessTrace {
    param(
        [Parameter(Mandatory)][string[]]$CandidateLines,
        [Parameter(Mandatory)][string[]]$WitnessLines,
        [Parameter(Mandatory)]
        [ValidateSet('x', 'y')]
        [string]$Axis
    )

    $absoluteKey = "hero.rigidbody.position.$Axis"
    $deltaKey = "hero.rigidbody.positionDelta.$Axis"
    if ($CandidateLines.Count -le 0 `
            -or $CandidateLines.Count -ne $WitnessLines.Count) {
        return [pscustomobject][ordered]@{
            axis = $Axis
            equivalent = $false
            comparedFrames = 0
            firstMismatch = [ordered]@{
                logicalTick = 0
                key = '$frameCount'
                reason = 'rigidbody-axis-witness-frame-count-mismatch'
                candidate = $CandidateLines.Count
                witness = $WitnessLines.Count
            }
        }
    }

    for ($index = 0; $index -lt $CandidateLines.Count; $index++) {
        $candidateFrame = $CandidateLines[$index] | ConvertFrom-Json
        $witnessFrame = $WitnessLines[$index] | ConvertFrom-Json
        if ([long]$candidateFrame.logicalTick `
                -ne [long]$witnessFrame.logicalTick) {
            return [pscustomobject][ordered]@{
                axis = $Axis
                equivalent = $false
                comparedFrames = $index
                firstMismatch = [ordered]@{
                    logicalTick = $index
                    key = '$logicalTick'
                    reason = 'rigidbody-axis-witness-logical-tick-mismatch'
                    candidate = [long]$candidateFrame.logicalTick
                    witness = [long]$witnessFrame.logicalTick
                }
            }
        }
        $candidateMap = Get-FieldMap -Frame $candidateFrame
        $witnessMap = Get-FieldMap -Frame $witnessFrame
        foreach ($key in @($absoluteKey, $deltaKey)) {
            $candidateField = if ($candidateMap.Contains($key)) {
                $candidateMap[$key]
            }
            else { $null }
            $witnessField = if ($witnessMap.Contains($key)) {
                $witnessMap[$key]
            }
            else { $null }
            $valid = $null -ne $candidateField `
                -and $null -ne $witnessField `
                -and [bool]$candidateField.comparable `
                -and [bool]$witnessField.comparable `
                -and [string]$candidateField.kind -eq 'Float32Bits' `
                -and [string]$witnessField.kind -eq 'Float32Bits' `
                -and [string]$candidateField.canonicalHex `
                    -cmatch '\A[0-9a-f]{8}\z' `
                -and [string]$witnessField.canonicalHex `
                    -cmatch '\A[0-9a-f]{8}\z'
            if (-not $valid `
                    -or [string]$candidateField.canonicalHex `
                        -cne [string]$witnessField.canonicalHex) {
                return [pscustomobject][ordered]@{
                    axis = $Axis
                    equivalent = $false
                    comparedFrames = $index
                    firstMismatch = [ordered]@{
                        logicalTick = $index
                        key = $key
                        reason = if ($valid) {
                            'rigidbody-axis-witness-bitwise-mismatch'
                        }
                        else {
                            'rigidbody-axis-witness-field-invalid'
                        }
                        candidate = if ($null -ne $candidateField) {
                            [string]$candidateField.canonicalHex
                        }
                        else { $null }
                        witness = if ($null -ne $witnessField) {
                            [string]$witnessField.canonicalHex
                        }
                        else { $null }
                    }
                }
            }
        }
    }

    return [pscustomobject][ordered]@{
        axis = $Axis
        equivalent = $true
        comparedFrames = $CandidateLines.Count
        firstMismatch = $null
    }
}

function Test-T24DerivedRenderDeltaNormalization {
    param(
        [Parameter(Mandatory)]$ReferenceMap,
        [Parameter(Mandatory)]$CandidateMap,
        [Parameter(Mandatory)]$ReferenceOriginMap,
        [Parameter(Mandatory)]$CandidateOriginMap,
        [Parameter(Mandatory)]$EnvelopePolicy,
        [Parameter(Mandatory)][string]$DeltaKey,
        $RigidbodyWitnessMap = $null,
        $RigidbodyWitnessOriginMap = $null,
        [bool]$RigidbodyWitnessTraceEquivalent = $false
    )

    $axis = switch ($DeltaKey) {
        'hero.positionDelta.x' { 'x' }
        'hero.positionDelta.y' { 'y' }
        default {
            return [pscustomobject][ordered]@{
                accepted = $false
                rule = 'derived-render-transform-delta-v1'
                rejection = 'unsupported-derived-render-delta-key'
            }
        }
    }
    $positionKey = "hero.position.$axis"
    $rigidbodyPositionKey = "hero.rigidbody.position.$axis"
    $rigidbodyDeltaKey = "hero.rigidbody.positionDelta.$axis"
    if (-not $EnvelopePolicy.fields.Contains($positionKey)) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = 'derived-render-transform-delta-v1'
            rejection = 'position-field-has-no-negative-control-bound'
        }
    }

    $referencePosition = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key $positionKey `
        -RequireComparable
    $candidatePosition = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key $positionKey `
        -RequireComparable
    $referenceOrigin = Get-T24Float32FieldValue `
        -Map $ReferenceOriginMap `
        -Key $positionKey `
        -RequireComparable
    $candidateOrigin = Get-T24Float32FieldValue `
        -Map $CandidateOriginMap `
        -Key $positionKey `
        -RequireComparable
    $referenceDelta = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key $DeltaKey `
        -RequireComparable
    $candidateDelta = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key $DeltaKey `
        -RequireComparable
    $referenceRigidbodyPosition = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key $rigidbodyPositionKey `
        -RequireComparable
    $candidateRigidbodyPosition = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key $rigidbodyPositionKey `
        -RequireComparable
    $referenceRigidbodyDelta = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key $rigidbodyDeltaKey `
        -RequireComparable
    $candidateRigidbodyDelta = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key $rigidbodyDeltaKey `
        -RequireComparable
    $required = @(
        $referencePosition,
        $candidatePosition,
        $referenceOrigin,
        $candidateOrigin,
        $referenceDelta,
        $candidateDelta,
        $referenceRigidbodyPosition,
        $candidateRigidbodyPosition,
        $referenceRigidbodyDelta,
        $candidateRigidbodyDelta)
    if (@($required | Where-Object { $null -eq $_ }).Count -ne 0) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = 'derived-render-transform-delta-v1'
            rejection = 'required-render-or-rigidbody-field-missing'
        }
    }
    $authoritativeRigidbodyBitwiseExact =
        [string]$referenceRigidbodyPosition.field.canonicalHex `
                -ceq [string]$candidateRigidbodyPosition.field.canonicalHex `
            -and [string]$referenceRigidbodyDelta.field.canonicalHex `
                -ceq [string]$candidateRigidbodyDelta.field.canonicalHex
    $authoritativeRigidbodyFullTraceWitnessEquivalent = $false
    $authoritativeRigidbodySource = 'main-reference'
    if (-not $authoritativeRigidbodyBitwiseExact) {
        if (-not $RigidbodyWitnessTraceEquivalent `
                -or $null -eq $RigidbodyWitnessMap `
                -or $null -eq $RigidbodyWitnessOriginMap) {
            return [pscustomobject][ordered]@{
                accepted = $false
                rule = 'derived-render-transform-delta-v1'
                rejection = 'authoritative-rigidbody-axis-is-not-bitwise-exact'
            }
        }
        $candidateRigidbodyOriginPosition = Get-T24Float32FieldValue `
            -Map $CandidateOriginMap `
            -Key $rigidbodyPositionKey `
            -RequireComparable
        $candidateRigidbodyOriginDelta = Get-T24Float32FieldValue `
            -Map $CandidateOriginMap `
            -Key $rigidbodyDeltaKey `
            -RequireComparable
        $witnessRigidbodyPosition = Get-T24Float32FieldValue `
            -Map $RigidbodyWitnessMap `
            -Key $rigidbodyPositionKey `
            -RequireComparable
        $witnessRigidbodyDelta = Get-T24Float32FieldValue `
            -Map $RigidbodyWitnessMap `
            -Key $rigidbodyDeltaKey `
            -RequireComparable
        $witnessRigidbodyOriginPosition = Get-T24Float32FieldValue `
            -Map $RigidbodyWitnessOriginMap `
            -Key $rigidbodyPositionKey `
            -RequireComparable
        $witnessRigidbodyOriginDelta = Get-T24Float32FieldValue `
            -Map $RigidbodyWitnessOriginMap `
            -Key $rigidbodyDeltaKey `
            -RequireComparable
        $witnessRequired = @(
            $candidateRigidbodyOriginPosition,
            $candidateRigidbodyOriginDelta,
            $witnessRigidbodyPosition,
            $witnessRigidbodyDelta,
            $witnessRigidbodyOriginPosition,
            $witnessRigidbodyOriginDelta)
        if (@($witnessRequired | Where-Object { $null -eq $_ }).Count `
                -ne 0) {
            return [pscustomobject][ordered]@{
                accepted = $false
                rule = 'derived-render-transform-delta-v1'
                rejection = 'authoritative-rigidbody-axis-witness-field-missing'
            }
        }
        $witnessPairs = @(
            [pscustomobject]@{
                candidate = $candidateRigidbodyPosition
                witness = $witnessRigidbodyPosition
            },
            [pscustomobject]@{
                candidate = $candidateRigidbodyDelta
                witness = $witnessRigidbodyDelta
            },
            [pscustomobject]@{
                candidate = $candidateRigidbodyOriginPosition
                witness = $witnessRigidbodyOriginPosition
            },
            [pscustomobject]@{
                candidate = $candidateRigidbodyOriginDelta
                witness = $witnessRigidbodyOriginDelta
            })
        if (@($witnessPairs | Where-Object {
                    [string]$_.candidate.field.canonicalHex `
                        -cne [string]$_.witness.field.canonicalHex
                }).Count -ne 0) {
            return [pscustomobject][ordered]@{
                accepted = $false
                rule = 'derived-render-transform-delta-v1'
                rejection = 'authoritative-rigidbody-axis-witness-does-not-match'
            }
        }
        $authoritativeRigidbodyFullTraceWitnessEquivalent = $true
        $authoritativeRigidbodySource = 'full-no-mod-axis-witness'
    }

    $positionBound = [double](
        $EnvelopePolicy.fields[$positionKey].maxAbsoluteDifference)
    $positionDifference = [Math]::Abs(
        [double]$referencePosition.value `
            - [double]$candidatePosition.value)
    $originDifference = [Math]::Abs(
        [double]$referenceOrigin.value `
            - [double]$candidateOrigin.value)
    if ($positionBound -le 0 `
            -or $positionDifference -gt $positionBound `
            -or $originDifference -gt $positionBound) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = 'derived-render-transform-delta-v1'
            rejection = 'absolute-render-position-is-outside-no-mod-bound'
            positionDifference = $positionDifference
            originDifference = $originDifference
            positionBound = $positionBound
        }
    }

    $referenceExpectedDelta = Get-T24Float32SubtractionHex `
        -Left $referencePosition.value `
        -Right $referenceOrigin.value
    $candidateExpectedDelta = Get-T24Float32SubtractionHex `
        -Left $candidatePosition.value `
        -Right $candidateOrigin.value
    if ($referenceExpectedDelta `
                -cne [string]$referenceDelta.field.canonicalHex `
            -or $candidateExpectedDelta `
                -cne [string]$candidateDelta.field.canonicalHex) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = 'derived-render-transform-delta-v1'
            rejection = 'recorded-render-delta-is-not-bitwise-derived'
            referenceExpectedDelta = $referenceExpectedDelta
            candidateExpectedDelta = $candidateExpectedDelta
        }
    }

    return [pscustomobject][ordered]@{
        accepted = $true
        rule = 'derived-render-transform-delta-v1'
        rejection = ''
        axis = $axis
        positionDifference = $positionDifference
        originDifference = $originDifference
        positionBound = $positionBound
        authoritativeRigidbodyBitwiseExact =
            $authoritativeRigidbodyBitwiseExact
        authoritativeRigidbodyFullTraceWitnessEquivalent =
            $authoritativeRigidbodyFullTraceWitnessEquivalent
        authoritativeRigidbodySource = $authoritativeRigidbodySource
        recordedDeltaBitwiseDerived = $true
    }
}

function Test-T24ProcessAgeClockResidualNormalization {
    param(
        [Parameter(Mandatory)]$ReferenceMap,
        [Parameter(Mandatory)]$CandidateMap
    )

    $rule = 'process-age-float-clock-residual-v1'
    foreach ($key in @(
            'time.relative',
            'time.fixedRelative',
            'time.deltaTime',
            'time.fixedDeltaTime',
            'time.captureDeltaTime',
            'time.timeScale')) {
        $left = Get-T24Float32FieldValue `
            -Map $ReferenceMap `
            -Key $key `
            -RequireComparable
        $right = Get-T24Float32FieldValue `
            -Map $CandidateMap `
            -Key $key `
            -RequireComparable
        if ($null -eq $left `
                -or $null -eq $right `
                -or [string]$left.field.canonicalHex `
                    -cne [string]$right.field.canonicalHex) {
            return [pscustomobject][ordered]@{
                accepted = $false
                rule = $rule
                rejection = "logical-clock-field-is-not-bitwise-exact:$key"
            }
        }
    }
    if (-not $ReferenceMap.Contains('tick.fixedSteps') `
            -or -not $CandidateMap.Contains('tick.fixedSteps') `
            -or [string]$ReferenceMap['tick.fixedSteps'].kind `
                -ne 'Int32' `
            -or [string]$CandidateMap['tick.fixedSteps'].kind `
                -ne 'Int32' `
            -or -not [bool]$ReferenceMap['tick.fixedSteps'].comparable `
            -or -not [bool]$CandidateMap['tick.fixedSteps'].comparable `
            -or [string]$ReferenceMap['tick.fixedSteps'].canonicalHex `
                -cne [string]$CandidateMap['tick.fixedSteps'].canonicalHex) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = $rule
            rejection = 'fixed-step-count-is-not-bitwise-exact'
        }
    }

    $referenceRelative = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key 'time.relative' `
        -RequireComparable
    $candidateRelative = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key 'time.relative' `
        -RequireComparable
    $referenceFixedRelative = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key 'time.fixedRelative' `
        -RequireComparable
    $candidateFixedRelative = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key 'time.fixedRelative' `
        -RequireComparable
    if ([string]$referenceRelative.field.canonicalHex `
                -cne [string]$referenceFixedRelative.field.canonicalHex `
            -or [string]$candidateRelative.field.canonicalHex `
                -cne [string]$candidateFixedRelative.field.canonicalHex) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = $rule
            rejection = 'logical-time-minus-fixed-is-nonzero'
        }
    }

    $referenceRaw = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key 'diagnostic.time.raw' `
        -RequireDiagnostic
    $referenceFixedRaw = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key 'diagnostic.time.fixedRaw' `
        -RequireDiagnostic
    $candidateRaw = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key 'diagnostic.time.raw' `
        -RequireDiagnostic
    $candidateFixedRaw = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key 'diagnostic.time.fixedRaw' `
        -RequireDiagnostic
    $referenceResidual = Get-T24Float32FieldValue `
        -Map $ReferenceMap `
        -Key 'time.timeMinusFixed' `
        -RequireComparable
    $candidateResidual = Get-T24Float32FieldValue `
        -Map $CandidateMap `
        -Key 'time.timeMinusFixed' `
        -RequireComparable
    $required = @(
        $referenceRaw,
        $referenceFixedRaw,
        $candidateRaw,
        $candidateFixedRaw,
        $referenceResidual,
        $candidateResidual)
    if (@($required | Where-Object { $null -eq $_ }).Count -ne 0) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = $rule
            rejection = 'required-raw-clock-field-missing'
        }
    }

    $referenceRawUlp = Get-T24Float32UlpDistance `
        -LeftBits $referenceRaw.bits `
        -RightBits $referenceFixedRaw.bits
    $candidateRawUlp = Get-T24Float32UlpDistance `
        -LeftBits $candidateRaw.bits `
        -RightBits $candidateFixedRaw.bits
    if ($referenceRawUlp -gt 1 -or $candidateRawUlp -gt 1) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = $rule
            rejection = 'raw-clock-distance-exceeds-one-ulp'
            referenceRawUlp = $referenceRawUlp
            candidateRawUlp = $candidateRawUlp
        }
    }

    $referenceExpectedResidual = Get-T24Float32SubtractionHex `
        -Left $referenceRaw.value `
        -Right $referenceFixedRaw.value
    $candidateExpectedResidual = Get-T24Float32SubtractionHex `
        -Left $candidateRaw.value `
        -Right $candidateFixedRaw.value
    if ($referenceExpectedResidual `
                -cne [string]$referenceResidual.field.canonicalHex `
            -or $candidateExpectedResidual `
                -cne [string]$candidateResidual.field.canonicalHex) {
        return [pscustomobject][ordered]@{
            accepted = $false
            rule = $rule
            rejection = 'recorded-clock-residual-is-not-bitwise-derived'
            referenceExpectedResidual = $referenceExpectedResidual
            candidateExpectedResidual = $candidateExpectedResidual
        }
    }

    return [pscustomobject][ordered]@{
        accepted = $true
        rule = $rule
        rejection = ''
        logicalClockBitwiseExact = $true
        logicalTimeMinusFixedZero = $true
        referenceRawUlp = $referenceRawUlp
        candidateRawUlp = $candidateRawUlp
        recordedResidualBitwiseDerived = $true
    }
}

function Import-T24NegativeControlEnvelope {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ReferenceBaseline,
        [Parameter(Mandatory)][string]$ObserverBuildRoot,
        [Parameter(Mandatory)][string[]]$ObserverAssemblyFiles,
        [Parameter(Mandatory)][int]$ExpectedFrameCount
    )

    $envelope = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int]$envelope.schemaVersion -ne 2 `
            -or [string]$envelope.policyId `
                -ne 't24-vanilla-synchronized-negative-control-v13-exact-post-root-request-first-phase' `
            -or [string]$envelope.verdict -ne 'ELIGIBLE' `
            -or -not [bool]$envelope.compatibility.physicalInputSynchronized `
            -or [int]$envelope.compatibility.physicalInputSynchronizationPrimeFrames `
                -ne 1 `
            -or [int]$envelope.compatibility.frameCount `
                -ne $ExpectedFrameCount `
            -or [int]$envelope.compatibility.minimumAllRunInputPrefixFrames `
                -ne $ExpectedFrameCount `
            -or -not [bool]$envelope.compatibility.canonicalInputPhaseEquivalentAcrossAllRuns `
            -or [int]$envelope.compatibility.canonicalInputPhaseEventCount `
                -le 0 `
            -or [string]$envelope.requirements.fixtureReadinessBoundary `
                -ne 'semantic-idle' `
            -or [int]$envelope.requirements.fixtureReadinessRequiredUpdates `
                -ne 10 `
            -or [string]$envelope.requirements.fixtureReadinessAbsoluteTimeTargetCanonicalHex `
                -ne '44000000' `
            -or [double]$envelope.requirements.fixtureReadinessAbsoluteTimeTarget `
                -ne 512d `
            -or [string]$envelope.requirements.recordingAbsoluteTimeTargetCanonicalHex `
                -ne '44400000' `
            -or [double]$envelope.requirements.recordingAbsoluteTimeTarget `
                -ne 768d `
            -or [string]$envelope.requirements.recordingArmPolicyId `
                -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
            -or [string]$envelope.requirements.recordingPhaseContractId `
                -ne 'exact-recording-root-global-phase-zero-v1' `
            -or [int]$envelope.requirements.recordingFramePhaseModulo -ne 4 `
            -or [int]$envelope.requirements.recordingFramePhaseTarget -ne 0 `
            -or [int]$envelope.requirements.recordingRootRequestFrameOffset -ne 1 `
            -or -not [bool]$envelope.requirements.recordingPhaseNormalizationRequired `
            -or [int]$envelope.requirements.recordingPhaseNormalizationMaximumHoldFrames -ne 8 `
            -or [string]$envelope.requirements.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1' `
            -or [string]$envelope.compatibility.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1' `
            -or [int]$envelope.requirements.baselineSchemaVersion -ne 2 `
            -or -not [bool]$envelope.requirements.absoluteTimeBaselineRequired `
            -or -not [bool]$envelope.requirements.absoluteTimeTraceFieldsBitwiseExact `
            -or [string]$envelope.requirements.doublePhaseCalibrationProfile `
                -ne 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
            -or -not [bool]$envelope.requirements.doublePhaseCalibrationRequired `
            -or [double]$envelope.requirements.doublePhaseMaximumAbsoluteResidualSeconds `
                -ne 0d `
            -or [int]$envelope.compatibility.baselineSchemaVersion -ne 2 `
            -or -not [bool]$envelope.compatibility.absoluteTimeTraceFieldsBitwiseExact `
            -or -not [bool]$envelope.compatibility.doublePhaseCalibrationRequired `
            -or [double]$envelope.compatibility.doublePhaseMaximumAbsoluteResidualSeconds `
                -ne 0d `
            -or [string]$envelope.compatibility.fixtureReadinessAbsoluteTimeTargetCanonicalHex `
                -ne '44000000' `
            -or [string]$envelope.compatibility.recordingAbsoluteTimeTargetCanonicalHex `
                -ne '44400000' `
            -or [string]$envelope.compatibility.recordingArmPolicyId `
                -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
            -or [string]$envelope.compatibility.recordingPhaseContractId `
                -ne 'exact-recording-root-global-phase-zero-v1' `
            -or [int]$envelope.compatibility.recordingFramePhaseModulo -ne 4 `
            -or [int]$envelope.compatibility.recordingFramePhaseTarget -ne 0 `
            -or [int]$envelope.compatibility.recordingRootRequestFrameOffset -ne 1 `
            -or -not [bool]$envelope.compatibility.recordingPhaseNormalizationRequired `
            -or [int]$envelope.compatibility.recordingPhaseNormalizationMaximumHoldFrames -ne 8 `
            -or -not [bool]$envelope.fieldPolicy.exactByDefault `
            -or -not [bool]$envelope.fieldPolicy.exactFixtureBaselineRequired `
            -or -not [bool]$envelope.fieldPolicy.authoritativePhysicsRemainsBitwiseExact `
            -or [int]$envelope.violationCount -ne 0) {
        throw 'The T24 negative-control envelope is not eligible or fail-closed.'
    }
    $toleratedClockFields = @(
        $envelope.fieldPolicy.toleratedFields |
            Where-Object {
                [string]$_.key -eq 'time.unscaledDeltaTime' `
                    -or [string]$_.key -eq 'time.unscaledRelative' `
                    -or [string]$_.key -eq 'time.raw' `
                    -or [string]$_.key -eq 'time.fixedRaw'
            })
    if ($toleratedClockFields.Count -ne 0) {
        throw 'Frame-clock and absolute-time fields must remain bitwise exact.'
    }

    $generatorPath = [IO.Path]::GetFullPath(
        [string]$envelope.generatorScript)
    $sourceMatrixPath = [IO.Path]::GetFullPath(
        [string]$envelope.sourceMatrix)
    foreach ($required in @($generatorPath, $sourceMatrixPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Negative-control source artifact is missing: $required"
        }
    }
    $generatorHash = (Get-FileHash `
        -LiteralPath $generatorPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceMatrixHash = (Get-FileHash `
        -LiteralPath $sourceMatrixPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($generatorHash -ne [string]$envelope.generatorScriptSha256 `
            -or $sourceMatrixHash -ne [string]$envelope.sourceMatrixSha256) {
        throw 'Negative-control generator or source-matrix hash changed.'
    }
    $sourceMatrix = Get-Content -LiteralPath $sourceMatrixPath -Raw |
        ConvertFrom-Json
    if ([int]$sourceMatrix.schemaVersion -ne 2 `
            -or [string]$sourceMatrix.verdict -ne 'CAPTURED' `
            -or [string]$sourceMatrix.mode -ne 'vanilla-reference' `
            -or -not [bool]$sourceMatrix.strictFirstAttemptCohort `
            -or [int]$sourceMatrix.maxTicks -ne $ExpectedFrameCount `
            -or [int]$sourceMatrix.attemptedRuns `
                -ne [int]$sourceMatrix.requiredSuccessfulRuns `
            -or [int]$sourceMatrix.successfulRuns `
                -ne [int]$sourceMatrix.requiredSuccessfulRuns `
            -or @($sourceMatrix.attempts).Count `
                -ne [int]$sourceMatrix.requiredSuccessfulRuns `
            -or [int]$sourceMatrix.successfulRuns `
                -ne [int]$envelope.compatibility.validReadOnlyVanillaRuns) {
        throw 'The negative-control source matrix is not a strict v2 vanilla cohort.'
    }
    $physicalInputSource = [IO.Path]::GetFullPath(
        [string]$envelope.compatibility.physicalInputSourceTrace)
    if (-not (Test-Path `
            -LiteralPath $physicalInputSource `
            -PathType Leaf) `
            -or (Get-FileHash `
                -LiteralPath $physicalInputSource `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne [string]$envelope.compatibility.physicalInputSourceTraceSha256) {
        throw 'The synchronized physical-input source is missing or changed.'
    }
    if (-not [string]::Equals(
            $physicalInputSource,
            [IO.Path]::GetFullPath(
                [string]$sourceMatrix.physicalInputTracePath),
            [StringComparison]::OrdinalIgnoreCase) `
            -or [string]$sourceMatrix.physicalInputTraceSha256 `
                -ne [string]$envelope.compatibility.physicalInputSourceTraceSha256) {
        throw 'The envelope input source differs from its source matrix.'
    }

    $expectedAttempts = @{}
    foreach ($attempt in @($sourceMatrix.attempts)) {
        $attemptNumber = [int]$attempt.attempt
        if (-not [bool]$attempt.success `
                -or $attemptNumber -le 0 `
                -or $expectedAttempts.ContainsKey($attemptNumber)) {
            throw 'The source matrix contains an invalid or duplicate attempt.'
        }
        $expectedAttempts[$attemptNumber] = $attempt
    }

    $referenceCatalog = [Collections.Generic.List[object]]::new()
    $seenAttempts = [Collections.Generic.HashSet[int]]::new()
    foreach ($cohort in @($envelope.exactBaselineCatalog)) {
        $cohortSignature = [string]$cohort.signatureSha256
        if ($cohortSignature -notmatch '^[0-9a-f]{64}$' `
                -or [int]$cohort.runCount -ne @($cohort.members).Count `
                -or [int]$cohort.runCount -le 0) {
            throw 'A negative-control exact-baseline cohort is malformed.'
        }
        foreach ($member in @($cohort.members)) {
            $attemptNumber = [int]$member.attempt
            if (-not $expectedAttempts.ContainsKey($attemptNumber) `
                    -or -not $seenAttempts.Add($attemptNumber)) {
                throw 'The exact-baseline catalog has an unknown or duplicate attempt.'
            }
            $expectedAttempt = $expectedAttempts[$attemptNumber]
            $expectedRoot = [IO.Path]::GetFullPath(
                [string]$expectedAttempt.evidenceRoot)
            $expectedTraceRoot = Join-Path `
                $expectedRoot `
                'traces\vanilla-reference'
            $baselinePath = [IO.Path]::GetFullPath(
                [string]$member.baselinePath)
            $tracePath = [IO.Path]::GetFullPath([string]$member.tracePath)
            $phasePath = [IO.Path]::GetFullPath([string]$member.phasePath)
            $resultPath = Join-Path $expectedTraceRoot 'result.json'
            if ([string]$member.name -ne [string]$expectedAttempt.run `
                    -or -not [string]::Equals(
                        $baselinePath,
                        (Join-Path $expectedTraceRoot 'baseline.json'),
                        [StringComparison]::OrdinalIgnoreCase) `
                    -or -not [string]::Equals(
                        $tracePath,
                        (Join-Path $expectedTraceRoot 'trace.jsonl'),
                        [StringComparison]::OrdinalIgnoreCase) `
                    -or -not [string]::Equals(
                        $phasePath,
                        (Join-Path $expectedTraceRoot 'input-phase.jsonl'),
                        [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A catalog member does not map to its source-matrix attempt.'
            }
            foreach ($artifact in @(
                    $baselinePath,
                    $tracePath,
                    $phasePath,
                    $resultPath
                )) {
                if (-not (Test-Path `
                        -LiteralPath $artifact `
                        -PathType Leaf)) {
                    throw 'A negative-control cohort artifact is missing.'
                }
            }
            if ((Get-FileHash `
                        -LiteralPath $baselinePath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                    -ne [string]$member.baselineSha256 `
                    -or (Get-FileHash `
                        -LiteralPath $tracePath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                    -ne [string]$member.traceSha256 `
                    -or (Get-FileHash `
                        -LiteralPath $phasePath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                    -ne [string]$member.phaseSha256 `
                    -or [string]$member.baselineSha256 `
                        -ne [string]$expectedAttempt.baselineSha256 `
                    -or [string]$member.traceSha256 `
                        -ne [string]$expectedAttempt.traceSha256 `
                    -or [string]$member.phaseSha256 `
                        -ne [string]$expectedAttempt.phaseSha256 `
                    -or (Get-FileHash `
                        -LiteralPath $resultPath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                        -ne [string]$expectedAttempt.resultSha256 `
                    -or (Get-T24BaselineSignature -Path $baselinePath) `
                        -ne $cohortSignature) {
                throw 'A negative-control cohort artifact hash or signature changed.'
            }
            $referenceCatalog.Add([pscustomobject][ordered]@{
                signatureSha256 = $cohortSignature
                authoritativeSignatureSha256 = `
                    (Get-T24AuthoritativeBaselineSignature `
                        -Path $baselinePath)
                semanticSignatureSha256 = `
                    (Get-T24SemanticBaselineSignature -Path $baselinePath)
                rigidbodyPositionXCanonicalHex = `
                    (Get-T24BaselineFloatHex `
                        -Path $baselinePath `
                        -Key 'rigidbodyPositionX')
                rigidbodyPositionYCanonicalHex = `
                    (Get-T24BaselineFloatHex `
                        -Path $baselinePath `
                        -Key 'rigidbodyPositionY')
                attempt = $attemptNumber
                name = [string]$member.name
                baselinePath = $baselinePath
                baselineSha256 = [string]$member.baselineSha256
                tracePath = $tracePath
                traceSha256 = [string]$member.traceSha256
                phasePath = $phasePath
                phaseSha256 = [string]$member.phaseSha256
            })
        }
    }
    if ($referenceCatalog.Count -ne `
            [int]$envelope.compatibility.validReadOnlyVanillaRuns `
            -or $seenAttempts.Count -ne $expectedAttempts.Count) {
        throw 'The exact-baseline reference catalog is incomplete.'
    }

    $referenceSignature = Get-T24BaselineSignature `
        -Path $ReferenceBaseline
    if (@(
            $referenceCatalog |
                Where-Object { $_.signatureSha256 -eq $referenceSignature }
        ).Count -eq 0) {
        throw 'Reference baseline is outside the synchronized exact-baseline catalog.'
    }
    $observerHash = Get-T24ObserverAssemblySetSha256 `
        -BuildRoot $ObserverBuildRoot `
        -Files $ObserverAssemblyFiles `
        -SamplingOnly
    if ($observerHash `
            -ne [string]$envelope.compatibility.samplingAssemblySetSha256) {
        throw 'Current Core/GameObservation sampler differs from the negative control.'
    }

    $allowedKeys = @(
        'hero.position.x',
        'hero.position.y',
        'hero.positionDelta.x',
        'hero.positionDelta.y',
        'time.timeMinusFixed',
        'time.unscaledDeltaTime',
        'time.unscaledRelative'
    )
    $fieldMap = [ordered]@{}
    foreach ($field in @($envelope.fieldPolicy.toleratedFields)) {
        $key = [string]$field.key
        $maximumAbsolute = [double]$field.maxAbsoluteDifference
        $maximumUlp = [uint64]$field.maxUlpDistance
        $expectedRule = 'absolute-only-ulp-diagnostic'
        if ($allowedKeys -notcontains $key `
                -or $fieldMap.Contains($key) `
                -or [string]$field.kind -ne 'Float32Bits' `
                -or [string]$field.classification `
                    -ne 'render-transform-or-clock-phase-sample' `
                -or [string]$field.comparisonRule -ne $expectedRule `
                -or [double]::IsNaN($maximumAbsolute) `
                -or [double]::IsInfinity($maximumAbsolute) `
                -or $maximumAbsolute -le 0 `
                -or $maximumUlp -le 0 `
                -or [long]$field.comparisonCount -le 0 `
                -or [long]$field.differenceCount -le 0) {
            throw "Invalid negative-control field policy: $key"
        }
        $fieldMap[$key] = [pscustomobject][ordered]@{
            key = $key
            comparisonRule = $expectedRule
            maxAbsoluteDifference = $maximumAbsolute
            maxUlpDistance = $maximumUlp
        }
    }
    return [pscustomobject][ordered]@{
        path = $Path
        sha256 = (Get-FileHash `
            -LiteralPath $Path `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        policyId = [string]$envelope.policyId
        baselineSignatureSha256 = $referenceSignature
        physicalInputSourceTraceSha256 =
            [string]$envelope.compatibility.physicalInputSourceTraceSha256
        clockContractSha256 =
            [string]$envelope.compatibility.externalClockContractSha256
        fields = $fieldMap
        referenceCatalog = @($referenceCatalog)
    }
}

function Import-T24SupplementalReferenceCatalog {
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$EnvelopePolicy,
        [Parameter(Mandatory)][string]$EnvelopePath,
        [Parameter(Mandatory)][string]$ObserverBuildRoot,
        [Parameter(Mandatory)][string[]]$ObserverAssemblyFiles,
        [Parameter(Mandatory)][int]$ExpectedFrameCount
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }
    $catalog = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int]$catalog.schemaVersion -ne 2 `
            -or [string]$catalog.policyId `
                -ne 't24-supplemental-reference-catalog-v7-exact-recording-phase' `
            -or [string]$catalog.verdict -ne 'ELIGIBLE' `
            -or [string]$catalog.selectionPolicy `
                -ne 'complete-source-matrix-exact-recording-phase-readiness-input-phase-contract-v6' `
            -or [int]$catalog.memberCount -ne @($catalog.members).Count `
            -or [int]$catalog.memberCount -le 0 `
            -or [int]$catalog.rejectedMemberCount `
                -ne @($catalog.rejectedMembers).Count `
            -or [int]$catalog.sourceCohortCount `
                -ne ([int]$catalog.memberCount `
                    + [int]$catalog.rejectedMemberCount) `
            -or [int]$catalog.compatibility.frameCount `
                -ne $ExpectedFrameCount `
            -or [string]$catalog.compatibility.physicalInputSourceTraceSha256 `
                -ne [string]$EnvelopePolicy.physicalInputSourceTraceSha256 `
            -or [string]$catalog.compatibility.externalClockContractSha256 `
                -ne [string]$EnvelopePolicy.clockContractSha256 `
            -or [string]$catalog.compatibility.fixtureReadinessBoundary `
                -ne 'semantic-idle' `
            -or [int]$catalog.compatibility.fixtureReadinessRequiredUpdates `
                -ne 10 `
            -or [double]$catalog.compatibility.fixtureReadinessMaximumPhaseErrorFractionOfFixedStep `
                -ne 0.001d `
            -or [int]$catalog.compatibility.baselineSchemaVersion -ne 2 `
            -or -not [bool]$catalog.compatibility.absoluteTimeTraceFieldsBitwiseExact `
            -or -not [bool]$catalog.compatibility.doublePhaseCalibrationRequired `
            -or [double]$catalog.compatibility.doublePhaseMaximumAbsoluteResidualSeconds `
                -ne 0d `
            -or [string]$catalog.compatibility.fixtureReadinessAbsoluteTimeTargetCanonicalHex `
                -ne '44000000' `
            -or [string]$catalog.compatibility.recordingAbsoluteTimeTargetCanonicalHex `
                -ne '44400000' `
            -or [string]$catalog.compatibility.recordingArmPolicyId `
                -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
            -or [string]$catalog.compatibility.recordingPhaseContractId `
                -ne 'exact-recording-root-global-phase-zero-v1' `
            -or [int]$catalog.compatibility.recordingFramePhaseModulo -ne 4 `
            -or [int]$catalog.compatibility.recordingFramePhaseTarget -ne 0 `
            -or [int]$catalog.compatibility.recordingRootRequestFrameOffset -ne 1 `
            -or -not [bool]$catalog.compatibility.recordingPhaseNormalizationRequired `
            -or [int]$catalog.compatibility.recordingPhaseNormalizationMaximumHoldFrames -ne 8 `
            -or [string]$catalog.compatibility.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1') {
        throw 'The supplemental T24 reference catalog is not eligible.'
    }

    $generatorPath = [IO.Path]::GetFullPath(
        [string]$catalog.generatorScript)
    $helperPath = [IO.Path]::GetFullPath([string]$catalog.helperScript)
    $catalogEnvelopePath = [IO.Path]::GetFullPath(
        [string]$catalog.negativeControlEnvelope)
    $sourceMatrixPath = [IO.Path]::GetFullPath(
        [string]$catalog.sourceMatrix)
    foreach ($required in @(
            $generatorPath,
            $helperPath,
            $catalogEnvelopePath,
            $sourceMatrixPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Supplemental catalog dependency is missing: $required"
        }
    }
    if ((Get-FileHash `
                -LiteralPath $generatorPath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ne [string]$catalog.generatorScriptSha256 `
            -or (Get-FileHash `
                -LiteralPath $helperPath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne [string]$catalog.helperScriptSha256 `
            -or -not [string]::Equals(
                $catalogEnvelopePath,
                [IO.Path]::GetFullPath($EnvelopePath),
                [StringComparison]::OrdinalIgnoreCase) `
            -or (Get-FileHash `
                -LiteralPath $catalogEnvelopePath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne [string]$catalog.negativeControlEnvelopeSha256 `
            -or [string]$catalog.negativeControlEnvelopeSha256 `
                -ne [string]$EnvelopePolicy.sha256 `
            -or (Get-FileHash `
                -LiteralPath $sourceMatrixPath `
                -Algorithm SHA256).Hash.ToLowerInvariant() `
                -ne [string]$catalog.sourceMatrixSha256) {
        throw 'A supplemental catalog dependency changed.'
    }
    $sourceMatrix = Get-Content -LiteralPath $sourceMatrixPath -Raw |
        ConvertFrom-Json
    if ([int]$sourceMatrix.schemaVersion -ne 2 `
            -or [string]$sourceMatrix.verdict -ne 'CAPTURED' `
            -or [string]$sourceMatrix.mode -ne 'vanilla-reference' `
            -or -not [bool]$sourceMatrix.strictFirstAttemptCohort `
            -or -not [bool]$sourceMatrix.stoppedAtFirstFailure `
            -or [int]$sourceMatrix.attemptedRuns `
                -ne [int]$catalog.sourceCohortCount `
            -or @($sourceMatrix.attempts).Count `
                -ne [int]$catalog.sourceCohortCount) {
        throw 'The supplemental source matrix is incomplete or changed.'
    }
    $seenSourceOrdinals = [Collections.Generic.HashSet[int]]::new()
    function Test-T24SupplementalReadinessPhase {
        param(
            [Parameter(Mandatory)]$ClockProfile,
            [Parameter(Mandatory)][double]$MaximumFraction
        )

        $fixedDeltaTime = [single]$ClockProfile.fixedDeltaTime
        $timeMinusFixed = [single]$ClockProfile.timeMinusFixed
        return -not [single]::IsNaN($fixedDeltaTime) `
            -and -not [single]::IsInfinity($fixedDeltaTime) `
            -and [double]$fixedDeltaTime -gt 0d `
            -and -not [single]::IsNaN($timeMinusFixed) `
            -and -not [single]::IsInfinity($timeMinusFixed) `
            -and [Math]::Abs([double]$timeMinusFixed) `
                -le ([Math]::Abs([double]$fixedDeltaTime) * $MaximumFraction)
    }
    foreach ($rejected in @($catalog.rejectedMembers)) {
        if ([int]$rejected.sourceOrdinal -lt 1 `
                -or [int]$rejected.sourceOrdinal `
                    -gt [int]$catalog.sourceCohortCount `
                -or -not $seenSourceOrdinals.Add(
                    [int]$rejected.sourceOrdinal) `
                -or [string]$rejected.reason -notin @(
                    'fixture-readiness-phase-outside-contract',
                    'captured-input-timeline-mismatch',
                    'canonical-phase-invalid',
                    'canonical-phase-timeline-mismatch')) {
            throw 'The supplemental rejected-member ledger is malformed.'
        }
        $rejectedSourceAttempt = @($sourceMatrix.attempts)[
            [int]$rejected.sourceOrdinal - 1]
        if ([int]$rejected.sourceAttempt -ne [int]$rejectedSourceAttempt.attempt `
                -or -not [string]::Equals(
                    [IO.Path]::GetFullPath([string]$rejected.evidenceRoot),
                    [IO.Path]::GetFullPath(
                        [string]$rejectedSourceAttempt.evidenceRoot),
                    [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A supplemental rejected member does not match its source matrix.'
        }
        if ([string]$rejected.reason `
                -eq 'fixture-readiness-phase-outside-contract') {
            $expectedClockProfilePath = Join-Path `
                ([IO.Path]::GetFullPath([string]$rejected.evidenceRoot)) `
                'observer-audit\clock-profile.json'
            $rejectedClockProfilePath = [IO.Path]::GetFullPath(
                [string]$rejected.clockProfilePath)
            if (-not [string]::Equals(
                    $rejectedClockProfilePath,
                    $expectedClockProfilePath,
                    [StringComparison]::OrdinalIgnoreCase) `
                    -or -not (Test-Path `
                        -LiteralPath $rejectedClockProfilePath `
                        -PathType Leaf) `
                    -or (Get-FileHash `
                        -LiteralPath $rejectedClockProfilePath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                        -ne [string]$rejected.clockProfileSha256) {
                throw 'A rejected supplemental readiness profile changed.'
            }
            $rejectedClockProfile = Get-Content `
                -LiteralPath $rejectedClockProfilePath `
                -Raw | ConvertFrom-Json
            if (Test-T24SupplementalReadinessPhase `
                    -ClockProfile $rejectedClockProfile `
                    -MaximumFraction ([double](
                        $catalog.compatibility.fixtureReadinessMaximumPhaseErrorFractionOfFixedStep))) {
                throw 'A rejected supplemental readiness profile now satisfies the contract.'
            }
        }
    }

    $currentSamplingHash = Get-T24ObserverAssemblySetSha256 `
        -BuildRoot $ObserverBuildRoot `
        -Files $ObserverAssemblyFiles `
        -SamplingOnly
    if ($currentSamplingHash `
            -ne [string]$catalog.compatibility.samplingAssemblySetSha256) {
        throw 'The supplemental catalog sampler differs from the current build.'
    }

    function Get-AuditAssemblyFingerprint {
        param(
            [Parameter(Mandatory)][string]$AssembliesPath,
            [switch]$SamplingOnly
        )

        $assemblies = @(Get-Content `
            -LiteralPath $AssembliesPath `
            -Raw | ConvertFrom-Json)
        if ($SamplingOnly) {
            $assemblies = @(
                $assemblies |
                    Where-Object {
                        [IO.Path]::GetFileName([string]$_.file) -in @(
                            'HollowKnightTAS.Core.dll',
                            'HollowKnightTAS.GameObservation.dll')
                    })
            if ($assemblies.Count -ne 2) {
                throw 'A supplemental audit lacks both sampling assemblies.'
            }
        }
        $normalized = @(
            $assemblies |
                Sort-Object file |
                ForEach-Object {
                    [ordered]@{
                        file = [string]$_.file
                        length = [long]$_.length
                        sha256 = [string]$_.sha256
                    }
                })
        return Get-T24Sha256Text -Text (
            $normalized | ConvertTo-Json -Compress -Depth 10)
    }

    $references = [Collections.Generic.List[object]]::new()
    $seenAttempts = [Collections.Generic.HashSet[int]]::new()
    $previousSourceOrdinal = 0
    for ($index = 0; $index -lt @($catalog.members).Count; $index++) {
        $member = @($catalog.members)[$index]
        $expectedOrdinal = $index + 1
        $expectedAttempt = 1000 + $expectedOrdinal
        if ([int]$member.ordinal -ne $expectedOrdinal `
                -or [int]$member.catalogAttempt -ne $expectedAttempt `
                -or -not $seenAttempts.Add($expectedAttempt) `
                -or [string]$member.name `
                    -ne ("supplemental-{0:D2}" -f $expectedOrdinal) `
                -or [int]$member.sourceOrdinal -le $previousSourceOrdinal `
                -or [int]$member.sourceOrdinal `
                    -gt [int]$catalog.sourceCohortCount `
                -or -not $seenSourceOrdinals.Add(
                    [int]$member.sourceOrdinal)) {
            throw 'Supplemental catalog ordering is malformed.'
        }
        $previousSourceOrdinal = [int]$member.sourceOrdinal

        $root = [IO.Path]::GetFullPath([string]$member.evidenceRoot)
        $sourceAttempt = @($sourceMatrix.attempts)[
            [int]$member.sourceOrdinal - 1]
        if ([int]$member.sourceAttempt -ne [int]$sourceAttempt.attempt `
                -or -not [string]::Equals(
                    $root,
                    [IO.Path]::GetFullPath(
                        [string]$sourceAttempt.evidenceRoot),
                    [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Supplemental member does not match its source matrix.'
        }
        $traceRoot = Join-Path $root 'traces\vanilla-reference'
        $expectedPaths = [ordered]@{
            baselinePath = Join-Path $traceRoot 'baseline.json'
            tracePath = Join-Path $traceRoot 'trace.jsonl'
            phasePath = Join-Path $traceRoot 'input-phase.jsonl'
            resultPath = Join-Path $traceRoot 'result.json'
            summaryPath = Join-Path $root 'reference-smoke.json'
            assembliesPath = Join-Path $root 'observer-audit\assemblies.json'
            clockPath = Join-Path $root 'observer-audit\clock-injection.json'
            clockProfilePath = Join-Path $root 'observer-audit\clock-profile.json'
        }
        $hashProperties = [ordered]@{
            baselinePath = 'baselineSha256'
            tracePath = 'traceSha256'
            phasePath = 'phaseSha256'
            resultPath = 'resultSha256'
            summaryPath = 'summarySha256'
            assembliesPath = 'assembliesSha256'
            clockPath = 'clockSha256'
            clockProfilePath = 'clockProfileSha256'
        }
        foreach ($pathProperty in $expectedPaths.Keys) {
            $actualPath = [IO.Path]::GetFullPath(
                [string]$member.$pathProperty)
            if (-not [string]::Equals(
                    $actualPath,
                    [IO.Path]::GetFullPath($expectedPaths[$pathProperty]),
                    [StringComparison]::OrdinalIgnoreCase) `
                    -or -not (Test-Path `
                        -LiteralPath $actualPath `
                        -PathType Leaf) `
                    -or (Get-FileHash `
                        -LiteralPath $actualPath `
                        -Algorithm SHA256).Hash.ToLowerInvariant() `
                        -ne [string]$member.($hashProperties[$pathProperty])) {
                throw "Supplemental catalog artifact changed: $pathProperty"
            }
        }

        $baselinePath = [IO.Path]::GetFullPath(
            [string]$member.baselinePath)
        $tracePath = [IO.Path]::GetFullPath([string]$member.tracePath)
        $phasePath = [IO.Path]::GetFullPath([string]$member.phasePath)
        $summary = Get-Content `
            -LiteralPath ([string]$member.summaryPath) `
            -Raw | ConvertFrom-Json
        $result = Get-Content `
            -LiteralPath ([string]$member.resultPath) `
            -Raw | ConvertFrom-Json
        $baseline = Get-Content -LiteralPath $baselinePath -Raw |
            ConvertFrom-Json
        $clockProfile = Get-Content `
            -LiteralPath ([string]$member.clockProfilePath) `
            -Raw | ConvertFrom-Json
        [void](Assert-T24ExternalRngBoundaryTelemetry `
            -Telemetry $summary `
            -TracePath $tracePath)
        [void](Assert-T24ExternalRngBoundaryTelemetry `
            -Telemetry $result `
            -TracePath $tracePath)
        if ([string]$summary.verdict `
                -ne 'REFERENCE_CLOCK_CONTROLLED_CAPTURED' `
                -or [string]$summary.mode -ne 'vanilla-reference' `
                -or -not [bool]$summary.tasRuntimeAbsent `
                -or -not [bool]$summary.observerOnlyManagedMod `
                -or [bool]$summary.tasControlStarted `
                -or -not [bool]$summary.externalPhysicalInputSynchronized `
                -or [int]$summary.externalInputSynchronizationPrimeFrames -ne 1 `
                -or [string]$summary.physicalInputSourceTraceSha256 `
                    -ne [string]$EnvelopePolicy.physicalInputSourceTraceSha256 `
                -or [bool]$summary.visualRecognitionUsed `
                -or [bool]$summary.observerInputInjected `
                -or [bool]$summary.observerGameplayStateWritten `
                -or -not [bool]$summary.externalRngSynchronized `
                -or -not [bool]$summary.externalRngSynchronizationOriginCaptured `
                -or [string]$summary.externalRngSynchronizationPolicy `
                    -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
                -or [int]$summary.externalRngSeed -ne 1212896321 `
                -or [uint32]$summary.externalClockBridgeAbi -ne 10 `
                -or [int]$summary.externalClockBridgeStatus -ne 2 `
                -or [bool]$summary.externalRuntimeVirtualClockRegistered `
                -or [int]$summary.externalVirtualClockPaused -ne 0 `
                -or [int]$summary.externalVirtualClockPauseCount -ne 0 `
                -or [int]$summary.externalVirtualClockResumePending -ne 0 `
                -or [int]$summary.externalVirtualClockResumeRequestCount -ne 0 `
                -or [int]$summary.externalVirtualClockResumeCount -ne 0 `
                -or -not [bool]$summary.externalDeterministicClockEnabled `
                -or [long]$summary.externalDeterministicClockFrequency -le 0 `
                -or [long]$summary.externalDeterministicClockStepTicks -le 0 `
                -or [long]$summary.externalDeterministicClockFrequency `
                    / [long]$summary.externalDeterministicClockStepTicks -ne 50 `
                -or [long]$summary.externalDeterministicClockAnchor -le 0 `
                -or [int]$summary.externalDeterministicClockFrameAdvanceCount -le 0 `
                -or [int]$summary.externalDeterministicClockEnableFaultCode -ne 0 `
                -or [int]$summary.externalDeterministicClockAdvanceFaultCode -ne 0 `
                -or [string]$summary.externalRealtimeEpochNormalizationPolicyId `
                    -ne 'root-game-minus-rounded-startup-offset-qpc-grid-v3' `
                -or -not [bool]$summary.externalRealtimeEpochNormalizationApplied `
                -or [int]$summary.externalRealtimeEpochNormalizationCount -le 0 `
                -or [string]$summary.externalRealtimeEpochNormalizationAfterBits `
                    -ne [string]$summary.externalRealtimeEpochNormalizationTargetBits `
                -or [int]$summary.externalRealtimeEpochNormalizationFaultCode -ne 0 `
                -or -not [bool]$summary.externalDoublePhaseCalibrationApplied `
                -or [int]$summary.externalDoublePhaseCalibrationAttempts -le 0 `
                -or [int]$summary.externalDoublePhaseCalibrationFaultCode -ne 0 `
                -or [string]$summary.externalDoublePhaseFinalResidualBits `
                    -ne '0000000000000000' `
                -or [string]$summary.externalDoublePhaseFinalResidual.canonicalHex `
                    -ne '0000000000000000' `
                -or [string]$summary.externalDoublePhaseLastCorrectionBits `
                    -eq '00000000' `
                -or -not [bool]$summary.externalStartupClockHookInstalled `
                -or -not [bool]$summary.externalStartupClockLatchEnabled `
                -or [uint32]$summary.externalStartupClockHookThreadId -eq 0 `
                -or -not [bool]$summary.externalStartupClockPrimaryThreadMatched `
                -or [int]$summary.externalStartupClockVirtualQpcCallCount -le 0 `
                -or [int]$summary.externalStartupClockHandoffAdoptCount -ne 1 `
                -or [int]$summary.externalStartupClockFaultCode -ne 0 `
                -or [string]$summary.externalStartupPolicy `
                    -ne 'create-suspended-early-apc-unity-then-bridge-v1' `
                -or [string]$summary.recordingArmPolicyId `
                    -ne 'exact-absolute-time-post-root-request-first-global-phase-host-release-v11' `
                -or [string]$summary.recordingAbsoluteTimeTarget.canonicalHex `
                    -ne '44400000' `
                -or -not [bool]$summary.recordingArmBoundaryReached `
                -or -not [bool]$summary.recordingArmReleased `
                -or [string]$summary.recordingArmBoundaryTimeRaw.canonicalHex `
                    -ne [string]$summary.recordingArmBoundaryFixedTimeRaw.canonicalHex `
                -or -not [bool]$summary.externalTimeUpdateResumeBoundaryInstalled `
                -or [int]$summary.externalTimeUpdateResumeBoundaryInstallCount -ne 1 `
                -or [int]$summary.externalTimeUpdateResumeBoundaryCallbackCount -le 0 `
                -or [int]$summary.externalTimeUpdateResumeBoundaryCommitCount -ne 0 `
                -or [int]$summary.externalTimeUpdateResumeCommitFaultCode -ne 0 `
                -or -not [string]::IsNullOrEmpty(
                    [string]$summary.externalPlayerLoopBoundaryError) `
                -or [string]$summary.baselineSha256 `
                    -ne [string]$member.baselineSha256 `
                -or [string]$summary.traceSha256 `
                    -ne [string]$member.traceSha256 `
                -or -not [bool]$result.success `
                -or [string]$result.samplingBoundary `
                    -ne 'post-render-completed-frame-sampling-v1' `
                -or [int]$result.frameCount -ne $ExpectedFrameCount `
                -or [bool]$result.inputInjected `
                -or [bool]$result.timeWritten `
                -or [bool]$result.gameplayStateWritten `
                -or [bool]$result.saveLoadedByObserver `
                -or -not [bool]$result.externalInputSynchronized `
                -or -not [bool]$result.externalInputSynchronizationRequired `
                -or [int]$result.externalInputSynchronizedFrames `
                    -ne $ExpectedFrameCount `
                -or [int]$result.externalInputSynchronizationPrimeFrames -ne 1 `
                -or -not [bool]$result.externalRngSynchronized `
                -or -not [bool]$result.externalRngSynchronizationOriginCaptured `
                -or [string]$result.externalRngSynchronizationPolicy `
                    -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
                -or [int]$result.externalRngSeed -ne 1212896321 `
                -or [uint32]$result.externalClockBridgeAbi -ne 10 `
                -or [int]$result.externalClockBridgeStatus -ne 2 `
                -or [bool]$result.externalRuntimeVirtualClockRegistered `
                -or [int]$result.externalVirtualClockPaused -ne 0 `
                -or [int]$result.externalVirtualClockPauseCount -ne 0 `
                -or [int]$result.externalVirtualClockResumePending -ne 0 `
                -or [int]$result.externalVirtualClockResumeRequestCount -ne 0 `
                -or [int]$result.externalVirtualClockResumeCount -ne 0 `
                -or -not [bool]$result.externalDeterministicClockEnabled `
                -or [long]$result.externalDeterministicClockFrequency -le 0 `
                -or [long]$result.externalDeterministicClockStepTicks -le 0 `
                -or [long]$result.externalDeterministicClockFrequency `
                    / [long]$result.externalDeterministicClockStepTicks -ne 50 `
                -or [long]$result.externalDeterministicClockAnchor -le 0 `
                -or [int]$result.externalDeterministicClockFrameAdvanceCount -le 0 `
                -or [int]$result.externalDeterministicClockEnableFaultCode -ne 0 `
                -or [int]$result.externalDeterministicClockAdvanceFaultCode -ne 0 `
                -or [string]$result.externalRealtimeEpochNormalizationPolicyId `
                    -ne [string]$summary.externalRealtimeEpochNormalizationPolicyId `
                -or -not [bool]$result.externalRealtimeEpochNormalizationApplied `
                -or [int]$result.externalRealtimeEpochNormalizationCount `
                    -ne [int]$summary.externalRealtimeEpochNormalizationCount `
                -or [string]$result.externalRealtimeEpochNormalizationAfterBits `
                    -ne [string]$result.externalRealtimeEpochNormalizationTargetBits `
                -or [int]$result.externalRealtimeEpochNormalizationFaultCode -ne 0 `
                -or -not [bool]$result.externalDoublePhaseCalibrationApplied `
                -or [int]$result.externalDoublePhaseCalibrationAttempts -le 0 `
                -or [int]$result.externalDoublePhaseCalibrationFaultCode -ne 0 `
                -or [string]$result.externalDoublePhaseFinalResidualBits `
                    -ne '0000000000000000' `
                -or [string]$result.externalDoublePhaseFinalResidual.canonicalHex `
                    -ne '0000000000000000' `
                -or [string]$result.externalDoublePhaseLastCorrectionBits `
                    -ne [string]$summary.externalDoublePhaseLastCorrectionBits `
                -or -not [bool]$result.externalStartupClockHookInstalled `
                -or -not [bool]$result.externalStartupClockLatchEnabled `
                -or [uint32]$result.externalStartupClockHookThreadId `
                    -ne [uint32]$summary.externalStartupClockHookThreadId `
                -or [int]$result.externalStartupClockVirtualQpcCallCount -le 0 `
                -or [int]$result.externalStartupClockHandoffAdoptCount -ne 1 `
                -or [int]$result.externalStartupClockFaultCode -ne 0 `
                -or [string]$result.recordingAbsoluteTimeTarget.canonicalHex `
                    -ne '44400000' `
                -or -not [bool]$result.recordingArmBoundaryReached `
                -or -not [bool]$result.recordingArmReleased `
                -or [string]$result.recordingArmBoundaryTimeRaw.canonicalHex `
                    -ne [string]$result.recordingArmBoundaryFixedTimeRaw.canonicalHex `
                -or -not [bool]$result.externalTimeUpdateResumeBoundaryInstalled `
                -or [int]$result.externalTimeUpdateResumeBoundaryInstallCount -ne 1 `
                -or [int]$result.externalTimeUpdateResumeBoundaryCallbackCount -le 0 `
                -or [int]$result.externalTimeUpdateResumeBoundaryCommitCount -ne 0 `
                -or [int]$result.externalTimeUpdateResumeCommitFaultCode -ne 0 `
                -or -not [string]::IsNullOrEmpty(
                    [string]$result.externalPlayerLoopBoundaryError) `
                -or [string]$baseline.samplingBoundary `
                    -ne 'post-render-completed-frame-sampling-v1' `
                -or [string]$baseline.fixtureReadinessBoundary `
                    -ne 'semantic-idle' `
                -or [int]$baseline.fixtureReadinessRequiredUpdates -ne 10 `
                -or [int]$baseline.schemaVersion -ne 2 `
                -or [string]$baseline.fixtureReadinessAbsoluteTimeTarget.canonicalHex `
                    -ne '44000000' `
                -or [string]$baseline.timeRaw.canonicalHex `
                    -ne [string]$baseline.fixedTimeRaw.canonicalHex `
                -or [string]$baseline.recordingAbsoluteTimeTarget.canonicalHex `
                    -ne '44400000' `
                -or [string]$clockProfile.phase `
                    -ne 'waiting-for-recording-arm-release' `
                -or [string]$clockProfile.readinessBoundary `
                    -ne 'semantic-idle' `
                -or [int]$clockProfile.readinessRequiredUpdates -ne 10 `
                -or [int]$clockProfile.readinessStableUpdates -ne 10 `
                -or [string]$clockProfile.readinessAbsoluteTimeTarget.canonicalHex `
                    -ne '44000000' `
                -or [string]$clockProfile.readinessTimeRaw.canonicalHex `
                    -ne [string]$clockProfile.readinessFixedTimeRaw.canonicalHex `
                -or [string]$clockProfile.recordingAbsoluteTimeTarget.canonicalHex `
                    -ne '44400000' `
                -or -not [bool]$clockProfile.recordingArmBoundaryReached `
                -or [bool]$clockProfile.recordingArmReleased `
                -or [string]$clockProfile.recordingArmBoundaryTimeRaw.canonicalHex `
                    -ne [string]$clockProfile.recordingArmBoundaryFixedTimeRaw.canonicalHex `
                -or -not (Test-T24RecordingPhaseContract `
                    -Telemetry $clockProfile `
                    -RootRequestApplied $false) `
                -or -not [bool]$clockProfile.externalDoublePhaseCalibrationApplied `
                -or [int]$clockProfile.externalDoublePhaseCalibrationAttempts -le 0 `
                -or [int]$clockProfile.externalDoublePhaseCalibrationFaultCode -ne 0 `
                -or [string]$clockProfile.externalDoublePhaseFinalResidualBits `
                    -ne '0000000000000000' `
                -or [string]$clockProfile.externalDoublePhaseFinalResidual.canonicalHex `
                    -ne '0000000000000000' `
                -or [string]$clockProfile.externalDoublePhaseLastCorrectionBits `
                    -ne [string]$summary.externalDoublePhaseLastCorrectionBits `
                -or [string]$clockProfile.timeRawDouble.canonicalHex `
                    -ne [string]$clockProfile.fixedTimeRawDouble.canonicalHex `
                -or -not (Test-T24SupplementalReadinessPhase `
                    -ClockProfile $clockProfile `
                    -MaximumFraction ([double](
                        $catalog.compatibility.fixtureReadinessMaximumPhaseErrorFractionOfFixedStep)))) {
            throw 'A supplemental reference no longer satisfies its read-only contract.'
        }
        if ((Get-T24BaselineSignature -Path $baselinePath) `
                -ne [string]$member.baselineSignatureSha256 `
                -or (Get-AuditAssemblyFingerprint `
                    -AssembliesPath ([string]$member.assembliesPath) `
                    -SamplingOnly) `
                    -ne [string]$catalog.compatibility.samplingAssemblySetSha256 `
                -or (Get-T24ClockContractSha256 `
                    -ClockAudit (Get-Content `
                        -LiteralPath ([string]$member.clockPath) `
                        -Raw | ConvertFrom-Json)) `
                    -ne [string]$EnvelopePolicy.clockContractSha256) {
            throw 'A supplemental reference signature or tooling changed.'
        }
        Assert-T24TraceContract -Path $tracePath
        $firstFrame = ([IO.File]::ReadLines($tracePath) |
            Select-Object -First 1) | ConvertFrom-Json
        if ([string]$firstFrame.comparisonSha256 `
                -ne [string]$member.entryComparisonSha256) {
            throw 'A supplemental reference entry-state hash changed.'
        }
        $references.Add([pscustomobject][ordered]@{
            signatureSha256 = [string]$member.baselineSignatureSha256
            authoritativeSignatureSha256 = `
                (Get-T24AuthoritativeBaselineSignature `
                    -Path $baselinePath)
            semanticSignatureSha256 = `
                (Get-T24SemanticBaselineSignature -Path $baselinePath)
            rigidbodyPositionXCanonicalHex = `
                (Get-T24BaselineFloatHex `
                    -Path $baselinePath `
                    -Key 'rigidbodyPositionX')
            rigidbodyPositionYCanonicalHex = `
                (Get-T24BaselineFloatHex `
                    -Path $baselinePath `
                    -Key 'rigidbodyPositionY')
            attempt = $expectedAttempt
            name = [string]$member.name
            baselinePath = $baselinePath
            baselineSha256 = [string]$member.baselineSha256
            tracePath = $tracePath
            traceSha256 = [string]$member.traceSha256
            phasePath = $phasePath
            phaseSha256 = [string]$member.phaseSha256
        })
    }
    if ($seenSourceOrdinals.Count -ne [int]$catalog.sourceCohortCount) {
        throw 'Supplemental catalog source ordinals are not exhaustive.'
    }
    return @($references)
}

function Test-T24BaselineHeroZVanillaContract {
    param(
        [Parameter(Mandatory)][string]$CandidatePath,
        [string]$ReferencePath = ''
    )

    $policyId = 'hero-z-vanilla-setz-random-v1'
    $minimumCanonicalHex = '3b83126f'
    $maximumCanonicalHex = '3ba3d634'
    $minimum = Convert-T24Float32Hex -Hex $minimumCanonicalHex
    $maximum = Convert-T24Float32Hex -Hex $maximumCanonicalHex

    function Get-T24HeroZStatus {
        param([Parameter(Mandatory)][string]$Path)

        $canonicalHex = ''
        try {
            $canonicalHex = Get-T24BaselineFloatHex `
                -Path $Path `
                -Key 'heroPositionZ'
            $decoded = Convert-T24Float32Hex -Hex $canonicalHex
            if ([single]::IsNaN([single]$decoded.value) `
                    -or [single]::IsInfinity([single]$decoded.value)) {
                return [pscustomobject][ordered]@{
                    valid = $false
                    canonicalHex = $canonicalHex
                    value = $null
                    reason = 'non-finite-float32'
                }
            }
            if ([uint64]$decoded.bits -lt [uint64]$minimum.bits `
                    -or [uint64]$decoded.bits -gt [uint64]$maximum.bits) {
                return [pscustomobject][ordered]@{
                    valid = $false
                    canonicalHex = $canonicalHex
                    value = [single]$decoded.value
                    reason = 'outside-vanilla-setz-range'
                }
            }
            return [pscustomobject][ordered]@{
                valid = $true
                canonicalHex = $canonicalHex
                value = [single]$decoded.value
                reason = ''
            }
        }
        catch {
            return [pscustomobject][ordered]@{
                valid = $false
                canonicalHex = $canonicalHex
                value = $null
                reason = 'missing-or-malformed-float32'
            }
        }
    }

    $candidate = Get-T24HeroZStatus -Path $CandidatePath
    $referenceRequired = -not [string]::IsNullOrWhiteSpace($ReferencePath)
    $reference = if ($referenceRequired) {
        Get-T24HeroZStatus -Path $ReferencePath
    }
    else {
        [pscustomobject][ordered]@{
            valid = $true
            canonicalHex = ''
            value = $null
            reason = ''
        }
    }
    $equivalent = [bool]$candidate.valid -and [bool]$reference.valid
    return [pscustomobject][ordered]@{
        equivalent = $equivalent
        pairedExact = $referenceRequired `
            -and $equivalent `
            -and [string]$candidate.canonicalHex `
                -ceq [string]$reference.canonicalHex
        policyId = $policyId
        candidateValid = [bool]$candidate.valid
        referenceRequired = $referenceRequired
        referenceValid = [bool]$reference.valid
        candidateCanonicalHex = [string]$candidate.canonicalHex
        referenceCanonicalHex = [string]$reference.canonicalHex
        minimumCanonicalHex = $minimumCanonicalHex
        maximumCanonicalHex = $maximumCanonicalHex
        candidateValue = $candidate.value
        referenceValue = $reference.value
        candidateFailureReason = [string]$candidate.reason
        referenceFailureReason = [string]$reference.reason
        vanillaSourceComponent = 'SetZ'
        vanillaSourceMember = 'OnEnable'
        vanillaSourceRule = 'Random.Range(z, z + 0.0009999f)'
        vanillaResetMember = 'HeroController.Update10'
    }
}

function Test-T24BaselineRenderTransformEnvelope {
    param(
        [Parameter(Mandatory)][string]$ReferencePath,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)]$EnvelopePolicy
    )

    $reference = Get-Content -LiteralPath $ReferencePath -Raw |
        ConvertFrom-Json -AsHashtable
    $candidate = Get-Content -LiteralPath $CandidatePath -Raw |
        ConvertFrom-Json -AsHashtable
    $differences = [Collections.Generic.List[object]]::new()
    $equivalent = $true
    $score = 0.0
    foreach ($mapping in @(
            [pscustomobject]@{
                baselineKey = 'heroPositionX'
                traceKey = 'hero.position.x'
            },
            [pscustomobject]@{
                baselineKey = 'heroPositionY'
                traceKey = 'hero.position.y'
            })) {
        $baselineKey = [string]$mapping.baselineKey
        $traceKey = [string]$mapping.traceKey
        $left = if ($reference.ContainsKey($baselineKey)) {
            $reference[$baselineKey]
        }
        else { $null }
        $right = if ($candidate.ContainsKey($baselineKey)) {
            $candidate[$baselineKey]
        }
        else { $null }
        if ($null -eq $left `
                -or $null -eq $right `
                -or $left -isnot [Collections.IDictionary] `
                -or $right -isnot [Collections.IDictionary] `
                -or -not $left.Contains('canonicalHex') `
                -or -not $right.Contains('canonicalHex')) {
            $equivalent = $false
            $differences.Add([ordered]@{
                key = $baselineKey
                accepted = $false
                reason = 'missing-or-malformed-render-transform'
            })
            continue
        }
        $leftHex = [string]$left['canonicalHex']
        $rightHex = [string]$right['canonicalHex']
        if ($leftHex -eq $rightHex) {
            continue
        }
        if (-not $EnvelopePolicy.fields.Contains($traceKey)) {
            $equivalent = $false
            $differences.Add([ordered]@{
                key = $baselineKey
                accepted = $false
                reason = 'no-negative-control-bound'
                referenceCanonicalHex = $leftHex
                candidateCanonicalHex = $rightHex
            })
            continue
        }
        $bound = $EnvelopePolicy.fields[$traceKey]
        $leftFloat = Convert-T24Float32Hex -Hex $leftHex
        $rightFloat = Convert-T24Float32Hex -Hex $rightHex
        $absolute = [Math]::Abs(
            [double]$leftFloat.value - [double]$rightFloat.value)
        $ulp = Get-T24Float32UlpDistance `
            -LeftBits $leftFloat.bits `
            -RightBits $rightFloat.bits
        $accepted = $absolute `
                -le [double]$bound.maxAbsoluteDifference `
            -and ([string]$bound.comparisonRule `
                    -ne 'absolute-and-ulp' `
                -or $ulp -le [uint64]$bound.maxUlpDistance)
        if (-not $accepted) {
            $equivalent = $false
        }
        if ([double]$bound.maxAbsoluteDifference -gt 0) {
            $score += $absolute / [double]$bound.maxAbsoluteDifference
        }
        if ([string]$bound.comparisonRule -eq 'absolute-and-ulp' `
                -and [uint64]$bound.maxUlpDistance -gt 0) {
            $score += [double]$ulp / [double]$bound.maxUlpDistance
        }
        $differences.Add([ordered]@{
            key = $baselineKey
            tracePolicyKey = $traceKey
            accepted = $accepted
            referenceCanonicalHex = $leftHex
            candidateCanonicalHex = $rightHex
            absoluteDifference = $absolute
            ulpDistance = $ulp
            allowedMaxAbsoluteDifference = `
                [double]$bound.maxAbsoluteDifference
            allowedMaxUlpDistance = [uint64]$bound.maxUlpDistance
        })
    }
    return [pscustomobject][ordered]@{
        equivalent = $equivalent
        differenceCount = $differences.Count
        score = $score
        differences = @($differences)
    }
}

function Test-T24BaselineRigidbodyComponentCatalog {
    param(
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)]$EnvelopePolicy,
        [string]$ReferencePath = ''
    )

    $semanticSignature = Get-T24SemanticBaselineSignature `
        -Path $CandidatePath
    $semanticReferences = @(
        $EnvelopePolicy.referenceCatalog |
            Where-Object {
                $_.semanticSignatureSha256 -eq $semanticSignature
            })
    $candidateX = Get-T24BaselineFloatHex `
        -Path $CandidatePath `
        -Key 'rigidbodyPositionX'
    $candidateY = Get-T24BaselineFloatHex `
        -Path $CandidatePath `
        -Key 'rigidbodyPositionY'
    $observedXValues = [Collections.Generic.List[string]]::new()
    $observedYValues = [Collections.Generic.List[string]]::new()
    foreach ($semanticReference in $semanticReferences) {
        $referenceXProperty = $semanticReference.PSObject.Properties[
            'rigidbodyPositionXCanonicalHex']
        $referenceYProperty = $semanticReference.PSObject.Properties[
            'rigidbodyPositionYCanonicalHex']
        if ($null -eq $referenceXProperty `
                -or $null -eq $referenceYProperty) {
            throw 'T24 baseline reference catalog integrity failure: ' `
                + 'a semantic reference is missing a Rigidbody component.'
        }
        $referenceXHex = [string]$referenceXProperty.Value
        $referenceYHex = [string]$referenceYProperty.Value
        if ($referenceXHex -cnotmatch '\A[0-9a-f]{8}\z' `
                -or $referenceYHex -cnotmatch '\A[0-9a-f]{8}\z') {
            throw 'T24 baseline reference catalog integrity failure: ' `
                + 'a semantic reference has malformed Rigidbody canonical hex.'
        }
        $observedXValues.Add($referenceXHex)
        $observedYValues.Add($referenceYHex)
    }
    $observedX = @($observedXValues | Sort-Object -Unique)
    $observedY = @($observedYValues | Sort-Object -Unique)
    $xObserved = $observedX -contains $candidateX
    $yObserved = $observedY -contains $candidateY
    $referenceX = ''
    $referenceY = ''
    $coordinateDifferenceCount = 0
    $absoluteDifferenceScore = 0.0
    if (-not [string]::IsNullOrWhiteSpace($ReferencePath)) {
        $referenceX = Get-T24BaselineFloatHex `
            -Path $ReferencePath `
            -Key 'rigidbodyPositionX'
        $referenceY = Get-T24BaselineFloatHex `
            -Path $ReferencePath `
            -Key 'rigidbodyPositionY'
        foreach ($pair in @(
                [pscustomobject]@{
                    reference = $referenceX
                    candidate = $candidateX
                },
                [pscustomobject]@{
                    reference = $referenceY
                    candidate = $candidateY
                })) {
            if ($pair.reference -eq $pair.candidate) {
                continue
            }
            $coordinateDifferenceCount++
            $left = Convert-T24Float32Hex -Hex $pair.reference
            $right = Convert-T24Float32Hex -Hex $pair.candidate
            $absoluteDifferenceScore += [Math]::Abs(
                [double]$left.value - [double]$right.value)
        }
    }
    return [pscustomobject][ordered]@{
        equivalent = $semanticReferences.Count -gt 0 `
            -and $xObserved `
            -and $yObserved
        semanticSignatureSha256 = $semanticSignature
        semanticReferenceCount = $semanticReferences.Count
        candidateXCanonicalHex = $candidateX
        candidateYCanonicalHex = $candidateY
        xObserved = $xObserved
        yObserved = $yObserved
        observedXCanonicalHex = $observedX
        observedYCanonicalHex = $observedY
        referenceXCanonicalHex = $referenceX
        referenceYCanonicalHex = $referenceY
        coordinateDifferenceCount = $coordinateDifferenceCount
        absoluteDifferenceScore = $absoluteDifferenceScore
    }
}

function Resolve-T24ReferenceForBaseline {
    param(
        [Parameter(Mandatory)]$EnvelopePolicy,
        [Parameter(Mandatory)][string]$CandidateBaselinePath
    )

    $candidateHeroZ = Test-T24BaselineHeroZVanillaContract `
        -CandidatePath $CandidateBaselinePath
    if (-not [bool]$candidateHeroZ.equivalent) {
        return $null
    }
    $signature = Get-T24BaselineSignature -Path $CandidateBaselinePath
    $authoritativeSignature = Get-T24AuthoritativeBaselineSignature `
        -Path $CandidateBaselinePath
    $semanticSignature = Get-T24SemanticBaselineSignature `
        -Path $CandidateBaselinePath
    $componentCatalog = Test-T24BaselineRigidbodyComponentCatalog `
        -CandidatePath $CandidateBaselinePath `
        -EnvelopePolicy $EnvelopePolicy
    $exactMatches = @(
        $EnvelopePolicy.referenceCatalog |
            Where-Object { $_.signatureSha256 -eq $signature } |
            Sort-Object attempt, name)
    $selected = $null
    if ($exactMatches.Count -ne 0) {
        $exactHeroZ = Test-T24BaselineHeroZVanillaContract `
            -CandidatePath $CandidateBaselinePath `
            -ReferencePath $exactMatches[0].baselinePath
        if ([bool]$exactHeroZ.equivalent) {
            $selected = [pscustomobject][ordered]@{
                reference = $exactMatches[0]
                render = Test-T24BaselineRenderTransformEnvelope `
                    -ReferencePath $exactMatches[0].baselinePath `
                    -CandidatePath $CandidateBaselinePath `
                    -EnvelopePolicy $EnvelopePolicy
                component = Test-T24BaselineRigidbodyComponentCatalog `
                    -CandidatePath $CandidateBaselinePath `
                    -ReferencePath $exactMatches[0].baselinePath `
                    -EnvelopePolicy $EnvelopePolicy
                heroZ = $exactHeroZ
                matchKind = 'exact'
            }
        }
    }
    if ($null -eq $selected) {
        $compatible = @(
            $EnvelopePolicy.referenceCatalog |
                Where-Object {
                    $_.authoritativeSignatureSha256 `
                        -eq $authoritativeSignature
                } |
                ForEach-Object {
                    $render = Test-T24BaselineRenderTransformEnvelope `
                        -ReferencePath $_.baselinePath `
                        -CandidatePath $CandidateBaselinePath `
                        -EnvelopePolicy $EnvelopePolicy
                    $heroZ = Test-T24BaselineHeroZVanillaContract `
                        -CandidatePath $CandidateBaselinePath `
                        -ReferencePath $_.baselinePath
                    if ([bool]$render.equivalent `
                            -and [bool]$heroZ.equivalent) {
                        [pscustomobject][ordered]@{
                            reference = $_
                            render = $render
                            component = `
                                Test-T24BaselineRigidbodyComponentCatalog `
                                    -CandidatePath $CandidateBaselinePath `
                                    -ReferencePath $_.baselinePath `
                                    -EnvelopePolicy $EnvelopePolicy
                            heroZ = $heroZ
                            matchKind = if ([bool]$heroZ.pairedExact) {
                                'authoritative-exact-render-envelope'
                            }
                            else {
                                'authoritative-exact-render-envelope-hero-z-vanilla-visual-random'
                            }
                        }
                    }
                } |
                Sort-Object `
                    @{ Expression = { $_.render.score } }, `
                    @{ Expression = { $_.reference.attempt } }, `
                    @{ Expression = { $_.reference.name } })
        if ($compatible.Count -ne 0) {
            $selected = $compatible[0]
        }
        elseif ([bool]$componentCatalog.equivalent) {
            $semanticCompatible = @(
                $EnvelopePolicy.referenceCatalog |
                    Where-Object {
                        $_.semanticSignatureSha256 -eq $semanticSignature
                    } |
                    ForEach-Object {
                        $render = Test-T24BaselineRenderTransformEnvelope `
                            -ReferencePath $_.baselinePath `
                            -CandidatePath $CandidateBaselinePath `
                            -EnvelopePolicy $EnvelopePolicy
                        $component = `
                            Test-T24BaselineRigidbodyComponentCatalog `
                                -CandidatePath $CandidateBaselinePath `
                                -ReferencePath $_.baselinePath `
                                -EnvelopePolicy $EnvelopePolicy
                        $heroZ = Test-T24BaselineHeroZVanillaContract `
                            -CandidatePath $CandidateBaselinePath `
                            -ReferencePath $_.baselinePath
                        if ([bool]$render.equivalent `
                                -and [bool]$component.equivalent `
                                -and [bool]$heroZ.equivalent) {
                            [pscustomobject][ordered]@{
                                reference = $_
                                render = $render
                                component = $component
                                heroZ = $heroZ
                                matchKind = if ([bool]$heroZ.pairedExact) {
                                    'semantic-exact-rigidbody-components-observed-render-envelope'
                                }
                                else {
                                    'semantic-exact-rigidbody-components-observed-render-envelope-hero-z-vanilla-visual-random'
                                }
                            }
                        }
                    } |
                    Sort-Object `
                        @{ Expression = {
                            $_.component.coordinateDifferenceCount
                        } }, `
                        @{ Expression = {
                            $_.component.absoluteDifferenceScore
                        } }, `
                        @{ Expression = { $_.render.score } }, `
                        @{ Expression = { $_.reference.attempt } }, `
                        @{ Expression = { $_.reference.name } })
            if ($semanticCompatible.Count -ne 0) {
                $selected = $semanticCompatible[0]
            }
        }
    }
    if ($null -eq $selected) {
        return $null
    }
    $reference = $selected.reference
    $xWitness = @(
        $EnvelopePolicy.referenceCatalog |
            Where-Object {
                $_.semanticSignatureSha256 -eq $semanticSignature `
                    -and $_.rigidbodyPositionXCanonicalHex `
                        -eq $componentCatalog.candidateXCanonicalHex
            } |
            Sort-Object attempt, name |
            Select-Object -First 1)
    $yWitness = @(
        $EnvelopePolicy.referenceCatalog |
            Where-Object {
                $_.semanticSignatureSha256 -eq $semanticSignature `
                    -and $_.rigidbodyPositionYCanonicalHex `
                        -eq $componentCatalog.candidateYCanonicalHex
            } |
            Sort-Object attempt, name |
            Select-Object -First 1)
    return [pscustomobject][ordered]@{
        signatureSha256 = [string]$reference.signatureSha256
        candidateSignatureSha256 = $signature
        authoritativeSignatureSha256 = $authoritativeSignature
        semanticSignatureSha256 = $semanticSignature
        matchKind = [string]$selected.matchKind
        heroZVanillaVisualRandomEquivalent = [bool]$selected.heroZ.equivalent
        heroZVanillaVisualRandomExact = [bool]$selected.heroZ.pairedExact
        heroZVanillaVisualRandomPolicyId = [string]$selected.heroZ.policyId
        heroZCandidateCanonicalHex = `
            [string]$selected.heroZ.candidateCanonicalHex
        heroZReferenceCanonicalHex = `
            [string]$selected.heroZ.referenceCanonicalHex
        heroZMinimumCanonicalHex = `
            [string]$selected.heroZ.minimumCanonicalHex
        heroZMaximumCanonicalHex = `
            [string]$selected.heroZ.maximumCanonicalHex
        renderTransformDifferenceCount = `
            [int]$selected.render.differenceCount
        rigidbodyComponentsObserved = [bool]$selected.component.equivalent
        rigidbodyPositionDifferenceCount = `
            [int]$selected.component.coordinateDifferenceCount
        attempt = [int]$reference.attempt
        name = [string]$reference.name
        baselinePath = [string]$reference.baselinePath
        tracePath = [string]$reference.tracePath
        phasePath = [string]$reference.phasePath
        rigidbodyAxisWitnessesAvailable =
            $xWitness.Count -eq 1 -and $yWitness.Count -eq 1
        rigidbodyXWitnessAttempt = if ($xWitness.Count -eq 1) {
            [int]$xWitness[0].attempt
        }
        else { 0 }
        rigidbodyXWitnessTracePath = if ($xWitness.Count -eq 1) {
            [string]$xWitness[0].tracePath
        }
        else { '' }
        rigidbodyXWitnessTraceSha256 = if ($xWitness.Count -eq 1) {
            [string]$xWitness[0].traceSha256
        }
        else { '' }
        rigidbodyYWitnessAttempt = if ($yWitness.Count -eq 1) {
            [int]$yWitness[0].attempt
        }
        else { 0 }
        rigidbodyYWitnessTracePath = if ($yWitness.Count -eq 1) {
            [string]$yWitness[0].tracePath
        }
        else { '' }
        rigidbodyYWitnessTraceSha256 = if ($yWitness.Count -eq 1) {
            [string]$yWitness[0].traceSha256
        }
        else { '' }
    }
}

function Compare-T24Baselines {
    param(
        [Parameter(Mandatory)][string]$ReferencePath,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)]$EnvelopePolicy
    )

    $reference = Get-Content -LiteralPath $ReferencePath -Raw |
        ConvertFrom-Json -AsHashtable
    $candidate = Get-Content -LiteralPath $CandidatePath -Raw |
        ConvertFrom-Json -AsHashtable
    $excluded = @('runId', 'mode', 'visualTick', 'fixedTick')
    $differences = @()
    $keys = @(
        @($reference.Keys) + @($candidate.Keys) |
            Where-Object { $excluded -notcontains $_ } |
            Sort-Object -Unique)
    foreach ($key in $keys) {
        $leftPresent = $reference.ContainsKey($key)
        $rightPresent = $candidate.ContainsKey($key)
        $left = if ($leftPresent) { $reference[$key] } else { $null }
        $right = if ($rightPresent) { $candidate[$key] } else { $null }
        $leftCanonical = if ($left -is [Collections.IDictionary] `
                -and $left.Contains('canonicalHex')) {
            [string]$left['canonicalHex']
        }
        elseif ($leftPresent) {
            $left | ConvertTo-Json -Compress -Depth 10
        }
        else { '<missing>' }
        $rightCanonical = if ($right -is [Collections.IDictionary] `
                -and $right.Contains('canonicalHex')) {
            [string]$right['canonicalHex']
        }
        elseif ($rightPresent) {
            $right | ConvertTo-Json -Compress -Depth 10
        }
        else { '<missing>' }
        if (-not $leftPresent `
                -or -not $rightPresent `
                -or $leftCanonical -ne $rightCanonical) {
            $differences += [ordered]@{
                key = [string]$key
                reference = $left
                candidate = $right
            }
        }
    }

    $authoritativeEquivalent = `
        (Get-T24AuthoritativeBaselineSignature -Path $ReferencePath) `
            -eq (Get-T24AuthoritativeBaselineSignature -Path $CandidatePath)
    $semanticEquivalent = `
        (Get-T24SemanticBaselineSignature -Path $ReferencePath) `
            -eq (Get-T24SemanticBaselineSignature -Path $CandidatePath)
    $heroZComparison = Test-T24BaselineHeroZVanillaContract `
        -ReferencePath $ReferencePath `
        -CandidatePath $CandidatePath
    $componentComparison = Test-T24BaselineRigidbodyComponentCatalog `
        -CandidatePath $CandidatePath `
        -ReferencePath $ReferencePath `
        -EnvelopePolicy $EnvelopePolicy
    $renderComparison = Test-T24BaselineRenderTransformEnvelope `
        -ReferencePath $ReferencePath `
        -CandidatePath $CandidatePath `
        -EnvelopePolicy $EnvelopePolicy
    $equivalent = $semanticEquivalent `
        -and [bool]$heroZComparison.equivalent `
        -and [bool]$componentComparison.equivalent `
        -and [bool]$renderComparison.equivalent
    if ($differences.Count -gt 0) {
        [ordered]@{
            schemaVersion = 4
            category = 'fixture-baseline'
            strictEquivalent = $false
            authoritativeExactEquivalent = $authoritativeEquivalent
            semanticExactEquivalent = $semanticEquivalent
            heroZVanillaVisualRandomEquivalent = `
                [bool]$heroZComparison.equivalent
            heroZVanillaVisualRandomExact = `
                [bool]$heroZComparison.pairedExact
            heroZVanillaVisualRandomPolicyId = `
                [string]$heroZComparison.policyId
            heroZVanillaVisualRandomContract = $heroZComparison
            rigidbodyComponentsObserved = `
                [bool]$componentComparison.equivalent
            rigidbodyComponentCatalog = $componentComparison
            renderTransformEnvelopeEquivalent = `
                [bool]$renderComparison.equivalent
            equivalent = $equivalent
            renderTransformPolicy = @($renderComparison.differences)
            differences = $differences
        } |
            ConvertTo-Json -Depth 20 |
            Set-Content `
                -LiteralPath (Join-Path `
                    $firstDifferenceDirectory `
                    "baseline-$Mode.json") `
                -Encoding utf8NoBOM
    }

    return [ordered]@{
        strictEquivalent = $differences.Count -eq 0
        authoritativeExactEquivalent = $authoritativeEquivalent
        semanticExactEquivalent = $semanticEquivalent
        heroZVanillaVisualRandomEquivalent = `
            [bool]$heroZComparison.equivalent
        heroZVanillaVisualRandomExact = `
            [bool]$heroZComparison.pairedExact
        heroZVanillaVisualRandomPolicyId = `
            [string]$heroZComparison.policyId
        heroZCandidateCanonicalHex = `
            [string]$heroZComparison.candidateCanonicalHex
        heroZReferenceCanonicalHex = `
            [string]$heroZComparison.referenceCanonicalHex
        heroZMinimumCanonicalHex = `
            [string]$heroZComparison.minimumCanonicalHex
        heroZMaximumCanonicalHex = `
            [string]$heroZComparison.maximumCanonicalHex
        rigidbodyComponentsObserved = [bool]$componentComparison.equivalent
        rigidbodyPositionDifferenceCount = `
            [int]$componentComparison.coordinateDifferenceCount
        renderTransformEnvelopeEquivalent = `
            [bool]$renderComparison.equivalent
        equivalent = $equivalent
        acceptedRenderTransformDifferenceCount = if ($equivalent) {
            [int]$renderComparison.differenceCount
        }
        else { 0 }
        firstDifferenceKey = if ($differences.Count -eq 0) {
            $null
        }
        else { [string]$differences[0].key }
        differenceCount = $differences.Count
    }
}

function Compare-T24PhaseTraces {
    param(
        [Parameter(Mandatory)][string]$ReferencePath,
        [Parameter(Mandatory)][string]$CandidatePath
    )

    foreach ($path in @($ReferencePath, $CandidatePath)) {
        if (Test-Path -LiteralPath ($path + '.incomplete')) { throw "Incomplete phase trace cannot be verified: $path" }
    }
    $referenceLines = @([IO.File]::ReadAllLines($ReferencePath))
    $candidateLines = @([IO.File]::ReadAllLines($CandidatePath))
    function Find-GameplayPhaseStart([string[]]$Lines) {
        for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
            $observation = $Lines[$lineIndex] | ConvertFrom-Json
            if ([string]$observation.phase -ne 'PlayerActionSet.after' `
                    -or -not (
                        [bool]$observation.held `
                        -or [bool]$observation.pressed `
                        -or [bool]$observation.released)) {
                continue
            }
            for ($beforeIndex = $lineIndex - 1; `
                    $beforeIndex -ge 0; `
                    $beforeIndex--) {
                $before = $Lines[$beforeIndex] | ConvertFrom-Json
                if ([long]$before.visualTick `
                        -ne [long]$observation.visualTick) {
                    break
                }
                if ([string]$before.phase -eq 'PlayerActionSet.before') {
                    return $beforeIndex
                }
            }
            return $lineIndex
        }
        return -1
    }
    $referenceStart = Find-GameplayPhaseStart -Lines $referenceLines
    $candidateStart = Find-GameplayPhaseStart -Lines $candidateLines
    if ($referenceStart -lt 0 -or $candidateStart -lt 0) {
        return [ordered]@{
            equivalent = $false
            referenceEvents = $referenceLines.Count
            candidateEvents = $candidateLines.Count
            comparedReferenceEvents = 0
            comparedCandidateEvents = 0
            firstDifferenceIndex = 0
            failure = 'gameplay-phase-start-missing'
        }
    }
    $referenceLines = @(
        $referenceLines[$referenceStart..($referenceLines.Count - 1)])
    $candidateLines = @(
        $candidateLines[$candidateStart..($candidateLines.Count - 1)])
    function Find-FirstActionCommit([string[]]$Lines) {
        for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
            $observation = $Lines[$lineIndex] | ConvertFrom-Json
            if ([string]$observation.phase -eq 'PlayerActionSet.after') {
                return $lineIndex
            }
        }
        return -1
    }
    $referenceCommitIndex = Find-FirstActionCommit -Lines $referenceLines
    $candidateCommitIndex = Find-FirstActionCommit -Lines $candidateLines
    if ($referenceCommitIndex -lt 0 -or $candidateCommitIndex -lt 0) {
        return [ordered]@{
            equivalent = $false
            referenceEvents = $referenceStart + $referenceLines.Count
            candidateEvents = $candidateStart + $candidateLines.Count
            comparedReferenceEvents = $referenceLines.Count
            comparedCandidateEvents = $candidateLines.Count
            firstDifferenceIndex = 0
            failure = 'action-commit-missing'
        }
    }
    $referenceCommit = $referenceLines[$referenceCommitIndex] |
        ConvertFrom-Json
    $candidateCommit = $candidateLines[$candidateCommitIndex] |
        ConvertFrom-Json
    $limit = [Math]::Min(
        $referenceLines.Count,
        $candidateLines.Count)
    $firstDifference = $null
    $referenceOrigins = $null
    $candidateOrigins = $null
    for ($index = 0; $index -lt $limit; $index++) {
        $left = $referenceLines[$index] | ConvertFrom-Json
        $right = $candidateLines[$index] | ConvertFrom-Json
        if ($null -eq $referenceOrigins) {
            $referenceOrigins = [ordered]@{
                visualTick = [long]$left.visualTick
                updateTick = [long]$left.updateTick
                actionSetTick = [long]$referenceCommit.actionSetTick
                actionTick = [long]$referenceCommit.actionTick
            }
            $candidateOrigins = [ordered]@{
                visualTick = [long]$right.visualTick
                updateTick = [long]$right.updateTick
                actionSetTick = [long]$candidateCommit.actionSetTick
                actionTick = [long]$candidateCommit.actionTick
            }
        }
        $leftCanonical = [ordered]@{
            phase = [string]$left.phase
            held = [bool]$left.held
            pressed = [bool]$left.pressed
            released = [bool]$left.released
            gameObject = [string]$left.gameObject
            fsm = [string]$left.fsm
            state = [string]$left.state
            visualTickRelative = [long]$left.visualTick `
                - [long]$referenceOrigins.visualTick
            updateTickRelative = [long]$left.updateTick `
                - [long]$referenceOrigins.updateTick
            actionSetTickRelative = if (
                $index -lt $referenceCommitIndex) {
                -1L
            }
            else {
                [long]$left.actionSetTick `
                    - [long]$referenceOrigins.actionSetTick
            }
            actionTickRelative = if (
                $index -lt $referenceCommitIndex) {
                -1L
            }
            else {
                [long]$left.actionTick `
                    - [long]$referenceOrigins.actionTick
            }
        }
        $rightCanonical = [ordered]@{
            phase = [string]$right.phase
            held = [bool]$right.held
            pressed = [bool]$right.pressed
            released = [bool]$right.released
            gameObject = [string]$right.gameObject
            fsm = [string]$right.fsm
            state = [string]$right.state
            visualTickRelative = [long]$right.visualTick `
                - [long]$candidateOrigins.visualTick
            updateTickRelative = [long]$right.updateTick `
                - [long]$candidateOrigins.updateTick
            actionSetTickRelative = if (
                $index -lt $candidateCommitIndex) {
                -1L
            }
            else {
                [long]$right.actionSetTick `
                    - [long]$candidateOrigins.actionSetTick
            }
            actionTickRelative = if (
                $index -lt $candidateCommitIndex) {
                -1L
            }
            else {
                [long]$right.actionTick `
                    - [long]$candidateOrigins.actionTick
            }
        }
        $leftJson = $leftCanonical | ConvertTo-Json -Compress
        $rightJson = $rightCanonical | ConvertTo-Json -Compress
        if ($leftJson -ne $rightJson) {
            $firstDifference = [ordered]@{
                index = $index
                reference = $leftCanonical
                candidate = $rightCanonical
            }
            break
        }
    }
    if ($null -eq $firstDifference `
            -and $referenceLines.Count -ne $candidateLines.Count) {
        $firstDifference = [ordered]@{
            index = $limit
            referenceCount = $referenceLines.Count
            candidateCount = $candidateLines.Count
        }
    }
    if ($null -ne $firstDifference) {
        [ordered]@{
            schemaVersion = 1
            category = 'input-phase'
            firstDifference = $firstDifference
        } |
            ConvertTo-Json -Depth 20 |
            Set-Content `
                -LiteralPath (
                    Join-Path `
                        $firstDifferenceDirectory `
                        "phase-$Mode.json") `
                -Encoding utf8NoBOM
    }

    return [ordered]@{
        equivalent = $null -eq $firstDifference
        referenceEvents = $referenceStart + $referenceLines.Count
        candidateEvents = $candidateStart + $candidateLines.Count
        comparedReferenceEvents = $referenceLines.Count
        comparedCandidateEvents = $candidateLines.Count
        firstDifferenceIndex = if ($null -eq $firstDifference) {
            $null
        }
        else {
            [int]$firstDifference.index
        }
    }
}

function Compare-T24Traces {
    param(
        [Parameter(Mandatory)][string]$ReferencePath,
        [Parameter(Mandatory)][string]$CandidatePath
    )

    Assert-T24TraceContract -Path $ReferencePath
    Assert-T24TraceContract -Path $CandidatePath
    $referenceLines = @([IO.File]::ReadAllLines($ReferencePath))
    $candidateLines = @([IO.File]::ReadAllLines($CandidatePath))
    $firstDifference = $null
    $inputEquivalent = $referenceLines.Count -eq $candidateLines.Count
    $limit = [Math]::Min($referenceLines.Count, $candidateLines.Count)
    $inputKeys = @(
        'input.axisX',
        'input.axisY',
        'input.held',
        'input.pressed',
        'input.released'
    )

    for ($index = 0; $index -lt $limit; $index++) {
        $reference = $referenceLines[$index] | ConvertFrom-Json
        $candidate = $candidateLines[$index] | ConvertFrom-Json
        $referenceMap = Get-FieldMap -Frame $reference
        $candidateMap = Get-FieldMap -Frame $candidate

        foreach ($key in $inputKeys) {
            if (-not $referenceMap.Contains($key) `
                    -or -not $candidateMap.Contains($key) `
                    -or [string]$referenceMap[$key].canonicalHex `
                        -ne [string]$candidateMap[$key].canonicalHex) {
                $inputEquivalent = $false
            }
        }

        if ($null -ne $firstDifference `
                -or [string]$reference.comparisonSha256 `
                    -eq [string]$candidate.comparisonSha256) {
            continue
        }

        $differences = @()
        $keys = @(
            @($referenceMap.Keys) + @($candidateMap.Keys) |
                Sort-Object -Unique
        )
        foreach ($key in $keys) {
            $left = if ($referenceMap.Contains($key)) {
                $referenceMap[$key]
            }
            else {
                $null
            }
            $right = if ($candidateMap.Contains($key)) {
                $candidateMap[$key]
            }
            else {
                $null
            }
            $comparable = $null -ne $left `
                -and [bool]$left.comparable `
                -or $null -ne $right -and [bool]$right.comparable
            if (-not $comparable) {
                continue
            }
            if ($null -eq $left `
                    -or $null -eq $right `
                    -or [string]$left.kind -ne [string]$right.kind `
                    -or [string]$left.canonicalHex `
                        -ne [string]$right.canonicalHex) {
                $differences += [ordered]@{
                    key = $key
                    reference = if ($null -eq $left) {
                        $null
                    }
                    else {
                        [ordered]@{
                            kind = [string]$left.kind
                            canonicalHex = [string]$left.canonicalHex
                            displayValue = [string]$left.displayValue
                        }
                    }
                    candidate = if ($null -eq $right) {
                        $null
                    }
                    else {
                        [ordered]@{
                            kind = [string]$right.kind
                            canonicalHex = [string]$right.canonicalHex
                            displayValue = [string]$right.displayValue
                        }
                    }
                }
            }
        }
        $firstDifference = [ordered]@{
            logicalTick = $index
            referenceHash = [string]$reference.comparisonSha256
            candidateHash = [string]$candidate.comparisonSha256
            fields = $differences
        }
    }

    if ($null -eq $firstDifference `
            -and $referenceLines.Count -ne $candidateLines.Count) {
        $firstDifference = [ordered]@{
            logicalTick = $limit
            referenceHash = if ($limit -lt $referenceLines.Count) {
                [string](
                    $referenceLines[$limit] |
                        ConvertFrom-Json
                ).comparisonSha256
            }
            else {
                $null
            }
            candidateHash = if ($limit -lt $candidateLines.Count) {
                [string](
                    $candidateLines[$limit] |
                        ConvertFrom-Json
                ).comparisonSha256
            }
            else {
                $null
            }
            fields = @(
                [ordered]@{
                    key = '$frameCount'
                    reference = $referenceLines.Count
                    candidate = $candidateLines.Count
                }
            )
        }
    }

    if ($null -ne $firstDifference) {
        $contextStart = [Math]::Max(
            0,
            [int]$firstDifference.logicalTick - 8)
        $contextEnd = [Math]::Min(
            $limit - 1,
            [int]$firstDifference.logicalTick + 8)
        $context = @()
        if ($contextEnd -ge $contextStart) {
            for ($index = $contextStart; $index -le $contextEnd; $index++) {
                $reference = $referenceLines[$index] | ConvertFrom-Json
                $candidate = $candidateLines[$index] | ConvertFrom-Json
                $context += [ordered]@{
                    logicalTick = $index
                    referenceHash = [string]$reference.comparisonSha256
                    candidateHash = [string]$candidate.comparisonSha256
                }
            }
        }
        $firstDifference['context'] = $context
        $firstDifference |
            ConvertTo-Json -Depth 30 |
            Set-Content `
                -LiteralPath (
                    Join-Path `
                        $firstDifferenceDirectory `
                        "first-$Mode.json") `
                -Encoding utf8NoBOM
    }

    return [ordered]@{
        schemaVersion = 1
        mode = $Mode
        referenceFrames = $referenceLines.Count
        candidateFrames = $candidateLines.Count
        inputEquivalent = $inputEquivalent
        gameplayEquivalent = $null -eq $firstDifference
        firstDifferenceTick = if ($null -eq $firstDifference) {
            $null
        }
        else {
            [int]$firstDifference.logicalTick
        }
        firstDifferenceKey = if ($null -eq $firstDifference `
                -or @($firstDifference.fields).Count -eq 0) {
            $null
        }
        else {
            [string]$firstDifference.fields[0].key
        }
    }
}

function Compare-T24TracesWithEnvelope {
    param(
        [Parameter(Mandatory)][string]$ReferencePath,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)]$EnvelopePolicy,
        [switch]$AllowBaselineNormalizedRigidbody,
        [string]$RigidbodyXWitnessPath = '',
        [string]$RigidbodyYWitnessPath = ''
    )

    foreach ($witness in @($RigidbodyXWitnessPath, $RigidbodyYWitnessPath)) {
        if (-not [string]::IsNullOrWhiteSpace($witness) -and
                (Test-Path -LiteralPath ($witness + '.incomplete'))) {
            throw "Incomplete rigidbody witness cannot be verified: $witness"
        }
    }
    $strict = Compare-T24Traces `
        -ReferencePath $ReferencePath `
        -CandidatePath $CandidatePath
    $referenceLines = @([IO.File]::ReadAllLines($ReferencePath))
    $candidateLines = @([IO.File]::ReadAllLines($CandidatePath))
    $rigidbodyXWitnessLines = @()
    $rigidbodyYWitnessLines = @()
    $rigidbodyAxisWitnessValidation = [ordered]@{}
    if ($AllowBaselineNormalizedRigidbody) {
        foreach ($witnessPath in @(
                $RigidbodyXWitnessPath,
                $RigidbodyYWitnessPath)) {
            if ([string]::IsNullOrWhiteSpace($witnessPath) `
                    -or -not (Test-Path `
                        -LiteralPath $witnessPath `
                        -PathType Leaf)) {
                throw 'A required Rigidbody axis witness trace is missing.'
            }
        }
        $rigidbodyXWitnessLines = @(
            [IO.File]::ReadAllLines($RigidbodyXWitnessPath))
        $rigidbodyYWitnessLines = @(
            [IO.File]::ReadAllLines($RigidbodyYWitnessPath))
        if ($rigidbodyXWitnessLines.Count -ne $candidateLines.Count `
                -or $rigidbodyYWitnessLines.Count -ne $candidateLines.Count) {
            throw 'A Rigidbody axis witness frame count differs from the candidate.'
        }
        $rigidbodyAxisWitnessValidation['x'] =
            Test-T24RigidbodyAxisWitnessTrace `
                -CandidateLines $candidateLines `
                -WitnessLines $rigidbodyXWitnessLines `
                -Axis 'x'
        $rigidbodyAxisWitnessValidation['y'] =
            Test-T24RigidbodyAxisWitnessTrace `
                -CandidateLines $candidateLines `
                -WitnessLines $rigidbodyYWitnessLines `
                -Axis 'y'
    }
    $limit = [Math]::Min($referenceLines.Count, $candidateLines.Count)
    $referenceOriginMap = if ($referenceLines.Count -gt 0) {
        Get-FieldMap -Frame (
            $referenceLines[0] | ConvertFrom-Json)
    }
    else { [ordered]@{} }
    $candidateOriginMap = if ($candidateLines.Count -gt 0) {
        Get-FieldMap -Frame (
            $candidateLines[0] | ConvertFrom-Json)
    }
    else { [ordered]@{} }
    $firstUnaccepted = $null
    $acceptedDifferenceCount = 0L
    $acceptedByField = [ordered]@{}
    $acceptedBaselineNormalizedRigidbodyDifferenceCount = 0L
    $acceptedBaselineNormalizedRigidbodyByField = [ordered]@{}
    $baselineNormalizedRigidbodyEquivalent =
        -not $AllowBaselineNormalizedRigidbody `
        -or ([bool]$rigidbodyAxisWitnessValidation['x'].equivalent `
            -and [bool]$rigidbodyAxisWitnessValidation['y'].equivalent)
    $rigidbodyAxisFields = @{
        'hero.rigidbody.position.x' = [ordered]@{
            axis = 'x'
            absoluteKey = 'hero.rigidbody.position.x'
            deltaKey = 'hero.rigidbody.positionDelta.x'
        }
        'hero.rigidbody.positionDelta.x' = [ordered]@{
            axis = 'x'
            absoluteKey = 'hero.rigidbody.position.x'
            deltaKey = 'hero.rigidbody.positionDelta.x'
        }
        'hero.rigidbody.position.y' = [ordered]@{
            axis = 'y'
            absoluteKey = 'hero.rigidbody.position.y'
            deltaKey = 'hero.rigidbody.positionDelta.y'
        }
        'hero.rigidbody.positionDelta.y' = [ordered]@{
            axis = 'y'
            absoluteKey = 'hero.rigidbody.position.y'
            deltaKey = 'hero.rigidbody.positionDelta.y'
        }
    }

    function Convert-FieldForReport($Field) {
        if ($null -eq $Field) {
            return $null
        }
        return [ordered]@{
            kind = [string]$Field.kind
            canonicalHex = [string]$Field.canonicalHex
            displayValue = [string]$Field.displayValue
        }
    }

    for ($index = 0; $index -lt $limit; $index++) {
        $reference = $referenceLines[$index] | ConvertFrom-Json
        $candidate = $candidateLines[$index] | ConvertFrom-Json
        if ([string]$reference.comparisonSha256 `
                -eq [string]$candidate.comparisonSha256) {
            continue
        }
        $referenceMap = Get-FieldMap -Frame $reference
        $candidateMap = Get-FieldMap -Frame $candidate
        $unaccepted = @()

        if ([long]$reference.logicalTick `
                -ne [long]$candidate.logicalTick) {
            $unaccepted += [ordered]@{
                key = '$logicalTick'
                reference = [long]$reference.logicalTick
                candidate = [long]$candidate.logicalTick
                reason = 'logical-tick-mismatch'
            }
        }

        $keys = @(
            @($referenceMap.Keys) + @($candidateMap.Keys) |
                Sort-Object -Unique)
        foreach ($key in $keys) {
            $left = if ($referenceMap.Contains($key)) {
                $referenceMap[$key]
            }
            else { $null }
            $right = if ($candidateMap.Contains($key)) {
                $candidateMap[$key]
            }
            else { $null }
            $leftComparable = $null -ne $left -and [bool]$left.comparable
            $rightComparable = $null -ne $right -and [bool]$right.comparable
            if (-not $leftComparable -and -not $rightComparable) {
                continue
            }
            if ($null -eq $left `
                    -or $null -eq $right `
                    -or $leftComparable -ne $rightComparable `
                    -or [string]$left.kind -ne [string]$right.kind) {
                $unaccepted += [ordered]@{
                    key = [string]$key
                    reference = Convert-FieldForReport -Field $left
                    candidate = Convert-FieldForReport -Field $right
                    reason = 'field-schema-mismatch'
                }
                continue
            }
            if ([string]$left.canonicalHex `
                    -eq [string]$right.canonicalHex) {
                continue
            }
            if ($AllowBaselineNormalizedRigidbody `
                    -and $rigidbodyAxisFields.ContainsKey([string]$key)) {
                $axisRule = $rigidbodyAxisFields[[string]$key]
                $witnessPath = if ($axisRule.axis -eq 'x') {
                    $RigidbodyXWitnessPath
                }
                else { $RigidbodyYWitnessPath }
                $witnessLine = if ($axisRule.axis -eq 'x') {
                    $rigidbodyXWitnessLines[$index]
                }
                else { $rigidbodyYWitnessLines[$index] }
                $witnessFrame = $witnessLine | ConvertFrom-Json
                $witnessMap = Get-FieldMap -Frame $witnessFrame
                $absoluteKey = [string]$axisRule.absoluteKey
                $deltaKey = [string]$axisRule.deltaKey
                $candidateAbsolute = if (
                    $candidateMap.Contains($absoluteKey)) {
                    $candidateMap[$absoluteKey]
                }
                else { $null }
                $candidateDelta = if ($candidateMap.Contains($deltaKey)) {
                    $candidateMap[$deltaKey]
                }
                else { $null }
                $witnessAbsolute = if (
                    $witnessMap.Contains($absoluteKey)) {
                    $witnessMap[$absoluteKey]
                }
                else { $null }
                $witnessDelta = if ($witnessMap.Contains($deltaKey)) {
                    $witnessMap[$deltaKey]
                }
                else { $null }
                $axisValidation =
                    $rigidbodyAxisWitnessValidation[$axisRule.axis]
                $axisFullTraceBitwiseExact =
                    [bool]$axisValidation.equivalent
                if ($axisFullTraceBitwiseExact) {
                    $acceptedBaselineNormalizedRigidbodyDifferenceCount++
                    if (-not $acceptedBaselineNormalizedRigidbodyByField.Contains(
                            $key)) {
                        $acceptedBaselineNormalizedRigidbodyByField[$key] = `
                            [ordered]@{
                                key = [string]$key
                                axis = [string]$axisRule.axis
                                witnessTracePath = $witnessPath
                                requiredExactKeys = @(
                                    $absoluteKey,
                                    $deltaKey)
                                acceptedDifferenceCount = 0L
                                firstLogicalTick = $index
                            }
                    }
                    $acceptedBaselineNormalizedRigidbodyByField[$key].acceptedDifferenceCount++
                    continue
                }
                $baselineNormalizedRigidbodyEquivalent = $false
                $unaccepted += [ordered]@{
                    key = [string]$key
                    reference = Convert-FieldForReport -Field $left
                    candidate = Convert-FieldForReport -Field $right
                    reason = 'rigidbody-axis-witness-mismatch'
                    witnessTracePath = $witnessPath
                    fullTraceWitnessValidation = $axisValidation
                    witnessAbsolute = Convert-FieldForReport `
                        -Field $witnessAbsolute
                    witnessDelta = Convert-FieldForReport `
                        -Field $witnessDelta
                }
                continue
            }
            if ([string]$left.kind -ne 'Float32Bits' `
                    -or -not $EnvelopePolicy.fields.Contains($key)) {
                $unaccepted += [ordered]@{
                    key = [string]$key
                    reference = Convert-FieldForReport -Field $left
                    candidate = Convert-FieldForReport -Field $right
                    reason = 'bitwise-exact-field-difference'
                }
                continue
            }

            $leftFloat = Convert-T24Float32Hex `
                -Hex ([string]$left.canonicalHex)
            $rightFloat = Convert-T24Float32Hex `
                -Hex ([string]$right.canonicalHex)
            $absolute = [Math]::Abs(
                [double]$leftFloat.value - [double]$rightFloat.value)
            $ulp = Get-T24Float32UlpDistance `
                -LeftBits $leftFloat.bits `
                -RightBits $rightFloat.bits
            $bound = $EnvelopePolicy.fields[$key]
            $finite = -not [single]::IsNaN($leftFloat.value) `
                -and -not [single]::IsNaN($rightFloat.value) `
                -and -not [single]::IsInfinity($leftFloat.value) `
                -and -not [single]::IsInfinity($rightFloat.value)
            $withinBound = $finite `
                -and $absolute `
                    -le [double]$bound.maxAbsoluteDifference
            if ([string]$bound.comparisonRule -eq 'absolute-and-ulp') {
                $withinBound = $withinBound `
                    -and $ulp -le [uint64]$bound.maxUlpDistance
            }
            if (-not $withinBound) {
                $conditionalNormalization = if ([string]$key -eq `
                        'hero.positionDelta.x' `
                        -or [string]$key -eq `
                            'hero.positionDelta.y') {
                    $renderAxis = if ([string]$key -eq `
                            'hero.positionDelta.x') { 'x' }
                    else { 'y' }
                    $renderWitnessLines = if ($renderAxis -eq 'x') {
                        $rigidbodyXWitnessLines
                    }
                    else { $rigidbodyYWitnessLines }
                    $renderWitnessMap = if (
                        $AllowBaselineNormalizedRigidbody `
                            -and $renderWitnessLines.Count -gt $index) {
                        Get-FieldMap -Frame (
                            $renderWitnessLines[$index] | ConvertFrom-Json)
                    }
                    else { $null }
                    $renderWitnessOriginMap = if (
                        $AllowBaselineNormalizedRigidbody `
                            -and $renderWitnessLines.Count -gt 0) {
                        Get-FieldMap -Frame (
                            $renderWitnessLines[0] | ConvertFrom-Json)
                    }
                    else { $null }
                    $renderWitnessTraceEquivalent =
                        $AllowBaselineNormalizedRigidbody `
                        -and $rigidbodyAxisWitnessValidation.Contains(
                            $renderAxis) `
                        -and [bool](
                            $rigidbodyAxisWitnessValidation[
                                $renderAxis].equivalent)
                    Test-T24DerivedRenderDeltaNormalization `
                        -ReferenceMap $referenceMap `
                        -CandidateMap $candidateMap `
                        -ReferenceOriginMap $referenceOriginMap `
                        -CandidateOriginMap $candidateOriginMap `
                        -EnvelopePolicy $EnvelopePolicy `
                        -DeltaKey ([string]$key) `
                        -RigidbodyWitnessMap $renderWitnessMap `
                        -RigidbodyWitnessOriginMap `
                            $renderWitnessOriginMap `
                        -RigidbodyWitnessTraceEquivalent `
                            $renderWitnessTraceEquivalent
                }
                elseif ([string]$key -eq 'time.timeMinusFixed') {
                    Test-T24ProcessAgeClockResidualNormalization `
                        -ReferenceMap $referenceMap `
                        -CandidateMap $candidateMap
                }
                else { $null }
                if ($null -ne $conditionalNormalization `
                        -and [bool]$conditionalNormalization.accepted) {
                    $acceptedDifferenceCount++
                    if (-not $acceptedByField.Contains($key)) {
                        $acceptedByField[$key] = [ordered]@{
                            key = [string]$key
                            acceptedDifferenceCount = 0L
                            firstLogicalTick = $index
                            maxObservedAbsoluteDifference = 0.0
                            maxObservedUlpDistance = 0L
                            allowedMaxAbsoluteDifference = `
                                [double]$bound.maxAbsoluteDifference
                            allowedMaxUlpDistance = `
                                [uint64]$bound.maxUlpDistance
                            comparisonRule = `
                                [string]$bound.comparisonRule
                            conditionalAcceptanceCount = 0L
                            conditionalAcceptanceRules = @()
                        }
                    }
                    $accepted = $acceptedByField[$key]
                    $accepted.acceptedDifferenceCount++
                    $accepted.conditionalAcceptanceCount++
                    if ($accepted.conditionalAcceptanceRules `
                            -notcontains `
                                [string]$conditionalNormalization.rule) {
                        $accepted.conditionalAcceptanceRules += `
                            [string]$conditionalNormalization.rule
                    }
                    if ($absolute -gt `
                            [double]$accepted.maxObservedAbsoluteDifference) {
                        $accepted.maxObservedAbsoluteDifference = $absolute
                    }
                    if ($ulp -gt `
                            [uint64]$accepted.maxObservedUlpDistance) {
                        $accepted.maxObservedUlpDistance = $ulp
                    }
                    continue
                }
                $unaccepted += [ordered]@{
                    key = [string]$key
                    reference = Convert-FieldForReport -Field $left
                    candidate = Convert-FieldForReport -Field $right
                    reason = 'negative-control-bound-exceeded'
                    absoluteDifference = $absolute
                    maxAbsoluteDifference = `
                        [double]$bound.maxAbsoluteDifference
                    ulpDistance = $ulp
                    maxUlpDistance = [uint64]$bound.maxUlpDistance
                    comparisonRule = [string]$bound.comparisonRule
                    conditionalNormalization = $conditionalNormalization
                }
                continue
            }

            $acceptedDifferenceCount++
            if (-not $acceptedByField.Contains($key)) {
                $acceptedByField[$key] = [ordered]@{
                    key = [string]$key
                    acceptedDifferenceCount = 0L
                    firstLogicalTick = $index
                    maxObservedAbsoluteDifference = 0.0
                    maxObservedUlpDistance = 0L
                    allowedMaxAbsoluteDifference = `
                        [double]$bound.maxAbsoluteDifference
                    allowedMaxUlpDistance = `
                        [uint64]$bound.maxUlpDistance
                    comparisonRule = [string]$bound.comparisonRule
                    conditionalAcceptanceCount = 0L
                    conditionalAcceptanceRules = @()
                }
            }
            $accepted = $acceptedByField[$key]
            $accepted.acceptedDifferenceCount++
            if ($absolute -gt [double]$accepted.maxObservedAbsoluteDifference) {
                $accepted.maxObservedAbsoluteDifference = $absolute
            }
            if ($ulp -gt [uint64]$accepted.maxObservedUlpDistance) {
                $accepted.maxObservedUlpDistance = $ulp
            }
        }

        if ($null -eq $firstUnaccepted -and $unaccepted.Count -ne 0) {
            $firstUnaccepted = [ordered]@{
                logicalTick = $index
                referenceHash = [string]$reference.comparisonSha256
                candidateHash = [string]$candidate.comparisonSha256
                fields = $unaccepted
            }
        }
    }

    if ($null -eq $firstUnaccepted `
            -and $referenceLines.Count -ne $candidateLines.Count) {
        $firstUnaccepted = [ordered]@{
            logicalTick = $limit
            fields = @(
                [ordered]@{
                    key = '$frameCount'
                    reference = $referenceLines.Count
                    candidate = $candidateLines.Count
                    reason = 'frame-count-mismatch'
                })
        }
    }

    [ordered]@{
        schemaVersion = 1
        policyId = $EnvelopePolicy.policyId
        envelopeSha256 = $EnvelopePolicy.sha256
        acceptedDifferenceCount = $acceptedDifferenceCount
        fields = @($acceptedByField.Values)
    } |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (
                Join-Path $firstDifferenceDirectory "tolerated-$Mode.json") `
            -Encoding utf8NoBOM

    [ordered]@{
        schemaVersion = 1
        enabled = [bool]$AllowBaselineNormalizedRigidbody
        equivalent = $baselineNormalizedRigidbodyEquivalent
        acceptanceRule = `
            'each-rigidbody-axis-position-and-positionDelta-must-bitwise-match-a-full-no-mod-trace-with-the-same-baseline-component'
        rigidbodyXWitnessPath = $RigidbodyXWitnessPath
        rigidbodyYWitnessPath = $RigidbodyYWitnessPath
        axisWitnesses = @($rigidbodyAxisWitnessValidation.Values)
        acceptedDifferenceCount = `
            $acceptedBaselineNormalizedRigidbodyDifferenceCount
        fields = @($acceptedBaselineNormalizedRigidbodyByField.Values)
    } |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (
                Join-Path `
                    $firstDifferenceDirectory `
                    "baseline-normalized-$Mode.json") `
            -Encoding utf8NoBOM

    if ($null -ne $firstUnaccepted) {
        [ordered]@{
            schemaVersion = 1
            category = 'vanilla-envelope-unaccepted'
            policyId = $EnvelopePolicy.policyId
            envelopeSha256 = $EnvelopePolicy.sha256
            firstDifference = $firstUnaccepted
        } |
            ConvertTo-Json -Depth 30 |
            Set-Content `
                -LiteralPath (
                    Join-Path `
                        $firstDifferenceDirectory `
                        "first-unaccepted-$Mode.json") `
                -Encoding utf8NoBOM
    }

    return [ordered]@{
        schemaVersion = 3
        mode = $Mode
        referenceFrames = $strict.referenceFrames
        candidateFrames = $strict.candidateFrames
        inputEquivalent = $strict.inputEquivalent
        bitwiseGameplayEquivalent = $strict.gameplayEquivalent
        vanillaEnvelopeEquivalent = $null -eq $firstUnaccepted
        baselineNormalizedRigidbodyEquivalent = `
            $baselineNormalizedRigidbodyEquivalent
        gameplayEquivalent = $null -eq $firstUnaccepted
        firstDifferenceTick = $strict.firstDifferenceTick
        firstDifferenceKey = $strict.firstDifferenceKey
        firstUnacceptedDifferenceTick = if ($null -eq $firstUnaccepted) {
            $null
        }
        else { [int]$firstUnaccepted.logicalTick }
        firstUnacceptedDifferenceKey = if ($null -eq $firstUnaccepted `
                -or @($firstUnaccepted.fields).Count -eq 0) {
            $null
        }
        else { [string]$firstUnaccepted.fields[0].key }
        acceptedDifferenceCount = $acceptedDifferenceCount
        acceptedBaselineNormalizedRigidbodyDifferenceCount = `
            $acceptedBaselineNormalizedRigidbodyDifferenceCount
    }
}

function Get-T24CandidateRunHelperProcesses {
    $tasInstallPrefix = [IO.Path]::GetFullPath($tasInstall).TrimEnd('\') + '\'
    $helpers = [Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($process in @(
            Get-Process `
                -Name `
                    HollowKnightTAS.Companion,
                    HollowKnightTAS.AgentBridge,
                    HollowKnightTAS.NativeHost `
                -ErrorAction SilentlyContinue)) {
        if ($script:beforeHelperPids -contains $process.Id `
                -and $script:ownedHelperPids -notcontains $process.Id) {
            continue
        }

        try {
            $process.Refresh()
            if ($process.HasExited) {
                continue
            }
            $processPath = [string]$process.Path
        }
        catch {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($processPath)) {
            throw (
                'Cannot verify the executable path of a new TAS helper: ' `
                + $process.Id)
        }
        $processFull = [IO.Path]::GetFullPath($processPath)
        if (-not $processFull.StartsWith(
                $tasInstallPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Refusing to stop a new TAS helper outside the isolated TAS ' `
                + "install: $processFull")
        }
        $helpers.Add($process)
    }
    return @($helpers)
}

function Close-RunProcesses {
    if ($null -ne $script:game) {
        try {
            $script:game.Refresh()
            if (-not $script:game.HasExited) {
                [void]$script:game.CloseMainWindow()
                [void]$script:game.WaitForExit(15000)
                $script:game.Refresh()
            }
            if (-not $script:game.HasExited) {
                Stop-Process `
                    -Id $script:game.Id `
                    -ErrorAction SilentlyContinue
                [void]$script:game.WaitForExit(5000)
            }
        }
        catch {
        }
        $script:game.Dispose()
        $script:game = $null
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $remaining = @(Get-T24CandidateRunHelperProcesses)
        $script:ownedHelperPids = @(
            @($script:ownedHelperPids) `
                + @($remaining | ForEach-Object Id) |
                Sort-Object -Unique)
        if ($remaining.Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    foreach ($process in $remaining) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    }
    $forcedStopIds = @($remaining | ForEach-Object Id)
    $remainingAfterStop = @()
    if ($forcedStopIds.Count -ne 0) {
        $forcedStopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        do {
            $remainingAfterStop = @(
                Get-Process `
                    -Id $forcedStopIds `
                    -ErrorAction SilentlyContinue)
            if ($remainingAfterStop.Count -eq 0) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $forcedStopDeadline)
    }
    if ($remainingAfterStop.Count -ne 0) {
        throw (
            'Owned TAS helper processes did not exit before filesystem ' `
            + 'restoration: ' `
            + [string]::Join(',', @($remainingAfterStop.Id)))
    }
    $script:ownedHelperPids = @()
    $script:beforeHelperPids = @()
}

$negativeControlPolicy = Import-T24NegativeControlEnvelope `
    -Path $NegativeControlEnvelopePath `
    -ReferenceBaseline $ReferenceBaselinePath `
    -ObserverBuildRoot $observerBuild `
    -ObserverAssemblyFiles $observerFiles `
    -ExpectedFrameCount $MaxTicks
$supplementalReferences = @(
    Import-T24SupplementalReferenceCatalog `
        -Path $SupplementalReferenceCatalogPath `
        -EnvelopePolicy $negativeControlPolicy `
        -EnvelopePath $NegativeControlEnvelopePath `
        -ObserverBuildRoot $observerBuild `
        -ObserverAssemblyFiles $observerFiles `
        -ExpectedFrameCount $MaxTicks)
if ($supplementalReferences.Count -ne 0) {
    $negativeControlPolicy.referenceCatalog = @(
        @($negativeControlPolicy.referenceCatalog) `
            + $supplementalReferences)
}
$supplementalCatalogSha256 = if (
    [string]::IsNullOrWhiteSpace($SupplementalReferenceCatalogPath)) {
    ''
}
else {
    (Get-FileHash `
        -LiteralPath $SupplementalReferenceCatalogPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{
    schemaVersion = 2
    policyId = $negativeControlPolicy.policyId
    envelopePath = $negativeControlPolicy.path
    envelopeSha256 = $negativeControlPolicy.sha256
    referenceBaselineSignatureSha256 = `
        $negativeControlPolicy.baselineSignatureSha256
    externalClockContractSha256 = `
        $negativeControlPolicy.clockContractSha256
    synchronizedPhysicalInputSourceTraceSha256 = `
        $negativeControlPolicy.physicalInputSourceTraceSha256
    exactBaselineReferenceCount = `
        @($negativeControlPolicy.referenceCatalog).Count
    supplementalReferenceCatalogPath = $SupplementalReferenceCatalogPath
    supplementalReferenceCatalogSha256 = $supplementalCatalogSha256
    supplementalReferenceCount = $supplementalReferences.Count
    toleratedFields = @(
        $negativeControlPolicy.fields.Values |
            ForEach-Object {
                [ordered]@{
                    key = $_.key
                    comparisonRule = $_.comparisonRule
                    maxAbsoluteDifference = $_.maxAbsoluteDifference
                    maxUlpDistance = $_.maxUlpDistance
                }
            })
} |
    ConvertTo-Json -Depth 20 |
    Set-Content `
        -LiteralPath (
            Join-Path $environmentDirectory 'negative-control-policy.json') `
        -Encoding utf8NoBOM

if (-not [string]::IsNullOrWhiteSpace($CompareOnlyCandidateTracePath)) {
    if ($Mode -ne 'tas-passive') {
        throw 'Compare-only capture is currently restricted to tas-passive.'
    }
    $candidateDirectory = Split-Path `
        -Parent $CompareOnlyCandidateTracePath
    $candidateBaselinePath = Join-Path $candidateDirectory 'baseline.json'
    $candidatePhasePath = Join-Path $candidateDirectory 'input-phase.jsonl'
    $candidateResultPath = Join-Path $candidateDirectory 'result.json'
    $candidateSourceRoot = Split-Path `
        -Parent (Split-Path -Parent $candidateDirectory)
    $referenceDirectory = Split-Path -Parent $ReferenceTracePath
    $referenceResultPath = Join-Path $referenceDirectory 'result.json'
    $referenceSourceRoot = Split-Path `
        -Parent (Split-Path -Parent $referenceDirectory)
    $candidateSummaryPath = Join-Path `
        $candidateSourceRoot `
        'reference-smoke.json'
    $referenceSummaryPath = Join-Path `
        $referenceSourceRoot `
        'reference-smoke.json'
    foreach ($required in @(
            $candidateBaselinePath,
            $candidatePhasePath,
            $candidateResultPath,
            $candidateSummaryPath,
            $referenceResultPath,
            $referenceSummaryPath
        )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Compare-only evidence is missing: $required"
        }
    }

    $matchedReference = Resolve-T24ReferenceForBaseline `
        -EnvelopePolicy $negativeControlPolicy `
        -CandidateBaselinePath $candidateBaselinePath
    $referenceBaselineCatalogMatch = $null -ne $matchedReference
    if ($referenceBaselineCatalogMatch) {
        $ReferenceTracePath = $matchedReference.tracePath
        $ReferenceBaselinePath = $matchedReference.baselinePath
        $ReferencePhasePath = $matchedReference.phasePath
    }

    $candidateResult = Get-Content `
        -LiteralPath $candidateResultPath `
        -Raw |
        ConvertFrom-Json
    $referenceResult = Get-Content `
        -LiteralPath $referenceResultPath `
        -Raw |
        ConvertFrom-Json
    $candidateSummary = Get-Content `
        -LiteralPath $candidateSummaryPath `
        -Raw |
        ConvertFrom-Json
    $referenceSummary = Get-Content `
        -LiteralPath $referenceSummaryPath `
        -Raw |
        ConvertFrom-Json
    $candidateRngBoundaryAudit =
        Assert-T24ExternalRngBoundaryTelemetry `
            -Telemetry $candidateResult `
            -TracePath $CompareOnlyCandidateTracePath
    $referenceRngBoundaryAudit =
        Assert-T24ExternalRngBoundaryTelemetry `
            -Telemetry $referenceResult `
            -TracePath $ReferenceTracePath
    [void](Assert-T24ExternalRngBoundaryTelemetry `
        -Telemetry $candidateSummary `
        -TracePath $CompareOnlyCandidateTracePath)
    [void](Assert-T24ExternalRngBoundaryTelemetry `
        -Telemetry $referenceSummary `
        -TracePath $ReferenceTracePath)
    $passiveContractClean = [bool]$candidateResult.success `
        -and [bool]$referenceResult.success `
        -and [int]$candidateResult.frameCount -eq $MaxTicks `
        -and [int]$referenceResult.frameCount -eq $MaxTicks `
        -and -not [bool]$candidateResult.inputInjected `
        -and -not [bool]$referenceResult.inputInjected `
        -and -not [bool]$candidateResult.timeWritten `
        -and -not [bool]$referenceResult.timeWritten `
        -and -not [bool]$candidateResult.gameplayStateWritten `
        -and -not [bool]$referenceResult.gameplayStateWritten `
        -and [bool]$candidateResult.externalInputSynchronized `
        -and [bool]$referenceResult.externalInputSynchronized `
        -and [bool]$candidateResult.externalInputSynchronizationRequired `
        -and [bool]$referenceResult.externalInputSynchronizationRequired `
        -and [int]$candidateResult.externalInputSynchronizedFrames `
            -eq $MaxTicks `
        -and [int]$referenceResult.externalInputSynchronizedFrames `
            -eq $MaxTicks `
        -and [int]$candidateResult.externalInputSynchronizationPrimeFrames -eq 1 `
        -and [int]$referenceResult.externalInputSynchronizationPrimeFrames -eq 1 `
        -and [bool]$candidateResult.externalRngSynchronized `
        -and [bool]$referenceResult.externalRngSynchronized `
        -and [bool]$candidateResult.externalRngSynchronizationOriginCaptured `
        -and [bool]$referenceResult.externalRngSynchronizationOriginCaptured `
        -and [string]$candidateResult.externalRngSynchronizationPolicy `
            -eq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [string]$referenceResult.externalRngSynchronizationPolicy `
            -eq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [int]$candidateResult.externalRngSeed -eq 1212896321 `
        -and [int]$referenceResult.externalRngSeed -eq 1212896321 `
        -and [int]$candidateRngBoundaryAudit.transitionCount `
            -eq [int]$referenceRngBoundaryAudit.transitionCount `
        -and [uint32]$candidateResult.externalClockBridgeAbi -eq 10 `
        -and [uint32]$referenceResult.externalClockBridgeAbi -eq 10 `
        -and [int]$candidateResult.externalClockBridgeStatus -eq 2 `
        -and [int]$referenceResult.externalClockBridgeStatus -eq 2 `
        -and [bool]$candidateResult.externalRuntimeVirtualClockRegistered `
        -and -not [bool]$referenceResult.externalRuntimeVirtualClockRegistered `
        -and [int]$candidateResult.externalVirtualClockPaused -eq 0 `
        -and [int]$referenceResult.externalVirtualClockPaused -eq 0 `
        -and [int]$candidateResult.externalVirtualClockPauseCount -eq 0 `
        -and [int]$referenceResult.externalVirtualClockPauseCount -eq 0 `
        -and [int]$candidateResult.externalVirtualClockResumePending -eq 0 `
        -and [int]$referenceResult.externalVirtualClockResumePending -eq 0 `
        -and [int]$candidateResult.externalVirtualClockResumeRequestCount -eq 0 `
        -and [int]$referenceResult.externalVirtualClockResumeRequestCount -eq 0 `
        -and [int]$candidateResult.externalVirtualClockResumeCount -eq 0 `
        -and [int]$referenceResult.externalVirtualClockResumeCount -eq 0 `
        -and [bool]$candidateResult.externalDeterministicClockEnabled `
        -and [bool]$referenceResult.externalDeterministicClockEnabled `
        -and [long]$candidateResult.externalDeterministicClockFrequency -gt 0 `
        -and [long]$candidateResult.externalDeterministicClockFrequency `
            -eq [long]$referenceResult.externalDeterministicClockFrequency `
        -and [long]$candidateResult.externalDeterministicClockStepTicks -gt 0 `
        -and [long]$candidateResult.externalDeterministicClockStepTicks `
            -eq [long]$referenceResult.externalDeterministicClockStepTicks `
        -and [long]$candidateResult.externalDeterministicClockFrequency `
            / [long]$candidateResult.externalDeterministicClockStepTicks -eq 50 `
        -and [long]$candidateResult.externalDeterministicClockAnchor -gt 0 `
        -and [long]$referenceResult.externalDeterministicClockAnchor -gt 0 `
        -and [int]$candidateResult.externalDeterministicClockFrameAdvanceCount -gt 0 `
        -and [int]$referenceResult.externalDeterministicClockFrameAdvanceCount -gt 0 `
        -and [int]$candidateResult.externalDeterministicClockEnableFaultCode -eq 0 `
        -and [int]$referenceResult.externalDeterministicClockEnableFaultCode -eq 0 `
        -and [int]$candidateResult.externalDeterministicClockAdvanceFaultCode -eq 0 `
        -and [int]$referenceResult.externalDeterministicClockAdvanceFaultCode -eq 0 `
        -and [bool]$candidateResult.externalTimeUpdateResumeBoundaryInstalled `
        -and [bool]$referenceResult.externalTimeUpdateResumeBoundaryInstalled `
        -and [int]$candidateResult.externalTimeUpdateResumeBoundaryInstallCount -eq 1 `
        -and [int]$referenceResult.externalTimeUpdateResumeBoundaryInstallCount -eq 1 `
        -and [int]$candidateResult.externalTimeUpdateResumeBoundaryCommitCount -eq 0 `
        -and [int]$referenceResult.externalTimeUpdateResumeBoundaryCommitCount -eq 0 `
        -and [int]$candidateResult.externalTimeUpdateResumeCommitFaultCode -eq 0 `
        -and [int]$referenceResult.externalTimeUpdateResumeCommitFaultCode -eq 0 `
        -and [string]$candidateSummary.mode -eq 'tas-passive' `
        -and [string]$referenceSummary.mode -eq 'vanilla-reference' `
        -and -not [bool]$candidateSummary.tasControlStarted `
        -and [bool]$referenceSummary.tasRuntimeAbsent `
        -and [bool]$candidateSummary.externalPhysicalInputSynchronized `
        -and [bool]$referenceSummary.externalPhysicalInputSynchronized `
        -and [string]$candidateSummary.physicalInputSourceTraceSha256 `
            -eq [string]$referenceSummary.physicalInputSourceTraceSha256 `
        -and [string]$candidateSummary.clockCapabilityId `
            -eq [string]$referenceSummary.clockCapabilityId `
        -and [string]$candidateSummary.clockProfile `
            -eq [string]$referenceSummary.clockProfile `
        -and -not [bool]$candidateSummary.visualRecognitionUsed `
        -and -not [bool]$referenceSummary.visualRecognitionUsed `
        -and -not [bool]$candidateSummary.observerInputInjected `
        -and -not [bool]$referenceSummary.observerInputInjected `
        -and -not [bool]$candidateSummary.observerGameplayStateWritten `
        -and -not [bool]$referenceSummary.observerGameplayStateWritten

    $scenarioCoverageCandidate = $null
    $scenarioCoverageReference = $null
    if ($null -ne $scenarioContract) {
        $coverageScript = Join-Path `
            $PSScriptRoot `
            'Test-T24ScenarioCoverage.ps1'
        $scenarioCoverageCandidate = & $coverageScript `
            -ContractPath $ScenarioContractPath `
            -TracePath $CompareOnlyCandidateTracePath `
            -OutputPath (
                Join-Path `
                    $EvidenceRoot `
                    'scenario-coverage.candidate.json') `
            -RunMode TasPassive `
            -NoThrow
        $scenarioCoverageReference = & $coverageScript `
            -ContractPath $ScenarioContractPath `
            -TracePath $ReferenceTracePath `
            -OutputPath (
                Join-Path `
                    $EvidenceRoot `
                    'scenario-coverage.reference.json') `
            -RunMode VanillaReference `
            -NoThrow
    }
    $scenarioCoverageClean = $null -eq $scenarioContract `
        -or ([string]$scenarioCoverageCandidate.verdict -ceq 'PASS' `
            -and [string]$scenarioCoverageReference.verdict -ceq 'PASS')

    $baselineComparison = Compare-T24Baselines `
        -ReferencePath $ReferenceBaselinePath `
        -CandidatePath $candidateBaselinePath `
        -EnvelopePolicy $negativeControlPolicy
    $phaseComparison = Compare-T24PhaseTraces `
        -ReferencePath $ReferencePhasePath `
        -CandidatePath $candidatePhasePath
    $allowBaselineNormalizedRigidbody = `
        [bool]$baselineComparison.equivalent `
        -and [int]$baselineComparison.rigidbodyPositionDifferenceCount -gt 0
    $rigidbodyXWitnessPath = if (
        $allowBaselineNormalizedRigidbody `
            -and $referenceBaselineCatalogMatch) {
        [string]$matchedReference.rigidbodyXWitnessTracePath
    }
    else { '' }
    $rigidbodyYWitnessPath = if (
        $allowBaselineNormalizedRigidbody `
            -and $referenceBaselineCatalogMatch) {
        [string]$matchedReference.rigidbodyYWitnessTracePath
    }
    else { '' }
    $traceComparison = Compare-T24TracesWithEnvelope `
        -ReferencePath $ReferenceTracePath `
        -CandidatePath $CompareOnlyCandidateTracePath `
        -EnvelopePolicy $negativeControlPolicy `
        -AllowBaselineNormalizedRigidbody:$allowBaselineNormalizedRigidbody `
        -RigidbodyXWitnessPath $rigidbodyXWitnessPath `
        -RigidbodyYWitnessPath $rigidbodyYWitnessPath
    $comparison = [ordered]@{
        schemaVersion = 4
        mode = $Mode
        referenceBaselineCatalogMatch = $referenceBaselineCatalogMatch
        referenceBaselineSignatureSha256 = if (
            $referenceBaselineCatalogMatch) {
            $matchedReference.signatureSha256
        }
        else { '' }
        selectedReferenceAttempt = if ($referenceBaselineCatalogMatch) {
            $matchedReference.attempt
        }
        else { 0 }
        referenceBaselineMatchKind = if (
            $referenceBaselineCatalogMatch) {
            $matchedReference.matchKind
        }
        else { '' }
        passiveContractClean = $passiveContractClean
        baselineStrictEquivalent = $baselineComparison.strictEquivalent
        baselineAuthoritativeExactEquivalent = `
            $baselineComparison.authoritativeExactEquivalent
        baselineSemanticExactEquivalent = `
            $baselineComparison.semanticExactEquivalent
        baselineHeroZVanillaVisualRandomEquivalent = `
            $baselineComparison.heroZVanillaVisualRandomEquivalent
        baselineHeroZVanillaVisualRandomExact = `
            $baselineComparison.heroZVanillaVisualRandomExact
        baselineHeroZVanillaVisualRandomPolicyId = `
            $baselineComparison.heroZVanillaVisualRandomPolicyId
        baselineHeroZCandidateCanonicalHex = `
            $baselineComparison.heroZCandidateCanonicalHex
        baselineHeroZReferenceCanonicalHex = `
            $baselineComparison.heroZReferenceCanonicalHex
        baselineHeroZMinimumCanonicalHex = `
            $baselineComparison.heroZMinimumCanonicalHex
        baselineHeroZMaximumCanonicalHex = `
            $baselineComparison.heroZMaximumCanonicalHex
        baselineRigidbodyComponentsObserved = `
            $baselineComparison.rigidbodyComponentsObserved
        baselineRigidbodyPositionDifferenceCount = `
            $baselineComparison.rigidbodyPositionDifferenceCount
        baselineRenderTransformEnvelopeEquivalent = `
            $baselineComparison.renderTransformEnvelopeEquivalent
        baselineEquivalent = $baselineComparison.equivalent
        baselineAcceptedRenderTransformDifferenceCount = `
            $baselineComparison.acceptedRenderTransformDifferenceCount
        baselineFirstDifferenceKey = `
            $baselineComparison.firstDifferenceKey
        baselineDifferenceCount = $baselineComparison.differenceCount
        phaseEquivalent = $phaseComparison.equivalent
        referencePhaseEvents = $phaseComparison.referenceEvents
        candidatePhaseEvents = $phaseComparison.candidateEvents
        comparedReferencePhaseEvents = `
            $phaseComparison.comparedReferenceEvents
        comparedCandidatePhaseEvents = `
            $phaseComparison.comparedCandidateEvents
        phaseFirstDifferenceIndex = $phaseComparison.firstDifferenceIndex
        referenceFrames = $traceComparison.referenceFrames
        candidateFrames = $traceComparison.candidateFrames
        inputEquivalent = $traceComparison.inputEquivalent
        bitwiseGameplayEquivalent = `
            $traceComparison.bitwiseGameplayEquivalent
        vanillaEnvelopeEquivalent = `
            $traceComparison.vanillaEnvelopeEquivalent
        baselineNormalizedRigidbodyEquivalent = `
            $traceComparison.baselineNormalizedRigidbodyEquivalent
        rigidbodyAxisWitnessesAvailable = `
            -not $allowBaselineNormalizedRigidbody `
            -or ($referenceBaselineCatalogMatch `
                -and [bool]$matchedReference.rigidbodyAxisWitnessesAvailable)
        rigidbodyAxisWitnessEquivalent = `
            $traceComparison.baselineNormalizedRigidbodyEquivalent
        rigidbodyXWitnessAttempt = if ($referenceBaselineCatalogMatch) {
            [int]$matchedReference.rigidbodyXWitnessAttempt
        }
        else { 0 }
        rigidbodyXWitnessTraceSha256 = if (
            $referenceBaselineCatalogMatch) {
            [string]$matchedReference.rigidbodyXWitnessTraceSha256
        }
        else { '' }
        rigidbodyYWitnessAttempt = if ($referenceBaselineCatalogMatch) {
            [int]$matchedReference.rigidbodyYWitnessAttempt
        }
        else { 0 }
        rigidbodyYWitnessTraceSha256 = if (
            $referenceBaselineCatalogMatch) {
            [string]$matchedReference.rigidbodyYWitnessTraceSha256
        }
        else { '' }
        gameplayEquivalent = $traceComparison.gameplayEquivalent
        firstDifferenceTick = $traceComparison.firstDifferenceTick
        firstDifferenceKey = $traceComparison.firstDifferenceKey
        firstUnacceptedDifferenceTick = `
            $traceComparison.firstUnacceptedDifferenceTick
        firstUnacceptedDifferenceKey = `
            $traceComparison.firstUnacceptedDifferenceKey
        acceptedNegativeControlDifferenceCount = `
            $traceComparison.acceptedDifferenceCount
        acceptedBaselineNormalizedRigidbodyDifferenceCount = `
            $traceComparison.acceptedBaselineNormalizedRigidbodyDifferenceCount
        negativeControlPolicyId = $negativeControlPolicy.policyId
        negativeControlEnvelopeSha256 = $negativeControlPolicy.sha256
    }
    $comparison |
        ConvertTo-Json -Depth 30 |
        Set-Content `
            -LiteralPath (Join-Path $EvidenceRoot 'comparison.json') `
            -Encoding utf8NoBOM
    [ordered]@{
        schemaVersion = 1
        verdict = if ($passiveContractClean `
                -and $scenarioCoverageClean `
                -and $referenceBaselineCatalogMatch `
                -and $comparison.baselineSemanticExactEquivalent `
                -and $comparison.baselineHeroZVanillaVisualRandomEquivalent `
                -and $comparison.baselineRigidbodyComponentsObserved `
                -and $comparison.baselineRenderTransformEnvelopeEquivalent `
                -and $comparison.baselineEquivalent `
                -and $comparison.phaseEquivalent `
                -and $comparison.inputEquivalent `
                -and $comparison.baselineNormalizedRigidbodyEquivalent `
                -and $comparison.rigidbodyAxisWitnessesAvailable `
                -and $comparison.rigidbodyAxisWitnessEquivalent `
                -and $comparison.gameplayEquivalent) {
            'PASS'
        }
        else { 'FAIL' }
        mode = $Mode
        referenceTrace = $ReferenceTracePath
        referenceTraceSha256 = (Get-FileHash `
            -LiteralPath $ReferenceTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        candidateTrace = $CompareOnlyCandidateTracePath
        candidateTraceSha256 = (Get-FileHash `
            -LiteralPath $CompareOnlyCandidateTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        passiveContractClean = $passiveContractClean
        scenarioCoverageClean = $scenarioCoverageClean
        scenarioId = if ($null -eq $scenarioContract) {
            ''
        }
        else {
            [string]$scenarioContract['scenarioId']
        }
        candidateScenarioCoverageVerdict = if (
            $null -eq $scenarioCoverageCandidate) {
            'NOT_REQUESTED'
        }
        else {
            [string]$scenarioCoverageCandidate.verdict
        }
        referenceScenarioCoverageVerdict = if (
            $null -eq $scenarioCoverageReference) {
            'NOT_REQUESTED'
        }
        else {
            [string]$scenarioCoverageReference.verdict
        }
        referenceBaselineCatalogMatch = $referenceBaselineCatalogMatch
        baselineStrictEquivalent = $comparison.baselineStrictEquivalent
        baselineAuthoritativeExactEquivalent = `
            $comparison.baselineAuthoritativeExactEquivalent
        baselineSemanticExactEquivalent = `
            $comparison.baselineSemanticExactEquivalent
        baselineHeroZVanillaVisualRandomEquivalent = `
            $comparison.baselineHeroZVanillaVisualRandomEquivalent
        baselineHeroZVanillaVisualRandomExact = `
            $comparison.baselineHeroZVanillaVisualRandomExact
        baselineHeroZVanillaVisualRandomPolicyId = `
            $comparison.baselineHeroZVanillaVisualRandomPolicyId
        baselineHeroZCandidateCanonicalHex = `
            $comparison.baselineHeroZCandidateCanonicalHex
        baselineHeroZReferenceCanonicalHex = `
            $comparison.baselineHeroZReferenceCanonicalHex
        baselineHeroZMinimumCanonicalHex = `
            $comparison.baselineHeroZMinimumCanonicalHex
        baselineHeroZMaximumCanonicalHex = `
            $comparison.baselineHeroZMaximumCanonicalHex
        baselineRigidbodyComponentsObserved = `
            $comparison.baselineRigidbodyComponentsObserved
        baselineRigidbodyPositionDifferenceCount = `
            $comparison.baselineRigidbodyPositionDifferenceCount
        baselineRenderTransformEnvelopeEquivalent = `
            $comparison.baselineRenderTransformEnvelopeEquivalent
        baselineEquivalent = $comparison.baselineEquivalent
        baselineAcceptedRenderTransformDifferenceCount = `
            $comparison.baselineAcceptedRenderTransformDifferenceCount
        phaseEquivalent = $comparison.phaseEquivalent
        inputEquivalent = $comparison.inputEquivalent
        bitwiseGameplayEquivalent = $comparison.bitwiseGameplayEquivalent
        vanillaEnvelopeEquivalent = $comparison.vanillaEnvelopeEquivalent
        baselineNormalizedRigidbodyEquivalent = `
            $comparison.baselineNormalizedRigidbodyEquivalent
        rigidbodyAxisWitnessesAvailable = `
            $comparison.rigidbodyAxisWitnessesAvailable
        rigidbodyAxisWitnessEquivalent = `
            $comparison.rigidbodyAxisWitnessEquivalent
        rigidbodyXWitnessAttempt = $comparison.rigidbodyXWitnessAttempt
        rigidbodyXWitnessTraceSha256 = `
            $comparison.rigidbodyXWitnessTraceSha256
        rigidbodyYWitnessAttempt = $comparison.rigidbodyYWitnessAttempt
        rigidbodyYWitnessTraceSha256 = `
            $comparison.rigidbodyYWitnessTraceSha256
        gameplayEquivalent = $comparison.gameplayEquivalent
        firstDifferenceTick = $comparison.firstDifferenceTick
        firstDifferenceKey = $comparison.firstDifferenceKey
        firstUnacceptedDifferenceTick = `
            $comparison.firstUnacceptedDifferenceTick
        firstUnacceptedDifferenceKey = `
            $comparison.firstUnacceptedDifferenceKey
        acceptedNegativeControlDifferenceCount = `
            $comparison.acceptedNegativeControlDifferenceCount
        acceptedBaselineNormalizedRigidbodyDifferenceCount = `
            $comparison.acceptedBaselineNormalizedRigidbodyDifferenceCount
        negativeControlPolicyId = $negativeControlPolicy.policyId
        negativeControlEnvelopeSha256 = $negativeControlPolicy.sha256
        visualRecognitionUsed = $false
    } |
        ConvertTo-Json -Depth 30 |
        Set-Content `
            -LiteralPath (Join-Path $EvidenceRoot 'final-verdict.json') `
            -Encoding utf8NoBOM
    Write-Output "T24 compare-only completed: $EvidenceRoot"
    return
}

$modsTopLevelFingerprintBefore = Get-T24TopLevelFingerprint `
    -Root $modsDirectory
$tasInstallFingerprintBefore = Get-T24DirectoryFingerprint `
    -Root $tasInstall
$observerInstallOriginallyExisted = Test-Path `
    -LiteralPath $observerInstall `
    -PathType Container
$observerInstallFingerprintBefore = if (
    $observerInstallOriginallyExisted) {
    Get-T24DirectoryFingerprint -Root $observerInstall
}
else { '' }

try {
    foreach ($file in Get-ChildItem `
            -LiteralPath $PersistentDataDirectory `
            -File `
            -Force |
        Where-Object { $_.Name -match $slotPattern }) {
        $slotFilesBefore[$file.Name] = [IO.File]::ReadAllBytes($file.FullName)
    }
    $slotNamesBefore = @($slotFilesBefore.Keys)
    $slotCaptured = $true

    foreach ($unexpected in @(
            $modsBackup,
            $modsIsolated,
            $automationBackup
        )) {
        if (Test-Path -LiteralPath $unexpected) {
            throw "Unexpected recovery path exists: $unexpected"
        }
    }

    Move-Item -LiteralPath $modsDirectory -Destination $modsBackup
    $modsMoved = $true
    New-Item -ItemType Directory -Path $modsDirectory | Out-Null
    Move-Item `
        -LiteralPath (Join-Path $modsBackup 'HollowKnightTAS') `
        -Destination $tasInstall
    New-Item -ItemType Directory -Path $observerInstall | Out-Null
    foreach ($file in $observerFiles) {
        Copy-Item `
            -LiteralPath (Join-Path $observerBuild $file) `
            -Destination (Join-Path $observerInstall $file)
    }
    $isolatedEntries = @(Get-ChildItem -LiteralPath $modsDirectory -Force)
    if ($isolatedEntries.Count -ne 2 `
            -or $isolatedEntries.Name -notcontains 'HollowKnightTAS' `
            -or $isolatedEntries.Name `
                -notcontains 'HollowKnightTAS.ReferenceObserver') {
        throw 'Failed to establish the isolated T24 candidate Mod profile.'
    }

    if ($automationOriginallyExisted) {
        Move-Item `
            -LiteralPath $automationRoot `
            -Destination $automationBackup
        $automationMoved = $true
    }
    $automationIsolationEstablished = $true
    Write-TestSettings

    Get-ChildItem `
            -LiteralPath $modsDirectory `
            -File `
            -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                file = [IO.Path]::GetRelativePath(
                    $modsDirectory,
                    $_.FullName)
                length = $_.Length
                sha256 = (Get-FileHash `
                    -LiteralPath $_.FullName `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        } |
        ConvertTo-Json -Depth 10 |
        Set-Content `
            -LiteralPath (
                Join-Path $environmentDirectory 'assemblies.json') `
            -Encoding utf8NoBOM

    $beforeHelpers = @(
        Get-Process `
            -Name `
                HollowKnightTAS.Companion,
                HollowKnightTAS.AgentBridge,
                HollowKnightTAS.NativeHost `
            -ErrorAction SilentlyContinue |
            ForEach-Object Id
    )
    $script:beforeHelperPids = @($beforeHelpers)
    $arguments = @(
        '-screen-width',
        '800',
        '-screen-height',
        '450',
        '-screen-fullscreen',
        '0',
        "--hktas-reference-run=$runId",
        "--hktas-reference-mode=$Mode",
        "--hktas-reference-output-base64=$encodedOutput",
        "--hktas-reference-max-ticks=$MaxTicks",
        "--hktas-t24-deferred-recording-arm=$runId"
    )
    $startupLaunch = Start-T24StartupClockedGame `
        -GameExecutable $GameExecutable `
        -GameArguments $arguments `
        -BundleRoot $ClockBundleRoot `
        -EvidenceDirectory $environmentDirectory
    $script:game = $startupLaunch.GameProcess
    $clockAudit = $startupLaunch.Audit
    $candidateClockContractSha256 = Get-T24ClockContractSha256 `
        -ClockAudit $clockAudit
    if ($candidateClockContractSha256 `
            -ne $negativeControlPolicy.clockContractSha256) {
        throw 'Candidate startup clock contract differs from the negative control.'
    }

    $launchDeadline =
        [DateTimeOffset]::UtcNow.AddSeconds($MaxLaunchSeconds)
    $menuReadinessConfirmed = $false
    $menuReadyStatus = $null
    do {
        $status = Get-ObserverStatus
        $script:ownedHelperPids = @(
            Get-Process `
                -Name `
                    HollowKnightTAS.Companion,
                    HollowKnightTAS.AgentBridge,
                    HollowKnightTAS.NativeHost `
                -ErrorAction SilentlyContinue |
                Where-Object {
                    $script:beforeHelperPids -notcontains $_.Id
                } |
                ForEach-Object Id
        )
        if ($null -ne $script:game `
                -and $null -ne $status `
                -and [string]$status.scene -eq 'Menu_Title' `
                -and (Test-ObserverMenuControlsReady -Status $status)) {
            $menuReadinessConfirmed = $true
            $menuReadyStatus = $status
            $menuReadyAtUtc = [DateTimeOffset]::UtcNow
            $bootstrapPresentAtMenu = Test-Path `
                -LiteralPath $bootstrapPath `
                -PathType Leaf
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $launchDeadline)

    if ($null -eq $script:game) {
        throw 'Hollow Knight did not start.'
    }
    if (-not $menuReadinessConfirmed) {
        [ordered]@{
            schemaVersion = 1
            maxLaunchSeconds = $MaxLaunchSeconds
            gameProcessFound = $null -ne $script:game
            bootstrapObserved = Test-Path `
                -LiteralPath $bootstrapPath `
                -PathType Leaf
            lastObserverStatus = $status
        } |
            ConvertTo-Json -Depth 20 |
            Set-Content `
                -LiteralPath (
                    Join-Path `
                        $environmentDirectory `
                        'menu-readiness-failure.json') `
                -Encoding utf8NoBOM
        throw 'T24 observer did not reach Menu_Title readiness.'
    }
    $status = $menuReadyStatus

    # The clock was loaded before the first instruction of the direct game
    # process. Match the frozen Reference menu dwell and load path before any
    # control lease or replay preparation.
    [void](Wait-T24ClockProfile `
        -GetStatus { Get-ObserverStatus } `
        -TimeoutSeconds 15)
    Start-Sleep -Seconds 3
    [void](Wait-ObserverMenuControlsStatus -TimeoutSeconds 5)
    Invoke-ExternalUiSlotLoad
    $fixtureReadyStatus = Wait-ObserverPhase `
        -Allowed @(
            'waiting-for-recording-time-target',
            'waiting-for-recording-arm-release') `
        -TimeoutSeconds $FixtureReadyTimeoutSeconds
    $fixtureReadyAtUtc = [DateTimeOffset]::UtcNow
    if (-not [bool]$fixtureReadyStatus.externalRngSynchronized `
            -or -not [bool]$fixtureReadyStatus.externalRngSynchronizationOriginCaptured) {
        throw 'Startup clock payload did not synchronize the frozen RNG origin.'
    }
    $script:rngSynchronizationArmed = $true
    $preArmClockStatus = Wait-T24ClockProfile `
        -GetStatus { Get-ObserverStatus } `
        -TimeoutSeconds 15
    $preArmClockStatus |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (
                Join-Path $environmentDirectory 'clock-profile-pre-arm.json') `
            -Encoding utf8NoBOM

    $candidateBaselinePath = Join-Path $traceDirectory 'baseline.json'
    if (-not (Test-Path `
            -LiteralPath $candidateBaselinePath `
            -PathType Leaf)) {
        throw 'Candidate fixture baseline was not written before control.'
    }
    $matchedReference = Resolve-T24ReferenceForBaseline `
        -EnvelopePolicy $negativeControlPolicy `
        -CandidateBaselinePath $candidateBaselinePath
    $referenceBaselineCatalogMatch = $null -ne $matchedReference
    if (-not $referenceBaselineCatalogMatch) {
        [ordered]@{
            schemaVersion = 1
            verdict = 'NO_EXACT_REFERENCE_BASELINE'
            candidateBaseline = $candidateBaselinePath
            candidateBaselineSha256 = (Get-FileHash `
                -LiteralPath $candidateBaselinePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            mainReferenceCount =
                @($negativeControlPolicy.referenceCatalog).Count
            supplementalReferenceCount =
                $supplementalReferences.Count
            gameplayStarted = $false
            timeWritten = $false
            gameplayStateWritten = $false
        } |
            ConvertTo-Json -Depth 10 |
            Set-Content `
                -LiteralPath (
                    Join-Path `
                        $environmentDirectory `
                        'baseline-catalog-miss.json') `
                -Encoding utf8NoBOM
        throw 'Candidate fixture baseline has no exact no-Mod catalog match.'
    }
    $ReferenceTracePath = $matchedReference.tracePath
    $ReferenceBaselinePath = $matchedReference.baselinePath
    $ReferencePhasePath = $matchedReference.phasePath

    $bootstrapWaitStartedUtc = [DateTimeOffset]::UtcNow
    $bootstrapDeadline =
        [DateTimeOffset]::UtcNow.AddSeconds($MaxLaunchSeconds)
    do {
        $script:ownedHelperPids = @(
            Get-Process `
                -Name `
                    HollowKnightTAS.Companion,
                    HollowKnightTAS.AgentBridge,
                    HollowKnightTAS.NativeHost `
                -ErrorAction SilentlyContinue |
                Where-Object {
                    $script:beforeHelperPids -notcontains $_.Id
                } |
                ForEach-Object Id
        )
        if (Test-Path -LiteralPath $bootstrapPath -PathType Leaf) {
            $bootstrapObservedAtUtc = [DateTimeOffset]::UtcNow
            break
        }
        if ($script:game.HasExited) {
            throw 'Hollow Knight exited while waiting for Companion bootstrap.'
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $bootstrapDeadline)
    if ($null -eq $bootstrapObservedAtUtc) {
        [ordered]@{
            schemaVersion = 1
            policyId = 'fixture-baseline-before-sdk-connect-v1'
            menuReadyAtUtc = $menuReadyAtUtc.ToString('O')
            fixtureReadyAtUtc = $fixtureReadyAtUtc.ToString('O')
            bootstrapPresentAtMenu = $bootstrapPresentAtMenu
            bootstrapObserved = $false
            fixtureReadyStatus = $fixtureReadyStatus
            gameplayStateWritten = $false
        } |
            ConvertTo-Json -Depth 30 |
            Set-Content `
                -LiteralPath $startupOrderAuditPath `
                -Encoding utf8NoBOM
        throw 'Companion bootstrap did not appear after fixture baseline capture.'
    }

    $toolsRoot = Join-Path $tasInstall 'Companion\win-x64\Tools'
    $sdkCorePath = Join-Path `
        $toolsRoot `
        'SDK\HollowKnightTAS.Core.dll'
    $sdkClientPath = Join-Path `
        $toolsRoot `
        'SDK\HollowKnightTAS.Automation.Client.dll'
    Add-Type -Path $sdkCorePath
    Add-Type -Path $sdkClientPath
    $client = [HollowKnightTAS.Automation.Client.AutomationClient]::new()
    $connectOptions =
        [HollowKnightTAS.Automation.Client.AutomationConnectOptions]::new()
    $connectOptions.ClientId = 't24-candidate-sdk'
    $connectOptions.BootstrapPath = $bootstrapPath
    $connectOptions.Timeout = [TimeSpan]::FromSeconds(15)
    $sdkConnectStartedAtUtc = [DateTimeOffset]::UtcNow
    [void]$client.ConnectAsync(
            $connectOptions,
            [Threading.CancellationToken]::None
        ).GetAwaiter().GetResult()
    $sdkConnectedAtUtc = [DateTimeOffset]::UtcNow

    $startupOrderVerified =
        $null -ne $menuReadyAtUtc `
        -and $null -ne $fixtureReadyAtUtc `
        -and $null -ne $bootstrapWaitStartedUtc `
        -and $null -ne $bootstrapObservedAtUtc `
        -and $null -ne $sdkConnectStartedAtUtc `
        -and $null -ne $sdkConnectedAtUtc `
        -and $menuReadyAtUtc -le $fixtureReadyAtUtc `
        -and $fixtureReadyAtUtc -le $bootstrapWaitStartedUtc `
        -and $bootstrapWaitStartedUtc -le $bootstrapObservedAtUtc `
        -and $bootstrapObservedAtUtc -le $sdkConnectStartedAtUtc `
        -and $sdkConnectStartedAtUtc -le $sdkConnectedAtUtc
    if (-not $startupOrderVerified) {
        throw 'Candidate startup order violated the fixture-before-SDK policy.'
    }

    [ordered]@{
        schemaVersion = 1
        policyId = 'fixture-baseline-before-sdk-connect-v1'
        menuReadinessRequiresBootstrap = $false
        menuReadyAtUtc = $menuReadyAtUtc.ToString('O')
        fixtureReadyAtUtc = $fixtureReadyAtUtc.ToString('O')
        bootstrapPresentAtMenu = $bootstrapPresentAtMenu
        bootstrapWaitStartedUtc = $bootstrapWaitStartedUtc.ToString('O')
        bootstrapObservedAtUtc = $bootstrapObservedAtUtc.ToString('O')
        sdkConnectStartedAtUtc = $sdkConnectStartedAtUtc.ToString('O')
        sdkConnectedAtUtc = $sdkConnectedAtUtc.ToString('O')
        startupOrderVerified = $startupOrderVerified
        menuReadyBeforeFixture =
            $menuReadyAtUtc -le $fixtureReadyAtUtc
        fixtureReadyBeforeBootstrapWait =
            $fixtureReadyAtUtc -le $bootstrapWaitStartedUtc
        fixtureReadyBeforeBootstrapObservation =
            $fixtureReadyAtUtc -le $bootstrapObservedAtUtc
        fixtureReadyBeforeSdkConnection =
            $fixtureReadyAtUtc -lt $sdkConnectedAtUtc
        fixtureReadyStatus = $fixtureReadyStatus
        inputInjected = $false
        timeWritten = $false
        gameplayStateWritten = $false
        visualRecognitionUsed = $false
    } |
        ConvertTo-Json -Depth 30 |
        Set-Content `
            -LiteralPath $startupOrderAuditPath `
            -Encoding utf8NoBOM

    $pause = $null
    $validate = $null
    $proposal = $null
    $apply = $null
    $start = $null
    $resume = $null
    $uiWindow = $null
    $manualUiPause = $null
    $recordingArmEvidence = $null
    if ($Mode -eq 'tas-manual-ui') {
        $uiWindow = Wait-T24CompanionWindow `
            -OwnedProcessIds ([int[]]$script:ownedHelperPids) `
            -TimeoutSeconds $MaxLaunchSeconds
        $manualUiPause = Invoke-T24ManualUiPause `
            -Root $uiWindow.Root `
            -DeferredRecordingArm `
            -GetState { Get-SdkState } `
            -WaitState {
                param($runtimeMode, $minimumMovieTick)
                if ($null -eq $minimumMovieTick) {
                    return Wait-SdkState `
                        -RuntimeMode $runtimeMode `
                        -TimeoutSeconds 30
                }
                return Wait-SdkState `
                    -RuntimeMode $runtimeMode `
                    -MinimumMovieTick ([long]$minimumMovieTick) `
                    -TimeoutSeconds 30
            }
    }
    else {
        $leaseId = Acquire-SdkLease `
            -Client $client `
            -Scopes @(
                'movie.apply-branch',
                'control.playback',
                'control.step'
            )
        $pause = Invoke-SdkCommand `
            -Client $client `
            -CommandId 'pause' `
            -Scope 'control.playback' `
            -LeaseId $leaseId `
            -ExpectedRuntimeMode 'Running'
    }
    $before = Get-SdkState
    $pauseArmChecks = [ordered]@{
        runtimeRunning =
            [string]$before.State.RuntimeMode -eq 'Running'
        gateEnabled = [string]$before.State.Fields[
            'deferredRecordingArmEnabled'] -eq 'true'
        runIdExact = [string]$before.State.Fields[
            'deferredRecordingArmRunId'] -eq $runId
        pauseArmed = [string]$before.State.Fields[
            'deferredRecordingArmPauseArmed'] -eq 'true'
        replayNotArmed = [string]$before.State.Fields[
            'deferredRecordingArmReplayArmed'] -eq 'false'
        releaseNotConsumed = [string]$before.State.Fields[
            'deferredRecordingArmReleaseConsumed'] -eq 'false'
        neutralPreRollNotStarted = [int]$before.State.Fields[
            'deferredRecordingArmNeutralPreRollCompletedFrameCount'] -eq 0
    }
    [ordered]@{
        schemaVersion = 1
        expectedRunId = $runId
        pauseCommand = if ($null -eq $pause) {
            $null
        }
        else { Convert-AutomationResult $pause }
        checks = $pauseArmChecks
        semanticState = $before.ToFriendlyJson() |
            ConvertFrom-Json -Depth 50
        gameplayStateWritten = $false
        visualRecognitionUsed = $false
    } |
        ConvertTo-Json -Depth 50 |
        Set-Content `
            -LiteralPath $pauseArmDiagnosticPath `
            -Encoding utf8NoBOM
    if (@($pauseArmChecks.Values | Where-Object { -not $_ }).Count -ne 0) {
        throw 'Candidate did not arm deferred pause while remaining Running.'
    }

    $capabilities = Invoke-SdkCommand `
        -Client $client `
        -CommandId 'getCapabilities' `
        -Scope 'observe.status'
    Convert-AutomationResult $capabilities |
        ConvertTo-Json -Depth 50 |
        Set-Content `
            -LiteralPath (
                Join-Path $environmentDirectory 'capabilities.json') `
            -Encoding utf8NoBOM
    $capabilityCatalog = @(
        [string]$capabilities.Data['catalogJson'] |
            ConvertFrom-Json -Depth 30
    )
    $debugCapabilities = @(
        $capabilityCatalog |
            Where-Object {
                $_.commandId -in @(
                    'setHeroPose',
                    'setPlayerResources')
            }
    )
    $debugCapabilitiesDisabled = $debugCapabilities.Count -eq 2 `
        -and @(
            $debugCapabilities |
                Where-Object { $_.availability -ne 'disabled' }
        ).Count -eq 0
    $manifestSha256 = [string]$capabilities.ManifestSha256
    $source = Get-Content -LiteralPath $MoviePath -Raw
    $manifestMatches = [regex]::Matches(
        $source,
        '(?m)^manifest-sha256 [0-9a-f]{64}$')
    if ($manifestMatches.Count -ne 1) {
        throw 'Candidate movie must contain one canonical manifest header.'
    }
    $source = [regex]::Replace(
        $source,
        '(?m)^manifest-sha256 [0-9a-f]{64}$',
        "manifest-sha256 $manifestSha256")
    $candidateBytes = [Text.UTF8Encoding]::new($false, $true).
        GetBytes($source.Replace("`r`n", "`n"))
    [IO.File]::WriteAllBytes($candidateSourcePath, $candidateBytes)
    $candidateBase64 = [Convert]::ToBase64String($candidateBytes)

    if ($Mode -eq 'tas-manual-ui') {
        $manualUiControl = Invoke-T24ManualUiControl `
            -Root $uiWindow.Root `
            -PauseEvidence $manualUiPause `
            -MovieText ([Text.UTF8Encoding]::new($false, $true).
                GetString($candidateBytes)) `
            -ExpectedTicks $MaxTicks `
            -GetState { Get-SdkState } `
            -WaitState {
                param($runtimeMode, $minimumMovieTick)
                if ($null -eq $minimumMovieTick) {
                    return Wait-SdkState `
                        -RuntimeMode $runtimeMode `
                        -TimeoutSeconds 30
                }
                return Wait-SdkState `
                    -RuntimeMode $runtimeMode `
                    -MinimumMovieTick ([long]$minimumMovieTick) `
                    -TimeoutSeconds 30
            } `
            -ArmExternalRng {
                if (-not $script:rngSynchronizationArmed) {
                    throw 'Frozen external RNG origin was not synchronized.'
                }
            } `
            -ReleaseRecordingArm {
                $script:recordingArmEvidence =
                    Complete-T24DeferredRecordingArm
                return $script:recordingArmEvidence
            }
        $manualUiControl |
            ConvertTo-Json -Depth 20 |
            Set-Content `
                -LiteralPath (
                    Join-Path `
                        $environmentDirectory `
                        'manual-ui-control.json') `
                -Encoding utf8NoBOM
    }
    else {
        $validate = Invoke-SdkCommand `
            -Client $client `
            -CommandId 'validateMoviePatch' `
            -Scope 'movie.validate' `
            -Arguments @{ candidateMovieBase64 = $candidateBase64 }
        if ([long]$validate.Data['expandedTicks'] -ne $MaxTicks) {
            throw (
                'Candidate expanded tick count differs from MaxTicks: ' `
                + $validate.Data['expandedTicks'])
        }
        $proposal = Invoke-SdkCommand `
            -Client $client `
            -CommandId 'proposeMoviePatch' `
            -Scope 'movie.propose' `
            -Arguments @{
                baseMovieId = 'none'
                candidateMovieBase64 = $candidateBase64
                reason = 'T24 vanilla-equivalence candidate'
                expectedMilestone = 'regression-end'
            }
        $branchMovieId = [string]$proposal.Data['branchMovieId']

        $apply = Invoke-SdkCommand `
            -Client $client `
            -CommandId 'applyMovieBranch' `
            -Scope 'movie.apply-branch' `
            -Arguments @{ branchMovieId = $branchMovieId } `
            -LeaseId $leaseId `
            -ExpectedRuntimeMode $before.State.RuntimeMode

        $start = Invoke-SdkCommand `
            -Client $client `
            -CommandId 'startReplay' `
            -Scope 'control.playback' `
            -LeaseId $leaseId `
            -ExpectedRuntimeMode 'Running'
        $recordingArmEvidence = Complete-T24DeferredRecordingArm
        if ($Mode -eq 'tas-continuous') {
            $resume = Invoke-SdkCommand `
                -Client $client `
                -CommandId 'resume' `
                -Scope 'control.playback' `
                -LeaseId $leaseId `
                -ExpectedRuntimeMode 'Paused'
            [void](Wait-SdkState -RuntimeMode 'Running')
        }

        if ($Mode -eq 'tas-sequential') {
            $progressState = Get-SdkState
            for ($index = 0; $index -lt $MaxTicks; $index++) {
                $expectedTick = [long]$progressState.State.MovieTick
                if ([DateTimeOffset]::UtcNow.AddSeconds(60) `
                        -ge $script:leaseExpiresAtUtc) {
                    $oldExpiry = $script:leaseExpiresAtUtc
                    $renewalStartedUtc = [DateTimeOffset]::UtcNow
                    $renewal = Renew-SdkLease `
                        -Client $client `
                        -Scope 'control.step' `
                        -LeaseId $leaseId `
                        -TtlSeconds 300
                    $afterRenewalState = Get-SdkState
                    $sameLease = [string]$renewal.Data['leaseId'] `
                        -eq $leaseId
                    $renewalStateStable =
                        [string]$progressState.State.RuntimeMode -eq 'Paused' `
                        -and [string]$afterRenewalState.State.RuntimeMode `
                            -eq 'Paused' `
                        -and [long]$afterRenewalState.State.MovieTick `
                            -eq $expectedTick
                    $renewalValid = $sameLease `
                        -and $renewalStateStable `
                        -and $script:leaseExpiresAtUtc -gt $oldExpiry `
                        -and $script:leaseExpiresAtUtc -gt $renewalStartedUtc
                    $leaseRenewalEntries.Add([pscustomobject][ordered]@{
                        beforeLogicalStep = $index
                        requestedTtlSeconds = 300
                        startedUtc = $renewalStartedUtc.ToString('O')
                        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                        oldExpiresAtUtc = $oldExpiry.ToString('O')
                        newExpiresAtUtc =
                            $script:leaseExpiresAtUtc.ToString('O')
                        leaseId = $leaseId
                        returnedLeaseId =
                            [string]$renewal.Data['leaseId']
                        sameLease = $sameLease
                        resultCode = [string]$renewal.ResultCode
                        runtimeModeBefore =
                            [string]$progressState.State.RuntimeMode
                        runtimeModeAfter =
                            [string]$afterRenewalState.State.RuntimeMode
                        movieTickBefore = $expectedTick
                        movieTickAfter =
                            [long]$afterRenewalState.State.MovieTick
                        stateStable = $renewalStateStable
                        valid = $renewalValid
                    })
                    if (-not $renewalValid) {
                        throw (
                            'Control lease renewal changed ownership, state, ' `
                            + "or movie tick before sequential step $index.")
                    }
                    $progressState = $afterRenewalState
                }
                $dwellStartedUtc = [DateTimeOffset]::UtcNow
                $dwellStopwatch = [Diagnostics.Stopwatch]::StartNew()
                if ($PauseDwellMilliseconds -gt 0) {
                    Start-Sleep -Milliseconds $PauseDwellMilliseconds
                    while ($dwellStopwatch.Elapsed.TotalMilliseconds `
                            -lt $PauseDwellMilliseconds) {
                        Start-Sleep -Milliseconds 1
                    }
                }
                $dwellStopwatch.Stop()
                $afterDwellState = Get-SdkState
                $dwellStateStable =
                    [string]$progressState.State.RuntimeMode -eq 'Paused' `
                    -and [string]$afterDwellState.State.RuntimeMode -eq 'Paused' `
                    -and [long]$afterDwellState.State.MovieTick `
                        -eq $expectedTick
                $pauseDwellEntries.Add([pscustomobject][ordered]@{
                    logicalStep = $index
                    requestedMilliseconds = $PauseDwellMilliseconds
                    actualElapsedMilliseconds =
                        [double]$dwellStopwatch.Elapsed.TotalMilliseconds
                    startedUtc = $dwellStartedUtc.ToString('O')
                    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                    runtimeModeBefore =
                        [string]$progressState.State.RuntimeMode
                    runtimeModeAfter =
                        [string]$afterDwellState.State.RuntimeMode
                    movieTickBefore = $expectedTick
                    movieTickAfter =
                        [long]$afterDwellState.State.MovieTick
                    stateStable = $dwellStateStable
                })
                if (-not $dwellStateStable) {
                    throw (
                        'Runtime advanced or left Paused during external dwell ' `
                        + "at sequential step $index.")
                }
                [void](Invoke-SdkCommand `
                    -Client $client `
                    -CommandId 'step' `
                    -Scope 'control.step' `
                    -Arguments @{ count = '1' } `
                    -LeaseId $leaseId `
                    -ExpectedRuntimeMode 'Paused')
                $progressState = Wait-SdkState `
                    -RuntimeMode 'Paused' `
                    -MinimumMovieTick ($expectedTick + 1)
                if ($progressState.State.MovieTick -ne $expectedTick + 1) {
                    [IO.File]::WriteAllText(
                        (Join-Path `
                            $environmentDirectory `
                            'sequential-failure-state.json'),
                        $progressState.ToFriendlyJson(),
                        [Text.UTF8Encoding]::new($false))
                    $failureFields = $progressState.State.Fields
                    throw (
                        'Sequential step advanced by more than one movie tick: ' `
                        + 'before=' `
                        + $expectedTick `
                        + '; after=' `
                        + $progressState.State.MovieTick `
                        + '; playbackMode=' `
                        + [string]$failureFields['playbackMode'] `
                        + '; mismatchCount=' `
                        + [string]$failureFields['replayMismatchCount'] `
                        + '; firstTick=' `
                        + [string]$failureFields[
                            'firstReplayMismatchMovieTick'] `
                        + '; firstExpected=' `
                        + [string]$failureFields[
                            'firstReplayMismatchExpected'] `
                        + '; firstActual=' `
                        + [string]$failureFields[
                            'firstReplayMismatchActual'] `
                        + '; lastExpected=' `
                        + [string]$failureFields['lastReplayMismatchExpected'] `
                        + '; lastActual=' `
                        + [string]$failureFields['lastReplayMismatchActual'])
                }
            }
            $dwellDurations = @(
                $pauseDwellEntries |
                    ForEach-Object {
                        [double]$_.actualElapsedMilliseconds
                    })
            $dwellEntriesClean = $pauseDwellEntries.Count -eq $MaxTicks `
                -and @(
                    $pauseDwellEntries |
                        Where-Object {
                            -not [bool]$_.stateStable `
                                -or [double]$_.actualElapsedMilliseconds `
                                    -lt $PauseDwellMilliseconds
                        }).Count -eq 0
            $renewalsClean = @(
                $leaseRenewalEntries |
                    Where-Object {
                        -not [bool]$_.valid `
                            -or -not [bool]$_.sameLease `
                            -or -not [bool]$_.stateStable
                    }).Count -eq 0
            $minimumRenewalCount = if ($PauseDwellMilliseconds -ge 5000) {
                2
            }
            else { 0 }
            $pauseDwellClean = $dwellEntriesClean `
                -and $renewalsClean `
                -and $leaseRenewalEntries.Count -ge $minimumRenewalCount
            $durationMeasure = $dwellDurations |
                Measure-Object -Minimum -Maximum -Sum
            [ordered]@{
                schemaVersion = 1
                policyId =
                    'external-wall-clock-dwell-with-lease-renewal-before-each-sequential-step-v2'
                mode = $Mode
                requestedMillisecondsPerStep = $PauseDwellMilliseconds
                expectedStepCount = $MaxTicks
                recordedStepCount = $pauseDwellEntries.Count
                totalRequestedMilliseconds =
                    [long]$PauseDwellMilliseconds * [long]$MaxTicks
                totalActualMilliseconds = [double]$durationMeasure.Sum
                minimumActualMilliseconds = [double]$durationMeasure.Minimum
                maximumActualMilliseconds = [double]$durationMeasure.Maximum
                everyDwellSatisfied = $dwellEntriesClean
                everyStateStable = $dwellEntriesClean -and $renewalsClean
                clock = 'System.Diagnostics.Stopwatch'
                leaseTtlSeconds = 300
                renewalThresholdSeconds = 60
                initialLeaseExpiresAtUtc =
                    $script:initialLeaseExpiresAtUtc.ToString('O')
                minimumRequiredRenewalCount = $minimumRenewalCount
                renewalCount = $leaseRenewalEntries.Count
                everyRenewalValid = $renewalsClean
                inputWritten = $false
                timeWritten = $false
                gameplayStateWritten = $false
                entries = @($pauseDwellEntries)
                renewals = @($leaseRenewalEntries)
            } |
                ConvertTo-Json -Depth 10 |
                Set-Content `
                    -LiteralPath $pauseDwellAuditPath `
                    -Encoding utf8NoBOM
            $pauseDwellAuditSha256 = (Get-FileHash `
                -LiteralPath $pauseDwellAuditPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $pauseDwellClean) {
                throw 'Sequential pause-dwell audit failed.'
            }
        }
        elseif ($Mode -eq 'tas-batch') {
            $progressState = Get-SdkState
            [void](Invoke-SdkCommand `
                -Client $client `
                -CommandId 'step' `
                -Scope 'control.step' `
                -Arguments @{ count = [string]$MaxTicks } `
                -LeaseId $leaseId `
                -ExpectedRuntimeMode 'Paused')
        }
    }

    $terminal = Wait-ObserverPhase `
        -Allowed @('complete', 'failed') `
        -TimeoutSeconds $RunTimeoutSeconds
    if ([string]$terminal.phase -ne 'complete') {
        throw "Reference observer failed: $($terminal.error)"
    }

    $resultPath = Join-Path $traceDirectory 'result.json'
    $candidateTracePath = Join-Path $traceDirectory 'trace.jsonl'
    $candidateBaselinePath = Join-Path $traceDirectory 'baseline.json'
    $candidatePhasePath = Join-Path $traceDirectory 'input-phase.jsonl'
    $observerResult = Get-Content -LiteralPath $resultPath -Raw |
        ConvertFrom-Json
    $candidateRngBoundaryAudit =
        Assert-T24ExternalRngBoundaryTelemetry `
            -Telemetry $observerResult `
            -TracePath $candidateTracePath
    if (-not $observerResult.success `
            -or [string]$observerResult.runId -cne $runId `
            -or [string]$observerResult.mode -cne $Mode `
            -or $observerResult.frameCount -ne $MaxTicks `
            -or [string]$observerResult.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1' `
            -or $observerResult.inputInjected `
            -or $observerResult.timeWritten `
            -or $observerResult.gameplayStateWritten `
            -or $observerResult.saveLoadedByObserver `
            -or -not $observerResult.baselineCaptured `
            -or -not [bool]$observerResult.externalRngSynchronized `
            -or -not [bool]$observerResult.externalRngSynchronizationOriginCaptured `
            -or [string]$observerResult.externalRngSynchronizationPolicy `
                -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
            -or [int]$observerResult.externalRngSeed -ne 1212896321 `
            -or [uint32]$observerResult.externalClockBridgeAbi -ne 10 `
            -or [int]$observerResult.externalClockBridgeStatus -ne 2 `
            -or -not [bool]$observerResult.externalRuntimeVirtualClockRegistered `
            -or [int]$observerResult.externalVirtualClockPaused -ne 0 `
            -or [int]$observerResult.externalVirtualClockResumePending -ne 0 `
            -or [int]$observerResult.externalVirtualClockPauseCount -le 0 `
            -or [int]$observerResult.externalVirtualClockPauseCount `
                -ne [int]$observerResult.externalVirtualClockResumeRequestCount `
            -or [int]$observerResult.externalVirtualClockPauseCount `
                -ne [int]$observerResult.externalVirtualClockResumeCount `
            -or -not [bool]$observerResult.externalDeterministicClockEnabled `
            -or [long]$observerResult.externalDeterministicClockFrequency -le 0 `
            -or [long]$observerResult.externalDeterministicClockStepTicks -le 0 `
            -or [long]$observerResult.externalDeterministicClockFrequency `
                / [long]$observerResult.externalDeterministicClockStepTicks -ne 50 `
            -or [long]$observerResult.externalDeterministicClockAnchor -le 0 `
            -or [int]$observerResult.externalDeterministicClockFrameAdvanceCount `
                -le [int]$observerResult.externalVirtualClockResumeCount `
            -or [int]$observerResult.externalDeterministicClockEnableFaultCode -ne 0 `
            -or [int]$observerResult.externalDeterministicClockAdvanceFaultCode -ne 0 `
            -or [string]$observerResult.externalRealtimeEpochNormalizationPolicyId `
                -ne 'root-game-minus-rounded-startup-offset-qpc-grid-v3' `
            -or -not [bool]$observerResult.externalRealtimeEpochNormalizationApplied `
            -or [int]$observerResult.externalRealtimeEpochNormalizationCount -le 0 `
            -or [string]$observerResult.externalRealtimeEpochNormalizationAfterBits `
                -ne [string]$observerResult.externalRealtimeEpochNormalizationTargetBits `
            -or [int]$observerResult.externalRealtimeEpochNormalizationFaultCode -ne 0 `
            -or -not [bool]$observerResult.externalDoublePhaseCalibrationApplied `
            -or [int]$observerResult.externalDoublePhaseCalibrationAttempts -le 0 `
            -or [int]$observerResult.externalDoublePhaseCalibrationFaultCode -ne 0 `
            -or [string]$observerResult.externalDoublePhaseFinalResidualBits `
                -ne '0000000000000000' `
            -or [string]$observerResult.externalDoublePhaseFinalResidual.canonicalHex `
                -ne '0000000000000000' `
            -or [string]$observerResult.externalDoublePhaseLastCorrectionBits `
                -eq '00000000' `
            -or -not [bool]$observerResult.externalStartupClockHookInstalled `
            -or -not [bool]$observerResult.externalStartupClockLatchEnabled `
            -or [uint32]$observerResult.externalStartupClockHookThreadId -eq 0 `
            -or [uint32]$observerResult.externalStartupClockHookThreadId `
                -ne [uint32]$clockAudit.primaryThreadId `
            -or [int]$observerResult.externalStartupClockVirtualQpcCallCount -le 0 `
            -or [int]$observerResult.externalStartupClockHandoffAdoptCount -ne 1 `
            -or [int]$observerResult.externalStartupClockFaultCode -ne 0 `
            -or [string]$clockAudit.startupPolicy `
                -ne 'create-suspended-early-apc-unity-then-bridge-v1' `
            -or [string]$observerResult.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or -not [bool]$observerResult.recordingArmBoundaryReached `
            -or -not [bool]$observerResult.recordingArmReleased `
            -or [string]$observerResult.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne '44400000' `
            -or [string]$observerResult.recordingArmBoundaryFixedTimeRaw.canonicalHex `
                -ne '44400000' `
            -or -not [bool]$observerResult.externalTimeUpdateResumeBoundaryInstalled `
            -or [int]$observerResult.externalTimeUpdateResumeBoundaryInstallCount -ne 1 `
            -or [int]$observerResult.externalTimeUpdateResumeBoundaryCallbackCount `
                -le [int]$observerResult.externalTimeUpdateResumeBoundaryCommitCount `
            -or [int]$observerResult.externalTimeUpdateResumeBoundaryCommitCount `
                -ne [int]$observerResult.externalVirtualClockResumeCount `
            -or [int]$observerResult.externalTimeUpdateResumeCommitFaultCode -ne 0 `
            -or [int]$observerResult.externalPlayerLoopPostLateUpdateIndex -lt 0 `
            -or [int]$observerResult.externalPlayerLoopTimeUpdateIndex -lt 0 `
            -or [int]$observerResult.externalPlayerLoopResumeBoundaryIndex -lt 0 `
            -or [int]$observerResult.externalPlayerLoopWaitForPresentationIndex `
                -ne ([int]$observerResult.externalPlayerLoopResumeBoundaryIndex + 1) `
            -or -not [string]::IsNullOrEmpty(
                [string]$observerResult.externalPlayerLoopBoundaryError) `
            -or -not [string]::IsNullOrEmpty(
                [string]$observerResult.inputPhaseObservationError) `
            -or -not [string]::IsNullOrEmpty(
                [string]$observerResult.error) `
            -or -not (
                Test-Path `
                    -LiteralPath $candidateBaselinePath `
                    -PathType Leaf) `
            -or -not (
                Test-Path `
                    -LiteralPath $candidatePhasePath `
                    -PathType Leaf)) {
        throw (
            'Candidate observer contract failed: ' `
            + ($observerResult | ConvertTo-Json -Compress))
    }
    $candidateBaseline = Get-Content `
        -LiteralPath $candidateBaselinePath `
        -Raw |
        ConvertFrom-Json
    if ([string]$candidateBaseline.samplingBoundary `
            -ne 'post-render-completed-frame-sampling-v1') {
        throw 'Candidate baseline was not sampled at a completed frame.'
    }

    $finalState = Get-SdkState
    Start-Sleep -Milliseconds 100
    $finalState = Get-SdkState
    [IO.File]::WriteAllText(
        (Join-Path $environmentDirectory 'final-state.json'),
        $finalState.ToFriendlyJson(),
        [Text.UTF8Encoding]::new($false))
    if ($Mode -eq 'tas-manual-ui') {
        $manualUiAudit = Export-T24ManualUiAudit `
            -BootstrapPath $bootstrapPath `
            -AutomationRoot $automationRoot `
            -OutputPath (
                Join-Path `
                    $environmentDirectory `
                    'manual-ui-automation-audit.json') `
            -ExpectedStepCount $MaxTicks
        if ([string]$manualUiAudit.verdict -cne 'PASS' `
                -or [int]$manualUiAudit.stepCommandCount -ne $MaxTicks `
                -or [int]$manualUiAudit.externalWriteCommandCount -ne 0) {
            throw 'Manual UI automation audit failed closed.'
        }
    }
    $verificationEligibility = if (
        $finalState.State.Fields.ContainsKey('verificationEligibility')
    ) {
        [string]$finalState.State.Fields['verificationEligibility']
    }
    else {
        ''
    }
    $debugMutationEnabled =
        [string]$capabilities.Data['debugMutationEnabled']
    $rngEnabled = if (
        $finalState.State.Fields.ContainsKey(
            'replayDeterministicRngEnabled')
    ) {
        [string]$finalState.State.Fields[
            'replayDeterministicRngEnabled']
    }
    else {
        ''
    }
    $rngResetCount = if (
        $finalState.State.Fields.ContainsKey(
            'replayDeterministicRngResetCount')
    ) {
        [string]$finalState.State.Fields[
            'replayDeterministicRngResetCount']
    }
    else {
        ''
    }
    $finalFields = $finalState.State.Fields
    $startupClockControlClean =
        [bool]$observerResult.externalStartupClockHookInstalled `
        -and [bool]$observerResult.externalStartupClockLatchEnabled `
        -and [uint32]$observerResult.externalStartupClockHookThreadId `
            -eq [uint32]$clockAudit.primaryThreadId `
        -and [int]$observerResult.externalStartupClockVirtualQpcCallCount -gt 0 `
        -and [int]$observerResult.externalStartupClockHandoffAdoptCount -eq 1 `
        -and [int]$observerResult.externalStartupClockFaultCode -eq 0 `
        -and [string]$observerResult.externalRealtimeEpochNormalizationPolicyId `
            -eq 'root-game-minus-rounded-startup-offset-qpc-grid-v3' `
        -and [bool]$observerResult.externalRealtimeEpochNormalizationApplied `
        -and [int]$observerResult.externalRealtimeEpochNormalizationCount -gt 0 `
        -and [string]$observerResult.externalRealtimeEpochNormalizationAfterBits `
            -eq [string]$observerResult.externalRealtimeEpochNormalizationTargetBits `
        -and [int]$observerResult.externalRealtimeEpochNormalizationFaultCode -eq 0 `
        -and [string]$clockAudit.startupPolicy `
            -eq 'create-suspended-early-apc-unity-then-bridge-v1'
    $recordingArmControlClean = $null -ne $recordingArmEvidence `
        -and [string]$recordingArmEvidence.policyId `
            -eq 'existing-manual-reset-one-neutral-completed-frame-preroll-v2' `
        -and [string]$recordingArmEvidence.runId -ceq $runId `
        -and [string]$recordingArmEvidence.armedRuntimeMode -eq 'Running' `
        -and [string]$recordingArmEvidence.armedPlaybackMode -eq 'Idle' `
        -and [bool]$recordingArmEvidence.armedPause `
        -and [bool]$recordingArmEvidence.armedReplay `
        -and -not [bool]$recordingArmEvidence.releaseConsumedBeforeHost `
        -and -not [bool]$recordingArmEvidence.activationAttemptedBeforeHost `
        -and [int]$recordingArmEvidence.
            neutralPreRollCompletedFrameCountBeforeHost -eq 0 `
        -and [string]$recordingArmEvidence.observerPhaseBeforeHost `
            -eq 'waiting-for-recording-arm-release' `
        -and [bool]$recordingArmEvidence.observerBoundaryReachedBeforeHost `
        -and -not [bool]$recordingArmEvidence.observerReleasedBeforeHost `
        -and [string]$recordingArmEvidence.observerBoundaryTimeRawHex `
            -eq '44400000' `
        -and [string]$recordingArmEvidence.observerBoundaryFixedTimeRawHex `
            -eq '44400000' `
        -and [string]$recordingArmEvidence.observerDoublePhaseFinalResidualBits `
            -eq '0000000000000000' `
        -and [bool]$recordingArmEvidence.hostReleaseSet `
        -and [string]$recordingArmEvidence.pausedRuntimeMode -eq 'Paused' `
        -and [string]$recordingArmEvidence.pausedPlaybackMode -eq 'Replaying' `
        -and [bool]$recordingArmEvidence.releaseConsumedAfterHost `
        -and [bool]$recordingArmEvidence.activationAttemptedAfterHost `
        -and [bool]$recordingArmEvidence.activationSucceededAfterHost `
        -and [int]$recordingArmEvidence.activationCountAfterHost -eq 1 `
        -and [int]$recordingArmEvidence.
            neutralPreRollCompletedFrameCountAfterHost -eq 1 `
        -and [string]::IsNullOrEmpty(
            [string]$recordingArmEvidence.activationErrorAfterHost) `
        -and [long]$recordingArmEvidence.replayObservationCountAfterHost -eq 0 `
        -and [long]$recordingArmEvidence.lastReplayMovieTickAfterHost -eq -1 `
        -and [string]$finalFields['deferredRecordingArmEnabled'] -eq 'true' `
        -and [string]$finalFields['deferredRecordingArmRunId'] -ceq $runId `
        -and [string]$finalFields['deferredRecordingArmPauseArmed'] -eq 'true' `
        -and [string]$finalFields['deferredRecordingArmReplayArmed'] -eq 'true' `
        -and [string]$finalFields['deferredRecordingArmReleaseConsumed'] -eq 'true' `
        -and [string]$finalFields['deferredRecordingArmActivationAttempted'] -eq 'true' `
        -and [string]$finalFields['deferredRecordingArmActivationSucceeded'] -eq 'true' `
        -and [int]$finalFields['deferredRecordingArmActivationCount'] -eq 1 `
        -and [int]$finalFields[
            'deferredRecordingArmNeutralPreRollCompletedFrameCount'] -eq 1 `
        -and [string]::IsNullOrEmpty(
            [string]$finalFields['deferredRecordingArmActivationError']) `
        -and [bool]$observerResult.recordingArmBoundaryReached `
        -and [bool]$observerResult.recordingArmReleased `
        -and [string]$observerResult.recordingArmBoundaryTimeRaw.canonicalHex `
            -eq '44400000' `
        -and [string]$observerResult.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -eq '44400000'
    [ordered]@{
        schemaVersion = 1
        verdict = if ($recordingArmControlClean) { 'PASS' } else { 'FAIL' }
        policyId =
            'existing-manual-reset-one-neutral-completed-frame-preroll-v2'
        runId = $runId
        controlClean = $recordingArmControlClean
        startupClockControlClean = $startupClockControlClean
        host = $recordingArmEvidence
        finalRuntime = [ordered]@{
            runtimeMode = [string]$finalState.State.RuntimeMode
            playbackMode = [string]$finalFields['playbackMode']
            enabled = [string]$finalFields['deferredRecordingArmEnabled']
            runId = [string]$finalFields['deferredRecordingArmRunId']
            pauseArmed = [string]$finalFields[
                'deferredRecordingArmPauseArmed']
            replayArmed = [string]$finalFields[
                'deferredRecordingArmReplayArmed']
            releaseConsumed = [string]$finalFields[
                'deferredRecordingArmReleaseConsumed']
            activationAttempted = [string]$finalFields[
                'deferredRecordingArmActivationAttempted']
            activationSucceeded = [string]$finalFields[
                'deferredRecordingArmActivationSucceeded']
            activationCount = [int]$finalFields[
                'deferredRecordingArmActivationCount']
            neutralPreRollCompletedFrameCount = [int]$finalFields[
                'deferredRecordingArmNeutralPreRollCompletedFrameCount']
            activationError = [string]$finalFields[
                'deferredRecordingArmActivationError']
        }
        observer = [ordered]@{
            boundaryReached = [bool]$observerResult.recordingArmBoundaryReached
            released = [bool]$observerResult.recordingArmReleased
            boundaryTimeRawHex = [string]$observerResult.
                recordingArmBoundaryTimeRaw.canonicalHex
            boundaryFixedTimeRawHex = [string]$observerResult.
                recordingArmBoundaryFixedTimeRaw.canonicalHex
            inputInjected = [bool]$observerResult.inputInjected
            timeWritten = [bool]$observerResult.timeWritten
            gameplayStateWritten = [bool]$observerResult.gameplayStateWritten
        }
    } |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath $recordingArmAuditPath `
            -Encoding utf8NoBOM
    $recordingArmAuditSha256 = (Get-FileHash `
        -LiteralPath $recordingArmAuditPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $mutationClean = $verificationEligibility -eq 'Eligible' `
        -and $debugMutationEnabled -eq 'false' `
        -and $debugCapabilitiesDisabled `
        -and $rngEnabled -eq 'false' `
        -and $rngResetCount -eq '0' `
        -and $startupClockControlClean `
        -and $recordingArmControlClean
    $controlSurfaceClean = $Mode -ne 'tas-manual-ui' `
        -or ($null -ne $manualUiControl `
            -and [string]$manualUiControl.verdict -ceq 'PASS' `
            -and -not [bool]$manualUiControl.aiWriteCommandsUsed `
            -and [int]$manualUiControl.exactStepCount -eq $MaxTicks `
            -and $null -ne $manualUiAudit `
            -and [string]$manualUiAudit.verdict -ceq 'PASS' `
            -and [int]$manualUiAudit.stepCommandCount -eq $MaxTicks `
            -and [int]$manualUiAudit.externalWriteCommandCount -eq 0)

    if (-not $referenceBaselineCatalogMatch `
            -or $null -eq $matchedReference) {
        throw 'Candidate lost its pre-step exact baseline catalog match.'
    }

    $scenarioCoverageCandidate = $null
    $scenarioCoverageReference = $null
    if ($null -ne $scenarioContract) {
        $coverageMode = switch ($Mode) {
            'tas-passive' { 'TasPassive' }
            'tas-continuous' { 'AiTas' }
            'tas-sequential' { 'SequentialStep' }
            'tas-batch' { 'BatchStep' }
            'tas-manual-ui' { 'ManualTas' }
            default { throw "Unsupported scenario coverage mode '$Mode'." }
        }
        $coverageScript = Join-Path `
            $PSScriptRoot `
            'Test-T24ScenarioCoverage.ps1'
        $scenarioCoverageCandidate = & $coverageScript `
            -ContractPath $ScenarioContractPath `
            -TracePath $candidateTracePath `
            -OutputPath (
                Join-Path `
                    $EvidenceRoot `
                    'scenario-coverage.candidate.json') `
            -RunMode $coverageMode `
            -NoThrow
        $scenarioCoverageReference = & $coverageScript `
            -ContractPath $ScenarioContractPath `
            -TracePath $ReferenceTracePath `
            -OutputPath (
                Join-Path `
                    $EvidenceRoot `
                    'scenario-coverage.reference.json') `
            -RunMode VanillaReference `
            -NoThrow
    }
    $scenarioCoverageClean = $null -eq $scenarioContract `
        -or ([string]$scenarioCoverageCandidate.verdict -ceq 'PASS' `
            -and [string]$scenarioCoverageReference.verdict -ceq 'PASS')

    $baselineComparison = Compare-T24Baselines `
        -ReferencePath $ReferenceBaselinePath `
        -CandidatePath $candidateBaselinePath `
        -EnvelopePolicy $negativeControlPolicy
    $phaseComparison = Compare-T24PhaseTraces `
        -ReferencePath $ReferencePhasePath `
        -CandidatePath $candidatePhasePath
    $allowBaselineNormalizedRigidbody = `
        [bool]$baselineComparison.equivalent `
        -and [int]$baselineComparison.rigidbodyPositionDifferenceCount -gt 0
    $rigidbodyXWitnessPath = if (
        $allowBaselineNormalizedRigidbody `
            -and $referenceBaselineCatalogMatch) {
        [string]$matchedReference.rigidbodyXWitnessTracePath
    }
    else { '' }
    $rigidbodyYWitnessPath = if (
        $allowBaselineNormalizedRigidbody `
            -and $referenceBaselineCatalogMatch) {
        [string]$matchedReference.rigidbodyYWitnessTracePath
    }
    else { '' }
    $traceComparison = Compare-T24TracesWithEnvelope `
        -ReferencePath $ReferenceTracePath `
        -CandidatePath $candidateTracePath `
        -EnvelopePolicy $negativeControlPolicy `
        -AllowBaselineNormalizedRigidbody:$allowBaselineNormalizedRigidbody `
        -RigidbodyXWitnessPath $rigidbodyXWitnessPath `
        -RigidbodyYWitnessPath $rigidbodyYWitnessPath
    $comparison = [ordered]@{
        schemaVersion = 4
        mode = $Mode
        referenceBaselineCatalogMatch = $referenceBaselineCatalogMatch
        referenceBaselineSignatureSha256 = if (
            $referenceBaselineCatalogMatch) {
            $matchedReference.signatureSha256
        }
        else { '' }
        selectedReferenceAttempt = if ($referenceBaselineCatalogMatch) {
            $matchedReference.attempt
        }
        else { 0 }
        referenceBaselineMatchKind = if (
            $referenceBaselineCatalogMatch) {
            $matchedReference.matchKind
        }
        else { '' }
        baselineStrictEquivalent = $baselineComparison.strictEquivalent
        baselineAuthoritativeExactEquivalent = `
            $baselineComparison.authoritativeExactEquivalent
        baselineSemanticExactEquivalent = `
            $baselineComparison.semanticExactEquivalent
        baselineHeroZVanillaVisualRandomEquivalent = `
            $baselineComparison.heroZVanillaVisualRandomEquivalent
        baselineHeroZVanillaVisualRandomExact = `
            $baselineComparison.heroZVanillaVisualRandomExact
        baselineHeroZVanillaVisualRandomPolicyId = `
            $baselineComparison.heroZVanillaVisualRandomPolicyId
        baselineHeroZCandidateCanonicalHex = `
            $baselineComparison.heroZCandidateCanonicalHex
        baselineHeroZReferenceCanonicalHex = `
            $baselineComparison.heroZReferenceCanonicalHex
        baselineHeroZMinimumCanonicalHex = `
            $baselineComparison.heroZMinimumCanonicalHex
        baselineHeroZMaximumCanonicalHex = `
            $baselineComparison.heroZMaximumCanonicalHex
        baselineRigidbodyComponentsObserved = `
            $baselineComparison.rigidbodyComponentsObserved
        baselineRigidbodyPositionDifferenceCount = `
            $baselineComparison.rigidbodyPositionDifferenceCount
        baselineRenderTransformEnvelopeEquivalent = `
            $baselineComparison.renderTransformEnvelopeEquivalent
        baselineEquivalent = $baselineComparison.equivalent
        baselineAcceptedRenderTransformDifferenceCount = `
            $baselineComparison.acceptedRenderTransformDifferenceCount
        baselineFirstDifferenceKey = `
            $baselineComparison.firstDifferenceKey
        baselineDifferenceCount = $baselineComparison.differenceCount
        phaseEquivalent = $phaseComparison.equivalent
        referencePhaseEvents = $phaseComparison.referenceEvents
        candidatePhaseEvents = $phaseComparison.candidateEvents
        comparedReferencePhaseEvents = `
            $phaseComparison.comparedReferenceEvents
        comparedCandidatePhaseEvents = `
            $phaseComparison.comparedCandidateEvents
        phaseFirstDifferenceIndex = `
            $phaseComparison.firstDifferenceIndex
        referenceFrames = $traceComparison.referenceFrames
        candidateFrames = $traceComparison.candidateFrames
        inputEquivalent = $traceComparison.inputEquivalent
        bitwiseGameplayEquivalent = `
            $traceComparison.bitwiseGameplayEquivalent
        vanillaEnvelopeEquivalent = `
            $traceComparison.vanillaEnvelopeEquivalent
        baselineNormalizedRigidbodyEquivalent = `
            $traceComparison.baselineNormalizedRigidbodyEquivalent
        rigidbodyAxisWitnessesAvailable = `
            -not $allowBaselineNormalizedRigidbody `
            -or ($referenceBaselineCatalogMatch `
                -and [bool]$matchedReference.rigidbodyAxisWitnessesAvailable)
        rigidbodyAxisWitnessEquivalent = `
            $traceComparison.baselineNormalizedRigidbodyEquivalent
        rigidbodyXWitnessAttempt = if ($referenceBaselineCatalogMatch) {
            [int]$matchedReference.rigidbodyXWitnessAttempt
        }
        else { 0 }
        rigidbodyXWitnessTraceSha256 = if (
            $referenceBaselineCatalogMatch) {
            [string]$matchedReference.rigidbodyXWitnessTraceSha256
        }
        else { '' }
        rigidbodyYWitnessAttempt = if ($referenceBaselineCatalogMatch) {
            [int]$matchedReference.rigidbodyYWitnessAttempt
        }
        else { 0 }
        rigidbodyYWitnessTraceSha256 = if (
            $referenceBaselineCatalogMatch) {
            [string]$matchedReference.rigidbodyYWitnessTraceSha256
        }
        else { '' }
        gameplayEquivalent = $traceComparison.gameplayEquivalent
        firstDifferenceTick = $traceComparison.firstDifferenceTick
        firstDifferenceKey = $traceComparison.firstDifferenceKey
        firstUnacceptedDifferenceTick = `
            $traceComparison.firstUnacceptedDifferenceTick
        firstUnacceptedDifferenceKey = `
            $traceComparison.firstUnacceptedDifferenceKey
        acceptedNegativeControlDifferenceCount = `
            $traceComparison.acceptedDifferenceCount
        acceptedBaselineNormalizedRigidbodyDifferenceCount = `
            $traceComparison.acceptedBaselineNormalizedRigidbodyDifferenceCount
        negativeControlPolicyId = $negativeControlPolicy.policyId
        negativeControlEnvelopeSha256 = $negativeControlPolicy.sha256
    }
    $comparison |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (Join-Path $EvidenceRoot 'comparison.json') `
            -Encoding utf8NoBOM

    [ordered]@{
        schemaVersion = 1
        mode = $Mode
        observerReadOnly = $true
        verificationEligibility = $verificationEligibility
        debugMutationEnabled = $debugMutationEnabled
        debugCapabilitiesDisabled = $debugCapabilitiesDisabled
        replayDeterministicRngEnabled = $rngEnabled
        replayDeterministicRngResetCount = $rngResetCount
        externalRngSynchronized =
            [bool]$observerResult.externalRngSynchronized
        externalRngSynchronizationOriginCaptured =
            [bool]$observerResult.externalRngSynchronizationOriginCaptured
        externalRngSynchronizationPolicy =
            [string]$observerResult.externalRngSynchronizationPolicy
        externalRngSeed = [int]$observerResult.externalRngSeed
        externalClockProfileId =
            [string]$observerResult.externalClockProfileId
        externalRngPayloadPolicyId =
            [string]$observerResult.externalRngPayloadPolicyId
        externalRngResetCount =
            [int]$observerResult.externalRngResetCount
        externalRngTransitionStartCount =
            [int]$observerResult.externalRngTransitionStartCount
        externalRngGameplayReadyCount =
            [int]$observerResult.externalRngGameplayReadyCount
        externalRngSceneEpoch =
            [int]$observerResult.externalRngSceneEpoch
        externalRngLastAppliedSeed =
            [int]$observerResult.externalRngLastAppliedSeed
        externalRngLastBoundary =
            [string]$observerResult.externalRngLastBoundary
        externalRngLastScene =
            [string]$observerResult.externalRngLastScene
        externalRngFaultCode =
            [int]$observerResult.externalRngFaultCode
        externalSceneRngPending =
            [bool]$observerResult.externalSceneRngPending
        externalSceneClockExclusionActive =
            [bool]$observerResult.externalSceneClockExclusionActive
        externalSceneClockExclusionBeginCount =
            [int]$observerResult.externalSceneClockExclusionBeginCount
        externalSceneClockExclusionFinishCount =
            [int]$observerResult.externalSceneClockExclusionFinishCount
        externalSceneClockExclusionFrozenTimeUpdateCount =
            [int]$observerResult.externalSceneClockExclusionFrozenTimeUpdateCount
        externalSceneClockExclusionFaultCode =
            [int]$observerResult.externalSceneClockExclusionFaultCode
        externalSceneClockExclusionPreSynchronizationCount =
            [int]$observerResult.externalSceneClockExclusionPreSynchronizationCount
        traceSceneTransitionCount =
            [int]$candidateRngBoundaryAudit.transitionCount
        externalInputSynchronizationPrimeFrames =
            [int]$observerResult.externalInputSynchronizationPrimeFrames
        externalClockBridgeAbi =
            [uint32]$observerResult.externalClockBridgeAbi
        externalClockBridgeStatus =
            [int]$observerResult.externalClockBridgeStatus
        externalRuntimeVirtualClockRegistered =
            [bool]$observerResult.externalRuntimeVirtualClockRegistered
        externalVirtualClockPaused =
            [int]$observerResult.externalVirtualClockPaused
        externalVirtualClockPauseCount =
            [int]$observerResult.externalVirtualClockPauseCount
        externalVirtualClockResumePending =
            [int]$observerResult.externalVirtualClockResumePending
        externalVirtualClockResumeRequestCount =
            [int]$observerResult.externalVirtualClockResumeRequestCount
        externalVirtualClockResumeCount =
            [int]$observerResult.externalVirtualClockResumeCount
        externalDeterministicClockEnabled =
            [bool]$observerResult.externalDeterministicClockEnabled
        externalDeterministicClockFrequency =
            [long]$observerResult.externalDeterministicClockFrequency
        externalDeterministicClockStepTicks =
            [long]$observerResult.externalDeterministicClockStepTicks
        externalDeterministicClockAnchor =
            [long]$observerResult.externalDeterministicClockAnchor
        externalDeterministicClockFrameAdvanceCount =
            [int]$observerResult.externalDeterministicClockFrameAdvanceCount
        externalDeterministicClockEnableFaultCode =
            [int]$observerResult.externalDeterministicClockEnableFaultCode
        externalDeterministicClockAdvanceFaultCode =
            [int]$observerResult.externalDeterministicClockAdvanceFaultCode
        externalRealtimeEpochNormalizationPolicyId =
            [string]$observerResult.externalRealtimeEpochNormalizationPolicyId
        externalRealtimeEpochNormalizationApplied =
            [bool]$observerResult.externalRealtimeEpochNormalizationApplied
        externalRealtimeEpochNormalizationCount =
            [int]$observerResult.externalRealtimeEpochNormalizationCount
        externalRealtimeEpochNormalizationGameTimeBits =
            [string]$observerResult.externalRealtimeEpochNormalizationGameTimeBits
        externalRealtimeEpochNormalizationBeforeBits =
            [string]$observerResult.externalRealtimeEpochNormalizationBeforeBits
        externalRealtimeEpochNormalizationTargetBits =
            [string]$observerResult.externalRealtimeEpochNormalizationTargetBits
        externalRealtimeEpochNormalizationAfterBits =
            [string]$observerResult.externalRealtimeEpochNormalizationAfterBits
        externalRealtimeEpochNormalizationCanonicalOffsetSeconds =
            [long]$observerResult.externalRealtimeEpochNormalizationCanonicalOffsetSeconds
        externalRealtimeEpochNormalizationDeltaTicks =
            [long]$observerResult.externalRealtimeEpochNormalizationDeltaTicks
        externalRealtimeEpochNormalizationFaultCode =
            [int]$observerResult.externalRealtimeEpochNormalizationFaultCode
        externalTimeUpdateResumeBoundaryInstalled =
            [bool]$observerResult.externalTimeUpdateResumeBoundaryInstalled
        externalTimeUpdateResumeBoundaryInstallCount =
            [int]$observerResult.externalTimeUpdateResumeBoundaryInstallCount
        externalTimeUpdateResumeBoundaryCallbackCount =
            [int]$observerResult.externalTimeUpdateResumeBoundaryCallbackCount
        externalTimeUpdateResumeBoundaryCommitCount =
            [int]$observerResult.externalTimeUpdateResumeBoundaryCommitCount
        externalTimeUpdateResumeCommitFaultCode =
            [int]$observerResult.externalTimeUpdateResumeCommitFaultCode
        externalPlayerLoopPostLateUpdateIndex =
            [int]$observerResult.externalPlayerLoopPostLateUpdateIndex
        externalPlayerLoopTimeUpdateIndex =
            [int]$observerResult.externalPlayerLoopTimeUpdateIndex
        externalPlayerLoopResumeBoundaryIndex =
            [int]$observerResult.externalPlayerLoopResumeBoundaryIndex
        externalPlayerLoopWaitForPresentationIndex =
            [int]$observerResult.externalPlayerLoopWaitForPresentationIndex
        externalPlayerLoopBoundaryError =
            [string]$observerResult.externalPlayerLoopBoundaryError
        startupClockControlClean = $startupClockControlClean
        recordingArmControlClean = $recordingArmControlClean
        recordingArmAuditSha256 = $recordingArmAuditSha256
        mutationClean = $mutationClean
        externalClockPrototype = $true
        clockCapabilityId = [string]$clockAudit.capabilityId
        clockProfile = [string]$clockAudit.profile
        externalRngPayloadSynchronizationPolicy =
            [string]$clockAudit.randomSynchronizationPolicy
        externalRngPayloadSeed =
            [int]$clockAudit.randomSynchronizationSeed
        negativeControlPolicyId = $negativeControlPolicy.policyId
        negativeControlEnvelopeSha256 = $negativeControlPolicy.sha256
        negativeControlClockContractSha256 = `
            $negativeControlPolicy.clockContractSha256
        candidateClockContractSha256 = $candidateClockContractSha256
    } |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (Join-Path $EvidenceRoot 'mutation-audit.json') `
            -Encoding utf8NoBOM

    [ordered]@{
        schemaVersion = 3
        verdict = if ($comparison.gameplayEquivalent `
                -and $comparison.inputEquivalent `
                -and $scenarioCoverageClean `
                -and $controlSurfaceClean `
                -and $referenceBaselineCatalogMatch `
                -and $comparison.baselineSemanticExactEquivalent `
                -and $comparison.baselineHeroZVanillaVisualRandomEquivalent `
                -and $comparison.baselineRigidbodyComponentsObserved `
                -and $comparison.baselineRenderTransformEnvelopeEquivalent `
                -and $comparison.baselineEquivalent `
                -and $comparison.phaseEquivalent `
                -and $comparison.baselineNormalizedRigidbodyEquivalent `
                -and $comparison.rigidbodyAxisWitnessesAvailable `
                -and $comparison.rigidbodyAxisWitnessEquivalent `
                -and $pauseDwellClean `
                -and $startupClockControlClean `
                -and $recordingArmControlClean `
                -and $mutationClean) {
            'PASS'
        }
        else {
            'FAIL'
        }
        runId = $runId
        mode = $Mode
        fixtureSlot = $FixtureSlot
        frameCount = $MaxTicks
        pauseDwellClean = $pauseDwellClean
        pauseDwellPolicyId = if ($Mode -eq 'tas-sequential') {
            'external-wall-clock-dwell-with-lease-renewal-before-each-sequential-step-v2'
        }
        else { 'NOT_APPLICABLE' }
        pauseDwellMillisecondsPerStep = if ($Mode -eq 'tas-sequential') {
            $PauseDwellMilliseconds
        }
        else { 0 }
        pauseDwellAuditSha256 = $pauseDwellAuditSha256
        controlSurfaceClean = $controlSurfaceClean
        scenarioCoverageClean = $scenarioCoverageClean
        scenarioId = if ($null -eq $scenarioContract) {
            ''
        }
        else {
            [string]$scenarioContract['scenarioId']
        }
        candidateScenarioCoverageVerdict = if (
            $null -eq $scenarioCoverageCandidate) {
            'NOT_REQUESTED'
        }
        else {
            [string]$scenarioCoverageCandidate.verdict
        }
        referenceScenarioCoverageVerdict = if (
            $null -eq $scenarioCoverageReference) {
            'NOT_REQUESTED'
        }
        else {
            [string]$scenarioCoverageReference.verdict
        }
        referenceTrace = $ReferenceTracePath
        referenceTraceSha256 = (Get-FileHash `
            -LiteralPath $ReferenceTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        referenceBaseline = $ReferenceBaselinePath
        referenceBaselineSha256 = (Get-FileHash `
            -LiteralPath $ReferenceBaselinePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        referenceBaselineCatalogMatch = $referenceBaselineCatalogMatch
        referenceBaselineSignatureSha256 = if (
            $referenceBaselineCatalogMatch) {
            $matchedReference.signatureSha256
        }
        else { '' }
        selectedReferenceAttempt = if ($referenceBaselineCatalogMatch) {
            $matchedReference.attempt
        }
        else { 0 }
        referenceBaselineMatchKind = if (
            $referenceBaselineCatalogMatch) {
            $matchedReference.matchKind
        }
        else { '' }
        candidateTraceSha256 = (Get-FileHash `
            -LiteralPath $candidateTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        candidateBaselineSha256 = (Get-FileHash `
            -LiteralPath $candidateBaselinePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        baselineStrictEquivalent = `
            $comparison.baselineStrictEquivalent
        baselineAuthoritativeExactEquivalent = `
            $comparison.baselineAuthoritativeExactEquivalent
        baselineSemanticExactEquivalent = `
            $comparison.baselineSemanticExactEquivalent
        baselineHeroZVanillaVisualRandomEquivalent = `
            $comparison.baselineHeroZVanillaVisualRandomEquivalent
        baselineHeroZVanillaVisualRandomExact = `
            $comparison.baselineHeroZVanillaVisualRandomExact
        baselineHeroZVanillaVisualRandomPolicyId = `
            $comparison.baselineHeroZVanillaVisualRandomPolicyId
        baselineHeroZCandidateCanonicalHex = `
            $comparison.baselineHeroZCandidateCanonicalHex
        baselineHeroZReferenceCanonicalHex = `
            $comparison.baselineHeroZReferenceCanonicalHex
        baselineHeroZMinimumCanonicalHex = `
            $comparison.baselineHeroZMinimumCanonicalHex
        baselineHeroZMaximumCanonicalHex = `
            $comparison.baselineHeroZMaximumCanonicalHex
        baselineRigidbodyComponentsObserved = `
            $comparison.baselineRigidbodyComponentsObserved
        baselineRigidbodyPositionDifferenceCount = `
            $comparison.baselineRigidbodyPositionDifferenceCount
        baselineRenderTransformEnvelopeEquivalent = `
            $comparison.baselineRenderTransformEnvelopeEquivalent
        baselineEquivalent = $comparison.baselineEquivalent
        baselineAcceptedRenderTransformDifferenceCount = `
            $comparison.baselineAcceptedRenderTransformDifferenceCount
        baselineFirstDifferenceKey = `
            $comparison.baselineFirstDifferenceKey
        phaseEquivalent = $comparison.phaseEquivalent
        phaseFirstDifferenceIndex = `
            $comparison.phaseFirstDifferenceIndex
        inputEquivalent = $comparison.inputEquivalent
        bitwiseGameplayEquivalent = `
            $comparison.bitwiseGameplayEquivalent
        vanillaEnvelopeEquivalent = `
            $comparison.vanillaEnvelopeEquivalent
        baselineNormalizedRigidbodyEquivalent = `
            $comparison.baselineNormalizedRigidbodyEquivalent
        rigidbodyAxisWitnessesAvailable = `
            $comparison.rigidbodyAxisWitnessesAvailable
        rigidbodyAxisWitnessEquivalent = `
            $comparison.rigidbodyAxisWitnessEquivalent
        rigidbodyXWitnessAttempt = $comparison.rigidbodyXWitnessAttempt
        rigidbodyXWitnessTraceSha256 = `
            $comparison.rigidbodyXWitnessTraceSha256
        rigidbodyYWitnessAttempt = $comparison.rigidbodyYWitnessAttempt
        rigidbodyYWitnessTraceSha256 = `
            $comparison.rigidbodyYWitnessTraceSha256
        gameplayEquivalent = $comparison.gameplayEquivalent
        firstDifferenceTick = $comparison.firstDifferenceTick
        firstDifferenceKey = $comparison.firstDifferenceKey
        firstUnacceptedDifferenceTick = `
            $comparison.firstUnacceptedDifferenceTick
        firstUnacceptedDifferenceKey = `
            $comparison.firstUnacceptedDifferenceKey
        acceptedNegativeControlDifferenceCount = `
            $comparison.acceptedNegativeControlDifferenceCount
        acceptedBaselineNormalizedRigidbodyDifferenceCount = `
            $comparison.acceptedBaselineNormalizedRigidbodyDifferenceCount
        negativeControlPolicyId = $negativeControlPolicy.policyId
        negativeControlEnvelopeSha256 = $negativeControlPolicy.sha256
        startupClockControlClean = $startupClockControlClean
        recordingArmControlClean = $recordingArmControlClean
        recordingArmAuditSha256 = $recordingArmAuditSha256
        mutationClean = $mutationClean
        externalClockPrototype = $true
        clockCapabilityId = [string]$clockAudit.capabilityId
        clockProfile = [string]$clockAudit.profile
        externalRngSynchronized =
            [bool]$observerResult.externalRngSynchronized
        externalRngSynchronizationOriginCaptured =
            [bool]$observerResult.externalRngSynchronizationOriginCaptured
        externalRngSynchronizationPolicy =
            [string]$observerResult.externalRngSynchronizationPolicy
        externalRngSeed = [int]$observerResult.externalRngSeed
        externalClockProfileId =
            [string]$observerResult.externalClockProfileId
        externalRngPayloadPolicyId =
            [string]$observerResult.externalRngPayloadPolicyId
        externalRngResetCount =
            [int]$observerResult.externalRngResetCount
        externalRngTransitionStartCount =
            [int]$observerResult.externalRngTransitionStartCount
        externalRngGameplayReadyCount =
            [int]$observerResult.externalRngGameplayReadyCount
        externalRngSceneEpoch =
            [int]$observerResult.externalRngSceneEpoch
        externalRngLastAppliedSeed =
            [int]$observerResult.externalRngLastAppliedSeed
        externalRngLastBoundary =
            [string]$observerResult.externalRngLastBoundary
        externalRngLastScene =
            [string]$observerResult.externalRngLastScene
        externalRngFaultCode =
            [int]$observerResult.externalRngFaultCode
        externalSceneRngPending =
            [bool]$observerResult.externalSceneRngPending
        externalSceneClockExclusionActive =
            [bool]$observerResult.externalSceneClockExclusionActive
        externalSceneClockExclusionBeginCount =
            [int]$observerResult.externalSceneClockExclusionBeginCount
        externalSceneClockExclusionFinishCount =
            [int]$observerResult.externalSceneClockExclusionFinishCount
        externalSceneClockExclusionFrozenTimeUpdateCount =
            [int]$observerResult.externalSceneClockExclusionFrozenTimeUpdateCount
        externalSceneClockExclusionFaultCode =
            [int]$observerResult.externalSceneClockExclusionFaultCode
        externalSceneClockExclusionPreSynchronizationCount =
            [int]$observerResult.externalSceneClockExclusionPreSynchronizationCount
        traceSceneTransitionCount =
            [int]$candidateRngBoundaryAudit.transitionCount
        externalInputSynchronizationPrimeFrames =
            [int]$observerResult.externalInputSynchronizationPrimeFrames
        externalClockBridgeAbi =
            [uint32]$observerResult.externalClockBridgeAbi
        externalClockBridgeStatus =
            [int]$observerResult.externalClockBridgeStatus
        externalRuntimeVirtualClockRegistered =
            [bool]$observerResult.externalRuntimeVirtualClockRegistered
        externalVirtualClockPaused =
            [int]$observerResult.externalVirtualClockPaused
        externalVirtualClockPauseCount =
            [int]$observerResult.externalVirtualClockPauseCount
        externalVirtualClockResumePending =
            [int]$observerResult.externalVirtualClockResumePending
        externalVirtualClockResumeRequestCount =
            [int]$observerResult.externalVirtualClockResumeRequestCount
        externalVirtualClockResumeCount =
            [int]$observerResult.externalVirtualClockResumeCount
        externalDeterministicClockEnabled =
            [bool]$observerResult.externalDeterministicClockEnabled
        externalDeterministicClockFrequency =
            [long]$observerResult.externalDeterministicClockFrequency
        externalDeterministicClockStepTicks =
            [long]$observerResult.externalDeterministicClockStepTicks
        externalDeterministicClockAnchor =
            [long]$observerResult.externalDeterministicClockAnchor
        externalDeterministicClockFrameAdvanceCount =
            [int]$observerResult.externalDeterministicClockFrameAdvanceCount
        externalDeterministicClockEnableFaultCode =
            [int]$observerResult.externalDeterministicClockEnableFaultCode
        externalDeterministicClockAdvanceFaultCode =
            [int]$observerResult.externalDeterministicClockAdvanceFaultCode
        externalRealtimeEpochNormalizationPolicyId =
            [string]$observerResult.externalRealtimeEpochNormalizationPolicyId
        externalRealtimeEpochNormalizationApplied =
            [bool]$observerResult.externalRealtimeEpochNormalizationApplied
        externalRealtimeEpochNormalizationCount =
            [int]$observerResult.externalRealtimeEpochNormalizationCount
        externalRealtimeEpochNormalizationGameTimeBits =
            [string]$observerResult.externalRealtimeEpochNormalizationGameTimeBits
        externalRealtimeEpochNormalizationBeforeBits =
            [string]$observerResult.externalRealtimeEpochNormalizationBeforeBits
        externalRealtimeEpochNormalizationTargetBits =
            [string]$observerResult.externalRealtimeEpochNormalizationTargetBits
        externalRealtimeEpochNormalizationAfterBits =
            [string]$observerResult.externalRealtimeEpochNormalizationAfterBits
        externalRealtimeEpochNormalizationCanonicalOffsetSeconds =
            [long]$observerResult.externalRealtimeEpochNormalizationCanonicalOffsetSeconds
        externalRealtimeEpochNormalizationDeltaTicks =
            [long]$observerResult.externalRealtimeEpochNormalizationDeltaTicks
        externalRealtimeEpochNormalizationFaultCode =
            [int]$observerResult.externalRealtimeEpochNormalizationFaultCode
        externalDoublePhaseCalibrationApplied =
            [bool]$observerResult.externalDoublePhaseCalibrationApplied
        externalDoublePhaseCalibrationAttempts =
            [int]$observerResult.externalDoublePhaseCalibrationAttempts
        externalDoublePhaseFinalResidualBits =
            [string]$observerResult.externalDoublePhaseFinalResidualBits
        externalDoublePhaseFinalResidual =
            $observerResult.externalDoublePhaseFinalResidual
        externalDoublePhaseLastCorrectionBits =
            [string]$observerResult.externalDoublePhaseLastCorrectionBits
        externalDoublePhaseCalibrationFaultCode =
            [int]$observerResult.externalDoublePhaseCalibrationFaultCode
        externalStartupClockHookInstalled =
            [bool]$observerResult.externalStartupClockHookInstalled
        externalStartupClockLatchEnabled =
            [bool]$observerResult.externalStartupClockLatchEnabled
        externalStartupClockHookThreadId =
            [uint32]$observerResult.externalStartupClockHookThreadId
        externalStartupClockPrimaryThreadMatched =
            [uint32]$observerResult.externalStartupClockHookThreadId `
                -eq [uint32]$clockAudit.primaryThreadId
        externalStartupClockVirtualQpcCallCount =
            [int]$observerResult.externalStartupClockVirtualQpcCallCount
        externalStartupClockHandoffAdoptCount =
            [int]$observerResult.externalStartupClockHandoffAdoptCount
        externalStartupClockFaultCode =
            [int]$observerResult.externalStartupClockFaultCode
        externalStartupPolicy = [string]$clockAudit.startupPolicy
        recordingArmPolicyId =
            'existing-manual-reset-one-neutral-completed-frame-preroll-v2'
        recordingAbsoluteTimeTarget =
            $observerResult.recordingAbsoluteTimeTarget
        recordingArmBoundaryReached =
            [bool]$observerResult.recordingArmBoundaryReached
        recordingArmReleased = [bool]$observerResult.recordingArmReleased
        recordingArmBoundaryTimeRaw =
            $observerResult.recordingArmBoundaryTimeRaw
        recordingArmBoundaryFixedTimeRaw =
            $observerResult.recordingArmBoundaryFixedTimeRaw
        recordingArmControl = $recordingArmEvidence
        externalTimeUpdateResumeBoundaryInstalled =
            [bool]$observerResult.externalTimeUpdateResumeBoundaryInstalled
        externalTimeUpdateResumeBoundaryInstallCount =
            [int]$observerResult.externalTimeUpdateResumeBoundaryInstallCount
        externalTimeUpdateResumeBoundaryCallbackCount =
            [int]$observerResult.externalTimeUpdateResumeBoundaryCallbackCount
        externalTimeUpdateResumeBoundaryCommitCount =
            [int]$observerResult.externalTimeUpdateResumeBoundaryCommitCount
        externalTimeUpdateResumeCommitFaultCode =
            [int]$observerResult.externalTimeUpdateResumeCommitFaultCode
        externalPlayerLoopPostLateUpdateIndex =
            [int]$observerResult.externalPlayerLoopPostLateUpdateIndex
        externalPlayerLoopTimeUpdateIndex =
            [int]$observerResult.externalPlayerLoopTimeUpdateIndex
        externalPlayerLoopResumeBoundaryIndex =
            [int]$observerResult.externalPlayerLoopResumeBoundaryIndex
        externalPlayerLoopWaitForPresentationIndex =
            [int]$observerResult.externalPlayerLoopWaitForPresentationIndex
        externalPlayerLoopBoundaryError =
            [string]$observerResult.externalPlayerLoopBoundaryError
        externalRngPayloadSynchronizationPolicy =
            [string]$clockAudit.randomSynchronizationPolicy
        externalRngPayloadSeed =
            [int]$clockAudit.randomSynchronizationSeed
        externalUiLoad = $true
        visualRecognitionUsed = $false
        startupOrderPolicyId =
            'fixture-baseline-before-sdk-connect-v1'
        startupOrderAuditSha256 = (Get-FileHash `
            -LiteralPath $startupOrderAuditPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        controlSurface = if ($Mode -eq 'tas-manual-ui') {
            'companion-ui'
        }
        else {
            'automation-sdk'
        }
        manualUiAutomationScriptSha256 = if (
            $Mode -eq 'tas-manual-ui') {
            (Get-FileHash `
                -LiteralPath $manualUiAutomationScriptPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        filesystemIsolationScriptSha256 = (Get-FileHash `
            -LiteralPath $filesystemIsolationScriptPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        manualUiControl = if ($null -eq $manualUiControl) {
            $null
        }
        else {
            [ordered]@{
                verdict = [string]$manualUiControl.verdict
                interaction = [string]$manualUiControl.interaction
                visualRecognitionUsed =
                    [bool]$manualUiControl.visualRecognitionUsed
                aiWriteCommandsUsed =
                    [bool]$manualUiControl.aiWriteCommandsUsed
                initialMovieTick = [long]$manualUiControl.initialMovieTick
                finalMovieTick = [long]$manualUiControl.finalMovieTick
                exactStepCount = [int]$manualUiControl.exactStepCount
            }
        }
        manualUiAudit = if ($null -eq $manualUiAudit) {
            $null
        }
        else {
            [ordered]@{
                verdict = [string]$manualUiAudit.verdict
                clientId = [string]$manualUiAudit.clientId
                commandCount = [int]$manualUiAudit.commandCount
                writeCommandCount = [int]$manualUiAudit.writeCommandCount
                stepCommandCount = [int]$manualUiAudit.stepCommandCount
                externalWriteCommandCount =
                    [int]$manualUiAudit.externalWriteCommandCount
                auditSourceSha256 =
                    [string]$manualUiAudit.auditSourceSha256
            }
        }
        validation = if ($null -eq $validate) {
            if ($null -eq $manualUiControl) {
                $null
            }
            else {
                [ordered]@{
                    success = $true
                    detail = [string]$manualUiControl.validation
                }
            }
        }
        else {
            Convert-AutomationResult $validate
        }
        proposal = if ($null -eq $proposal) {
            $null
        }
        else {
            Convert-AutomationResult $proposal
        }
        apply = if ($null -eq $apply) {
            $null
        }
        else {
            Convert-AutomationResult $apply
        }
        pause = if ($null -eq $pause) {
            $null
        }
        else {
            Convert-AutomationResult $pause
        }
        resume = if ($null -eq $resume) {
            $null
        }
        else {
            Convert-AutomationResult $resume
        }
        startReplay = if ($null -eq $start) {
            if ($null -eq $manualUiControl) {
                $null
            }
            else {
                [ordered]@{
                    success = $true
                    detail = [string]$manualUiControl.startReplayStatus
                }
            }
        }
        else {
            Convert-AutomationResult $start
        }
        rngSynchronizationArmed = $rngSynchronizationArmed
    } |
        ConvertTo-Json -Depth 50 |
        Set-Content `
            -LiteralPath (Join-Path $EvidenceRoot 'final-verdict.json') `
            -Encoding utf8NoBOM

    if (-not [string]::IsNullOrWhiteSpace($leaseId)) {
        [void](Invoke-SdkCommand `
            -Client $client `
            -CommandId 'releaseControl' `
            -Scope 'control.playback' `
            -LeaseId $leaseId `
            -AllowFailure)
    }
    $leaseReleased = $true
}
catch {
    $runFailure = $_
    try {
        [ordered]@{
            schemaVersion = 1
            failedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            message = [string]$_.Exception.Message
            exceptionType = $_.Exception.GetType().FullName
            category = [string]$_.CategoryInfo.Category
            target = [string]$_.CategoryInfo.TargetName
            scriptStackTrace = [string]$_.ScriptStackTrace
        } |
            ConvertTo-Json -Depth 10 |
            Set-Content `
                -LiteralPath (
                    Join-Path $environmentDirectory 'run-failure.json') `
                -Encoding utf8NoBOM
    }
    catch {
    }
}
finally {
    try {
        Release-GameplayKeys
    if ($null -ne $recordingArmRelease) {
        [void]$recordingArmRelease.Set()
        $recordingArmRelease.Dispose()
        $recordingArmRelease = $null
    }
    if ($null -ne $client) {
        if (-not $leaseReleased `
                -and -not [string]::IsNullOrWhiteSpace($leaseId)) {
            try {
                [void](Invoke-SdkCommand `
                    -Client $client `
                    -CommandId 'releaseControl' `
                    -Scope 'control.playback' `
                    -LeaseId $leaseId `
                    -AllowFailure)
            }
            catch {
            }
        }
        try {
            $client.DisposeAsync().GetAwaiter().GetResult()
        }
        catch {
        }
    }
    Close-RunProcesses

    if ($slotCaptured) {
        $currentSlotFiles = @(
            Get-ChildItem `
                -LiteralPath $PersistentDataDirectory `
                -File `
                -Force |
            Where-Object { $_.Name -match $slotPattern }
        )
        foreach ($file in $currentSlotFiles) {
            if ($slotNamesBefore -notcontains $file.Name) {
                $full = [IO.Path]::GetFullPath($file.FullName)
                if (-not $full.StartsWith(
                        $persistentPrefix,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing slot cleanup outside persistent data.'
                }
                Remove-Item -LiteralPath $full -Force
            }
        }
        foreach ($entry in $slotFilesBefore.GetEnumerator()) {
            [IO.File]::WriteAllBytes(
                (Join-Path $PersistentDataDirectory $entry.Key),
                $entry.Value)
        }
    }

    [IO.File]::WriteAllBytes($settingsPath, $settingsOriginal)
    if ($settingsBackupOriginallyExisted) {
        [IO.File]::WriteAllBytes(
            $settingsBackupPath,
            $settingsBackupOriginal)
    }
    elseif (Test-Path -LiteralPath $settingsBackupPath) {
        Remove-Item -LiteralPath $settingsBackupPath -Force
    }

    if ($modsMoved) {
        $isolatedTas = Join-Path $modsDirectory 'HollowKnightTAS'
        if (Test-Path -LiteralPath $isolatedTas) {
            Move-T24PathWithRetry `
                -Source $isolatedTas `
                -Destination (Join-Path $modsBackup 'HollowKnightTAS')
        }
        if (Test-Path -LiteralPath $modsDirectory) {
            Move-T24PathWithRetry `
                -Source $modsDirectory `
                -Destination $modsIsolated
            $isolationMoved = $true
        }
        Move-T24PathWithRetry `
            -Source $modsBackup `
            -Destination $modsDirectory
        $modsMoved = $false
    }
    if ($isolationMoved -and (Test-Path -LiteralPath $modsIsolated)) {
        $isolatedFull = [IO.Path]::GetFullPath($modsIsolated)
        if (-not $isolatedFull.StartsWith(
                $managedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing isolated Mods cleanup outside Managed.'
        }
        Remove-Item -LiteralPath $isolatedFull -Recurse -Force
    }

    if ($automationIsolationEstablished `
            -and (Test-Path -LiteralPath $automationRoot)) {
        $automationFull = [IO.Path]::GetFullPath($automationRoot)
        if (-not $automationFull.StartsWith(
                $localPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing automation cleanup outside LocalApplicationData.'
        }
        Remove-Item -LiteralPath $automationFull -Recurse -Force
    }
        if ($automationMoved) {
            Move-T24PathWithRetry `
                -Source $automationBackup `
                -Destination $automationRoot
            $automationMoved = $false
        }
    }
    catch {
        $cleanupFailure = $_
    }
}

$cleanupError = if ($null -eq $cleanupFailure) {
    ''
}
else {
    [string]$cleanupFailure.Exception.Message
}
$modsDirectoryExistsAfter = Test-Path `
    -LiteralPath $modsDirectory `
    -PathType Container
$tasInstallExistsAfter = Test-Path `
    -LiteralPath $tasInstall `
    -PathType Container
$modsTopLevelFingerprintAfter = ''
$tasInstallFingerprintAfter = ''
$nestedTasDirectoryCount = -1
try {
    if ($modsDirectoryExistsAfter) {
        $modsTopLevelFingerprintAfter = Get-T24TopLevelFingerprint `
            -Root $modsDirectory
    }
    if ($tasInstallExistsAfter) {
        $tasInstallFingerprintAfter = Get-T24DirectoryFingerprint `
            -Root $tasInstall
        $nestedTasDirectoryCount = @(
            Get-ChildItem `
                -LiteralPath $tasInstall `
                -Directory `
                -Recurse `
                -Force |
                Where-Object Name -eq 'HollowKnightTAS'
        ).Count
    }
}
catch {
    $cleanupError = $_.Exception.Message
}
$modsBackupAbsent = -not (Test-Path -LiteralPath $modsBackup)
$modsIsolatedAbsent = -not (Test-Path -LiteralPath $modsIsolated)
$observerInstallExistsAfter = Test-Path `
    -LiteralPath $observerInstall `
    -PathType Container
$observerInstallFingerprintAfter = if ($observerInstallExistsAfter) {
    Get-T24DirectoryFingerprint -Root $observerInstall
}
else { '' }
$observerInstallEquivalent = if ($observerInstallOriginallyExisted) {
    $observerInstallExistsAfter `
        -and [string]::Equals(
            $observerInstallFingerprintBefore,
            $observerInstallFingerprintAfter,
            [StringComparison]::Ordinal)
}
else { -not $observerInstallExistsAfter }
$automationBackupAbsent = -not (Test-Path -LiteralPath $automationBackup)
$modsTopLevelEquivalent = [string]::Equals(
    $modsTopLevelFingerprintBefore,
    $modsTopLevelFingerprintAfter,
    [StringComparison]::Ordinal)
$tasInstallEquivalent = [string]::Equals(
    $tasInstallFingerprintBefore,
    $tasInstallFingerprintAfter,
    [StringComparison]::Ordinal)
$cleanupVerdict = if (
    [string]::IsNullOrEmpty($cleanupError) `
        -and $modsDirectoryExistsAfter `
        -and $tasInstallExistsAfter `
        -and $modsBackupAbsent `
        -and $modsIsolatedAbsent `
        -and $observerInstallEquivalent `
        -and $automationBackupAbsent `
        -and $modsTopLevelEquivalent `
        -and $tasInstallEquivalent `
        -and $nestedTasDirectoryCount -eq 0) {
    'PASS'
}
else {
    'FAIL'
}
[ordered]@{
    schemaVersion = 1
    verdict = $cleanupVerdict
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    modsDirectory = $modsDirectory
    modsDirectoryExistsAfter = $modsDirectoryExistsAfter
    tasInstall = $tasInstall
    tasInstallExistsAfter = $tasInstallExistsAfter
    modsBackupAbsent = $modsBackupAbsent
    modsIsolatedAbsent = $modsIsolatedAbsent
    observerInstallOriginallyExisted = $observerInstallOriginallyExisted
    observerInstallExistsAfter = $observerInstallExistsAfter
    observerInstallFingerprintBefore = $observerInstallFingerprintBefore
    observerInstallFingerprintAfter = $observerInstallFingerprintAfter
    observerInstallEquivalent = $observerInstallEquivalent
    automationBackupAbsent = $automationBackupAbsent
    modsTopLevelFingerprintBefore = $modsTopLevelFingerprintBefore
    modsTopLevelFingerprintAfter = $modsTopLevelFingerprintAfter
    modsTopLevelEquivalent = $modsTopLevelEquivalent
    tasInstallFingerprintBefore = $tasInstallFingerprintBefore
    tasInstallFingerprintAfter = $tasInstallFingerprintAfter
    tasInstallEquivalent = $tasInstallEquivalent
    nestedTasDirectoryCount = $nestedTasDirectoryCount
    error = $cleanupError
} |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $cleanupAuditPath -Encoding utf8NoBOM
if ($null -ne $runFailure) {
    throw $runFailure
}
if ($null -ne $cleanupFailure) {
    throw $cleanupFailure
}
if ($cleanupVerdict -ne 'PASS') {
    throw "T24 candidate cleanup audit failed: $cleanupAuditPath"
}

Write-Output "T24 candidate smoke completed: $EvidenceRoot"
