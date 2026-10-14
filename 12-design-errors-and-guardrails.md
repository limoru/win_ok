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
- 同一 `ConsumerId + TextSignature` 在等待窗口内不重复投递。
- OCR 文本变化后立即重新判定消费者。
- 相同文本持续存在时只在超时后允许重投。
- 消费者动作失败要写日志，但不能阻塞采集。

### 4. 防复发测试

- `PublishingDoesNotWaitForConsumer`
- `SameConsumerAndTextIsSuppressedUntilTimeout`
- `ChangedTextDispatchesImmediately`
- `RunOncePublishesFrameWithoutWaitingForConsumerClick`

### 5. 判断规则

只要出现“截图循环等待业务动作完成”，就一定不是总线模式。消费者可以等待超时或页面变化，采集侧不能等待它。

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