$managed = "D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed"
$asm = [System.Reflection.Assembly]::LoadFrom("$managed\Mods\HollowKnightTAS\HollowKnightTAS.dll")
$policyType = $asm.GetType("HollowKnightTAS.Runtime.FullRun.ResidentGraphPolicy")
$policyCtor = $policyType.GetConstructors([System.Reflection.BindingFlags]"Instance,NonPublic,Public") | Select-Object -First 1

$fn1 = [System.Func[System.Object, System.String]]{ param($o) return $null }
$policy = $policyCtor.Invoke(@($fn1, $null, $null))
$tryResolve = $policyType.GetMethod("TryResolveMetadata", [System.Reflection.BindingFlags]"Instance,NonPublic")

$pythonCode = @'
import struct, json, base64

packet_path = "artifacts/scene-checkpoints/p3b/cached-b8-source/packet.bin"
with open(packet_path, "rb") as f:
    header = f.read(64)
    magic, version, meta_len, domain_count = struct.unpack("<IIII", header[:16])
    domain_lens = struct.unpack("<12I", header[16:64])
    f.read(meta_len)
    domains = [f.read(dlen) for dlen in domain_lens]

graph_bytes = domains[3]
magic, ver, string_count = struct.unpack("<III", graph_bytes[:12])
offset = 12
meta_prefix = "resident-metadata/1/"
for i in range(string_count):
    slen, = struct.unpack("<I", graph_bytes[offset:offset+4])
    offset += 4
    sbytes = graph_bytes[offset:offset+slen]
    offset += slen
    try:
        stext = sbytes.decode("utf-8")
        if stext.startswith(meta_prefix):
            print(stext)
    except:
        pass
'@
$metaStrings = python -c $pythonCode

Write-Output ("Total metadata strings: " + $metaStrings.Length)
foreach ($s in $metaStrings) {
    try {
        $argsArray = [object[]]@($s.ToString(), $null)
        $res = $tryResolve.Invoke($policy, $argsArray)
    } catch [System.Reflection.TargetInvocationException] {
        $inner = $_.Exception.InnerException
        if ($inner.Message -like "*noncanonical*") {
            Write-Output ("NONCANONICAL STRING: " + $s)
            $b64 = $s.ToString().Substring("resident-metadata/1/".Length)
            $bytes = [System.Convert]::FromBase64String($b64)
            Write-Output ("EXP JSON: " + [System.Text.Encoding]::UTF8.GetString($bytes))
            # Call BindMetadata to see act
            $bindMeta = $policyType.GetMethod("BindMetadata", [System.Reflection.BindingFlags]"Instance,NonPublic")
            $resolvedVal = $argsArray[1]
            Write-Output ("Resolved val is null? " + ($resolvedVal -eq $null))
            break
        }
    }
}
