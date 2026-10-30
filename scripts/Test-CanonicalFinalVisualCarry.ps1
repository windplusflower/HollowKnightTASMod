param([string]$Output)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release'
$managed='D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed'
$resolve=[ResolveEventHandler]{param($sender,$event) $name=([Reflection.AssemblyName]$event.Name).Name+'.dll';foreach($directory in @($bin,$managed)){$path=Join-Path $directory $name;if([IO.File]::Exists($path)){return [Reflection.Assembly]::LoadFrom($path)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolve)
try {
 $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'HollowKnightTAS.dll'))
 $game=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
 $static=[Reflection.BindingFlags]'Static,Public,NonPublic';$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
 $carry=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalFinalVisualCarry',$true)
 $fields=$carry.GetMethod('Fields',$static)
 if($fields.Invoke($null,[object[]]@($game.GetType('TMPro.TextMeshPro',$true))).Count-ne223-or$fields.Invoke($null,[object[]]@($game.GetType('TMPro.TMP_SubMesh',$true))).Count-ne13){throw 'Full original TMP field ABI differs'}
 $continuation=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalPersistentSpriteContinuation',$true)
 if($continuation.GetField('AnimatorCount',$static).GetRawConstantValue()-ne13-or$continuation.GetField('NativeOwnerCount',$static).GetRawConstantValue()-ne26){throw 'Shadow animator/lifecycle directory not complete'}
 $keys=$continuation.GetField('Keys',$static).GetValue($null)
 if($keys.Count-ne13-or$keys[-1]-ne'Hero/Effects/Shadow Recharge'){throw 'Shadow original continuation key absent'}
 $registry=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalCarryFieldRegistry',$true)
 $codec=$registry.GetMethod('Create',$static).Invoke($null,[object[]]@($false,$false,$false,$false,$false,$false,$false,$false,$false,$true))
 $dataType=$carry.GetNestedType('Data',[Reflection.BindingFlags]'Public,NonPublic');$data=[Activator]::CreateInstance($dataType,$true)
 $meshInfoType=$game.GetType('TMPro.TMP_MeshInfo',$true);$textInfoType=$game.GetType('TMPro.TMP_TextInfo',$true)
 $info=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($textInfoType)
 foreach($field in $textInfoType.GetFields($instance)){
  if($field.FieldType.IsArray){$field.SetValue($info,[Array]::CreateInstance($field.FieldType.GetElementType(),4))}
 }
 $meshInfo=[Activator]::CreateInstance($meshInfoType)
 foreach($field in $meshInfoType.GetFields($instance)){
  if($field.FieldType.IsArray){$field.SetValue($meshInfo,[Array]::CreateInstance($field.FieldType.GetElementType(),$(if($field.Name-eq'triangles'){6}else{4})))}
 }
 $meshInfos=[Array]::CreateInstance($meshInfoType,1);$meshInfos.SetValue($meshInfo,0)
 $textInfoType.GetField('meshInfo',$instance).SetValue($info,$meshInfos)
 $textInfoType.GetField('materialCount',$instance).SetValue($info,1)
 $dictionary=[Collections.Generic.Dictionary[int,int]]::new();$dictionary.Add(1,7);$dictionary.Add(4,9)
 $shared=[int[]]@(5,0,-1,17);$row=[object[]]@($info,$dictionary,$shared,$shared,'source text',[single]-0.5)
 $dataType.GetField('Values').SetValue($data,[object[][]]@($row,$row))
 $roots=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal);$roots.Add('offline/final-visual',$data)
 $capture=$codec.GetType().GetMethod('Capture',$instance)
 Add-Type 'public static class FinalVisualFixtureBinding { public static string Bind(object value) { return object.ReferenceEquals(value, System.Collections.Generic.EqualityComparer<int>.Default) ? "fixture/original-default-int-comparer" : null; } }'
 $bind=[Delegate]::CreateDelegate([Func[object,string]],[FinalVisualFixtureBinding].GetMethod('Bind'))
 $graph=$capture.Invoke($codec,[object[]]@($roots,$bind,$null,$null))
 if($graph.Unsupported.Length-ne0){throw ('Actual compact TMP source fixture unsupported: '+($graph.Unsupported-join';'))}
 $encode=$codec.GetType().GetMethod('Encode',$instance);$decode=$codec.GetType().GetMethod('Decode',$instance)
 $bytes=$encode.Invoke($codec,[object[]]@($graph));$decoded=$decode.Invoke($codec,[object[]]@(,$bytes));$again=$encode.Invoke($codec,[object[]]@($decoded))
 if([Convert]::ToBase64String($bytes)-ne[Convert]::ToBase64String($again)){throw 'Actual compact TMP source wire did not roundtrip canonically'}
 $policy=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalRegisteredVisualMeshCachePolicy',$true)
 $projection=$policy.GetMethod('Project',$static)
 $descriptorType=$projection.GetParameters()[0].ParameterType;$boundsType=$projection.GetParameters()[1].ParameterType
 $descriptor=[Activator]::CreateInstance($descriptorType);$descriptorType.GetProperty('indexCount').SetValue($descriptor,6,$null);$descriptorType.GetProperty('baseVertex').SetValue($descriptor,3,$null)
 $bounds=[Activator]::CreateInstance($boundsType);$projected=$projection.Invoke($null,[object[]]@($descriptor,$bounds))
 if($descriptorType.GetProperty('indexCount').GetValue($projected,$null)-ne6-or$descriptorType.GetProperty('baseVertex').GetValue($projected,$null)-ne3){throw 'Bounds policy changed non-cache descriptor fields'}
 $glyphType=$game.GetType('TMPro.TMP_Glyph',$true)
 $glyph=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($glyphType)
 $glyphSlotsType=[Collections.Generic.SortedDictionary``2].MakeGenericType([type[]]@([string],$glyphType))
 $glyphSlots=[Activator]::CreateInstance($glyphSlotsType);$glyphSlotsType.GetMethod('Add').Invoke($glyphSlots,[object[]]@('resources.assets:1/list/0',$glyph))|Out-Null
 $glyphSlotsType.GetMethod('Add').Invoke($glyphSlots,[object[]]@('resources.assets:1/character/32',$glyph))|Out-Null
 $catalogType=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalFinalVisualGlyphs',$true)
 $ctor=$catalogType.GetConstructors($instance)|Where-Object {$_.GetParameters().Count-eq1}
 $catalog=$ctor.Invoke([object[]]@($glyphSlots));$binding=$catalogType.GetMethod('Bind',$instance).Invoke($catalog,[object[]]@($glyph))
 if(-not[object]::ReferenceEquals($glyph,$catalogType.GetMethod('Resolve',$instance).Invoke($catalog,[object[]]@($binding)))){throw 'Original shared font glyph identity was replaced'}
 $clone=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($glyphType)
 $glyphSlotsType.GetProperty('Item').SetValue($glyphSlots,$clone,[object[]]@('resources.assets:1/character/32'))
 $broken=$ctor.Invoke([object[]]@($glyphSlots));$rejected=$false
 try{$catalogType.GetMethod('Resolve',$instance).Invoke($broken,[object[]]@($binding))|Out-Null}catch{$rejected=$_.Exception.InnerException.Message.Contains('aliases differ')}
 if(-not$rejected){throw 'Cloned font glyph alias was accepted'}
 $heroCallbacks=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHeroAnimationCallbacks',$true)
 if($heroCallbacks.GetMethod('ControllerFields',$static).Invoke($null,@()).Length-ne17){throw 'Hero animation controller full original field ABI differs'}
 $controllerMethod=$heroCallbacks.GetMethod('ControllerMethod',$static).Invoke($null,@())
 $controllerType=$game.GetType('HeroAnimationController',$true)
 $controller=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($controllerType)
 $controllerDelegateType=[Action``2].MakeGenericType([type[]]@($game.GetType('tk2dSpriteAnimator',$true),$game.GetType('tk2dSpriteAnimationClip',$true)))
 $controllerCallback=[Delegate]::CreateDelegate($controllerDelegateType,$controller,$controllerMethod)
 if($controllerMethod.MetadataToken-ne0x0600028e-or-not[object]::ReferenceEquals($controllerCallback.Target,$controller)){throw 'Original Hero controller callback binding differs'}
 $heroDataType=$heroCallbacks.GetNestedType('Data',[Reflection.BindingFlags]'Public,NonPublic')
 $heroData=[Activator]::CreateInstance($heroDataType,$true);$heroRef=[object]::new()
 $heroDataType.GetField('ControllerValues').SetValue($heroData,[object[]]@($heroRef,$heroRef))
 $bindHero=$heroCallbacks.GetMethod('BindReadbackReference',$static)
 if($bindHero.Invoke($null,[object[]]@($heroData,$heroRef))-ne'Hero-animation/source-reference[0]'-or$null-ne$bindHero.Invoke($null,[object[]]@($heroData,[object]::new()))){throw 'Hero readback binding hid a changed reference alias'}
 $callbackMethods=$assembly.GetType('HollowKnightTAS.Runtime.FullRun.CanonicalHeroAnimationCallbacks',$true).GetMethod('Method',$static)
 $callbackCount=0
 foreach($actionName in @('Tk2dPlayAnimationWithEvents','Tk2dWatchAnimationEvents')){
  $actionType=$game.GetType(('HutongGames.PlayMaker.Actions.'+$actionName),$true)
  $action=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($actionType)
  foreach($complete in @($true,$false)){
   $method=$callbackMethods.Invoke($null,[object[]]@($actionType,$complete))
   $arguments=[type[]]@($game.GetType('tk2dSpriteAnimator',$true),$game.GetType('tk2dSpriteAnimationClip',$true))
   $delegateType=if($complete){[Action``2].MakeGenericType($arguments)}else{[Action``3].MakeGenericType([type[]]@($arguments[0],$arguments[1],[int]))}
   $callback=[Delegate]::CreateDelegate($delegateType,$action,$method)
   if(-not[object]::ReferenceEquals($callback.Target,$action)-or$callback.Method-ne$method){throw 'Original callback binding changed target or method'}
   $callbackCount++
  }
 }
 $result=[ordered]@{completeTmpFields=223;completeSubmeshFields=13;animators=13;nativeContinuations=26;heroControllerFields=17;originalHeroControllerCallback=$true;changedHeroReferenceAliasRejected=$true;actualTmpGraphNodes=$graph.Nodes.Length;actualTmpFixtureBytes=$bytes.Length;noUnsupported=$true;canonicalWireRoundtrip=$true;nonBoundsSubmeshFieldsPreserved=$true;originalFontGlyphRebound=$true;clonedGlyphAliasRejected=$true;pinnedOriginalClosedCallbacks=$callbackCount;newStandaloneWireSegments=0;nativeAcceptanceNotClaimed=$true}
 if($Output){$result|ConvertTo-Json|Set-Content -LiteralPath $Output -Encoding utf8}
 $result|ConvertTo-Json -Compress
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve)}
