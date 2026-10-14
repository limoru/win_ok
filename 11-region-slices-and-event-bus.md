# 11. 区域切片与事件总线重构方案

> 目标：把当前“全屏 OCR 后按页面顺序尝试处理器，并在每个处理器里各自处理点击和确认”的执行模型，逐步迁移为“全屏帧统一采集、区域切片并行识别、事件总线发布事实、仲裁器统一执行动作”的模型。
>
> 本文只记录重构方案，不要求一次性重写所有任务逻辑。

## 实现状态

截至 2026-10-13，第一阶段和 `ChaosModeTask` 的页面路由迁移已经完成：

- `FullFrameOcrAnalyzer`：同一会话、同一 `FrameSequence` 最多执行一次全图 OCR。
- `SessionEventBus`：按会话发布事件，后续区域检测器可直接接入。
- `PageObservation` / `PageFingerprint`：把全图 OCR 转换为可比较的页面观察，不依赖页面名称。
- `ActionIntent`：页面只表达“跳过当前内容”或“请求中心确认”等业务意图。
- `ActionArbiter`：统一执行旧帧淘汰、同帧去重、跨页面确认挂起、页面级单次动作和单动作仲裁。
- `ConfirmationSession`：统一管理中心确认、来源页面返回、页面退出确认和重试。
- `FrameRouter<TFrame>`：在一帧观察结果上按优先级匹配唯一页面任务；命中后只调用该任务，即使任务不产生动作，也不会再回退顺序处理器。
- `ChaosModeTask` 的全部页面处理器已经注册到路由；旧的 `PageHandlers` 顺序兜底已删除。
- 新增帧级 `ChaosModeFrameClassification`：同一帧内缓存 OCR 点查询、区域文本框、特征匹配、事件选项、路线节点和卡牌识别。路由判断与实际动作共用同一份结果，避免重复识别。
- 路线、事件、商店、休息、卡牌、装备、命运、结算和通用确认都已按“分类 -> 唯一路由 -> 产生动作 -> 下一帧确认”执行。
- `ClickPointAsync` 和页面处理器不再固定等待；`复制主战员` 改为每帧只点一个候选，探险结果增加同页单次结算门闩。
- 路线选择点击后不再等待 1 秒或 2 秒，直接由下一帧反馈决定后续动作。

当前仍使用全图 OCR。区域切片尚未启用，后续只需让区域检测器发布同类型观察事件，不需要修改确认状态机。未知画面仍由全图 OCR 分类失败后进入未处理统计，不再回退执行所有旧处理器。
## 1. 背景

当前页面处理链的主要问题是执行时序不统一：

- 每次循环先对全屏做一次 OCR，再由页面处理器按固定顺序尝试。
- 页面处理器既能识别页面，也能直接点击，还会保存页面自己的临时状态。
- “跳过 -> 中心确认 -> 返回原页面”这类流程没有统一的状态跟踪。
- 当确认点击后页面尚未切换，新的循环仍可能再次识别到原页面并重复触发跳过。
- 同一个中心确认按钮，被不同页面处理器用不同方式判断和点击。
- 全屏 OCR 每轮都参与所有页面处理，延迟和资源占用较高。
- 页面处理器的局部布尔状态无法可靠表达跨帧、跨页面的操作意图。

这类问题的根因不是某个按钮坐标，而是缺少一个统一的帧序号、观察事件、操作意图和动作仲裁层。

## 2. 设计目标

1. 每个自动化会话只有一个持续采集循环。
2. 全屏截图始终是唯一事实源，区域切片不得额外调用截图接口。
3. 每次采集都有单调递增的 `FrameSequence`。
4. 区域切片从全屏帧派生，检测服务只处理自己关心的区域。
5. 视觉检测只发布观察事件，不直接点击。
6. 业务处理器只发布操作意图，不直接抢输入。
7. 一个会话同一时刻最多执行一个输入动作。
8. 中心确认、跳过确认、结算确认等统一进入同一个动作仲裁器。
9. 点击确认后，必须由更新帧证明源页面已经离开，才能重新接受同一类意图。
10. 全屏 OCR 保留，但降级为兜底路径，不再是每个页面的默认热路径。
11. 所有事件、决策和动作都能按 `FrameSequence` 回放和测试。
12. 迁移过程保持可运行，允许旧处理器和新总线适配器短期并存。

## 3. 非目标

- 不在第一阶段重写所有 `ChaosModeTask` 页面处理器。
- 不让每个检测服务自己截取一张图。
- 不把“确认”逻辑继续拆成卡牌专用、休息专用、事件专用版本。
- 不把页面临时状态继续写进 `ChaosRunState` 之类的业务全局状态。
- 不在本轮引入用户可编辑的区域编辑器。区域定义先由代码或布局配置维护。
- 不要求一次性删除现有全屏 OCR。它先作为兜底和对照。
- 不改变任务配置界面和任务方案结构。

## 4. 核心原则

### 4.1 帧是时间轴

所有观察必须携带产生它的帧序号：

```text
FrameSequence 101 -> RegionSlice(center-confirm) -> CenterDialogDetected(seq=101)
FrameSequence 101 -> RegionSlice(card-list)      -> CardRewardDetected(seq=101)
FrameSequence 101 -> SkipRequested(seq=101)
```

任何序号小于当前会话已处理帧的事件都视为过期事件并丢弃。

### 4.2 观察、意图、命令分离

```text
观察 Observation
  视觉层确认画面里有什么。例如：检测到中心确认弹窗、检测到跳过按钮。

意图 Intent
  业务层想要做什么。例如：本页需要跳过，或者选择了某张卡。

命令 Command
  仲裁器最终允许发给目标窗口的输入。例如：点击相对坐标 (0.661, 0.631)。
```

页面处理器不能越过仲裁器直接发送输入。检测服务也不能根据业务规则直接发输入。

### 4.3 一个确认协议服务所有页面

“跳过确认”只是触发来源，确认动作本身没有卡牌、休息、事件或结算语义。统一流程为：

```text
业务意图 -> 仲裁器登记意图 -> 等待确认弹窗观察
-> 发确认命令 -> 等待源页面离开 -> 成功或失败
```

这样不会再出现卡牌页一套确认状态、休息页另一套确认状态。

## 5. 总体架构

```text
Target Capture Loop
    |
    v
FrameCaptured(sessionId, frameSequence, timestamp, fullFrame)
    |
    v
Region Slicer
    |
    +--> RegionFrameReady(center-confirm)
    +--> RegionFrameReady(bottom-right-actions)
    +--> RegionFrameReady(card-list)
    +--> RegionFrameReady(event-options)
    +--> RegionFrameReady(settlement)
    +--> RegionFrameReady(shop)
    |
    v
Detector Services
    |
    +--> CenterDialogDetected
    +--> SkipButtonDetected
    +--> CardRewardDetected
    +--> EventOptionsDetected
    +--> EquipmentChoiceDetected
    +--> FlashBranchDetected
    +--> BattleSettlementDetected
    +--> PageChanged / PageGone
    |
    v
Action Arbiter
    |
    +--> 确认状态机
    +--> 单帧单动作门控
    +--> 冷却、重试、超时
    +--> 优先级仲裁
    |
    v
Input Command
    |
    v
SessionTaskCoordinator / Input Gateway
```

## 6. 核心数据模型

### 6.1 FrameCaptured

```csharp
public sealed record FrameCaptured(
    string SessionId,
    long FrameSequence,
    DateTimeOffset CapturedAt,
    int Width,
    int Height,
    string PixelFormat);
```

实际帧数据继续通过帧租约或只读像素缓冲提供。事件本身不长期持有可释放的 `FrameLease`，检测服务需要的像素应在租约有效期内复制或完成处理。

### 6.2 RegionDefinition

区域定义是可复用的声明数据，不属于某个页面处理器：

```csharp
public sealed record RegionDefinition(
    string RegionId,
    string Purpose,
    RelativeRect Bounds,
    SamplingPolicy Sampling,
    OcrHints OcrHints,
    string? PageId);
```

建议属性：

- `RegionId`：稳定标识，例如 `center-dialog.confirm`。
- `Purpose`：给开发和日志使用，例如“中心弹窗确认按钮”。
- `Bounds`：相对全屏坐标，坐标为 `0..1`。
- `Sampling`：每帧、按间隔、仅在指定页面激活等。
- `OcrHints`：预期文字、语言、简化/繁体映射和预处理提示。
- `PageId`：可选的页面作用域。全局区域可以为空。

第一阶段区域定义放在 `Kes.Layout.WindowsPc` 或对应布局项目中。后续可导出为 JSON，但不需要马上做编辑器。

### 6.3 RegionSlice

```csharp
public sealed record RegionSlice(
    string SessionId,
    long FrameSequence,
    string RegionId,
    string Purpose,
    RelativeRect SourceBounds,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    ReadOnlyMemory<byte> Pixels);
```

约束：

- 切片来自同一张全屏帧。
- 保留源区域坐标，检测结果可映射回全屏相对坐标。
- OCR 返回的区域框必须能换算到全屏坐标，否则仲裁器无法点击。
- 如果切片只用于识别，不保留到下一帧。
- 不因为新增区域定义而增加截图次数。

### 6.4 观察事件

所有观察事件至少包含：

```csharp
string SessionId
long FrameSequence
string DetectorId
string? RegionId
DateTimeOffset ObservedAt
double Confidence
IReadOnlyList<TextLine> OcrEvidence
```

示例：

```text
CenterDialogDetected
SkipButtonDetected
PageChanged
PageGone
BattleSettlementDetected
EventOptionsDetected
CardRewardDetected
EquipmentChoiceDetected
FlashBranchDetected
```

### 6.5 意图事件

业务层把观察和配置规则组合为意图：

```text
SkipRequested
ConfirmRequested
ContinueRequested
SelectOptionRequested
SelectCardRequested
OpenChoiceRequested
```

意图必须包含：

- 来源页面或来源处理器。
- 源帧序号。
- 期望目标，例如“点击确认”“跳过当前奖励”。
- 业务原因，例如“卡牌不在优先级中”。
- 冷却键，例如 `card-assign.skip`。
- 可选的预期页面迁移。

### 6.6 动作命令

只有仲裁器可以创建动作命令：

```text
ClickRelativeCommand
ClickTextCommand
SwipeCommand
KeyCommand
WaitCommand
```

命令必须记录：

- 被哪条意图授权。
- 使用哪一帧的观察作为依据。
- 目标相对坐标或文字锚点。
- 是否进入“等待页面变化”状态。
- 成功发送和实际调度的区别。

`PostMessage` 返回成功只代表消息已发送，绝不能当作游戏已经接受操作。

## 7. 区域切片

### 7.1 切片策略

第一版建议优先建立这些高频区域：

- 中心弹窗和中心确认。
- 右下角跳过、转换、继续等动作。
- 卡牌奖励和卡牌分配。
- 事件选项。
- 装备选择。
- 闪光分支和重新闪光。
- 战斗结算。
- 商店和休息入口。

区域切片可以由检测服务按需请求，但必须由切片器从当前全屏帧派生。推荐模式：

```text
FrameCaptured
  -> RegionSlicer 检查本帧激活的区域定义
  -> 生成 RegionFrameReady
  -> 对应检测服务消费
```

### 7.2 页面相关区域

区域不一定每帧都需要识别。支持以下采样策略：

- `EveryFrame`：中心确认、右下角跳过等短生命周期控件。
- `WhenPageActive`：卡牌列表、装备选择、商店等只在页面状态命中后启用。
- `Interval`：低变化页面或昂贵模板匹配。
- `OnDemand`：由仲裁器需要在指定帧上显式触发。

### 7.3 与页面处理器的关系

页面处理器可以保留“业务决策”，但不再直接点击：

```text
旧模式：
AnalyzeFullFrame -> Handler -> ClickSkip -> Handler -> ClickConfirm

新模式：
RegionDetector -> SkipButtonDetected
BusinessPolicy -> SkipRequested
ActionArbiter -> ClickSkipCommand
CenterDialogDetector -> CenterDialogDetected
ActionArbiter -> ClickConfirmCommand
PageStateService -> PageGone
ActionArbiter -> ConfirmedPageExited
```

## 8. 事件总线

### 8.1 作用域

总线按会话创建，避免不同窗口之间共享事件：

```csharp
ISessionEventBus
```

建议使用有界通道：

- `FrameCaptured` 采用最新帧覆盖策略，旧帧可以丢弃。
- 区域观察事件按 `FrameSequence` 保留短窗口，防止检测延迟导致乱序。
- 输入命令不能无界排队，必须经过输入门控。
- 队列满时记录日志和指标，不能静默阻塞采集线程。

### 8.2 发布与订阅

```csharp
public interface ISessionEventBus
{
    void Publish<TEvent>(TEvent @event) where TEvent : ISessionEvent;
    IAsyncEnumerable<TEvent> Subscribe<TEvent>(CancellationToken ct) where TEvent : ISessionEvent;
}
```

实际实现可以使用 `System.Threading.Channels`。一个会话的高频图像事件和低频业务事件可以有不同通道，但必须保持帧序号语义一致。

### 8.3 顺序

事件可能并行产生，但仲裁器按以下顺序决策：

1. 明确页面状态和确认弹窗事实。
2. 高优先级安全动作。
3. 页面业务意图。
4. 通用按钮。
5. 全屏 OCR 兜底产生的低置信度观察。

当同一帧出现多个候选动作时，仲裁器只选择一个。其余候选记录为 `SuppressedByPriority`，不直接丢弃证据。

## 9. 检测服务

### 9.1 接口

```csharp
public interface IRegionDetector
{
    string DetectorId { get; }
    IReadOnlySet<string> InputRegionIds { get; }
    ValueTask DetectAsync(RegionSlice slice, ISessionEventBus bus, CancellationToken ct);
}
```

检测服务只负责把像素变成观察，不负责点击，也不读取任务配置中的业务优先级。

### 9.2 中心确认检测器

中心确认检测器只识别：

- 是否出现中心确认弹窗。
- 是否识别到确认按钮。
- 按钮是否可用。
- 确认按钮在全屏中的相对坐标。
- 弹窗标题、正文和确认文字证据。
- 识别置信度。

它发布：

```text
CenterDialogDetected
CenterConfirmButtonDetected
CenterConfirmButtonUnavailable
```

它不决定要点击确认的是卡牌、休息还是事件。

### 9.3 页面变化检测

页面变化不能只靠“文字还在不在”判断。建议为关键页面生成页面签名：

```text
PageSignature = 页面 ID + 稳定区域观察摘要 + 配置版本
```

当确认点击后，仲裁器订阅：

- `PageGone(originPageId, frameSequence)`
- `PageChanged(originPageId, newPageId, frameSequence)`
- `CenterDialogDetected(frameSequence)`

只有更新的帧证明源页面已经离开，确认才算成功。

## 10. 动作仲裁器与确认状态机

### 10.1 职责

`ActionArbiter` 是会话内唯一的动作决策入口：

- 消费观察事件和意图事件。
- 按帧序号淘汰过期事件。
- 同一帧最多授权一个动作。
- 统一执行冷却、去重、重试和超时。
- 维护确认流程状态。
- 记录每次被抑制的动作及原因。
- 不包含卡牌、休息、事件等页面专属业务词。

### 10.2 确认状态

建议状态：

```text
Idle
IntentRequested
AwaitingConfirmation
ConfirmClicked
AwaitingPageExit
ConfirmedPageExited
ConfirmationFailed
```

### 10.3 状态迁移

```text
Idle
  + SkipRequested(originPageId, seq)
  -> IntentRequested

IntentRequested
  + CenterDialogDetected(seq > intentSeq)
  -> AwaitingConfirmation

AwaitingConfirmation
  + 确认按钮可用且属于当前意图
  -> ConfirmClicked
     -> 发送一次确认命令

ConfirmClicked
  -> AwaitingPageExit
     记录 originPageId、intentSeq、confirmSeq

AwaitingPageExit
  + 更新帧显示 originPageId 已消失
  -> ConfirmedPageExited

AwaitingPageExit
  + 新帧仍显示同一个确认弹窗
  -> 在重试策略允许时再次点击确认

AwaitingPageExit
  + originPageId 再次出现但弹窗消失
  -> ConfirmationFailed，并进入冷却

AwaitingPageExit
  + 超时
  -> ConfirmationFailed
```

### 10.4 防止重复循环

必须执行以下约束：

1. 同一个 `FrameSequence` 的 `SkipRequested` 最多产生一次跳过命令。
2. 进入 `AwaitingPageExit` 后，不再接受相同 `cooldownKey` 的新跳过意图。
3. 确认命令发送后，不因原页面仍短暂存在而立即重新执行跳过。
4. 只有 `PageGone`、`PageChanged` 或确认弹窗消失且源页面离开，才结束确认会话。
5. 原页面重新出现时，必须先经过冷却和新的完整页面观察，不能直接重新点击。
6. 重试只允许重试“确认”动作，不允许重新点击被确认对话框遮住的底层跳过按钮。
7. 超时和失败必须写入结构化日志，记录帧序号、页面、区域、检测器和动作。

### 10.5 优先级示例

同一帧同时出现以下情况时：

```text
CenterDialogDetected(center-confirm)
SkipButtonDetected(bottom-right)
```

优先处理中心确认。因为中心弹窗是更具体的模态状态，底层跳过按钮可能是旧页面残留。

## 11. 全屏 OCR 兜底

全屏 OCR 保留为兜底，不删除：

- 区域检测器都没有命中。
- 页面状态未知。
- 关键区域置信度不足，仲裁器需要额外证据。
- 新页面尚未配置区域。
- 区域定义版本升级后的对照验证。

兜底流程：

```text
RegionDetectors 未产生可决策事件
  -> FullFrameOcrFallback 按限流策略执行
  -> FullFrameOcrCompleted(seq, lines)
  -> 所有订阅者重新检查该帧
  -> 仍无动作则记录 UnknownPage
```

限流要求：

- 同一帧最多执行一次全屏 OCR。
- 同一会话的兜底 OCR 只在冷却时间后再次执行。
- 全屏 OCR 和区域 OCR 都经过 `VisionScheduler`。
- 记录“是否命中兜底”和延迟，避免兜底变成新的每帧默认路径。

## 12. 与现有架构的关系

### 12.1 Automation.Core

新增或扩展以下概念：

```text
Frames/
  FrameCaptured
  FrameSequence
Regions/
  RegionDefinition
  RegionSlice
  RegionSlicer
Events/
  ISessionEventBus
  SessionEventBus
  ObservationEvents
  IntentEvents
Arbitration/
  ActionArbiter
  ConfirmationSession
  ActionCommand
```

### 12.2 VisionScheduler

继续作为 OCR 和模板匹配的并发门控。区域检测器只能通过它提交视觉任务，不能绕过调度器直接并发调用原生模型。

### 12.3 TaskContext

短期保留现有 API，逐步增加只读的事件总线上下文。页面处理器可以通过 `TaskContext` 发布意图，但不能直接获得“任意点击”能力。

### 12.4 SessionTaskCoordinator

继续维护任务模块的启用、暂停和故障状态。输入发送最终由 `ActionArbiter` 和输入网关串行化，不能让每个页面处理器各自点击。

### 12.5 ChaosModeTask

`ChaosModeTask` 最终负责：

- 读取业务配置。
- 把区域观察组合成“本页应该做什么”的意图。
- 维护长期业务状态。
- 不负责通用确认状态机。
- 不维护 `CardAssignSkipRequested`、`RestSkipConfirmed` 这类页面专用确认布尔值。

## 13. 开发调试与可观测性

新增结构化事件日志，至少记录：

- `sessionId`
- `frameSequence`
- `regionId`
- `detectorId`
- `eventType`
- `confidence`
- `evidenceText`
- `intentId`
- `commandId`
- `suppressedReason`
- `latencyMs`

日志原则：

- 每个状态迁移记录一次，不在每帧重复刷屏。
- 点击命令记录“已授权”和“已发送”。
- `PostMessage` 成功不等于页面响应。
- 区域检测命中、兜底 OCR、确认超时等关键事件保留可回放字段。
- 开发测试功能仍可保存决策快照，但诊断逻辑不能被截图查看绑定。
- 不使用图片比对作为单元测试断言。

建议后续增加一个内存决策轨迹：

```text
seq 101  center-confirm  CenterDialogDetected
seq 101  card-assign     CardRewardDetected
seq 101  arbiter         SuppressedByPriority(bottom-right)
seq 102  arbiter         Command ClickConfirm
seq 104  page-state      PageGone(card-assign)
seq 104  arbiter         ConfirmedPageExited
```

## 14. 测试策略

测试不依赖真实游戏画面，只使用合成帧元数据、假 OCR 文本和假输入接收器。

必须覆盖：

1. 相同 `FrameSequence` 重复发布 `SkipRequested` 只产生一个命令。
2. 旧序号事件晚到后不会覆盖新页面状态。
3. 中心确认弹窗优先于底层跳过按钮。
4. 点击确认后，原页面短暂停留不会立即重新跳过。
5. 原页面离开后确认会话进入成功状态。
6. 原页面重新出现时进入失败或冷却，而不是无限循环。
7. 休息、卡牌、事件、结算使用同一个确认状态机。
8. 确认按钮不可用时不会发送点击命令。
9. `PostMessage` 返回成功但页面未变化时，不会被视为确认成功。
10. 区域坐标能正确映射回全屏坐标。
11. 同一帧只允许一个输入命令。
12. 全屏 OCR 兜底被限流，且不会与区域检测重复执行同一帧 OCR。

建议新增测试文件：

```text
Automation.Core.Tests/SessionEventBusTests.cs
Automation.Core.Tests/RegionSlicerTests.cs
Automation.Core.Tests/ActionArbiterTests.cs
Automation.Core.Tests/ConfirmationSessionTests.cs
Kes.Tasks.Tests/ChaosIntentMigrationTests.cs
```

## 15. 分阶段迁移

### 阶段 0：清除页面专用确认状态

- 删除卡牌专用跳过状态和处理器。
- 保留现有页面处理器行为，但不新增页面专用确认布尔值。
- 建立共享确认流程的测试用例，先使用假事件。

### 阶段 1：总线与帧序号基础设施

- 在 `Automation.Core` 增加会话事件总线。
- 增加 `FrameSequence` 和标准事件信封。
- 保持现有任务执行路径不变，只旁路发布采集事件。
- 验证旧逻辑不受影响。

### 阶段 2：区域切片基础设施

- 增加 `RegionDefinition`、`RegionSlice`、`RegionSlicer`。
- 先接入中心确认和右下角动作区域。
- 只发布观察事件并记录日志，不立即替代现有点击。
- 验证切片不增加截图次数。

### 阶段 3：统一中心确认

- 增加 `ActionArbiter` 和 `ConfirmationSession`。
- 把跳过确认迁移到共享协议。
- 休息、卡牌、事件、结算只发布意图，不实现自己的确认状态。
- 增加循环、超时、重试和旧帧测试。

### 阶段 4：迁移高频页面前置检测

- 迁移结算、卡牌奖励、事件选项、装备选择、闪光分支和商店休息入口。
- 保留全屏 OCR 兜底。
- 对比事件决策结果，确认与现有业务规则一致。

### 阶段 5：逐步缩回全屏 OCR

- 当区域检测覆盖稳定后，降低全屏 OCR 频率。
- 保留未知页面兜底。
- 删除重复的页面级识别和点击代码。

### 阶段 6：清理和指标收敛

- 移除临时适配器。
- 整理区域定义。
- 汇总区域命中率、OCR 延迟、确认成功率和兜底比例。
- 决定哪些区域需要继续拆分或合并。

## 16. 验收标准

重构完成后必须满足：

- 每个会话只有一个截图采集循环。
- 区域切片不会触发新的截图调用。
- 每个观察和意图都有 `FrameSequence`。
- 过期事件不会影响当前决策。
- 同一帧最多执行一个输入命令。
- 中心确认只有一个共享状态机。
- 卡牌、休息、事件、结算都不再维护自己的确认状态。
- 确认点击后必须等待新帧证明页面离开。
- 原页面反复出现时不会产生无限跳过循环。
- 全屏 OCR 是兜底路径，不是所有页面的默认热路径。
- 区域检测能降低平均 OCR 区域面积和延迟。
- 纯逻辑测试覆盖循环、超时、重试、过期事件和优先级。
- GUI 停止、暂停和故障状态不受事件总线影响。

## 17. 风险与待确认项

1. 区域定义放在布局项目还是独立配置文件。第一阶段建议代码或布局项目维护，稳定后再导出 JSON。
2. 页面签名如何生成。需要同时避免误判和过度依赖 OCR。
3. 区域检测的采样频率。中心确认必须高频，卡牌和装备可低频。
4. 多开时的视觉并发上限。必须通过 `VisionScheduler` 统一控制。
5. 总线队列满时的事件丢弃策略。帧可以丢，业务动作事件不能无记录丢弃。
6. 确认失败的恢复方式。默认进入冷却并上报活动状态，不自动无限重试。
7. 旧处理器与新总线的短期兼容边界。适配器必须只发布意图，不允许绕过仲裁器点击。
8. 区域定义与页面迁移的顺序。每迁移一个页面都必须有对应纯逻辑测试。

## 18. 结论

这次重构不是为某个页面补特判，而是建立统一的帧时间轴、区域观察、业务意图和动作仲裁协议。

中心确认只需要实现一次。卡牌、休息、事件、结算等页面只描述自己的业务意图，由仲裁器决定何时点击、是否等待、是否重试，以及何时根据新帧确认页面已经离开。
## 19. 当前实现补充：PageActionSession

2026-10-13 起，普通页面点击也进入统一动作所有权模型。

- `FrameRouter` 先解析唯一路由，再为会点击页面的路由申请 `PageActionSession`。
- 默认所有路由都使用动作边界，确认路由和具有独立有限多步状态机的 `copy-member` 明确豁免。
- 动作边界按唯一路由身份判断，不按原始像素或 OCR 指纹释放。动态数字、光标、动画和 OCR 抖动不会直接释放动作权。
- 同一路由仍然命中时，原动作会话继续占位，不会再次执行。
- 中心确认弹窗出现时，页面动作让出当前帧，由共享 `ConfirmationSession` 接管。
- 页面语义变化后，旧动作会话结束，新页面可以申请新的动作权。
- 同一源页面最多允许一次受时间限制的重试，默认等待 5 秒，不允许每帧无限重复。
- 路由处理器如果没有真正执行动作，应返回 `false`，仲裁器会取消尚未发送命令的会话。

执行顺序固定为：

```text
唯一路由
-> ObservePage
-> PageActionSession 仲裁
-> 路由处理器
-> MarkPageActionSent 或 CancelPageAction
-> 下一帧确认页面是否离开
```

页面专属布尔值只能表示业务状态，例如是否还能休息；不能替代动作所有权。
## 20. 当前实现补充：帧投递总线

2026-10-13 起，`ChaosModeTask` 的生产运行路径使用独立的帧投递总线，截图/OCR 和消费者动作不再串在同一个等待链里。

- 采集侧只负责取新帧、执行全图 OCR、生成 `PageObservation`，然后按 OCR 和状态选择消费者。
- `FrameDispatchBus<TFrame>` 只负责投递，不等待消费者执行完成；消费者运行期间，采集循环仍会继续取新帧并 OCR。
- `FrameDelivery` 包含 `ConsumerId`、`TextSignature` 和原始页面观察。`TextSignature` 来自 OCR 指纹，不是图片像素比较。
- 同一个消费者 + 相同 OCR 文本在 `5` 秒内不会重复投递；OCR 文本变化时立即重新判定并投递；超过等待窗口后允许重投。
- 总线下发后由对应消费者执行路由处理，消费者失败或未处理不会阻塞采集；诊断日志保留失败消费者、帧号和原因。
- 总线使用有界队列，消费者落后时保留最新帧；队列满时丢弃旧帧，避免截图循环被历史画面拖住。
- 生产环境由 `TaskModuleCatalog` 启用帧投递总线；单元测试仍可使用同步入口直接验证单个路由逻辑。

这套边界解决的是“截图循环等待消费者”这一根因，而不是为某一个卡死页面继续增加页面专用特判。