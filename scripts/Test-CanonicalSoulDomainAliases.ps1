$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($sender,$e) $n=([Reflection.AssemblyName]$e.Name).Name+'.dll';foreach($dir in @($bin,$managed)){$p=Join-Path $dir $n;if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type @'
using System;
public sealed class SoulDomainShared { public int Value; }
public sealed class SoulDomainRoot { public SoulDomainShared First, Second; }
public static class SoulDomainFixture {
 public static object Source, Target;
 public static string Bind(object value) { return Object.ReferenceEquals(value, Source) ? "Soul/MainNode/1" : null; }
 public static object Resolve(string binding) { if(binding!="Soul/MainNode/1") throw new InvalidOperationException("unknown binding"); return Target; }
}
'@
try {
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $flags=[Reflection.BindingFlags]'Instance,Public,NonPublic,Static'
 $codecType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalCarryGraphCodec',$true)
 $ctor=$codecType.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance')|Where-Object {$_.GetParameters().Count-eq7}
 $codec=$ctor.Invoke([object[]]@([Type[]]@([SoulDomainRoot],[SoulDomainShared],[int]),'soul-domain-alias-contract',$null,$null,$null,$true,$null))
 $src=[SoulDomainShared]::new(); $src.Value=73; $root=[SoulDomainRoot]::new(); $root.First=$src; $root.Second=$src; [SoulDomainFixture]::Source=$src
 $roots=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal); $roots.Add('Soul/root',$root)
 $bind=[Delegate]::CreateDelegate([Func[object,string]],[SoulDomainFixture].GetMethod('Bind'))
 $graph=$codecType.GetMethod('Capture',$flags).Invoke($codec,[object[]]@($roots,$bind,$null,$null))
 if($graph.Unsupported.Length-ne0-or$graph.Nodes.Length-ne1-or$graph.Externals.Length-ne1){throw 'Shared source was copied or duplicated'}
 $targetShared=[SoulDomainShared]::new();$targetShared.Value=73;[SoulDomainFixture]::Target=$targetShared
 $target=[SoulDomainRoot]::new();$targetRoots=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal);$targetRoots.Add('Soul/root',$target)
 $resolve=[Delegate]::CreateDelegate([Func[string,object]],[SoulDomainFixture].GetMethod('Resolve'))
 $prepare=$codecType.GetMethod('PrepareGraph',$flags)
 $plan=$prepare.Invoke($codec,[object[]]@($graph,$targetRoots,$resolve,$null))
 $plan.GetType().GetMethod('Apply',$flags).Invoke($plan,@())
 $plan.GetType().GetMethod('VerifyReadback',$flags).Invoke($plan,@())
 if(-not[object]::ReferenceEquals($target.First,$targetShared)-or-not[object]::ReferenceEquals($target.Second,$targetShared)-or$targetShared.Value-ne73){throw 'Cross-domain alias or main value changed'}
 [SoulDomainFixture]::Target=[object]::new()
 try {$prepare.Invoke($codec,[object[]]@($graph,$targetRoots,$resolve,$null));throw 'Wrong external type accepted'} catch {if($_.Exception.InnerException-isnot[IO.InvalidDataException]){throw}}
 Write-Output 'Soul/main external alias, main object preservation and wrong-type rejection contracts passed.'
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
