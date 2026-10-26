param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
foreach($path in Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine*.dll'){[Reflection.Assembly]::LoadFrom($path.FullName)|Out-Null}
foreach($name in @('Assembly-CSharp.dll','PlayMaker.dll','Newtonsoft.Json.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $managed $name))|Out-Null}
[Reflection.Assembly]::LoadFrom((Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'))|Out-Null
$runtime=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'))
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$inputs=$runtime.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHudBorderInputs',$true)
$dataType=$inputs.GetNestedType('Data',$flags);$rectType=$inputs.GetNestedType('RectData',$flags)
$layout=$inputs.GetMethod('InputLayout',$flags).Invoke($null,@())
$schema=$inputs.GetProperty('SchemaIdentity',$flags).GetValue($null)
$font=[Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([TMPro.TMP_FontAsset])
$material=[Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([UnityEngine.Material])
$registered=[Collections.Generic.HashSet[Type]]::new()
foreach($type in $inputs.GetField('DataTypes',$flags).GetValue($null)){[void]$registered.Add($type)}
function AddValueType([Type]$type){
 if(-not$type.IsValueType-or$type.IsPrimitive-or$type.IsEnum-or$registered.Contains($type)){return}
 [void]$registered.Add($type)
 foreach($field in $type.GetFields([Reflection.BindingFlags]'Instance,Public,NonPublic')){AddValueType $field.FieldType}
}
foreach($row in $layout.Values){foreach($field in $row){AddValueType $field.FieldType;if($field.FieldType.IsEnum){[void]$registered.Add($field.FieldType)}}}
foreach($field in $rectType.GetFields()){AddValueType $field.FieldType}
function NewInput{
 $data=[Activator]::CreateInstance($dataType,$true)
 $dataType.GetField('Schema').SetValue($data,$schema)
 $dataType.GetField('Owners').SetValue($data,[string[]]@($layout.Keys))
 $names=[Collections.Generic.List[string]]::new();$values=[object[][]]::new(10);$i=0
 foreach($entry in $layout.GetEnumerator()){
  $values[$i]=[object[]]::new($entry.Value.Length);$k=0
  foreach($field in $entry.Value){
   $names.Add($entry.Key+'/'+$field.DeclaringType.FullName+'/'+$field.Name)
   $value=if($field.FieldType.IsValueType){[Activator]::CreateInstance($field.FieldType)}elseif($field.FieldType-eq[string]){''}else{$null}
   if($field.Name-eq'm_fontAsset'){$value=$font};if($field.Name-eq'm_sharedMaterial'){$value=$material}
   $values[$i][$k]=$value;$k++
  };$i++
 }
 $dataType.GetField('FieldNames').SetValue($data,$names.ToArray());$dataType.GetField('Values').SetValue($data,$values)
 $dataType.GetField('BaseFontSizes').SetValue($data,[float[]]@(2.5,8.3,22))
 $dataType.GetField('RendererEnabled').SetValue($data,[bool[]]@($false,$true,$false))
 $rects=[Array]::CreateInstance($rectType,3);for($i=0;$i-lt3;$i++){$rects.SetValue([Activator]::CreateInstance($rectType,$true),$i)}
 $dataType.GetField('Rects').SetValue($data,$rects);$dataType.GetField('PaneStrings').SetValue($data,[string[]]@('Charm','Inventory','Quest'))
 return $data
}
$validate=$inputs.GetMethod('Validate',$flags);$source=NewInput;$validate.Invoke($null,[object[]]@($source))|Out-Null
Add-Type 'public static class BorderFixtureAssets { public static object Font, Material; public static string Bind(object v){ return object.ReferenceEquals(v,Font)?"font":object.ReferenceEquals(v,Material)?"material":null; } public static object Resolve(string s){return s=="font"?Font:s=="material"?Material:throw new System.Exception("foreign");} }'
[BorderFixtureAssets]::Font=$font;[BorderFixtureAssets]::Material=$material
$bind=[Delegate]::CreateDelegate([Func[object,string]],[BorderFixtureAssets].GetMethod('Bind'))
$resolve=[Delegate]::CreateDelegate([Func[string,object]],[BorderFixtureAssets].GetMethod('Resolve'))
$codecType=$runtime.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalCarryGraphCodec',$true)
$codec=$codecType.GetConstructors($flags)[0].Invoke([object[]]@([Type[]]@($registered),$schema,$null,$null,$null,$true,$null))
$roots=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal);$roots.Add('HudBorder/PreAwakeInputs',$source)
$graph=$codecType.GetMethod('Capture',$flags).Invoke($codec,[object[]]@($roots,$bind,$null))
if($graph.Unsupported.Length-ne0){throw ($graph.Unsupported-join';')}
$bytes=$codecType.GetMethod('Encode',$flags).Invoke($codec,[object[]]@(,$graph))
$decoded=$codecType.GetMethod('Decode',$flags).Invoke($codec,[object[]]@(,$bytes))
$target=NewInput;$targetRoots=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal);$targetRoots.Add('HudBorder/PreAwakeInputs',$target)
$plan=$codecType.GetMethod('PrepareGraph',$flags).Invoke($codec,[object[]]@($decoded,$targetRoots,$resolve,$null))
$plan.GetType().GetMethod('Apply',$flags).Invoke($plan,@())|Out-Null;$plan.GetType().GetMethod('VerifyReadback',$flags).Invoke($plan,@())|Out-Null
$validate.Invoke($null,[object[]]@($target))|Out-Null
$reject=0
foreach($kind in @('old-schema','missing-owner','wrong-field','bad-type','bad-baseline','null-font','null-rect','wrong-row')){
 $bad=NewInput
 switch($kind){
  old-schema{$bad.Schema='old'} missing-owner{$bad.Owners=[string[]]@('one')} wrong-field{$bad.FieldNames[0]='foreign'}
  bad-type{$bad.Values[0][0]=[IO.MemoryStream]::new()} bad-baseline{$bad.BaseFontSizes[0]=[float]::NaN}
  null-font{for($i=0;$i-lt10;$i++){for($k=0;$k-lt$layout[$bad.Owners[$i]].Length;$k++){if($layout[$bad.Owners[$i]][$k].Name-eq'm_fontAsset'){$bad.Values[$i][$k]=$null}}}}
  null-rect{$bad.Rects[0]=$null} wrong-row{$bad.Values[0]=[object[]]::new(1)}
 }
 try{$validate.Invoke($null,[object[]]@($bad))|Out-Null;throw "Accepted $kind"}catch{if($_.Exception.InnerException-is[IO.InvalidDataException]){$reject++}else{throw}}
}
[ordered]@{logicalOwners=10;logicalFields=204;productionDataAndWireRoundtrip=$true;sourceAssetsExplicit=$true;malformedInputsRejected=$reject;encodedBytes=$bytes.Length;nativeCallsOrGameRun=$false;originalMonoRegistryNotExecuted=$true}|ConvertTo-Json|Set-Content -LiteralPath $OutputPath
Get-Content -LiteralPath $OutputPath
