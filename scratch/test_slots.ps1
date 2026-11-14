$managed = "D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed"
[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $args)
    $name = $args.Name.Split(',')[0]
    $path = Join-Path $managed "$name.dll"
    if (Test-Path $path) {
        return [System.Reflection.Assembly]::LoadFrom($path)
    }
    return $null
})

$asm = [System.Reflection.Assembly]::LoadFrom("$managed\Mods\HollowKnightTAS\HollowKnightTAS.dll")
$bytes = [System.IO.File]::ReadAllBytes("artifacts/scene-checkpoints/p3b/cached-b8-source/packet.bin")
$metaLen = [System.BitConverter]::ToInt32($bytes, 8)
$offset = 64 + $metaLen
$domainLens = @()
for ($i = 0; $i -lt 12; $i++) { $domainLens += [System.BitConverter]::ToInt32($bytes, 16 + $i * 4) }
$off6 = $offset
for ($i = 0; $i -lt 6; $i++) { $off6 += $domainLens[$i] }
$procBytes = New-Object byte[] $domainLens[6]
[System.Buffer]::BlockCopy($bytes, $off6, $procBytes, 0, $domainLens[6])

$procType = $asm.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBoundaryProcessState+Data")
$jsonNet = [System.Reflection.Assembly]::LoadFrom("$managed\Newtonsoft.Json.dll")
$jsonConvert = $jsonNet.GetType("Newtonsoft.Json.JsonConvert")
$procJson = [System.Text.Encoding]::UTF8.GetString($procBytes)
$deserializeMethod = $jsonConvert.GetMethods() | Where-Object { $_.Name -eq "DeserializeObject" -and $_.IsGenericMethod -and $_.GetParameters().Length -eq 1 } | Select-Object -First 1
$genericDeserialize = $deserializeMethod.MakeGenericMethod($procType)
$sourceProcData = $genericDeserialize.Invoke($null, @($procJson))
$sourceSessionData = $sourceProcData.Session

Write-Output ("Source session key: " + $sourceSessionData.Key)
Write-Output ("Source session fields: " + $sourceSessionData.Fields.Length)

$slotsType = $asm.GetType("HollowKnightTAS.Runtime.FullRun.ResidentProcessSlots")
$canonicalLayoutMethod = $slotsType.GetMethod("CanonicalLayoutJson", [System.Reflection.BindingFlags]"Static,NonPublic")
$sourceLayoutJson = $canonicalLayoutMethod.Invoke($null, @($sourceSessionData))
Write-Output ("Source Layout Json length: " + $sourceLayoutJson.Length)
