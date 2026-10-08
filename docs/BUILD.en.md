# Build guide

[中文](BUILD.md)

The project contains a .NET Framework game Runtime, .NET Studio/CLI/MCP/SDK tools, and a Windows C library for native frame control. Build on Windows x64.

## Prerequisites and layout

Install the .NET 8 SDK, .NET Framework 4.7.2 targeting assemblies, PowerShell 7.2 or newer, and x64 MinGW GCC. The game must have Modding API installed and provide the referenced assemblies, including `Assembly-CSharp.dll`, `MMHOOK_Assembly-CSharp.dll`, `MMHOOK_PlayMaker.dll`, Unity modules, PlayMaker, MonoMod and Mono.Cecil.

| Directory | Contents |
| --- | --- |
| `src` | Runtime, Core, Companion, CLI, AgentBridge, automation SDK and build tools |
| `native` | Native frame control, startup gate and save-write protection |
| `tests` | Unit, protocol and WPF interface tests |
| `fixtures` / `schemas` | Reusable test inputs and data constraints |
| `scripts` | Build, packaging and validation scripts |
| `packaging` | Companion build inputs and generated directories |

From the repository root, copy the local configuration template and set both paths:

```powershell
Copy-Item LocalBuildProperties.props.example LocalBuildProperties.props
```

```xml
<Project>
  <PropertyGroup>
    <HKManagedDir>D:\Games\Hollow Knight\hollow_knight_Data\Managed</HKManagedDir>
    <HKModsDir>D:\Games\Hollow Knight\hollow_knight_Data\Managed\Mods</HKModsDir>
  </PropertyGroup>
</Project>
```

`LocalBuildProperties.props`, signing keys, build output, temporary artifacts and development notes stay local and are excluded by `.gitignore`.

## Compile and test

Run all commands from the repository root. To compile without installing:

```powershell
dotnet restore HollowKnightTAS.sln
dotnet build HollowKnightTAS.sln -c Release -p:SkipHKTASInstall=true
```

Runtime's default Build copies and packages files into `HKModsDir/HollowKnightTAS`. For development checks, explicitly pass `SkipHKTASInstall=true` to avoid replacing an installation in use.

Run tests appropriate to the change, for example:

```powershell
dotnet test tests/HollowKnightTAS.Core.Tests/HollowKnightTAS.Core.Tests.csproj -c Release -p:SkipHKTASInstall=true

dotnet test tests/HollowKnightTAS.Companion.Tests/HollowKnightTAS.Companion.Tests.csproj -c Release -p:SkipHKTASInstall=true --filter FullyQualifiedName~StudioThemeTests
```

WPF tests require Windows and should run in a separate test process. Compilation and offline tests do not establish in-game replay results for a particular game, Mod and save combination. Validate replay using the same build and matching environment.

## Sign the companion bundle

The complete Companion bundle includes Studio, native tools, ClockStartup, CLI, MCP and SDK. Its manifest signs the file list and hashes. Runtime and Companion verify it with embedded public keys.

To maintain an existing release identity, use its matching private and public keys. For an independent release, generate your own RSA keys:

```powershell
dotnet run --project src/HollowKnightTAS.BundleTool -c Release -- keygen .local/signing/companion-private.json .local/signing/companion-public.json
dotnet run --project src/HollowKnightTAS.BundleTool -c Release -- print-public .local/signing/companion-public.json
```

Copy the printed modulus and exponent into `ModulusBase64` and `ExponentBase64` in both files before building:

- `src/HollowKnightTAS.Runtime/Companion/CompanionReleaseKey.cs`
- `src/HollowKnightTAS.Companion/Services/CompanionReleaseKey.cs`

Do not commit the private key or include it in a release. Generating a key without updating the embedded public keys causes bundle verification to fail at runtime. Key generation refuses to overwrite existing files.

## Build ClockStartup and the complete bundle

Specify the local game and GCC paths, then build ClockStartup into a directory that does not yet exist:

```powershell
./scripts/Build-T24ClockPrototype.ps1 -Configuration Release `
    -GameExecutable 'D:/Games/Hollow Knight/hollow_knight.exe' `
    -GccExecutable 'C:/Tools/mingw64/bin/gcc.exe' `
    -OutputRoot "$PWD/artifacts/clock-local"
```

This script is the native clock build entry used by the complete bundle build. Its output is bound to the specified game executable, Unity and game assemblies.

Build and verify a staging bundle without changing the game installation:

```powershell
./scripts/Build-CompanionBundle.ps1 -Configuration Release -StageOnly `
    -GameExecutable 'D:/Games/Hollow Knight/hollow_knight.exe' `
    -ClockBundleRoot "$PWD/artifacts/clock-local"
```

Keys default to `.local/signing`; override them with `-PrivateKeyPath` and `-PublicKeyPath`. The script regenerates `packaging/publish` and `packaging/staging/bundle`, publishes Studio as a self-contained `win-x64` application, signs the bundle and verifies it. `-StageOnly` does not build or install Runtime.

Save your edits and close the game and Studio. Omit `-StageOnly` to build and install the complete package:

```powershell
./scripts/Build-CompanionBundle.ps1 -Configuration Release `
    -GameExecutable 'D:/Games/Hollow Knight/hollow_knight.exe' `
    -ClockBundleRoot "$PWD/artifacts/clock-local"
```

`HKModsDir` in `LocalBuildProperties.props` determines the installation location. The script checks that the game and Studio are closed, replaces Companion, builds Runtime and verifies the installed signed bundle. It produces `HollowKnightTAS.zip` and `SHA256.txt`. The zip root contains the Mod files; extract it into `Mods/HollowKnightTAS`.

The build runs `scripts/Stage-BundledFfmpeg.ps1` to download the pinned FFmpeg 8.0.1 essentials build, verify its SHA-256 and cache it in `.local/ffmpeg`. The signed bundle includes `Tools/ffmpeg/ffmpeg.exe`, the upstream license and build information. The first build needs network access; players can export video offline with the complete release. Redistribution must provide the corresponding source and dependency build materials under the included GPL license.

## Package a release with documentation

`scripts/Package-StudioUpdate.ps1` packages an existing installation without recompiling. It includes five documents: Chinese and English player guides, Chinese and English AI control guides, and the helper Mod compatibility list. Obtain and verify the lowercase SHA-256 hashes of the target installation's Runtime, Core and manifest, then pass them to the script:

```powershell
./scripts/Package-StudioUpdate.ps1 `
    -ModDirectory 'D:/Games/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS' `
    -OutputPath "$PWD/artifacts/releases/HollowKnightTAS.zip" `
    -ExpectedRuntimeSha256 '<runtime-sha256>' `
    -ExpectedCoreSha256 '<core-sha256>' `
    -ExpectedManifestSha256 '<manifest-sha256>'
```

The script defaults to the Release BundleTool and local public key. Override these with `-BundleToolPath` and `-PublicKeyPath` when needed. The output path must not exist. Packaging verifies the installed signature and every file hash, then reads the resulting zip and verifies each entry. `-VerifyOnly` checks an existing archive against the current inputs.

Changing Runtime, Core, native clock code, the game or other Mods can change the environment identity. Distribute matching binaries together and retain the installation that matches a sequence. Recompilation alone does not establish compatibility with existing recordings.

Studio, ClockInjector, NativeHost, CLI and AgentBridge share the bundled .NET runtime in `Companion/win-x64`, retaining their existing EXE paths. `Stage-SharedDotnetApp.ps1` binds each helper EXE to its managed assembly in the shared directory and checks runtime versions and duplicate file hashes. Players do not need a system .NET installation.

ClockInjector retains its native components and six-file startup identity in `ClockStartup`; its managed DLL, deps and runtimeconfig also reside beside the shared runtime. The build refreshes the startup manifest hashes after rebinding the EXE, then signs the complete bundle. `-ClockBundleRoot` accepts only self-contained clock bundles built from the current injector source; rebuild older bundles with the current `Build-T24ClockPrototype.ps1`.

`python scripts/Test-SharedBundle.py <mod-root> <evidence-directory>` checks local runtime loading with an empty PATH, invalid DOTNET_ROOT and a separate working directory. Close the game and Studio before the full check. Add `--clock-only` to check only ClockInjector's local runtime and native component lookup without starting Studio or accessing any game session; this check can run while the game is active.

[Installation and player guide](USER-MANUAL.en.md) · [AI control guide](AI-CONTROL.en.md)
