$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LoadingProbeModIsolation.ps1')
$fixtureParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/scene-checkpoints/p3'))
$outputRoot = Join-Path $fixtureParent ('mod-isolation-test-' + [Guid]::NewGuid().ToString('N'))
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($outputRoot)) -ne $fixtureParent) { throw 'Fixture escaped its workspace boundary.' }
$modRoot = Join-Path $outputRoot 'Mods'
$install = Join-Path $modRoot 'HollowKnightTAS'
$modQuarantine = Join-Path (Join-Path $modRoot 'disabled') '__HKTAS_Test'
$OnlyTasMods = $true
$modMoves = [Collections.Generic.List[object]]::new()
$modIsolationStatus = 'not-requested'
foreach ($fixtureDirectory in @($install, (Join-Path $modRoot 'OtherA'), (Join-Path $modRoot 'OtherB'), (Join-Path $modRoot 'disabled/Existing'))) {
    New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureDirectory 'identity.dll'), $fixtureDirectory)
}
[IO.File]::WriteAllText((Join-Path $modRoot 'RootMod.dll'), 'root mod')
$before = Read-ModEntryAudit $modRoot
Isolate-OtherMods
if ($modMoves.Count -ne 3 -or (Test-Path -LiteralPath (Join-Path $modRoot 'OtherA')) -or
    -not (Test-Path -LiteralPath (Join-Path $install 'identity.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $modRoot 'disabled/Existing/identity.dll'))) { throw 'Isolation scope differs.' }
Restore-OtherMods
if ((Read-ModEntryAudit $modRoot) -ne $before -or $modIsolationStatus -ne 'restored') { throw 'Full restoration differs.' }

$modMoves.Clear()
Isolate-OtherMods
$conflictEntry = @($modMoves | Where-Object { [IO.Path]::GetFileName($_.original) -eq 'OtherA' })[0]
Assert-ModMovePaths $conflictEntry.original $conflictEntry.isolated
[IO.File]::WriteAllText($conflictEntry.original, 'new conflicting file must remain unchanged')
$refused = $false
try { Restore-OtherMods } catch { $refused = $_.Exception.Message -eq 'Mod restoration refuses to overwrite an existing entry.' }
if (-not $refused -or [IO.File]::ReadAllText($conflictEntry.original) -ne 'new conflicting file must remain unchanged' -or
    -not (Test-Path -LiteralPath $conflictEntry.isolated)) { throw 'Conflicting entry was not protected.' }
# Delete only this fixture's new conflict file, after validating its exact path.
Assert-ModMovePaths $conflictEntry.original $conflictEntry.isolated
Remove-Item -LiteralPath $conflictEntry.original
Restore-OtherMods
if ((Read-ModEntryAudit $modRoot) -ne $before) { throw 'Partial restoration recovery differs.' }

$pathRejections = 0
foreach ($rejectedPath in @($install, (Join-Path $modRoot 'disabled'), (Join-Path $outputRoot 'outside'))) {
    try { Assert-ModMovePaths $rejectedPath (Join-Path $modQuarantine ([IO.Path]::GetFileName($rejectedPath))) }
    catch { $pathRejections++ }
}
if ($pathRejections -ne 3) { throw 'Path boundary checks differ.' }
$result = [ordered]@{ status='passed'; isolationRoundTrip=$true; overwriteRefusal=$true;
    partialRecovery=$true; pathRejections=$pathRejections; fixture=$outputRoot }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json -Compress | Write-Output
