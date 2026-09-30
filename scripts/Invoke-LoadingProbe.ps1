param(
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [Parameter(Mandatory=$true)][string] $Movie,
    [long] $FirstFrame = 0,
    [long] $EndFrame = 3800,
    [ValidateRange(1,3)][int] $Runs = 1
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/scene-checkpoints/p0')) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be under artifacts/scene-checkpoints/p0/.' }
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a fresh evidence directory.' }
if (Get-Process hollow_knight,HollowKnightTAS.Companion,StudioScenarioHarness -ErrorAction SilentlyContinue) { throw 'A game or Studio session is already running.' }
$install = 'D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS'
$companion = Join-Path $install 'Companion/win-x64'
$harnessBuild = Join-Path $repo 'scripts/StudioScenarioHarness/bin/Release/net8.0-windows10.0.19041.0/win-x64'
$bundleTool = Join-Path $repo 'src/HollowKnightTAS.BundleTool/bin/Release/net8.0/HollowKnightTAS.BundleTool.dll'
$runtime = Join-Path $repo 'src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll'
$saveRoot = Join-Path $env:USERPROFILE 'AppData/LocalLow/Team Cherry/Hollow Knight'
$hostFiles = @('StudioScenarioHarness.exe','StudioScenarioHarness.dll','StudioScenarioHarness.deps.json','StudioScenarioHarness.runtimeconfig.json')
foreach ($name in $hostFiles) { if (Test-Path -LiteralPath (Join-Path $companion $name)) { throw "Existing test host: $name" } }
foreach ($path in @($runtime,$bundleTool,$Movie) + @($hostFiles | ForEach-Object { Join-Path $harnessBuild $_ })) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file missing: $path" }
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$backup = Join-Path $outputRoot 'installation-backup'
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($name in @('HollowKnightTAS.dll','companion.manifest.json')) { Copy-Item -LiteralPath (Join-Path $install $name) -Destination (Join-Path $backup $name) }
function Read-SaveAudit {
    @(Get-ChildItem -LiteralPath $saveRoot -File -Filter 'user*' | Sort-Object Name | ForEach-Object {
        [ordered]@{ name=$_.Name; length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) | ConvertTo-Json -Depth 4 -Compress
}
$before = Read-SaveAudit
[IO.File]::WriteAllText((Join-Path $outputRoot 'saves-before.json'), $before)
$hostProcess = $null
try {
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $install 'HollowKnightTAS.dll')
    foreach ($name in $hostFiles) { Copy-Item -LiteralPath (Join-Path $harnessBuild $name) -Destination (Join-Path $companion $name) }
    & dotnet $bundleTool sign (Join-Path $repo '.local/signing/companion-private.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature failed.' }
    & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signature verification failed.' }
    @('HollowKnightTAS.dll','HollowKnightTAS.Core.dll','companion.manifest.json') | ForEach-Object {
        $path = Join-Path $install $_
        [ordered]@{ name=$_; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'candidate-identity.json') -Encoding utf8
    for ($run=1; $run -le $Runs; $run++) {
        $destination = Join-Path $outputRoot ('run-{0:d2}' -f $run)
        New-Item -ItemType Directory -Path $destination | Out-Null
        $arguments = @('--headless','--loading-probe', ('--scenario-output="{0}"' -f $destination),
            ('--probe-movie="{0}"' -f ([IO.Path]::GetFullPath($Movie))),
            ('--probe-first={0}' -f $FirstFrame), ('--probe-end={0}' -f $EndFrame))
        $hostProcess = Start-Process -FilePath (Join-Path $companion 'StudioScenarioHarness.exe') -ArgumentList $arguments `
            -WorkingDirectory $companion -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $destination 'stdout.log') -RedirectStandardError (Join-Path $destination 'stderr.log')
        [ordered]@{ run=$run; pid=$hostProcess.Id; status='running' } | ConvertTo-Json -Compress | Write-Output
        if (-not $hostProcess.WaitForExit(270000)) { $hostProcess.Kill(); throw 'Probe host timed out.' }
        $hostProcess.Refresh()
        if ($hostProcess.ExitCode -ne 0) { throw "Probe run $run failed with exit $($hostProcess.ExitCode)." }
        if ((Read-SaveAudit) -ne $before) { throw 'Original save audit mismatch.' }
        [ordered]@{ run=$run; status='trace-collected'; phaseP0Passed=$false } | ConvertTo-Json -Compress | Write-Output
    }
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { $hostProcess.Kill(); $hostProcess.WaitForExit() }
    # This runner admitted no existing game; only terminate a child carrying our probe environment
    # through its owned host if App shutdown has not already completed. Do not kill unrelated PIDs.
    $remainingGame = Get-Process hollow_knight -ErrorAction SilentlyContinue
    if ($remainingGame) {
        Write-Warning 'Game remains running. Candidate installation retained; restore after identifying and closing the owned process.'
    } else {
        foreach ($name in @('HollowKnightTAS.dll','companion.manifest.json')) {
            Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $install $name)
        }
        $withdrawn = Join-Path $outputRoot 'withdrawn-test-host'
        New-Item -ItemType Directory -Path $withdrawn | Out-Null
        foreach ($name in $hostFiles) {
            $source = [IO.Path]::GetFullPath((Join-Path $companion $name))
            if (-not $source.StartsWith([IO.Path]::GetFullPath($companion) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test-host path escaped installation.' }
            if (Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination (Join-Path $withdrawn $name) }
        }
        & dotnet $bundleTool verify (Join-Path $repo '.local/signing/companion-public.json') $install (Join-Path $install 'companion.manifest.json')
        if ($LASTEXITCODE -ne 0) { throw 'Original installation verification failed.' }
    }
    $after = Read-SaveAudit
    [IO.File]::WriteAllText((Join-Path $outputRoot 'saves-after.json'), $after)
    if ($after -ne $before) { throw 'Original save audit mismatch on cleanup.' }
}
