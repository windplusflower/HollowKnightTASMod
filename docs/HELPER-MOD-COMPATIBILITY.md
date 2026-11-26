# TAS 辅助 Mod 兼容清单

适用于当前 HollowKnightTAS；结论综合 2026-09-28 与 2026-11-26 的实机 S0 验收及反编译核查结果。修改 Mod 设置后请重启游戏。

## 不能装（已安装的禁用）

| Mod | 版本 | 说明 |
| --- | --- | --- |
| QoL | 4.9.0.0 | 与 TAS 帧时钟/流程机制冲突，必须禁用。 |

## 可直接使用的纯观察类辅助 Mod

| Mod | 版本 | 当前结论 |
| --- | --- | --- |
| Enemy HP Bar | 3.1.5.0 | 2026-11-25 实机神居 3 Boss 序列 S0 自动播放与热回档全通过；纯 UI 血条渲染，无新增关闭项。 |

## 需要关闭部分功能 / 注意使用边界

### Custom Knight 3.5.0.0
- 2026-11-25 实机 S0 验收通过。
- 关闭 `GenerateDefaultSkin`、`SwapperEnabled`、`EnableParticleSwap`、`EnablePauseMenu`、`EnableSaveHuds`。
- 开启 `DisableDirectorySwaps`。
- 录制前选好固定皮肤；TAS 会自动屏蔽换皮和重载快捷键，无需清空绑定。

### DebugMod 1.4.10.2
- 2026-11-25 挂载运行 S0 验收通过（未触发修改类功能）。
- **推荐常规配置**：
  - 关闭变速、暂停、逐帧、传送、读状态、无敌、穿墙、无限资源和伤害修改。
  - 清空热键：`binds={}`，并设置 `FirstRun=false`。
  - 关闭 `NumPadForSaveStates`、`TopMenuVisible`、`SaveStatePanelVisible`、`EnemiesPanelVisible`、`ConsoleVisible`。
  - 信息面板（`InfoPanel`）、碰撞箱（`Show Hitboxes`）属于只读展示，可以保留（建议提前在配置中开启，避免录制期手动点击面板）。
- **使用 Studio 自定义按键（`CustomKey`）触发 DebugMod 快捷键的边界**：
  - `FullRunKeyboardBridge` 会将序列中编排的 `CustomKey` 注入 `UnityEngine.Input.GetKey/GetKeyDown/GetKeyUp`，因此在**从头冷回放**中可触发快捷键；但涉及 **S0 热回档与分支切换** 时分三类：
    1. **仅改 `PlayerData` 的瞬时/数值功能**（加魂 `Add Soul`、修改骨钉伤害 `Nail Damage ±4`、无限跳 `Infinite Jump`）：状态在 S0 快照（`C1`）内，理论上支持冷回放与 S0 热回档。
    2. **依赖 DebugMod 自身静态开关的 Toggle 功能**（无敌 `Invincibility` / `Infinite HP`、无限魂 `Infinite Soul`、飞天遁地 `Noclip`）：静态字段不在 S0 快照内，热回档后开关会残留，播到原按键帧时甚至会被再次 Toggle 反转。
    3. **底层机制直接冲突功能**（`TimeScale` / 逐帧 / `SaveState` / 传送 / 梦门 / 重生 Boss）：直接冲突原生帧门闩、旁路 `LeanS0Gate` 或读写外部文件，禁止在 TAS 会话中使用。

### GodSeekerPlus 0.25.0.0
- **依赖要求**：必须与 `Osmi`、`Satchel` 一并启用（缺失依赖会导致启动报错）。
- **必须关闭（直接冲突 / 报错 / 静态状态跨 S0 残留）**：
  - `ColosseumOfFools`（实机日志已证实 `SetupColosseumEntrance` 抛出 `NullReferenceException`，且每 `FixedUpdate` 持续加魂）、`CreateLag`（主线程 `Thread.Sleep`）、`AggressiveGC`。
  - `ActivateFury`、`AddLifeblood`、`AddSoul`、`CarefreeMelodyReset`、`P5Health`、所有 `HalveDamage*`。
  - `InfiniteChallenge`、`SegmentedP5`（静态 `running` 字段跨 S0 回档与分支切换不同步）、`InfiniteGrimmPufferfish`、`InfiniteRadianceClimbing`、`restartFightOnSuccess`、`restartFightAndMusic`。
  - `NoDiveInvincibility`、`NoNailAttack`、`NoNailDamage`、`NoSpellDamage`。
- **仅限同配置 Mod 环境谨慎使用（会改变机制/菜单/流程，不可用于原版规则序列）**：
  - 动作/流程类：`FastDash`、`FastDreamWarp`、`FastSuperDash`、`ShortDeathAnimation`、`DoorDefaultBegin`（会修改神居门默认选中按钮，实测已确认会改变菜单输入与路由）、`CorrectRadianceHP`、`KeepCocoonLifeblood`、`ForceArriveAnimation`、`ForceGreyPrinceEnterType`、`UnlockRadiant`、`CompleteLowerDifficulty`、`EternalOrdealPlatform`、`UnlockPantheons`、`UnlockRadiance`、`UnlockEternalOrdeal`、`UnlockAllModes`（会写入全局 `EncryptedSharedData`）。
  - 其中 `P5Teleport`（含 `static bool teleporting` 与 2 秒协程）和 `MemorizeBindings`（静态 `LocalSetting` 不进 S0）在热回档或切分支时存在状态残留风险。
- **基本兼容（纯视听 + 确定性 Bugfix，录放两端需保持开关一致）**：
  - `Cosmetic`：`MuteCrowd`、`NoFuryEffect`、`NoLowHealthEffect`、`NoDefendersCrestEffect`、`UseOwnMusic`、`MorePantheonCaps`（本地顶盖记录不随分支回滚）。
  - `Bugfix`：`CameraKeepRumbling`、`CrossingOoma`、`GodTamerBeastRollCollider`、`GreyPrinceNailSlamCollider`、`HUDDisplayChecker`、`TransitionDeath`。

### Benchwarp 3.2.6.1
- 2026-11-26 实测更新：修复原生 `ClockBridge` 故障后，已实机验证触发 `DeployedBench` 部署椅子重生及 `GG_God_Tamer` 进出 2/2 S0 热回档全通过。
- 录制正式原版规则 TAS 时，仍建议关闭 `ShowMenu`、`EnableDeploy`、`DoorWarp`、`EnableHotkeys`、`LegacyHotkeys`、`UnlockAllBenches`、`ModifyVanillaBenchStyles`，并确保录制起点位于原版真实椅子存档。

### SpeedRunQoL 0.6.1.0
- 2026-11-25 实机 S0 验收通过。
- 按上面的 DebugMod 原则关闭修改类功能并清空全部热键。
- 不启用房间/位置/复制状态加载、斗兽场波次跳转、碰撞修改、梦门无敌、遮幕隐藏和视觉状态切换。

以上为已测片段与反编译核查结论，不代表全部场景均已实机覆盖。

## ModBoss 和共用依赖

以下结论综合 2026-09-28 与 2026-11-26 的实机进战与 S0 热回档测试结果。

| Mod | 版本 | 当前结论 |
| --- | --- | --- |
| TribeOfBattle | 1.2.0.0 | **通过（3/3）**。2026-11-26 `GG_Mantis_Lords_V` 真实进战 S0 自动播放与热回档全通过，切场景帧、RNG 与 `tftMs` 完全一致。 |
| Shade Lord | 1.0.0.1 | **通过（3/3）**。2026-11-26 `GG_Shade_Lord` 真实进战 S0 自动播放与热回档全通过，切场景帧、RNG 与 `tftMs` 完全一致。 |
| Pale Prince | 2.1.0.0 | **通过（3/3）**。2026-11-26 `GG_Hollow_Knight` 真实进战 S0 自动播放与热回档全通过，切场景帧、RNG 与 `tftMs` 完全一致。 |
| Pale Court | 1.1.1.7 | **基本兼容（已知第三方限制）**。2026-11-26 `GG_Brooding_Mawlek_V`（提索）实测中，Boss AI 决策与 `tftMs`（`0.0 ms`）已 100% 对齐；仅残余 1 次 RNG 计数差，根因为 `PaleCourt.dll` 自身跨场景未重置的静态字典 `FiveKnights.Tiso.TisoRandAudio._counter`。 |
| Inferno King Grimm + WeaverCore | 4.2.3 / 2.2.0.7 | **部分兼容（已知差异）**。2026-11-26 `GG_Grimm_Nightmare` 实测切场景帧与 `tftMs`（`0.0 ms`）一致，但进战加载帧热回档后存在 15 次 `SoulOrb_fill/Liquid` `iTween.GenerateID()` RNG 计数差（与 `WeaverCore` 替换 `BossSceneController` 加载时序相关），整场战斗热回档 RNG 严格一致待进一步闭环。 |
| 嫉妒马尔穆（本机版） | — | 原辐辉序列通过，无新增关闭项（2026-09-28）。 |
| HuKing | 2.1 | 预加载启动通过；新环境下 3800 帧严格冷重放两轮通过，不必关闭预加载，整场战斗 S0 热回档尚未验证。 |
| Corrupted Kin | 1.4 | 已处理共用的 WeaverCore 预加载和洗牌问题，完整战斗待验证。 |
| DoodleBosses / RadianceSkin | 0.9.6 / 0.1.1 | 已加入通用预加载兼容，尚未逐个实测。 |

## 通用兼容说明

- 加入或移除 Mod 后，旧序列的输入与 RNG 调用顺序可能变化（例如 `DebugMod` 构造期会消耗 1 次 Unity RNG），不能保证跨 Mod 环境直接回放旧序列；应在确定的 Mod 配置下重新录制并验证回放。
- 独立随机数兼容覆盖 TAS 启动保护安装后，Mod 在游戏主线程构造的无参 `System.Random()`（包含同进程 S0 之后由 `.cctor()` 惰性创建的静态实例，热回档时会自动重置回初始确定性种子状态）。已创建的旧实例、显式时间种子、自定义 PRNG 和多线程调度不在此范围；Movie 的 `rngSeed` 字段仍只重设 Unity RNG。
