[CmdletBinding()]
param(
    [string]$AssemblyPath =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed\Assembly-CSharp.dll',

    [string]$ResourcesPath =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\resources.assets',

    [string]$GlobalManagersAssetsPath =
        'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\globalgamemanagers.assets',

    [string]$IlspyCmdPath =
        'C:\Users\33361\.dotnet\tools\ilspycmd.exe',

    [string]$PythonPath = 'python',

    [Parameter(Mandatory)]
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'T24 Hero Z vanilla audit requires PowerShell 7.'
}

$AssemblyPath = [IO.Path]::GetFullPath($AssemblyPath)
$ResourcesPath = [IO.Path]::GetFullPath($ResourcesPath)
$GlobalManagersAssetsPath = [IO.Path]::GetFullPath(
    $GlobalManagersAssetsPath)
$IlspyCmdPath = [IO.Path]::GetFullPath($IlspyCmdPath)
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
$assetInspectorPath = [IO.Path]::GetFullPath((Join-Path `
    $PSScriptRoot `
    'Inspect-T24HeroSetZAsset.py'))

$expectedHashes = [ordered]@{
    assemblyCSharp =
        '5944411bd93830369390a4b51766ee68c4ab26195b299e25a07b5e7d0e00086d'
    resourcesAssets =
        'f2a9851f0424d94c0231b2689aeb51327fc7054f284bd77db2e3293d80b9386c'
    globalManagersAssets =
        '094a137c257826aca23e4ba78b12d639c9f6b5f32f92b43fdb99a0348306bc01'
}
foreach ($requiredPath in @(
        $AssemblyPath,
        $ResourcesPath,
        $GlobalManagersAssetsPath,
        $IlspyCmdPath,
        $assetInspectorPath
    )) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required T24 Hero Z audit input is missing: $requiredPath"
    }
}

$evidenceDirectory = Split-Path -Parent $EvidencePath
New-Item -ItemType Directory -Path $evidenceDirectory -Force |
    Out-Null
$sourceDirectory = Join-Path $evidenceDirectory 'decompiled-source'
New-Item -ItemType Directory -Path $sourceDirectory -Force |
    Out-Null
$assetEvidencePath = Join-Path $evidenceDirectory 'hero-setz-asset.json'
$setZSourcePath = Join-Path $sourceDirectory 'SetZ.cs.txt'
$heroControllerSourcePath = Join-Path `
    $sourceDirectory `
    'HeroController.cs.txt'

$actualHashes = [ordered]@{
    assemblyCSharp = (Get-FileHash `
        -LiteralPath $AssemblyPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    resourcesAssets = (Get-FileHash `
        -LiteralPath $ResourcesPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    globalManagersAssets = (Get-FileHash `
        -LiteralPath $GlobalManagersAssetsPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}

$setZOutput = @(& $IlspyCmdPath -t SetZ $AssemblyPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw 'ilspycmd failed while decompiling SetZ.'
}
$setZText = $setZOutput -join [Environment]::NewLine
$setZText | Set-Content -LiteralPath $setZSourcePath -Encoding utf8NoBOM

$heroOutput = @(& $IlspyCmdPath -t HeroController $AssemblyPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw 'ilspycmd failed while decompiling HeroController.'
}
$heroText = $heroOutput -join [Environment]::NewLine
$heroText |
    Set-Content -LiteralPath $heroControllerSourcePath -Encoding utf8NoBOM

$assetOutput = @(& $PythonPath `
    $assetInspectorPath `
    --resources $ResourcesPath `
    --global-managers-assets $GlobalManagersAssetsPath 2>&1)
$assetExitCode = $LASTEXITCODE
$assetText = $assetOutput -join [Environment]::NewLine
$assetText | Set-Content -LiteralPath $assetEvidencePath -Encoding utf8NoBOM
if ($assetExitCode -ne 0) {
    throw 'The read-only Hero SetZ Unity asset audit failed.'
}
$assetEvidence = $assetText | ConvertFrom-Json

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$decompileDirectory = [IO.Path]::GetFullPath((Join-Path `
    $temporaryBase `
    ("hktas-t24-heroz-{0}" -f [Guid]::NewGuid().ToString('N'))))
if (-not $decompileDirectory.StartsWith(
        $temporaryBase,
        [StringComparison]::OrdinalIgnoreCase) `
        -or [IO.Path]::GetFileName($decompileDirectory) `
            -notlike 'hktas-t24-heroz-*') {
    throw 'Refusing to create an unsafe T24 decompile directory.'
}

$heroZReadMatches = @()
try {
    New-Item -ItemType Directory -Path $decompileDirectory -Force |
        Out-Null
    $null = @(& $IlspyCmdPath `
        -p `
        -o $decompileDirectory `
        $AssemblyPath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw 'ilspycmd failed while decompiling the vanilla assembly.'
    }
    $heroZReadMatches = @(
        Get-ChildItem `
            -LiteralPath $decompileDirectory `
            -Recurse `
            -File `
            -Filter '*.cs' |
            Select-String `
                -Pattern '\b(heroObject|hero)\.transform\.position\.z\b' `
                -CaseSensitive |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    file = [IO.Path]::GetRelativePath(
                        $decompileDirectory,
                        $_.Path).Replace('\', '/')
                    lineNumber = [int]$_.LineNumber
                    line = [string]$_.Line.Trim()
                }
            })
}
finally {
    if (Test-Path -LiteralPath $decompileDirectory) {
        $resolvedDeleteTarget = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $decompileDirectory).Path)
        if (-not $resolvedDeleteTarget.StartsWith(
                $temporaryBase,
                [StringComparison]::OrdinalIgnoreCase) `
                -or [IO.Path]::GetFileName($resolvedDeleteTarget) `
                    -notlike 'hktas-t24-heroz-*') {
            throw 'Refusing to remove an unsafe T24 decompile directory.'
        }
        Remove-Item -LiteralPath $resolvedDeleteTarget -Recurse -Force
    }
}

$readFiles = @(
    $heroZReadMatches.file |
        ForEach-Object { [IO.Path]::GetFileName($_) } |
        Sort-Object -Unique)
$expectedReadFiles = @('JumpEffects.cs', 'SpellGetOrb.cs')
$sourceChecks = [ordered]@{
    assemblyHashExact = $actualHashes.assemblyCSharp `
        -ceq $expectedHashes.assemblyCSharp
    resourcesHashExact = $actualHashes.resourcesAssets `
        -ceq $expectedHashes.resourcesAssets
    globalManagersHashExact = $actualHashes.globalManagersAssets `
        -ceq $expectedHashes.globalManagersAssets
    setZOnEnablePresent = $setZText -match 'private void OnEnable\(\)'
    setZRandomRangeExact = @(
        [regex]::Matches(
            $setZText,
            'setZ = Random\.Range\(z, z \+ 0\.0009999f\);')).Count -eq 1
    setZDelayExact = $setZText -match `
        'yield return new WaitForSeconds\(delayBeforeRandomizing\);'
    setZWritesOnlyTransformZ = $setZText -match `
            'transform\.SetPositionZ\(setZ\);' `
        -and $setZText -notmatch `
            'Rigidbody2D|PlayMaker|PlayerData|velocity|damage|collision|input'
    heroUpdate10Present = $heroText -match 'private void Update10\(\)'
    heroUpdateCadenceExact = $heroText -match `
        'Time\.frameCount % 10 == 0'
    heroZResetConditionExact = $heroText -match `
        'transform\.position\.z != 0\.004f'
    heroZResetWriteExact = $heroText -match `
        'transform\.SetPositionZ\(0\.004f\);'
    heroZReadCountExact = $heroZReadMatches.Count -eq 2
    heroZReadFilesExact = $readFiles.Count -eq 2 `
        -and $readFiles[0] -ceq $expectedReadFiles[0] `
        -and $readFiles[1] -ceq $expectedReadFiles[1]
    heroZReadsAreVisualConstruction = @(
        $heroZReadMatches |
            Where-Object {
                $_.line -match 'Spawn\(|new Vector3\('
            }).Count -eq 2
    heroZReadsExcludeGameplayTokens = @(
        $heroZReadMatches |
            Where-Object {
                $_.line -match `
                    'Rigidbody2D|velocity|collision|damage|PlayMaker|FSM|input'
            }).Count -eq 0
    unityAssetAuditPass = [string]$assetEvidence.verdict -ceq 'PASS'
    heroAssetZLowerExact = [string]$assetEvidence.setZ.zCanonicalHex `
        -ceq '3b83126f'
    heroAssetZUpperExact = `
        [string]$assetEvidence.setZ.randomRangeMaximumCanonicalHex `
            -ceq '3ba3d634'
}
$verdict = if (@($sourceChecks.Values | Where-Object { -not $_ }).Count `
        -eq 0) {
    'PASS'
}
else {
    'FAIL'
}

$artifact = [ordered]@{
    schemaVersion = 1
    verdict = $verdict
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    policyId = 'hero-z-vanilla-setz-random-v1'
    purpose = 'read-only-vanilla-source-and-asset-classification'
    minimumCanonicalHex = '3b83126f'
    maximumCanonicalHex = '3ba3d634'
    inputs = [ordered]@{
        assemblyCSharp = $AssemblyPath
        resourcesAssets = $ResourcesPath
        globalManagersAssets = $GlobalManagersAssetsPath
        expectedSha256 = $expectedHashes
        actualSha256 = $actualHashes
    }
    tooling = [ordered]@{
        ilspycmd = $IlspyCmdPath
        ilspycmdSha256 = (Get-FileHash `
            -LiteralPath $IlspyCmdPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        python = $PythonPath
        assetInspector = $assetInspectorPath
        assetInspectorSha256 = (Get-FileHash `
            -LiteralPath $assetInspectorPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    decompiledEvidence = [ordered]@{
        setZSource = $setZSourcePath
        setZSourceSha256 = (Get-FileHash `
            -LiteralPath $setZSourcePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        heroControllerSource = $heroControllerSourcePath
        heroControllerSourceSha256 = (Get-FileHash `
            -LiteralPath $heroControllerSourcePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        heroZReadPattern = `
            '\b(heroObject|hero)\.transform\.position\.z\b'
        heroZReadMatches = $heroZReadMatches
    }
    unityAssetEvidence = [ordered]@{
        path = $assetEvidencePath
        sha256 = (Get-FileHash `
            -LiteralPath $assetEvidencePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        verdict = [string]$assetEvidence.verdict
        heroName = [string]$assetEvidence.hero.name
        heroPathId = [long]$assetEvidence.hero.pathId
        setZComponentPathId = [long]$assetEvidence.setZ.componentPathId
        setZScriptPathId = [long]$assetEvidence.setZ.scriptPathId
        serializedParameters = [ordered]@{
            z = [single]$assetEvidence.setZ.z
            zCanonicalHex = [string]$assetEvidence.setZ.zCanonicalHex
            dontRandomize = [bool]$assetEvidence.setZ.dontRandomize
            randomizeFromStartingValue = `
                [bool]$assetEvidence.setZ.randomizeFromStartingValue
            delayBeforeRandomizing = `
                [single]$assetEvidence.setZ.delayBeforeRandomizing
            randomRangeMaximumCanonicalHex = `
                [string]$assetEvidence.setZ.randomRangeMaximumCanonicalHex
        }
    }
    checks = $sourceChecks
}
$artifact |
    ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM

Write-Output "T24 Hero Z vanilla contract audit: $verdict; $EvidencePath"
if ($verdict -ne 'PASS') {
    exit 1
}
