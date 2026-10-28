$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($sender,$e) $n=([Reflection.AssemblyName]$e.Name).Name+'.dll';foreach($d in @($bin,$managed)){$p=Join-Path $d $n;if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type 'public sealed class MeshRootEncodingFixture { public string id, type, digest; public int nodes; } public sealed class MeshRootMetadataFixture {}'
try {
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $type=$asm.GetType('HollowKnightTAS.Runtime.FullRun.LoadingInventoryRootEncoding',$true)
 $first=[MeshRootEncodingFixture]::new();$first.id='asset:UnityEngine.Mesh:[0]/asset-values';$first.type='UnityEngine.Mesh';$first.digest='first';$first.nodes=1
 $last=[MeshRootEncodingFixture]::new();$last.id='asset:UnityEngine.Mesh:[9]/asset-values';$last.type='UnityEngine.Mesh';$last.digest='last';$last.nodes=2
 $ctor=$type.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance')|Where-Object {$_.GetParameters().Count-eq1}
 $encoder=$ctor.Invoke([object[]]@(,[object[]]@($last,$first)))
 $payload=$type.GetMethod('EncodeSuffix',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($encoder,[object[]]@([MeshRootMetadataFixture]::new()))
 [ordered]@{inventory=$payload}|ConvertTo-Json -Depth 8 -Compress
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
