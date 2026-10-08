# 项目构建指南

[English](BUILD.en.md)

项目包含 .NET Framework 游戏 Runtime、.NET Studio/CLI/MCP/SDK，以及用于原生帧控制的 Windows C 动态库。构建在 Windows x64 上进行。

## 环境与目录

准备 .NET 8 SDK、.NET Framework 4.7.2 目标程序集、PowerShell 7.2 或更高版本，以及 x64 MinGW GCC。游戏目录需已安装 Modding API，并包含 `Assembly-CSharp.dll`、`MMHOOK_Assembly-CSharp.dll`、`MMHOOK_PlayMaker.dll`、Unity 模块、PlayMaker、MonoMod 和 Mono.Cecil 等项目引用。

| 目录 | 内容 |
| --- | --- |
| `src` | Runtime、Core、Companion、CLI、AgentBridge、自动化 SDK 和构建工具 |
| `native` | 原生帧控制、启动门闩和存档写入保护 |
| `tests` | 单元测试、协议测试和 WPF 界面检查 |
| `fixtures` / `schemas` | 可复用测试输入和数据约束 |
| `scripts` | 构建、打包和验证脚本 |
| `packaging` | 配套包构建输入及生成目录 |

在仓库根目录复制本地配置模板，并修改两个路径：

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

`LocalBuildProperties.props`、签名密钥、编译输出、临时产物和开发记录保留在本地，由 `.gitignore` 排除。

## 编译与检查

所有命令从仓库根目录执行。仅编译而不安装：

```powershell
dotnet restore HollowKnightTAS.sln
dotnet build HollowKnightTAS.sln -c Release -p:SkipHKTASInstall=true
```

Runtime 的默认 Build 会复制文件并打包到 `HKModsDir/HollowKnightTAS`。开发检查始终显式传 `SkipHKTASInstall=true`，避免覆盖正在使用的安装。

按修改范围运行相应测试，例如：

```powershell
dotnet test tests/HollowKnightTAS.Core.Tests/HollowKnightTAS.Core.Tests.csproj -c Release -p:SkipHKTASInstall=true

dotnet test tests/HollowKnightTAS.Companion.Tests/HollowKnightTAS.Companion.Tests.csproj -c Release -p:SkipHKTASInstall=true --filter FullyQualifiedName~StudioThemeTests
```

WPF 测试需要 Windows，界面测试宜独立进程运行。编译和离线测试不证明具体游戏、Mod、存档组合的实机回放结果；回放验证使用同一构建和匹配环境。

## 配套包签名

完整 Companion 包包含 Studio、原生工具、ClockStartup、CLI、MCP 和 SDK。manifest 对文件清单和哈希签名，Runtime 和 Companion 使用内嵌公钥验证。

维护现有发行身份时，使用对应私钥和公钥。独立构建自己的发行包时，先生成自己的 RSA 密钥：

```powershell
dotnet run --project src/HollowKnightTAS.BundleTool -c Release -- keygen .local/signing/companion-private.json .local/signing/companion-public.json
dotnet run --project src/HollowKnightTAS.BundleTool -c Release -- print-public .local/signing/companion-public.json
```

将输出公钥的 modulus 和 exponent 同步到以下两个文件中的 `ModulusBase64` 与 `ExponentBase64`，再构建：

- `src/HollowKnightTAS.Runtime/Companion/CompanionReleaseKey.cs`
- `src/HollowKnightTAS.Companion/Services/CompanionReleaseKey.cs`

私钥不可提交或放入发布包。仅生成新密钥而不更新内嵌公钥，会导致运行时拒绝配套包。密钥生成命令拒绝覆盖已有文件。

## 构建原生时钟与完整配套包

显式指定本机游戏和 GCC 路径，先构建 ClockStartup 到一个尚不存在的目录：

```powershell
./scripts/Build-T24ClockPrototype.ps1 -Configuration Release `
    -GameExecutable 'D:/Games/Hollow Knight/hollow_knight.exe' `
    -GccExecutable 'C:/Tools/mingw64/bin/gcc.exe' `
    -OutputRoot "$PWD/artifacts/clock-local"
```

该脚本是完整配套构建使用的原生时钟构建入口。输出与指定游戏可执行文件、Unity 和游戏程序集绑定。

构建并验签暂存包，不修改游戏安装：

```powershell
./scripts/Build-CompanionBundle.ps1 -Configuration Release -StageOnly `
    -GameExecutable 'D:/Games/Hollow Knight/hollow_knight.exe' `
    -ClockBundleRoot "$PWD/artifacts/clock-local"
```

默认从 `.local/signing` 读取密钥，也可传 `-PrivateKeyPath` 和 `-PublicKeyPath`。脚本会重新生成 `packaging/publish` 与 `packaging/staging/bundle`，将 Studio 发布为自带运行时的 `win-x64` 包，然后签名并验证。`-StageOnly` 不构建和安装 Runtime。

保存编辑并关闭游戏与 Studio 后，移除 `-StageOnly` 执行完整构建安装：

```powershell
./scripts/Build-CompanionBundle.ps1 -Configuration Release `
    -GameExecutable 'D:/Games/Hollow Knight/hollow_knight.exe' `
    -ClockBundleRoot "$PWD/artifacts/clock-local"
```

安装位置由 `LocalBuildProperties.props` 中的 `HKModsDir` 决定。脚本检查游戏和 Studio 未运行后替换 Companion，构建 Runtime，并验证已安装的签名包。生成 `HollowKnightTAS.zip` 和 `SHA256.txt`；该 zip 的根目录是 Mod 内容，安装时解压到 `Mods/HollowKnightTAS`。

构建会通过 `scripts/Stage-BundledFfmpeg.ps1` 下载固定的 FFmpeg 8.0.1 essentials 构建并校验 SHA-256，缓存于 `.local/ffmpeg`。发布包包含 `Tools/ffmpeg/ffmpeg.exe`、上游许可及构建信息，并纳入签名清单。首次构建需要联网；发布给玩家的完整包可离线导出视频。再次分发时须按附带 GPL 许可提供对应源码和依赖的构建材料。

## 带文档的发布包

`scripts/Package-StudioUpdate.ps1` 从现有安装生成 zip，不重新编译。包内文档为中英文人工操作指南、中英文 AI 操作指南，以及辅助 Mod 兼容清单，共五份。先取得并核对目标安装的 Runtime、Core 和 manifest 的小写 SHA-256，再传入：

```powershell
./scripts/Package-StudioUpdate.ps1 `
    -ModDirectory 'D:/Games/Hollow Knight/hollow_knight_Data/Managed/Mods/HollowKnightTAS' `
    -OutputPath "$PWD/artifacts/releases/HollowKnightTAS.zip" `
    -ExpectedRuntimeSha256 '<runtime-sha256>' `
    -ExpectedCoreSha256 '<core-sha256>' `
    -ExpectedManifestSha256 '<manifest-sha256>'
```

默认使用 Release BundleTool 和本地公钥，必要时传 `-BundleToolPath`、`-PublicKeyPath`。输出路径必须不存在。打包时验证安装签名和每个文件的哈希，生成后读取 zip 再逐项核对。`-VerifyOnly` 检查已存在的目标包与当前输入是否一致。

修改 Runtime、Core、原生时钟、游戏或其他 Mod 可能改变环境身份。分发时使用成套二进制，保留与序列匹配的安装；重新编译后不应直接声称原有序列兼容。

Studio、ClockInjector、NativeHost、CLI 和 AgentBridge 共享 `Companion/win-x64` 下的内置 .NET 运行时，辅助 EXE 仍保留原路径。`Stage-SharedDotnetApp.ps1` 使用 SDK apphost writer 将辅助 EXE 绑定到上一级的托管 DLL；所有程序仍以自包含方式发布，不要求玩家安装 .NET。合并时检查运行时版本与重复文件哈希；WindowsDesktop 提供的 `Microsoft.VisualBasic.dll`、`System.Drawing.dll`、`WindowsBase.dll` 保留 Studio 版本。

ClockInjector 的原生组件和六文件启动身份保留在 `ClockStartup`，托管 DLL、deps 和 runtimeconfig 同时保留在共享运行时目录；最终 EXE 重绑定后更新启动清单哈希，再签名整个配套包。`-ClockBundleRoot` 只接受当前启动器源码生成的自包含包；旧包需用当前 `Build-T24ClockPrototype.ps1` 重新构建。

`python scripts/Test-SharedBundle.py <Mod目录> <证据目录>` 在空 PATH、无效 DOTNET_ROOT 和独立工作目录下检查程序启动及实际加载的运行时。运行前关闭游戏和 Studio。有效 CLI/MCP 会话、录制、冷回放及音视频导出由 `StudioScenarioHarness --fractional-fps --package-smoke` 实机检查。

加 `--clock-only` 可单独验证 ClockInjector 从包内加载运行时并读取正确的原生组件；不启动 Studio、不连接或启动游戏，可在游戏运行期间执行。

[玩家安装与使用](USER-MANUAL.md) · [AI 控制指南](AI-CONTROL.md)
