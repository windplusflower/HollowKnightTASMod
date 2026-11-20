[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$AllowlistPath = "",
    [string]$OutputDirectory = "",
    [switch]$NoThrowOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$requiredScanRoots = @(
    "src/HollowKnightTAS.Runtime",
    "src/HollowKnightTAS.ReferenceObserver",
    "src/HollowKnightTAS.GameObservation",
    "src/HollowKnightTAS.ClockPayload"
)
$allowedReachability = @(
    "normal-tas",
    "verification-only",
    "debug-only"
)
$directGameplayWriterCategories = @(
    "animator-writer",
    "fsm-variable-writer",
    "hero-control-writer",
    "hero-input-gate-writer",
    "hero-transform-writer",
    "hero-velocity-writer",
    "playmaker-event-writer",
    "playmaker-state-writer",
    "player-resource-writer",
    "random-state-writer",
    "rigidbody-position-writer",
    "rigidbody-velocity-writer",
    "transform-position-writer"
)
$writerCategories = @($directGameplayWriterCategories) + @(
    "input-binding-writer",
    "input-value-writer",
    "scene-activation-writer",
    "reflection-field-writer",
    "scene-orchestration-writer",
    "time-settings-writer"
)

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath))
    {
        throw "Repository-relative path expected: $RelativePath"
    }

    $candidate = [System.IO.Path]::GetFullPath(
        (Join-Path $script:repositoryRootFull $RelativePath))
    if (!$candidate.StartsWith(
            $script:repositoryPrefix,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Path escapes the repository: $RelativePath"
    }

    return $candidate
}

function Get-RepositoryRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FullPath
    )

    $resolved = [System.IO.Path]::GetFullPath($FullPath)
    if (!$resolved.StartsWith(
            $script:repositoryPrefix,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Scanned path escapes the repository: $FullPath"
    }

    return $resolved.Substring($script:repositoryPrefix.Length).Replace("\", "/")
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [object]$Default = $null
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value)
    {
        return $Default
    }

    return $property.Value
}

function Normalize-Expression {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Expression
    )

    return ($Expression -replace "\s+", "")
}

function Add-Finding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Category,
        [Parameter(Mandatory = $true)]
        [string]$Expression,
        [Parameter(Mandatory = $true)]
        [int]$LineNumber,
        [Parameter(Mandatory = $true)]
        [string]$LineText
    )

    $normalized = Normalize-Expression $Expression
    $dedupeKey = "$Path|#|$Category|#|$normalized|#|$LineNumber"
    if ($script:findingKeys.Add($dedupeKey))
    {
        $script:findings.Add(
            [pscustomobject][ordered]@{
                path = $Path
                category = $Category
                expression = $normalized
                line = $LineNumber
                source = $LineText.Trim()
            }) | Out-Null
    }
}

function Test-IsWriterCategory {
    param([string]$Category)

    return $script:writerCategories -contains $Category
}

function Test-IsDirectGameplayWriterCategory {
    param([string]$Category)

    return $script:directGameplayWriterCategories -contains $Category
}

function New-GroupKey {
    param(
        [string]$Path,
        [string]$Category,
        [string]$Expression
    )

    return "$Path|#|$Category|#|$Expression"
}

function Count-Literal {
    param(
        [string]$Text,
        [string]$Expression
    )

    if ([string]::IsNullOrEmpty($Expression))
    {
        return 0
    }

    return [System.Text.RegularExpressions.Regex]::Matches(
        $Text,
        [System.Text.RegularExpressions.Regex]::Escape($Expression)).Count
}

$repositoryRootFull = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd("\", "/")
$repositoryPrefix = $repositoryRootFull + [System.IO.Path]::DirectorySeparatorChar
if (![System.IO.Directory]::Exists($repositoryRootFull))
{
    throw "Repository root does not exist: $repositoryRootFull"
}

if ([string]::IsNullOrWhiteSpace($AllowlistPath))
{
    $AllowlistPath = Join-Path $repositoryRootFull "fixtures/t24/runtime-hook-mutation-allowlist.v1.json"
}
elseif (![System.IO.Path]::IsPathRooted($AllowlistPath))
{
    $AllowlistPath = Resolve-RepositoryPath $AllowlistPath
}
else
{
    $AllowlistPath = [System.IO.Path]::GetFullPath($AllowlistPath)
}

if (![System.IO.File]::Exists($AllowlistPath))
{
    throw "T24 static allowlist is missing: $AllowlistPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $campaignId = "t24-static-mutation-audit-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
    $OutputDirectory = Join-Path $repositoryRootFull ("artifacts/vanilla-equivalence/" + $campaignId)
}
elseif (![System.IO.Path]::IsPathRooted($OutputDirectory))
{
    $OutputDirectory = Resolve-RepositoryPath $OutputDirectory
}
else
{
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (!$OutputDirectory.StartsWith(
            $repositoryPrefix,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Output directory must stay inside the repository: $OutputDirectory"
    }
}

$allowlistBytes = [System.IO.File]::ReadAllBytes($AllowlistPath)
$allowlistSha256 = Get-Sha256 $AllowlistPath
$allowlistText = [System.Text.Encoding]::UTF8.GetString($allowlistBytes)
$allowlist = $allowlistText | ConvertFrom-Json

$validationFailures = New-Object System.Collections.Generic.List[string]
if ([int](Get-OptionalProperty $allowlist "schemaVersion" 0) -ne 1)
{
    $validationFailures.Add("Allowlist schemaVersion must be 1.")
}

$allowlistRoots = @(Get-OptionalProperty $allowlist "scanRoots" @())
$sortedRequiredRoots = @($requiredScanRoots | Sort-Object)
$sortedAllowlistRoots = @($allowlistRoots | ForEach-Object { [string]$_ } | Sort-Object)
if (($sortedRequiredRoots -join "`n") -cne ($sortedAllowlistRoots -join "`n"))
{
    $validationFailures.Add(
        "Allowlist scanRoots must equal the three frozen T24 source roots.")
}

$normalTasAllowedPolicies = @(
    Get-OptionalProperty $allowlist "normalTasAllowedWriterPolicies" @()
    | ForEach-Object { [string]$_ }
)
if ($normalTasAllowedPolicies.Count -eq 0)
{
    $validationFailures.Add("normalTasAllowedWriterPolicies must not be empty.")
}

$sourceFiles = New-Object System.Collections.Generic.List[System.IO.FileInfo]
foreach ($scanRoot in $requiredScanRoots)
{
    $scanRootFull = Resolve-RepositoryPath $scanRoot
    if (![System.IO.Directory]::Exists($scanRootFull))
    {
        throw "Required scan root does not exist: $scanRoot"
    }

    foreach ($file in Get-ChildItem -LiteralPath $scanRootFull -Recurse -File -Filter "*.cs")
    {
        $sourceFiles.Add($file) | Out-Null
    }
}

$sourceFiles = @($sourceFiles | Sort-Object FullName -Unique)
$findings = New-Object System.Collections.Generic.List[object]
$findingKeys = New-Object System.Collections.Generic.HashSet[string]

$eventSubscriptionRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_]*\s*(?:\+=|-=))")
$lifecycleRegex = [regex]::new(
    "\bvoid\s+(?<expression>Update|FixedUpdate|LateUpdate|OnGUI)\s*\(")
$timeWriterRegex = [regex]::new(
    "(?<expression>(?:Time\.(?:timeScale|fixedDeltaTime|captureDeltaTime)|Application\.targetFrameRate|QualitySettings\.vSyncCount)\s*=(?!=))")
$sceneActivationRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.(?:IsActivationAllowed|allowSceneActivation)\s*=(?!=))")
$reflectionFieldWriterRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_]*Field\??\.SetValue)\s*\(")
$inputBindingRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.(?:AddBinding|AddDefaultBinding|ClearBindings|RemoveBinding))\s*\(")
$inputValueRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.SetValue)\s*\(")
$playMakerEventRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.SendEvent)\s*\(")
$playMakerStateRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.SetState)\s*\(")
$heroControlRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.(?:RegainControl|RelinquishControl))\s*\(")
$memberAssignmentRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_?.]*(?:\.[A-Za-z_][A-Za-z0-9_?]*)*\.(?<member>position|localPosition|velocity|angularVelocity|current_velocity|acceptingInput|health|Value)\s*=(?!=))")
$playerResourceRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.(?:SetInt|SetMPCharge))\s*\(")
$randomInitRegex = [regex]::new(
    "(?<expression>UnityEngine\.Random\.InitState)\s*\(")
$randomStateRegex = [regex]::new(
    "(?<expression>UnityEngine\.Random\.state\s*=)")
$sceneOrchestrationRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?!]*\.(?:BeginSceneTransition|LoadGame|LoadGameFromUI|ReturnToMainMenu|SaveGame|SaveGameToFile|StartNewGame))\s*\(")
$movePositionRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*\.MovePosition)\s*\(")
$animatorWriterRegex = [regex]::new(
    "(?<expression>[A-Za-z_][A-Za-z0-9_.?]*(?:anim|animation|animator)[A-Za-z0-9_.?]*\.(?:Play|SetBool|SetFloat|SetInteger|SetTrigger))\s*\(",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

foreach ($file in $sourceFiles)
{
    $relativePath = Get-RepositoryRelativePath $file.FullName
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++)
    {
        $line = $lines[$lineIndex]
        $lineNumber = $lineIndex + 1

        foreach ($match in $eventSubscriptionRegex.Matches($line))
        {
            $expression = Normalize-Expression $match.Groups["expression"].Value
            $category = if ($expression.StartsWith("On.", [StringComparison]::Ordinal) -or $expression.StartsWith("IL.", [StringComparison]::Ordinal))
            {
                "detour-subscription"
            }
            elseif ($expression.StartsWith("ModHooks.", [StringComparison]::Ordinal))
            {
                "modhook-subscription"
            }
            else
            {
                "runtime-event-subscription"
            }
            Add-Finding $relativePath $category $expression $lineNumber $line
        }

        foreach ($match in $lifecycleRegex.Matches($line))
        {
            Add-Finding $relativePath "unity-lifecycle" $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $timeWriterRegex.Matches($line))
        {
            Add-Finding $relativePath "time-settings-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $sceneActivationRegex.Matches($line))
        {
            Add-Finding $relativePath "scene-activation-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $reflectionFieldWriterRegex.Matches($line))
        {
            Add-Finding $relativePath "reflection-field-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $inputBindingRegex.Matches($line))
        {
            Add-Finding $relativePath "input-binding-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $inputValueRegex.Matches($line))
        {
            Add-Finding $relativePath "input-value-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $playMakerEventRegex.Matches($line))
        {
            Add-Finding $relativePath "playmaker-event-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $playMakerStateRegex.Matches($line))
        {
            Add-Finding $relativePath "playmaker-state-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $heroControlRegex.Matches($line))
        {
            Add-Finding $relativePath "hero-control-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $memberAssignmentRegex.Matches($line))
        {
            $expression = Normalize-Expression $match.Groups["expression"].Value
            $member = $match.Groups["member"].Value
            $category = switch ($member)
            {
                "acceptingInput" { "hero-input-gate-writer"; break }
                "current_velocity" { "hero-velocity-writer"; break }
                "health" {
                    if ($expression -match "(^|\.)(player|playerData|PlayerData\.instance)\.health=")
                    {
                        "player-resource-writer"
                    }
                    else
                    {
                        "sensitive-member-assignment-review"
                    }
                    break
                }
                "Value" { "fsm-variable-writer"; break }
                "velocity" { "rigidbody-velocity-writer"; break }
                "angularVelocity" { "rigidbody-velocity-writer"; break }
                "position" {
                    if ($expression -match "\.transform\.position=")
                    {
                        if ($expression -match "(^|\.)hero(\.|\?)")
                        {
                            "hero-transform-writer"
                        }
                        else
                        {
                            "transform-position-writer"
                        }
                    }
                    elseif ($expression -match "(^|\.)body\.position=")
                    {
                        "rigidbody-position-writer"
                    }
                    else
                    {
                        "transform-position-writer"
                    }
                    break
                }
                "localPosition" { "transform-position-writer"; break }
                default { throw "Unhandled member assignment category: $member" }
            }
            Add-Finding $relativePath $category $expression $lineNumber $line
        }

        foreach ($match in $playerResourceRegex.Matches($line))
        {
            Add-Finding $relativePath "player-resource-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $randomInitRegex.Matches($line))
        {
            Add-Finding $relativePath "random-state-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $randomStateRegex.Matches($line))
        {
            Add-Finding $relativePath "random-state-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $sceneOrchestrationRegex.Matches($line))
        {
            Add-Finding $relativePath "scene-orchestration-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $movePositionRegex.Matches($line))
        {
            Add-Finding $relativePath "rigidbody-position-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }

        foreach ($match in $animatorWriterRegex.Matches($line))
        {
            Add-Finding $relativePath "animator-writer" `
                $match.Groups["expression"].Value $lineNumber $line
        }
    }
}

$actualGroups = @(
    $findings
    | Group-Object path, category, expression
    | ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject][ordered]@{
            path = $first.path
            category = $first.category
            expression = $first.expression
            count = $_.Count
            lines = @($_.Group | ForEach-Object { $_.line } | Sort-Object)
        }
    }
    | Sort-Object path, category, expression
)
$actualByKey = @{}
foreach ($group in $actualGroups)
{
    $actualByKey[(New-GroupKey $group.path $group.category $group.expression)] = $group
}

$expectedByKey = @{}
$resolvedExpected = New-Object System.Collections.Generic.List[object]
$sourceByPath = @{}
$duplicateAllowlistEntries = New-Object System.Collections.Generic.List[object]
$hashMismatches = New-Object System.Collections.Generic.List[object]
$gateFailures = New-Object System.Collections.Generic.List[object]
$policyFailures = New-Object System.Collections.Generic.List[string]

foreach ($source in @(Get-OptionalProperty $allowlist "sources" @()))
{
    $path = [string](Get-OptionalProperty $source "path" "")
    if ([string]::IsNullOrWhiteSpace($path))
    {
        $policyFailures.Add("Allowlist source has an empty path.")
        continue
    }
    $path = $path.Replace("\", "/")
    if ($sourceByPath.ContainsKey($path))
    {
        $duplicateAllowlistEntries.Add(
            [pscustomobject]@{ type = "source"; key = $path }) | Out-Null
        continue
    }
    $sourceByPath[$path] = $source

    $reachability = [string](Get-OptionalProperty $source "reachability" "")
    if ($allowedReachability -notcontains $reachability)
    {
        $policyFailures.Add("Invalid source reachability for ${path}: ${reachability}")
    }
    foreach ($requiredMetadata in @(
            "installPhase",
            "executionPhase",
            "readSet",
            "writeSet",
            "rationale"))
    {
        $value = Get-OptionalProperty $source $requiredMetadata $null
        if ($null -eq $value -or ([string]$value).Length -eq 0)
        {
            $policyFailures.Add("Missing $requiredMetadata metadata for ${path}.")
        }
    }

    $sourceFull = $null
    try
    {
        $sourceFull = Resolve-RepositoryPath $path
    }
    catch
    {
        $policyFailures.Add($_.Exception.Message)
    }
    if ($null -ne $sourceFull -and ![System.IO.File]::Exists($sourceFull))
    {
        $policyFailures.Add("Allowlist source is missing: ${path}")
        $sourceFull = $null
    }

    $frozenHash = [string](Get-OptionalProperty $source "sourceSha256" "")
    if ($null -ne $sourceFull -and ![string]::IsNullOrWhiteSpace($frozenHash))
    {
        $actualHash = Get-Sha256 $sourceFull
        if ($actualHash -cne $frozenHash.ToLowerInvariant())
        {
            $hashMismatches.Add(
                [pscustomobject][ordered]@{
                    path = $path
                    expectedSha256 = $frozenHash.ToLowerInvariant()
                    actualSha256 = $actualHash
                }) | Out-Null
        }
    }

    $gates = @(Get-OptionalProperty $source "gates" @())
    foreach ($gate in $gates)
    {
        $gatePath = [string](Get-OptionalProperty $gate "path" "")
        $gateExpression = [string](Get-OptionalProperty $gate "expression" "")
        $gateExpectedCount = [int](Get-OptionalProperty $gate "count" -1)
        try
        {
            $gateFull = Resolve-RepositoryPath $gatePath
            if (![System.IO.File]::Exists($gateFull))
            {
                throw "Gate source is missing: $gatePath"
            }
            $gateText = [System.IO.File]::ReadAllText($gateFull)
            $gateActualCount = Count-Literal $gateText $gateExpression
            $gateHashExpected = [string](Get-OptionalProperty $gate "sourceSha256" "")
            $gateHashActual = Get-Sha256 $gateFull
            if ($gateActualCount -ne $gateExpectedCount -or ([string]::IsNullOrWhiteSpace($gateHashExpected)) -or $gateHashActual -cne $gateHashExpected.ToLowerInvariant())
            {
                $gateFailures.Add(
                    [pscustomobject][ordered]@{
                        path = $gatePath.Replace("\", "/")
                        expression = $gateExpression
                        expectedCount = $gateExpectedCount
                        actualCount = $gateActualCount
                        expectedSha256 = $gateHashExpected.ToLowerInvariant()
                        actualSha256 = $gateHashActual
                    }) | Out-Null
            }
        }
        catch
        {
            $gateFailures.Add(
                [pscustomobject][ordered]@{
                    path = $gatePath
                    expression = $gateExpression
                    expectedCount = $gateExpectedCount
                    actualCount = -1
                    error = $_.Exception.Message
                }) | Out-Null
        }
    }

    foreach ($expected in @(Get-OptionalProperty $source "findings" @()))
    {
        $category = [string](Get-OptionalProperty $expected "category" "")
        $expression = Normalize-Expression `
            ([string](Get-OptionalProperty $expected "expression" ""))
        $count = [int](Get-OptionalProperty $expected "count" 0)
        $findingReachability = [string](
            Get-OptionalProperty $expected "reachability" $reachability)
        $normalTasPolicy = [string](
            Get-OptionalProperty $expected "normalTasPolicy" "")
        $key = New-GroupKey $path $category $expression

        if ($expectedByKey.ContainsKey($key))
        {
            $duplicateAllowlistEntries.Add(
                [pscustomobject]@{ type = "finding"; key = $key }) | Out-Null
            continue
        }
        if ($count -le 0)
        {
            $policyFailures.Add("Finding count must be positive: ${key}")
        }
        if ($allowedReachability -notcontains $findingReachability)
        {
            $policyFailures.Add("Invalid finding reachability: ${key}")
        }

        $resolved = [pscustomobject][ordered]@{
            path = $path
            category = $category
            expression = $expression
            count = $count
            reachability = $findingReachability
            normalTasPolicy = $normalTasPolicy
            installPhase = Get-OptionalProperty $source "installPhase" ""
            executionPhase = Get-OptionalProperty $source "executionPhase" @()
            readSet = Get-OptionalProperty $source "readSet" @()
            writeSet = Get-OptionalProperty $source "writeSet" @()
            rationale = Get-OptionalProperty $source "rationale" ""
        }
        $expectedByKey[$key] = $resolved
        $resolvedExpected.Add($resolved) | Out-Null

        if ((Test-IsWriterCategory $category) -and $findingReachability -ne "normal-tas")
        {
            if ([string]::IsNullOrWhiteSpace($frozenHash))
            {
                $policyFailures.Add(
                    "Non-normal writer source must freeze SHA-256: ${path}")
            }
            if ($gates.Count -eq 0)
            {
                $policyFailures.Add(
                    "Non-normal writer source must declare frozen gate evidence: ${path}")
            }
        }

        if ((Test-IsWriterCategory $category) -and $findingReachability -eq "normal-tas" -and !(Test-IsDirectGameplayWriterCategory $category) -and $normalTasAllowedPolicies -notcontains $normalTasPolicy)
        {
            $policyFailures.Add(
                "Normal TAS writer lacks an approved policy: ${key}")
        }
    }
}

foreach ($absenceGuard in @(Get-OptionalProperty $allowlist "absenceGuards" @()))
{
    $guardRoots = @(
        Get-OptionalProperty $absenceGuard "scanRoots" $requiredScanRoots
        | ForEach-Object { [string]$_ }
    )
    $expression = [string](Get-OptionalProperty $absenceGuard "expression" "")
    $expectedCount = [int](Get-OptionalProperty $absenceGuard "count" -1)
    $actualCount = 0
    foreach ($root in $guardRoots)
    {
        $rootFull = Resolve-RepositoryPath $root
        foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Filter "*.cs")
        {
            $actualCount += Count-Literal `
                ([System.IO.File]::ReadAllText($file.FullName)) $expression
        }
    }
    if ($actualCount -ne $expectedCount)
    {
        $gateFailures.Add(
            [pscustomobject][ordered]@{
                path = ($guardRoots -join ",")
                expression = $expression
                expectedCount = $expectedCount
                actualCount = $actualCount
                kind = "absence-guard"
            }) | Out-Null
    }
}

$unexpectedFindings = New-Object System.Collections.Generic.List[object]
$missingFindings = New-Object System.Collections.Generic.List[object]
$countMismatches = New-Object System.Collections.Generic.List[object]
foreach ($key in $actualByKey.Keys)
{
    if (!$expectedByKey.ContainsKey($key))
    {
        $unexpectedFindings.Add($actualByKey[$key]) | Out-Null
        continue
    }
    $actual = $actualByKey[$key]
    $expected = $expectedByKey[$key]
    if ($actual.count -ne $expected.count)
    {
        $countMismatches.Add(
            [pscustomobject][ordered]@{
                path = $actual.path
                category = $actual.category
                expression = $actual.expression
                expectedCount = $expected.count
                actualCount = $actual.count
                lines = $actual.lines
            }) | Out-Null
    }
}
foreach ($key in $expectedByKey.Keys)
{
    if (!$actualByKey.ContainsKey($key))
    {
        $missingFindings.Add($expectedByKey[$key]) | Out-Null
    }
}

$normalTasGameplayWriterCount = 0
$normalTasAllowedWriterCount = 0
foreach ($expected in $resolvedExpected)
{
    if ($expected.reachability -ne "normal-tas")
    {
        continue
    }
    if (Test-IsDirectGameplayWriterCategory $expected.category)
    {
        $normalTasGameplayWriterCount += $expected.count
    }
    elseif (Test-IsWriterCategory $expected.category)
    {
        $normalTasAllowedWriterCount += $expected.count
    }
}
if ($normalTasGameplayWriterCount -ne 0)
{
    $policyFailures.Add(
        "normalTasGameplayWriterCount must be zero, actual=$normalTasGameplayWriterCount")
}

$failureReasons = New-Object System.Collections.Generic.List[string]
if ($validationFailures.Count -gt 0) { $failureReasons.Add("allowlist-validation") }
if ($duplicateAllowlistEntries.Count -gt 0) { $failureReasons.Add("duplicate-allowlist-entry") }
if ($unexpectedFindings.Count -gt 0) { $failureReasons.Add("unexpected-finding") }
if ($missingFindings.Count -gt 0) { $failureReasons.Add("missing-finding") }
if ($countMismatches.Count -gt 0) { $failureReasons.Add("finding-count-mismatch") }
if ($hashMismatches.Count -gt 0) { $failureReasons.Add("source-hash-mismatch") }
if ($gateFailures.Count -gt 0) { $failureReasons.Add("gate-evidence-failure") }
if ($policyFailures.Count -gt 0) { $failureReasons.Add("policy-failure") }
$verdict = if ($failureReasons.Count -eq 0) { "PASS" } else { "FAIL" }
$allowlistFindingCount = 0
foreach ($expected in $resolvedExpected)
{
    $allowlistFindingCount += [int]$expected.count
}

$report = [ordered]@{
    schemaVersion = 1
    auditId = "t24-runtime-hook-mutation-static-audit-v1"
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    verdict = $verdict
    failureReasons = $failureReasons.ToArray()
    repositoryRoot = $repositoryRootFull
    allowlistPath = $AllowlistPath
    allowlistSha256 = $allowlistSha256
    scanRoots = $requiredScanRoots
    coverageLimitations = @(
        "Lexical C# scan; not a proof of call reachability or absence of mutation.",
        "Reflection-field detection recognizes named *Field.SetValue calls only; other reflection and aliases require manual review.",
        "Native ClockStartup/injector and third-party binary behavior require separate review."
    )
    scannedFileCount = $sourceFiles.Count
    observedFindingCount = $findings.Count
    observedGroupCount = $actualGroups.Count
    allowlistFindingCount = $allowlistFindingCount
    normalTasGameplayWriterCount = $normalTasGameplayWriterCount
    normalTasAllowedWriterCount = $normalTasAllowedWriterCount
    validationFailures = $validationFailures.ToArray()
    policyFailures = $policyFailures.ToArray()
    duplicateAllowlistEntries = $duplicateAllowlistEntries.ToArray()
    unexpectedFindings = @($unexpectedFindings.ToArray() | Sort-Object path, category, expression)
    missingFindings = @($missingFindings.ToArray() | Sort-Object path, category, expression)
    countMismatches = @($countMismatches.ToArray() | Sort-Object path, category, expression)
    hashMismatches = @($hashMismatches.ToArray() | Sort-Object path)
    gateFailures = @($gateFailures.ToArray() | Sort-Object path, expression)
    resolvedAllowlist = @($resolvedExpected.ToArray() | Sort-Object path, category, expression)
    observedGroups = $actualGroups
    observedFindings = @($findings.ToArray() | Sort-Object path, line, category, expression)
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$reportPath = Join-Path $OutputDirectory "static-audit.json"
$reportJson = $report | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText(
    $reportPath,
    $reportJson + "`n",
    (New-Object System.Text.UTF8Encoding($false)))

$summary = "T24 static mutation audit verdict=" + $verdict `
    + " findings=" + $findings.Count `
    + " groups=" + $actualGroups.Count `
    + " normalTasGameplayWriters=" + $normalTasGameplayWriterCount
Write-Host $summary
Write-Host ("Report: " + $reportPath)

if ($verdict -ne "PASS" -and !$NoThrowOnFailure)
{
    throw "T24 static mutation audit failed. See $reportPath"
}

$report
