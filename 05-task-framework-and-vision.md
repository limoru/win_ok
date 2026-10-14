# 05. 任务框架与视觉层设计

## 1. 目标

- 核心库提供与目标厂商无关的任务运行时。
- 业务层只通过 `TaskContext` 访问帧、OCR、模板匹配和输入。
- 任务可以逐页面迁移和测试。
- 同一会话只执行一个操作序列，避免并发点击。
- 视觉层可脱离真实目标，使用假帧和假输入做纯逻辑测试。

> 实现状态（2026-10-11）：`ITaskModule`、`SessionTaskCoordinator`、`TaskContext` 基础能力和 `DiagnosticTask` 已落地。当前 `TaskContext` 先提供帧与输入能力；OCR 和模板匹配会沿用现有开发测试实现接入，不改变任务边界。任务方案与用户界面见 [09-task-board-design.md](./09-task-board-design.md)。

## 2. 任务接口

```csharp
public interface ITaskModule
{
    string ModuleId { get; }
    string DisplayName { get; }
    ValueTask<bool> RunOnceAsync(TaskContext context, CancellationToken ct);
}
```

`RunOnceAsync` 返回 `true` 表示本轮处理了页面，返回 `false` 表示本轮未命中。

## 3. 会话协调器

不再让同一会话的多个任务模块各自启动独立循环。使用 `SessionTaskCoordinator`：

```csharp
public sealed class SessionTaskCoordinator : IDisposable
{
    public SessionTaskCoordinator(
        IEnumerable<ITaskModule> modules,
        TaskContext context,
        TimeSpan pollInterval);

    public ValueTask<bool> RunOnceAsync(CancellationToken ct);
    public Task RunAsync(CancellationToken ct);
    public void Pause();
    public void Resume();
}
```

调度约束：

- 同一会话同一时刻只有一个任务可操作目标。
- 一个任务执行期间，其他任务不能抓取输入门控。
- GUI 暂停可以等待当前单步操作完成，不强制中止页面点击序列。
- 任务异常只标记当前会话为 `Faulted`，不能影响其他会话。

## 4. TaskContext

```csharp
public sealed class TaskContext
{
    public string SessionId { get; }
    public UiProfile UiProfile { get; }
    public TargetCapabilities Capabilities { get; }
    public IFrameSource Frames { get; }
    public IInputSink Input { get; }
    public IActivityReporter? Activity { get; }
    public ITaskLogSink? LogSink { get; }

    public ValueTask<FrameLease> GetLatestFrameAsync(CancellationToken ct);
    public ValueTask<FrameLease> GetNextFrameAsync(
        long afterSequence,
        TimeSpan timeout,
        CancellationToken ct);

    public ValueTask ClickRelativeAsync(RelativePoint point, CancellationToken ct);
    public ValueTask SwipeRelativeAsync(
        RelativePoint from,
        RelativePoint to,
        TimeSpan duration,
        CancellationToken ct);
    public ValueTask KeyAsync(InputKey key, CancellationToken ct);
    public ValueTask TextAsync(string text, CancellationToken ct);
    public Task SleepAsync(TimeSpan delay, CancellationToken ct);
    public void LogInfo(string message, string? tag = null);
}
```

关键约束：

- OCR 和模板匹配显式接收 `frame`，不再依赖一个含义模糊的全局“当前帧”。
- 点击后需要新帧时必须重新获取。
- `TaskContext` 不暴露 `hwnd`、MuMu 实例或 ADB 类型。

### 4.1 活动快照与进度

任务模块通过 `IActivityReporter` 上报结构化状态：

```csharp
context.Activity?.Report(new SessionActivity(
    Kind: SessionActivityKind.Progress,
    TaskId: "chaos",
    TaskLabel: "混沌模式",
    StepId: "select-card",
    StepLabel: "选择卡牌",
    Detail: "等待卡牌识别完成",
    Current: 3,
    Total: 10,
    UpdatedAt: DateTimeOffset.Now));
```

约束：

- 只在关键步骤切换、进度变化和状态变化时上报，不按每一帧更新。
- GUI 直接绑定最新 `SessionActivity`；不得从自由文本日志反推当前状态。
- 会话 ViewModel 保存最新快照，GUI 日志层将其镜像为 `activity` 或 `progress` tag 日志。
- 长耗时操作至少上报“开始”和“完成”，避免前台看起来卡死。
- 任务异常时上报 `Faulted`，由 GUI 显示故障步骤和说明。

## 5. 页面处理器

业务层采用“帧分类 + 唯一路由”模型。每轮先对当前帧建立分类结果，再由 `FrameRouter<TFrame>` 按优先级选择唯一页面任务；命中后不再回退执行其他处理器。

```csharp
var dispatch = await router.DispatchAsync(page, cancellationToken);
```

同一帧内的 OCR 点查询、区域文本框、特征匹配、事件选项、路线节点和卡牌识别都会缓存到分类对象；路由判断和实际动作共用这些结果。

页面处理任务按 `UiProfile` 获取区域、文本和模板：

```text
Kes.Tasks                共享决策、优先级、卡牌和路线规则
Kes.Layout.WindowsPc     PC 页面区域、文本和模板
Kes.Layout.AndroidMobile 安卓页面区域、文本和模板
```

如果两个客户端的页面结构相同，可以共享任务；不同则必须分开，不能依赖运行时猜测。
## 6. OCR 分阶段策略

### 第一阶段

- 复用现有 PP-OCRv5 ONNX 模型。
- 实现 `OnnxOcrEngine : IOcrEngine`。
- 保持输入的像素格式、缩放、阈值、后处理和繁简转换与现有实现可对比。
- 用日志和固定帧样本验证识别结果。

### 后续阶段

- 建立游戏专属数据采集、合成数据、标注和训练流水线。
- 保持训练侧和推理侧的前后处理一致。
- 通过 `IOcrEngine` 替换或新增模型，不修改任务接口。

自训练模型不阻塞第一阶段 C# 链路开发。

## 7. 模板匹配

```csharp
public interface ITemplateMatcher
{
    MatchResult? FindOne(FrameLease frame, string templateName, double threshold, Rectangle? region);
    IReadOnlyList<MatchResult> FindAll(FrameLease frame, string templateName, double threshold, Rectangle? region);
}
```

第一实现使用 OpenCvSharp4。模板按 `UiProfile` 和任务模块分目录，核心库只负责加载和匹配，不包含具体游戏模板语义。

## 8. 文本归一化

文本归一化属于业务层：简繁转换、日本字形到繁体、编辑距离、子序列匹配和 OCR 常见错字修正都放在 `Kes.Tasks` 或对应布局项目。

## 9. 视觉并发

所有 OCR 和模板请求经过 `VisionScheduler`：

- 默认限制并发度，不直接让每个会话同时调用原生推理库。
- 记录排队延迟和推理耗时。
- 支持取消排队任务。
- 根据基准测试调整并发上限。

## 10. 测试

- 假目标提供者提供固定帧序列。
- 假输入记录点击、滑动和按键。
- 假配置提供者支持运行时替换。
- 页面处理器测试验证给定帧是否触发预期操作。
- 核心测试不依赖真实窗口、模拟器或游戏。

当前 `Automation.Core.Tests` 已有 4 个纯逻辑测试，覆盖首个模块命中后停止本轮、无处理器时报告等待、暂停/恢复门控，以及 `TaskContext` 读取最新帧。安全烟测另用本地 WinForms 目标验证抓帧和诊断任务，不发送真实鼠标键盘输入。