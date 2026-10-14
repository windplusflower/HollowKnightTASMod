$ErrorActionPreference = 'Stop'
if (-not ('HollowKnightTAS.Diagnostics.ActualDMemoryRelay' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../StudioScenarioHarness/ActualDMemoryRelay.cs')
}
$taskPacket = [HollowKnightTAS.Diagnostics.ActualDRelayPacket]::new()
$taskPacket.Data = [byte[]]::new(32*1024*1024)
$taskPacket.Data[0] = 42; $taskPacket.Data[-1] = 17
$taskPacket.MovieFrame = 1421; $taskPacket.NativeFrame = 2696
$taskPacket.Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskPacket.Data)).ToLowerInvariant()
foreach ($taskReceiveAtServer in @($true, $false)) {
    $taskName = 'HKTAS-ActualD-' + [Guid]::NewGuid().ToString('N')
    $taskServer = [HollowKnightTAS.Diagnostics.ActualDMemoryRelay]::CreateServer($taskName)
    try {
        $taskServerWork = [HollowKnightTAS.Diagnostics.ActualDMemoryRelay]::ExchangeServer($taskServer, $taskReceiveAtServer, $taskPacket, 15000, $null)
        $taskClientWork = [HollowKnightTAS.Diagnostics.ActualDMemoryRelay]::ExchangeClient($taskName, -not $taskReceiveAtServer, $taskPacket)
        $taskClientResult = $taskClientWork.GetAwaiter().GetResult()
        $taskServerResult = $taskServerWork.GetAwaiter().GetResult()
        $taskReceived = if ($taskReceiveAtServer) { $taskServerResult } else { $taskClientResult }
        if ($taskReceived.Data.Length -ne $taskPacket.Data.Length -or $taskReceived.Data[0] -ne 42 -or
            $taskReceived.Data[-1] -ne 17 -or $taskReceived.Sha256 -ne $taskPacket.Sha256) { throw 'Full-bound relay roundtrip differs.' }
    }
    finally { $taskServer.Dispose() }
}
$taskBad = [HollowKnightTAS.Diagnostics.ActualDRelayPacket]::new()
$taskBad.Data = [byte[]](1); $taskBad.Sha256 = '0'*64
try { [HollowKnightTAS.Diagnostics.ActualDMemoryRelay]::Validate($taskBad); throw 'Bad packet digest accepted.' }
catch { if ($_.Exception.Message -like '*Bad packet digest accepted*') { throw } }
$taskBad.Data = [byte[]]::new(32*1024*1024+1)
try { [HollowKnightTAS.Diagnostics.ActualDMemoryRelay]::Validate($taskBad); throw 'Oversized packet accepted.' }
catch { if ($_.Exception.Message -like '*Oversized packet accepted*') { throw } }
try { [HollowKnightTAS.Diagnostics.ActualDMemoryRelay]::CreateServer('shared-name'); throw 'Non-random relay name accepted.' }
catch { if ($_.Exception.Message -like '*Non-random relay name accepted*') { throw } }
'Actual D relay: both 32 MiB directions, corruption, allocation bound and single-use name checks passed.'
