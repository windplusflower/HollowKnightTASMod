[CmdletBinding()]
param(
    [Parameter()]
    [string]$ManagedDir = "D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed",

    [Parameter()]
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot "scripts\verify\S0StaticAudit\S0StaticAudit.csproj"
if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "artifacts\static-audit\s0-static-audit-$timestamp"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$dotnetArgs = @(
    "run",
    "--project", $projectPath,
    "-c", "Release"
)

$cecilInManagedDir = Join-Path $ManagedDir "Mono.Cecil.dll"
if (Test-Path $cecilInManagedDir) {
    $dotnetArgs += "-p:HKManagedDir=$ManagedDir"
}

$dotnetArgs += @(
    "--",
    "--managed-dir", $ManagedDir,
    "--out-dir", $OutputDirectory
)

Write-Host "Running S0 static audit..."
Write-Host "  ManagedDir: $ManagedDir"
Write-Host "  OutputDir : $OutputDirectory"

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    throw "S0StaticAudit failed with exit code $LASTEXITCODE"
}
