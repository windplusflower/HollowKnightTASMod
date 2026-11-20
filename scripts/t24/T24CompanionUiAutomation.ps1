Set-StrictMode -Version Latest

function Initialize-T24CompanionUiAutomation {
    if ($null -ne ('System.Windows.Automation.AutomationElement' -as [type])) {
        return
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
}

function Get-T24UiElement {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Subtree,
        $condition)
}

function Wait-T24UiElement {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20,
        [switch]$RequireEnabled
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $element = Get-T24UiElement `
                -Root $Root `
                -AutomationId $AutomationId
            if ($null -ne $element `
                    -and (-not $RequireEnabled `
                        -or [bool]$element.Current.IsEnabled)) {
                return $element
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 20
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw (
        "UI Automation element was not ready: $AutomationId; " `
        + "requireEnabled=$RequireEnabled")
}

function Wait-T24CompanionWindow {
    param(
        [Parameter(Mandatory)][int[]]$OwnedProcessIds,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 60
    )

    Initialize-T24CompanionUiAutomation
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $processes = @(
            Get-Process `
                -Name HollowKnightTAS.Companion `
                -ErrorAction SilentlyContinue |
                Where-Object { $OwnedProcessIds -contains $_.Id }
        )
        foreach ($process in $processes) {
            try {
                $process.Refresh()
                if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
                    continue
                }
                $root = [System.Windows.Automation.AutomationElement]::FromHandle(
                    $process.MainWindowHandle)
                if ($null -ne $root `
                        -and [string]$root.Current.AutomationId `
                            -ceq 'HktasStudio.MainWindow') {
                    return [pscustomobject]@{
                        ProcessId = $process.Id
                        Root = $root
                    }
                }
            }
            catch [System.Windows.Automation.ElementNotAvailableException] {
            }
            catch [InvalidOperationException] {
            }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw 'The owned HollowKnightTAS Studio UI Automation window was not found.'
}

function Get-T24UiText {
    param([Parameter(Mandatory)]$Element)

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        return [string]$pattern.Current.Value
    }
    return [string]$Element.Current.Name
}

function Wait-T24UiText {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId,
        [Parameter(Mandatory)][string]$Pattern,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = ''
    do {
        try {
            $element = Wait-T24UiElement `
                -Root $Root `
                -AutomationId $AutomationId `
                -TimeoutSeconds 1
            $last = Get-T24UiText -Element $element
            if ($last -match $Pattern) {
                return $last
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        catch [System.Management.Automation.RuntimeException] {
            if ($_.Exception.Message -notmatch 'was not ready') {
                throw
            }
        }
        Start-Sleep -Milliseconds 20
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw (
        "UI Automation text did not match: id=$AutomationId; " `
        + "pattern=$Pattern; last=$last")
}

function Select-T24UiTab {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId
    )

    $element = Wait-T24UiElement `
        -Root $Root `
        -AutomationId $AutomationId `
        -RequireEnabled
    $pattern = $null
    if (-not $element.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        throw "UI Automation tab lacks SelectionItemPattern: $AutomationId"
    }
    $pattern.Select()
}

function Set-T24UiValue {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId,
        [AllowEmptyString()][Parameter(Mandatory)][string]$Value
    )

    $element = Wait-T24UiElement `
        -Root $Root `
        -AutomationId $AutomationId `
        -RequireEnabled
    $pattern = $null
    if (-not $element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        throw "UI Automation element lacks ValuePattern: $AutomationId"
    }
    if ([bool]$pattern.Current.IsReadOnly) {
        throw "UI Automation element is read-only: $AutomationId"
    }
    $pattern.SetValue($Value)
}

function Invoke-T24UiButton {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
    )

    $element = Wait-T24UiElement `
        -Root $Root `
        -AutomationId $AutomationId `
        -TimeoutSeconds $TimeoutSeconds `
        -RequireEnabled
    $pattern = $null
    if (-not $element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        throw "UI Automation element lacks InvokePattern: $AutomationId"
    }
    $pattern.Invoke()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 10
        try {
            $element = Get-T24UiElement `
                -Root $Root `
                -AutomationId $AutomationId
            if ($null -ne $element -and [bool]$element.Current.IsEnabled) {
                return
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "UI Automation command did not complete: $AutomationId"
}

function Invoke-T24ManualUiPause {
    param(
        [Parameter(Mandatory)]$Root,
        [switch]$DeferredRecordingArm,
        [Parameter(Mandatory)][scriptblock]$GetState,
        [Parameter(Mandatory)][scriptblock]$WaitState
    )

    $connection = Wait-T24UiText `
        -Root $Root `
        -AutomationId 'HktasStudio.ConnectionBadge' `
        -Pattern '^CONNECTED' `
        -TimeoutSeconds 60
    $warmStatus = Wait-T24UiText `
        -Root $Root `
        -AutomationId 'HktasStudio.RuntimeReadinessBadge' `
        -Pattern '^READY$' `
        -TimeoutSeconds 60
    $running = & $GetState
    if ([string]$running.State.RuntimeMode -cne 'Running') {
        throw (
            'Manual UI candidate must begin in Running mode; actual=' `
            + [string]$running.State.RuntimeMode)
    }

    Select-T24UiTab `
        -Root $Root `
        -AutomationId 'HktasStudio.ControlStateTab'
    Invoke-T24UiButton `
        -Root $Root `
        -AutomationId 'HktasStudio.ControlPauseButton'
    $pauseStatus = Wait-T24UiText `
        -Root $Root `
        -AutomationId 'HktasStudio.StatusText' `
        -Pattern '^pause:'
    $paused = if ($DeferredRecordingArm) {
        & $GetState
    }
    else {
        & $WaitState 'Paused' $null
    }
    if ($DeferredRecordingArm `
            -and ([string]$paused.State.RuntimeMode -cne 'Running' `
                -or [string]$paused.State.Fields[
                    'deferredRecordingArmPauseArmed'] -cne 'true' `
                -or [string]$paused.State.Fields[
                    'deferredRecordingArmReleaseConsumed'] -cne 'false')) {
        throw 'Manual UI pause did not arm the deferred recording boundary.'
    }

    return [ordered]@{
        schemaVersion = 1
        verdict = 'PASS'
        interaction = 'semantic-windows-ui-automation'
        visualRecognitionUsed = $false
        aiWriteCommandsUsed = $false
        uiClientId = 'companion-ui'
        connection = $connection
        warmStatus = $warmStatus
        pauseStatus = $pauseStatus
        initialMovieTick = [long]$paused.State.MovieTick
        deferredRecordingArm = [bool]$DeferredRecordingArm
        runtimeModeAfterPause = [string]$paused.State.RuntimeMode
    }
}

function Invoke-T24ManualUiControl {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][Collections.IDictionary]$PauseEvidence,
        [Parameter(Mandatory)][string]$MovieText,
        [ValidateRange(1, 10000)][int]$ExpectedTicks,
        [Parameter(Mandatory)][scriptblock]$GetState,
        [Parameter(Mandatory)][scriptblock]$WaitState,
        [Parameter(Mandatory)][scriptblock]$ArmExternalRng,
        [Parameter(Mandatory)][scriptblock]$ReleaseRecordingArm
    )

    if ([string]$PauseEvidence['verdict'] -cne 'PASS' `
            -or [bool]$PauseEvidence['aiWriteCommandsUsed']) {
        throw 'Manual UI pause evidence is not eligible.'
    }

    Select-T24UiTab `
        -Root $Root `
        -AutomationId 'HktasStudio.MovieEditorTab'
    Set-T24UiValue `
        -Root $Root `
        -AutomationId 'HktasStudio.MovieText' `
        -Value $MovieText
    Invoke-T24UiButton `
        -Root $Root `
        -AutomationId 'HktasStudio.MovieValidateButton'
    $validation = Wait-T24UiText `
        -Root $Root `
        -AutomationId 'HktasStudio.MovieValidationOutput' `
        -Pattern ('^VALID .+ expandedTicks=' + $ExpectedTicks + '$')
    Invoke-T24UiButton `
        -Root $Root `
        -AutomationId 'HktasStudio.MovieUploadButton' `
        -TimeoutSeconds 60
    $uploadStatus = Wait-T24UiText `
        -Root $Root `
        -AutomationId 'HktasStudio.StatusText' `
        -Pattern '^Canonical movie proposed and applied through the shared authoring service\.$' `
        -TimeoutSeconds 60

    & $ArmExternalRng
    Invoke-T24UiButton `
        -Root $Root `
        -AutomationId 'HktasStudio.MovieStartReplayButton'
    $startStatus = Wait-T24UiText `
        -Root $Root `
        -AutomationId 'HktasStudio.StatusText' `
        -Pattern '^startReplay:'
    $recordingArmEvidence = & $ReleaseRecordingArm
    $paused = & $WaitState 'Paused' $null

    Select-T24UiTab `
        -Root $Root `
        -AutomationId 'HktasStudio.ControlStateTab'
    $progress = $paused
    for ($index = 0; $index -lt $ExpectedTicks; $index++) {
        $expectedMovieTick = [long]$progress.State.MovieTick
        Invoke-T24UiButton `
            -Root $Root `
            -AutomationId 'HktasStudio.ControlStepButton'
        $progress = & $WaitState 'Paused' ($expectedMovieTick + 1)
        if ([long]$progress.State.MovieTick -ne $expectedMovieTick + 1) {
            throw (
                'Manual UI single-frame step was not exact: before=' `
                + $expectedMovieTick `
                + '; after=' `
                + [long]$progress.State.MovieTick)
        }
    }

    return [ordered]@{
        schemaVersion = 1
        verdict = 'PASS'
        interaction = 'semantic-windows-ui-automation'
        visualRecognitionUsed = $false
        aiWriteCommandsUsed = $false
        uiClientId = 'companion-ui'
        connection = [string]$PauseEvidence['connection']
        warmStatus = [string]$PauseEvidence['warmStatus']
        pauseStatus = [string]$PauseEvidence['pauseStatus']
        validation = $validation
        uploadStatus = $uploadStatus
        startReplayStatus = $startStatus
        recordingArmPolicyId =
            [string]$recordingArmEvidence.policyId
        initialMovieTick = [long]$PauseEvidence['initialMovieTick']
        finalMovieTick = [long]$progress.State.MovieTick
        exactStepCount = $ExpectedTicks
    }
}

function Get-T24HmacSha256 {
    param(
        [Parameter(Mandatory)][byte[]]$Key,
        [Parameter(Mandatory)][string]$Value
    )

    $hmac = [Security.Cryptography.HMACSHA256]::new($Key)
    try {
        return [Convert]::ToHexString(
            $hmac.ComputeHash(
                [Text.UTF8Encoding]::new($false, $true).GetBytes($Value))
        ).ToLowerInvariant()
    }
    finally {
        $hmac.Dispose()
    }
}

function Export-T24ManualUiAudit {
    param(
        [Parameter(Mandatory)][string]$BootstrapPath,
        [Parameter(Mandatory)][string]$AutomationRoot,
        [Parameter(Mandatory)][string]$OutputPath,
        [ValidateRange(1, 10000)][int]$ExpectedStepCount,
        [string]$ReadOnlyClientId = 't24-candidate-sdk'
    )

    $bootstrap = Get-Content -LiteralPath $BootstrapPath -Raw |
        ConvertFrom-Json -AsHashtable -Depth 10
    foreach ($key in @('sessionId', 'token')) {
        if (-not $bootstrap.ContainsKey($key) `
                -or [string]::IsNullOrWhiteSpace([string]$bootstrap[$key])) {
            throw "Automation bootstrap lacks $key."
        }
    }
    $token = [Convert]::FromBase64String([string]$bootstrap['token'])
    if ($token.Length -ne 32) {
        throw 'Automation bootstrap token length is invalid.'
    }
    $uiClientHash = Get-T24HmacSha256 `
        -Key $token `
        -Value 'companion-ui'
    $readOnlyClientHash = Get-T24HmacSha256 `
        -Key $token `
        -Value $ReadOnlyClientId
    $auditPath = Join-Path `
        $AutomationRoot `
        ("artifacts\{0}\audit\automation-v1.jsonl" -f `
            [string]$bootstrap['sessionId'])
    if (-not (Test-Path -LiteralPath $auditPath -PathType Leaf)) {
        throw "Automation audit is missing: $auditPath"
    }

    $all = @(
        foreach ($line in [IO.File]::ReadAllLines($auditPath)) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $line | ConvertFrom-Json
            }
        }
    )
    $ui = @(
        $all |
            Where-Object {
                [string]$_.clientIdHash -ceq $uiClientHash
            }
    )
    $nonUi = @(
        $all |
            Where-Object {
                [string]$_.clientIdHash -cne $uiClientHash
            }
    )
    foreach ($entry in $nonUi) {
        if ([string]$entry.clientIdHash -cne $readOnlyClientHash) {
            throw (
                'Manual UI audit contains an unexpected client hash: ' `
                + [string]$entry.clientIdHash)
        }
        if ([string]$entry.commandOrResource -cnotin @(
                'getCapabilities',
                'getState',
                'getStatus')) {
            throw (
                'Manual UI run used a non-UI write command: ' `
                + [string]$entry.commandOrResource)
        }
        if ([string]$entry.resultCode -cnotin @('Ok', 'Accepted')) {
            throw (
                'Manual UI read-only observer command failed: ' `
                + [string]$entry.resultCode)
        }
    }
    $expected = [Collections.Generic.List[string]]::new()
    $expectedResultCodes = [Collections.Generic.List[string]]::new()
    foreach ($pair in @(
            @('pause', 'Ok'),
            @('getMovie', 'Ok'),
            @('proposeMoviePatch', 'Valid'),
            @('applyMovieBranch', 'Ok'),
            @('startReplay', 'Ok'))) {
        $expected.Add([string]$pair[0])
        $expectedResultCodes.Add([string]$pair[1])
    }
    for ($index = 0; $index -lt $ExpectedStepCount; $index++) {
        $expected.Add('step')
        $expectedResultCodes.Add('Ok')
    }
    if ($ui.Count -ne $expected.Count) {
        throw (
            'Manual UI audit command count mismatch: expected=' `
            + $expected.Count `
            + '; actual=' `
            + $ui.Count)
    }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if ([string]$ui[$index].commandOrResource -cne $expected[$index]) {
            throw (
                'Manual UI audit command mismatch at index ' `
                + $index `
                + ': expected=' `
                + $expected[$index] `
                + '; actual=' `
                + [string]$ui[$index].commandOrResource)
        }
        if ([string]$ui[$index].resultCode `
                -cne $expectedResultCodes[$index]) {
            throw (
                'Manual UI audit result-code mismatch at index ' `
                + $index `
                + ': expected=' `
                + $expectedResultCodes[$index] `
                + '; actual=' `
                + [string]$ui[$index].resultCode)
        }
    }

    $writeCommands = @(
        $ui |
            Where-Object {
                [string]$_.commandOrResource -cne 'getMovie'
            }
    )
    $evidence = [ordered]@{
        schemaVersion = 1
        verdict = 'PASS'
        interaction = 'semantic-windows-ui-automation'
        visualRecognitionUsed = $false
        uiAutomationIdRoot = 'HktasStudio.MainWindow'
        clientId = 'companion-ui'
        clientIdHash = $uiClientHash
        readOnlyClientId = $ReadOnlyClientId
        readOnlyClientIdHash = $readOnlyClientHash
        sessionId = [string]$bootstrap['sessionId']
        commandCount = $ui.Count
        writeCommandCount = $writeCommands.Count
        externalWriteCommandCount = 0
        readOnlyExternalCommandCount = $nonUi.Count
        stepCommandCount = @(
            $ui |
                Where-Object {
                    [string]$_.commandOrResource -ceq 'step'
                }
        ).Count
        allResultsAccepted = $true
        commandSequenceSha256 = (
            [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData(
                    [Text.UTF8Encoding]::new($false, $true).GetBytes(
                        ($expected -join "`n"))))
        ).ToLowerInvariant()
        auditSourceSha256 = (Get-FileHash `
            -LiteralPath $auditPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        commands = @(
            $ui |
                ForEach-Object {
                    [ordered]@{
                        requestId = [string]$_.requestId
                        command = [string]$_.commandOrResource
                        scope = [string]$_.scope
                        requestedAtTick = [long]$_.requestedAtTick
                        acceptedAtTick = [long]$_.acceptedAtTick
                        completedAtTick = [long]$_.completedAtTick
                        resultCode = [string]$_.resultCode
                        responseSha256 = [string]$_.responseSha256
                    }
                }
        )
    }
    $evidence |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
    return $evidence
}
