param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($sender,$e) $n=([Reflection.AssemblyName]$e.Name).Name+'.dll';foreach($r in @($bin,$managed)){$p=Join-Path $r $n;if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
$temp=Join-Path ([IO.Path]::GetTempPath()) ('hktas-engine-lease-'+[Guid]::NewGuid().ToString('N')+'.bin')
try {
 $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $outer=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.LoadingEngineClockProbe',$true)
 $flags=[Reflection.BindingFlags]'NonPublic,Static'
 $lease=$outer.GetNestedType('ActualDValidation',[Reflection.BindingFlags]'NonPublic')
 $ctor=$lease.GetConstructor([Reflection.BindingFlags]'NonPublic,Instance',$null,[Type[]]@(),$null)
 $rejected=0
 foreach($name in @('actualDValidation','checkpointValidation')){
  $slot=$outer.GetField($name,$flags)
  $fake=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($slot.FieldType)
  $slot.SetValue($null,$fake)
  try {$ctor.Invoke([object[]]@())|Out-Null;throw 'Nested validation was admitted'}
  catch {if($_.Exception.InnerException-is[InvalidOperationException]){$rejected++}else{throw}}
  finally {$slot.SetValue($null,$null)}
 }
 [IO.File]::WriteAllBytes($temp,[byte[]]@(1,2,3))
 $hashType=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalEngineFileSha256',$true)
 $hash=$hashType.GetMethod('ComputeHash',$flags)
 $first=$null;$second=$null;$blocked=$false
 $stream=[IO.File]::Open($temp,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try {
  $first=[BitConverter]::ToString($hash.Invoke($null,[object[]]@($stream)))
  try {$writer=[IO.File]::Open($temp,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read);$writer.Dispose()}
  catch [IO.IOException] {$blocked=$true}
 } finally {$stream.Dispose()}
 if(-not$blocked){throw 'Read lease did not prohibit concurrent writes'}
 [IO.File]::WriteAllBytes($temp,[byte[]]@(1,2,4))
 $stream=[IO.File]::Open($temp,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try {$second=[BitConverter]::ToString($hash.Invoke($null,[object[]]@($stream)))}finally{$stream.Dispose()}
 if($first-eq$second){throw 'Next transaction did not rehash changed file'}
 $fake=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($lease)
 $instance=[Reflection.BindingFlags]'NonPublic,Instance'
 $stream=[IO.File]::Open($temp,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $lease.GetField('file',$instance).SetValue($fake,$stream)
 $handle=$stream.SafeFileHandle
 $lease.GetField('disposed',$instance).SetValue($fake,$true)
 $slot=$outer.GetField('actualDValidation',$flags);$slot.SetValue($null,$fake)
 try {$lease.GetMethod('Dispose').Invoke($fake,[object[]]@())|Out-Null;throw 'Faulted lease was admitted'}
 catch {if($_.Exception.InnerException-isnot[InvalidOperationException]){throw}}
 if($null-ne$slot.GetValue($null)-or-not$handle.IsClosed-or$stream.CanRead){throw 'Exceptional lease did not clear owner and close file'}
 [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Mono.Cecil.dll'))|Out-Null
 $cecil=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $bin 'HollowKnightTAS.dll'))
 try {
  $probe=$cecil.MainModule.Types|Where-Object Name -eq 'LoadingEngineClockProbe'
  $type=$probe.NestedTypes|Where-Object Name -eq 'ActualDValidation'
  $calls=@(($type.Methods|Where-Object Name -eq '.ctor').Body.Instructions|Where-Object {$_.Operand -is[Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ComputeHash'})
  $dispose=$type.Methods|Where-Object Name -eq 'Dispose'
  if($calls.Count-ne1-or@($dispose.Body.ExceptionHandlers|Where-Object HandlerType -eq 'Finally').Count-ne1){throw 'Hash count/finally cleanup wiring differs'}
  $session=$cecil.MainModule.Types|Where-Object Name -eq 'RuntimeFullRunSession'
  $uses=@($session.Methods|Where-Object {$_.HasBody}|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.OpCode.Code-eq'Newobj'-and $_.Operand.DeclaringType.FullName-eq$type.FullName})
  if($uses.Count-ne1){throw 'Actual-D single production entry differs'}
 } finally {$cecil.Dispose()}
 [ordered]@{nestedTransactionAndPriorScopeRejected=$rejected;exceptionClearsThreadOwnerAndClosesFile=$true;writeLeaseBlocked=$blocked;nextTransactionRehashesChangedFile=$true;productionFullHashCalls=1;productionActualDEntries=1;productionFinallyCleanup=$true;nativeModulePositivePathRequiresGame=$true;gameLaunched=$false}|ConvertTo-Json|Set-Content -LiteralPath $OutputPath -Encoding utf8
 Write-Output 'Actual D engine validation: narrow offline contracts passed; native positive path remains game-only.'
} finally {
 [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
 if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}
}

