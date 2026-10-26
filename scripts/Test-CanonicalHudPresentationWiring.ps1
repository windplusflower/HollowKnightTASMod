param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Mono.Cecil.dll'))|Out-Null
$runtime=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'))
$game=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $managed 'Assembly-CSharp.dll'))
try {
 function FindType($module,$name){$module.Types|Where-Object Name -eq $name}
 function FindMethod($type,$name){@($type.Methods|Where-Object Name -eq $name)}
 function Calls($method,$owner,$name){@($method.Body.Instructions|Where-Object{$_.Operand-is[Mono.Cecil.MethodReference]-and$_.Operand.DeclaringType.Name-eq$owner-and$_.Operand.Name-eq$name})}
 $policy=FindType $runtime CanonicalBoundaryPolicy
 $ctor=(FindMethod $policy '.ctor')[0];$apply=(FindMethod $policy Apply)[0]
 if((Calls $ctor NativeExecutionProfile UsesCanonicalBossChallengeContext).Count-ne1-or(Calls $ctor CanonicalBossChallengeAssets '.ctor').Count-ne1){throw 'Common session asset startup/profile wiring differs'}
 if((Calls $apply CanonicalBossChallengePolicy '.ctor').Count-ne1-or(Calls $apply CanonicalBossChallengePolicy Apply).Count-ne1){throw 'Common boundary context Canon is absent'}
 $assets=FindType $runtime CanonicalBossChallengeAssets
 $assetCtor=(FindMethod $assets '.ctor')[0]
 if((Calls $assetCtor Resources Load).Count-ne1-or(Calls $assetCtor BossSequence GetBossScene).Count-ne0){throw 'Asset resolver does not use the sole exact original resource factory'}
 foreach($method in $assets.Methods|Where-Object HasBody){
  if((Calls $method Resources FindObjectsOfTypeAll).Count-ne0-or(Calls $method Random set_state).Count-ne0){throw 'Asset preparation searches resources or rewrites RNG'}
  if($method.Name-ne'.ctor'-and(Calls $method File OpenRead).Count-ne0){throw 'Large asset files are rehashed beyond session startup'}
 }
 $walker=(FindMethod (FindType $runtime CanonicalGeoColdTemplate) ObserveIncoming)[0]
 if((Calls $walker Resources FindObjectsOfTypeAll).Count-ne1-or(Calls $walker CanonicalBossChallengePolicy ReadFiniteRoots).Count-ne1-or(Calls $walker CanonicalBossChallengePolicy CompleteWalk).Count-ne1){throw 'Incoming proof has an extra/missing world inventory or finite-root completion'}
 $carry=FindType $runtime CanonicalCarryCapture
 if((Calls (FindMethod $carry Capture)[0] CanonicalBossChallengeCarry Capture).Count-ne1){throw 'Boss context D root capture missing'}
 $nested=@($carry.NestedTypes|ForEach-Object {$_.Methods}|Where-Object HasBody)
 $prepareCalls=@($nested|ForEach-Object {Calls $_ CanonicalBossChallengeCarry PrepareApply})
 if($prepareCalls.Count-ne2){throw 'Source audit and target prepared application are not both wired'}
 $awake=(FindMethod (FindType $game BossSceneController) Awake)[0]
 $instructions=@($awake.Body.Instructions)
 $clear=@($instructions|Where-Object{$_.OpCode.Name-eq'stsfld'-and$_.Operand.Name-eq'SetupEvent'})
 $invoke=@($instructions|Where-Object{$_.Operand-is[Mono.Cecil.MethodReference]-and$_.Operand.DeclaringType.Name-eq'SetupEventDelegate'-and$_.Operand.Name-eq'Invoke'})
 if($clear.Count-ne1-or$invoke.Count-ne1-or$clear[0].Offset-ge$invoke[0].Offset){throw 'Original Awake clear-before-invoke contract differs'}
 $hudBoundary=FindType $runtime CanonicalHudPresentationBoundaryPolicy
 if((Calls $apply CanonicalHudPresentationBoundaryPolicy Reset).Count-ne1){throw 'Common HUD presentation boundary is absent'}
 $capture=(FindMethod $carry Capture)[0]
 if((Calls $capture CanonicalHudPresentationSourceDomain Capture).Count-ne1){throw 'HUD source D capture is absent'}
 $hudCalls=@($nested|ForEach-Object {Calls $_ CanonicalHudPresentationSourceDomain Prepare})
 if($hudCalls.Count-ne1){throw 'HUD target D prepare is absent'}
 $oracle=FindType $runtime LoadingGameplayOracle
 $oracleCalls=@($oracle.Methods|Where-Object HasBody|ForEach-Object{Calls $_ LoadingGeoIncomingReferences BeginHudPresentationSample})
 if($oracleCalls.Count-ne2){throw 'HUD current/frozen incoming domains are not both observed'}
 $reset=(FindMethod $hudBoundary Reset)[0]
 if((Calls $reset Object DestroyImmediate).Count-ne1-or(Calls $reset CanonicalGeoColdTemplate ObserveIncoming).Count-ne1){throw 'HUD original destruction or full incoming proof absent'}
 $factoryCapture=(FindMethod (FindType $runtime CanonicalHudPresentationColdTemplate) Capture)[0]
 if((Calls $factoryCapture CanonicalCarryFieldRegistry Create).Count-ne1-or(Calls $factoryCapture CanonicalCarryGraphCodec '.ctor').Count-ne0){throw 'HUD raw slots require full original value-type registry'}
 $hudCarry=FindType $runtime CanonicalHudPresentationCarry
 if((Calls (FindMethod $hudCarry Capture)[0] BoundaryMonoLifecycle ObserveUnstartedUi).Count-gt1){throw 'Unexpected HUD lifecycle observation route'}
 [ordered]@{commonStartupAssetLoadOnce=$true;commonBoundaryCanon=$true;oneOriginalIncomingInventory=$true;threeFiniteRetiredRoots=$true;sourceAuditAndTargetDApply=$true;originalAwakeClearBeforeInvoke=$true;noAssetSearchOrRngRewrite=$true;fileHashesOnlyAtStartup=$true;hudCommonBoundaryAndSourceTargetDomain=$true;hudCurrentAndFrozenIncoming=$true;nativeMonoExecutionNotTested=$true}|ConvertTo-Json|Set-Content -LiteralPath $OutputPath
 Get-Content -LiteralPath $OutputPath
} finally {$runtime.Dispose();$game.Dispose()}
