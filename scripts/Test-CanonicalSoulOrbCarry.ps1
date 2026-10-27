param([Parameter(Mandatory=$true)][string]$OriginalDefinition,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolver=[ResolveEventHandler]{param($sender,$e) $n=([Reflection.AssemblyName]$e.Name).Name+'.dll';foreach($r in @($bin,$managed)){$p=Join-Path $r $n;if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
public static class SoulMetadataFixture {
 public static object Callback;
 public static string Bind(object value) { return Object.ReferenceEquals(value,Callback)?"contract-callback":null; }
 public static object MakeRaw(Assembly game, Assembly pm, string[] names, string[][] kinds) {
  var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
  var ft=pm.GetType("HutongGames.PlayMaker.Fsm",true);var st=pm.GetType("HutongGames.PlayMaker.FsmState",true);
  var at=pm.GetType("HutongGames.PlayMaker.FsmStateAction",true);
  var raw=FormatterServices.GetUninitializedObject(ft);var states=Array.CreateInstance(st,names.Length);
  for(int s=0;s<names.Length;s++) {
   var state=FormatterServices.GetUninitializedObject(st);st.GetField("name",flags).SetValue(state,names[s]);
   var actions=Array.CreateInstance(at,kinds[s].Length);
   for(int a=0;a<kinds[s].Length;a++)actions.SetValue(FormatterServices.GetUninitializedObject(game.GetType(kinds[s][a],false)??pm.GetType(kinds[s][a],true)),a);
   st.GetField("actions",flags).SetValue(state,actions);states.SetValue(state,s);
  }
  ft.GetField("states",flags).SetValue(raw,states);ft.GetField("activeState",flags).SetValue(raw,states.GetValue(8));return raw;
 }
 public static Type[] Types(IDictionary roots, MethodInfo fields, params Type[] extra) {
  var types=new HashSet<Type>();
  foreach(object owner in roots.Values) {
   types.Add(owner.GetType());
   foreach(FieldInfo f in (FieldInfo[])fields.Invoke(null,new object[]{owner.GetType()}))if(f.FieldType.IsValueType)types.Add(f.FieldType);
  }
  foreach(var type in extra)types.Add(type);
  var pending=new Queue<Type>(types);
  while(pending.Count!=0)foreach(FieldInfo f in (FieldInfo[])fields.Invoke(null,new object[]{pending.Dequeue()}))
   if(f.FieldType.IsValueType&&types.Add(f.FieldType))pending.Enqueue(f.FieldType);
  var result=new Type[types.Count];types.CopyTo(result);return result;
 }
}
'@
try {
 $asm=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $game=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
 $pm=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'PlayMaker.dll'))
 $flags=[Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
 $type=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalSoulOrbCarry',$true)
 $boundType=$type.GetMethod('BoundSoulType',$flags).Invoke($null,@())
 if($boundType.FullName-ne'GGCheckBoundSoul'){throw 'Original bound Soul action registry differs'}
 $dataType=$type.GetNestedType('Data',[Reflection.BindingFlags]'NonPublic')
 $fsmType=$pm.GetType('HutongGames.PlayMaker.Fsm',$true)
 $stateType=$pm.GetType('HutongGames.PlayMaker.FsmState',$true)
 $actionType=$pm.GetType('HutongGames.PlayMaker.FsmStateAction',$true)
 $baseTween=$game.GetType('HutongGames.PlayMaker.Actions.iTweenFsmAction',$true)
 $definitions=Get-Content -LiteralPath $OriginalDefinition -Raw|ConvertFrom-Json
 if($definitions.Count-ne28-or($definitions|ForEach-Object {$_.actions.Count}|Measure-Object -Sum).Sum-ne121){throw 'Independent original Soul directory differs'}
 $names=[string[]]@($definitions|ForEach-Object {$_.name})
 $kinds=[string[][]]@($definitions|ForEach-Object {,[string[]]$_.actions})
 $raw=[SoulMetadataFixture]::MakeRaw($game,$pm,$names,$kinds)
 $states=$fsmType.GetField('states',$flags).GetValue($raw)
 $roots=$type.GetMethod('ObjectRootsFromRaw',$flags).Invoke($null,[object[]]@($raw))
 if($roots.Count-ne150){throw 'Original 150 identity roots differ'}
 $tween=$stateType.GetField('actions',$flags).GetValue($states.GetValue(8)).GetValue(10)
 if(-not[object]::ReferenceEquals($tween,$roots['R2/SoulOrb/fsm/state[8]/action[10]'])){throw 'Original action identity was copied'}
 $actionType.GetField('fsm',$flags).SetValue($tween,$raw)
 $callbackType=$game.GetType('iTweenFSMEvents',$true)
 $callback=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($callbackType)
 $baseTween.GetField('itweenEvents',$flags).SetValue($tween,$callback)
 $baseTween.GetField('itweenID',$flags).SetValue($tween,200)
 $finish=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($pm.GetType('HutongGames.PlayMaker.FsmEvent',$true))
 $finish.GetType().GetField('name',$flags).SetValue($finish,'FINISHED')
 $baseTween.GetField('finishEvent',$flags).SetValue($tween,$finish)
 $data=[Activator]::CreateInstance($dataType,$true)
 foreach($pair in @(@('Present',$true),@('DonotFinish',$true),@('IsLooping',$false),@('Enabled',$true),@('Id',200),@('Counter',200),@('Callback',$callback),@('Action',$tween),@('FinishEvent',$finish))){$dataType.GetField($pair[0]).SetValue($data,$pair[1])}
 $dataType.GetField('Lifecycle').SetValue($data,[byte[]]@(1,0,1,0,0))
 $dataType.GetField('SharedSlots').SetValue($data,[string[]]@("R2/SoulOrb/global-gameobject[14]`nfixture"))
 $dataType.GetField('SharedPaths').SetValue($data,[string[]]@("R2/SoulOrb/global-gameobject[14]`nholder.Value"))
 $roots.Add('R2/SoulOrb/context',$data);$roots.Add('R6/FsmEvent-cache/FINISHED',$finish)
 $codecType=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalCarryGraphCodec',$true)
 $fieldMethod=$codecType.GetMethod('Fields',$flags)
 $types=[SoulMetadataFixture]::Types($roots,$fieldMethod,[Type[]]@($states.GetType(),$stateType.GetField('actions',$flags).FieldType,[byte[]],[string[]]))
 $ctor=$codecType.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance')|Where-Object {$_.GetParameters().Count-eq7}
 $codec=$ctor.Invoke([object[]]@([Type[]]@($types),'Soul-original-metadata-contract',$null,$null,$null,$true,$null))
 [SoulMetadataFixture]::Callback=$callback
 $bind=[Delegate]::CreateDelegate([Func[object,string]],[SoulMetadataFixture].GetMethod('Bind'))
 $graph=$codecType.GetMethod('Capture',$flags).Invoke($codec,[object[]]@($roots,$bind,$null,$null))
 if($graph.Unsupported.Length-ne0){throw ('Unexpected contract graph gaps: '+[string]::Join(';',$graph.Unsupported))}
 $check=$type.GetMethod('SourceRequiresCallback',$flags)
 $schema=$codecType.GetProperty('SchemaIdentity',$flags).GetValue($codec)
 if(-not$check.Invoke($null,[object[]]@($graph,$schema,'contract-callback'))){throw 'Original positive callback header rejected'}
 $encode=$codecType.GetMethod('Encode',$flags);$decode=$codecType.GetMethod('Decode',$flags)
 $bytes=$encode.Invoke($codec,[object[]]@($graph))
 $decodeArgs=[object[]]::new(1);$decodeArgs[0]=$bytes
 $clone=$decode.Invoke($codec,$decodeArgs)
 $domainGraph=$decode.Invoke($codec,$decodeArgs)
 $domainRoots=@($domainGraph.Roots|Where-Object {$_.Binding-ne'R6/FsmEvent-cache/FINISHED'})
 $rootArray=[Array]::CreateInstance($domainGraph.Roots.GetType().GetElementType(),$domainRoots.Count)
 for($i=0;$i-lt$domainRoots.Count;$i++){$rootArray.SetValue($domainRoots[$i],$i)}
 $domainGraph.Roots=$rootArray
 $domainValidate=$type.GetMethod('ValidateDomainRoots',$flags);$domainValidate.Invoke($null,[object[]]@($domainGraph))
 $oldRoot=$domainGraph.Roots[0];$badRoot=$oldRoot;$badRoot.Binding='foreign/root';$domainGraph.Roots[0]=$badRoot
 try {$domainValidate.Invoke($null,[object[]]@($domainGraph));throw 'Foreign Soul domain root accepted'} catch {if($_.Exception.InnerException-isnot[IO.InvalidDataException]){throw}}
 $domainGraph.Roots[0]=$oldRoot
 $mainValidator=$asm.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalEventCarryRoots',$true).GetMethod('ValidateGraph',$flags)
 try {$mainValidator.Invoke($null,[object[]]@($domainGraph));throw 'Soul domain accepted as main event graph'} catch {if($_.Exception.InnerException-isnot[IO.InvalidDataException]){throw}}
 $sourceStrings=$type.GetMethod('SourceStrings',$flags)
 $shared=[string[]]$sourceStrings.Invoke($null,[object[]]@($clone,'SharedSlots'))
 if($shared.Count-ne1-or$shared[0]-ne"R2/SoulOrb/global-gameobject[14]`nfixture"){throw 'Soul shared source wire directory differs'}
 if(-not$check.Invoke($null,[object[]]@($clone,$schema,'contract-callback'))){throw 'Wire changed original root/header aliases'}
 $rejected=0
 foreach($case in @('schema','callback-binding','root-layout','state-order','action-order','counter','flags','active-state')){
  $bad=$decode.Invoke($codec,$decodeArgs);$testSchema=$schema;$testBinding='contract-callback'
  $context=$bad.Nodes[($bad.Roots|Where-Object Binding -eq 'R2/SoulOrb/context').Node-1]
  $fsm=$bad.Nodes[($bad.Roots|Where-Object Binding -eq 'R2/SoulOrb/fsm').Node-1]
  switch($case){
   'schema' {$testSchema+='-old'}
   'callback-binding' {$testBinding='foreign-same-type'}
   'root-layout' {$bad.Nodes[($bad.Roots|Where-Object Binding -eq 'R2/SoulOrb/fsm/state[8]/action[10]').Node-1].Type='foreign'}
   'state-order' {$edge=$fsm.Fields|Where-Object {$_.Name.EndsWith('/states')};$array=$bad.Nodes[$edge.Value.Reference-1];$x=$array.Items[0];$array.Items[0]=$array.Items[1];$array.Items[1]=$x}
   'action-order' {$st=$bad.Nodes[($bad.Roots|Where-Object Binding -eq 'R2/SoulOrb/fsm/state[8]').Node-1];$edge=$st.Fields|Where-Object {$_.Name.EndsWith('/actions')};$array=$bad.Nodes[$edge.Value.Reference-1];$x=$array.Items[0];$array.Items[0]=$array.Items[1];$array.Items[1]=$x}
   'counter' {$at=[Array]::FindIndex($context.Fields,[Predicate[object]]{param($x)$x.Name.EndsWith('/Counter')});$v=$context.Fields[$at];$value=$v.Value;$value.Text='201';$v.Value=$value;$context.Fields[$at]=$v}
   'flags' {$at=[Array]::FindIndex($context.Fields,[Predicate[object]]{param($x)$x.Name.EndsWith('/DonotFinish')});$v=$context.Fields[$at];$value=$v.Value;$value.Text='False';$v.Value=$value;$context.Fields[$at]=$v}
   'active-state' {$at=[Array]::FindIndex($fsm.Fields,[Predicate[object]]{param($x)$x.Name.EndsWith('/activeState')});$v=$fsm.Fields[$at];$value=$v.Value;$value.Reference=1;$v.Value=$value;$fsm.Fields[$at]=$v}
  }
  try {$check.Invoke($null,[object[]]@($bad,$testSchema,$testBinding))|Out-Null;throw "Accepted $case"}
  catch {if($_.Exception.InnerException-is[IO.InvalidDataException]){$rejected++}else{throw}}
 }
 [ordered]@{originalStates=28;originalActions=121;actualOriginalRootIdentities=150;callbackActionRootIdentityPreserved=$true;headerWireRoundtrip=$true;headerCounterAliasLayoutRejections=$rejected;sourceFixtureUsesIndependentOriginalDefinition=$true;nativeLifecycleAndProducerNotExecuted=$true;gameLaunched=$false}|ConvertTo-Json|Set-Content -LiteralPath $OutputPath -Encoding utf8
 Write-Output 'Soul original metadata/150 identities/header and 8 rejection contracts passed; no native execution.'
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
