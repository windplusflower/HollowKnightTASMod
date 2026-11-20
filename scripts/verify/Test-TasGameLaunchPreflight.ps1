$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '..\Start-TasGame.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$guard = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-NoRunningGame'
}, $true)
if ($null -eq $guard) { throw 'Missing existing-game guard.' }
. ([scriptblock]::Create($guard.Extent.Text))
function Get-Process { param($Name, $ErrorAction) $script:fakeGames }
$script:fakeGames = @()
Assert-NoRunningGame
$script:fakeGames = @([pscustomobject]@{ Id = 42 })
$rejected = $false
try { Assert-NoRunningGame } catch {
    if ($_.Exception.Message -notlike 'A game is already running.*') { throw }
    $rejected = $true
}
if (!$rejected) { throw 'Running game was not rejected.' }
$text = $ast.Extent.Text
foreach ($required in @('VerifiedStartupProfile]::Load(', 'VerifiedGameLauncher]::new(',
    '$attestation.runId -eq $runId', '$state.isStableTitleMenu -eq ''true''',
    '$handle.ReleaseSupervision()', '$handle.Dispose()', '$loadAccepted = $true')) {
    if (!$text.Contains($required)) { throw "Missing launch safety check: $required" }
}
if ($text.Contains('Stop-Process') -or $text.Contains('Copy-Item') -or $text.Contains('Remove-Item')) {
    throw 'Launch workflow must not terminate games or replace files.'
}
if ($text.IndexOf('$handle.ReleaseSupervision()') -gt $text.IndexOf('function Read-LaunchState')) {
    throw 'Observation must not retain kill-on-dispose ownership.'
}
if ([regex]::Matches($text, 'automation call loadGameSlot').Count -ne 1) {
    throw 'Expected one guarded native load call site.'
}
'PASS: syntax, existing-game rejection, verified launch, matching run, stable menu, one load, released process ownership.'
