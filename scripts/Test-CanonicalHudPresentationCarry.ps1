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
 function NewData {
  $data=[Activator]::CreateInstance($dataType,$true)
  $dataType.GetField('Schema').SetValue($data,$schema)
  $dataType.GetField('OriginalAssetOrigins').SetValue($data,('a'*64))
  $dataType.GetField('NativeOwners').SetValue($data,[string[]](0..39|ForEach-Object{"owner$_"}))
  $dataType.GetField('GameObjectOwners').SetValue($data,[string[]](0..17|ForEach-Object{"go$_"}))
  $dataType.GetField('ActiveSelf').SetValue($data,[bool[]]::new(18))
  $dataType.GetField('Lifecycle').SetValue($data,[byte[]]::new(200))
  $dataType.GetField('FieldOwners').SetValue($data,[string[]](0..2400|ForEach-Object{"field$_"}))
  $rows=[object[][]]::new(40)
  for($i=0;$i-lt40;$i++){$rows[$i]=[object[]]::new($(if($i-eq0){61}else{60}))}
  $shared=[object[]]@('source-alias',[int]17)
  $rows[0][0]=$shared;$rows[1][0]=$shared
  $dataType.GetField('FieldValues').SetValue($data,$rows)
  return $data
 }
 $source=NewData
 $rootsMethod=$carry.GetMethod('Roots',$flags)
 $roots=$rootsMethod.Invoke($null,[object[]]@($source))
 $codecType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalCarryGraphCodec',$true)
 $readonlyType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalMarkerReadonlyCarry',$true)
 $types=[Type[]](@($carry.GetField('DataTypes',$flags).GetValue($null))+@($readonlyType.GetField('DataTypes',$flags).GetValue($null))+@([int[]],[float[]],[UnityEngine.Vector3]))
 $codec=$codecType.GetConstructors($flags)[0].Invoke([object[]]@($types,$schema,$null,$null,$null,$true,$null))
 Add-Type 'public static class HudCarryNullBinding { public static string Bind(object value) { return null; } }'
 $bind=[Delegate]::CreateDelegate([Func[object,string]],[HudCarryNullBinding].GetMethod('Bind'))
 $graph=$codec.GetType().GetMethod('Capture',$flags).Invoke($codec,[object[]]@($roots,$bind,$null))
 if($graph.Unsupported.Length-ne0){throw ('Unsupported graph: '+($graph.Unsupported-join';'))}
 $encoded=$codec.GetType().GetMethod('Encode',$flags).Invoke($codec,[object[]]@(,$graph))
 $decoded=$codec.GetType().GetMethod('Decode',$flags).Invoke($codec,[object[]]@(,$encoded))
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
 [ordered]@{productionSchema=$schema;encodedBytes=$encoded.Length;productionWireRoundtrip=$true;sharedAliasesPreserved=$true;malformedDirectoriesRejected=$rejected;exactSiblingProjections=14;invalidSiblingProjectionsRejected=$invalidProjection;frozenRegistryRequiresOriginalMono=$true;nativeMonoExecutionNotTested=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath
 [ordered]@{encodedBytes=$encoded.Length;productionWireRoundtrip=$true;sharedAliasesPreserved=$true;malformedDirectoriesRejected=$rejected;nativeMonoExecutionNotTested=$true}|ConvertTo-Json
}finally{}
