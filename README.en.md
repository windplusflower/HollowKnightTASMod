# HollowKnightTASMod

[中文](README.md)

A tool-assisted speedrun toolkit for Hollow Knight, with an in-game Runtime, a Windows Studio input editor, and local control interfaces for AI clients and scripts.

Studio supports recording and replay from startup, frame-by-frame input editing, timeline branches with in-process Lean S0 fast restore, sequence packages bound to starting saves (`.hktaspack`), and MP4 export. Normal game launches retain ordinary play; open Studio from the title menu to enter TAS mode.

The complete release includes FFmpeg; exporting an MP4 only requires choosing an output file.

## Repository Layout

| Directory | Purpose |
| --- | --- |
| `src/` | Core source code (12 .NET projects) |
| `native/` | Native frame clock, startup gate, and save-write guard C library (`HollowKnightTAS.ClockBridge`) |
| `tests/` | Unit, protocol, and WPF UI test suites (`Core.Tests` / `Companion.Tests` / `AgentBridge.Tests` + `native/` C harnesses) |
| `scripts/` | Top-level build/package/launch scripts; `verify/` preflight & Lean S0 checks; `harnesses/` host apps; `t24/` equivalence scripts |
| `docs/` | Player manuals, AI control guides, build guides, and helper Mod compatibility list |
| `fixtures/` | Minimal reusable inputs and test fixtures |
| `packaging/` | Native build whitelist (`native-build-whitelist-v1.json`); build outputs go to git-ignored `publish/` and `staging/` |
| `third_party/` | Third-party license texts embedded into `Core` |

### Core Projects (`src/`)

| Project | Responsibility |
| --- | --- |
| `HollowKnightTAS.Core` | Domain models, Movie v1/v2, timeline & scene checkpoints, IPC/automation contracts, and bundle signing |
| `HollowKnightTAS.Runtime` | In-game Runtime Mod; `FullRun/` organized into `Session/`, `LeanS0/`, `Checkpoints/`, `Canonical/`, and `Loading/` |
| `HollowKnightTAS.Companion` | Windows WPF Studio editor, timeline tree, world info overlay, and Boss FSM viewer |
| `HollowKnightTAS.AgentBridge` | Local MCP Server aligned with FullRun v2 & Studio control flow |
| `HollowKnightTAS.Cli` | CLI controller and Movie validation/formatting commands |
| `HollowKnightTAS.Automation.Client` | External automation pipe client SDK |
| `HollowKnightTAS.ClockInjector` / `ClockPayload` | Suspended-launch injector and native frame-gate / RNG bridge |
| `HollowKnightTAS.NativeHost` / `BundleTool` | Native capability host and companion bundle signing CLI |
| `HollowKnightTAS.GameObservation` / `ReferenceObserver` | Non-visual observation contracts and reference observer Mod |

## Build & Offline Checks

```powershell
# 1. Build without overwriting your installed game Mod directory
dotnet build HollowKnightTAS.sln -c Release -p:SkipHKTASInstall=true

# 2. Run all offline test suites
dotnet test tests/HollowKnightTAS.Core.Tests/HollowKnightTAS.Core.Tests.csproj -c Release -p:SkipHKTASInstall=true --no-build
dotnet test tests/HollowKnightTAS.Companion.Tests/HollowKnightTAS.Companion.Tests.csproj -c Release -p:SkipHKTASInstall=true --no-build
dotnet test tests/HollowKnightTAS.AgentBridge.Tests/HollowKnightTAS.AgentBridge.Tests.csproj -c Release -p:SkipHKTASInstall=true --no-build
```

## Documentation

- [Installation and player guide](docs/USER-MANUAL.en.md) · [中文](docs/USER-MANUAL.md)
- [AI control guide](docs/AI-CONTROL.en.md) · [中文](docs/AI-CONTROL.md)
- [Build guide](docs/BUILD.en.md) · [中文](docs/BUILD.md)
- [Helper Mod compatibility](docs/HELPER-MOD-COMPATIBILITY.md)

## License

Licensed under [GPL-3.0](LICENSE) with Section 7 attribution and origin notice terms in [NOTICE](NOTICE).
