$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('hktas-incomplete-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$path = Join-Path $root 'trace.jsonl'
[IO.File]::WriteAllText($path, '{}')
[IO.File]::WriteAllText($path + '.incomplete', 'truncated fixture')
try {
    $cases = @(
        @('Invoke-T24CandidateSmoke.ps1', 'Assert-T24TraceContract'),
        @('Invoke-T24CandidateSmoke.ps1', 'Compare-T24PhaseTraces'),
        @('Measure-T24ReferenceEnvelope.ps1', 'Read-T24ValidatedTrace'))
    foreach ($case in $cases) {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $case[0]), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw $errors[0] }
        $name = $case[1]
        $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (!$function) { throw "Missing function: $name" }
        # Load only the selected pure validator, never the script's game/cleanup workflow.
        . ([scriptblock]::Create($function.Extent.Text))
        $rejected = $false
        try {
            if ($name -eq 'Compare-T24PhaseTraces') { & $name -ReferencePath $path -CandidatePath $path }
            else { & $name -Path $path }
        } catch {
            if ($_.Exception.Message -notlike 'Incomplete*') { throw }
            $rejected = $true
        }
        if (!$rejected) { throw "Incomplete trace was accepted by $name" }
    }
    [pscustomobject]@{ Result = 'PASS'; Cases = $cases.Count }
} finally {
    # Exact GUID-named directory created above; no game or historical paths are involved.
    [IO.Directory]::Delete($root, $true)
}
