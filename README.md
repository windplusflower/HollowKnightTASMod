# HollowKnightTASMod

[English](README.en.md)

《空洞骑士》的 TAS 工具，包含游戏内 Runtime、Windows Studio 输入编辑器，以及供 AI 和脚本使用的本地控制接口。

Studio 支持从启动起点录制和回放、逐帧输入编辑、时间线分支与同进程 Lean S0 热回档、绑定初始存档的序列包（`.hktaspack`），以及 MP4 导出。普通启动游戏时保持原版游玩模式；从标题菜单打开 Studio 后进入 TAS。

完整发布包自带 FFmpeg 视频编码器，导出 MP4 时只需选择输出文件。

## 仓库目录总览

| 目录 | 用途 |
| --- | --- |
| `src/` | 核心源码（12 个 .NET 项目） |
| `native/` | 原生帧时钟、启动门闩与存档保护 C 库（`HollowKnightTAS.ClockBridge`） |
| `tests/` | 单元测试、协议与 WPF 界面测试（`Core.Tests` / `Companion.Tests` / `AgentBridge.Tests` + `native/` 离线 C 测试） |
| `scripts/` | 顶层构建/打包/启动脚本，`verify/` 预检与 Lean S0 验收，`harnesses/` 测试宿主，`t24/` 逐帧等价验证脚本 |
| `docs/` | 玩家手册、AI 控制指南、构建指南与辅助 Mod 兼容清单 |
| `fixtures/` | 单元测试使用的最小可复用输入与验证夹具 |
| `packaging/` | 配套包原生构建白名单（`native-build-whitelist-v1.json`）；构建产物生成至被忽略的 `publish/` 与 `staging/` |
| `third_party/` | 第三方许可文件（内嵌至 `Core`） |

### 核心项目（`src/`）

| 项目 | 职责 |
| --- | --- |
| `HollowKnightTAS.Core` | 领域模型、Movie v1/v2、时间线与场景检查点、IPC/自动化协议与签名校验 |
| `HollowKnightTAS.Runtime` | 游戏内 Runtime Mod；`FullRun/` 下按 `Session/`、`LeanS0/`、`Checkpoints/`、`Canonical/`、`Loading/` 分层 |
| `HollowKnightTAS.Companion` | Windows WPF Studio 编辑器、时间线树、世界信息叠加层与 Boss 状态机视图 |
| `HollowKnightTAS.AgentBridge` | 对外本地 MCP Server（对齐 FullRun v2 与 Studio 控制流） |
| `HollowKnightTAS.Cli` | 命令行控制与 Movie 校验/格式化工具 |
| `HollowKnightTAS.Automation.Client` | 外部自动化管道客户端 SDK |
| `HollowKnightTAS.ClockInjector` / `ClockPayload` | 启动挂起注入与原生帧门闩/RNG 协同层 |
| `HollowKnightTAS.NativeHost` / `BundleTool` | 原生能力宿主进程与配套包签名工具 |
| `HollowKnightTAS.GameObservation` / `ReferenceObserver` | 非视觉观察契约与参考观测 Mod |

## 常用构建与离线检查

```powershell
# 1. 仅编译而不覆盖游戏安装目录
dotnet build HollowKnightTAS.sln -c Release -p:SkipHKTASInstall=true

# 2. 运行全部离线测试套件
dotnet test tests/HollowKnightTAS.Core.Tests/HollowKnightTAS.Core.Tests.csproj -c Release -p:SkipHKTASInstall=true --no-build
dotnet test tests/HollowKnightTAS.Companion.Tests/HollowKnightTAS.Companion.Tests.csproj -c Release -p:SkipHKTASInstall=true --no-build
dotnet test tests/HollowKnightTAS.AgentBridge.Tests/HollowKnightTAS.AgentBridge.Tests.csproj -c Release -p:SkipHKTASInstall=true --no-build
```

## 文档导航

- [玩家安装与使用](docs/USER-MANUAL.md) · [English](docs/USER-MANUAL.en.md)
- [AI 控制指南](docs/AI-CONTROL.md) · [English](docs/AI-CONTROL.en.md)
- [项目构建指南](docs/BUILD.md) · [English](docs/BUILD.en.md)
- [TAS 辅助 Mod 兼容清单](docs/HELPER-MOD-COMPATIBILITY.md)

## 许可证

本项目基于 [GPL-3.0](LICENSE) 许可发布，附加署名与来源声明条款见 [NOTICE](NOTICE)。
