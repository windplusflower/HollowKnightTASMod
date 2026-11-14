$bytes = [System.IO.File]::ReadAllBytes("artifacts/scene-checkpoints/p3b/cached-b8-source/packet.bin")
$metaLen = [System.BitConverter]::ToInt32($bytes, 8)
$offset = 64 + $metaLen
$domainLens = @()
for ($i = 0; $i -lt 12; $i++) {
    $domainLens += [System.BitConverter]::ToInt32($bytes, 16 + $i * 4)
}
$off3 = $offset
for ($i = 0; $i -lt 3; $i++) { $off3 += $domainLens[$i] }
$graphBytes = New-Object byte[] $domainLens[3]
[System.Buffer]::BlockCopy($bytes, $off3, $graphBytes, 0, $domainLens[3])
$graphJson = [System.Text.Encoding]::UTF8.GetString($graphBytes)
$graph = ConvertFrom-Json $graphJson
Write-Output ("Graph Roots count: " + $graph.Roots.Count)
$sessionRoots = $graph.Roots | Where-Object { $_.Binding -like "*session*" }
Write-Output ("Graph Session Roots count: " + $sessionRoots.Count)
foreach ($r in $sessionRoots) {
    Write-Output ("  Graph root: " + $r.Binding)
}
$loaderRoots = $graph.Roots | Where-Object { $_.Binding -like "*loader*" }
Write-Output ("Graph Loader Roots count: " + $loaderRoots.Count)
foreach ($r in $loaderRoots) {
    Write-Output ("  Graph loader root: " + $r.Binding)
}
