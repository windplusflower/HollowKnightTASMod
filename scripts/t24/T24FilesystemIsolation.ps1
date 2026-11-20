Set-StrictMode -Version Latest

function Move-T24PathNode {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][DateTimeOffset]$Deadline,
        [Parameter(Mandatory)][ref]$LastError
    )

    do {
        if (-not (Test-Path -LiteralPath $Source)) {
            return
        }

        try {
            $sourceItem = Get-Item -LiteralPath $Source -Force
            if ($sourceItem.PSIsContainer) {
                if (($sourceItem.Attributes `
                            -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "T24 path moves do not follow reparse points: $Source"
                }

                if (Test-Path -LiteralPath $Destination) {
                    $destinationItem = Get-Item `
                        -LiteralPath $Destination `
                        -Force
                    if (-not $destinationItem.PSIsContainer `
                            -or ($destinationItem.Attributes `
                                -band [IO.FileAttributes]::ReparsePoint) `
                                -ne 0) {
                        throw (
                            'T24 path move destination type mismatch: ' `
                            + $Destination)
                    }
                }
                else {
                    New-Item `
                        -ItemType Directory `
                        -Path $Destination `
                        -ErrorAction Stop |
                        Out-Null
                }

                foreach ($child in @(
                        Get-ChildItem `
                            -LiteralPath $Source `
                            -Force `
                            -ErrorAction Stop)) {
                    Move-T24PathNode `
                        -Source $child.FullName `
                        -Destination (Join-Path $Destination $child.Name) `
                        -Deadline $Deadline `
                        -LastError $LastError
                }

                Remove-Item `
                    -LiteralPath $Source `
                    -Force `
                    -ErrorAction Stop
            }
            else {
                if (Test-Path -LiteralPath $Destination) {
                    $destinationItem = Get-Item `
                        -LiteralPath $Destination `
                        -Force
                    if ($destinationItem.PSIsContainer) {
                        throw (
                            'T24 path move destination type mismatch: ' `
                            + $Destination)
                    }

                    $sourceHash = (Get-FileHash `
                        -LiteralPath $Source `
                        -Algorithm SHA256).Hash
                    $destinationHash = (Get-FileHash `
                        -LiteralPath $Destination `
                        -Algorithm SHA256).Hash
                    if (-not [string]::Equals(
                            $sourceHash,
                            $destinationHash,
                            [StringComparison]::OrdinalIgnoreCase)) {
                        throw (
                            'T24 path move found conflicting files: ' `
                            + $Source `
                            + ' -> ' `
                            + $Destination)
                    }

                    Remove-Item `
                        -LiteralPath $Source `
                        -Force `
                        -ErrorAction Stop
                }
                else {
                    Move-Item `
                        -LiteralPath $Source `
                        -Destination $Destination `
                        -ErrorAction Stop
                }
            }

            if (-not (Test-Path -LiteralPath $Source)) {
                return
            }
        }
        catch {
            $LastError.Value = $_.Exception
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)

    $detail = if ($null -eq $LastError.Value) {
        'source path remained after the move returned'
    }
    else {
        $LastError.Value.Message
    }
    throw "Timed out restoring '$Source' to '$Destination': $detail"
}

function Move-T24PathWithRetry {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 15
    )

    $sourceFull = [IO.Path]::GetFullPath($Source)
    $destinationFull = [IO.Path]::GetFullPath($Destination)
    if ([string]::Equals(
            $sourceFull,
            $destinationFull,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'T24 path move source and destination must differ.'
    }

    $sourcePrefix = $sourceFull.TrimEnd('\', '/') `
        + [IO.Path]::DirectorySeparatorChar
    if ($destinationFull.StartsWith(
            $sourcePrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'T24 path move destination must not be inside its source.'
    }

    $lastError = $null
    Move-T24PathNode `
        -Source $sourceFull `
        -Destination $destinationFull `
        -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)) `
        -LastError ([ref]$lastError)
}
