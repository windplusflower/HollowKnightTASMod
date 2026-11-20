[CmdletBinding()]
param(
    [ValidateSet('vanilla-reference', 'tas-passive')]
    [string]$Mode = 'vanilla-reference',

    [ValidateRange(120, 10000)]
    [int]$MaxTicks = 1200,

    [ValidateRange(1, 4)]
    [int]$FixtureSlot = 2,

    [string]$ManagedDirectory =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed',

    [string]$SteamExecutable =
        'C:\Program Files (x86)\Steam\steam.exe',

    [string]$PersistentDataDirectory =
        'C:\Users\33361\AppData\LocalLow\Team Cherry\Hollow Knight',

    [string]$EvidenceRoot = '',

    [Parameter(Mandatory = $true)]
    [string]$ClockBundleRoot,

    [ValidateRange(30, 240)]
    [int]$MaxLaunchSeconds = 180,

    [ValidateRange(60, 300)]
    [int]$FixtureReadyTimeoutSeconds = 240,

    [string]$PhysicalInputTracePath = '',

    [string]$ScenarioContractPath = '',

    [ValidateRange(0, 60000)]
    [int]$PreClockMenuDwellMilliseconds = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$clockHarnessPath = Join-Path $PSScriptRoot 'T24ClockHarness.ps1'
. $clockHarnessPath
$ManagedDirectory = [IO.Path]::GetFullPath($ManagedDirectory)
$GameExecutable = Join-Path `
    (Split-Path -Parent (Split-Path -Parent $ManagedDirectory)) `
    'hollow_knight.exe'
$ClockBundleRoot = [IO.Path]::GetFullPath($ClockBundleRoot)
if (-not [string]::IsNullOrWhiteSpace($PhysicalInputTracePath)) {
    $PhysicalInputTracePath =
        [IO.Path]::GetFullPath($PhysicalInputTracePath)
    if (-not (Test-Path `
            -LiteralPath $PhysicalInputTracePath `
            -PathType Leaf)) {
        throw "Physical input trace is missing: $PhysicalInputTracePath"
    }
}
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
    if ([string]$scenarioContract['scenarioId'] -cnotmatch '\At24\.[a-z0-9-]+\.v[1-9][0-9]*\z') {
        throw 'scenarioId must match t24.<lowercase-id>.v<positive-integer>.'
    }
    if ([int]$scenarioContract['fixture']['slot'] -ne $FixtureSlot `
            -or [int]$scenarioContract['movie']['expandedTicks'] `
                -ne $MaxTicks) {
        throw 'Scenario contract fixture slot or expanded ticks mismatch.'
    }
}
$modsDirectory = Join-Path $ManagedDirectory 'Mods'
$tasInstall = Join-Path $modsDirectory 'HollowKnightTAS'
$PersistentDataDirectory =
    [IO.Path]::GetFullPath($PersistentDataDirectory)
$observerBuild = Join-Path `
    $repoRoot `
    'src\HollowKnightTAS.ReferenceObserver\bin\Debug'
$observerFiles = @(
    'HollowKnightTAS.ReferenceObserver.dll',
    'HollowKnightTAS.GameObservation.dll',
    'HollowKnightTAS.Core.dll'
)

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $campaign = 't24-{0}-smoke-{1}-{2}' -f `
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

foreach ($path in @(
        $ManagedDirectory,
        $modsDirectory,
        $PersistentDataDirectory,
        $observerBuild,
        $ClockBundleRoot
    )) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required directory is missing: $path"
    }
}
foreach ($file in $observerFiles) {
    $path = Join-Path $observerBuild $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Reference observer build output is missing: $path"
    }
}
if (-not (Test-Path -LiteralPath $GameExecutable -PathType Leaf)) {
    throw "Hollow Knight executable is missing: $GameExecutable"
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
if (-not $modsDirectory.StartsWith(
        $managedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Mods directory is outside the intended Managed directory.'
}
$persistentPrefix = $PersistentDataDirectory.TrimEnd('\') + '\'
$evidenceParent = [IO.Path]::GetFullPath(
    (Split-Path -Parent $EvidenceRoot))
if (-not $EvidenceRoot.StartsWith(
        $evidenceParent.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence root validation failed.'
}

New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$referenceDirectory = Join-Path $EvidenceRoot "traces\$Mode"
$observerAuditDirectory = Join-Path $EvidenceRoot 'observer-audit'
$preClockMenuDwellAuditPath = Join-Path `
    $observerAuditDirectory `
    'pre-clock-menu-dwell.json'
$physicalInputEquivalenceAuditPath = Join-Path `
    $observerAuditDirectory `
    'physical-input-equivalence.json'
New-Item -ItemType Directory -Path $referenceDirectory -Force |
    Out-Null
New-Item -ItemType Directory -Path $observerAuditDirectory -Force |
    Out-Null

$runId = '{0}-{1}' -f `
    $Mode, `
    [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$inputSyncPrefix = "HollowKnightTAS.T24.InputSync.$runId"
$inputSyncStart = if ([string]::IsNullOrWhiteSpace(
        $PhysicalInputTracePath)) {
    $null
}
else {
    [Threading.EventWaitHandle]::new(
        $false,
        [Threading.EventResetMode]::ManualReset,
        $inputSyncPrefix + '.start')
}
$inputSyncFrameReady = if ($null -eq $inputSyncStart) {
    $null
}
else {
    [Threading.EventWaitHandle]::new(
        $false,
        [Threading.EventResetMode]::AutoReset,
        $inputSyncPrefix + '.frame-ready')
}
$inputSyncApplied = if ($null -eq $inputSyncStart) {
    $null
}
else {
    [Threading.EventWaitHandle]::new(
        $false,
        [Threading.EventResetMode]::AutoReset,
        $inputSyncPrefix + '.input-applied')
}
$recordingArmRelease = [Threading.EventWaitHandle]::new(
    $false,
    [Threading.EventResetMode]::ManualReset,
    "HollowKnightTAS.T24.RecordingArm.$runId")
$encodedOutput = [Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes($referenceDirectory))
$swapId = [Guid]::NewGuid().ToString('N')
$modsBackup = Join-Path `
    $ManagedDirectory `
    "Mods.HKTAS-T24-$swapId.backup"
$modsIsolated = Join-Path `
    $ManagedDirectory `
    "Mods.HKTAS-T24-$swapId.isolated"
$observerInstall = Join-Path `
    $modsDirectory `
    'HollowKnightTAS.ReferenceObserver'
$slotPattern = '^user{0}(?:[._].*)?$' -f $FixtureSlot
$slotFilesBefore = @{}
$slotNamesBefore = @()
$slotSnapshotComplete = $false
$settingsPath = Join-Path `
    $PersistentDataDirectory `
    'HollowKnightTASMod.GlobalSettings.json'
$settingsBackupPath = $settingsPath + '.bak'
$settingsOriginallyExisted =
    Test-Path -LiteralPath $settingsPath -PathType Leaf
$settingsOriginal = if ($settingsOriginallyExisted) {
    [IO.File]::ReadAllBytes($settingsPath)
}
else {
    $null
}
$settingsBackupOriginallyExisted =
    Test-Path -LiteralPath $settingsBackupPath -PathType Leaf
$settingsBackupOriginal = if ($settingsBackupOriginallyExisted) {
    [IO.File]::ReadAllBytes($settingsBackupPath)
}
else {
    $null
}
$game = $null
$modsMoved = $false
$isolationMoved = $false
$script:resolvedKeys = [Collections.Generic.HashSet[byte]]::new()

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class HktasPhysicalKeyboard
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

    [HktasPhysicalKeyboard]::Down($VirtualKey)
    Start-Sleep -Milliseconds $HoldMilliseconds
    [HktasPhysicalKeyboard]::Up($VirtualKey)
}

function Release-GameplayKeys {
    foreach ($key in @(
            [byte[]]@(0x25, 0x26, 0x27, 0x28, 0x58, 0x5A, 0x41, 0x44, 0x53, 0x57),
            [byte[]]@($script:resolvedKeys)
        ) | ForEach-Object { $_ }) {
        [HktasPhysicalKeyboard]::Up($key)
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
    $path = Join-Path $referenceDirectory 'status.json'
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
        [ValidateRange(5, 300)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        $last = Get-ObserverStatus
        if ($null -ne $last -and $Allowed -contains [string]$last.phase) {
            return $last
        }
        Start-Sleep -Milliseconds 100
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
        if ([HktasPhysicalKeyboard]::Focus(
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

    # Menu_Title is reached without skipping gameplay. Repeated Up presses
    # normalize the selection to the first visible option without relying on
    # pixels or OCR, then Enter opens the save-slot list.
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

    # Navigate by the target build's selected SaveSlotButton index, not by a
    # guessed starting selection or pixels.
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
    throw "Reference observer did not reach Menu_Title; last=$lastText"
}

function Invoke-PhysicalRegressionInput {
    Focus-Game
    $recordingArmRelease.Set() | Out-Null
    $status = Get-ObserverStatus
    $rightKey = Resolve-VirtualKey -Bindings ([string]$status.rightBindings)
    $leftKey = Resolve-VirtualKey -Bindings ([string]$status.leftBindings)
    $jumpKey = Resolve-VirtualKey -Bindings ([string]$status.jumpBindings)
    $attackKey = Resolve-VirtualKey -Bindings ([string]$status.attackBindings)
    # The observer records the actually committed HeroActions; the later TAS
    # candidate is generated from those committed values, not wall-clock
    # assumptions in this test driver.
    if ([string]$status.benchFsmState -ne 'Resting' `
            -or -not [bool]$status.benchSleeping `
            -or [string]$status.heroAnimationClip -ne 'Sit Fall Asleep' `
            -or [int]$status.heroAnimationFrame -ne 2) {
        throw (
            'Vanilla fixture was not at the committed seated boundary: ' `
            + ($status | ConvertTo-Json -Compress)
        )
    }

    # The save-slot load path itself owns Init Resting -> Startle -> Resting
    # and the later vanilla Fall Asleep cycle. Begin the logical timeline at
    # the stable sleeping Resting boundary so the fresh edge must traverse
    # the original Wake? animation before Get Off.
    Invoke-KeyTap -VirtualKey $rightKey -HoldMilliseconds 90

    $wakeDeadline = [DateTimeOffset]::UtcNow.AddSeconds(12)
    do {
        $status = Get-ObserverStatus
        if ($null -ne $status `
                -and [string]$status.phase -eq 'recording' `
                -and -not [bool]$status.atBench `
                -and [bool]$status.heroAcceptingInput `
                -and [string]$status.heroAnimationClip `
                    -in @('Idle', 'Run')) {
            break
        }
        Start-Sleep -Milliseconds 20
    } while ([DateTimeOffset]::UtcNow -lt $wakeDeadline)
    if ($null -eq $status `
            -or [bool]$status.atBench `
            -or -not [bool]$status.heroAcceptingInput `
            -or [string]$status.heroAnimationClip `
                -notin @('Idle', 'Run')) {
        throw (
            'Vanilla bench exit did not reach controllable Idle/Run: ' `
            + ($status | ConvertTo-Json -Compress)
        )
    }
    Start-Sleep -Milliseconds 150

    Invoke-KeyTap -VirtualKey $attackKey -HoldMilliseconds 70
    Start-Sleep -Milliseconds 200

    [HktasPhysicalKeyboard]::Down($leftKey)
    Start-Sleep -Milliseconds 40
    Invoke-KeyTap -VirtualKey $attackKey -HoldMilliseconds 70
    Start-Sleep -Milliseconds 40
    [HktasPhysicalKeyboard]::Up($leftKey)
    Start-Sleep -Milliseconds 180

    [HktasPhysicalKeyboard]::Down($jumpKey)
    Start-Sleep -Milliseconds 120
    [HktasPhysicalKeyboard]::Up($jumpKey)
    Start-Sleep -Milliseconds 70
    [HktasPhysicalKeyboard]::Down($jumpKey)
    Start-Sleep -Milliseconds 220
    [HktasPhysicalKeyboard]::Up($jumpKey)
}

function Get-SynchronizedHeldSchedule {
    if ([string]::IsNullOrWhiteSpace($PhysicalInputTracePath)) {
        return @()
    }

    $lines = @([IO.File]::ReadAllLines($PhysicalInputTracePath))
    if ($lines.Count -ne $MaxTicks) {
        throw (
            'Synchronized physical-input trace length mismatch: expected=' `
            + $MaxTicks `
            + '; actual=' `
            + $lines.Count)
    }
    $schedule = [Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $frame = $lines[$index] | ConvertFrom-Json
        if ([int]$frame.schemaVersion -ne 1 `
                -or [long]$frame.logicalTick -ne $index) {
            throw "Synchronized input trace logical tick is invalid at $index."
        }
        $fields = @{}
        foreach ($field in $frame.fields) {
            $fields[[string]$field.key] = $field
        }
        foreach ($key in @(
            'input.axisX',
            'input.axisY',
            'input.held',
            'input.pressed',
            'input.released')) {
            if (-not $fields.ContainsKey($key) `
                    -or [string]$fields[$key].kind -ne 'Int32' `
                    -or [string]$fields[$key].canonicalHex `
                        -notmatch '^[0-9a-f]{8}$') {
                throw "Synchronized input field is invalid at tick ${index}: $key"
            }
        }
        $held = [int64][Convert]::ToUInt32(
            [string]$fields['input.held'].canonicalHex,
            16)
        $pressed = [int64][Convert]::ToUInt32(
            [string]$fields['input.pressed'].canonicalHex,
            16)
        $released = [int64][Convert]::ToUInt32(
            [string]$fields['input.released'].canonicalHex,
            16)
        # Only held drives the external OS keyboard schedule. The source trace
        # carries mechanically derived pressed/released fields for InputSample
        # schema compatibility, but they are not authoritative physical input.
        # Hollow Knight's original PlayerAction update may suppress an edge
        # while resetting/gating an action set (for example during damage stun).
        # The observer records those committed edges; Candidate-vs-Reference
        # comparison later requires the two original-game results to be exact.
        if (($held -band (-bnot 0x7ffL)) -ne 0 `
                -or ($pressed -band (-bnot 0x7ffL)) -ne 0 `
                -or ($released -band (-bnot 0x7ffL)) -ne 0) {
            throw (
                'Synchronized trace contains unsupported or inconsistent ' `
                + "input at tick $index.")
        }
        $schedule.Add([int]$held)
    }
    return @($schedule)
}

function Invoke-SynchronizedPhysicalRegressionInput {
    param(
        [Parameter(Mandatory)][int[]]$HeldSchedule,
        [Parameter(Mandatory)][object]$StatusSnapshot
    )

    if ($null -eq $inputSyncStart `
            -or $null -eq $inputSyncFrameReady `
            -or $null -eq $inputSyncApplied `
            -or $null -eq $recordingArmRelease) {
        throw 'Synchronized input events are unavailable.'
    }
    $keys = @(
        [pscustomobject]@{
            bit = 0x01
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.leftBindings)
        },
        [pscustomobject]@{
            bit = 0x02
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.rightBindings)
        },
        [pscustomobject]@{
            bit = 0x04
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.menuUpBindings)
        },
        [pscustomobject]@{
            bit = 0x08
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.menuDownBindings)
        },
        [pscustomobject]@{
            bit = 0x10
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.jumpBindings)
        },
        [pscustomobject]@{
            bit = 0x20
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.attackBindings)
        },
        [pscustomobject]@{
            bit = 0x40
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.dashBindings)
        },
        [pscustomobject]@{
            bit = 0x80
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.castBindings)
        },
        [pscustomobject]@{
            bit = 0x100
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.quickCastBindings)
        },
        [pscustomobject]@{
            bit = 0x200
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.superDashBindings)
        },
        [pscustomobject]@{
            bit = 0x400
            key = Resolve-VirtualKey `
                -Bindings ([string]$StatusSnapshot.dreamNailBindings)
        })
    foreach ($entry in $keys) {
        [void]$script:resolvedKeys.Add([byte]$entry.key)
    }

    Focus-Game
    # Establish a neutral OS state before activating the observer. The first
    # named handshake is an unrecorded pre-roll: it posts schedule[0] while
    # Unity's main thread is blocked after this frame's message pump. The next
    # PlayerLoop therefore observes schedule[0], including its pressed edges,
    # as logical tick 0. Later handshakes post the following logical tick.
    foreach ($entry in $keys) {
        [HktasPhysicalKeyboard]::Up([byte]$entry.key)
    }
    $current = 0
    $recordingArmRelease.Set() | Out-Null
    $inputSyncStart.Set() | Out-Null
    try {
        if (-not $inputSyncFrameReady.WaitOne(
                [TimeSpan]::FromSeconds(10))) {
            throw 'Observer input-sync pre-roll timeout.'
        }
        $desired = $HeldSchedule[0]
        foreach ($entry in $keys) {
            if (($desired -band [int]$entry.bit) -ne 0) {
                [HktasPhysicalKeyboard]::Down([byte]$entry.key)
            }
        }
        $current = $desired
        $inputSyncApplied.Set() | Out-Null

        for ($index = 0; $index -lt $HeldSchedule.Count; $index++) {
            if (-not $inputSyncFrameReady.WaitOne(
                    [TimeSpan]::FromSeconds(10))) {
                throw "Observer input-sync frame timeout at tick $index."
            }
            $desired = if ($index + 1 -lt $HeldSchedule.Count) {
                $HeldSchedule[$index + 1]
            }
            else {
                0
            }
            foreach ($entry in $keys) {
                $wasHeld = ($current -band [int]$entry.bit) -ne 0
                $isHeld = ($desired -band [int]$entry.bit) -ne 0
                if ($wasHeld -and -not $isHeld) {
                    [HktasPhysicalKeyboard]::Up([byte]$entry.key)
                }
            }
            foreach ($entry in $keys) {
                $wasHeld = ($current -band [int]$entry.bit) -ne 0
                $isHeld = ($desired -band [int]$entry.bit) -ne 0
                if (-not $wasHeld -and $isHeld) {
                    [HktasPhysicalKeyboard]::Down([byte]$entry.key)
                }
            }
            $current = $desired
            $inputSyncApplied.Set() | Out-Null
        }
    }
    finally {
        foreach ($entry in $keys) {
            [HktasPhysicalKeyboard]::Up([byte]$entry.key)
        }
        $inputSyncApplied.Set() | Out-Null
    }
}

function Get-CanonicalPhysicalControlTimeline {
    param([Parameter(Mandatory)][string]$Path)

    $inputKeys = @(
        'input.axisX',
        'input.axisY',
        'input.held')
    $timeline = [Collections.Generic.List[string]]::new()
    $logicalTick = 0
    foreach ($line in [IO.File]::ReadLines($Path)) {
        $frame = $line | ConvertFrom-Json
        if ([int]$frame.schemaVersion -ne 1 `
                -or [long]$frame.logicalTick -ne $logicalTick) {
            throw "Input trace logical tick is invalid at $logicalTick."
        }
        $fields = @{}
        foreach ($field in @($frame.fields)) {
            $fields[[string]$field.key] = $field
        }
        $canonical = [ordered]@{ logicalTick = $logicalTick }
        foreach ($key in $inputKeys) {
            if (-not $fields.ContainsKey($key) `
                    -or [string]$fields[$key].kind -ne 'Int32' `
                    -or [string]$fields[$key].canonicalHex `
                        -notmatch '^[0-9a-f]{8}$') {
                throw "Input trace field is invalid: $key at $logicalTick."
            }
            $canonical[$key] = [string]$fields[$key].canonicalHex
        }
        $timeline.Add(($canonical | ConvertTo-Json -Compress))
        $logicalTick++
    }
    return @($timeline)
}

function Test-CapturedInputTimeline {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$CapturedPath,
        [Parameter(Mandatory)][string]$AuditPath
    )

    $source = @(Get-CanonicalPhysicalControlTimeline -Path $SourcePath)
    $captured = @(Get-CanonicalPhysicalControlTimeline -Path $CapturedPath)
    $limit = [Math]::Min($source.Count, $captured.Count)
    $firstDifference = $null
    for ($index = 0; $index -lt $limit; $index++) {
        if ($source[$index] -cne $captured[$index]) {
            $firstDifference = [ordered]@{
                logicalTick = $index
                source = $source[$index] | ConvertFrom-Json
                captured = $captured[$index] | ConvertFrom-Json
            }
            break
        }
    }
    if ($null -eq $firstDifference `
            -and $source.Count -ne $captured.Count) {
        $firstDifference = [ordered]@{
            logicalTick = $limit
            sourceFrameCount = $source.Count
            capturedFrameCount = $captured.Count
        }
    }
    $equivalent = $null -eq $firstDifference
    [ordered]@{
        schemaVersion = 1
        policyId = 'captured-vs-source-physical-control-signal-v2'
        verdict = if ($equivalent) { 'PASS' } else { 'FAIL' }
        sourceTrace = [IO.Path]::GetFullPath($SourcePath)
        sourceTraceSha256 = (Get-FileHash `
            -LiteralPath $SourcePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        capturedTrace = [IO.Path]::GetFullPath($CapturedPath)
        capturedTraceSha256 = (Get-FileHash `
            -LiteralPath $CapturedPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceFrameCount = $source.Count
        capturedFrameCount = $captured.Count
        comparedKeys = @(
            'input.axisX',
            'input.axisY',
            'input.held')
        observedOnlyKeys = @(
            'input.pressed',
            'input.released')
        observedOnlyPolicy =
            'committed-by-original-player-action-and-compared-in-reference-candidate-trace-v1'
        firstDifference = $firstDifference
    } |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $AuditPath -Encoding utf8NoBOM
    return $equivalent
}

function Get-TraceSceneTimelineAudit {
    param([Parameter(Mandatory)][string]$Path)

    $scenePattern = [regex]::new(
        '"key":"scene\.name","kind":"Utf8String",' `
        + '"canonicalHex":"[0-9a-f]*",' `
        + '"displayValue":"(?<scene>[^"]*)","comparable":true')
    $frameCount = 0
    $transitionCount = 0
    $firstScene = ''
    $lastScene = ''
    foreach ($line in [IO.File]::ReadLines($Path)) {
        $match = $scenePattern.Match($line)
        if (-not $match.Success) {
            throw "Trace frame $frameCount has no canonical scene.name field."
        }
        $scene = [string]$match.Groups['scene'].Value
        if ($frameCount -eq 0) {
            $firstScene = $scene
        }
        elseif ($scene -cne $lastScene) {
            $transitionCount++
        }
        $lastScene = $scene
        $frameCount++
    }

    [pscustomobject]@{
        frameCount = $frameCount
        transitionCount = $transitionCount
        firstScene = $firstScene
        lastScene = $lastScene
    }
}

function Write-PassiveSettings {
    if (-not $settingsOriginallyExisted) {
        throw 'TasPassive requires an existing TAS settings file to restore.'
    }

    $settings = Get-Content -LiteralPath $settingsPath -Raw |
        ConvertFrom-Json -AsHashtable -Depth 50
    $settings['VerificationModeRequested'] = $false
    $settings['CompanionEnabled'] = $false
    $settings['AutoStartCompanion'] = $false
    $settings['ExitCompanionWithGame'] = $true
    $settings['CompanionOverlayEnabled'] = $false
    $settings['EnableNativeCapabilities'] = $false
    $settings['EnableSemanticKeyframes'] = $false
    $settings['InspectorEnabled'] = $false
    $settings['InspectorOverlayEnabled'] = $false
    $settings['InspectorExportEnabled'] = $false
    $settings['ReplaySaveEnabled'] = $false
    $settings['ReplaySaveAutoEnabled'] = $false
    $settings['ReplaySaveDeterministicTimingEnabled'] = $false
    $settings['ReplayDeterministicRngEnabled'] = $false
    $settings['DedicatedTasSaveSlot'] = $FixtureSlot
    $settings['ExternalAutomationMode'] = 'ReadOnly'
    $settings['DebugMutationEnabled'] = $false
    $settings |
        ConvertTo-Json -Depth 50 |
        Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM
}

$synchronizedHeldSchedule = @(Get-SynchronizedHeldSchedule)
if ($synchronizedHeldSchedule.Count -ne 0) {
    [ordered]@{
        schemaVersion = 1
        mode = 'external-os-keyboard-frame-handshake-preroll-v2'
        sourceTrace = $PhysicalInputTracePath
        sourceTraceSha256 = (Get-FileHash `
            -LiteralPath $PhysicalInputTracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        frameCount = $synchronizedHeldSchedule.Count
        primeFrameCount = 1
        supportedActionMask = 0x7ff
        logicalEdgePolicy =
            'committed-by-original-player-action-not-source-authoritative-v2'
        authoritativeControlFields = @(
            'input.axisX',
            'input.axisY',
            'input.held')
        observedResultFields = @(
            'input.pressed',
            'input.released')
        observerWritesInput = $false
        visualRecognitionUsed = $false
    } |
        ConvertTo-Json -Depth 10 |
        Set-Content `
            -LiteralPath (
                Join-Path `
                    $observerAuditDirectory `
                    'external-input-sync.json') `
            -Encoding utf8NoBOM
}

try {
    foreach ($file in Get-ChildItem `
            -LiteralPath $PersistentDataDirectory `
            -File `
            -Force |
        Where-Object { $_.Name -match $slotPattern }) {
        $slotFilesBefore[$file.Name] = [IO.File]::ReadAllBytes($file.FullName)
    }
    $slotNamesBefore = @($slotFilesBefore.Keys)
    $slotSnapshotComplete = $true

    foreach ($unexpected in @($modsBackup, $modsIsolated)) {
        if (Test-Path -LiteralPath $unexpected) {
            throw "Unexpected recovery path exists: $unexpected"
        }
    }
    Move-Item -LiteralPath $modsDirectory -Destination $modsBackup
    $modsMoved = $true
    New-Item -ItemType Directory -Path $modsDirectory | Out-Null
    if ($Mode -eq 'tas-passive') {
        Move-Item `
            -LiteralPath (Join-Path $modsBackup 'HollowKnightTAS') `
            -Destination $tasInstall
        Write-PassiveSettings
    }
    New-Item -ItemType Directory -Path $observerInstall | Out-Null
    foreach ($file in $observerFiles) {
        Copy-Item `
            -LiteralPath (Join-Path $observerBuild $file) `
            -Destination (Join-Path $observerInstall $file)
    }

    Get-ChildItem -LiteralPath $modsDirectory -File -Recurse |
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
            -LiteralPath (Join-Path $observerAuditDirectory 'assemblies.json') `
            -Encoding utf8NoBOM

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
        '--hktas-reference-exit'
    )
    if ($synchronizedHeldSchedule.Count -ne 0) {
        $arguments += '--hktas-reference-require-external-input-sync'
    }
    $startupLaunch = Start-T24StartupClockedGame `
        -GameExecutable $GameExecutable `
        -GameArguments $arguments `
        -BundleRoot $ClockBundleRoot `
        -EvidenceDirectory $observerAuditDirectory
    $game = $startupLaunch.GameProcess
    $script:game = $game
    $clockAudit = $startupLaunch.Audit

    $launchDeadline =
        [DateTimeOffset]::UtcNow.AddSeconds($MaxLaunchSeconds)
    $menuReadyStatus = $null
    do {
        $status = Get-ObserverStatus
        if ($null -ne $game `
                -and $null -ne $status `
                -and [string]$status.scene -eq 'Menu_Title' `
                -and (Test-ObserverMenuControlsReady -Status $status)) {
            $menuReadyStatus = $status
            break
        }
        if ($null -ne $game -and $game.HasExited) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $launchDeadline)

    if ($null -eq $game) {
        throw 'Hollow Knight did not start.'
    }
    if ($null -eq $menuReadyStatus) {
        throw 'Reference observer did not reach Menu_Title.'
    }
    $status = $menuReadyStatus

    $dwellStartedUtc = [DateTimeOffset]::UtcNow
    $dwellStopwatch = [Diagnostics.Stopwatch]::StartNew()
    if ($PreClockMenuDwellMilliseconds -gt 0) {
        Start-Sleep -Milliseconds $PreClockMenuDwellMilliseconds
        while ($dwellStopwatch.Elapsed.TotalMilliseconds `
                -lt $PreClockMenuDwellMilliseconds) {
            Start-Sleep -Milliseconds 1
        }
    }
    $dwellStopwatch.Stop()
    $statusAfterDwell = Wait-ObserverMenuControlsStatus -TimeoutSeconds 5
    $dwellCompletedUtc = [DateTimeOffset]::UtcNow
    $preClockMenuDwellActualMilliseconds = [Math]::Round(
        $dwellStopwatch.Elapsed.TotalMilliseconds,
        3)
    [ordered]@{
        schemaVersion = 1
        policyId = 'wall-clock-wait-in-menu-title-v1'
        operation = 'external-wall-clock-wait-only'
        requestedMilliseconds = $PreClockMenuDwellMilliseconds
        actualElapsedMilliseconds =
            $preClockMenuDwellActualMilliseconds
        startedUtc = $dwellStartedUtc.ToString('O')
        completedUtc = $dwellCompletedUtc.ToString('O')
        sceneBefore = [string]$status.scene
        sceneAfter = if ($null -eq $statusAfterDwell) {
            ''
        }
        else { [string]$statusAfterDwell.scene }
        statusBefore = $status
        statusAfter = $statusAfterDwell
        inputInjected = $false
        timeWritten = $false
        gameplayStateWritten = $false
        visualRecognitionUsed = $false
    } |
        ConvertTo-Json -Depth 30 |
        Set-Content `
            -LiteralPath $preClockMenuDwellAuditPath `
            -Encoding utf8NoBOM
    if ($null -eq $statusAfterDwell `
            -or [string]$statusAfterDwell.scene -ne 'Menu_Title') {
        throw 'Reference left Menu_Title during the pre-clock dwell.'
    }

    [void](Wait-T24ClockProfile `
        -GetStatus { Get-ObserverStatus } `
        -TimeoutSeconds 15)
    Start-Sleep -Seconds 3
    Invoke-ExternalUiSlotLoad
    [void](Wait-ObserverPhase `
        -Allowed @('waiting-for-recording-arm-release') `
        -TimeoutSeconds $FixtureReadyTimeoutSeconds)
    $clockStatus = Wait-T24ClockProfile `
        -GetStatus { Get-ObserverStatus } `
        -TimeoutSeconds 15
    $clockStatus |
        ConvertTo-Json -Depth 20 |
        Set-Content `
            -LiteralPath (
                Join-Path $observerAuditDirectory 'clock-profile.json') `
            -Encoding utf8NoBOM
    if ($synchronizedHeldSchedule.Count -eq 0) {
        Invoke-PhysicalRegressionInput
    }
    else {
        Invoke-SynchronizedPhysicalRegressionInput `
            -HeldSchedule $synchronizedHeldSchedule `
            -StatusSnapshot $clockStatus
    }
    $terminal = Wait-ObserverPhase `
        -Allowed @('complete', 'failed') `
        -TimeoutSeconds 90
    if ([string]$terminal.phase -ne 'complete') {
        throw "Reference observer failed: $($terminal.error)"
    }

    $resultPath = Join-Path $referenceDirectory 'result.json'
    $tracePath = Join-Path $referenceDirectory 'trace.jsonl'
    $baselinePath = Join-Path $referenceDirectory 'baseline.json'
    $physicalInputEquivalent = if (
        $synchronizedHeldSchedule.Count -eq 0) {
        $true
    }
    else {
        Test-CapturedInputTimeline `
            -SourcePath $PhysicalInputTracePath `
            -CapturedPath $tracePath `
            -AuditPath $physicalInputEquivalenceAuditPath
    }
    if (-not $physicalInputEquivalent) {
        throw (
            'Captured physical input differs from the source timeline: ' `
            + $physicalInputEquivalenceAuditPath)
    }
    $result = Get-Content -LiteralPath $resultPath -Raw |
        ConvertFrom-Json
    $baseline = if (Test-Path -LiteralPath $baselinePath -PathType Leaf) {
        Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    $expectedRuntimeVirtualClockRegistration =
        $Mode -eq 'tas-passive'
    $recordingRootFramePhase =
        [int]$result.recordingArmBoundaryFramePhase
    $recordingPhaseNormalized =
        -not [bool]$result.externalRecordingPhaseNormalizationActive `
        -and [bool]$result.externalRecordingPhaseNormalizationCompleted `
        -and [int]$result.externalRecordingPhaseNormalizationBeginCount -eq 1 `
        -and [int]$result.externalRecordingPhaseNormalizationHoldFrameCount `
            -ge 1 `
        -and [int]$result.externalRecordingPhaseNormalizationHoldFrameCount `
            -le 5 `
        -and [int]$result.externalRecordingPhaseNormalizationReleaseCount -eq 1 `
        -and [int]$result.externalRecordingPhaseNormalizationFaultCode -eq 0 `
        -and [int]$result.externalRecordingPhaseNormalizationLastFrameCount `
            -gt 0 `
        -and [string]$result.externalRecordingPhaseNormalizationHeldTimeBits `
            -match '^[0-9a-f]{8}$' `
        -and [string]$result.externalRecordingPhaseNormalizationHeldTimeBits `
            -ne '00000000' `
        -and [string]$result.externalRecordingPhaseNormalizationHeldTimeDoubleBits `
            -match '^[0-9a-f]{16}$' `
        -and [string]$result.externalRecordingPhaseNormalizationHeldTimeDoubleBits `
            -ne '0000000000000000' `
        -and [int]$result.externalRecordingPhaseNormalizationRemainingNormalFrameCount `
            -ge 1 `
        -and [int]$result.externalRecordingPhaseNormalizationRemainingNormalFrameCount `
            -le 16 `
        -and [int]$result.externalRecordingPhaseNormalizationRestoreFramePhase `
            -ge 0 `
        -and [int]$result.externalRecordingPhaseNormalizationRestoreFramePhase `
            -lt 4 `
        -and [int]$result.externalRecordingPhaseNormalizationLastFramePhase `
            -eq [int]$result.externalRecordingPhaseNormalizationRestoreFramePhase
    $externalScenePhasesAligned =
        [int]$result.externalSceneFramePhaseModulo -eq 4 `
        -and $recordingRootFramePhase -eq 0 `
        -and [int]$result.externalSceneFramePhaseTarget -eq 0 `
        -and [int]$result.externalRecordingRootRequestObservedFrameCount `
            -eq ([int]$result.recordingArmBoundaryFrameCount + 1) `
        -and [int]$result.externalRecordingRootFramePhase -eq 0 `
        -and $recordingPhaseNormalized
    $externalRngBoundaryBalanced =
        [string]$result.externalClockProfileId `
            -ceq 'external-unity-startup-continuous-clock-v40-native-scene-lifecycle' `
        -and [string]$result.externalRngPayloadPolicyId `
            -ceq 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
        -and [int]$result.externalRngResetCount -eq 2 `
        -and [bool]$result.externalRecordingRngSynchronizationRequested `
        -and [bool]$result.externalRecordingRngSynchronizationApplied `
        -and [int]$result.externalRngFaultCode -eq 0 `
        -and $externalScenePhasesAligned `
        -and (Test-T24NativeSceneLifecycleContract -Telemetry $result) `
        -and [string]$result.externalRngLastBoundary -ceq 'recording-root' `
        -and -not [string]::IsNullOrEmpty([string]$result.externalRngLastScene)
    if (-not $result.success `
            -or $result.frameCount -ne $MaxTicks `
            -or [string]$result.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1' `
            -or $result.inputInjected `
            -or $result.timeWritten `
            -or $result.gameplayStateWritten `
            -or $result.saveLoadedByObserver `
            -or -not $result.baselineCaptured `
            -or $synchronizedHeldSchedule.Count -ne 0 `
                -and (-not $result.externalInputSynchronized `
                    -or -not [bool]$result.externalInputSynchronizationRequired `
                    -or [int]$result.externalInputSynchronizedFrames `
                        -ne $MaxTicks `
                    -or [int]$result.externalInputSynchronizationPrimeFrames `
                        -ne 1 `
                    -or -not [bool]$result.externalRngSynchronized `
                    -or -not [bool]$result.externalRngSynchronizationOriginCaptured `
                    -or -not [bool]$result.recordingRngSynchronized `
                    -or -not [bool]$result.recordingRngSynchronizationOriginCaptured `
                    -or [string]$result.externalRngSynchronizationPolicy `
                        -ne 'unity-init-state-at-root-only-native-scene-lifecycle-v19' `
                    -or [int]$result.externalRngSeed -ne 1212896321) `
            -or -not $externalRngBoundaryBalanced `
            -or [uint32]$result.externalClockBridgeAbi -ne 10 `
            -or [int]$result.externalClockBridgeStatus -ne 2 `
            -or [bool]$result.externalRuntimeVirtualClockRegistered `
                -ne $expectedRuntimeVirtualClockRegistration `
            -or [int]$result.externalVirtualClockPaused -ne 0 `
            -or [int]$result.externalVirtualClockPauseCount -ne 0 `
            -or [int]$result.externalVirtualClockResumePending -ne 0 `
            -or [int]$result.externalVirtualClockResumeRequestCount -ne 0 `
            -or [int]$result.externalVirtualClockResumeCount -ne 0 `
            -or -not [bool]$result.externalDeterministicClockEnabled `
            -or [long]$result.externalDeterministicClockFrequency -le 0 `
            -or [long]$result.externalDeterministicClockStepTicks -le 0 `
            -or [long]$result.externalDeterministicClockFrequency `
                % [long]$result.externalDeterministicClockStepTicks -ne 0 `
            -or [long]$result.externalDeterministicClockFrequency `
                / [long]$result.externalDeterministicClockStepTicks -ne 50 `
            -or [long]$result.externalDeterministicClockAnchor -le 0 `
            -or [int]$result.externalDeterministicClockFrameAdvanceCount -le 0 `
            -or [int]$result.externalDeterministicClockEnableFaultCode -ne 0 `
            -or [int]$result.externalDeterministicClockAdvanceFaultCode -ne 0 `
            -or [int]$result.externalDeterministicClockLastUnityFrameCount -le 0 `
            -or [int]$result.externalDeterministicClockDuplicateTimeUpdateSkipCount -lt 0 `
            -or [int]$result.externalDeterministicClockSceneLoadFrameSkipCount -ne 0 `
            -or [int]$result.externalDeterministicClockUnityFrameFaultCode -ne 0 `
            -or [string]$result.externalRealtimeEpochNormalizationPolicyId `
                -ne 'root-game-minus-rounded-startup-offset-qpc-grid-v3' `
            -or -not [bool]$result.externalRealtimeEpochNormalizationApplied `
            -or [int]$result.externalRealtimeEpochNormalizationCount -le 0 `
            -or [string]$result.externalRealtimeEpochNormalizationGameTimeBits `
                -notmatch '^[0-9a-f]{16}$' `
            -or [string]$result.externalRealtimeEpochNormalizationBeforeBits `
                -notmatch '^[0-9a-f]{16}$' `
            -or [string]$result.externalRealtimeEpochNormalizationTargetBits `
                -notmatch '^[0-9a-f]{16}$' `
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
                -eq '00000000' `
            -or -not [bool]$result.externalStartupClockHookInstalled `
            -or -not [bool]$result.externalStartupClockLatchEnabled `
            -or [uint32]$result.externalStartupClockHookThreadId `
                -ne [uint32]$clockAudit.primaryThreadId `
            -or [int]$result.externalStartupClockVirtualQpcCallCount -le 0 `
            -or [int]$result.externalStartupClockHandoffAdoptCount -ne 1 `
            -or [int]$result.externalStartupClockFaultCode -ne 0 `
            -or -not [bool]$result.recordingArmBoundaryReached `
            -or -not [bool]$result.recordingArmReleased `
            -or [string]$result.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or [string]$result.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne [string]$result.recordingArmBoundaryFixedTimeRaw.canonicalHex `
            -or [string]$result.recordingArmBoundaryTimeRaw.canonicalHex `
                -ne '44400000' `
            -or [int]$result.recordingFramePhaseModulo -ne 4 `
            -or [int]$result.recordingFramePhaseTarget -ne 0 `
            -or [int]$result.recordingArmBoundaryFramePhase -ne 0 `
            -or [int]$result.recordingArmBoundaryFrameCount % 4 -ne 0 `
            -or -not [bool]$result.externalTimeUpdateResumeBoundaryInstalled `
            -or [int]$result.externalTimeUpdateResumeBoundaryInstallCount -ne 1 `
            -or [int]$result.externalTimeUpdateResumeBoundaryCallbackCount -le 0 `
            -or [int]$result.externalDeterministicClockFrameAdvanceCount `
                -gt [int]$result.externalTimeUpdateResumeBoundaryCallbackCount `
            -or [int]$result.externalTimeUpdateResumeBoundaryCommitCount -ne 0 `
            -or [int]$result.externalTimeUpdateResumeCommitFaultCode -ne 0 `
            -or [int]$result.externalPlayerLoopPostLateUpdateIndex -lt 0 `
            -or [int]$result.externalPlayerLoopTimeUpdateIndex -lt 0 `
            -or [int]$result.externalPlayerLoopResumeBoundaryIndex -lt 0 `
            -or [int]$result.externalPlayerLoopWaitForPresentationIndex `
                -ne ([int]$result.externalPlayerLoopResumeBoundaryIndex + 1) `
            -or -not [string]::IsNullOrEmpty(
                [string]$result.externalPlayerLoopBoundaryError) `
            -or $null -eq $baseline `
            -or [int]$baseline.schemaVersion -ne 2 `
            -or [string]$baseline.timeRaw.canonicalHex `
                -notmatch '^[0-9a-f]{8}$' `
            -or [string]$baseline.fixedTimeRaw.canonicalHex `
                -notmatch '^[0-9a-f]{8}$' `
            -or [string]$baseline.recordingAbsoluteTimeTarget.canonicalHex `
                -ne '44400000' `
            -or [string]$baseline.samplingBoundary `
                -ne 'post-render-completed-frame-sampling-v1') {
        throw (
            'Reference observer contract failed: ' `
            + ($result | ConvertTo-Json -Compress)
        )
    }
    $sceneTimeline = Get-TraceSceneTimelineAudit -Path $tracePath
    if ([int]$sceneTimeline.frameCount -ne $MaxTicks) {
        throw 'Reference trace frame count does not match max ticks.'
    }
    if ([string]$sceneTimeline.firstScene -cne [string]$result.externalRngLastScene) {
        throw 'Reference RNG root scene differs from the first trace scene.'
    }

    $scenarioCoverage = $null
    if ($null -ne $scenarioContract) {
        $coverageMode = if ($Mode -eq 'vanilla-reference') {
            'VanillaReference'
        }
        else {
            'TasPassive'
        }
        $scenarioCoverage = & (
            Join-Path $PSScriptRoot 'Test-T24ScenarioCoverage.ps1') `
            -ContractPath $ScenarioContractPath `
            -TracePath $tracePath `
            -OutputPath (
                Join-Path $EvidenceRoot 'scenario-coverage.json') `
            -RunMode $coverageMode
        if ([string]$scenarioCoverage.verdict -cne 'PASS') {
            throw 'Reference scenario coverage did not pass.'
        }
    }

    [pscustomobject]@{
        schemaVersion = 2
        verdict = if ($Mode -eq 'vanilla-reference') {
            'REFERENCE_CLOCK_CONTROLLED_CAPTURED'
        }
        else {
            'TAS_PASSIVE_CLOCK_CONTROLLED_CAPTURED'
        }
        mode = $Mode
        runId = $runId
        fixtureSlot = $FixtureSlot
        frameCount = $MaxTicks
        samplingBoundary =
            'post-render-completed-frame-sampling-v1'
        traceSha256 = (Get-FileHash `
            -LiteralPath $tracePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        baselineSha256 = (Get-FileHash `
            -LiteralPath $baselinePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        tasRuntimeAbsent = $Mode -eq 'vanilla-reference'
        observerOnlyManagedMod = $Mode -eq 'vanilla-reference'
        tasControlStarted = $false
        externalClockPrototype = $true
        clockHarnessScript = $clockHarnessPath
        clockHarnessScriptSha256 = (Get-FileHash `
            -LiteralPath $clockHarnessPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        clockCapabilityId = [string]$clockAudit.capabilityId
        clockProfile = [string]$clockAudit.profile
        externalRngSynchronized =
            [bool]$result.externalRngSynchronized
        externalRngSynchronizationOriginCaptured =
            [bool]$result.externalRngSynchronizationOriginCaptured
        externalRngSynchronizationPolicy =
            [string]$result.externalRngSynchronizationPolicy
        externalRngSeed = [int]$result.externalRngSeed
        externalClockProfileId =
            [string]$result.externalClockProfileId
        externalRngPayloadPolicyId =
            [string]$result.externalRngPayloadPolicyId
        externalRngResetCount =
            [int]$result.externalRngResetCount
        externalRngTransitionStartCount =
            [int]$result.externalRngTransitionStartCount
        externalRngGameplayReadyCount =
            [int]$result.externalRngGameplayReadyCount
        externalRngFirstGameplayReadyFrameCount =
            [int]$result.externalRngFirstGameplayReadyFrameCount
        externalRngFirstGameplayReadyFramePhase =
            [int]$result.externalRngFirstGameplayReadyFramePhase
        externalRngGameplayReadyAlignmentHoldUpdateCount =
            [int]$result.externalRngGameplayReadyAlignmentHoldUpdateCount
        externalRngSceneEpoch =
            [int]$result.externalRngSceneEpoch
        externalRngLastAppliedSeed =
            [int]$result.externalRngLastAppliedSeed
        externalRngLastBoundary =
            [string]$result.externalRngLastBoundary
        externalRngLastScene =
            [string]$result.externalRngLastScene
        externalRngFaultCode =
            [int]$result.externalRngFaultCode
        externalSceneRngPending =
            [bool]$result.externalSceneRngPending
        externalSceneClockExclusionActive =
            [bool]$result.externalSceneClockExclusionActive
        externalSceneClockExclusionBeginCount =
            [int]$result.externalSceneClockExclusionBeginCount
        externalSceneClockExclusionFinishCount =
            [int]$result.externalSceneClockExclusionFinishCount
        externalSceneClockExclusionFrozenTimeUpdateCount =
            [int]$result.externalSceneClockExclusionFrozenTimeUpdateCount
        externalSceneClockExclusionFaultCode =
            [int]$result.externalSceneClockExclusionFaultCode
        externalSceneClockExclusionPreSynchronizationCount =
            [int]$result.externalSceneClockExclusionPreSynchronizationCount
        externalSceneFramePhaseModulo =
            [int]$result.externalSceneFramePhaseModulo
        externalSceneFramePhaseTarget =
            [int]$result.externalSceneFramePhaseTarget
        externalRecordingRootRequestObservedFrameCount =
            [int]$result.externalRecordingRootRequestObservedFrameCount
        externalRecordingRootFramePhase =
            [int]$result.externalRecordingRootFramePhase
        externalRecordingPhaseNormalizationActive =
            [bool]$result.externalRecordingPhaseNormalizationActive
        externalRecordingPhaseNormalizationCompleted =
            [bool]$result.externalRecordingPhaseNormalizationCompleted
        externalRecordingPhaseNormalizationBeginCount =
            [int]$result.externalRecordingPhaseNormalizationBeginCount
        externalRecordingPhaseNormalizationHoldFrameCount =
            [int]$result.externalRecordingPhaseNormalizationHoldFrameCount
        externalRecordingPhaseNormalizationReleaseCount =
            [int]$result.externalRecordingPhaseNormalizationReleaseCount
        externalRecordingPhaseNormalizationLastFrameCount =
            [int]$result.externalRecordingPhaseNormalizationLastFrameCount
        externalRecordingPhaseNormalizationLastFramePhase =
            [int]$result.externalRecordingPhaseNormalizationLastFramePhase
        externalRecordingPhaseNormalizationHeldTimeBits =
            [string]$result.externalRecordingPhaseNormalizationHeldTimeBits
        externalRecordingPhaseNormalizationHeldTimeDoubleBits =
            [string]$result.externalRecordingPhaseNormalizationHeldTimeDoubleBits
        externalRecordingPhaseNormalizationRemainingNormalFrameCount =
            [int]$result.externalRecordingPhaseNormalizationRemainingNormalFrameCount
        externalRecordingPhaseNormalizationRestoreFramePhase =
            [int]$result.externalRecordingPhaseNormalizationRestoreFramePhase
        externalRecordingPhaseNormalizationFaultCode =
            [int]$result.externalRecordingPhaseNormalizationFaultCode
        externalSceneActivationAlignmentBeginCount =
            [int]$result.externalSceneActivationAlignmentBeginCount
        externalSceneActivationAlignmentHoldFrameCount =
            [int]$result.externalSceneActivationAlignmentHoldFrameCount
        externalSceneActivationAlignmentReleaseCount =
            [int]$result.externalSceneActivationAlignmentReleaseCount
        externalSceneActivationAlignmentLastFrameCount =
            [int]$result.externalSceneActivationAlignmentLastFrameCount
        externalSceneActivationAlignmentLastFramePhase =
            [int]$result.externalSceneActivationAlignmentLastFramePhase
        externalSceneFinishAlignmentBeginCount =
            [int]$result.externalSceneFinishAlignmentBeginCount
        externalSceneFinishAlignmentHoldFrameCount =
            [int]$result.externalSceneFinishAlignmentHoldFrameCount
        externalSceneFinishAlignmentReleaseCount =
            [int]$result.externalSceneFinishAlignmentReleaseCount
        externalSceneFinishAlignmentLastFrameCount =
            [int]$result.externalSceneFinishAlignmentLastFrameCount
        externalSceneFinishAlignmentLastFramePhase =
            [int]$result.externalSceneFinishAlignmentLastFramePhase
        externalSceneFrameAlignmentFaultCode =
            [int]$result.externalSceneFrameAlignmentFaultCode
        traceSceneTransitionCount =
            [int]$sceneTimeline.transitionCount
        externalClockBridgeAbi =
            [uint32]$result.externalClockBridgeAbi
        externalClockBridgeStatus =
            [int]$result.externalClockBridgeStatus
        externalRuntimeVirtualClockRegistered =
            [bool]$result.externalRuntimeVirtualClockRegistered
        externalVirtualClockPaused =
            [int]$result.externalVirtualClockPaused
        externalVirtualClockPauseCount =
            [int]$result.externalVirtualClockPauseCount
        externalVirtualClockResumePending =
            [int]$result.externalVirtualClockResumePending
        externalVirtualClockResumeRequestCount =
            [int]$result.externalVirtualClockResumeRequestCount
        externalVirtualClockResumeCount =
            [int]$result.externalVirtualClockResumeCount
        externalDeterministicClockEnabled =
            [bool]$result.externalDeterministicClockEnabled
        externalDeterministicClockFrequency =
            [long]$result.externalDeterministicClockFrequency
        externalDeterministicClockStepTicks =
            [long]$result.externalDeterministicClockStepTicks
        externalDeterministicClockAnchor =
            [long]$result.externalDeterministicClockAnchor
        externalDeterministicClockFrameAdvanceCount =
            [int]$result.externalDeterministicClockFrameAdvanceCount
        externalDeterministicClockEnableFaultCode =
            [int]$result.externalDeterministicClockEnableFaultCode
        externalDeterministicClockAdvanceFaultCode =
            [int]$result.externalDeterministicClockAdvanceFaultCode
        externalDeterministicClockLastUnityFrameCount =
            [int]$result.externalDeterministicClockLastUnityFrameCount
        externalDeterministicClockDuplicateTimeUpdateSkipCount =
            [int]$result.externalDeterministicClockDuplicateTimeUpdateSkipCount
        externalDeterministicClockSceneLoadFrameSkipCount =
            [int]$result.externalDeterministicClockSceneLoadFrameSkipCount
        externalDeterministicClockUnityFrameFaultCode =
            [int]$result.externalDeterministicClockUnityFrameFaultCode
        externalRealtimeEpochNormalizationPolicyId =
            [string]$result.externalRealtimeEpochNormalizationPolicyId
        externalRealtimeEpochNormalizationApplied =
            [bool]$result.externalRealtimeEpochNormalizationApplied
        externalRealtimeEpochNormalizationCount =
            [int]$result.externalRealtimeEpochNormalizationCount
        externalRealtimeEpochNormalizationGameTimeBits =
            [string]$result.externalRealtimeEpochNormalizationGameTimeBits
        externalRealtimeEpochNormalizationBeforeBits =
            [string]$result.externalRealtimeEpochNormalizationBeforeBits
        externalRealtimeEpochNormalizationTargetBits =
            [string]$result.externalRealtimeEpochNormalizationTargetBits
        externalRealtimeEpochNormalizationAfterBits =
            [string]$result.externalRealtimeEpochNormalizationAfterBits
        externalRealtimeEpochNormalizationCanonicalOffsetSeconds =
            [long]$result.externalRealtimeEpochNormalizationCanonicalOffsetSeconds
        externalRealtimeEpochNormalizationDeltaTicks =
            [long]$result.externalRealtimeEpochNormalizationDeltaTicks
        externalRealtimeEpochNormalizationFaultCode =
            [int]$result.externalRealtimeEpochNormalizationFaultCode
        externalDoublePhaseCalibrationApplied =
            [bool]$result.externalDoublePhaseCalibrationApplied
        externalDoublePhaseCalibrationAttempts =
            [int]$result.externalDoublePhaseCalibrationAttempts
        externalDoublePhaseInitialResidualBits =
            [string]$result.externalDoublePhaseInitialResidualBits
        externalDoublePhaseInitialResidual =
            $result.externalDoublePhaseInitialResidual
        externalDoublePhaseFinalResidualBits =
            [string]$result.externalDoublePhaseFinalResidualBits
        externalDoublePhaseFinalResidual =
            $result.externalDoublePhaseFinalResidual
        externalDoublePhaseDownwardQuantizationCount =
            [int]$result.externalDoublePhaseDownwardQuantizationCount
        externalDoublePhaseLastCorrectionBits =
            [string]$result.externalDoublePhaseLastCorrectionBits
        externalDoublePhaseCalibrationFaultCode =
            [int]$result.externalDoublePhaseCalibrationFaultCode
        externalStartupClockHookInstalled =
            [bool]$result.externalStartupClockHookInstalled
        externalStartupClockLatchEnabled =
            [bool]$result.externalStartupClockLatchEnabled
        externalStartupClockHookThreadId =
            [uint32]$result.externalStartupClockHookThreadId
        externalStartupClockPrimaryThreadMatched =
            [uint32]$result.externalStartupClockHookThreadId `
                -eq [uint32]$clockAudit.primaryThreadId
        externalStartupClockVirtualQpcCallCount =
            [int]$result.externalStartupClockVirtualQpcCallCount
        externalStartupClockHandoffAdoptCount =
            [int]$result.externalStartupClockHandoffAdoptCount
        externalStartupClockFaultCode =
            [int]$result.externalStartupClockFaultCode
        externalStartupPolicy = [string]$clockAudit.startupPolicy
        recordingArmPolicyId =
            'exact-absolute-time-post-root-request-first-global-phase-host-release-v11'
        recordingAbsoluteTimeTarget =
            $result.recordingAbsoluteTimeTarget
        recordingArmBoundaryReached =
            [bool]$result.recordingArmBoundaryReached
        recordingArmReleased = [bool]$result.recordingArmReleased
        recordingArmBoundaryTimeRaw =
            $result.recordingArmBoundaryTimeRaw
        recordingArmBoundaryFixedTimeRaw =
            $result.recordingArmBoundaryFixedTimeRaw
        recordingArmBoundaryFrameCount =
            [int]$result.recordingArmBoundaryFrameCount
        recordingArmBoundaryFramePhase =
            [int]$result.recordingArmBoundaryFramePhase
        recordingFramePhaseModulo =
            [int]$result.recordingFramePhaseModulo
        recordingFramePhaseTarget =
            [int]$result.recordingFramePhaseTarget
        externalTimeUpdateResumeBoundaryInstalled =
            [bool]$result.externalTimeUpdateResumeBoundaryInstalled
        externalTimeUpdateResumeBoundaryInstallCount =
            [int]$result.externalTimeUpdateResumeBoundaryInstallCount
        externalTimeUpdateResumeBoundaryCallbackCount =
            [int]$result.externalTimeUpdateResumeBoundaryCallbackCount
        externalTimeUpdateResumeBoundaryCommitCount =
            [int]$result.externalTimeUpdateResumeBoundaryCommitCount
        externalTimeUpdateResumeCommitFaultCode =
            [int]$result.externalTimeUpdateResumeCommitFaultCode
        externalPlayerLoopPostLateUpdateIndex =
            [int]$result.externalPlayerLoopPostLateUpdateIndex
        externalPlayerLoopTimeUpdateIndex =
            [int]$result.externalPlayerLoopTimeUpdateIndex
        externalPlayerLoopResumeBoundaryIndex =
            [int]$result.externalPlayerLoopResumeBoundaryIndex
        externalPlayerLoopWaitForPresentationIndex =
            [int]$result.externalPlayerLoopWaitForPresentationIndex
        externalPlayerLoopBoundaryError =
            [string]$result.externalPlayerLoopBoundaryError
        externalRngPayloadSynchronizationPolicy =
            [string]$clockAudit.randomSynchronizationPolicy
        externalRngPayloadSeed =
            [int]$clockAudit.randomSynchronizationSeed
        externalUiLoad = $true
        externalPhysicalInput = $true
        externalPhysicalInputSynchronized =
            $synchronizedHeldSchedule.Count -ne 0
        capturedPhysicalInputEquivalent = $physicalInputEquivalent
        physicalInputEquivalencePolicyId =
            'captured-vs-source-physical-control-signal-v2'
        physicalInputEquivalenceAuditSha256 = if (
            Test-Path `
                -LiteralPath $physicalInputEquivalenceAuditPath `
                -PathType Leaf) {
            (Get-FileHash `
                -LiteralPath $physicalInputEquivalenceAuditPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        else { '' }
        externalInputSynchronizationRequired =
            [bool]$result.externalInputSynchronizationRequired
        externalInputSynchronizationPrimeFrames =
            [int]$result.externalInputSynchronizationPrimeFrames
        physicalInputSourceTraceSha256 = if (
            $synchronizedHeldSchedule.Count -eq 0) {
            ''
        }
        else {
            (Get-FileHash `
                -LiteralPath $PhysicalInputTracePath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        visualRecognitionUsed = $false
        observerInputInjected = $false
        observerGameplayStateWritten = $false
        preClockMenuDwellPolicyId =
            'wall-clock-wait-in-menu-title-v1'
        preClockMenuDwellRequestedMilliseconds =
            $PreClockMenuDwellMilliseconds
        preClockMenuDwellActualMilliseconds =
            $preClockMenuDwellActualMilliseconds
        preClockMenuDwellAuditSha256 = (Get-FileHash `
            -LiteralPath $preClockMenuDwellAuditPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        scenarioId = if ($null -eq $scenarioContract) {
            ''
        }
        else {
            [string]$scenarioContract['scenarioId']
        }
        scenarioContractSha256 = if ($null -eq $scenarioContract) {
            ''
        }
        else {
            (Get-FileHash `
                -LiteralPath $ScenarioContractPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        scenarioCoverageVerdict = if ($null -eq $scenarioCoverage) {
            'NOT_REQUESTED'
        }
        else {
            [string]$scenarioCoverage.verdict
        }
    } |
        ConvertTo-Json -Depth 10 |
        Set-Content `
            -LiteralPath (Join-Path $EvidenceRoot 'reference-smoke.json') `
            -Encoding utf8NoBOM
}
finally {
    Release-GameplayKeys
    if ($null -ne $inputSyncApplied) {
        $inputSyncApplied.Set() | Out-Null
    }
    if ($null -ne $inputSyncStart) {
        $inputSyncStart.Dispose()
    }
    if ($null -ne $inputSyncFrameReady) {
        $inputSyncFrameReady.Dispose()
    }
    if ($null -ne $inputSyncApplied) {
        $inputSyncApplied.Dispose()
    }
    if ($null -ne $recordingArmRelease) {
        $recordingArmRelease.Set() | Out-Null
        $recordingArmRelease.Dispose()
    }
    if ($null -ne $game) {
        try {
            $game.Refresh()
            if (-not $game.HasExited) {
                [void]$game.CloseMainWindow()
                [void]$game.WaitForExit(15000)
                $game.Refresh()
            }
            if (-not $game.HasExited) {
                Stop-Process -Id $game.Id -ErrorAction SilentlyContinue
                [void]$game.WaitForExit(5000)
            }
        }
        catch {
        }
        $game.Dispose()
    }

    # Before snapshot completion no game or slot mutation has started.
    # A partial read must never authorize removal of unrecorded originals.
    if ($slotSnapshotComplete) {
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

    if ($settingsOriginallyExisted) {
        [IO.File]::WriteAllBytes($settingsPath, $settingsOriginal)
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
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
            Move-Item `
                -LiteralPath $isolatedTas `
                -Destination (Join-Path $modsBackup 'HollowKnightTAS')
        }
        if (Test-Path -LiteralPath $modsDirectory) {
            Move-Item `
                -LiteralPath $modsDirectory `
                -Destination $modsIsolated
            $isolationMoved = $true
        }
        Move-Item -LiteralPath $modsBackup -Destination $modsDirectory
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
}

Write-Output "T24 $Mode smoke captured: $EvidenceRoot"
