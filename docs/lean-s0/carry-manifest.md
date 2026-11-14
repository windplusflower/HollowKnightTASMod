# Lean S0 场景边界携带清单与实证报告（T1）

本报告基于本地反编译源码（`../hkapi/Assembly-CSharp/`）、FSM 导出（`C:\Users\33361\.gemini\config\skills\hk-api\fsm-export\`）、游戏托管程序集 IL 审计以及三条不同历史的实机 S0 语义 JSON 探针对比生成，覆盖精简 S0 方案 T1 要求的全部待确认项、携带集（C1–C12）、S0 规范化重置项与忽略项分类。

---

## 1. S0 精确挂点与钩子可行性

1. **原版流程没有天然的“旧场景已卸载、新场景未实例化”纯窗口**：
   - `GameManager.BeginSceneTransition`（`../hkapi/Assembly-CSharp/GameManager.cs:399-403`）先调用 `ModHooks.BeforeSceneLoad(info.SceneName)`（第 401 行，此时旧场景尚未卸载），随后启动协程 `BeginSceneTransitionRoutine`（第 411 行），再创建并执行 `SceneLoad.BeginRoutine`（`../hkapi/Assembly-CSharp/SceneLoad.cs:99-200`）。
   - 在 `SceneLoad.BeginRoutine` 中，`loadOperation.allowSceneActivation = true` 并等待激活完成后（`SceneLoad.cs:148-155`），`ActivationComplete` 才调用 `UnityEngine.SceneManagement.SceneManager.UnloadScene(lastSceneName)`（`GameManager.cs:476-478`）。因此原版存在新旧两场景短暂共存，`ModHooks.BeforeSceneLoad` 无法作为“旧卸载/新未实例化”的 S0 挂点。
2. **Mod 实证 S0 挂点（MonoMod `Hook`）**：
   - 现网已在 `src/HollowKnightTAS.Runtime/FullRun/LoadingSerialSceneProbe.cs`（第 126–281 行）验证了 MonoMod `Hook` 串行边界：`Begin` 拦截 `SceneLoad.BeginRoutine`（与 `GameManager.BeginSceneTransitionRoutine`、`UnityEngine.SceneManagement.SceneManager.UnloadScene(string)` 配合），在 `IsFetchAllowed && IsActivationAllowed` 满足后：
     1. 将活跃场景切换至空暂存场景 `__HKTAS_SerialBoundary_v6`（第 150–152 行）；
     2. 异步卸载源场景 `UnloadSceneAsync(source)` 并等待 `operation.isDone`（第 154–162 行）；
     3. `yield return null` 经过一个 PlayerLoop 使销毁回调展开（第 164 行）；
     4. 在 `ObserveNativeBoundary`（第 250–281 行）停靠——**实测 `loadedScenes` 仅剩 `rootCount = 0` 的 `__HKTAS_SerialBoundary_v6` 与 `DontDestroyOnLoad`**，之后才执行 `original(load)` 实例化目标场景。
   - **关键时序细节（本次实证新发现）**：在 `LoadingSerialSceneProbe` 当前顺序中，`ObserveNativeBoundary` 位于 `original(load)` 之前，因此原版在 `SceneLoad.WillActivate` 触发的 `GameManager.DestroyPersonalPools()`（`GameManager.cs:470-473`、`SceneLoad.cs:136-140`）**尚未在 S0 之前执行**。T2 设计 `S0Gate` 时应在写入携带集前先统一调度/清理依赖 `DestroyPersonalPools()` 的常驻残留对象（详见第 3 节 `ObjectPool` 与第 4 节 D2）。

---

## 2. 方案第 3 节 C1–C12 逐项确认与裁剪

| 编号 | 类别 | 结论 | 携带字段 / 范围 | 源码 / 实证依据 |
|---|---|---|---|---|
| **C1** | `PlayerData` | **保留（整表携带）** | `GameManager.instance.playerData` 完整序列化（血量、灵魂、Geo、护符、能力、地图、Boss 进度、`bossDoorStateTier*`、hazard respawn、`playTime` 等；其中 `playTime` 为界面显示，不影响玩法，可一并由自带序列化携带） | `../hkapi/Assembly-CSharp/GameManager.cs:2345,2376,2721`；`SaveStats.cs:55-90`；Pair 2 diff `hazardRespawnLocation`/`profileID`/`playTime` |
| **C2** | `SceneData` | **保留（整表携带）** | `GameManager.instance.sceneData`（`persistentBoolItems`、`persistentIntItems`、`geoRocks`） | 跨场景持久保存已破坏墙体/机关/拾取物状态；Pair 2 同房间二次进入前后一致 |
| **C3** | 神居连战静态状态 | **保留** | `BossSequenceController.currentData`（`bindings`、`timer`、`knightDamaged`、`playerData` 目标 `PlayerData` 字段名如 `"bossDoorStateTier4"`、`previousCompletion`、`previousEquippedCharms`、`wasOvercharmed`、`bossSequenceName`）、`currentSequence`（按 `BossSequence` 资产名重绑定）、`bossIndex`、`WasCompleted` | `../hkapi/Assembly-CSharp/BossSequenceController.cs:455,498-549`；`history-a` b04 静态 dump 实测全字段在位 |
| **C4** | `StaticVariableList.variables` | **保留** | `StaticVariableList.variables` 字典（键值均为基础类型/可序列化数据，仅在 `GameManager.ReturnToMainMenu` 时清空） | `../hkapi/Assembly-CSharp/StaticVariableList.cs:6-46`；`GameManager.cs:3054` |
| **C5** | PlayMaker 全局变量 | **大幅裁剪（仅携带极少数运行时变量，其余按引用自绑定）** | 实测 `PlayMakerGlobals.Instance.Variables` 共含 32 个变量与 51 个全局事件，其中 **27 个为常驻 DDOL 对象固定引用**（如 `Hero`、`MainCamera`、`HUD Camera`、`GameManager`、`AudioManager`、`Inventory` 等，无需序列化）；其余 `Is HUD Out`、`Orb Target`、`MenuStagMap`、`Geo Counter`、`Track 1`、`Orbit Shield` 仅当非空且有跨场景逻辑时按需处理（若 D2 采纳 S0 重置，`Orbit Shield` 亦不需携带） | `history-c` b02 `globalVars.*` 全量导出（32 项）与 `diff-historyA-vs-historyC-b02.json`（仅 `globalVars.go:Orbit Shield` 存在差异） |
| **C6** | 过场参数 | **保留** | `SceneLoadInfo`：`SceneName`、`EntryGateName`、`HeroLeaveDirection`、`Visualization`、`EntryDelay`、`AlwaysUnloadUnusedAssets`、`PreventCameraFadeOut`、`IsFirstLevelForPlayer` | `../hkapi/Assembly-CSharp/GameManager.cs:399-468, 613-627`；恢复端用于发起正常 `BeginSceneTransition` |
| **C7** | `HeroController` | **精细裁剪后保留（剔除 `EnterScene` / `LeaveScene` 重置项；异常旧场景时间戳/泄露委托归入 S0 规范化）** | 见下表 2.1（共 16 项保留或 S0 规范化；`position`、`velocity`、`airDashed`、`doubleJumped`、`attack_time` 等由 `EnterScene` 重置，**全部剔除**） | `../hkapi/Assembly-CSharp/HeroController.cs:193-226, 1787-2083, 3104-3165, 3617-3639, 5055-5091`；Pair 2 diff `Knight.HeroController` 21 项差异 |
| **C8** | 小骑士身上 FSM | **保留当前状态 + 极少数跨场景未复位字段（并满足 D4 空闲边界前提）** | 小骑士身上共 12 个 FSM；正常场景出口触发 `HeroCtrl-LeavingScene` 后 11 个处于空闲态，`Control Interpolation` 位于 `Idle` 或 `Check Pause`。需注意 **`Spore Cooldown` 没有 `LEAVING SCENE` 转移**（若带毒菌冷却过场则处于 `Cooldown`/`Dung`），以及 **`Dream Nail` 保留 `Queue Timer`**（与 `Time.timeSinceLevelLoad` 比较） | `history-a` b02/b04/b07 `summary.knightFsms` 实测（见第 4 节表） |
| **C9** | 护符随从 | **D2 已拍板方案 B（仅携带 `Grimmchild` + `Hatchlings` 计数）** | `Weaverling`（39）与 `Orbit Shield`（38）在原版过场随 `DestroyPersonalPools` 回收并重生；`Knight Hatchling`（22）由 `Charm Effects/Hatchling Spawn` 的 `int Hatchlings` 计数并在入场后重生；**`Grimmchild`（40）按 D2 方案 B 携带跨场景存活实例状态** | `history-c` b02 实测 + `serial-scenes-18032.jsonl`（frame 2259/2261/2266）+ `Charm_Effects__*.md` |
| **C10** | 输入前一帧状态 | **保留（复用 TAS 输入帧状态）** | `InputHandler` 上一帧按键状态（`WasPressed` / `WasReleased` 边沿判定）；InControl 设备 GUID、`UpdateTick`、`LastInputTypeChangedTick`、`pendingTick`、`lastVectorPressedTime` 等平台/进程计数不携带 | Pair 1 diff（InControl tick 差异 22 项）与 Pair 2 diff |
| **C11** | 时间相关 | **裁剪后仅保留玩法状态** | 不携带 `PlayerData.playTime`（或仅由 C1 顺带）与 `GameManager.sessionPlayTimer` 以外的 wall-clock/引擎时间；若存在正在进行的 `timeSlowedCount` / `Time.timeScale` 慢动作窗口则记录（正常过场边界 `timeSlowedCount == 0`、`timeScale == 1`） | `../hkapi/Assembly-CSharp/GameManager.cs:23-27, 2345-2376, 2810-2901` |
| **C12** | TAS 控制面 | **保留（复用现有实现）** | 影片帧号、ClockBridge 帧时钟状态、输入队列 | 现有运行时基础设施 |

**明确不携带（精简原则剔除）**：新场景全部对象、相机状态（见第 3 节）、HUD/Inventory 显示节点（由 `PlayerData` 重建，Pair 2 diff 639+179 项全部随入场同步）、音乐与音效本身（按 D3 仅记 cue 标识且不参与 K3 验收）、`ObjectPool` 中 60 个空闲预制体残留位置/颜色（Pair 2 diff 2697 项）、`SceneParticlesController` 粒子区域缓存、`Low Health Vignette` 与 `Hero Damaged Effects` 视听过渡状态、`Fsm.lastRaycastHit2DInfoLUT`（Pair 2 348 项）与 `FsmEvent._eventLookup`（全局事件注册缓存）。

---

### 2.1 C7 `HeroController` 逐字段审计表

对比 `HeroController.LeaveScene`（`HeroController.cs:2050-2083`）、`EnterScene`（`1787-2047`）、`FinishedEnteringScene`（`3104-3165`）与 `SceneInit`（`193-226`）：

- **明确会被过场重置（不携带）**：`isHeroInPosition`、`transitionState`、`cState.transitioning`、`cState.onGround`（顶/底/左/右/门分支均重设）、`cState.attacking` / `upAttacking` / `downAttacking` / `attack_time`（`ResetAttacksDash`, 3617-3623）、`airDashed`、`doubleJumped`（1791-1792）、`rb2d.velocity` / `transition_vel` / `wallLocked` / `nailChargeTimer` / 跳跃与弹反状态（`ResetMotion`, 3626-3639）、`hardLandingTimer`（1793）、`move_input` / `vertical_input`（或进门重置）、`transform.position`（`EnterScene` 重定位）、`positionHistory[0..1]`（`FinishedEnteringScene`, 3163-3164，Pair 1 diff 中亦有 1e-5 浮点差）。
- **过场不会被 `EnterScene` 重置且影响后续玩法的字段（携带或 S0 规范化）**：

| 字段名 | 声明行 | 影响机制与过场行为 | 处理分类 |
|---|---|---|---|
| `cState.facingRight` | `HeroControllerStates.cs:60` | 仅从左右门与指定 `alwaysEnterRight/Left` 的门重置；**从 `GatePosition.top` 入口与无固定朝向的 `bottom` 入口进入时不重置**（`HeroController.cs:1805-1906`） | **携带** |
| `exitedSuperDashing` | `HeroController.cs:5981` | `BeginSceneTransitionRoutine`（`GameManager.cs:427-430`）置位；`EnterScene` 左右门分支读取后触发 `HeroCtrl-EnterSuperDash`，`FinishedEnteringScene`（3155-3158）清零 | **携带** |
| `exitedQuake` | `HeroController.cs:5984` | `BeginSceneTransitionRoutine`（`GameManager.cs:431-434`）置位；`EnterScene` 顶门分支读取后触发 `HeroCtrl-EnterQuake` 保持下砸无敌帧（3138-3141, 3159-3162） | **携带** |
| `enterWithoutInput` | `HeroController.cs:5855` | `FinishedEnteringScene`（3146-3149）检查是否恢复输入控制，仅在执行后清零 | **携带** |
| `attack_cooldown` | `HeroController.cs:5498` | 普攻冷却剩余时间（`orig_DoAttack` 设为 `ATTACK_COOLDOWN_TIME[_CH]`），`EnterScene` 不清零；Pair 2 实测 `0` vs `-0.009999961` | **携带**（若 `> 0`；`< 0` 已到期可忽略） |
| `dashCooldownTimer` | `HeroController.cs:5543` | 冲刺冷却计时器（3013-3017），`EnterScene` 不清零；Pair 2 实测 `0` vs `-0.01999997` | **携带**（若 `> 0`） |
| `shadowDashTimer` | `HeroController.cs:5540` | 黑冲冷却计时器（3019-3021），`EnterScene` 不清零；Pair 2 实测 `0` vs `-0.0199991949` | **携带**（若 `> 0`） |
| `parryInvulnTimer` | `HeroController.cs:5567` | 拼刀/旋风/下砸剩余无敌时间（1183-1209, 1250, 4960-4962），`EnterScene` 不清零 | **携带**（若 `> 0`） |
| `preventCastByDialogueEndTimer` | `HeroController.cs:6180` | 对话结束禁施法 0.3s 计时（2428, 2434），Pair 2 实测 `-48.88` vs `-82.77` | **携带剩余正值 / 到期可置 0** |
| `hitsSinceShielded` | `HeroController.cs:5867` | 无忧旋律累计受击次数（1262-1323），影响下次触发无敌护盾概率 | **携带** |
| `cState.altAttack` & `altAttackTime` | `HeroControllerStates.cs:123`, `HeroController.cs:673,837,5504` | Pair 2 实测 `altAttack: False -> True`、`altAttackTime: 0 -> 92.836`。记录 `Time.timeSinceLevelLoad`（因 HK 仅用 `LoadSceneMode.Additive` 实测不归零，但不同耗时历史该绝对时间不同）。直接携带绝对时间戳会导致跨历史判定漂移 | **S0 规范化重置**（置 `altAttack = false, altAttackTime = 0f`；或在记录时转为相对剩余时间 `max(0, ALT_ATTACK_RESET - (timeSinceLevelLoad - altAttackTime))`） |
| `cState.invulnerable` | `HeroControllerStates.cs:162` | 受击/无敌协程开启期间的布尔状态，`EnterScene` 未显式清零 | **S0 规范化或携带**（一般过场后应为 `false`） |
| `cState.nailCharging` | `HeroControllerStates.cs:75` | `EnterScene` 只调用 `ResetAttacksDash()` 而不清 `nailCharging`（`ResetMotion()` 只清 `nailChargeTimer`） | **S0 规范化置 `false`**（与 D4 空闲边界一致） |
| `carefreeShieldEquipped` / `joniBeam` / `nailChargeTime` | `HeroController.cs:5055-5091, 5864, 5996, 6002` | 仅在 `orig_CharmUpdate()` 时从 `PlayerData` 计算，**过场时不重算**；若仅写 `PlayerData` 而不调用 `CharmUpdate()` 会不同步 | **恢复端在写回 `PlayerData` 后调用 `HeroController.CharmUpdate()` 统一重算** |
| `wieldingLantern` / `takeNoDamage` / `blockerFix` | `HeroController.cs:1030,1215,5915,5999,6014` | 提灯状态、`takeNoDamage` 标记、一次性 `blockerFix` 修复标记，`EnterScene` 不清零 | **携带**（`blockerFix` 对旧档安全保留） |
| `OnTakenDamage` 事件委托链 | `HeroController.cs:40, 1397-1399`; `BossSceneController.cs:180,209` | **Pair 2 diff 实证关键隐患**：战死离开 `GG_Crystal_Guardian_2` 时未触发 `EndBossScene()`，且 `BossSceneController.OnDestroy()`（112-122）**未退订 `OnTakenDamage -= SetKnightDamaged`**，导致边界 #7 时 `OnTakenDamage` 仍悬挂已销毁场景的 `BossSceneController.SetKnightDamaged` 委托 | **S0 规范化清理**（若 `BossSceneController.Instance == null` 或无活跃场景归属时清空残留委托，防止悬挂跨场景执行） |

（说明：`HeroController` 中的 `recoilVector`、`attackDuration`、`attackQueueSteps` 虽在 Pair 2 diff 出现，但仅在主动触发攻击/受击反冲时由 `DoAttack`/`Recoil` 覆盖写入，`EnterScene` 已重置 `attacking`/`recoiling`；`SpriteFlash` 仅影响视觉闪烁，均可忽略。）

---

## 3. 专题问题逐项实证结论

1. **C8 小骑士身上 12 个 FSM 列表及过场空闲状态**：
   - 在 `history-a` 边界 `b02`、`b04`、`b07` 的 S0 时刻实测全部 12 个 FSM 均处于空闲/等待状态：
     - `ProxyFSM`: `Idle`（`prev = Leaving Scene`）
     - `Spell Control`: `Inactive`（`prev = Reset Damage Mode`）
     - `Map Control`: `Inactive`（`prev = Reset Timer`）
     - `Roar Lock`: `Detect`（`prev = Cancel Effects`）
     - `Superdash`: `Inactive`（`prev = Cancel`）
     - `Nail Arts`: `Inactive`（`prev = Cancel All`）
     - `Surface Water`: `Inactive`（`prev = Cancel All`）
     - `Dream Nail`: `Inactive`（`prev = Cancel`，但其变量 `float:Queue Timer` 在 Pair 2 存在旧时间戳 `-48.76` vs `-82.81`，建议 S0 重置或归零）
     - `Dream Return`: `Idle`（`prev = Check`）
     - `Spore Cooldown`: `Idle`（`prev = Init`；**注意**：源码/FSM 中该 FSM 无 `LEAVING SCENE` 转移，若于蘑菇孢子 4.25s 冷却期离开场景，将带着 `Cooldown`/`Dung` 状态穿过 S0，因此需要**携带状态或 S0 重置为 `Idle`**）
     - `Globalise`: `Init`（`prev = Init`）
     - `Control Interpolation`: b02 为 `Idle`，b04/b07 为 `Check Pause`（0.1s 轮询等待态，`activeAction[0](Wait).startTime` 随 `Time.time` 变化，属时间基差异）
   - 符合 D4：正常场景过渡出口均先广播 `HeroCtrl-LeavingScene`，使战斗/法术/冲刺类 FSM 在到达 S0 前已复位为空闲。
2. **`ObjectPool` 在过场时是否回收（`RevertToStartState` 调用方）**：
   - `ObjectPool.RevertToStartState()`（`../hkapi/Assembly-CSharp/ObjectPool.cs:110-160`）**仅被作弊菜单调试逻辑调用**（`CheatManager.cs:366`）。
   - `ObjectPool.RecycleAll()`（0 参数，`ObjectPool.cs:309`）仅在 `RevertToStartState()`（112）和退回主菜单 `QuitToMenu.cs:34` 调用。
   - **正常过场回收机制**：`GameManager.BeginSceneTransitionRoutine` 在 `sceneLoad.WillActivate` 触发 `DestroyPersonalPools()`（`GameManager.cs:470-473`），通知：
     - `PersonalObjectPool.DestroyMyPooledObjects()`（`../hkapi/Assembly-CSharp/PersonalObjectPool.cs:14-54`）按旧场景启动池配额销毁多余对象；
     - 挂载 `AutoRecycleSelf` 且 `afterEvent == AfterEvent.LEVEL_UNLOAD` 的实例触发 `RecycleSelf()`（`../hkapi/Assembly-CSharp/AutoRecycleSelf.cs:22-25, 83-85`），自动归还至 DDOL `_GameManager/GlobalPool`。
   - **实机验证**：`history-a` 全部 7 个边界的 `objectPoolSummary.spawnedTotalCount` 在 b01/b03/b04/b06/b07 均为 `0`，仅 b02/b05 为 `1`（即刚离开门时活跃的 `Run Effects(Clone)`，在 `LoadingSerialSceneProbe` 中因 `WillActivate` 尚未触发而暂留）。
3. **相机是否在入场时重新定位**：
   - **是，完全重定位，不需要携带。**
   - 卸载时 `CameraController.OnLevelUnload()`（`../hkapi/Assembly-CSharp/CameraController.cs:741-751`）释放全部 `lockZoneList`。
   - 场景入口 `CameraController.SceneInit()`（`CameraController.cs:24-66`）与 `CameraTarget.SceneInit()`（`../hkapi/Assembly-CSharp/CameraTarget.cs:19-42`）重置 `startLockedTimer = 0.5f`、`velocity = Vector3.zero`、`xLockMin/Max`、`yLockMin/Max`、`dampTimeX/Y`、`mode = FOLLOW_HERO`、`stickToHeroX/Y = true`、`fallCatcher = 0f`。
   - 随后 `HeroController.heroInPosition` 触发 `CameraController.PositionToHero` -> `DoPositionToHero` -> `camTarget.PositionToStart()`（`CameraController.cs:516-600`，`CameraTarget.cs:505-595`），清零 `velocityX/Y` 并瞬移对齐骑士位置、朝向偏移与场景/锁区边界。
   - Pair 1/2 diff 中的 `CameraController.destination` / `lastLockPosition` / `velocityX` 1e-6 微小差异与 `CameraTarget.exitedTop` 均在入场时覆盖或仅影响瞬时视觉跟拍，可忽略。
4. **梦之门在同场景传送时是否重载场景**：
   - **是，无条件重载场景，可以作为 S0 边界。**
   - `GameManager.WarpToDreamGate()`（`../hkapi/Assembly-CSharp/GameManager.cs:613-627`）无同场景短路判断，直接调用 `this.BeginSceneTransition(new SceneLoadInfo { SceneName = playerData.GetString("dreamGateScene"), EntryGateName = "dreamGate", AlwaysUnloadUnusedAssets = true, PreventCameraFadeOut = true, Visualization = SceneLoadVisualizations.Dream })`。
5. **随机源全量核实（C# + PlayMaker）**：
   - 用 `Mono.Cecil` 对本地运行库执行全量 IL 指令扫描结果：
     - `Assembly-CSharp.dll`（含全部 `HutongGames.PlayMaker.Actions.*` 动作类）：`UnityEngine.Random` 调用 **415** 处，`System.Random` **0** 处，`InitState` / `set_seed` / `set_state` **0** 处。
     - `Assembly-CSharp-firstpass.dll`：`UnityEngine.Random` **11** 处，`System.Random` **0** 处，`InitState` / `set_seed` / `set_state` **0** 处。
     - `PlayMaker.dll`：`UnityEngine.Random` **0** 处，`System.Random` **0** 处，`InitState` / `set_seed` / `set_state` **0** 处。
   - 源码交叉检索（`../hkapi/Assembly-CSharp/`）确认 `new System.Random(` / `new Random(` 为 **0** 处，152 个文件使用 `UnityEngine.Random`。
   - **结论**：游戏与 PlayMaker 的全部伪随机数统一来自单一的 `UnityEngine.Random`，在 `S0Gate` 调用 `UnityEngine.Random.InitState(seed)` 即可完全确定性重置。

---

## 4. C9 护符随从实证与 D2 建议

我们在 `history-c`（`artifacts/scene-checkpoints/p3b/lean-s0-probe-20261114-143044/history-c/run-01/`，装备护符 `22, 38, 39, 40`）中分别检查了 `before-unload`（frame 2259）、`native-empty-boundary`（S0，frame 2261）、`target-finished`（frame 2266）的 DDOL 根与组件，并对照 FSM 导出文件：

| 护符 / 随从 | `AutoRecycleSelf` 行为 | `Charm Effects` FSM 过场逻辑 | `history-c` 实测（`GG_Workshop -> GG_Atrium`） | 归类 |
|---|---|---|---|---|
| **编织者之歌（Weaverling, 39）** | `afterEvent = LEVEL_UNLOAD`（b02 实测） | `LEVEL LOADED -> Spawn Pause -> WaitForHeroInPosition -> Spawn` 重生（`Charm_Effects__Weaverling_Control__fsm_1076.md:321-336`） | frame 2261 有 3 个 `ddol/Weaverling(Clone)` 根；经过 `WillActivate` 后在 frame 2266（`target-finished`）**全部回收**，DDOL 根数从 16 降至 13 | **原版自动销毁并重生** -> 归入 **S0 重置**（不需要携带实例；若 S0 停在 `WillActivate` 前则由 `S0Gate` 提前清理或交由后继 `WillActivate` 回收） |
| **舞梦者之盾（Orbit Shield, 38）** | `afterEvent = LEVEL_UNLOAD`（b02 实测） | `LEVEL LOADED -> Init -> Charms Allowed? -> Check -> Spawn` 立即重生并写 `[Global] Orbit Shield`（`Charm_Effects__Spawn_Orbit_Shield__fsm_1082.md:100-150,247`） | frame 2261 存在 `ddol/Orbit Shield(Clone)`；`WillActivate` 回收后在 `LEVEL LOADED` 立即重生 | **原版自动回收并重生** -> 归入 **S0 重置**（亦无需携带 `[Global] Orbit Shield` 引用） |
| **发光子宫小虫（Knight Hatchling, 22）** | 不跨场景保留实体 | **`LEAVING SCENE -> Count Remaining` 统计存量至 `int Hatchlings`**；`LEVEL LOADED -> Respawn Pause -> Respawn` 按该计数以 `QUICK SPAWN` 在骑士身边重生（`Charm_Effects__Hatchling_Spawn__fsm_972.md:330-457,701-702`） | b02 时因进入 `GG_Workshop` 灵魂清零（`MPCharge=0`）未孵化（`Hatchlings=0`） | **原版销毁实体，仅在 `Charm Effects/Hatchling Spawn` 保留整数 `int Hatchlings`（与 `bool Dreamgate Entry`）** -> 携带 `int Hatchlings` 计数即可，不携带小虫实例 |
| **格林之子（Grimmchild, 40）** | **无 `AutoRecycleSelf` 组件**（b02 全组件列表实测无此脚本） | `Spawn` 阶段执行 `FindGameObject(tag="Grimmchild")`，**非空则直接 `CANCEL` 不重生**（`Charm_Effects__Spawn_Grimmchild__fsm_1056.md:135-160,551`） | `ddol/Grimmchild(Clone)` 在 `before-unload`（2259）、S0（2261）、`target-finished`（2266）**全程原实例存活于 DDOL**，保留世界坐标、速度与 `Control` FSM 的 `Attack Timer`（`0.55s`） | **原版过场直接保留存活实例（与前三种不同！）** |

**D2 终裁（方案 B，已获 `@coordinator` 批准）**：
- 保留原版过场行为：当源端存在 `ddol/Grimmchild(Clone)` 时，携带其跨场景持久状态：
  - 根物体启用态与 `Transform`（`localPosition`、`localRotation`、`localScale`）
  - `Rigidbody2D`（`position`、`velocity`）与 `ObjectBounce`（`velocity`、`lastPos`、`speed`、`stepCounter`、`bouncing`）
  - `PlayMakerFSM("Control")`：`activeStateName`、`previousStateName`、全部运行时变量（`Away`、`Dream Appear`、`Not Away`、`Scene Appear`、`Attack Timer`、`Away Timer`、`Distance`、`Hero Scale`、`Offset X/Y`、`Radius`、`Ray Down Distance`、`Speed`、`Damage`、`Grimm Level`、`Absorb Pos`、`Offset`、`Self Pos` 及 `Level Str` / anim 字符串等）与运行中的 `Wait`/`GrimmChildFly` 动作剩余计时器
- 同步生成/回收策略：若源端存在而目标端尚未生成，则从 `Charm Effects/Spawn Grimmchild` 的 `Spawn` 状态 `SpawnObjectFromGlobalPool.gameObject` prefab 经 `ObjectPool` 生成并挂入 DDOL 后写回；若目标端已存在则直接复用并写回；若源端不存在而目标端存在则回收目标端多余实例。
- 其余三种随从按原版规则处理：`Weaverling(Clone)` 与 `Orbit Shield(Clone)` 由 `S0Gate` 清理/等待 `WillActivate` 回收后在 `LEVEL LOADED` 重生；`Knight Hatchling` 仅携带 `Charm Effects/Hatchling Spawn` 的 `int Hatchlings`（与 `bool Dreamgate Entry`）。

---

## 5. 双历史差分实证汇总

实机产出目录：`artifacts/scene-checkpoints/p3b/lean-s0-probe-20261114-143044/`
- **历史 A（原始 P3B）**：`history-a/run-01/s0-semantic-boundary-01..07.json`（完整运行至 frame 5800，全部 7 个边界完整采集）。
- **历史 B（开头菜单插入 `+120` 空闲帧）**：`history-b/run-01/s0-semantic-boundary-01..04.json`（完整采集 `Menu_Title->GG_Workshop`、`GG_Workshop->GG_Atrium`、`GG_Atrium->GG_Boss_Door_Entrance`、`GG_Boss_Door_Entrance->GG_Crystal_Guardian_2` 4 个对应边界；注：运行在进入第 4 边界后至 frame 4136 时因 `domain=director reason=40` 中止于 `EndFrame=4220` 前，已采集的 b01–b04 快照完整有效）。
- **历史 C（装备随从 `[22,38,39,40]`）**：`history-c/run-01/s0-semantic-boundary-01..02.json`（完整运行至 frame 2500）。

| 对比组 | 差分文件 | 总差异键数 | 分域分布与分类结论 |
|---|---|---|---|
| **Pair 1a（b02：`GG_Workshop -> GG_Atrium`，+120 空闲帧）** | `diff-pair1-b02.json` | **61**（总键 ~85.8 万） | `UIManager:22`（InControl tick / `CheckpointSprite.frameTimer` -> **忽略**）、`GameManager:20`（AudioSource `.time` / `InputHandler` tick & controller GUID / 池内空闲效果位置 / `OnScreenDebugInfo.unloadTime` / `previousTimeSinceStartup` -> **忽略**）、`GameCameras:11`（`CameraController` / `Low Health Vignette` 起始时间 / `Blanker White` -> **忽略/入场重设**）、`Knight.Children:2`（`Wall Puff` 局部坐标 & `Charm Effects` Wait 时间 -> **忽略**）、`Knight.HeroController:2` / `OtherDDOL:4`（`saveDirPath` 沙盒路径、`positionHistory` 等 -> **忽略**）。**零漏携项，RNG 状态完全相同。** |
| **Pair 1b（b04：`GG_Boss_Door_Entrance -> GG_Crystal_Guardian_2`，+120 空闲帧）** | `diff-pair1-b04.json` | **61**（857,935 键） | 结构与 Pair 1a 几乎完全一致：仅多出 `Knight.FSM:1`（`Control Interpolation.Wait.startTime`: `77.74` vs `80.14`）、`Weaverling Control.Wait.startTime`（即使未装备也跑 Wait 计时器）。**零漏携玩法状态，RNG 状态完全相同（`s0=1212896321` 等）。** |
| **Pair 2（b04 vs b07：首次 vs 战败返回后重入 `GG_Crystal_Guardian_2`）** | `diff-pair2-b04-vs-b07.json` | **4708** | 真实战斗+退场后的全部差分已穷举分类：<br>1. **需携带（C1/C7）**：`PlayerData` 3 项（`hazardRespawnLocation`, `profileID`, `playTime`）、`HeroController.cState.facingRight`（顶/底门不重设）、`attack_cooldown` / `dashCooldownTimer` / `shadowDashTimer` / `parryInvulnTimer` / `preventCastByDialogueEndTimer`。<br>2. **需 S0 规范化重置**：**`HeroController.OnTakenDamage` 悬挂委托 `BossSceneController.SetKnightDamaged`**（Pair 2 实证捕获的真漏洞）；`HeroController.cState.altAttack` + `altAttackTime`（`0` vs `92.836` 绝对关卡时间）；`Dream Nail.vars.float:Queue Timer`（`-48.76` vs `-82.81`）。<br>3. **可安全忽略**：`GlobalPool` 60 个空闲预制体（2697 项）、`HudCamera` / `Inventory` / `Low Health Vignette` / `SceneParticlesController`（1367 项）、`Fsm.lastRaycastHit2DInfoLUT` & `FsmEvent._eventLookup`（350 项）、`NailSlash` / 粒子 / `SpriteFlash`（197 项）、`AudioSource` & `Hero Damaged Effects`（8 项）、InControl/LoadingSpinner（35 项）、`positionHistory`/`recoilVector`/`attackDuration`/`attackQueueSteps`（6 项）。 |
| **Pair 3（b02：history-a vs history-c 护符随从对照）** | `diff-historyA-vs-historyC-b02.json` | **6197** | 实证确认 `Grimmchild(Clone)`、`Orbit Shield(Clone)`、`Weaverling(Clone)` ×3、`[Global] Orbit Shield` 与 `PlayerData` 护符字段差异。 |

---

## 6. 产物索引与机器可读清单

- 机器可读携带清单：[`docs/lean-s0/carry-manifest.json`](carry-manifest.json)
- 探针代码与脚本：`src/HollowKnightTAS.Runtime/FullRun/S0SemanticProbe.cs`、`src/HollowKnightTAS.Runtime/FullRun/LoadingSerialSceneProbe.cs`（264–272 行，严格受 `HKTAS_S0_SEMANTIC_PROBE=1` 控制）、`scripts/Invoke-S0SemanticProbe.ps1`、`scripts/Compare-S0SemanticSnapshots.py`。
- 实机卫生确认：三次运行（`history-a`、`history-b`、`history-c`）均满足 31 份玩家存档 `saves-before.json == saves-after.json`、`mods-isolation.json` 为 `restored`、无残留 `hollow_knight`/`StudioScenarioHarness` 进程、Companion 签名 `Valid`。
