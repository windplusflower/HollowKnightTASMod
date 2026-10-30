$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($sender,$event) $name=([Reflection.AssemblyName]$event.Name).Name+'.dll';foreach($directory in @($bin,$managed)){$path=Join-Path $directory $name;if([IO.File]::Exists($path)){return [Reflection.Assembly]::LoadFrom($path)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
 $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $method=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.LoadingVisualMeshBatchAudit',$true).GetMethod('PoseBits',[Reflection.BindingFlags]'Static,NonPublic')
 $types=$method.GetParameters().ParameterType
 $negativeZero=[BitConverter]::ToSingle([BitConverter]::GetBytes([int]::MinValue),0)
 $position=[Activator]::CreateInstance($types[0],[object[]]@([single]1,$negativeZero,[single]0))
 $rotation=[Activator]::CreateInstance($types[1],[object[]]@([single]0,[single]0,[single]0,[single]1))
 $scale=[Activator]::CreateInstance($types[2],[object[]]@([single]1,[single]1,[single]1))
 $bits=$method.Invoke($null,[object[]]@($position,$rotation,$scale))
 if($bits.Count-ne10-or$bits[1]-ne[int]::MinValue-or$bits[2]-ne0){throw 'Pose copy lost signed-zero bits or layout'}
 $json=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Newtonsoft.Json.dll'))
 $serialize=$json.GetType('Newtonsoft.Json.JsonConvert',$true).GetMethod('SerializeObject',[type[]]@([object]))
 $text=$serialize.Invoke($null,[object[]]@(,$bits))
 if(($text|ConvertFrom-Json).Count-ne10-or$text.Contains('normalized')){throw 'Pose diagnostic JSON is not a finite bit-array'}
 Write-Output 'Actual candidate pose DTO serializes without Unity property traversal and preserves signed zero.'
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
