param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){throw 'Use Windows PowerShell for the original dictionary field ABI contract.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolve=[ResolveEventHandler]{param($sender,$eventArgs)
 $name=([Reflection.AssemblyName]$eventArgs.Name).Name+'.dll'
 foreach($root in @($bin,$managed)){$file=Join-Path $root $name;if([IO.File]::Exists($file)){return [Reflection.Assembly]::LoadFrom($file)}}
 return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolve)
try {
 $runtime=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $type=$runtime.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHudBorderQueuePartition',$true)
 $flags=[Reflection.BindingFlags]'Static,NonPublic'
 $copy=$type.GetMethod('CopyOriginalLazyCache',$flags)
 $storage=$type.GetMethod('CompleteQueueStorage',$flags)
 $source=New-Object 'System.Collections.Generic.Dictionary[int,int]'
 $target=New-Object 'System.Collections.Generic.Dictionary[int,int]'
 $foreign=New-Object 'System.Collections.Generic.Dictionary[int,int]'
 $source.Add(7,7)
 $passed=0
 foreach($kind in @('keys','values')){
  $cache=if($kind-eq'keys'){$source.Keys}else{$source.Values}
  # Preserve collection identity; PowerShell pipeline enumeration would discard it.
  $field=$source.GetType().GetField($kind,[Reflection.BindingFlags]'Instance,NonPublic')
  $cache=$field.GetValue($source)
  $result=$copy.Invoke($null,[object[]]@($source.PSObject.BaseObject,$target.PSObject.BaseObject,[string]$kind,$cache.PSObject.BaseObject))
  $owner=$cache.GetType().GetField('dictionary',[Reflection.BindingFlags]'Instance,NonPublic')
  if([object]::ReferenceEquals($cache,$result)-or-not[object]::ReferenceEquals($owner.GetValue($result),$target)-or-not[object]::ReferenceEquals($owner.GetValue($cache),$source)){throw 'Cache source/copy owner identity differs'}
  $passed++
  try{$null=$copy.Invoke($null,[object[]]@($foreign.PSObject.BaseObject,$target.PSObject.BaseObject,[string]$kind,$cache.PSObject.BaseObject));throw 'Foreign cache owner admitted'}catch {if($_.Exception.Message-notmatch "Foreign TMP collection cache|foreign dictionary owner"){throw};$passed++}
 }
 try{$null=$copy.Invoke($null,[object[]]@($source,$target,'other',$source.Keys));throw 'Foreign cache kind admitted'}catch {if($_.Exception.Message-notmatch "Foreign TMP collection cache|foreign dictionary owner"){throw};$passed++}
 $queues=[object[]]@($source,(New-Object 'System.Collections.Generic.List[int]'),$foreign,(New-Object 'System.Collections.Generic.List[int]'))
 for($q=0;$q-lt$queues.Length;$q++){$queues[$q]=$queues[$q].PSObject.BaseObject}
 $rows=$storage.Invoke($null,[object[]]@(,$queues))
 if($rows.Length-ne4-or$rows[0].Length-ne10-or$rows[1].Length-ne4){throw 'Complete raw field projection differs'}
 $fields=@($source.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic,Public')|Sort-Object Name)
 for($i=0;$i-lt$fields.Count;$i++){
  $expected=$fields[$i].GetValue($source);$actual=$rows[0][$i]
  if($fields[$i].FieldType.IsValueType){if(-not[object]::Equals($expected,$actual)){throw 'Raw value field omitted'}}
  elseif(-not[object]::ReferenceEquals($expected,$actual)){throw 'Raw storage reference omitted'}
 }
 $passed++
 [ordered]@{passed=$passed;failed=0;productionStaticMethods=$true;dictionaryFields=10;listFields=4;sourceCacheUnchanged=$true;copyBackReferenceExact=$true;foreignOwnerRejected=$true;nativeMonoLifecycleTested=$false;hostCorlibMvid=[string][System.Collections.Generic.Dictionary[int,int]].Module.ModuleVersionId}|ConvertTo-Json|Set-Content -LiteralPath $OutputPath -Encoding utf8
 Write-Output "Queue cache contracts passed: $passed"
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve)}
