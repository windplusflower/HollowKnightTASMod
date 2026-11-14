$managed = "D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed"
$asm = [System.Reflection.Assembly]::LoadFrom("$managed\Mods\HollowKnightTAS\HollowKnightTAS.dll")
$policyType = $asm.GetType("HollowKnightTAS.Runtime.FullRun.ResidentGraphPolicy")
$policyCtor = $policyType.GetConstructors([System.Reflection.BindingFlags]"Instance,NonPublic,Public") | Select-Object -First 1

$fn1 = [System.Func[System.Object, System.String]]{ param($o) return $null }
$policy = $policyCtor.Invoke(@($fn1, $null, $null))

# Test string 32584 directly
$metaJson = '{"Kind":"assembly","Assembly":"Newtonsoft.Json","Module":"38552c99-5240-46d5-bbe9-2b16d16688ac","Type":null,"Token":0,"MemberKind":0,"Arguments":[]}'
$b64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($metaJson))
$binding = "resident-metadata/1/" + $b64

# Step 1: Call ResolveModule directly
$catType = $asm.GetType("HollowKnightTAS.Runtime.FullRun.ResidentManagedTypeCatalog")
$resMod = $catType.GetMethod("ResolveModule", [System.Reflection.BindingFlags]"Static,NonPublic")
$module = $resMod.Invoke($null, @("Newtonsoft.Json", "38552c99-5240-46d5-bbe9-2b16d16688ac"))
Write-Output ("Resolved module: " + $module.FullyQualifiedName)
$assembly = $module.Assembly
Write-Output ("Assembly: " + $assembly.FullName)

# Step 2: Call BindMetadata on $assembly
$bindMeta = $policyType.GetMethod("BindMetadata", [System.Reflection.BindingFlags]"Instance,NonPublic")
$bound = $bindMeta.Invoke($policy, @($assembly))
Write-Output ("Bound string: " + $bound)

$expBytes = [System.Convert]::FromBase64String($b64)
$actB64 = $bound.Substring("resident-metadata/1/".Length)
$actBytes = [System.Convert]::FromBase64String($actB64)

Write-Output ("EXP: " + [System.Text.Encoding]::UTF8.GetString($expBytes))
Write-Output ("ACT: " + [System.Text.Encoding]::UTF8.GetString($actBytes))
