$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\Build-CompanionBundle.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Bundle script has parse errors.' }
$guard = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Assert-NoRunningTasApplications'
}, $true)
if ($null -eq $guard) { throw 'Missing install guard.' }
# Load only the guard, never the publishing/install workflow.
. ([scriptblock]::Create($guard.Extent.Text))
function Get-Process { param($Name, $ErrorAction) $script:fakeProcesses }
$script:fakeProcesses = @()
Assert-NoRunningTasApplications
foreach ($name in @('hollow_knight', 'HollowKnightTAS.Companion')) {
    $script:fakeProcesses = @([pscustomobject]@{ ProcessName = $name; Id = 123 })
    $rejected = $false
    try { Assert-NoRunningTasApplications }
    catch {
        if ($_.Exception.Message -notlike 'Close the game and save/close Studio*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Guard accepted running $name" }
}
$calls = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Assert-NoRunningTasApplications'
}, $true))
$deletes = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Remove-Item'
}, $true))
if ($calls.Count -ne 2 -or $calls[0].Extent.StartOffset -gt $deletes[0].Extent.StartOffset -or
    $calls[1].Extent.StartOffset -gt $deletes[1].Extent.StartOffset) {
    throw 'Install guards must precede output cleanup and installed-file replacement.'
}
'PASS: idle accepted; game/Studio rejected; both guards precede destructive work.'
