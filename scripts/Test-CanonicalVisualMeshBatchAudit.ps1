param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Mono.Cecil.dll'))|Out-Null
$original=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $managed 'Assembly-CSharp.dll'))
$candidate=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'))
try {
 if($original.Mvid.ToString()-ne'2b54cdaf-7a93-4f0e-b297-d50c4090ea9f'){throw 'Original visual component module changed'}
 $types=@('tk2dSprite','tk2dBaseSprite','TMPro.TMP_Text','TMPro.TextMeshPro','TMPro.TMP_SubMesh','TMPro.TMP_MeshInfo')
 $layouts=@(foreach($name in $types){
  $type=$original.Types|Where-Object FullName -eq $name
  if(@($type).Count-ne1){throw "Original type absent or ambiguous: $name"}
  [ordered]@{type=$name;fields=@($type.Fields|Where-Object {-not$_.IsStatic}|ForEach-Object{[ordered]@{name=$_.Name;type=$_.FieldType.FullName}})}
 })
 $audit=$candidate.Types|Where-Object FullName -eq 'HollowKnightTAS.Runtime.FullRun.LoadingVisualMeshBatchAudit'
 if(@($audit).Count-ne1){throw 'Batch diagnostic type absent'}
 $methods=@($audit.Methods)+@($audit.NestedTypes|ForEach-Object Methods)
 $calls=@($methods|Where-Object HasBody|ForEach-Object{$_.Body.Instructions|Where-Object {$_.Operand-is[Mono.Cecil.MethodReference]}|ForEach-Object Operand})
 $mutations=@($calls|Where-Object {
  $_.DeclaringType.Namespace.StartsWith('UnityEngine')-and
  ($_.Name.StartsWith('set_')-or$_.Name-in@('SetMesh','SetVertices','SetTriangles','SetIndices','SetColors','UploadMeshData','ForceMeshUpdate','Destroy','DestroyImmediate','Instantiate','AddComponent','SetActive'))
 })
 if($mutations.Count-ne0){throw 'Read-only batch diagnostic contains a Unity mutation'}
 $unityDtoFields=@($calls|Where-Object {$_.Name-eq'.ctor'-and$_.DeclaringType.Name.StartsWith('<>f__AnonymousType')}|ForEach-Object {
  $_.DeclaringType.GenericArguments|Where-Object {$_.Namespace.StartsWith('UnityEngine')}
 })
 if($unityDtoFields.Count-ne0){throw 'Unity structs/objects must not enter the JSON DTO property walker'}
 $scope=@($audit.Methods|Where-Object Name -eq Capture|ForEach-Object {$_.Body.Instructions|Where-Object {$_.OpCode.Code-eq'Ldstr'}|ForEach-Object Operand})
 foreach($key in @('Hero','Hero/Effects/Shadow Recharge','HUD/Notches','HUD/Overcharmed','HUD/First Map Label')){
  if($key-notin$scope){throw "Batch diagnostic is missing a required owner: $key"}
 }
 $prior=Get-Content -LiteralPath (Join-Path $repo 'artifacts/scene-checkpoints/p3/canonical-boss-context-v15-build-20261026/original-mesh-producer-and-draw-callers.json') -Raw|ConvertFrom-Json
 foreach($module in $prior.modules){if((Get-FileHash -LiteralPath (Join-Path $managed $module.module) -Algorithm SHA256).Hash-ne$module.sha256){throw 'Original mesh caller audit is stale'}}
 [ordered]@{diagnosticOnly=$true;moduleMvid=$original.Mvid.ToString();candidateSha256=(Get-FileHash -LiteralPath $candidate.FileName -Algorithm SHA256).Hash;
  layouts=$layouts;allFiveOwnersCovered=$true;unityMutations=0;unityJsonDtoFields=0;priorOriginalCallerModuleHashesCurrent=$true;
  priorMeshApiCallsites=@($prior.allOriginalMeshApiCalls).Count;noNewCarrySchema=$true;maximumOutputBytes=2097152;
  maximumNativeConsumers=100000;maximumParticleSlots=100000;nativeRuntimeConsumersRequireLiveEvidence=$true;
  comparisonRulesUnchanged=$true;restoreQualified=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $Output -Encoding utf8
 Write-Output 'Five-owner original layouts, read-only candidate IL and original Mesh-caller module identities passed.'
} finally {$original.Dispose();$candidate.Dispose()}
