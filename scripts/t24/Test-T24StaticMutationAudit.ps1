[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$auditScript = Join-Path $PSScriptRoot "Invoke-T24StaticMutationAudit.ps1"
$canonicalAllowlistPath = Join-Path $repositoryRoot "fixtures/t24/runtime-hook-mutation-allowlist.v1.json"
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $campaignId = "t24-static-mutation-audit-self-test-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
    $OutputDirectory = Join-Path $repositoryRoot ("artifacts/vanilla-equivalence/" + $campaignId)
}
elseif (![System.IO.Path]::IsPathRooted($OutputDirectory))
{
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$repositoryPrefix = $repositoryRoot.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
if (!$OutputDirectory.StartsWith(
        $repositoryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "Self-test output must stay inside the repository."
}
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$canonicalText = [System.IO.File]::ReadAllText($canonicalAllowlistPath)
$canonical = $canonicalText | ConvertFrom-Json
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Copy-Allowlist
{
    return ($script:canonicalText | ConvertFrom-Json)
}

function Write-Allowlist
{
    param(
        [object]$Allowlist,
        [string]$Path
    )

    $json = $Allowlist | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($Path, $json + "`n", $script:utf8)
}

function Invoke-AuditCase
{
    param(
        [string]$CaseId,
        [string]$AllowlistPath
    )

    $caseDirectory = Join-Path $script:OutputDirectory $CaseId
    & $script:auditScript -RepositoryRoot $script:repositoryRoot -AllowlistPath $AllowlistPath -OutputDirectory $caseDirectory -NoThrowOnFailure | Out-Null
    $reportPath = Join-Path $caseDirectory "static-audit.json"
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    return [pscustomobject][ordered]@{
        caseId = $CaseId
        reportPath = $reportPath
        verdict = $report.verdict
        failureReasons = @($report.failureReasons)
        unexpectedFindingCount = @($report.unexpectedFindings).Count
        hashMismatchCount = @($report.hashMismatches).Count
        normalTasGameplayWriterCount = [int]$report.normalTasGameplayWriterCount
    }
}

$results = New-Object System.Collections.Generic.List[object]
$positive = Invoke-AuditCase "positive-canonical" $canonicalAllowlistPath
$results.Add($positive) | Out-Null

$missingFinding = Copy-Allowlist
$missingSource = $missingFinding.sources | Where-Object {
        $_.path -eq "src/HollowKnightTAS.Runtime/HollowKnightTASMod.cs"
    }
if ($null -eq $missingSource -or @($missingSource.findings).Count -lt 2)
{
    throw "Could not prepare missing-finding negative control."
}
$missingSource.findings = @($missingSource.findings | Select-Object -Skip 1)
$missingPath = Join-Path $OutputDirectory "negative-missing-finding-allowlist.json"
Write-Allowlist $missingFinding $missingPath
$missingResult = Invoke-AuditCase "negative-missing-finding" $missingPath
$results.Add($missingResult) | Out-Null

$hashMismatch = Copy-Allowlist
$hashedSource = $hashMismatch.sources | Where-Object {
        $_.path -eq "src/HollowKnightTAS.ReferenceObserver/HollowKnightTASReferenceObserverMod.cs"
    }
if ($null -eq $hashedSource)
{
    throw "Could not prepare source-hash negative control."
}
$hashedSource.sourceSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
$hashPath = Join-Path $OutputDirectory "negative-source-hash-allowlist.json"
Write-Allowlist $hashMismatch $hashPath
$hashResult = Invoke-AuditCase "negative-source-hash" $hashPath
$results.Add($hashResult) | Out-Null

$normalWriter = Copy-Allowlist
$writerSource = $normalWriter.sources | Where-Object {
        $_.path -eq "src/HollowKnightTAS.Runtime/Automation/Mutation/HeroPoseMutationAdapter.cs"
    }
if ($null -eq $writerSource)
{
    throw "Could not prepare normal-gameplay-writer negative control."
}
$writerSource.reachability = "normal-tas"
$writerPath = Join-Path $OutputDirectory "negative-normal-gameplay-writer-allowlist.json"
Write-Allowlist $normalWriter $writerPath
$writerResult = Invoke-AuditCase "negative-normal-gameplay-writer" $writerPath
$results.Add($writerResult) | Out-Null

$positivePass = $positive.verdict -eq "PASS"
$missingPass = $missingResult.verdict -eq "FAIL" -and $missingResult.failureReasons -contains "unexpected-finding" -and $missingResult.unexpectedFindingCount -gt 0
$hashPass = $hashResult.verdict -eq "FAIL" -and $hashResult.failureReasons -contains "source-hash-mismatch" -and $hashResult.hashMismatchCount -gt 0
$writerPass = $writerResult.verdict -eq "FAIL" -and $writerResult.failureReasons -contains "policy-failure" -and $writerResult.normalTasGameplayWriterCount -gt 0
$pass = $positivePass -and $missingPass -and $hashPass -and $writerPass

$verdict = [ordered]@{
    schemaVersion = 1
    testId = "t24-static-mutation-audit-self-test-v1"
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    verdict = if ($pass) { "PASS" } else { "FAIL" }
    positiveCanonicalPass = $positivePass
    missingFindingNegativePass = $missingPass
    sourceHashNegativePass = $hashPass
    normalGameplayWriterNegativePass = $writerPass
    cases = $results.ToArray()
}
$verdictPath = Join-Path $OutputDirectory "static-audit-self-test.json"
$verdictJson = $verdict | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    $verdictPath,
    $verdictJson + "`n",
    $utf8)

Write-Host ("T24 static mutation audit self-test verdict=" + $verdict.verdict)
Write-Host ("Report: " + $verdictPath)
if (!$pass)
{
    throw "T24 static mutation audit self-test failed. See $verdictPath"
}

$verdict
