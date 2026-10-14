# 01. 整体架构

## 1. 设计前提

架构分为三个正交维度：

1. **运行目标**：数据从哪里抓、输入发到哪里。例如 Windows 窗口或 MuMu 模拟器。
2. **游戏 UI 配置**：任务面对的页面布局是哪种。例如 Windows PC 或 Android 移动端。
3. **业务任务**：卡厄思梦境的路线、卡牌、装备刻印和页面决策规则。

核心库只负责前两层的通用机制和第三层的任务运行时契约，不包含卡厄思梦境业务词汇。

## 2. 分层概览

```text
┌─────────────────────────────────────────────────────────────┐
│ 业务层                                                       │
│ Kes.Tasks: ChaosMode / 配置 schema / 业务规则                 │
│ Kes.Layout.WindowsPc: PC 页面区域、文本和模板                 │
│ Kes.Layout.AndroidMobile: 安卓页面区域、文本和模板            │
└──────────────────────────┬──────────────────────────────────┘
                           │ 任务只依赖 TaskContext 和核心契约
┌──────────────────────────▼──────────────────────────────────┐
│ Automation.Core                                             │
│ Targeting: TargetBinding / ITargetSession / IFrameSource     │
│ Runtime: ITaskModule / TaskModuleCatalog / TaskCoordinator   │
│ Vision: IOcrEngine / ITemplateMatcher / FrameLease           │
│ Config: IConfigProvider / ConfigSnapshot / IConfigService   │
│ Orchestration: TargetManager / AutomationSession             │
└──────────────────────────┬──────────────────────────────────┘
                           │ 目标提供者实现核心接口
┌──────────────────────────▼──────────────────────────────────┐
│ Automation.Targets.Windows    Win32 / WGC / BitBlt / 输入     │
│ Automation.Targets.MuMu       原生 IPC / ADB                  │
│ Automation.Vision.Onnx        ONNX OCR / 模板实现             │
└─────────────────────────────────────────────────────────────┘
```

## 3. 解决方案结构

```text
AutomationPlatform/
├── AutomationPlatform.sln
├── Automation.Core/
│   ├── Targeting/
│   │   ├── TargetKind.cs
│   │   ├── TargetBinding.cs
│   │   ├── TargetCapabilities.cs
│   │   ├── ITargetProvider.cs
│   │   ├── ITargetLocator.cs
│   │   ├── ITargetSession.cs
│   │   ├── IFrameSource.cs
│   │   └── IInputSink.cs
│   ├── Frames/
│   │   ├── FrameLease.cs
│   │   └── FrameMetadata.cs
│   ├── Vision/
│   │   ├── IOcrEngine.cs
│   │   ├── ITemplateMatcher.cs
│   │   ├── VisionScheduler.cs
│   │   ├── Box.cs
│   │   └── MatchResult.cs
│   ├── Runtime/
│   │   ├── ITaskModule.cs
│   │   ├── TaskModuleDescriptor.cs
│   │   ├── TaskModuleCatalog.cs
│   │   ├── SessionTaskPlan.cs
│   │   ├── TaskContext.cs
│   │   ├── IReadinessProbe.cs
│   │   └── SessionTaskCoordinator.cs
│   ├── Configuration/
│   │   ├── IConfigProvider.cs
│   │   ├── IConfigService.cs
│   │   ├── IConfigView.cs
│   │   ├── ConfigSnapshot.cs
│   │   ├── FileConfigProvider.cs
│   │   └── ConfigChangeBus.cs
│   └── Orchestration/
│       ├── AutomationSession.cs
│       ├── SessionManager.cs
│       └── TargetManager.cs
│
├── Automation.Targets.Windows/
│   ├── WindowsTargetProvider.cs
│   ├── WindowsTargetLocator.cs
│   ├── WindowsTargetSession.cs
│   ├── WgcFrameSource.cs
│   ├── BitBltFrameSource.cs
│   └── WindowsInputSink.cs
│
├── Automation.Targets.MuMu/
│   ├── MuMuTargetProvider.cs
│   ├── MuMuNativeSession.cs
│   ├── MuMuAdbSession.cs
│   └── MuMuBindingResolver.cs
│
├── Automation.Vision.Onnx/
│   ├── OnnxOcrEngine.cs
│   └── OpenCvTemplateMatcher.cs
│
├── Automation.Gui.Shared/
├── Kes.Tasks/
├── Kes.Layout.WindowsPc/
├── Kes.Layout.AndroidMobile/
├── Kes.Gui/
├── Automation.Core.Tests/
└── Kes.Tasks.Tests/
```

## 4. 核心对象

### 4.1 TargetBinding

`TargetBinding` 是可持久化的目标定位信息，不暴露具体传输对象：

```csharp
public sealed record TargetBinding
{
    public required TargetKind Kind { get; init; }
    public required string ProviderInstallationId { get; init; }
    public required string ProviderInstanceId { get; init; }
    public string Fingerprint { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool ManualPin { get; init; }
}
```

- Windows 的 `hwnd` 只属于运行时句柄，不写入持久化配置。
- MuMu 的实例索引与其他安装标识组合后形成绑定，不直接把单一索引当全局主键。

### 4.2 ITargetSession

```csharp
public interface ITargetSession : IAsyncDisposable
{
    TargetBinding Binding { get; }
    TargetCapabilities Capabilities { get; }
    IFrameSource Frames { get; }
    IInputSink Input { get; }
}
```

目标提供者负责创建、恢复和销毁会话。业务层不直接使用 Win32 或 ADB 类型。

### 4.3 AutomationSession

`AutomationSession` 是应用层运行单元，持有：

- 一个稳定的 `SessionId`
- 一个 `TargetBinding` 和当前 `ITargetSession`
- 一个 `UiProfile`
- 当前 `ConfigSnapshot` 和 `IConfigService`
- 任务协调器和状态快照

它与具体账号名称解耦。一个账号通常绑定一个会话，但架构不把这种关系写死。

## 5. 任务与并发模型

### 5.1 单会话单操作序列

同一会话内可以有多个已启用任务模块，但它们不能各自独立循环并直接点击目标。统一由 `SessionTaskCoordinator` 控制：

```text
SessionTaskCoordinator
  -> 选择一个可运行任务
  -> 执行一次 RunOnceAsync
  -> 释放操作门控
  -> 再选择下一个任务
```

这样既避免旧帧和新点击交错，也避免两个任务同时操作同一个页面。

### 5.2 帧源模型

每个活跃会话只有一个抓帧循环：

```text
Provider Capture
  -> LatestFrame(sequence, timestamp, lease)
       -> TaskContext
       -> GUI Preview
```

任务需要新帧时等待下一次帧序号；GUI 预览只读最新帧，不额外启动抓屏调用。

### 5.3 视觉并发

`IOcrEngine` 和 `ITemplateMatcher` 可由会话共享，但并发调用统一经过 `VisionScheduler`。调度器负责：

- 控制 OCR/模板匹配的最大并发度。
- 限制 GPU、内存和原生库调用压力。
- 记录队列等待时间，便于判断多开会话上限。
- 在取消任务时清理排队请求。

## 6. 启动流程

1. 加载全局配置和会话配置。
2. 初始化 `VisionScheduler` 和具体视觉实现。
3. 注册 Windows/MuMu 目标提供者。
4. 发现可绑定目标，或按 `TargetBinding` 恢复已有会话。
5. 为用户确认的会话创建 `AutomationSession`。
6. 选择 `UiProfile` 并注册对应任务模块。
7. 用户启动会话后，由 `SessionTaskCoordinator` 串行驱动任务。
8. GUI 订阅会话状态和共享帧源。

## 7. 依赖约束

- `Automation.Core` 不引用任何目标提供者、WPF、OpenCvSharp 或 ONNX Runtime 具体包。
- `Automation.Targets.Windows` 和 `Automation.Targets.MuMu` 只依赖 `Automation.Core`。
- `Kes.Tasks` 不直接引用 Win32、ADB、MuMu 或具体截图库。
- `Kes.Layout.WindowsPc` 与 `Kes.Layout.AndroidMobile` 只提供页面配置，不直接操作设备。
- `Kes.Gui` 只依赖业务层和公共 GUI 组件，不直接调用目标提供者。

## 8. 第一阶段实施边界

第一阶段只要求完成：

- 统一 `TargetBinding`、`ITargetSession`、`IFrameSource`、`IInputSink` 契约。
- Windows 目标发现、绑定、抓帧和输入。
- 单会话操作协调器。
- 配置热更新。
- OCR/模板匹配和页面处理器迁移。

MuMu 目标在第二阶段实现，但其接口约束从第一阶段开始固定，避免后续重构核心任务层。