with open('scripts/Invoke-ActualDSuffixPair.ps1', 'r', encoding='utf-8') as f:
    text = f.read()

target = "Add-Type -Path (Join-Path  'StudioScenarioHarness/ActualDMemoryRelay.cs')"
replacement = "Add-Type -Path (Join-Path $PSScriptRoot 'StudioScenarioHarness/ActualDMemoryRelay.cs')"
if target not in text:
    raise Exception('target not found')
text = text.replace(target, replacement)
with open('scripts/Invoke-ActualDSuffixPair.ps1', 'w', encoding='utf-8') as f:
    f.write(text)
print('Fixed successfully')
