param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
foreach($path in Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine*.dll') {[Reflection.Assembly]::LoadFrom($path.FullName)|Out-Null}
foreach($name in @('Assembly-CSharp.dll','PlayMaker.dll','Newtonsoft.Json.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $managed $name))|Out-Null}
[Reflection.Assembly]::LoadFrom((Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'))|Out-Null
try{
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'))
 $flags=[Reflection.BindingFlags]'Static,Instance,Public,NonPublic'
 $carry=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHudPresentationCarry',$true)
 $dataType=$carry.GetNestedType('Data',$flags)
 $schema=$carry.GetProperty('SchemaIdentity',$flags).GetValue($null)
 $directoryType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHudPresentationDirectory',$true)
 $managedTypes=$directoryType.GetField('ManagedTypes',$flags).GetValue($null)
 $metadata=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
 $objectKeys=@();foreach($node in $directoryType.GetProperty('Definitions',$flags).GetValue($null)){
  $key='$/'+$node.GetType().GetField('Path',$flags).GetValue($node);$objectKeys+=$key;$ordinals=@{}
  foreach($type in $node.GetType().GetProperty('Components',$flags).GetValue($node)){
   $ordinal=if($ordinals.ContainsKey($type.FullName)){$ordinals[$type.FullName]}else{0};$ordinals[$type.FullName]=$ordinal+1
   if($managedTypes-contains$type){$metadata.Add($key+':'+$type.FullName+'['+$ordinal+']',@($directoryType.GetMethod('ManagedFields',$flags).Invoke($null,[object[]]@($type))))}
  }
 }
 $metadataRows=@($metadata.Values);$nativeKeys=[string[]]@($metadata.Keys);$objectKeys=[string[]]$objectKeys;[Array]::Sort($objectKeys,[StringComparer]::Ordinal)
 if($metadataRows.Count-ne40-or($metadataRows|ForEach-Object Count|Measure-Object -Sum).Sum-ne2301){throw 'Original 40-owner field metadata closure differs'}
 function NewData {
  $data=[Activator]::CreateInstance($dataType,$true)
  $dataType.GetField('Schema').SetValue($data,$schema)
  $dataType.GetField('OriginalAssetOrigins').SetValue($data,('a'*64))
  $dataType.GetField('NativeOwners').SetValue($data,$nativeKeys)
  $dataType.GetField('GameObjectOwners').SetValue($data,$objectKeys)
  $dataType.GetField('ActiveSelf').SetValue($data,[bool[]]::new(18))
  $dataType.GetField('Lifecycle').SetValue($data,[byte[]]::new(200))
  $fieldOwners=@();for($owner=0;$owner-lt40;$owner++){foreach($field in $metadataRows[$owner]){$fieldOwners+=$nativeKeys[$owner]+"/"+$field.DeclaringType.FullName+"/"+$field.Name}}
  $dataType.GetField('FieldOwners').SetValue($data,[string[]]$fieldOwners)
  $rows=[object[][]]::new(40)
  for($i=0;$i-lt40;$i++){$rows[$i]=[object[]]::new($metadataRows[$i].Count)}
  $shared=[object[]]@('source-alias',[int]17)
  $rows[0][0]=$shared;$rows[1][0]=$shared
  $fsmType=([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name-eq'PlayMaker'}).GetType('HutongGames.PlayMaker.Fsm',$true)
  for($owner=0;$owner-lt40;$owner++){for($field=0;$field-lt$metadataRows[$owner].Count;$field++){
   if($metadataRows[$owner][$field].DeclaringType.FullName-eq'PlayMakerFSM'-and$metadataRows[$owner][$field].Name-eq'fsm'){
    $rows[$owner][$field]=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($fsmType)
   }
  }}
  $dataType.GetField('FieldValues').SetValue($data,$rows)
  return $data
 }
 $source=NewData
 $rootsMethod=$carry.GetMethod('Roots',$flags)
 $roots=$rootsMethod.Invoke($null,[object[]]@($source))
 $codecType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalCarryGraphCodec',$true)
 $readonlyType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalMarkerReadonlyCarry',$true)
 $types=[Type[]](@($carry.GetField('DataTypes',$flags).GetValue($null))+@($readonlyType.GetField('DataTypes',$flags).GetValue($null))+@([int[]],[float[]],[UnityEngine.Vector3],([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name-eq'PlayMaker'}).GetType('HutongGames.PlayMaker.Fsm')))
 $fsmContractType=([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name-eq'PlayMaker'}).GetType('HutongGames.PlayMaker.Fsm')
 $types=[Type[]](@($types)+@([UnityEngine.RaycastHit],[UnityEngine.Vector2])+@($fsmContractType.GetFields($flags)|Where-Object {$_.FieldType.IsEnum}|ForEach-Object FieldType))
 $codec=$codecType.GetConstructors($flags)[0].Invoke([object[]]@($types,$schema,$null,$null,$null,$true,$null))
 Add-Type 'public static class HudCarryNullBinding { public static string Bind(object value) { if(value.GetType().FullName=="HutongGames.PlayMaker.Fsm") throw new System.IO.InvalidDataException("foreign-FSM-needs-explicit-owner-binding"); return null; } }'
 $bind=[Delegate]::CreateDelegate([Func[object,string]],[HudCarryNullBinding].GetMethod('Bind'))
 $graph=$codec.GetType().GetMethod('Capture',$flags).Invoke($codec,[object[]]@($roots,$bind,$null))
 if($graph.Unsupported.Length-ne0){throw ('Unsupported graph: '+($graph.Unsupported-join';'))}
 $encoded=$codec.GetType().GetMethod('Encode',$flags).Invoke($codec,[object[]]@(,$graph))
 $decoded=$codec.GetType().GetMethod('Decode',$flags).Invoke($codec,[object[]]@(,$encoded))
 $session=$directoryType.GetMethod('BeginMetadataSession',$flags).Invoke($null,@())
 try{
  if($carry.GetProperty('SchemaIdentity',$flags).GetValue($null)-cne$schema){throw 'Cached original HUD schema changed'}
  $borderType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHudBorderInputs',$true)
  $null=$borderType.GetProperty('SchemaIdentity',$flags).GetValue($null)
  $tmpType=([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name-eq'Assembly-CSharp'}).GetType('TMPro.TextMeshPro')
  $array=$directoryType.GetMethod('ManagedFields',$flags).Invoke($null,[object[]]@($tmpType));$first=$array[0];$array[0]=$null
  $fresh=$directoryType.GetMethod('ManagedFields',$flags).Invoke($null,[object[]]@($tmpType))
  if($fresh[0]-ne$first){throw 'Caller mutation poisoned session metadata'}
  $cachedRoots=$rootsMethod.Invoke($null,[object[]]@($source))
  $cachedGraph=$codec.GetType().GetMethod('Capture',$flags).Invoke($codec,[object[]]@($cachedRoots,$bind,$null))
  $cachedBytes=$codec.GetType().GetMethod('Encode',$flags).Invoke($codec,[object[]]@(,$cachedGraph))
  if(-not[Linq.Enumerable]::SequenceEqual[byte]($encoded,$cachedBytes)){throw 'Cached original HUD graph wire changed'}
 }finally{$session.Dispose()}
 if($session.GetType().GetField('fields',$flags).GetValue($session).Count-ne0-or$session.GetType().GetField('schemas',$flags).GetValue($session).Count-ne0){throw 'Template session metadata survived disposal'}
 try{$session.GetType().GetMethod('Schema',$flags).Invoke($session,[object[]]@('old',$null))|Out-Null;throw 'Expired session metadata reused'}catch{if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 $destination=NewData
 $targetRoots=$rootsMethod.Invoke($null,[object[]]@($destination))
 $resolveExternal=[Func[string,object]]{param($key)throw "Unexpected external $key"}
 $plan=$codec.GetType().GetMethod('PrepareGraph',$flags).Invoke($codec,[object[]]@($decoded,$targetRoots,$resolveExternal,$null))
 $plan.GetType().GetMethod('Apply',$flags).Invoke($plan,@())|Out-Null
 $plan.GetType().GetMethod('VerifyReadback',$flags).Invoke($plan,@())|Out-Null
 $rows=$dataType.GetField('FieldValues').GetValue($destination)
 if(![object]::ReferenceEquals($rows[0][0],$rows[1][0])-or$rows[0][0][1]-ne17){throw 'Source shared alias roundtrip differs'}
 $rejected=0
 foreach($field in @('Schema','OriginalAssetOrigins','NativeOwners','FieldOwners','Lifecycle','GameObjectOwners','Registered')){
  $bad=NewData
  $f=$dataType.GetField($field)
  $value=switch($field){Schema{'foreign'} OriginalAssetOrigins{'b'*63} NativeOwners{[string[]]@('one')} FieldOwners{[string[]]@('one')} Lifecycle{[byte[]]::new(199)} GameObjectOwners{[string[]]@('one')} Registered{[string[]]@('foreign')}}
  $f.SetValue($bad,[Management.Automation.LanguagePrimitives]::ConvertTo($value,$f.FieldType))
  try{$rootsMethod.Invoke($null,[object[]]@($bad))|Out-Null;throw "Accepted malformed $field"}catch { if($_.Exception.InnerException -is [IO.InvalidDataException]){$rejected++}else{throw} }
 }
 # Same aggregate count cannot hide a missing field in one owner and padding in another.
 $bad=NewData;$badRows=$dataType.GetField('FieldValues').GetValue($bad)
 $badRows[0]=[object[]]::new($badRows[0].Length-1);$badRows[1]=[object[]]::new($badRows[1].Length+1)
 try{$rootsMethod.Invoke($null,[object[]]@($bad))|Out-Null;throw 'Accepted redistributed complete field total'}catch{if($_.Exception.InnerException-is[IO.InvalidDataException]){$rejected++}else{throw}}
 $fsmBindings=@($roots.Keys|Where-Object {$_.StartsWith('HudPresentation/Fsm/')})
 if($fsmBindings.Count-ne7){throw 'Original FSM explicit roots differ'}
 foreach($binding in $fsmBindings){
  $key=$binding.Substring('HudPresentation/Fsm/'.Length);$owner=[Array]::IndexOf($nativeKeys,$key)
  $field=[Array]::FindIndex([Reflection.FieldInfo[]]$metadataRows[$owner],[Predicate[Reflection.FieldInfo]]{param($f)$f.Name-eq'fsm'})
  if(-not[object]::ReferenceEquals($targetRoots[$binding],$rows[$owner][$field])){throw 'FSM root/field alias not preserved'}
 }
 $withoutFsm=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
 $withoutFsm.Add('HudPresentation/Source',$source)
 $unbound=$codec.GetType().GetMethod('Capture',$flags).Invoke($codec,[object[]]@($withoutFsm,$bind,$null))
 if(@($unbound.Unsupported|Where-Object {$_-like'*foreign-FSM-needs-explicit-owner-binding*'}).Count-ne7){throw 'Foreign raw FSM fallback was widened'}
 $guard=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalMapKeyTextExternalGuard',$true)
 $project=$guard.GetMethod('ProjectOriginalDetachedSibling',$flags)
 $original='{"id":7,"parent":9,"active":true,"hierarchy":false,"poseBits":[-2147483648,1,17],"sibling":17,"layer":5,"tag":"Untagged","isStatic":false,"hideFlags":0}'
 for($count=0;$count-le13;$count++){
  $actual=$project.Invoke($null,[object[]]@($original,$count))|ConvertFrom-Json -AsHashtable
  $expected=$original|ConvertFrom-Json -AsHashtable
  if($actual.sibling-ne(17-$count)){throw 'Detached sibling projection differs'}
  $actual.Remove('sibling');$expected.Remove('sibling')
  if(($actual|ConvertTo-Json -Compress)-cne($expected|ConvertTo-Json -Compress)){throw 'Detach projection changes another native field'}
 }
 $invalidProjection=0
 foreach($count in @(-1,14,18)){
  try{$project.Invoke($null,[object[]]@($original,$count))|Out-Null;throw 'Invalid sibling projection admitted'}
  catch{if($_.Exception.InnerException-is[InvalidOperationException]){$invalidProjection++}else{throw}}
 }
 [ordered]@{productionSchema=$schema;encodedBytes=$encoded.Length;productionWireRoundtrip=$true;sessionMetadataRawWireAndSchemaEqual=$true;metadataMutationAndExpiredSessionRejected=$true;sharedAliasesPreserved=$true;malformedDirectoriesRejected=$rejected;exactSiblingProjections=14;invalidSiblingProjectionsRejected=$invalidProjection;frozenRegistryRequiresOriginalMono=$true;explicitOriginalFsmRoots=7;rootFieldAliasesPreserved=$true;foreignFsmStillRejected=$true;nativeMonoExecutionNotTested=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath
 [ordered]@{encodedBytes=$encoded.Length;productionWireRoundtrip=$true;sessionMetadataRawWireAndSchemaEqual=$true;metadataMutationAndExpiredSessionRejected=$true;sharedAliasesPreserved=$true;malformedDirectoriesRejected=$rejected;explicitOriginalFsmRoots=7;rootFieldAliasesPreserved=$true;foreignFsmStillRejected=$true;nativeMonoExecutionNotTested=$true}|ConvertTo-Json
}finally{}
