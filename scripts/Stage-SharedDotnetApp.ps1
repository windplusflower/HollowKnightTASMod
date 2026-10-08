#requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$SharedDirectory,
    [Parameter(Mandatory)][string]$EntrypointDirectory,
    [Parameter(Mandatory)][string]$ApplicationName,
    [string]$Configuration = 'Release',
    [string[]]$ExcludedFiles = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Keep self-contained runtimeconfig/deps files, but store identical runtime files
# only once. Reject mismatched dependencies instead of silently overwriting them.
$shared = [IO.Path]::GetFullPath($SharedDirectory)
$entrypoints = [IO.Path]::GetFullPath($EntrypointDirectory)
[IO.Directory]::CreateDirectory($entrypoints) | Out-Null
$studioConfig = Get-Content (Join-Path $shared 'HollowKnightTAS.Companion.runtimeconfig.json') -Raw | ConvertFrom-Json
$appConfig = Get-Content (Join-Path $PublishDirectory "$ApplicationName.runtimeconfig.json") -Raw | ConvertFrom-Json
if ($appConfig.runtimeOptions.PSObject.Properties.Name -notcontains 'includedFrameworks') {
    throw "Shared application must be published self-contained: $ApplicationName. Rebuild its publish output."
}
$coreVersion = ($appConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$studioCore = ($studioConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$desktopVersion = ($studioConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
if (!$coreVersion -or $coreVersion -ne $studioCore -or $coreVersion -ne $desktopVersion) {
    throw 'All shared applications must use the same .NET/Desktop runtime version.'
}
# WindowsDesktop supplies these implementations/forwarders in place of the
# NETCore runtime's stubs. Preserve Studio's versions, as desktop publishing does.
$desktopOverrides = @('Microsoft.VisualBasic.dll', 'System.Drawing.dll', 'WindowsBase.dll')
foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($PublishDirectory), $file.FullName)
    if ($relative -cin $ExcludedFiles) { continue }
    if ($relative -eq "$ApplicationName.exe") { continue }
    $target = Join-Path $shared $relative
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            if ($relative -cin $desktopOverrides) { continue }
            throw "Shared dependency differs: $relative"
        }
    } else {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

# Use the SDK's own apphost writer and the project's resolved .NET 8 template.
# The binding is relative to the executable, independent of cwd and DOTNET_ROOT.
$resolved = & dotnet msbuild $Project -t:ResolveFrameworkReferences "-p:Configuration=$Configuration" `
    -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -getProperty:AppHostSourcePath,NetCoreRoot
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve apphost template.' }
$properties = ($resolved -join "`n" | ConvertFrom-Json).Properties
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve SDK version.' }
$hostModel = Join-Path $properties.NetCoreRoot "sdk/$sdk/Microsoft.NET.HostModel.dll"
[Reflection.Assembly]::LoadFrom($hostModel) | Out-Null
$writer = [Microsoft.NET.HostModel.AppHost.HostWriter].GetMethods() |
    Where-Object Name -eq 'CreateAppHost' | Select-Object -First 1
$parameters = $writer.GetParameters()
$arguments = [object[]]::new($parameters.Length)
for ($i = 0; $i -lt $arguments.Length; $i++) { $arguments[$i] = $parameters[$i].DefaultValue }
$arguments[0] = [string]$properties.AppHostSourcePath
$arguments[1] = [string](Join-Path $entrypoints "$ApplicationName.exe")
$arguments[2] = [IO.Path]::GetRelativePath($entrypoints, (Join-Path $shared "$ApplicationName.dll"))
$arguments[3] = $false
$arguments[4] = [string](Join-Path $shared "$ApplicationName.dll")
$writer.Invoke($null, $arguments) | Out-Null
