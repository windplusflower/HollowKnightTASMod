$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($sender,$e) $n=([Reflection.AssemblyName]$e.Name).Name+'.dll';foreach($d in @($bin,$managed)){$p=Join-Path $d $n;if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type 'public sealed class MeshAuditProjectionFixture { public int vertexCount; public object[] uv; public object[] submeshes; public object[] blendShapes; public object vertices; } public sealed class MeshAuditCopiedHashFixture { public int count, stride; public string sha256; }'
try {
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'));$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
 $selector=$asm.GetType('HollowKnightTAS.Runtime.FullRun.LoadingGameplayOracle',$true).GetMethod('MeshAuditSelected',[Reflection.BindingFlags]'Static,Public,NonPublic')
 foreach($case in @(@($true,$true,[long]2200,[long]2200,$true),@($true,$true,[long]2201,[long]2200,$false),@($true,$false,[long]2200,[long]2200,$false),@($false,$true,[long]2200,[long]2200,$false))){
  if($selector.Invoke($null,[object[]]@($case[0],$case[1],$case[2],$case[3]))-ne$case[4]){throw 'Mesh audit first-suffix sample selector differs'}
 }
 $type=$asm.GetType('HollowKnightTAS.Runtime.FullRun.LoadingMeshValueAudit',$true);$audit=[Activator]::CreateInstance($type,$true)
 $read=$type.GetMethod('CaptureProjection',$flags)
 $a=[MeshAuditProjectionFixture]::new();$a.vertexCount=4;$a.uv=[object[]]@('one');$a.submeshes=[object[]]::new(0);$a.blendShapes=$a.submeshes
 $b=[MeshAuditProjectionFixture]::new();$b.vertexCount=4;$b.uv=[object[]]@('one');$b.submeshes=[object[]]::new(0);$b.blendShapes=$b.submeshes
 $x=$read.Invoke($audit,[object[]]@($a,0));$y=$read.Invoke($audit,[object[]]@($b,0))
 if(($x|ConvertTo-Json -Depth 15 -Compress)-ne($y|ConvertTo-Json -Depth 15 -Compress)){throw 'Equal independent projections differ'}
 if($x.emptyArrayAliasGroups.Count-ne1-or$x.emptyArrayAliasGroups[0].Count-ne2){throw 'Projection empty-array alias not preserved'}
 $b.vertexCount=5;$z=$read.Invoke($audit,[object[]]@($b,0))
 if(($x.fields|Where-Object name -eq 'vertexCount').digest-eq($z.fields|Where-Object name -eq 'vertexCount').digest){throw 'Changed scalar did not alter its field digest'}
 $b.vertexCount=4;$b.blendShapes=[object[]]::new(0);$z=$read.Invoke($audit,[object[]]@($b,0))
 if($z.emptyArrayAliasGroups.Count-ne2){throw 'Distinct empty arrays were merged'}
 $copy=[MeshAuditCopiedHashFixture]::new();$copy.count=4;$copy.stride=12;$copy.sha256='a'*64;$a.vertices=$copy
 $z=$read.Invoke($audit,[object[]]@($a,0));$v=$z.fields|Where-Object name -eq 'vertices'
 if($v.digest-ne$copy.sha256-or$v.copiedArrayCount-ne4-or$v.copiedArrayStride-ne12){throw 'Original geometry hash was not reused exactly'}
 $audit.Dispose();Write-Output 'Independent Mesh projection field values and same/distinct empty-array alias contracts passed.'
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
