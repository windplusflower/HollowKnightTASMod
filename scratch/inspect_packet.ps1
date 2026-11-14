$bytes = [System.IO.File]::ReadAllBytes("artifacts/scene-checkpoints/p3b/cached-b8-source/packet.bin")
$metaLen = [System.BitConverter]::ToInt32($bytes, 8)
$offset = 64 + $metaLen
$domainLens = @()
for ($i = 0; $i -lt 12; $i++) {
    $domainLens += [System.BitConverter]::ToInt32($bytes, 16 + $i * 4)
}
Write-Output ("Metadata length: " + $metaLen)
for ($i = 0; $i -lt 12; $i++) {
    Write-Output ("Domain " + $i + " length: " + $domainLens[$i])
}
$off6 = $offset
for ($i = 0; $i -lt 6; $i++) { $off6 += $domainLens[$i] }
$procBytes = New-Object byte[] $domainLens[6]
[System.Buffer]::BlockCopy($bytes, $off6, $procBytes, 0, $domainLens[6])
$json = [System.Text.Encoding]::UTF8.GetString($procBytes)
$proc = ConvertFrom-Json $json
Write-Output ("Session Key: " + $proc.Session.Key)
Write-Output ("Session Fields count: " + $proc.Session.Fields.Count)
$dFields = $proc.Session.Fields | Where-Object { $_.Storage -eq "D" }
Write-Output ("Session D Fields count: " + $dFields.Count)
foreach ($f in $dFields) {
    Write-Output ("  D-field: Name=" + $f.Name + " Token=" + ('{0:x8}' -f $f.Token) + " Root=" + $f.Root)
}
Write-Output ("Loader Key: " + $proc.Loader.Owner.Key)
$loaderD = $proc.Loader.Owner.Fields | Where-Object { $_.Storage -eq "D" }
foreach ($f in $loaderD) {
    Write-Output ("  Loader D-field: Name=" + $f.Name + " Token=" + ('{0:x8}' -f $f.Token) + " Root=" + $f.Root)
}
