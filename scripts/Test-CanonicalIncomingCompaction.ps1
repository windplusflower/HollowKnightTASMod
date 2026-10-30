$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($s,$e) $name=([Reflection.AssemblyName]$e.Name).Name+'.dll';foreach($dir in @($bin,$managed)){$p=Join-Path $dir $name;if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
 Add-Type 'public sealed class IncomingCompactionFixture { public object Value; }'
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $type=$asm.GetType('HollowKnightTAS.Runtime.FullRun.LoadingGeoIncomingReferences',$true)
 $flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
 $observe=$type.GetMethod('Observe',$flags);$slot=$type.GetMethod('ObserveSlot',$flags)
 $field=[IncomingCompactionFixture].GetField('Value');$value=[object]::new();$owner=[IncomingCompactionFixture]::new();$owner.Value=$value
 function New-Tracker([string]$identity){
  $t=[Activator]::CreateInstance($type,$true)
  $type.GetField('owned',$flags).GetValue($t).Add($value,$identity)
  $type.GetField('owned',$flags).GetValue($t).Add($owner,'$/owner')
  $type.GetField('ownedRoots',$flags).SetValue($t,[string[]]@('internal'))
  return ,$t
 }
 $tracker=New-Tracker '$/value'
 for($i=0;$i-lt5000;$i++){
  $path='internal.edge['+$i+']'
  $slot.Invoke($tracker,[object[]]@($owner,$field,-1,$value,$path,'internal'))|Out-Null
  $observe.Invoke($tracker,[object[]]@($value,$path,'internal'))|Out-Null
 }
 if($type.GetField('references',$flags).GetValue($tracker).Count-ne0-or$type.GetField('persistentInternalSlotCount',$flags).GetValue($tracker)-ne5000){throw 'Exact owned internal edges were not compacted'}
 $foreign=[IncomingCompactionFixture]::new();$foreign.Value=$value
 $slot.Invoke($tracker,[object[]]@($foreign,$field,-1,$value,'internal.foreign','internal'))|Out-Null
 $observe.Invoke($tracker,[object[]]@($value,'internal.foreign','internal'))|Out-Null
 if($type.GetField('references',$flags).GetValue($tracker).Count-ne1){throw 'A root label hid an unowned containing object'}
 $prefix=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalMapKeyParentDirectory',$true).GetField('ContainerPrefix',[Reflection.BindingFlags]'Static,Public,NonPublic').GetRawConstantValue()
 $parent=New-Tracker ($prefix+'fixture')
 $slot.Invoke($parent,[object[]]@($owner,$field,-1,$value,'internal.container','internal'))|Out-Null
 $observe.Invoke($parent,[object[]]@($value,'internal.container','internal'))|Out-Null
 if($type.GetMethod('PathsForIdentity',$flags).Invoke($parent,[object[]]@($prefix+'fixture')).Length-ne1){throw 'Executable parent container binding was omitted'}
 $external=New-Tracker '$/value'
 for($i=0;$i-lt4096;$i++){
  $path='foreign.edge['+$i+']'
  $slot.Invoke($external,[object[]]@($foreign,$field,-1,$value,$path,'foreign'))|Out-Null
  $observe.Invoke($external,[object[]]@($value,$path,'foreign'))|Out-Null
 }
 $slot.Invoke($external,[object[]]@($foreign,$field,-1,$value,'foreign.edge[0]','foreign'))|Out-Null
 $observe.Invoke($external,[object[]]@($value,'foreign.edge[0]','foreign'))|Out-Null
 foreach($method in @($slot,$observe)){
  $rejected=$false
  try{if($method-eq$slot){$method.Invoke($external,[object[]]@($foreign,$field,-1,$value,'foreign.extra','foreign'))|Out-Null}else{$method.Invoke($external,[object[]]@($value,'foreign.extra','foreign'))|Out-Null}}catch{$rejected=$_.Exception.InnerException-is[InvalidOperationException]}
  if(-not$rejected){throw 'The original 4096 external bound was weakened'}
 }
 $rejected=$false
 try{$slot.Invoke($external,[object[]]@($owner,$field,-1,$value,'foreign.edge[0]','foreign'))|Out-Null}catch{$rejected=$_.Exception.InnerException-is[InvalidOperationException]}
 if(-not$rejected){throw 'A different writable slot with the same path was admitted'}
 Write-Output '5000 exact internal edges compacted; foreign owners and all parent bindings retained; 4096 external path/slot caps and duplicate identity guards passed.'
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
