# 03. 截图与输入策略

## 1. 统一接口

核心库定义与传输方式无关的抓帧和输入契约：

```csharp
public interface IFrameSource
{
    ValueTask<FrameLease> GetLatestAsync(CancellationToken ct = default);
    ValueTask<FrameLease> WaitNextAsync(long afterSequence, TimeSpan timeout, CancellationToken ct = default);
}

public interface IInputSink
{
    ValueTask ClickAsync(RelativePoint point, CancellationToken ct = default);
    ValueTask SwipeAsync(RelativePoint from, RelativePoint to, TimeSpan duration, CancellationToken ct = default);
    ValueTask KeyAsync(InputKey key, CancellationToken ct = default);
    ValueTask TextAsync(string text, CancellationToken ct = default);
}
```

`RelativePoint` 使用 0 到 1 的屏幕相对坐标。`InputKey` 是通用逻辑键，不暴露 Windows 虚拟键或 Android KeyCode。

## 2. 目标能力

不同目标支持的后台能力不同，必须通过能力声明，而不是假定所有实现等价：

```csharp
[Flags]
public enum TargetCapabilities
{
    None = 0,
    BackgroundCapture = 1 << 0,
    BackgroundInput = 1 << 1,
    ForegroundInput = 1 << 2,
    Pointer = 1 << 3,
    Touch = 1 << 4,
    Keyboard = 1 << 5,
    TextInput = 1 << 6,
    Scroll = 1 << 7
}
```

业务任务可以检查能力，但不能直接判断 `TargetKind` 后调用传输层代码。

### 2.1 GUI 暴露边界

抓帧和输入后端的选择属于运行适配层，不属于目标任务配置。单目标阶段采用以下规则：

- 默认使用 `Auto`，由目标提供者按能力探测结果选择后端。
- `capture.preference` 与 `input.preference` 放在“设置 / 运行时”，捕获页只显示当前生效状态。
- 设置页根据当前 `TargetKind` 过滤选项。例如 Windows PC 目标不展示 MuMu 输入方式，模拟器目标也不展示 `SendInput`。
- 手动选项只允许指定“首选后端”，最终仍受能力探测和运行时失败回退约束。
- 多会话阶段，设置页保存各目标类型的默认策略，会话高级设置可以提供覆盖值；业务任务始终不感知 GUI 选择。

候选后端及稳定回退顺序如下：

| 目标 | 捕获方式 | 交互方式 |
|---|---|---|
| Windows PC | `Auto`: WGC -> PrintWindow + BitBlt -> BitBlt | `Auto`: PostMessage -> 前台 SendInput |
| MuMu | `Auto`: 原生 IPC -> ADB 兼容路径 | `Auto`: 原生 IPC -> ADB 兼容路径 |

实际生效的后端必须写入会话状态和诊断日志，便于确认发生了哪种降级。

## 3. 帧模型

核心层不跨线程长期传递可变的 `System.Drawing.Bitmap`。统一使用带生命周期的帧对象：

```csharp
public sealed class FrameLease : IDisposable
{
    public long Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
}
```

实现要求：

- 每个会话只有一个抓帧循环。
- 新帧到达时替换 `LatestFrame`，旧帧在无订阅者后释放或归还缓冲池。
- 任务通过 `WaitNextAsync` 获取新帧，GUI 通过 `GetLatestAsync` 读取最新帧。
- 点击、滑动或关键页面切换后，业务层必须请求新帧，不能继续使用旧帧坐标。

## 4. Windows 实现

### 4.1 抓帧

Windows 抓帧后端按配置优先级尝试：

1. `Windows.Graphics.Capture`
2. `PrintWindow(PW_RENDERFULLCONTENT)` + BitBlt
3. 普通 BitBlt

后端选择由 `WindowsTargetProvider` 和会话内的 `WindowsFrameSource` 完成，业务层不可见。当前已接入 WGC：使用自由线程帧池和 D3D11 staging 读回，按窗口客户区裁切；`Auto` 在 WGC 失败或返回黑帧时回退，显式选择 WGC 时则保留错误供诊断，不静默降级。

需要处理：

- DPI 缩放和客户区到屏幕坐标转换。
- 多显示器负坐标。
- 窗口最小化、遮挡和硬件加速导致的黑帧。
- WGC 设备丢失后的重建。
- 窗口尺寸变化后的帧池重建。

### 4.2 输入

输入后端包括：

- `PostMessage`：适用于支持后台鼠标/键盘消息的客户端。
- 前台模式加 `PostMessage`：窗口需要前台状态，但仍使用目标窗口消息。
- `SendInput`：前台模式，必要时要求管理员权限。

如果 `PostMessage` 不被目标游戏接受，必须显式降级或提示用户，不能把“发送成功”当成“游戏已响应”。

## 5. MuMu 实现

第二阶段实现：

- `MuMuNativeSession`：优先调用官方原生 IPC，负责抓帧、触摸、滑动和按键。
- `MuMuAdbSession`：原生 IPC 不可用时使用 ADB 或更高效率的兼容协议。

原生 IPC 和 ADB 都必须以 `TargetBinding` 定位实例，不允许通过全局默认设备隐式操作。

## 6. 操作串行化

每个 `ITargetSession` 内含一个操作门控。所有抓帧切换、点击、滑动、按键和页面确认必须遵守：

- 同一会话同一时刻最多一个输入操作。
- 任务模块之间由 `SessionTaskCoordinator` 串行调度。
- GUI 预览只读帧，不发送输入。
- 调试命令需要显式获取操作门控，不能绕过任务协调器。

## 7. 坐标模型

- 业务配置和页面处理器统一使用相对坐标。
- `FrameLease` 提供实际像素尺寸，输入适配器负责转换为目标坐标。
- 页面区域应记录设计分辨率和允许的宽高比。
- `UiProfile` 决定使用哪套坐标、文字和模板配置。

## 8. 验证项

### Windows

1. WGC 和 BitBlt 在目标客户端的实际可用性。
2. `PostMessage` 是否能被游戏接受。
3. 管理员权限、UIPI 和前后台限制。
4. 最小化、遮挡和 DPI 变化下的抓帧稳定性。

### MuMu

1. 原生 IPC 函数签名、版本兼容和连接方式。
2. 后台保活开启后是否仍能拿到实际游戏画面。
3. 多实例并发抓帧和输入的稳定性。
4. 移动端对触摸、滑动和按键事件的响应。
5. ADB 兜底路径的性能和资源成本。