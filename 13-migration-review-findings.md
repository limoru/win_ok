# 13. 迁移与总线审查记录（2026-10-14）

> 本文最初记录静态审查发现的问题；2026-10-14 后续已追加修复状态、复核结论和针对性回归测试。所有行号基于审查时的文件内容，后续改动可能导致行号偏移，使用前请重新核对。
>
> Python 源码路径：`D:\work\ok\ok-kes\ok_tasks\`
> C# 代码路径：`D:\work\win_ok\`

## 1. Chaos 模式迁移差异（Python → C#）

### 1.1 `HandleRestAsync` 恢复 Python 的 AND 条件（已修复）

- Python：`utils.py:4372`
  ```python
  can_rest = bool(rest_feature and "免费" in free_text and flash_or_rest)
  ```
  要求"识别到休息特征" **且** "免费区域确实有'免费'文字" **且** 业务门闩为真，三者同时满足才能休息。

- C#：`Kes.Tasks/ChaosModeTask.ShopRest.cs`（`HandleRestAsync` 开头附近）
  ```csharp
  var canRest = restFeature is not null
                && hasFreeRest
                && page.State.FlashOrRest;
  ```
  修复后与 Python 一样要求三者同时满足，不再在没有“免费”文字时放行休息。

- 影响：免费次数用完、画面只剩休息特征但没有“免费”文字时，C# 现在与 Python 一样判定不能休息，改走后续移除/商店检测分支。
- 状态：**已修复（2026-10-14）**；`RestDoesNotClickWhenFreeTextMissing` 已锁定该行为。

### 1.2 `ResetLayer()` 保留冥想状态和待处理移除计数（已修复）

- Python `reset_layer_status`（`utils.py:5464-5486`）只触碰 `node_status` 字典里的 9 个字段（`pass_final_boss_count`/`total_rounds`/`success_rounds`/`save_target_member`/`target_mask_card_position`/`get_specific_flash`/`removed_card_count`/`shop_removed_card_count`/`neutral_card_count`），**完全不调用** `_reset_meditation_state(task)`，**也完全不触碰** `task._pending_removed_card_count`。
- 对比同文件里的 `reset_mission_status`（`utils.py:5447-5461`）和 `reset_all_status`（`utils.py:5435-5444`），这两者都会调用 `_reset_meditation_state(task)` 并把 `_pending_removed_card_count` 清零——但 `reset_layer_status` 两者都不做，这是 Python 代码里刻意的差异化设计："过关进入下一层"场景下，冥想追踪状态和待处理的移除卡牌计数要原样延续，不能清空。
- C# 对应：`ChaosRunState.ResetLayer()`（`Kes.Tasks/ChaosRunState.cs`）内部调用 `ResetAll()`，而 `ResetAll()` 会无条件执行 `MeditationCards.Clear()` 和 `PendingRemovedCardCount = 0`。`ResetLayer()` 的保留清单里没有把这两项加回去，所以执行完 `ResetLayer()` 之后，`MeditationCards` 变成空字典、`PendingRemovedCardCount` 变成 0。
- 影响：玩家过关进入下一层时，C# 版本会意外清空"哪些卡牌还需要冥想"的追踪状态，以及还没处理完的移除卡牌计数会被重置为 0，这两者在 Python 里都应该原样带入下一层。这是一个会实际影响游戏内行为的差异，不是无害的实现细节。
- 核对方式：直接读取 Python 源码确认了 `reset_layer_status` 函数体，并读取 C# `ChaosRunState.ResetLayer`/`ResetAll` 的完整实现对照，确认是真实差异，不是审查误判。
- 状态：**已修复（2026-10-14）**。`ResetLayer()` 现在保留 `MeditationCards` 和 `PendingRemovedCardCount`；`ResetLayerPreservesProgressCounters` 已覆盖。

### 1.3 装备安装补充“传说装备互斥”判断（已修复）

- Python：`utils.py:2902-2908`
  ```python
  has_legendary_in_other_slot = any(
      quality == "传说" and equipment_slot != slot
      for equipment_slot, quality in enumerate(live_qualities)
  )
  if new_equipment["quality"] == "传说" and has_legendary_in_other_slot:
      should_install_first = False
      install_reason = "该主战员其他装备位已有传说装备"
  ```
  非刻印模式下，除了比较优先级和品质，还会检查该主战员其他槛位是否已有传说装备，避免同一人装两件传说装备。

- C#：`Kes.Tasks/ChaosEquipmentLogic.cs` 的 `DecideInstall` 方法——只比较优先级 rank 和品质高低，**完全没有这段互斥检查**。
- 影响：C# 版本可能给同一主战员的不同槛位各装一件传说装备，Python 会主动把第二件让给别的主战员。
- 状态：**已修复（2026-10-14）**。`DecideInstall` 现在接收其他槽位品质并拒绝第二件传说装备；`EquipmentDecisionRejectsSecondLegendaryForSameMember` 已覆盖。

### 1.4 商店删牌上限统计口径（复核结论：原审查误判）

- Python `_shop_removal_limit`（`utils.py:4793`）和 `_try_remove_shop_card`（`utils.py:4817`）实际读取的是 `shop_removed_card_count`；文档注释也明确写着事件/三选一删卡不占商店额度。
- C# 的 `RemovedCardCount`（总数）与 `ShopRemovedCardCount`（仅商店）拆分与 Python 语义一致，`HandleShopAsync` 使用后者正确。
- 影响：原审查所述“总数与商店数混用”并不存在，实际不会产生该差异。
- 状态：**复核完成，无需修改**；`ThreeChoiceAndShopRemovalKeepIndependentCounters` 已覆盖两类计数互不影响。

### 1.5 路线优先级里的“商店”排序（复核结论：原审查误判）

- Python 在实际选点时先检查 `shop_rank = priority_index.get("商店")`：列表包含“商店”时按它自己的排名计分，不包含时才沿用商店节点固定最优先。
- C# 的 `SelectVisibleRouteNode` 与 Python 分支一致，列表中是否包含“商店”决定商店节点采用可排序排名还是固定最高优先级。
- 状态：**复核完成，无需修改**；`RouteSelectionHonorsShopOnlyWhenItAppearsInPriorityList` 已覆盖列表内/列表外两种行为。

### 1.6 "Boss 前自动暂停"接入运行时暂停（已修复）

- `ChaosModeTask.cs` 约 1986-1992 行，命中"第几层 boss 前自动暂停"配置后只打印 warning 日志（"已配置 Boss 前自动暂停；当前阶段尚未接入运行时暂停，继续进入 Boss"），没有真正调用暂停，直接继续进 Boss。
- Python 对应位置会真正调用 `og.executor.pause()` 并 `sleep(5)`。
- 影响：原实现中用户在配置界面上可以设置该选项，但设置后不会生效。
- 状态：**已修复（2026-10-14）**。命中配置后调用 `TaskContext.RequestPause`，GUI 会话进入暂停状态；`PauseRequestedBossLayer` 防止同一层重复请求，`RouteSelectionPausesBeforeConfiguredBossLayer` 已覆盖。

### 1.7 "首层刷特定闪光"完整链路（已修复）

- `ChaosRunState.GetSpecificFlash` 字段已声明，但在 `HandleFlashEffectAsync`/`ChaosCardRecognition.ChooseByFlashPriority` 中没有任何代码把它设为 `true`。
- Python 对应逻辑（`utils.py:5258-5264`）命中闪光优先级第一项且配置"首层刷特定闪光"为真时会设置该标记，后续 `_finish_only_first_layer`（`utils.py:5408-5432`）用它判断"没刷到指定闪光就在第一层通关后自动退出重刷"。
- 影响：原实现中这条链路整体断开，配置设置了也不会起作用。
- 状态：**已修复（2026-10-14）**。`HandleFlashEffectAsync` 命中闪光优先级第一项时写入 `GetSpecificFlash`；新增卡厄思奖励结算/领取处理器并补齐 `_finish_only_first_layer` 的退出重刷与“只打第一层”分支；配置面板新增“首层刷特定闪光”开关。`FlashEffectChoosesCardByFlashPriority`、`ChaosRewardSettlementExitsWhenSpecificFlashMissing` 已覆盖。

### 1.8 `HandleNegotiationAsync` 改为精确匹配并补充回归测试（已修复）

- Python：`utils.py:3283-3290` 使用 `title.name in "失败"`，只能匹配“失”“败”“失败”，不能匹配“同步失败”“战斗失败”等其他含“失败”的文案；正常页面 OCR 恰好为“失败”时仍会触发。
- C#：`ChaosModeTask.cs` 现在使用 `MatchesAny` 精确匹配清理后的“失败”，不会把同坐标的“同步失败”“战斗失败”等文案一并吃掉。
- 状态：**已修复并补测**。`NegotiationClicksSkip` 验证精确“失败”会点击，`NegotiationDoesNotClickWithoutFailureText` 与 `NegotiationDoesNotClickForOtherFailureText` 验证缺失失败文字或其他“XX失败”时不点击。

### 1.9 已知范围缩减（迁移台账已记录，非本次新发现）

以下是台账中已标注"暂缓/待迁移"的功能缺口，列出仅为完整性备查，不是新问题：

- 刻印模式整体未迁移（刷装备刻印、装备刻印优先级）。
- 刷初始卡牌 / 刷空档的 ESC 兜底分支（事件处理器）。
- `treasure`（宝库）特征优先点击分支（事件处理器）。
- 结算流程中 `engraving_log.end_run(...)` 对应的刻印日志记录（`ChaosModeTask.cs:1364` TODO）。
- 复制卡牌优先级分支（`handle_copy_card_choice`）等台账中列为"待迁移"的 18 个入口。

## 2. 总线/仲裁器设计审查

### 2.1 整体评估：设计合理，核心边界守住了

`PageObservation → ActionIntent → InputCommand` 三层分离在代码里确实落实了，`ActionArbiter` 是唯一产生 `InputCommand` 的入口，`ConfirmationSession` 用显式状态机加帧序号单调校验挡住了旧帧覆盖新状态。`FrameDispatchBus` 用有界 channel + `TryPublish` 非阻塞写入，采集侧不等待消费者，这是对 ERR-007（截图循环等待消费者）的正确修复。

### 2.2 残留问题与修复状态

1. **消费者线程卡死看门狗（已修复）。** `FrameDispatchBus` 每个实例只有一个消费者 `Task`（`Task.Run(RunConsumerAsync)`），如果 `ProcessFrameBusDeliveryAsync` 内部某次处理真的卡住不返回（例如某个 handler 调的原生 Win32 调用阻塞），channel 容量 4、`FullMode = DropOldest`，会导致：采集仍在跑、OCR 仍在跑，但新帧全部被静默丢弃，任务表现为"不再执行任何动作"。现有自愈机制（`GetOrCreateFrameDispatchBus` 检测 `_frameDispatchBus.IsCompleted` 后重建）只覆盖"消费者任务崩溃退出"的情况，**不覆盖"消费者任务卡住但没退出"的情况**，没有超时/心跳检测去强制回收卡死的消费者。
   - 文件：`Automation.Core/Dispatching/FrameDispatchBus.cs`、`Kes.Tasks/ChaosModeTask.FrameBus.cs`
   - 状态：**已修复（2026-10-17）**。总线保留 30 秒消费者看门狗；超时只替换当前 worker，下一帧自动重建，不再让该 consumer 永久失效。`TimedOutConsumerIsReplacedForLaterFrames` 已覆盖。

2. **`_lastSequence` 跨线程写入竞态（已修复）。** 采集线程（`RunFrameBusOnceAsync`）和消费者线程（`ProcessFrameBusDeliveryAsync`）都会写 `ChaosModeTask._lastSequence`，没有锁也没有 `Interlocked`。存在理论上的"丢失更新"场景：消费者线程基于一个过期的旧值算出 `Math.Max` 后覆盖掉采集线程刚写入的更大序号，导致 `_lastSequence` 短暂回退。实际后果大概率是重复抓取/重复投递同一帧（会被 `TryPublish` 的去重逻辑挡住），但这是一处真实的、没有同步保护的跨线程共享字段写入。
   - 文件：`Kes.Tasks/ChaosModeTask.cs`（字段声明）、`Kes.Tasks/ChaosModeTask.FrameBus.cs`（两处写入点）
   - 状态：**已修复（2026-10-14）**。`_lastSequence` 改由 `Volatile.Read` + `Interlocked.CompareExchange` 单调推进；`AdvanceSequenceNeverRegressesUnderConcurrentWriters` 已覆盖。

3. **`PageActionSession` 的自动重试策略导致重复点击（已修复 2026-10-17）。** RB-22 曾把高置信度布局指纹变化和 800 ms 超时作为同路由重试许可，但 OCR 抖动会让每个新帧看起来都像“页面状态变化”，事件消费者又会继续重试已选选项，最终对休息、事件、跳过、继续和商店刷新发送同一动作多次。现已移除时间与 OCR 指纹自动授权；同一页面阶段只允许一次动作，多步流程必须通过确认状态机、装备确认阶段键或 `BattleEventFlow` 显式推进。
   - 文件：`Automation.Core/Arbitration/PageActionSession.cs`、`Automation.Core/Arbitration/ActionArbiter.cs`、`Kes.Tasks/ChaosModeTask.cs`
   - 回归：`PageActionDoesNotRetryStablePageAfterCapturedTimeouts`、`PageActionIgnoresOcrLayoutJitterOnTheSameRoute`、`PageActionGateSuppressesSameRouteUntilRouteChanges`

## 3. 测试覆盖审查（2026-10-14，针对业务逻辑）

> 范围：`Automation.Core.Tests` 下与 ChaosMode 相关的测试文件（`ChaosModeTaskTests.cs`、`ChaosNormalFlowTests.cs`、`ChaosSpecialPageMigrationTests.cs`、`ChaosModeTaskFrameBusTests.cs`、`ChaosModeTaskScreenshotFrameBusTests.cs`、`ChaosModeConfigTests.cs`）。统计方式：列出全部测试方法名，再针对本文档第 1 节已记录的差异点逐一搜索对应测试，确认有没有被覆盖、覆盖的方向是否正确。

### 3.1 AND 条件回归测试已恢复 Python 语义

- `ChaosNormalFlowTests` 的 `RestClicksFreeRestAndClearsFlag` 验证“休息特征 + 免费文字 + 业务门闩”三者齐全时点击休息。
- `RestDoesNotClickWhenFreeTextMissing`、`RestDoesNotClickFromFreeTextWhenRestFeatureMissing` 验证缺少任一前置条件时不点击，回归测试方向与 Python 一致。

### 3.2 本文档记录的差异点，测试覆盖情况逐项核对

| 差异点（对应第1节编号） | 是否有专门测试覆盖 | 说明 |
|---|---|---|
| 1.1 `canRest` 条件 | 有 | `RestDoesNotClickWhenFreeTextMissing` 验证缺少“免费”文字时不休息。 |
| 1.2 `ResetLayer()` 保留状态 | 有 | `ResetLayerPreservesProgressCounters` 现在同时断言 `MeditationCards` 与 `PendingRemovedCardCount` 保留。 |
| 1.3 装备传说互斥 | 有 | `EquipmentDecisionRejectsSecondLegendaryForSameMember` 覆盖拒绝与允许两种分支。 |
| 1.4 商店删牌上限口径 | 有，复核为原审查误判 | `ThreeChoiceAndShopRemovalKeepIndependentCounters` 验证商店删牌与三选一删牌计数互不影响。 |
| 1.5 路线优先级“商店”排序 | 有，复核为原审查误判 | `RouteSelectionHonorsShopOnlyWhenItAppearsInPriorityList` 覆盖列表内/列表外两种行为。 |
| 1.6 Boss 前自动暂停 | 有 | `RouteSelectionPausesBeforeConfiguredBossLayer` 验证暂停请求只发一次，恢复后可继续点击 Boss。 |
| 1.7 首层刷特定闪光链路 | 有 | `FlashEffectChoosesCardByFlashPriority` 验证命中后写标记；`ChaosRewardSettlementExitsWhenSpecificFlashMissing` 验证未命中时退出重刷。 |
| 1.8 掷骰/谈判失败精确匹配 | 有 | `NegotiationClicksSkip`、`NegotiationDoesNotClickWithoutFailureText`、`NegotiationDoesNotClickForOtherFailureText` 覆盖正负路径。 |

### 3.3 其他补充覆盖与残留风险

1. **`PageActionSession` 的重复点击边界已补测试（2026-10-17）。** 自动时间重试和 OCR 指纹重试均已移除；新增测试覆盖超时后不重试、OCR 布局抖动不重试，同时保留页面阶段变化后执行新动作的覆盖。
2. **消费者线程卡死看门狗已补失败注入测试。** `FrameDispatchBusTests.TimedOutConsumerIsReplacedForLaterFrames` 模拟消费者挂起，验证超时后下一帧仍可恢复处理。
3. **`_lastSequence` 并发推进已补测试。** `AdvanceSequenceNeverRegressesUnderConcurrentWriters` 验证多线程写入后序号保持最大值。
4. **商店/三选一混合来源已有独立计数测试。** `ThreeChoiceAndShopRemovalKeepIndependentCounters` 覆盖两类计数互不影响。
5. **“首层刷特定闪光”已补链路测试。** `ChaosRewardSettlementExitsWhenSpecificFlashMissing` 验证配置开启、未命中闪光时优先退出重刷；奖励开启时的结算页点击也有 `ChaosRewardSettlementClicksClaimWhenValidationRewardEnabled` 覆盖。

## 6. 新增功能：事项截图归档（2026-10-14 实施）

> 本节记录一次真正落地的代码改动（不是审查记录），供后续核对改动范围和设计意图。

### 6.1 背景

用户反馈现有的开发调试截图功能（`DecisionSnapshotService`，标注渲染框、文字说明、建议 ROI）太复杂看不懂，希望有一个更简单的功能：任务触发任意下游处理器时，截取一张纯净的原始画面+OCR文本，按事项分文件夹归档，去重，每类最多保留 20 张，用于后期人工分析。要求不侵入主业务流程，仅作为开发测试功能。

讨论过程中一度误删了用户要保留的手动 1:1 预览窗口标注功能（`DeveloperPreviewWindow` 里"选择当前画面类型，生成标注样本"按钮），已确认恢复。**最终方案是两套机制并存**：
- 旧机制（`DecisionSnapshotService`/`DecisionSnapshotCatalog`/`DecisionSnapshotModels`/`DecisionSnapshotJson`）：手动触发，1:1 预览窗口里选择页面类型后生成带标注框的样本，保留不变。
- 新机制（`RouteImageArchiver`）：自动触发，任务运行时在路由分发点自动截图，不做任何标注渲染。

### 6.2 新增文件

- `Automation.Core/DeveloperTools/IRouteImageArchiver.cs`：接口定义。`TryCapture(eventId, frame, ocr, fingerprint, force)`。
- `Kes.Gui/DeveloperTools/RouteImageArchiver.cs`：实现类。内部维护 `Dictionary<string, HashSet<string>>`（每个 eventId 的已见指纹集合）和 `Dictionary<string, int>`（每个 eventId 已保存数量），用页面指纹（`PageObservation.Fingerprint.Value`，即 OCR 文本归一化后的 SHA256）去重，每个 eventId 最多保存 20 张。输出路径：`{程序运行目录}/image/{eventId}/frame-{序号:000}.png` + 同名 `.txt`（OCR 文本和逐行坐标/置信度）。

### 6.3 挂载点（不侵入业务 handler 本体）

- `Kes.Tasks/ChaosModeTask.cs` 新增私有静态方法 `ArchiveEventSample(page, eventId, force)`，内部读取 `page.Context.RouteImageArchiver`，调用失败吞掉异常，不影响主流程。
- 挂载位置：`HandlePageAsync` 方法内，在 `HandleClosePageAsync`/`HandleResumeTargetMemberArchiveAsync`/`HandleSaveTargetMemberAsync` 命中后各加一行；在路由匹配循环里，`route.HandleAsync` 调用前加一行（覆盖 `UseActionBoundary` 为真和为假两种分支）。
- `ChaosModeTask.FrameBus.cs` 的 `ProcessFrameBusDeliveryAsync` 里也加了一行，覆盖帧总线模式下的特殊消费者（主战员存档类）。
- 由于 `HandlePageAsync` 本身在帧总线模式下也会被 `ProcessFrameBusDeliveryAsync` 以 `preferredRouteId` 方式再次调用，同一事项可能被尝试归档两次，但两次用的是同一个页面指纹，去重机制会挡住重复写盘，不会产生重复文件，不是 bug。
- ERR-005（固定坐标点击拦截，`HandleRecognitionFailureAsync` 内）和 ERR-008（闪光效果页未识别到卡牌）这两处原本调用旧 `CaptureDecisionSnapshotAsync` 并传 `force: true` 的安全诊断逻辑，改为调用 `ArchiveEventSample(page, eventId, force: true)`，绕过开发者开关，保证这两个安全兜底场景在归档功能关闭时也能留存现场（这是用户此前在同一会话里确认过要保留的 ERR-005 行为，这次延续到了新机制上）。

### 6.4 UI 入口

`MainWindow.xaml` 新增一张独立卡片"事项截图归档"（与原"判断点标注样本"卡片并列），绑定 `IsRouteImageArchiveEnabled` 开关、`RouteImageArchiveOutputDirectory` 路径展示、`OpenRouteImageArchiveFolderCommand` 打开目录按钮。`MainWindowViewModel.cs` 新增 `RouteImageArchiver` 属性（构造时指向 `{程序运行目录}/image`），与原有 `DecisionSnapshots` 属性并存，互不影响。

### 6.5 数据流入口

`TaskContext` 构造函数新增 `routeImageArchiver` 参数（放在参数列表最后，默认 `null`，不影响任何现有位置参数调用点）；`SessionViewModel` 构造函数新增同名参数并透传给 `TaskContext`；`MainWindowViewModel` 创建 `SessionViewModel` 时传入 `routeImageArchiver: RouteImageArchiver`。

### 6.6 已核实不受影响的范围

- 全部 5 处测试文件里的 `new TaskContext(...)` 调用、1 处 `new SessionViewModel(...)` 调用，均使用具名参数或参数数量未覆盖到新增参数位置，无需修改。
- 旧机制的全部字段、方法、UI 绑定、`DeveloperPreviewWindow` 手动标注按钮均完整保留，未被这次改动触碰业务逻辑。

### 6.7 未验证事项

- 本节记录归档功能落地时，曾因公司代理阻断 NuGet 无法完成编译验证；2026-10-14 本机恢复后已补跑 `dotnet build AutomationPlatform.slnx --no-restore`（0 警告、0 错误）和 `dotnet test AutomationPlatform.slnx --no-restore --no-build`（209/209 通过）。
- 没有新增任何单元测试覆盖 `RouteImageArchiver` 的去重/上限逻辑，如果需要测试覆盖需要另行安排。

## 4. 处理结果清单

1. `ResetLayer()`：**已修复**，保留冥想状态与待处理移除计数，并有回归测试。
2. `HandleRestAsync`：**已修复**，恢复“休息特征 + 免费文字 + 业务门闩”的 AND 条件。
3. 装备传说互斥：**已修复**，拒绝同一主战员安装第二件传说装备。
4. 消费者看门狗：**已修复（2026-10-17 修订）**，消费者超时只替换故障 worker，后续帧可以自动恢复。
5. `_lastSequence` 并发推进：**已修复**，改为原子单调推进。
6. 商店删牌口径：**原审查误判**，Python 与 C# 都按商店删牌数限制，当前实现正确。
7. 路线优先级“商店”：**原审查误判**，Python 与 C# 都支持“列表有则按排名，列表无则固定最优先”。
8. Boss 前自动暂停：**已修复**，已接入真实暂停请求与会话状态。
9. 首层刷特定闪光：**已修复**，补齐标记、奖励结算/领取处理和退出重刷链路。
10. 掷骰/谈判失败误触发：**已修复并补测**，改为精确匹配“失败”，其他“XX失败”不再触发。


## 7. 2026-10-17 静态审查后续修复

本轮针对帧总线与开发样本归档完成以下修订：

1. `ChaosModeTask` 的不同消费者仍可并行接收总线投递，但业务状态和输入动作统一经过串行执行门。旧帧在进入 `ActionArbiter` 和 `ChaosRunState` 前按帧序号丢弃，避免共享状态并发修改。
2. 帧总线消费者超时后不再永久拒绝后续帧，而是在下一次投递时重建对应 worker。
3. 超时、停止等未消费队列路径会释放实现 `IDisposable` 的 Payload；`ChaosModeTask` 已接入会话停止时的异步释放。
4. 派发样本不再按 `RunId|ConsumerId` 每轮只保存一张，而是按图片哈希去重，每个消费者累计最多 20 张完整 PNG/JSON 样本。
5. 样本队列满时返回未接收，调用方保留最后归档帧序号并在下一帧重试；后台写盘失败会通过事件进入开发日志。
6. PNG 与 JSON 先写临时文件，再移动到最终路径；失败时清理临时文件和已移动的 PNG，只统计成对样本。
7. 总线消费者入口不再因 `ObservePage` 的 `SuppressDuplicate` 提前返回；事件消费者按“识别失败等待轮数”进行帧序号有界重试，避免同一路由被永久锁死。
8. 通用 `confirm` 与正式 `close-page` 路由已接入同一有界动作重试；未变化页面不会立即重复点击，达到配置轮数后最多尝试 3 次。`close-page` 使用 `IsPriorityOverlay` 在总线路由层优先于主战员采集，既不再绕过动作会话，也不会被采集流程抢占。

刻印日志迁移仍明确不在本次范围内；本次没有修改刻印、面具或赛季逻辑。
