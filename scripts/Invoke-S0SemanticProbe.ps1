param(
    [ValidateSet('All', 'HistoryA', 'HistoryB', 'HistoryC')]
    [string] $Which = 'All',
    [string] $OutputDirectory = '',
    [int] $IdleShiftFrames = 120
)

$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clockBridge = Join-Path $repo 'scratch/resident-clock-offline-074c843437b94c879d0f47756cdf6bf3/HollowKnightTAS.ClockBridge.dll'
$sourceMovie = Join-Path $repo 'artifacts/scene-checkpoints/p3b/resident-recording-20261103-02/run-01/serial-scenes-canonical-boundary-input-route-recording-unqualified.hktaspack'
$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$publicKey = Join-Path $repo '.local/signing/companion-public.json'
$installDir = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
$loadingProbeScript = Join-Path $PSScriptRoot 'Invoke-LoadingProbe.ps1'

foreach ($req in @($clockBridge, $sourceMovie, $bundleTool, $publicKey, $loadingProbeScript)) {
    if (-not (Test-Path -LiteralPath $req -PathType Leaf)) {
        throw "Required file missing: $req"
    }
}

$coreRuntime = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.Core.dll'
$coreHarness = Join-Path $repo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64/HollowKnightTAS.Core.dll'
if ((Get-FileHash -LiteralPath $coreRuntime -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $coreHarness -Algorithm SHA256).Hash) {
    throw 'HollowKnightTAS.Core.dll mismatch between Runtime and StudioScenarioHarness. Rebuild both with -p:SkipHKTASInstall=true.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repo "artifacts/scene-checkpoints/p3b/lean-s0-probe-$stamp"
}
$outRoot = [IO.Path]::GetFullPath($OutputDirectory)
$p3bOwned = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/scene-checkpoints/p3b')) + [IO.Path]::DirectorySeparatorChar
if (-not $outRoot.StartsWith($p3bOwned, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must reside under artifacts/scene-checkpoints/p3b/."
}
$fixturesDir = Join-Path $outRoot 'fixtures'
New-Item -ItemType Directory -Force -Path $fixturesDir | Out-Null

function Assert-PostRunHygiene([string] $runOutDir) {
    $lingering = Get-Process hollow_knight, StudioScenarioHarness -ErrorAction SilentlyContinue
    if ($lingering) {
        throw "Post-run hygiene failed: lingering processes: $($lingering.ProcessName -join ', ')"
    }
    $beforePath = Join-Path $runOutDir 'saves-before.json'
    $afterPath = Join-Path $runOutDir 'saves-after.json'
    if (-not (Test-Path -LiteralPath $beforePath) -or -not (Test-Path -LiteralPath $afterPath)) {
        throw "Missing save audit file in $runOutDir"
    }
    $before = Get-Content -LiteralPath $beforePath -Raw
    $after = Get-Content -LiteralPath $afterPath -Raw
    if ($before -ne $after) {
        throw "Save audit mismatch in $runOutDir"
    }
    $savesCount = @($before | ConvertFrom-Json).Count
    $modsIsoPath = Join-Path $runOutDir 'mods-isolation.json'
    if (-not (Test-Path -LiteralPath $modsIsoPath)) {
        throw "Missing mods-isolation.json in $runOutDir"
    }
    $modsIso = Get-Content -LiteralPath $modsIsoPath -Raw | ConvertFrom-Json
    if ($modsIso.status -ne 'restored') {
        throw "Mods isolation did not restore cleanly: $($modsIso.status)"
    }
    & dotnet $bundleTool verify $publicKey $installDir (Join-Path $installDir 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) {
        throw 'Companion signature verification failed.'
    }
    Write-Output "Hygiene OK ($runOutDir): saves=$savesCount matched, mods=$($modsIso.status), processes=clean, signature=Valid"
}

function New-IdleShiftedPack([string] $destPack, [int] $deltaFrames) {
    & python -c '
import sys, zipfile, json, hashlib
src_pack, dst_pack, delta_s = sys.argv[1], sys.argv[2], int(sys.argv[3])
with zipfile.ZipFile(src_pack, "r") as zin:
    manifest = json.loads(zin.read("manifest.json").decode("utf-8"))
    lines = zin.read("movie.hktas").decode("utf-8").splitlines()
    first_run = json.loads(lines[1])
    first_run["repeatCount"] = int(first_run["repeatCount"]) + delta_s
    lines[1] = json.dumps(first_run, separators=(",", ":"))
    movie_bytes = ("\n".join(lines) + "\n").encode("utf-8")
    manifest["MovieSha256"] = hashlib.sha256(movie_bytes).hexdigest()
    with zipfile.ZipFile(dst_pack, "w", compression=zipfile.ZIP_DEFLATED) as zout:
        for info in zin.infolist():
            if info.filename == "movie.hktas":
                zout.writestr("movie.hktas", movie_bytes)
            elif info.filename == "manifest.json":
                zout.writestr("manifest.json", json.dumps(manifest, separators=(",", ":")).encode("utf-8"))
            else:
                zout.writestr(info.filename, zin.read(info.filename))
' $sourceMovie $destPack $deltaFrames
    if ($LASTEXITCODE -ne 0) { throw "Failed to create idle-shifted pack $destPack" }
}

function New-MinionsPack([string] $destPack) {
    $user4Dat = Join-Path $fixturesDir 'user4.dat'
    & python -c '
import sys, zipfile
src_pack, dst_dat = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(src_pack, "r") as zin:
    with open(dst_dat, "wb") as fout:
        fout.write(zin.read("initial-saves/user4.dat"))
' $sourceMovie $user4Dat
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract initial-saves/user4.dat' }

    $psScript = Join-Path $fixturesDir 'patch-user4.ps1'
    @'
param([string]$DatPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions

$bf = New-Object System.Runtime.Serialization.Formatters.Binary.BinaryFormatter
$fsIn = [IO.File]::OpenRead($DatPath)
try { $encStr = [string]$bf.Deserialize($fsIn) } finally { $fsIn.Dispose() }

$aes = New-Object System.Security.Cryptography.RijndaelManaged
$aes.Mode = [System.Security.Cryptography.CipherMode]::ECB
$aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
$aes.Key = [Text.Encoding]::UTF8.GetBytes('UKu52ePUBwetZ9wNX88o54dnfKRu0T1l')
$rawBytes = [Convert]::FromBase64String($encStr)
$decBytes = $aes.CreateDecryptor().TransformFinalBlock($rawBytes, 0, $rawBytes.Length)
$jsonText = [Text.Encoding]::UTF8.GetString($decBytes)

$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 67108864
$saveObj = $ser.DeserializeObject($jsonText)
$pd = $saveObj['playerData']
$pd['equippedCharms'] = @(22, 38, 39, 40)
foreach ($cid in @(22, 38, 39, 40)) {
    $pd["gotCharm_$cid"] = $true
    $pd["equippedCharm_$cid"] = $true
}
$pd['charmSlots'] = 11
$pd['charmSlotsFilled'] = 9
$pd['grimmChildLevel'] = 4
$pd['MPCharge'] = 99
$newJson = $ser.Serialize($saveObj)

$newBytes = [Text.Encoding]::UTF8.GetBytes($newJson)
$newEnc = [Convert]::ToBase64String($aes.CreateEncryptor().TransformFinalBlock($newBytes, 0, $newBytes.Length))
$fsOut = [IO.File]::Create($DatPath)
try { $bf.Serialize($fsOut, $newEnc) } finally { $fsOut.Dispose() }
'@ | Set-Content -LiteralPath $psScript -Encoding utf8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $psScript -DatPath $user4Dat
    if ($LASTEXITCODE -ne 0) { throw 'Failed to patch user4.dat' }

    & python -c '
import sys, zipfile, json, hashlib
src_pack, dst_dat, dst_pack = sys.argv[1], sys.argv[2], sys.argv[3]
dat_bytes = open(dst_dat, "rb").read()
dat_hash = hashlib.sha256(dat_bytes).hexdigest()
with zipfile.ZipFile(src_pack, "r") as zin:
    manifest = json.loads(zin.read("manifest.json").decode("utf-8"))
    hashes = {}
    for entry in manifest["Files"]:
        if entry["Name"] == "user4.dat":
            entry["Length"] = len(dat_bytes)
            entry["Sha256"] = dat_hash
        hashes[entry["Name"].lower()] = entry["Sha256"]
    sorted_hashes = {k: hashes[k] for k in sorted(hashes.keys())}
    id_bytes = json.dumps(sorted_hashes, separators=(",", ":")).encode("utf-8")
    manifest["InitialSavesId"] = hashlib.sha256(id_bytes).hexdigest()
    manifest_bytes = json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    with zipfile.ZipFile(dst_pack, "w", compression=zipfile.ZIP_DEFLATED) as zout:
        for info in zin.infolist():
            if info.filename == "initial-saves/user4.dat":
                zout.writestr("initial-saves/user4.dat", dat_bytes)
            elif info.filename == "manifest.json":
                zout.writestr("manifest.json", manifest_bytes)
            else:
                zout.writestr(info.filename, zin.read(info.filename))
' $sourceMovie $user4Dat $destPack
    if ($LASTEXITCODE -ne 0) { throw "Failed to create minions pack $destPack" }
}

function Invoke-SingleProbe([string] $name, [string] $moviePath, [int] $endFrame) {
    $runOut = Join-Path $outRoot $name
    if (Test-Path -LiteralPath $runOut) {
        throw "Run directory already exists: $runOut"
    }
    Write-Output "=== Running S0 Semantic Probe: $name (EndFrame=$endFrame) ==="
    $priorProbe = $env:HKTAS_S0_SEMANTIC_PROBE
    try {
        $env:HKTAS_S0_SEMANTIC_PROBE = '1'
        & $loadingProbeScript `
            -OutputDirectory $runOut `
            -Movie $moviePath `
            -FirstFrame 0 `
            -EndFrame $endFrame `
            -Runs 1 `
            -DisableLegacyCalibration `
            -SynchronousFetch `
            -CanonicalStartupClock `
            -ModWallClock `
            -SerialScenes `
            -GameplayOracle `
            -SerialBoundaryOnly `
            -NativeScheduler `
            -NativePhysics `
            -VirtualAudio `
            -IsolatedOnline `
            -CanonicalParticles `
            -CanonicalBoundary `
            -CanonicalPhaseOneTrace `
            -ClockBridgePath $clockBridge `
            -EvidencePhase p3b
    }
    finally {
        $env:HKTAS_S0_SEMANTIC_PROBE = $priorProbe
    }
    Assert-PostRunHygiene $runOut
}

if ($Which -in @('All', 'HistoryA')) {
    Invoke-SingleProbe 'history-a' $sourceMovie 5800
}
if ($Which -in @('All', 'HistoryB')) {
    $packB = Join-Path $fixturesDir 'history-b-idle.hktaspack'
    New-IdleShiftedPack $packB $IdleShiftFrames
    Invoke-SingleProbe 'history-b' $packB (4100 + $IdleShiftFrames)
}
if ($Which -in @('All', 'HistoryC')) {
    $packC = Join-Path $fixturesDir 'history-c-minions.hktaspack'
    New-MinionsPack $packC
    Invoke-SingleProbe 'history-c' $packC 2500
}

Write-Output "All requested S0 semantic probes completed cleanly under: $outRoot"
