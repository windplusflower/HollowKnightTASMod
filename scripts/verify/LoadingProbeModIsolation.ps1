function Write-ModIsolation {
    [ordered]@{ onlyTasMods=[bool]$OnlyTasMods; modRoot=$modRoot; quarantine=$modQuarantine;
        status=$script:modIsolationStatus; entries=@($modMoves.ToArray()) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'mods-isolation.json') -Encoding utf8
}
function Read-ModEntryAudit([string]$entryPath) {
    $entryItem = Get-Item -LiteralPath $entryPath
    $entryChildren = if ($entryItem.PSIsContainer) { @(Get-ChildItem -LiteralPath $entryPath -Recurse -Force) } else { @($entryItem) }
    if (@($entryChildren | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) { throw 'Mod isolation does not follow links.' }
    $entryFiles = @($entryChildren | Where-Object { -not $_.PSIsContainer })
    $entryAudit = @($entryFiles | Sort-Object FullName | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Mod isolation does not follow file links.' }
        [ordered]@{ file=if ($entryItem.PSIsContainer) { [IO.Path]::GetRelativePath($entryPath, $_.FullName) } else { $_.Name };
            length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return (ConvertTo-Json -InputObject $entryAudit -Depth 4 -Compress)
}
function Assert-ModMovePaths([string]$original, [string]$isolated) {
    $originalAbsolute = [IO.Path]::GetFullPath($original)
    $isolatedAbsolute = [IO.Path]::GetFullPath($isolated)
    if ([IO.Path]::GetDirectoryName($originalAbsolute) -ne $modRoot -or
        [IO.Path]::GetDirectoryName($isolatedAbsolute) -ne [IO.Path]::GetFullPath($modQuarantine) -or
        $originalAbsolute -eq [IO.Path]::GetFullPath($install) -or
        [IO.Path]::GetFileName($originalAbsolute) -eq 'disabled') { throw 'Mod isolation path escaped its direct owned boundaries.' }
}
function Isolate-OtherMods {
    if (-not $OnlyTasMods) { return }
    $foreignEntries = @(Get-ChildItem -LiteralPath $modRoot -Force | Where-Object {
        if ($_.PSIsContainer) { $_.Name -ne 'disabled' -and $_.FullName -ne [IO.Path]::GetFullPath($install) }
        else { $_.Extension -eq '.dll' }
    })
    foreach ($foreignEntry in $foreignEntries) {
        if ($foreignEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Mod isolation does not move linked Mod directories.' }
        $isolatedPath = Join-Path $modQuarantine $foreignEntry.Name
        Assert-ModMovePaths $foreignEntry.FullName $isolatedPath
        $modMoves.Add([ordered]@{ original=$foreignEntry.FullName; isolated=$isolatedPath;
            beforeAudit=(Read-ModEntryAudit $foreignEntry.FullName); moved=$false; restored=$false })
    }
    $script:modIsolationStatus = 'planned'
    Write-ModIsolation
    New-Item -ItemType Directory -Path $modQuarantine | Out-Null
    foreach ($modMove in $modMoves) {
        Assert-ModMovePaths $modMove.original $modMove.isolated
        Move-Item -LiteralPath $modMove.original -Destination $modMove.isolated
        $modMove.moved = $true
        Write-ModIsolation
    }
    $script:modIsolationStatus = 'isolated'
    Write-ModIsolation
}
function Restore-OtherMods {
    if (-not $OnlyTasMods) { return }
    foreach ($modMove in $modMoves) {
        if (-not $modMove.moved -or $modMove.restored) { continue }
        Assert-ModMovePaths $modMove.original $modMove.isolated
        if (Test-Path -LiteralPath $modMove.original) { throw 'Mod restoration refuses to overwrite an existing entry.' }
        Move-Item -LiteralPath $modMove.isolated -Destination $modMove.original
        if ((Read-ModEntryAudit $modMove.original) -ne $modMove.beforeAudit) { throw 'Restored Mod file audit differs.' }
        $modMove.restored = $true
        Write-ModIsolation
    }
    $script:modIsolationStatus = 'restored'
    Write-ModIsolation
    if ((Test-Path -LiteralPath $modQuarantine) -and @(Get-ChildItem -LiteralPath $modQuarantine -Force).Count -eq 0) {
        # Remove only the empty directory created by this run, never recursively.
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($modQuarantine)) -ne (Join-Path $modRoot 'disabled')) { throw 'Quarantine cleanup escaped disabled Mods.' }
        Remove-Item -LiteralPath $modQuarantine
    }
}
