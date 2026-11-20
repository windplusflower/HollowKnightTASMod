[CmdletBinding()]
param([string]$ManagedPath = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed',
    [switch]$OtherSlotOnly,
    [switch]$LifecycleConsentOnly,
    [switch]$RecoveryOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (@($OtherSlotOnly,$LifecycleConsentOnly,$RecoveryOnly).Where({$_}).Count -gt 1) { throw 'Select one directed scope.' }
$runtime = Join-Path $PSScriptRoot '../../src/HollowKnightTAS.Runtime/bin/Release'
[void][Reflection.Assembly]::LoadFrom((Join-Path $ManagedPath 'UnityEngine.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $runtime 'HollowKnightTAS.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $runtime 'HollowKnightTAS.dll'))
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../../artifacts/slot-file-smoke-' + [guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory($root)
$backups = Join-Path $root 'backups'
$provider = [HollowKnightTAS.Runtime.ReplaySave.DesktopSaveSlotBaselineProvider]::new($root, $backups)
$builder = [HollowKnightTAS.Core.State.SemanticSnapshotBuilder]::new()
$builder.AddString('scene.name', 'fixture')
$builder.AddString('game.state', 'PLAYING')
$builder.AddString('hero.actorState', 'idle')
foreach ($state in @('attacking','dashing','falling','jumping','onGround','wallSliding')) {
    $builder.AddBoolean('hero.cState.' + $state, $false)
}
foreach ($field in @('position.x','position.y','velocity.x','velocity.y')) {
    $builder.AddFloat32('hero.' + $field, 0)
}
foreach ($field in @('health','maxHealth','mp')) { $builder.AddInt32('player.' + $field, 0) }
$snapshot = $builder.Build()
$semantic = [HollowKnightTAS.Core.State.SemanticSnapshotCanonicalizer]::Serialize($snapshot)
$hash = [HollowKnightTAS.Core.State.SemanticSnapshotHasher]::ComputeSha256($snapshot)
$baseline = [HollowKnightTAS.Core.ReplaySave.BaselineBundle]::new(1, 'slot-test', $hash, 2,
    [DateTimeOffset]::UtcNow, [byte[]](1,2,3), [byte[]](4,5), $semantic, $null)
if ($RecoveryOnly) {
    $savePath = Join-Path $root 'user4.dat'
    $modPath = Join-Path $root 'user4.modded.json'
    [IO.File]::WriteAllBytes($savePath, [byte[]](91,92))
    [IO.File]::WriteAllBytes($modPath, [byte[]](93,94))
    $plan = $provider.PlanInstall($baseline, 4)
    $blockedRoot = Join-Path $root 'not-a-directory'
    [IO.File]::WriteAllBytes($blockedRoot, [byte[]](1))
    $badLease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $plan)
    $badLease.ConfigureRecovery([HollowKnightTAS.Core.ReplaySave.SlotRecoveryStore]::new($blockedRoot),
        'cold-recovery-test', $PID, [DateTimeOffset]::UtcNow, 0)
    $rejected = $false
    try { $null = $badLease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved) }
    catch { $rejected = $true }
    if (!$rejected -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($savePath)) -ne 'W1w=' -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($modPath)) -ne 'XV4=') { throw 'Failed recovery publication allowed a slot write.' }
    $storeRoot = Join-Path $root 'recovery'
    $lease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $plan)
    $lease.ConfigureRecovery([HollowKnightTAS.Core.ReplaySave.SlotRecoveryStore]::new($storeRoot),
        'cold-recovery-test', $PID, [DateTimeOffset]::UtcNow, 1)
    $result = $lease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
    if (!$result.Success) { throw $result.Error }
    if (@(Get-ChildItem -LiteralPath $storeRoot -Recurse -Filter '*.pending').Count -ne 1) { throw 'Missing pending recovery.' }
    # Model an interrupted rollback: dat already restored, modded still installed.
    [IO.File]::WriteAllBytes($savePath, [byte[]](91,92))
    $lease.Rollback()
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($modPath)) -ne 'XV4=') { throw 'Partial rollback retry failed.' }
    if (@(Get-ChildItem -LiteralPath $storeRoot -Recurse -Filter '*.pending').Count -ne 0 -or
        @(Get-ChildItem -LiteralPath $storeRoot -Recurse -Filter '*.restored').Count -ne 1) { throw 'Recovery completion missing.' }
    $lease.Dispose()
    [pscustomobject]@{Result='PASS';Scope='Actual lease write-ahead failure, pending publication, partial rollback retry and completion';IsolatedDirectory=$root}
    return
}
if ($LifecycleConsentOnly) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $ManagedPath 'Assembly-CSharp.dll'))
    # Exercise the actual source consent methods without constructing the Unity
    # runner. Only their filesystem provider and slot number are initialized;
    # this is not a game-loop, IPC, or native-loading test.
    $type = [HollowKnightTAS.Runtime.ReplaySave.RuntimeReplayRestoreCoordinator]
    $source = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $type.GetField('baselineProvider', $flags).SetValue($source, $provider)
    $type.GetField('dedicatedTasSlot', $flags).SetValue($source, 4)
    $objects = [Collections.Generic.Dictionary[string,byte[]]]::new()
    $records = [Collections.Generic.List[HollowKnightTAS.Core.ReplaySave.ReplayLifecycleRecord]]::new()
    foreach ($slot in @(1,2)) {
        [IO.File]::WriteAllBytes((Join-Path $root "user$slot.dat"), [byte[]]@(90,$slot))
        [IO.File]::WriteAllBytes((Join-Path $root "user$slot.modded.json"), [byte[]]@(80,$slot))
        $target = [byte[]]@(10,$slot)
        $targetHash = [HollowKnightTAS.Core.Cryptography.Sha256Utility]::ComputeHex($target)
        $objects.Add($targetHash, $target)
        $records.Add([HollowKnightTAS.Core.ReplaySave.ReplayLifecycleRecord]::new($slot-1, $slot,
            ('0'*64), [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleKind]::LoadSlot, $slot, $targetHash,
            [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleOutcome]::Completed, 1, '', ''))
    }
    function Request-Consent {
        $source.ValidateColdLifecycleBeforeHandoff($baseline, ('a'*64), ('b'*64), ('c'*64), 5, $records, $objects)
    }
    function Expect-Rejected([scriptblock]$Action, [string]$Message) {
        $rejected = $false
        try { & $Action | Out-Null } catch { if ($_.Exception.ToString().Contains($Message)) { $rejected = $true } else { throw } }
        if (!$rejected) { throw "Expected rejection: $Message" }
    }
    function Assert-IsolatedOriginal([int]$Slot, [byte[]]$Save) {
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root "user$Slot.dat"))) -cne [Convert]::ToBase64String($Save) -or
            [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root "user$Slot.modded.json"))) -cne [Convert]::ToBase64String([byte[]]@(80,$Slot))) {
            throw 'Isolated original files changed unexpectedly.'
        }
    }
    Expect-Rejected { Request-Consent } 'ColdRestoreSlotApprovalRequired'
    if (!$source.HasPendingSourceSlotApproval) { throw 'Source approval flag missing.' }
    $source.ResolveSourceSlotApproval($false) | Out-Null
    Assert-IsolatedOriginal 1 ([byte[]](90,1))
    Expect-Rejected { Request-Consent } 'ColdRestoreSlotApprovalRequired'
    $source.ResolveSourceSlotApproval($true) | Out-Null
    if ([IO.Directory]::GetDirectories($backups).Length -ne 0) { throw 'Source consent created a backup or wrote a slot.' }
    $authorization = [HollowKnightTAS.Core.ReplaySave.ReplaySlotOverwriteAuthorization]::Deserialize((Request-Consent))
    if (!$authorization.Allows(1, [byte[]](90,1), [byte[]](80,1)) -or !$authorization.Allows(2, [byte[]](90,2), [byte[]](80,2))) { throw 'Durable consent lost slot identities.' }
    [IO.File]::WriteAllBytes((Join-Path $root 'user1.dat'), [byte[]](91,1))
    Expect-Rejected { Request-Consent } 'ColdRestoreSlotApprovalRequired'
    if ($authorization.Allows(1, [byte[]](91,1), [byte[]](80,1))) { throw 'Stale serialized consent matched changed files.' }
    $source.ResolveSourceSlotApproval($true) | Out-Null
    $authorization = [HollowKnightTAS.Core.ReplaySave.ReplaySlotOverwriteAuthorization]::Deserialize((Request-Consent))
    $method = $provider.GetType().GetMethod('PlanLifecycleInstall', $flags)
    $plan = $method.Invoke($provider, [object[]]@($baseline, $records[0], $objects))
    if (!$authorization.Allows(1, [byte[]](91,1), [byte[]](80,1))) { throw 'Fresh consent unavailable.' }
    $lease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $plan)
    try {
        $installed = $lease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
        if (!$installed.Success -or !$installed.WroteSlot) { throw 'Authorized temporary install failed.' }
        if (![IO.File]::Exists((Join-Path $installed.BackupDirectory 'original.dat'))) { throw 'Original backup missing.' }
    } finally { $lease.Dispose() }
    Assert-IsolatedOriginal 1 ([byte[]](91,1))
    $racePlan = $method.Invoke($provider, [object[]]@($baseline, $records[1], $objects))
    [IO.File]::WriteAllBytes((Join-Path $root 'user2.dat'), [byte[]](92,2))
    $raced = $provider.Install($racePlan, [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
    if ($raced.Success -or $raced.WroteSlot) { throw 'Install accepted files changed after planning.' }
    Assert-IsolatedOriginal 2 ([byte[]](92,2))
    Expect-Rejected { Request-Consent } 'ColdRestoreSlotApprovalRequired'
    $type.GetField('lifecycleConsentExpiresAt', $flags).SetValue($source, [DateTimeOffset]::UtcNow.AddMinutes(-1))
    Expect-Rejected { $source.ResolveSourceSlotApproval($true) } 'expired'
    [pscustomobject]@{Result='PASS';Scope='Actual source consent methods, serialized two-slot identity, approved install/rollback, file-race rejection and expiry; no game loop';IsolatedDirectory=$root}
    return
}
if ($OtherSlotOnly) {
    $otherSave = Join-Path $root 'user1.dat'
    $otherModded = Join-Path $root 'user1.modded.json'
    [IO.File]::WriteAllBytes($otherSave, [byte[]](91,92))
    [IO.File]::WriteAllBytes($otherModded, [byte[]](93,94))
    $target = [byte[]](1,2,3)
    $targetHash = [HollowKnightTAS.Core.Cryptography.Sha256Utility]::ComputeHex($target)
    $objects = [Collections.Generic.Dictionary[string,byte[]]]::new()
    $objects.Add($targetHash, $target)
    $operation = [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleRecord]::new(0, 0, ('f'*64),
        [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleKind]::LoadSlot, 1, $targetHash,
        [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleOutcome]::Completed, 1, '', '')
    $method = $provider.GetType().GetMethod('PlanLifecycleInstall', [Reflection.BindingFlags]'Instance,NonPublic')
    $plan = $method.Invoke($provider, [object[]]@($baseline, $operation, $objects))
    if (!$plan.RequiresOverwriteApproval -or $plan.DedicatedTasSlot -ne 1) { throw 'Foreign slot was treated as owned.' }
    foreach ($approval in @('None','Denied')) {
        $result = $provider.Install($plan, [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::$approval)
        if ($result.Success -or $result.WroteSlot) { throw 'Unapproved foreign slot was modified.' }
    }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($otherSave)) -cne [Convert]::ToBase64String([byte[]](91,92)) -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($otherModded)) -cne [Convert]::ToBase64String([byte[]](93,94))) {
        throw 'Denied proposal changed slot contents.'
    }
    [IO.File]::WriteAllBytes($otherSave, [byte[]](95,96))
    $stale = $provider.Install($plan, [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
    if ($stale.Success -or $stale.WroteSlot) { throw 'Stale foreign-slot approval was accepted.' }
    $fresh = $method.Invoke($provider, [object[]]@($baseline, $operation, $objects))
    $lease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $fresh)
    $approved = $lease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
    if (!$approved.Success) { throw $approved.Error }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $approved.BackupDirectory 'original.dat'))) -cne
        [Convert]::ToBase64String([byte[]](95,96))) { throw 'Foreign-slot backup mismatch.' }
    $lease.Dispose()
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($otherSave)) -cne [Convert]::ToBase64String([byte[]](95,96)) -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($otherModded)) -cne [Convert]::ToBase64String([byte[]](93,94))) {
        throw 'Foreign-slot rollback mismatch.'
    }
    [pscustomobject]@{Result='PASS';Scope='Foreign slot denial, stale approval rejection, explicit backup and rollback';IsolatedDirectory=$root}
    return
}
$save = Join-Path $root 'user4.dat'
$modded = Join-Path $root 'user4.modded.json'
[IO.File]::WriteAllBytes($save, [byte[]](9,8))
[IO.File]::WriteAllBytes($modded, [byte[]](7,6))
function Assert-Bytes($Path, [byte[]]$Expected) {
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -cne [Convert]::ToBase64String($Expected)) {
        throw "Unexpected bytes: $Path"
    }
}
$plan = $provider.PlanInstall($baseline, 4)
$denied = $provider.Install($plan, [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Denied)
if ($denied.Success -or !$denied.Cancelled) { throw 'Cancellation failed.' }
Assert-Bytes $save ([byte[]](9,8)); Assert-Bytes $modded ([byte[]](7,6))
if ([IO.Directory]::GetDirectories($backups).Length -ne 0) { throw 'Cancellation created a backup.' }
[IO.File]::WriteAllBytes($modded, [byte[]](8,8))
$stale = $provider.Install($plan, [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
if ($stale.Success) { throw 'Stale approval was accepted.' }
Assert-Bytes $save ([byte[]](9,8)); Assert-Bytes $modded ([byte[]](8,8))
if ([IO.Directory]::GetDirectories($backups).Length -ne 0) { throw 'Stale approval wrote backup data.' }
$fresh = $provider.PlanInstall($baseline, 4)
$installed = $provider.Install($fresh, [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
if (!$installed.Success) { throw $installed.Error }
Assert-Bytes $save ([byte[]](1,2,3)); Assert-Bytes $modded ([byte[]](4,5))
Assert-Bytes (Join-Path $installed.BackupDirectory 'original.dat') ([byte[]](9,8))
Assert-Bytes (Join-Path $installed.BackupDirectory 'original.modded.json') ([byte[]](8,8))
$same = $provider.Install($provider.PlanInstall($baseline, 4), [HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::None)
if (!$same.Success -or $same.WroteSlot) { throw 'Identical baseline should require no writes.' }
$lifecyclePlanMethod = $provider.GetType().GetMethod('PlanLifecycleInstall', [Reflection.BindingFlags]'Instance,NonPublic')
if ($null -eq $lifecyclePlanMethod) { throw 'Lifecycle install implementation is missing.' }
$slotBytes = [byte[]](21,22,23)
$slotHash = [HollowKnightTAS.Core.Cryptography.Sha256Utility]::ComputeHex($slotBytes)
$emptyHash = [HollowKnightTAS.Core.Cryptography.Sha256Utility]::ComputeHex([byte[]]@())
$objects = [Collections.Generic.Dictionary[string,byte[]]]::new()
$objects.Add($slotHash, $slotBytes)
$objects.Add($emptyHash, [byte[]]@())
$record = [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleRecord]::new(0, 0, ('f' * 64),
    [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleKind]::LoadSlot, 4, $slotHash,
    [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleOutcome]::Completed, 1, '', $emptyHash)
$lifecyclePlan = $lifecyclePlanMethod.Invoke($provider, [object[]]@($baseline, $record, $objects))
$lease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $lifecyclePlan)
$result = $lease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
if (!$result.Success) { throw $result.Error }
Assert-Bytes $save $slotBytes
Assert-Bytes $modded ([byte[]]@())
Assert-Bytes (Join-Path $result.BackupDirectory 'original.dat') ([byte[]](1,2,3))
Assert-Bytes (Join-Path $result.BackupDirectory 'original.modded.json') ([byte[]](4,5))
$lease.Rollback()
$lease.Dispose()
Assert-Bytes $save ([byte[]](1,2,3)); Assert-Bytes $modded ([byte[]](4,5))

$absentRecord = [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleRecord]::new(0, 0, ('f' * 64),
    [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleKind]::LoadSlot, 4, $slotHash,
    [HollowKnightTAS.Core.ReplaySave.ReplayLifecycleOutcome]::Completed, 1, '', '')
$absentPlan = $lifecyclePlanMethod.Invoke($provider, [object[]]@($baseline, $absentRecord, $objects))
$absentLease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $absentPlan)
$result = $absentLease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
if (!$result.Success -or [IO.File]::Exists($modded)) { throw 'Absent modded file was not restored accurately.' }
$absentLease.Dispose()
Assert-Bytes $save ([byte[]](1,2,3)); Assert-Bytes $modded ([byte[]](4,5))

$objects.Remove($emptyHash) | Out-Null
$missingRejected = $false
try { $null = $lifecyclePlanMethod.Invoke($provider, [object[]]@($baseline, $record, $objects)) }
catch { $missingRejected = $_.Exception.ToString().Contains('Lifecycle slot object is missing.') }
if (!$missingRejected) { throw 'Missing lifecycle object was not rejected.' }
Assert-Bytes $save ([byte[]](1,2,3)); Assert-Bytes $modded ([byte[]](4,5))

$conflictLease = [HollowKnightTAS.Runtime.ReplaySave.DedicatedTasSlotLease]::new($provider, $absentPlan)
$conflictInstall = $conflictLease.Install([HollowKnightTAS.Runtime.ReplaySave.UserOverwriteApproval]::Approved)
if (!$conflictInstall.Success) { throw $conflictInstall.Error }
[IO.File]::WriteAllBytes($modded, [byte[]](99,98))
$conflictRejected = $false
try { $conflictLease.Rollback() }
catch { $conflictRejected = $_.Exception.ToString().Contains('Slot changed after installation') }
if (!$conflictRejected) { throw 'Rollback overwrote a post-install change.' }
Assert-Bytes $save $slotBytes; Assert-Bytes $modded ([byte[]](99,98))
Assert-Bytes (Join-Path $conflictInstall.BackupDirectory 'original.dat') ([byte[]](1,2,3))
Assert-Bytes (Join-Path $conflictInstall.BackupDirectory 'original.modded.json') ([byte[]](4,5))
[pscustomobject]@{ Result = 'PASS'; Cases = 8; IsolatedDirectory = $root }
