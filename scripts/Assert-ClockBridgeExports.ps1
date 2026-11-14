function Assert-ClockBridgeExports {
    param([Parameter(Mandatory=$true)][string]$Path, [switch]$RequireContactImpulsePolicy, [switch]$RequireResidentBoundary)
    $bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Path))
    if ($bytes.Length -lt 256 -or $bytes.Length -gt 32MB -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5a4d) { throw 'Clock bridge PE size or signature differs.' }
    function Read-U32([int]$Offset) {
        if ($Offset -lt 0 -or $Offset -gt $bytes.Length - 4) { throw 'Clock bridge PE field is out of bounds.' }
        [BitConverter]::ToUInt32($bytes,$Offset)
    }
    $pe = [int](Read-U32 0x3c)
    if ($pe -lt 64 -or $pe -gt $bytes.Length - 144 -or (Read-U32 $pe) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664) { throw 'Clock bridge is not the supported x64 PE.' }
    $sectionCount = [BitConverter]::ToUInt16($bytes,$pe+6)
    $optionalSize = [BitConverter]::ToUInt16($bytes,$pe+20)
    $optional = $pe+24
    if ($sectionCount -lt 1 -or $sectionCount -gt 96 -or $optionalSize -lt 120 -or
        [BitConverter]::ToUInt16($bytes,$optional) -ne 0x20b -or (Read-U32 ($optional+108)) -lt 1) { throw 'Clock bridge optional header differs.' }
    $sections = $optional+$optionalSize
    if ($sections -gt $bytes.Length - 40*$sectionCount) { throw 'Clock bridge section table is out of bounds.' }
    function File-Offset([uint32]$Rva,[uint32]$Length) {
        for ($index=0;$index -lt $sectionCount;$index++) {
            $at=$sections+40*$index; $start=Read-U32 ($at+12); $size=Read-U32 ($at+16); $raw=Read-U32 ($at+20)
            if ([uint64]$Rva -ge $start -and [uint64]$Rva+$Length -le [uint64]$start+$size) {
                $result=[uint64]$raw+$Rva-$start
                if ($result+$Length -gt $bytes.Length) { throw 'Clock bridge mapped PE range is truncated.' }
                return [int]$result
            }
        }
        throw 'Clock bridge export RVA has no bounded section mapping.'
    }
    $export=File-Offset (Read-U32 ($optional+112)) 40
    $count=Read-U32 ($export+24)
    if ($count -lt 1 -or $count -gt 4096) { throw 'Clock bridge export count exceeds its bound.' }
    $table=File-Offset (Read-U32 ($export+32)) (4*$count)
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index=0;$index -lt $count;$index++) {
        $rva=Read-U32 ($table+4*$index); $text=[Text.StringBuilder]::new()
        for ($character=0;$character -lt 512;$character++) {
            $at=File-Offset ($rva+$character) 1; $value=$bytes[$at]
            if ($value -eq 0) { break }
            if ($value -lt 32 -or $value -gt 126) { throw 'Clock bridge export name is not bounded ASCII.' }
            [void]$text.Append([char]$value)
        }
        if ($character -eq 512 -or $text.Length -eq 0 -or -not $names.Add($text.ToString())) { throw 'Clock bridge export name is invalid or duplicated.' }
    }
    foreach ($name in @('HktasClockBridge_GetContinuationBytes','HktasClockBridge_ExportContinuation','HktasClockBridge_ImportContinuation')) {
        if (-not $names.Contains($name)) { throw ('Clock bridge continuation export is missing before installation: '+$name) }
    }
    if ($RequireContactImpulsePolicy -and -not $names.Contains('HktasClockBridge_GetContactImpulsePolicy')) {
        throw 'Clock bridge canonical contact impulse policy export is missing before installation.'
    }
    if ($RequireResidentBoundary) {
        foreach ($entry in 'GetResidentBoundaryCapability','BeginResidentBoundary','BeginResidentSourceBoundary','RequireResidentBoundary',
            'RequireResidentWritableBoundary','ExportResidentContinuation','ImportResidentContinuation','EndResidentBoundary',
            'BeginResidentFrameObservation','RequireResidentFrameObservation','GetResidentFrameClockBytes','ExportResidentFrameClock',
            'CopyResidentAudioFenceStatus','CopyResidentAudioWorkers','CopyResidentParticleFenceStatus',
            'CopyResidentClipCurveLedgerStatus','QueryResidentClipCurveEligibility',
            'QueryResidentAudioVoiceControl',
            'QueryResidentAudioVoiceControlV2',
            'BeginResidentParticleFence','RequireResidentParticleFence','EndResidentParticleFence',
            'CopyResidentInputFenceStatus','CopyResidentDirectorFenceStatus','BeginResidentDirectorFence','RequireResidentDirectorFence',
            'VerifyResidentDirectorQueues','EndResidentDirectorFence','BeginResidentDirectorRestoreMutation',
            'EndResidentDirectorRestoreMutation','GetDepthImageRenderEvent',
            'BeginDepthImage','ReadDepthImage','ReleaseDepthImage') {
            if (-not $names.Contains('HktasClockBridge_' + $entry)) { throw "Resident bridge export missing before installation: $entry" }
        }
    }
}
