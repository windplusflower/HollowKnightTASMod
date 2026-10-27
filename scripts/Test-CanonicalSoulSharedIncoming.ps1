$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver = [ResolveEventHandler]{ param($sender,$event) $name=([Reflection.AssemblyName]$event.Name).Name+'.dll'; foreach($dir in @($bin,$managed)){ $path=Join-Path $dir $name; if([IO.File]::Exists($path)){ return [Reflection.Assembly]::LoadFrom($path) } }; return $null }
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type 'public sealed class SoulIncomingFixture { public object Value; } public static class SoulBenchFixture { public static bool Removed; public static void Before() {} public static void Verify() { if(!Removed) throw new System.InvalidOperationException("callback still present"); } }'
try {
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $type=$asm.GetType('HollowKnightTAS.Runtime.FullRun.LoadingGeoIncomingReferences',$true)
 $flags=[Reflection.BindingFlags]'Instance,NonPublic,Public'
 $tracker=[Activator]::CreateInstance($type,$true)
 $value=[object]::new(); $foreign=[object]::new(); $holder=[SoulIncomingFixture]::new(); $holder.Value=$value
 $type.GetField('soulSample',$flags).SetValue($tracker,$true)
 $type.GetField('owned',$flags).GetValue($tracker).Add($value,'$/Soul/node[1]/root')
 $type.GetField('soulMembers',$flags).GetValue($tracker).Add($value)|Out-Null
 $type.GetField('soulGlobalCandidates',$flags).GetValue($tracker).Add($value)|Out-Null
 $field=[SoulIncomingFixture].GetField('Value')
 $type.GetMethod('Observe',$flags).Invoke($tracker,[object[]]@($value,'holder.Value','foreign-root'))
 $type.GetMethod('ObserveSlot',$flags).Invoke($tracker,[object[]]@($holder,$field,0,$value,'holder.Value','foreign-root'))
 $type.GetMethod('Observe',$flags).Invoke($tracker,[object[]]@($value,'holder.Value','foreign-root'))
 $type.GetMethod('ObserveSlot',$flags).Invoke($tracker,[object[]]@($holder,$field,0,$value,'holder.Value','foreign-root'))
 $other=[SoulIncomingFixture]::new(); $other.Value=$value
 try {$type.GetMethod('ObserveSlot',$flags).Invoke($tracker,[object[]]@($other,$field,0,$value,'holder.Value','foreign-root'));throw 'Different slot with same path accepted'} catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 $map=[Collections.Generic.Dictionary[object,string]]::new(); $map.Add($value,'R2/SoulOrb/global-gameobject[14]')
 $paths=[string[]]$type.GetMethod('SoulSharedPaths',$flags).Invoke($tracker,[object[]]@($map))
 if($paths.Count-ne1-or$paths[0]-ne"R2/SoulOrb/global-gameobject[14]`nholder.Value"){throw 'Original incoming path identity differs'}
 $require=$type.GetMethod('RequireSoulSharedPaths',$flags)
 $require.Invoke($tracker,[object[]]@($map,$paths))
 $bad=[string[]]@('wrong')
 try {$require.Invoke($tracker,[object[]]@($map,$bad));throw 'Changed path accepted'} catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 $holder.Value=$foreign
 try {$require.Invoke($tracker,[object[]]@($map,$paths));throw 'Changed slot accepted'} catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 $holder.Value=$value; $map.Clear()
 try {$require.Invoke($tracker,[object[]]@($map,$paths));throw 'Foreign shared owner accepted'} catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 $type.GetField('soulReferenceBytes',$flags).SetValue($tracker,4194304)
 try {$type.GetMethod('Observe',$flags).Invoke($tracker,[object[]]@($value,'another.Value','foreign-root'));throw 'Index byte overflow accepted'} catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 $pm=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'PlayMaker.dll'))
 $game=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
 $varType=$pm.GetType('HutongGames.PlayMaker.FsmGameObject',$true);$ownerType=$pm.GetType('HutongGames.PlayMaker.FsmOwnerDefault',$true)
 $raw=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($varType);$owner=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($ownerType)
 $gameObjectField=$ownerType.GetField('gameObject',$flags);$gameObjectField.SetValue($owner,$raw)
 $target=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($game.GetType('HutongGames.PlayMaker.Actions.Tk2dPlayAnimationWithEvents',$true))
 $heroType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHeroAnimationCallbacks',$true)
 $proofType=$heroType.GetNestedType('DetachedBenchProof',[Reflection.BindingFlags]'NonPublic');$proof=[Activator]::CreateInstance($proofType,$true)
 $proofType.GetField('Target',$flags).SetValue($proof,$target)
 $proofType.GetField('VerifyBefore',$flags).SetValue($proof,[Delegate]::CreateDelegate([Action],[SoulBenchFixture].GetMethod('Before')))
 $proofType.GetField('VerifyRemoved',$flags).SetValue($proof,[Delegate]::CreateDelegate([Action],[SoulBenchFixture].GetMethod('Verify')))
 $suffix='state[20]:Init Resting.actionData.fsmOwnerDefaultParams._items[0].gameObject'
 $proofType.GetField('Slots',$flags).GetValue($proof).Add($suffix,$owner)
 $tr=[Activator]::CreateInstance($type,$true);$type.GetField('soulSample',$flags).SetValue($tr,$true)
 $type.GetField('soulBench',$flags).SetValue($tr,$proof);$type.GetField('soulBenchId',$flags).SetValue($tr,'detached-fsm[0]')
 $type.GetField('owned',$flags).GetValue($tr).Add($raw,'global');$type.GetField('soulGlobalCandidates',$flags).GetValue($tr).Add($raw)|Out-Null
 $path='detached-fsm[0]/'+$suffix
 $type.GetMethod('Observe',$flags).Invoke($tr,[object[]]@($raw,$path,'detached-fsm[0]/state[20]'))
 $type.GetMethod('ObserveSlot',$flags).Invoke($tr,[object[]]@($owner,$gameObjectField,0,$raw,$path,'detached-fsm[0]/state[20]'))
 $refs=$type.GetField('soulBenchReferences',$flags).GetValue($tr)
 $refs.Add([Tuple[object,string,string]]::new($target,'DontDestroyOnLoad/Knight[0]:tk2dSpriteAnimator[0].AnimationCompleted.target[0]','hero'))
 $globals=[Collections.Generic.Dictionary[object,string]]::new();$globals.Add($raw,'R2/SoulOrb/global-gameobject[14]')
 $transition=$type.GetMethod('ValidateSoulSharedTransition',$flags).Invoke($tr,[object[]]@($globals,[string[]]@()))
 try {$transition.Invoke();throw 'Live detached callback accepted after restore'} catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 [SoulBenchFixture]::Removed=$true;$transition.Invoke()
 $refs.Add([Tuple[object,string,string]]::new($target,'foreign.edge','foreign'))
 try {$type.GetMethod('ValidateSoulSharedTransition',$flags).Invoke($tr,[object[]]@($globals,[string[]]@()));throw 'Unknown detached holder accepted'} catch {if($_.Exception.InnerException-isnot[IO.InvalidDataException]){throw}}
 Write-Output 'Soul original incoming slot identity, path, mutation and foreign-owner rejection contracts passed.'
} finally { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
