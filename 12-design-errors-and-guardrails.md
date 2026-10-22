# 12. 设计错误与防复发清单

> 本文用于记录已经出现过的架构错误、错误产生的原因、正确的 C# 对象化方案，以及提交前必须检查的防复发规则。
>
> 这里记录的是设计和实现约束，不约束仓库中的测试数据、布局坐标或业务文案。

## ERR-001：把通用确认流程硬编码成页面专用状态

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** 行为硬编码、状态机缺失、职责放错层级

### 1. 错误表现

处理卡牌分配页面的跳过循环时，曾在 `ChaosRunState` 中加入：

```csharp
public bool CardAssignSkipRequested { get; set; }
public bool CardAssignSkipConfirmed { get; set; }
```

并增加卡牌页面专用的确认处理器，例如：

```csharp
HandleConfirmedCardAssignSkipAsync
```

这类实现试图通过“卡牌页面是否请求过跳过”和“卡牌页面是否已经确认”解决循环。

实际结果是：

- 卡牌页有自己的一套跳过确认状态。
- 休息页、事件页、结算页仍然可能复制另一套同类状态。
- 中心确认按钮这个通用动作被错误地绑定了卡牌业务语义。
- 页面判断、确认状态、点击操作和页面迁移观察混在一起。
- 同一帧或旧帧再次进入时，重复触发条件仍然可能成立。
- 修复了卡牌页面，不代表休息、事件和其他页面共用同一套正确逻辑。

这类修改看似解决了当前页面，实际上把问题复制到了状态字段和页面处理器中，没有修复共同根因。

### 2. 为什么这是硬编码

这里的“硬编码”不只是坐标写死，而是把通用行为写死到了某一个页面的名字上：

```text
通用行为：
跳过当前内容 -> 等待中心确认 -> 点击确认 -> 等待来源页面离开

错误硬编码：
卡牌分配页 -> CardAssignSkipRequested -> CardAssignSkipConfirmed
```

页面坐标、OCR 文案和区域范围可以是页面数据，因为这些内容天然与页面相关：

```text
WindowsPc 卡牌奖励区域
WindowsPc 中心确认区域
AndroidMobile 中心确认区域
```

但是下面这些是通用行为，不应该知道页面是卡牌、休息还是事件：

```text
请求确认
发送一次确认命令
等待页面退出
确认成功或失败
冷却和重试
同一帧去重
```

**判断规则：**

> 数据和区域可以页面化，通用行为不能页面化。

### 3. 根因

根因不是某个点击坐标不对，而是缺少统一的动作对象和确认生命周期对象。

旧实现仍然是过程式写法：

```text
全屏 OCR
-> 按页面处理器顺序 if 判断
-> 页面处理器自己保存临时布尔值
-> 页面处理器自己点击
-> 下一轮重新从全屏 OCR 开始
```

这套结构无法可靠表达：

- 一个操作从请求到确认完成的完整生命周期。
- 哪些事件属于同一个操作。
- 哪个帧序号之后才能认为操作完成。
- 页面还没切走时应该等待，而不是重新执行页面动作。
- 页面切走又回来后应该进入冷却，而不是无限循环。
- 不同页面如何复用同一个确认协议。

### 4. 正确方向

正确设计不是一个更长的卡牌判断，而是把通用流程对象化。

```text
页面策略对象只产生意图
-> ActionArbiter 接收意图
-> ConfirmationSession 管理确认生命周期
-> InputGateway 发送输入命令
-> PageState 根据新帧确认页面是否离开
```

### 5. C# 对象模型

#### 5.1 ActionIntent

意图不是页面专用布尔值，而是一个有身份和约束的对象。

```csharp
public abstract record ActionIntent(
    IntentId Id,
    PageId OriginPageId,
    long FrameSequence,
    CooldownKey CooldownKey,
    ExpectedPageTransition ExpectedTransition);

public sealed record SkipCurrentContentIntent(
    IntentId Id,
    PageId OriginPageId,
    long FrameSequence,
    CooldownKey CooldownKey,
    ExpectedPageTransition ExpectedTransition,
    string Reason)
    : ActionIntent(
        Id,
        OriginPageId,
        FrameSequence,
        CooldownKey,
        ExpectedTransition);
```

约束：

- `OriginPageId` 表示意图从哪里产生。
- `FrameSequence` 表示该意图依据哪一帧产生。
- `CooldownKey` 用于去重和防循环。
- `ExpectedPageTransition` 表示操作成功后预计页面如何变化。
- 页面策略不保存 `CardAssignSkipRequested` 这种临时布尔值。

#### 5.2 PagePolicy

页面策略只把观察和配置组合成意图：

```csharp
public interface IPagePolicy
{
    ValueTask<ActionIntent?> EvaluateAsync(
        PageObservation observation,
        IReadOnlyDictionary<string, object?> config,
        CancellationToken cancellationToken);
}
```

页面策略不得：

- 直接调用 `PostMessage` 或其他输入接口。
- 判断中心确认按钮是否可用。
- 管理确认重试次数。
- 记录“已经确认过”的页面专用布尔值。
- 根据页面名称决定是否绕过共享仲裁器。

#### 5.3 ConfirmationSession

确认流程必须由有生命周期和封装不变量的对象管理：

```csharp
public sealed class ConfirmationSession
{
    public IntentId IntentId { get; }
    public ConfirmationState State { get; private set; }
    public long SourceFrameSequence { get; }
    public long? ConfirmCommandFrameSequence { get; private set; }

    public ConfirmationDecision Observe(PageObservation observation);
    public void MarkConfirmCommandSent(long frameSequence);
    public void MarkTimedOut();
}
```

状态建议：

```text
Idle
IntentRequested
AwaitingConfirmation
ConfirmClicked
AwaitingPageExit
ConfirmedPageExited
ConfirmationFailed
```

该类负责保证：

- 一个确认会话最多发出一次对应的业务意图。
- 确认按钮尚未出现时只能等待。
- 确认命令发送后不能重新点击底层跳过按钮。
- 只有更新帧证明来源页面离开，才进入成功状态。
- 页面重新出现时进入失败或冷却。
- 超时、重试和失败原因可被测试和记录。

#### 5.4 ActionArbiter

仲裁器是共享行为的唯一入口：

```csharp
public sealed class ActionArbiter
{
    public ValueTask SubmitIntentAsync(ActionIntent intent, CancellationToken ct);
    public ValueTask SubmitObservationAsync(PageObservation observation, CancellationToken ct);
}
```

仲裁器负责：

- 淘汰过期帧事件。
- 同一帧最多授权一个输入命令。
- 合并重复意图。
- 选择共享确认会话。
- 执行冷却和重试。
- 发布结构化决策轨迹。
- 通过输入网关发送命令。

#### 5.5 InputCommand

输入命令是不可变值对象：

```csharp
public sealed record InputCommand(
    CommandId Id,
    IntentId IntentId,
    long EvidenceFrameSequence,
    InputCommandKind Kind,
    RelativePoint Target,
    CommandPurpose Purpose);
```

输入命令必须记录它由哪条意图和哪一帧观察授权。发送成功不是页面执行成功。

### 6. 错误示例与正确示例

#### 6.1 错误示例

```csharp
public bool CardAssignSkipRequested { get; set; }
public bool CardAssignSkipConfirmed { get; set; }

if (page.IsCardAssign)
{
    if (state.CardAssignSkipRequested && !state.CardAssignSkipConfirmed)
    {
        ClickConfirm();
        state.CardAssignSkipConfirmed = true;
    }
}
```

问题：

- 页面专用状态进入全局运行状态。
- 点击确认与卡牌页面耦合。
- 没有统一等待页面离开的协议。
- 休息、事件和结算无法复用。
- 很容易继续增加更多 `XxxRequested` 和 `XxxConfirmed`。

#### 6.2 正确示例

```csharp
var intent = new SkipCurrentContentIntent(
    Id: IntentId.New(),
    OriginPageId: observation.PageId,
    FrameSequence: observation.FrameSequence,
    CooldownKey: CooldownKeys.CardAssignSkip,
    ExpectedTransition: ExpectedPageTransition.Leave(observation.PageId),
    Reason: "当前卡牌不在优先级中");

await arbiter.SubmitIntentAsync(intent, cancellationToken);
```

仲裁器后续统一处理：

```text
SkipCurrentContentIntent
-> CenterDialogDetected
-> ConfirmClicked
-> AwaitingPageExit
-> ConfirmedPageExited
```

页面只需要说明为什么跳过，不需要知道如何确认。

### 7. 防复发规则

以下规则适用于后续所有自动化页面和任务修改。

#### 7.1 状态字段规则

禁止在 `ChaosRunState` 或类似长期状态对象中新增页面专用临时字段：

```text
XxxRequested
XxxConfirmed
XxxClicked
XxxWaiting
```

长期状态可以保存：

- 通关次数。
- 当前层数。
- 任务进度。
- 用户选择的长期配置。
- 跨局累计统计。
- 业务规则确实需要的持久状态。

不能保存：

- 某个页面当前是否等待确认。
- 某个弹窗是否点击过。
- 某个按钮本轮是否处理。
- 某个页面的跨帧点击状态。
- 可以通过帧序号和共享状态机表达的过程状态。

#### 7.2 页面处理器规则

页面处理器只能：

- 读取观察结果和业务配置。
- 返回业务意图。
- 提供日志上下文。
- 不直接发送点击、滑动和按键。
- 不自行判断通用中心确认。
- 不保存通用确认生命周期。
- 不为某一个页面复制通用重试或去重流程。

#### 7.3 通用行为提取规则

如果同一个行为开始出现在第二个页面，必须先停止复制，并检查是否可以建立共享对象。

例如：

```text
卡牌跳过确认
休息跳过确认
事件跳过确认
结算确认
```

应统一为：

```text
业务意图 -> ActionArbiter -> ConfirmationSession -> InputCommand
```

#### 7.4 帧和时序规则

任何动作都必须绑定：

- 输入依据的 `FrameSequence`。
- 操作来源的 `PageId`。
- 期望的页面变化。
- 冷却键。
- 超时策略。
- 成功判定条件。

没有这些信息的状态机无法证明操作完成，也不能可靠测试。

#### 7.5 C# 对象化规则

优先使用以下 C# 结构：

```text
record / sealed record        不可变事件、意图、命令和值对象
interface                     页面策略、检测服务、总线、输入网关
sealed class + state machine  确认会话、仲裁器、页面状态服务
enum / value object           状态、页标识、冷却键、命令类型
DI                             共享服务通过构造函数注入
```

避免：

```text
巨大静态工具类              容易把行为和页面逻辑混在一起
全局布尔字段                无法表达生命周期
字符串页面名称分支          难以约束和测试
页面处理器直接发送输入      绕过统一仲裁
匿名 object 或字符串事件    事件字段不清晰且无法编译期检查
```

### 8. 提交前检查清单

每次新增页面行为或修复自动化循环时，必须检查：

1. 这次修改是否只改了一个页面处理器，但解决的是通用时序问题。
2. 是否新增了 `XxxRequested`、`XxxConfirmed` 或类似布尔字段。
3. 页面处理器是否直接调用点击、滑动或按键。
4. 是否复制了中心确认、等待页面离开、重试或冷却逻辑。
5. 新状态是否有明确的拥有者、生命周期和入口。
6. 新动作是否绑定帧序号、来源页面和期望页面变化。
7. 同一帧是否可能发送两个动作。
8. 旧帧事件是否可能覆盖新页面状态。
9. 页面返回后是否可能立即重新执行同一业务意图。
10. 是否能使用假观察和假输入完成纯逻辑测试。
11. 是否能用同一个确认会话覆盖至少两个不同页面来源。
12. 是否把页面坐标、文字和区域留在布局数据中，而不是写进通用仲裁器。

### 9. 快速搜索命令

提交前可以搜索以下模式：

```powershell
rg -n "Requested|Confirmed|Clicked|Waiting" .\Kes.Tasks
rg -n "Click|PostMessage|SendInput|Mouse" .\Kes.Tasks
rg -n "CardAssign|Rest.*Confirm|Confirm.*CardAssign" .\Kes.Tasks
rg -n "中心确认|确认弹窗|跳过确认" .\Kes.Tasks
```

搜索结果不一定都是错误，但每一处都需要确认：

- 数据是否属于页面配置。
- 状态是否有明确的生命周期所有者。
- 行为是否应该进入共享服务。
- 输入是否绕过了仲裁器。

### 10. 必须覆盖的回归测试

至少保留以下纯逻辑测试：

1. 同一个 `FrameSequence` 重复提交跳过意图，只产生一次命令。
2. 确认弹窗出现前不会点击底层跳过按钮。
3. 确认命令发送后，源页面短暂停留不会再次跳过。
4. 只有更新帧证明源页面离开，确认才成功。
5. 源页面重新出现时进入冷却，不会无限循环。
6. 超时后不会无条件无限重试。
7. 同一确认会话可以服务卡牌、休息、事件和结算。
8. 旧帧事件不会影响新页面。
9. 同一帧最多一个输入命令。
10. 输入发送成功但页面未变化时，不会标记确认成功。

### 11. 结论

ERR-001 的修复原则不是继续增加卡牌页面的条件，而是彻底删除页面专用确认状态，把通用行为提取为：

```text
ActionIntent
    |
    v
ActionArbiter
    |
    v
ConfirmationSession
    |
    v
InputCommand
    |
    v
PageState verification
```

页面负责表达业务意图，通用对象负责时序、确认、去重、等待、重试和输入。

这类行为以后只允许实现一次，不允许通过新增页面专用布尔值复制。
## ERR-002：跨页面采集流程被页面处理器顺序抢占

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** 隐式流程、处理器顺序依赖、帧所有权不明确

### 1. 错误表现

主战员头像采集从主界面开始，需要跨过“信息统计页面”和“主战员列表页面”。旧实现使用 `TargetMemberArchiveStep` 和 `TargetMemberArchiveAttempts` 保存临时阶段，并把 `HandleEventTaskAsync` 放在 `HandleArchiveTargetMemberAsync` 前面。

实际运行中，进入采集流程后的下一帧如果仍被 OCR 识别成事件页，事件处理器会先选事件选项，采集流程随后才继续执行，表现为：

```text
先读取事件选项
再寻找刷存档主战员
```

### 2. 根因

主战员采集不是一个真正的独占流程，而是：

```text
长期状态字段
+ 处理器数组顺序
+ 每个页面处理器自己判断阶段
```

这种结构只能表达“某一步可能发生过”，不能表达：

- 当前帧属于哪个流程。
- 流程是否正在占用输入权。
- 流程何时完成、失败或超时。
- 其他页面处理器何时禁止接管当前帧。

因此，即使暂时交换处理器顺序，后续新增事件、卡牌或路线页面时仍会再次出现同类抢占。

### 3. 正确方案

跨页面采集使用明确的流程对象：

```text
TargetMemberArchiveFlow
    阶段
    启动时间
    超时判断
    完成
    失败
```

主循环先判断流程是否激活：

```text
流程激活 -> 只推进该流程 -> 不运行普通页面处理器
流程空闲 -> 按正常页面处理器分发
```

流程对象只负责生命周期和阶段；具体页面识别与业务动作仍留在主战员采集处理器中。

### 4. 防复发规则

- 跨帧、跨页面的连续操作必须有明确流程对象，不能只增加整数步骤或布尔字段。
- 激活中的流程必须明确拥有当前帧，普通页面处理器不得抢占。
- 流程等待必须绑定启动时间和超时，不能依赖固定帧数。
- 流程完成、失败或超时后必须明确释放帧所有权。
- 页面处理器顺序只能处理无状态页面竞争，不能替代状态机。
- 必须覆盖“流程激活时出现事件页也不能选择事件”和“流程超时后恢复普通处理”的纯逻辑测试。

## ERR-003：已消费的节点状态被兜底条件重新激活

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** 状态生命周期错误、兜底条件覆盖业务门闩、重复动作

### 1. 错误表现

休息节点点击一次后，日志持续在“点击休息”和“中心确认弹窗”之间循环：

```text
检测到休息界面，点击休息
中心确认弹窗，点击确认
检测到休息界面，点击休息
中心确认弹窗，点击确认
```

从日志看确认按钮并非完全没有点击，而是确认后底层休息页面仍然存在时，休息处理器又获得了动作权。

### 2. 根因

Python 中 `handle_rest` 的入口条件是：

```text
识别到休息特征或免费文字
且 node_status['flash_or_rest'] == true
```

C# 迁移时把条件改成了：

```text
识别到休息特征或免费文字
且 (FlashOrRest == true 或 识别到免费文字)
```

后半段兜底使 `FlashOrRest` 在首次点击后被清掉也无效。只要“免费”文字还在下一帧，休息处理器就会再次点击底层按钮。

### 3. 正确方案

- 节点业务状态 `FlashOrRest` 是唯一动作门闩，必须始终参与判断。
- OCR 文字、特征匹配和坐标兜底只能帮助识别按钮位置，不能重新赋予已经消费的动作权。
- 同一业务动作发出后，必须等待确认弹窗或页面离开，不得因为底层文字仍然可见而重复触发。

### 4. 防复发规则

- 不允许用“兜底识别成功”覆盖页面节点的已消费状态。
- 业务门闩、动作所有权和视觉识别条件必须在代码中分层表达。
- 回归测试必须覆盖“动作已消费，但原页面文字仍存在”的帧。
- 日志出现“动作 -> 确认 -> 原动作”循环时，优先检查状态是否被兜底条件重新激活。
## ERR-004：普通点击缺少统一动作所有权

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** 页面状态分散、重复动作、确认流程被底层动作抢占

### 1. 错误表现

休息问题修复后，进一步检查发现事件、卡牌、商店和其他普通点击仍可能因为同一语义页面反复出现而重复执行。页面专用字段只能说明“可能已经点过”，不能统一下达“当前帧只允许哪个动作执行”。

### 2. 根因

原有 `ActionArbiter` 主要管理跳过和中心确认，普通路由点击直接调用处理器。每个处理器各自维护有无重试、是否消费、尝试次数和页面字段，动作所有权没有统一对象。

### 3. 正确方案

增加 `PageActionSession`，由唯一路由在调用处理器前统一申请动作权：

```text
唯一路由 -> PageActionSession -> 点击处理器 -> 下一帧观察
```

- 同一语义页面只允许一个动作会话。
- 确认弹窗出现时，确认会话接管。
- 页面语义变化后结束旧会话。
- 超时后最多允许一次受时间限制的重试。
- 具有明确有限次数状态机的多步流程可以显式豁免，但必须继续使用自身上限。

### 4. 防复发规则

- 新增路由默认接入 `PageActionSession`，不能直接绕过仲裁器点击。
- 不允许用原始像素变化代替页面语义变化。
- 不允许在同一个源页面每帧重试业务动作。
- 路由没有执行动作时必须返回 `false`，避免错误提交动作会话。
- 回归测试必须覆盖“同页第二次执行被抑制”“确认弹窗接管”“换页释放”“超时单次重试”。
## 2026-10-13 收口记录

`ChaosModeTask` 已完成唯一路由和帧级分类收口：

- 所有页面入口都注册到 `FrameRouter<ChaosModePage>`。
- 路由命中后不再回退执行旧的 `PageHandlers` 列表。
- 同一帧的 OCR 查询、区域文本框、特征匹配、事件选项、路线节点和卡牌识别由 `ChaosModeFrameClassification` 缓存。
- 固定点击等待、重新截图等待和探险结果重复结算已经移除或加门闩。
- 回归测试补充了“路由命中后不执行低优先级处理器”“同页探险结果只结算一次”“同一帧事件识别只执行一次”三类约束。

后续新增页面时必须先增加分类和唯一路由，再增加动作；禁止重新引入顺序扫描所有处理器的兜底。
## ERR-005：OCR 未识别时禁止固定坐标兜底

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** OCR 漏识别被坐标硬猜掩盖、缺少可复现现场

### 1. 错误表现

卡牌奖励、卡牌分配和转换按钮等分支在 OCR 没识别到目标时，会直接点击预设固定坐标。这样表面能推进流程，但一旦坐标、分辨率或页面改版不一致，就会误点并制造难以定位的循环。

### 2. 根因

固定坐标被当作正常兜底，而不是识别失败信号。不同动作共享一个页面专用计数，既不能分别调整等待轮数，也无法判断是哪个动作连续漏识别。

### 3. 正确方案

- 每种动作使用独立识别失败计数。
- 未达到配置轮数时只等待下一帧重新识别，不点击。
- 达到配置轮数后禁止固定坐标点击，保存完整画面、OCR、原固定坐标和最近任务日志。
- 后续 OCR 成功并执行真实动作时，立即清零对应计数。
- 配置项为“识别失败等待轮数”，默认 2，范围 1 到 10。

### 4. 防复发规则

- 不允许把固定坐标作为 OCR 未命中的正常兜底。
- 固定坐标兜底事件必须保存 `original.png`、`annotated.png`、`metadata.json` 和 `context.log`。
- 诊断原因必须包含动作 key、等待轮数、OCR 文本和最近日志。
- 页面已确认后，针对稳定按钮的固定坐标操作与 OCR 漏识别兜底必须分开设计，不能复用“猜坐标”路径。
## ERR-006：闪光效果路由命中后吞掉后续可执行路由

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** 路由命中与动作成功混为一谈、后续匹配路由被阻塞

### 1. 日志证据

`logs/runtime.log` 从 `22:43:30.135` 到 `22:44:43.799` 连续输出：

```text
WIN-01 · 检测到查看原件页面，但未识别到卡牌。
```

这是当时旧文案的原样日志；该页面现已统一称为“闪光效果页面”。期间没有点击日志，页面也没有切换。

### 2. 根因

`flash-effect` 路由（旧名 `view-original`）由“请决定闪光效果”入口文字命中，但卡牌特征识别返回空。处理器返回 `false` 后，旧路由框架仍把该帧视为“已经由最高优先级路由认领”，因此后面的 `skip` 等匹配路由不会被执行。结果是每帧重复识别、重复返回 `false`，形成空转。

“查看原件/查看之前的闪光”只是 Python 旧实现中用于识别页面的 OCR 特征，不是需要处理的业务动作或字段。

### 3. 正确方案

- 路由默认仍保持“最高优先级匹配后停止”。
- 只有明确声明 `AllowFallbackWhenUnhandled` 的路由，在处理器返回 `false` 时才继续尝试下一个匹配路由。
- `flash-effect` 首次未识别到卡牌时保存 `flash-effect-no-cards` 诊断现场，然后继续尝试后续路由。
- 诊断包含完整画面、OCR、标题区、卡牌候选区、底部操作区和最近任务日志。

### 4. 防复发测试

- `FlashEffectWithoutCardsCapturesDiagnostic`
- `DispatchContinuesToNextMatchingRouteWhenFallbackIsAllowed`
- `DispatchStopsAfterFirstMatchEvenWhenHandlerDeclines`
- `FlashEffectSelectionScreenshotChoosesRecognizedPriorityCard`：使用 `capture-20261013-224529.png` 作为真实帧素材，经过真实 PP-OCR 和真实特征模板匹配，验证闪光选择会按优先级点击识别出的卡牌；测试不注入“跳过”文本，不使用固定点击坐标。

### 5. 判断规则

路由“命中”不等于“动作已经完成”。不能让无法执行的高优先级路由永久吞掉同一帧中真正能推进流程的路由。
## ERR-007：截图/OCR 循环直接等待消费者

### 1. 问题表现

页面处理在 `RunOnceAsync` 中直接 `await` 路由处理器。只要消费者等待确认、等待页面变化或陷入自身状态，下一帧 OCR 和下一轮路由就无法开始。表现通常不是抛异常，而是日志停止、画面仍然更新，但系统不再做任何判断。

### 2. 根因

把采集、识别、消费者选择、消费者执行放进了同一条串行调用链：

```text
取帧 -> OCR -> 路由 -> await 点击/确认 -> 等待页面变化 -> 下一帧
```

这不是帧总线，而是“消费者完成后才允许采下一帧”的同步处理循环。

### 3. 正确边界

```text
独立采集/OCR
-> 按当前 OCR 选择 ConsumerId
-> 投递 FrameDelivery(ConsumerId, TextSignature, payload)
-> 消费者自行处理
-> 下一帧继续采集/OCR
```

- 总线不等待消费者返回值。
- 总线不能因为 `ConsumerId + TextSignature` 相同而跳过投递；每一帧都必须投递给已经确定的消费者。
- OCR 文本是否重复、是否需要重试、是否应该丢弃，由消费者根据自己的执行状态决定。
- OCR 文本未变化不代表总线可以停止派发；它只说明消费者可能会在自身状态中等待或去重。
- 消费者动作失败要写日志，但不能阻塞采集。

### 4. 防复发测试

- `PublishingDoesNotWaitForConsumer`
- `SameConsumerAndTextIsSuppressedUntilTimeout`
- `ChangedTextDispatchesImmediately`
- `RunOncePublishesFrameWithoutWaitingForConsumerClick`

### 5. 判断规则

只要出现“截图循环等待业务动作完成”，就一定不是总线模式。消费者可以等待超时或页面变化，采集侧不能等待它。

> **2026-10-15 纠正：** 旧版 ERR-007 仍保留了“相同 `ConsumerId + TextSignature` 在等待窗口内不重复投递”的错误约束。用户明确要求总线始终完成投递，消费者自行决定是否重试或丢弃；该旧约束已作废，详见 ERR-009。

## ERR-008：把合成“跳过”测试误当成截图证据

**记录日期：** 2026-10-13

**严重级别：** 高

**问题类型：** 测试夹具与真实素材混淆、固定坐标误判、卡牌识别条件过严

### 1. 错误表现

在分析 `capture-20261013-224529.png` 时，曾出现“截图里有跳过区域”的判断。该判断没有来自真实截图 OCR，而是来自另一个合成单元测试：

- `Automation.Core.Tests/ChaosModeTaskTests.cs` 的跳过测试调用 `CreateOcr("跳过", new OcrTextRegion(1450, 800, 110, 50))`。
- 这条文本和坐标是测试自己构造的，用来验证跳过分支，不代表任何真实截图内容。
- 真实截图 OCR 结果是“请决定闪光效果”“查看原件”以及三张卡牌的 OCR 文本，没有“跳过”。其中“查看原件”只是页面 OCR 特征，不参与业务分支。

同一张截图真正失败的原因是：攻击图标已经匹配到三张卡牌，卡牌名称和描述也有 OCR，但类型文字为空；旧逻辑要求名称、类型、描述三者全部非空，因此返回空卡牌列表。

### 2. 根因

- 测试夹具中的 OCR 文本被错误地当作真实素材证据。
- 跳过分支保留了单一固定探测点，容易把“测试构造的位置”误解为截图中的固定区域。
- 卡牌类型图标本身已经能表达类型，但识别核心仍把类型 OCR 当作硬性条件。

### 3. 正确方案

- 真实截图测试必须使用真实 PP-OCR 和真实特征模板匹配，不能注入“跳过”或其他伪造文本。
- 合成 OCR 测试必须只验证纯逻辑，测试名称或注释要明确它是合成场景。
- 跳过按钮在候选区域内查找 OCR 文本框，点击点使用文本框中心；不再使用固定点击坐标。
- 普通闪光卡牌识别允许高置信度特征模板在类型 OCR 缺失时通过，但仍必须同时具备卡牌名称和描述，不能用固定坐标兜底。
- 如果真实截图没有某种区域，不得根据合成测试推断该区域存在。

### 4. 防复发规则

- 任何“截图中看到了某文本”的结论，必须能引用真实 OCR 日志或真实截图测试的 OCR 输出。
- 不把 `CreateOcr`、`FakeOcr` 或测试文本框当作游戏画面证据。
- 不把固定探测点的存在解释成固定点击是正常兜底。
- 真实素材测试必须断言点击来自识别结果坐标，而不是测试硬编码坐标。
## ERR-009：把帧总线实现成会被消费者状态反向锁死的同步单消费者流水线

**记录日期：** 2026-10-15

**严重级别：** 严重

**问题类型：** 总线语义错误、消费者状态反向锁死派发、异步设计退化为同步处理、错误分发不可见

### 1. 错误结论

当前实现虽然使用了 `Channel` 和非阻塞 `TryPublish`，但业务语义不是真正的投递消费总线。它实际上是一个单消费者、串行处理、共享页面动作状态的同步流水线：

```text
取帧 -> OCR -> 选择一个 ConsumerId -> 相同文本抑制 -> 单消费者处理
                                           -> ActionArbiter 先拦截
                                           -> 命中旧页面动作锁则直接丢弃
```

消费者内部的页面动作状态可以反向阻止后续帧进入真正的业务处理器。这违反了帧总线的根本职责：总线负责把观察结果投递给消费者，消费者负责决定如何执行业务动作。

### 2. 当前实现中的错误点

- `ResolveFrameConsumerId` 返回空时，当前实现不发布帧，只记录未处理页面。
- `FrameDispatchBus` 对相同 `ConsumerId + TextSignature` 做等待窗口抑制，导致总线主动停止投递。
- `ProcessFrameBusDeliveryAsync` 在调用消费者业务处理器之前，先经过共享 `ActionArbiter`。
- `ActionArbiter` 返回 `SuppressDuplicate` 时，消费者入口直接结束，后续业务处理器没有机会判断当前帧是否仍应重试、丢弃或走兜底。
- `FrameDispatchBus` 使用单 reader 串行消费，消费者的慢状态和锁状态会影响整个业务链。
- 消费者发现“这不是我的图”时没有明确的路由错误记录；未找到消费者时也没有严重错误记录。

因此，日志中的“当前路由动作已经执行，路由未离开前禁止重复执行”不是总线停止抓帧，而是总线完成投递后，消费者入口被旧状态永久拦截。下一帧即使已经采集并完成 OCR，也无法进入真正的业务判断。

### 3. 用户确认的正确设计边界

```text
每帧采集
-> 每帧 OCR
-> 形成不可变 FrameObservation
-> 总线确定投递目标
-> 无条件投递给消费者
-> 消费者判断是否属于自己
     -> 属于：执行、重试、等待或丢弃，全部由消费者决定
     -> 不属于：记录总线路由错误
-> 下一帧继续采集、OCR、投递
```

总线职责只有：

- 采集结果发布。
- 投递目标选择。
- 投递和投递错误记录。
- 不在业务入口做全局动作锁定。
- 不用上一帧的消费者状态阻止下一帧投递。

消费者职责包括：

- 判断当前帧是否在自己的处理范围。
- 判断当前动作是否需要重试、等待、丢弃或完成。
- 维护自己的页面动作生命周期。
- 将“不是我的图”作为消费者拒绝结果返回总线并记录错误。

### 4. 必须记录的总线错误

- `NoConsumerForFrame`：本帧没有任何消费者可选，属于总线路由失败。
- `ConsumerRejectedFrame`：总线选中了消费者，但消费者判定该帧不属于它的处理范围，属于总线路由错误。
- `ConsumerTimedOut`：消费者处理超时；记录错误和帧序号，但不能锁死后续采集/OCR/投递。
- `DeliveryRejected` / `QueueOverflow`：总线无法完成投递时必须显式记录，不能静默丢弃。

同一 OCR 文本是否重复、是否已经执行过、是否需要再次点击，都不是总线错误，必须由消费者根据 `FrameSequence`、时间戳、页面状态和自身动作状态判断。

### 5. 防复发测试

- `EveryObservationIsDeliveredEvenWhenTextIsIdentical`
- `NoConsumerForFrameIsRecordedAsError`
- `ConsumerRejectedFrameIsRecordedAsRoutingError`
- `ConsumerActionLockDoesNotBlockNextDelivery`
- `SlowConsumerDoesNotBlockCaptureOrOcr`
- `ChangedTextIsDeliveredImmediately`
- `EveryFrameStillReachesConsumerWhenPageDoesNotChange`

### 6. 防复发判断规则

> 只要总线会因为消费者尚未完成、同一文本重复、旧页面动作锁仍然存在而跳过投递，它就不是投递消费总线，而是同步处理循环。

> 消费者可以决定自己怎么处理帧；总线不能代替消费者决定“这一帧没有必要投递”。

> 总线下发错误（无消费者、选错消费者、投递失败）必须生产明确 Error；消费者的正常去重、等待和丢弃不能伪装成总线错误。

## ERR-010：把中心确认 ROI 设为普通页面的默认下一帧路径

**记录日期：** 2026-10-17

**严重级别：** 高

**问题类型：** 热路径识别策略错误、状态预测过宽、消费者调度延迟

### 1. 错误表现

普通消费者处理完页面后，`UpdateConfirmationProbeState` 会把 `_confirmationProbeArmed` 设置为真。下一帧因此先对中心确认区域做局部 OCR；连续未命中达到阈值后，才回退全图 OCR。

这不是确认弹窗识别失败，而是普通页面被错误预测成“下一帧会出现中心确认弹窗”。

2026-10-17 的运行窗口 `13:20:00-13:23:10` 中：

- 全图 OCR 回退 89 次。
- 中心局部 OCR 命中 5 次，未命中 177 次，命中率约 2.7%。
- 局部未命中耗时中位数 288ms。
- 第二次未命中后，全图 OCR 耗时中位数 759ms。
- 单次回退在 OCR 阶段通常增加约 1.3 秒。

典型日志：

```text
13:22:09.470 全图路由=obtain-reward，下一帧探测=True
13:22:09.756 hit=false miss=1/2 ocr=244ms
13:22:10.557 hit=false miss=2/2 ocr=255ms
13:22:10.558 回退全图 OCR，原因：连续未命中达到阈值
13:22:11.340 全图路由=obtain-reward
```

### 2. 根因

`Kes.Tasks/ChaosModeTask.FrameBus.cs` 的探测预置条件把除共享确认和页脚确认之外的几乎所有路由都设置为下一帧探测中心确认。战斗、路线、事件、奖励、离开、闪光和休息等普通页面因此持续进入“局部 miss -> 再次局部 miss -> 全图 OCR”的高成本路径。

这里混淆了两个概念：

```text
页面可能出现确认弹窗
!=
每次普通页面处理结束后都要默认探测确认弹窗
```

只有明确知道某次动作会产生中心确认的消费者，才有资格预置该探测。没有明确声明时，应直接走当前页面所需的全图路由。

### 3. 正确边界

- ROI 探测是动作预期，不是全局默认状态。
- 消费者执行动作时可以显式声明“预计下一帧出现中心确认”。
- 没有声明时不得武装中心确认探测。
- 预期落空后应清理动作预期，由下一张新帧重新做全图路由。
- 性能日志必须区分区域 OCR、全图 OCR、消费者业务处理和新帧等待，不能用一个 `route` 指标代表完整操作耗时。

### 4. 防复发规则

> 不允许把“可能出现的页面”作为所有页面的默认后续状态。页面预期必须由当前动作明确产生，并在页面变化或超时后释放。

防复发测试至少应覆盖：

- 普通战斗、路线、奖励、事件和执行离开动作后都不会武装中心确认探测。
- 明确声明中心确认的动作只在下一次预期窗口内探测。
- 探测未命中后，下一张新帧可以重新进入正常路由。
- 业务消费者不会因为探测状态而延迟接收全图路由。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- 删除普通路由的广泛默认预置；只有动作或路由明确声明会进入中心确认时，才武装下一次 ROI 探测。
- `skip` 路由通过 `ExpectsCenterConfirmation` 元数据在派发前声明预期；休息和冥想由动作在执行点击前显式请求探测。
- 普通关闭、页脚确认、战斗、路线、奖励和事件页面不再因为“可能弹出确认”而先做无意义 ROI OCR。
- `OrdinaryCloseActionDoesNotArmCenterProbeForFollowingContinuePage`、`FooterConfirmDoesNotArmCenterProbeForFollowingDestinyPage` 和原有确认 ROI 命中/回退用例覆盖该边界。

## ERR-011：通用“跳过”路由吞掉尚未迁移的专用业务流程

**记录日期：** 2026-10-17

**严重级别：** 高

**问题类型：** 路由所有权不清、通用兜底遮蔽专用消费者、迁移缺口不可见

### 1. 错误表现

卡牌复制的主战员选择已经成功，也点击了确认按钮，但进入“请选择要复制的卡牌”页面后，C# 将其路由为 `skip` 并点击“跳过”，从外部看就是复制被取消。

运行日志：

```text
13:28:09.423 选择要复制卡牌的主战员，点击 (0.504, 0.504)
13:28:10.345 检测到确认按钮，点击 (0.922, 0.931)
13:28:13.042 全图路由=skip
13:28:13.056 检测到跳过按钮，点击 (0.942, 0.929)
```

Frame #769 的真实 OCR JSON 明确包含：

```text
请选择要复制的卡牌。 score=0.955 box=610,89,367,57
跳过 score=1.000 box=1469,815,75,43
```

### 2. 根因

Python 基线存在独立的 `handle_copy_card_choice`，通过标题区域 `(0.498, 0.133)` 匹配“请选择要复制的卡牌”，再按 `复制卡牌列表` 选择卡牌。

C# 当前没有对应的专用路由和消费者，迁移台账仍将该项标记为“待迁移”。页面上的“跳过”文字又被优先级较低的通用 `skip` 路由命中，于是缺失的专用消费者被通用兜底静默替代。

这不是卡牌复制配置没有生效，而是页面根本没有进入复制卡牌消费者。

### 3. 正确边界

- 页面有专用业务提示时，必须先由专用消费者取得所有权。
- 通用“跳过”只能处理没有更具体业务含义的残余页面。
- 专用处理器尚未迁移时，应暴露明确的未迁移或 NoConsumer 诊断，不能让通用跳过静默取消业务。
- 迁移台账中的“待迁移”入口如果能在实机页面被通用路由命中，必须记录实际遮蔽行为和回归素材。

### 4. 防复发测试

- “请选择要复制的卡牌”页面必须路由到 `copy-card-choice`，不能路由到 `skip`。
- OCR 同时存在标题“请选择要复制的卡牌”和“跳过”按钮时，专用路由优先。
- 复制卡牌列表命中时选择对应卡牌，未命中时保持 Python 的返回行为，不把通用跳过当作成功处理。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- 新增 `copy-card-choice` 路由和 `HandleCopyCardChoiceAsync`，路由优先级高于 `copy-member` 与通用 `skip`。
- 已覆盖标题与“跳过”同时存在时仍派发给复制消费者、`target` 特征优先、配置优先级选牌，以及帧总线消费者选择。

## ERR-012：用页面动作去重代替战斗事件跨页面状态机

**记录日期：** 2026-10-17

**严重级别：** 高

**问题类型：** 跨页面业务状态缺失、残留帧被错误重试、动作生命周期边界混淆

### 1. 错误表现

事件页选择战斗后，事件页在切换到战斗预览前短暂残留。程序把残留帧再次交给 event-task，连续重复选择战斗；随后虽然点击了 start_attack_event，返回的事件页又被 Y 坐标优先分支选走。

本次运行日志：

~~~text
14:56:21.689 检测到attack_event特征，点击进入战斗任务
14:56:22.786 检测到attack_event特征，点击进入战斗任务
14:56:24.542 检测到attack_event特征，点击进入战斗任务
14:56:25.730 战斗页面检测到start_attack_event特征，点击发起攻击
14:56:27.346 事件任务页面: 识别到3个事件选项
14:56:27.763 检测到attack_event特征，点击进入战斗任务
14:56:28.786 检测到Y坐标小于0.925的任务，立即选择
~~~

因此“没有点击开始战斗”的表象并不成立。开始战斗按钮已经命中并点击；真实错误是战斗事件没有跨帧阶段状态，页面动作在布局变化或约 800 ms 后重新获得重试权。

### 2. 根因

event-task 和 battle-auto-check 分属两个消费者，但二者之间没有共享的业务流程对象。PageActionSession 只能回答“同一路由动作是否还能重试”，不能回答：

~~~text
战斗事件是否已经选择？
开始战斗是否已经发送？
当前事件页是正常新页面，还是上个动作尚未消失的残留帧？
~~~

事件路由优先级高于战斗路由。当 OCR 仍识别到事件选项时，残留帧会再次进入事件选择；动作状态指纹变化又会提前解除同路由去重，于是重复点击战斗，并在页面返回时进入普通事件选项分支。

### 3. 正确边界

- 跨页面、多步骤流程必须有自己的状态对象，不能只依赖页面路由名或动作指纹。
- 点击战斗事件后，事件消费者只能消费残留帧，不能再次选择战斗或其他事件。
- start_attack_event 发送后必须只记录一次点击；重复预览帧只能等待页面变化。
- 超时后允许一次受控重试，但重试必须回到原战斗事件，不能退化成随机选项。
- 结算、路线选择或新局重置时必须显式结束战斗事件流程。

### 4. 防复发测试

- BattleEventTransitionDoesNotReselectEventOrRepeatStartAttack
- BattleEventFlowTimesOutAndOnlySendsStartAttackOnce
- 回归序列必须覆盖“战斗事件 -> 同事件残留 -> 战斗预览 -> 事件残留 -> 战斗预览”。
- 全程只允许两次点击：一次战斗事件、一次开始战斗。
- 超时前后必须验证状态从过渡阶段回到 Idle，并允许后续流程正常处理。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- 新增 Kes.Tasks/BattleEventFlow.cs，阶段为 Idle、EventSelected、StartAttackRequested。
- 事件消费者在过渡阶段忽略残留事件帧；超时后只受控重试 attack_event。
- 战斗消费者通过 TryBeginStartAttack 保证 start_attack_event 只点击一次。
- 结算点击完成后、回到路线选择页或重置运行状态时结束流程。

## ERR-013：通用页脚确认抢占有状态页面消费者

**记录日期：** 2026-10-17

**严重级别：** 高

**问题类型：** 路由所有权错误、消费者边界错误、确认动作被重复建模

### 1. 错误表现

复制卡牌主战员页面先由 copy-member 消费者选择主战员。确认按钮为灰色时，通用 confirm 返回未处理，路由 fallback 后 copy-member 可以继续工作；确认按钮激活后，confirm 却直接返回已处理，copy-member 在这一帧完全收不到投递。

日志中的表现形式为：

~~~text
全图路由=confirm
确认按钮未激活，跳过点击
检测到卡牌复制主战员选择事件
...
全图路由=confirm
检测到确认按钮，点击 (0.921, 0.931)
当前路由动作已经执行，路由未离开前禁止重复执行
~~~

### 2. 根因

这里混用了两种确认：

1. 中心“确认 + 取消”模态弹窗，属于跨页面 overlay。
2. 页面底部的确认按钮，属于当前业务流程的最后一步。

通用 confirm 原本用于处理第二类按钮，但实现成了独立路由，并在优先级 56 上先于 copy-member 与 main-member-flash 匹配。它只检查页脚是否存在“确认”，不知道当前页面已经选中了哪个主战员，也不知道点击失败后是否应该重试。

一旦页面进入 PageActionSession，通用确认失败后又被“路由未离开前禁止重复执行”永久抑制，形成确认按钮仍存在、总线仍在截图，但没有任何消费者能继续推进的状态。

### 3. 正确边界

- 中央模态确认可以继续作为 shared-confirmation overlay 路由。
- 页脚确认按钮不独立取得页面所有权，它是页面消费者的动作步骤。
- copy-member、main-member-flash、equipment 等有状态页面必须从选择阶段持续负责到确认完成。
- 通用 confirm 只处理没有更具体页面消费者的页脚确认兜底，并必须排除已声明所有权的页面。
- 确认点击失败时由页面消费者按固定冷却重试；不能依赖全局页面动作边界，也不能每帧重复点击。
- OCR 只负责证明按钮文字和激活状态，实际落点继续使用稳定控件热点。

### 4. 防复发测试

- ActiveCopyMemberConfirmationRoutesToCopyMemberConsumer
- CopyMemberOwnsActiveFooterConfirmation
- CopyMemberConfirmationRetriesOnlyAfterCooldown
- MainMemberFlashOwnsActiveFooterConfirmation
- 既有 ConfirmClicksActiveButton 与 ConfirmDoesNotClickDisabledGrayButton 继续覆盖通用兜底和禁用按钮。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- main-member-flash 与 copy-member 路由优先级调整到通用 confirm 之前。
- ShouldHandleConfirmButton 明确排除这两种有状态页面。
- 两个页面消费者自行检测激活的页脚确认、调用共享稳定热点点击，并按 2.5 秒冷却决定是否重试。
- 全量测试 268/268 通过，解决方案构建 0 警告、0 错误。

## ERR-014：装备多阶段流程复用了同一个动作边界键

**记录日期：** 2026-10-17

**严重级别：** 高

**问题类型：** 页面阶段建模错误、动作边界错误、修改确认路由时的衍生回归

### 1. 错误表现

装备候选页点击一件装备后，后续安装/选人页仍持续识别为 `equipment`，但消费者不再执行：

~~~text
检测到装备页面。
选择1号位装备「深渊虫之齿」，点击 (0.518, 0.454)。
...
全图路由=equipment
当前路由动作已经执行，路由未离开前禁止重复执行。
~~~

页面仍在截图和 OCR，但不是总线停止，而是动作仲裁在消费者之前把后续阶段当成同一动作的重复帧抑制了。

进一步实机日志显示，安装页右上角提示可能被 OCR 误读成“请选择装备”；如果只按单个提示或词典装备名判断安装页，页面已经切换后仍会被当成候选页。

### 2. 根因

装备业务实际至少有三个连续阶段：

~~~text
equipment-choice：选择候选装备
equipment-install：选择主战员并点击安装
equipment-confirm：点击页脚确认
~~~

为修复通用 confirm 抢占装备确认，新增了 `equipment-confirm`，但没有同时为前两个阶段建立独立动作键。候选点击完成后，下一帧进入安装页时仍拿到 `equipment`，因此 `PageActionSession` 认为源页面没有离开，装备消费者无法进入选人阶段。安装页判断还依赖易被误读的单个提示，进一步放大了停滞。

问题不在按钮点击坐标，也不在确认按钮激活判断；根因是动作边界只覆盖了流程最后一段。

### 3. 正确边界

- 一个路由包含多个页面阶段时，动作边界键必须随阶段显式变化。
- 装备动作键按页面内容解析为 `equipment-choice`、`equipment-install`、`equipment-confirm`。
- 候选点击后先进入 `equipment-awaiting-install` 等待态，旧候选帧不得重复点击；安装页可由“装备标题 + 长按已佩戴装备的图标可进行比较”确认。
- 通用 confirm 仍不接管装备页脚确认；装备消费者继续从选人负责到确认完成。
- 不通过缩短或移除全局去重来掩盖问题；全局动作仲裁规则保持不变，只修正装备自身的阶段语义。
- 新增多阶段流程时，测试必须覆盖每一次“点击后进入下一阶段”，不能只测试最后一步。

### 4. 防复发测试

- EquipmentChoiceToInstallStageIsNotBlockedByPriorActionBoundary
- EquipmentChoiceWaitsForMisreadInstallPageWithoutReclickingCandidate
- EquipmentConfirmIsNotBlockedByPreviousMemberSelectionAction
- 三组合并覆盖：候选装备 -> 等待安装页 -> 安装选人 -> 确认的完整装备链。
- 第一段只允许点击候选装备；第二段只允许点击主战员；第三段才允许点击确认。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- `ResolveActionBoundaryKey` 只对 `equipment` 路由做阶段拆分，其他路由行为不变。
- 全量测试 270/270 通过。
## ERR-015：页面观察在消费者入口前永久锁死事件后续帧

**记录日期：** 2026-10-17

**严重级别：** 严重

**问题类型：** 总线投递被消费者状态反向拦截、动作边界没有有界重试、事件消费迁移回归

### 1. 错误表现

事件选项第一次点击后，截图和 OCR 仍在继续，但事件消费者不再收到业务处理机会。运行日志在 `22:37:23.625` 记录第一次点击事件选项 `(0.502, 0.853)`，随后从 `22:37:24.395` 开始持续输出“当前路由动作已经执行，路由未离开前禁止重复执行”，OCR 一直运行到帧 `#64`。

这不是截图停止，也不是 OCR 停止，而是 `ObservePage` 在消费者入口前返回 `SuppressDuplicate` 后直接结束了本帧处理。事件消费者内部的 `PendingEventOptionKey` 重试分支因此永远没有机会执行。

### 2. 根因

- `ProcessFrameBusDeliveryAsync` 在调用消费者业务处理器之前调用 `ObservePage`，命中抑制后直接 `return`。
- 非总线 `RunOnceAsync` 路径复制了同样的提前返回逻辑。
- `PageActionSession` 只保存“同一路由已经执行”，没有记录最后发送动作的帧序号，也没有有界重试规则。
- 事件消费者已有“继续重试本轮已选事件选项”的业务逻辑，但被外层动作锁永久挡住。

因此总线表面上仍在截图，实际上后续帧在消费者入口前被旧状态全部丢弃。这个实现再次违反了 ERR-009 规定的真正投递消费边界。

### 3. 正确边界

~~~text
每帧截图
-> 每帧 OCR
-> ObservePage 只推进确认/阶段状态
-> 不因 SuppressDuplicate 提前结束总线投递
-> 消费者收到当前帧
-> 消费者动作边界按 FrameSequence 判断等待或重试
-> 页面阶段变化时结束旧动作会话
~~~

- 总线入口不得使用上一帧的消费者状态阻止下一帧投递。
- `ObservePage` 可以维护确认会话和页面状态，但返回值不能成为总线提前返回条件。
- 消费者侧 `PageActionSession` 记录最后发送命令的帧序号。
- 事件路由把“识别失败等待轮数”作为重试帧数传给动作边界。
- 未达到等待轮数时抑制重复点击；达到轮数且页面阶段未变化时允许消费者重试。
- 不使用固定睡眠、墙钟超时或 OCR 指纹抖动作为重试依据。
- 路由阶段变化时立即结束旧动作会话，新的消费者动作可以正常开始。

### 4. 防复发测试

- `PageActionRetriesSameRouteAfterConfiguredFrames`
- `FrameBusRetriesEventOptionAfterConfiguredUnchangedFrames`
- 测试必须同时证明：未达到轮数不重复点击；达到轮数后允许消费者重试；总线在同一页面连续投递时没有被入口锁死。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- `FrameDispatchBus` 的 Chaos 消费者入口不再因 `ObservePage` 抑制而提前返回。
- 非总线路径同步移除了相同的入口拦截。
- `ActionArbiter.TryBeginPageAction` 接收消费者侧有界重试帧数。
- 事件路由使用配置键 `识别失败等待轮数`，默认 2，范围 1 到 10。
- 全量自动化测试 `276/276` 通过。

## ERR-016：结算页兜底“继续”在下一张新帧上重复点击

**记录日期：** 2026-10-17

**严重级别：** 严重

**问题类型：** 页面动作所有权错误、时间戳保护误用、路由兜底与动作键不一致

### 1. 错误表现

运行日志中，同一个卡厄思奖励结算页面连续点击了两次“继续”：

~~~text
22:54:58.331 帧 #461 路由=chaos-reward-settlement
22:54:58.332 检测到继续按钮，点击 (0.947, 0.915)
22:55:00.017 帧 #463 路由=chaos-reward-settlement
22:55:00.018 检测到继续按钮，点击 (0.947, 0.915)
22:55:00.869 帧 #465 路由=validation-card-prompt
~~~

第二次点击不是“旧帧重新入队”。帧 `#463` 的截图时间和帧序号都晚于第一次点击，`CapturedAt` 与 `FrameSequence` 的新鲜度检查确实通过。问题在于游戏尚未切走结算页，新截图仍然显示同一页面，程序把“新截图”误当成了“新页面阶段”。

这次重复点击覆盖了随后出现的中心确认弹窗，最终表现为继续按钮已经点过、中心确认按钮消失、页面卡住。

### 2. 根因

同一帧同时命中两个路由：

- `chaos-reward-settlement` 优先级 365，先匹配，但处理器返回 `false`，没有执行业务动作。
- `continue` 优先级 370，随后兜底匹配，真正点击“继续”。

旧动作仲裁只有一个 `_pageAction`。它保存的是实际执行的 `continue`，而下一帧入口观察到的是最高优先级路由 `chaos-reward-settlement`。旧逻辑看到键不同，就清除了 `continue` 的动作保护；路由再次兜底到 `continue` 时，又被当成第一次动作执行。

因此根因不是时间戳失效，而是：

- 时间戳只能证明截图发生在点击之后，不能证明游戏页面阶段已经变化。
- 页面动作保护绑定到了“本帧最高优先级匹配路由”，没有绑定到“实际发送动作的路由”。
- 观察到一个未执行动作的其他路由时，不应释放已经发送动作的保护。

### 3. 正确边界

- `CapturedAt` 继续只用于拒绝点击前截取、点击完成后才到达的旧帧。
- 页面动作保护按实际动作键独立保存，不能在观察到其他路由时立即清除。
- 只有其他路由确实发送了新动作，或中心确认流程完成，才允许清除旧页面动作保护。
- 路由命中但处理器返回 `false` 时，不得取得动作所有权。
- 不使用固定等待、固定坐标或“再观察三帧”来掩盖页面阶段判断问题。

### 4. 防复发测试

- `PageActionSurvivesDifferentRouteUntilThatRouteDispatches`
- `ChaosRewardSettlementDoesNotRepeatFallbackContinueOnUnchangedFrame`
- 前者锁定仲裁器语义：中间路由只有真正发送动作后才能释放旧保护。
- 后者锁定真实组合：结算页未处理、继续按钮兜底执行一次；下一张仍为同一结算页的帧不得再次点击。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- `ActionArbiter` 从单一页面动作槽改为按动作键保存页面动作会话。
- `ObservePage` 只推进与当前动作键对应的会话，不再因为看到其他路由而清除已发送动作。
- `MarkPageActionSent` 在另一个路由真正发送动作后清理旧会话。
- 中心确认完成后清空旧页面动作会话。
- 全量自动化测试 `279/279` 通过。

## ERR-017：把 OCR 指纹抖动误判成来源页面离开

**记录日期：** 2026-10-17

**严重级别：** 严重

**问题类型：** 页面身份建模错误、确认会话生命周期错误、重复点击

### 1. 错误表现

休息节点或带商店标志的休息节点已经点击“跳过”后，后续帧仍属于同一语义页面，但只因 OCR 文本布局发生轻微变化，`ConfirmationSession` 就连续累计“新页面帧”。达到两帧后，来源页面被错误判定为离开，动作保护被释放，下一帧再次点击“跳过”。

实际日志中可以观察到同一页面连续点击：

~~~text
检测到跳过按钮，点击
检测到跳过按钮，点击
检测到跳过按钮，点击
~~~

这类重复点击与截图时间戳无关。新截图确实是点击后采集的，但“新截图”仍然可能展示同一个页面阶段。

### 2. 根因

旧 `ConfirmationSession` 只用 `PageFingerprint` 判断来源页面是否仍在。指纹由 OCR 行文本和位置桶组成，同一页面的轻微识别抖动就可能生成不同指纹。

错误链路如下：

~~~text
来源帧：rest + skip
-> 点击跳过
-> 下一帧仍是 rest + skip，但 OCR 指纹变化
-> 指纹不同，被累计为“来源页面离开”
-> 连续两帧后确认会话 Completed
-> 页面动作保护被清除
-> 下一帧再次执行跳过
~~~

因此，指纹只能作为布局变化证据，不能单独作为语义页面身份。

### 3. 正确边界

- `PageObservation` 保存本帧匹配到的全部路由 ID，而不只保存最高优先级路由。
- `ConfirmationSession` 同时保存来源页面的语义路由集合。
- 新帧的当前最高优先级路由只要仍属于来源候选集合，就视为来源页面仍存在，即使 OCR 指纹已经变化。
- 只有连续两帧既没有来源指纹、当前主路由也不再属于来源候选集合时，才允许确认会话完成。
- 没有路由 ID 的旧测试和合成观察继续使用指纹兼容兜底。
- 不通过“再 OCR 一次按钮区域”解决此类问题，因为旧页面仍会识别到同一个按钮，二次确认也会通过。
- 通用 `copy` 路由恢复默认动作边界，页面语义未变化时不得重复发送复制点击。

### 4. 防复发测试

- `ConfirmationKeepsSourceBlockedWhileSemanticRouteRemains`
- `SkipDoesNotRepeatAcrossOcrJitterOnSameSemanticPage`
- `CopyClicksActiveButtonOnlyOnceUntilRouteChanges`
- 测试同时覆盖语义路由不变、真正离开后完成、OCR 抖动和通用按钮动作边界。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- `PageObservation.RouteIds` 记录完整候选路由。
- 普通任务路径和帧总线路径都在构造观察后写入候选路由。
- `ConfirmationSession` 使用来源路由交集维持动作保护。
- 全量自动化测试 `281/281` 通过。

## ERR-018：确认动作无界等待，特殊消费者未接入同一动作生命周期

**记录日期：** 2026-10-17

**严重级别：** 严重

**问题类型：** 动作生命周期默认值错误、消费者边界不一致、失败后无法恢复

### 1. 错误表现

卡厄思奖励结算页点击“继续”后，帧总线识别到通用页脚“确认”并发送了一次点击，但下一帧仍被识别为 `confirm`。此后消费者不再发送点击，任务表面停在确认页。

实机日志：

~~~text
23:25:48.986 全图路由=confirm
23:25:48.987 确认按钮 OCR 中心=(0.921, 0.928)，使用底部确认控件稳定热点 (0.921, 0.931)
23:25:48.988 检测到确认按钮，点击 (0.921, 0.931)
23:25:49.866 全图路由=confirm
23:25:49.866 当前页面动作已经发送，等待页面阶段变化；重试至少需要 2147483647 帧
...
23:26:59.661 全图路由=confirm
23:26:59.662 当前页面动作已经发送，等待页面阶段变化；重试至少需要 2147483647 帧
~~~

从帧 `#341` 到帧 `#429`，截图和 OCR 都持续运行，性能日志仍显示每帧约 0.75 秒完成。因此这不是帧总线停止，也不是 OCR 停止，而是 `confirm` 消费者在第一次点击后进入了实际永久的重试等待。

本次日志没有异常堆栈或消费者超时记录，并在 `23:26:59` 后直接结束，因此日志只能证明业务流程被永久抑制，不能单独证明进程为什么退出。

### 2. 根因

`confirm` 路由没有声明 `actionRetryAfterFrames`：

- `Kes.Tasks/ChaosModeTask.Routing.cs:27`
- `Kes.Tasks/ChaosModeTask.cs:2244`

未声明时使用 `int.MaxValue`：

- `Automation.Core/Arbitration/ActionArbiter.cs:33`
- `Automation.Core/Arbitration/PageActionSession.cs:56`

第一次点击成功后，`MarkPageActionSent` 记录动作帧号；页面仍属于同一路由时，`CanRetryAfter` 要等到 `int.MaxValue` 帧才允许重试。实际运行中这等价于禁止重试。

第一次点击为什么没有让页面变化，现有日志不能区分是输入未命中、游戏未响应，还是页面切换尚未完成。可以确定的是，软件没有为“点击后页面仍未变化”提供任何有界恢复路径，所以一次未生效就变成了永久失败。

另一个独立但同类的边界问题是 `close-page`。它是特殊消费者，不在 `_frameRouter.Routes` 中：

- `Kes.Tasks/ChaosModeTask.FrameBus.cs:300`
- `Kes.Tasks/ChaosModeTask.FrameBus.cs:472`
- `Kes.Tasks/ChaosModeTask.FrameBus.cs:493`

特殊消费者只调用 `ObservePage`，没有像普通路由那样调用 `TryBeginPageAction` 和 `MarkPageActionSent`。因此“点击屏幕”提示只要在下一帧仍存在，处理器就会逐帧再次点击 OCR 识别到的提示位置。总线仍在正常投递，重复动作来自消费者没有建立动作会话，不是固定坐标或总线锁死。

### 3. 正确边界

- 帧总线继续每帧截图、OCR 和投递，不能因为消费者等待或页面文本不变而停止。
- 通用页脚确认必须由消费者维护有界重试，等待轮数使用现有配置模型，不增加固定睡眠或固定坐标。
- 一次点击后页面仍未变化时，应在新的有效帧上按配置轮数受控重试，并记录尝试次数、最后动作帧号和当前路由证据。
- 所有特殊消费者必须走同一动作生命周期：或者在路由目录中正式声明，或者在消费者入口显式申请并标记自己的动作边界。
- 动作保护只防止同一语义阶段的无变化重复点击，不能把一次未生效变成永久不可恢复。
- 不通过点击固定位置来规避“点击屏幕”或页脚确认失败。

### 4. 防复发测试

- `PageActionRetriesSameRouteAfterConfiguredFrames`
- `PageActionRetriesSameRouteUpToAttemptLimitAndThenSuppresses`
- `ClickScreenConsumerRetriesOnConfiguredFreshFramesUpToLimit`
- `FooterConfirmRetriesOnConfiguredFreshFramesUpToLimit`
- `FrameBusRetriesEventOptionAfterConfiguredUnchangedFrames`
- `ClosePageOverlayHasPriorityOverTargetMemberArchive`
- `PriorityOverlayCanWinWithoutChangingNormalPriorityOrder`
- 测试覆盖首次点击、未变化帧、未到轮数不点击、达到轮数重试、最多 3 次、达到上限后停止，以及总线继续投递后续帧。

### 5. 修复状态

**状态：** 已完成（2026-10-17）

- `PageActionSession` 保存 `AttemptCount`，按帧序号和配置轮数判断是否允许下一次尝试，并强制执行最大尝试次数。
- `confirm` 路由通过 `GetActionRetryAfterFrames` 使用 `识别失败等待轮数`，默认 2，限制范围为 1..10。
- `close-page` 已改为正式帧路由，并通过 `IsPriorityOverlay` 元数据拥有覆盖层优先级；移除了 `HandlePageAsync` 的提前直调和帧总线中的同义特殊分支。覆盖层与普通消费者共用 `TryExecuteRouteAsync`、`TryBeginPageAction`、`MarkPageActionSent` 与有界重试。
- 全量自动化测试 `285/285` 通过。其余 58 个未声明重试策略的普通路由仍按业务阶段单独审计，不能因为本次修复就假定它们都允许安全重试。

### 6. 同类风险记录

- `HandleConvertCardAsync` 仍沿用 Python 的两处固定坐标连续点击（`Kes.Tasks/ChaosModeTask.cs` 约 `1558`、`1565` 行）。它与本次确认动作生命周期的根因不同，本次没有扩大修改范围；后续应改为先识别两处“跳过”目标，再进行点击与页面变化验证。

## ERR-019：无动作边界的中转页未释放来源路由会话

**记录日期：** 2026-10-20

**严重级别：** 严重

**问题类型：** 动作生命周期泄漏、跨页面状态污染、无界等待复现

### 1. 错误表现

Boss 装备奖励第一次领取后，画面经过闪光选择并进入装备页。装备安装结束后再次出现 `boss-equipment-reward`，但消费者持续记录 `int.MaxValue` 抑制，不再领取或继续处理。

实机日志：

~~~text
22:14:46.258 检测到 Boss 装备奖励，先领取装备
22:14:46.258 领取 Boss 装备奖励，点击 (0.922, 0.917)
22:14:47.288 全图路由=boss-equipment-reward
22:14:47.288 当前页面动作已经发送，等待页面阶段变化；重试至少需要 2147483647 帧
22:14:49.351 全图路由=flash-effect
22:14:57.789 全图路由=equipment
22:15:01.924 全图路由=boss-equipment-reward
22:15:01.924 当前页面动作已经发送，等待页面阶段变化；重试至少需要 2147483647 帧
~~~

### 2. 根因

动作会话按路由独立保存后，只有“当前路由启用动作边界”时才会调用 `ObservePage`。`equipment` 路由配置了 `useActionBoundary: false`：

- `Kes.Tasks/ChaosModeTask.Routing.cs:41`
- `Kes.Tasks/ChaosModeTask.cs:315`
- `Kes.Tasks/ChaosModeTask.FrameBus.cs:474`

因此装备页中转时没有观察到 Boss 来源路由已经消失，旧会话继续存在。再次出现 Boss 页面时，仲裁器把新页面误认为旧动作仍未完成；同时 Boss 路由未声明重试策略，只能使用 `int.MaxValue`。

### 3. 正确边界

- 每个 `PageActionSession` 保存来源路由，而不是只依赖动作键推断来源。
- 所有已识别的语义页面都参与观察；来源路由不再属于候选路由时，应释放旧会话。
- 无消费者或 OCR 暂时为空时不能立即释放，避免未知帧打断当前动作保护。
- 只有完成业务审计的路由才能启用有界重试；不能为了消除 `int.MaxValue` 而给全部路由统一打开重试。
- `boss-equipment-reward` 的“获得”每次语义页面只点击一次；页面未切换时等待来源路由生命周期变化，不自动重复领取。

### 4. 防复发测试

- `PageActionIsReleasedAfterSourceRouteLeavesObservedCandidates`
- `BossEquipmentRewardClicksOnlyOnceUntilRouteChanges`
- 覆盖来源路由消失、来源路由重新出现、同一语义页面连续多帧只点击一次，以及日志不再出现 `2147483647`。

### 5. 修复状态

**状态：** 已完成（2026-10-20）

- `PageActionSession` 保存 `SourceRouteId`。
- `ActionArbiter` 在语义路由集合明确变化时释放已离开的来源会话。
- `boss-equipment-reward` 不自动重试；来源路由重新出现时由生命周期释放建立新的单次动作。
- 未声明重试的路由不再使用 `int.MaxValue` 作为日志中的伪等待值，统一明确记录“未声明自动重试”；该改动不改变动作行为。
- 全量自动化测试 `288/288` 通过，完整解决方案构建 0 警告、0 错误。

### 6. 横向审计结果

**审计日期：** 2026-10-20

当前帧路由共 60 个：

| 类别 | 数量 | 路由 | 说明 |
|---|---:|---|---|
| 配置化有界重试 | 3 | `close-page`、`event-task`、`confirm` | 只在页面仍未变化的新帧上重试，最多 3 次 |
| 消费者自管动作生命周期 | 5 | `shared-confirmation`、`main-member-flash`、`equipment`、`battle-auto-check`、`copy-member` | 分别由确认状态机、成员选择状态、装备阶段键或 `BattleEventFlow` 管理 |
| 单次动作，无自动重试 | 52 | 其余路由 | 包含路线选择、选牌、购买、删卡、`boss-equipment-reward` 领取和页面跳转，不能统一重试 |

历史日志中，连续同一路由被抑制的主要区间只有：

- `boss-equipment-reward`：150 帧，约 141 秒，已由 ERR-019 修复。
- `confirm`：两组旧日志分别 97 帧约 77 秒、88 帧约 71 秒；当前代码已为 `confirm` 配置有界重试。
- `route-selection`、`leave`、`obtain-reward`、`continue`、`settlement` 等其余路由最多为 6 帧左右，未发现同类长时间停住证据。

不能把所有 52 个单次动作直接改成自动重试。路线节点、卡牌、装备、购买、删卡等动作可能在“页面尚未切换”的帧上产生第二次业务选择，必须分别证明幂等性。后续优先审计下列通用导航或领取入口，但顺序仍以状态机和真实帧证据为准：

`continue`、`enter`、`obtain-reward`、`settlement`、`chaos-reward-settlement`、`validation-card-prompt`。

另有三处固定坐标与本次来源路由泄漏无关，暂不混入本修复：

- `HandleSettlementAsync` 已识别“结算”文本，但点击仍使用固定相对坐标且未检查按钮激活状态。
- `HandleChaosRewardClaimAsync` 在验证卡信息缺失时固定点击取消位置。
- `TryFinishOnlyFirstLayerAsync` 固定点击右上角退出位置。

这些位置需要按“页面已确认后的稳定控件”和“OCR 未识别兜底”分开审计，不能因为本次动作生命周期修复而直接改坐标或增加盲目重试。

## ERR-020：中心确认弹窗被 OCR 指纹抖动重置后再次点击

**记录日期：** 2026-10-20

**严重级别：** 严重

**问题类型：** 确认会话生命周期、重复点击、旧帧语义判断

### 1. 错误表现

中心确认按钮第一次点击后，同一坐标在约 2.4 秒后又被点击一次。日志中第一次命令继承跳过意图的用途文字，第二次则被识别成一个新的“中心确认弹窗”意图：

~~~text
22:34:26.961 检测到跳过按钮，点击 (0.661, 0.633)
22:34:29.316 中心确认弹窗，点击 (0.661, 0.633)
~~~

两次坐标相同，间隔内页面仍持续路由到 `shared-confirmation`。因此这不是截图时间戳失效，也不是旧帧被重新执行，而是同一个新帧上的确认会话被错误结束并重新创建。

### 2. 根因

`ConfirmationSession.ObservePage` 原先只在 OCR 指纹完全相同时维持“原确认弹窗仍然存在”的保护。点击按钮后，按钮动画、禁用状态或 OCR 文本框变化会改变页面指纹；连续两帧指纹不同时，旧逻辑把弹窗状态计为“来源页面已经离开”，达到 `RequiredDistinctPageFrames = 2` 后结束会话。

随后下一帧仍检测到中心确认按钮，`ActionArbiter.ObserveConfirmation` 因会话已被清空而创建新的 `ConfirmCenterDialogIntent`，于是对同一按钮发出第二次确认。与此同时，旧实现还允许确认弹窗在持续存在 6 帧后自动重试，这也是独立的重复点击风险。

相关代码位置：

- `Automation.Core/Arbitration/ConfirmationSession.cs:100`：确认点击后判断是否仍是同一弹窗。
- `Automation.Core/Arbitration/ConfirmationSession.cs:172`：确认命令已发送后只等待，不再按帧数自动重试。
- `Automation.Core/Arbitration/ConfirmationSession.cs:220`：结合按钮位置和语义路由判断是否仍是同一弹窗。
- `Automation.Core/Arbitration/ActionArbiter.cs:249`：由确认会话决定是否创建并发送确认命令。

### 3. 正确边界

- 确认命令发送后，只要同一中心按钮仍然存在，就必须等待页面响应，禁止按固定帧数再次点击。
- OCR 指纹只作为辅助证据，不能单独证明弹窗已经离开；应同时参考中心按钮位置和候选语义路由。
- 只有弹窗消失或语义页面真正切换后，旧确认会话才能结束；后续新出现且可证明不同的弹窗才允许建立新意图。
- 不增加固定睡眠、固定坐标或新的“连续 N 帧”业务门控。

### 4. 防复发测试

- `ConfirmedDialogWithOcrJitterDoesNotIssueRepeatedConfirm`
- 测试在第一次确认后连续提供 9 帧不同 OCR 指纹、相同按钮位置和相同语义路由的中心确认页面，断言不会再次返回 `ExecuteConfirm`。
- 原有 `ConfirmationCompletesWhenNextPageAlsoHasCenterConfirmation` 继续覆盖语义页面确实切换、后续中央弹窗需要重新处理的正向路径。

### 5. 修复状态

**状态：** 已完成（2026-10-20）

- 删除中心确认弹窗的固定帧自动重试。
- 保存首次确认弹窗的按钮位置和语义路由，用于识别同一弹窗的 OCR 抖动。
- 全量自动化测试 `289/289` 通过，完整解决方案构建 0 警告、0 错误。

## ERR-021：同页自动补点造成事件与装备确认重复点击

**记录日期：** 2026-10-20

**严重级别：** 严重

**问题类型：** 动作重试边界、消费者状态机、重复点击

### 1. 错误表现

事件选项第一次点击后，同一落点在后续帧被补点两次：

~~~text
22:41:34.119 事件任务页面选择「帮忙埋葬残骸随机主战员触发1次闪光」，点击 (0.629, 0.853)。
22:41:35.536 继续重试本轮已选事件选项（第2次）: 帮忙埋葬残骸随机主战员触发1次闪光
22:41:37.173 继续重试本轮已选事件选项（第3次）: 帮忙埋葬残骸随机主战员触发1次闪光
~~~

装备安装确认也在页面尚未切换时再次点击同一位置：

~~~text
22:43:54.150 安装装备确认，点击 (0.921, 0.931)。
22:43:55.542 已发送安装装备确认，等待页面变化 1/2 帧。
22:43:57.090 安装装备确认，点击 (0.921, 0.931)。
~~~

### 2. 根因

事件问题有两层重试叠加：

- `Kes.Tasks/ChaosModeTask.Routing.cs` 给 `event-task` 配置了 `actionRetryAfterFrames`。
- `Kes.Tasks/ChaosModeTask.Event.cs` 又保存 `PendingEventOptionKey`，在相同选项仍被 OCR 识别时主动再次点击。

装备问题来自 `HandleEquipmentInstallAsync` 的确认尝试计数。第一次确认后，游戏处理期间按钮仍可能停留在当前 OCR 帧中；旧逻辑把后续“按钮仍存在”的帧当成点击失败，达到配置轮数后再次点击。

### 3. 正确边界

- 事件选项允许一次有界补点：首次点击后，页面仍识别到相同选项时，等待 `识别失败等待轮数` 个新帧再点击一次；总点击次数最多 2 次。
- 路由级 `actionRetryAfterFrames` 只负责在等待新鲜帧后重新开放消费者判断，实际点击次数必须由事件消费者统一计数，不能两层各自重试。
- 达到 2 次后仍停留在相同选项，消费者停止点击并等待页面阶段变化，不能继续无限补点。
- 装备安装确认发出后，同一安装页只允许一次确认点击；页面离开才是确认成功的证据。
- 总线继续每帧截图、OCR 和投递；是否重试、等待或丢弃由消费者状态机决定，不由总线停止投递来实现。

### 4. 防复发测试

- `FrameBusRetriesEventOptionOnceWhenUnchangedPagePersists`
- `EquipmentInstallConfirmationIsClickedOnlyOnceWhilePageRemains`
- `EventTaskDoesNotRepeatSameOptionWithinThePageActionBoundary`
- `EquipmentConfirmIsNotBlockedByPreviousMemberSelectionAction`

### 5. 修复状态

**状态：** 已完成（2026-10-20）

- `event-task` 保留路由级的新鲜帧重试窗口，但消费者把实际点击严格限制为首次点击加一次补点。
- 事件消费者在达到 2 次点击后只记录等待，不再点击。
- 装备安装确认记录点击帧；后续同页帧不再补点，页面路由离开时正常结束确认状态。

### 6. 过度修复纠正（2026-10-20）

第一次修复错误地删除了 `event-task` 路由级重试，同时让消费者在相同选项上直接返回。实机日志显示，`23:03:29.753` 第一次点击「献上赞美信用点增加100」后页面没有推进；到 `23:04:32.689` 仍持续识别同一组事件选项，但不再产生任何点击，任务因此卡住。

原回归测试只断言“相同帧不重复点击”，没有覆盖“第一次点击没有生效时，后续新鲜帧必须允许一次补点”，所以测试通过但真实流程卡死。正确边界是 2 次封顶，不是永不重试。