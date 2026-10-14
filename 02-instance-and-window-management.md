# 02. 目标、实例与会话管理

> 本文档统一使用“目标”和“会话”两个概念，不再把模拟器实例当作核心库的基础模型。

## 1. 目标

- 支持发现、绑定和重新关联 Windows 游戏窗口。
- 支持在第二阶段发现 MuMu 实例并绑定为同一种逻辑会话。
- 允许同一进程同时管理多个会话，包括混合的 Windows 和 MuMu 会话。
- 每个会话拥有独立配置、独立状态和独立任务协调器。
- 运行时句柄失效时，能够按持久绑定重新定位目标。

## 2. 概念区分

| 概念 | 含义 | 是否持久化 |
|---|---|---|
| `TargetKind` | WindowsWindow 或 MuMuEmulator | 是 |
| `TargetBinding` | 用于重新找到目标的组合标识 | 是 |
| `ITargetSession` | 当前进程内已连接的抓帧/输入会话 | 否 |
| `SessionId` | 本工具内部的稳定运行单元标识 | 是 |
| `UiProfile` | WindowsPc 或 AndroidMobile | 是 |
| `AccountLabel` | 用户可读的账号备注 | 是，仅用于展示和筛选 |

`hwnd`、MuMu 原生句柄和 ADB 临时端口都只属于运行时状态，不能直接作为持久化主键。

## 3. TargetBinding

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

- `ProviderInstallationId` 用于区分不同安装位置或版本环境。
- `ProviderInstanceId` 在 Windows 下对应逻辑窗口特征，在 MuMu 下对应实例索引或管理工具返回的稳定标识。
- `Fingerprint` 保存进程路径、窗口类、标题模式或 Android serial 等辅助信息。
- `ManualPin` 表示用户手动确认绑定，自动发现不得覆盖。

## 4. Windows 目标发现与绑定

### 4.1 发现

Windows 目标提供者通过 Win32 API 枚举候选窗口：

- `EnumWindows`
- `GetWindowThreadProcessId`
- `QueryFullProcessImageName`
- `GetClassName`
- `GetWindowText`
- `GetClientRect` 和 DPI 转换

候选信息至少包括：

- 进程名称和完整路径
- 窗口类名
- 窗口标题
- 客户区尺寸
- 是否可见
- 是否属于当前用户会话

### 4.2 当前原型的目标档位

桌面原型不向用户展示任意可见窗口，只暴露两个运行目标档位：

- `PC 版`：只接受进程名为 `ssr-xcent.exe` 或 `ssr-stove-shield.exe` 的窗口。
- `模拟器`：只接受实际承载 MuMu 画面的 `MuMuPlayer.exe`、`MuMuNxDevice.exe` 或 `NemuPlayer.exe` 窗口。

GUI 每 2 秒重新检查一次目标是否存在，并持续显示“检测到 / 未检测到”。其他应用窗口不会进入候选列表，也不会被导出到诊断目标集合。
### 4.3 绑定

Windows 的 `hwnd` 每次启动可能变化，因此持久绑定使用组合特征：

```text
exe path + window class + title pattern + client size + user pin
```

如果只能匹配到多个候选窗口，必须让用户选择，不能按枚举顺序自动绑定。用户手动指定后，以 `ManualPin` 保存该选择。

### 4.4 运行时恢复

启动或重连时：

1. 枚举当前窗口。
2. 按 `TargetBinding` 计算候选。
3. 唯一高置信匹配时自动恢复。
4. 多个候选或零候选时标记为 `NeedsAttention`，由 GUI 要求用户重新确认。
5. 恢复成功后更新运行时 `hwnd`，不修改持久绑定。

## 5. MuMu 目标发现与绑定

第二阶段通过 MuMu 管理工具、原生 IPC 或 ADB 查询实例列表。候选信息至少包括：

- 实例索引
- 安装位置
- 实例状态
- Android serial 或等价稳定标识
- 显示名称

绑定使用 `ProviderInstallationId + ProviderInstanceId + Fingerprint`。仅在实例索引一个字段上建立持久关系是不够的，因为删除重建、重装或多个 MuMu 环境都可能造成冲突。

MuMu 的官方接口细节封装在 `Automation.Targets.MuMu`，核心库不出现 MuMu 类型。

## 6. 会话生命周期

```text
Discovered
  -> Bound
  -> Starting
  -> TransportReady
  -> GameReady
  -> Running
  -> Paused
  -> Stopped
  -> Faulted
  -> NeedsAttention
```

- `TransportReady` 表示目标进程或模拟器 IPC/ADB 可用。
- `GameReady` 表示游戏已经进入可操作页面，由业务层 `IReadinessProbe` 判断。
- `Paused` 只暂停任务，不等价于关闭目标。
- `Faulted` 表示抓帧/输入失败或任务异常，需要隔离当前会话。
- `NeedsAttention` 表示无法自动重新绑定目标，需要用户处理。

一个会话的故障不能中断其他会话的循环。

## 7. 会话与账号关系

每个会话通常对应一个固定账号，但架构不强制：

- `SessionId` 是配置和调度主键。
- `AccountLabel` 只是用户可读备注。
- 一个账号未来可以在不同目标之间迁移，但迁移属于显式操作。
- 自动登录和账号切换不属于核心库职责。

## 8. 多开与资源治理

- 每个会话拥有独立操作门控和状态。
- Windows 会话和 MuMu 会话可以混合存在。
- 抓帧/OCR 的并发上限由 `VisionScheduler` 统一控制。
- GUI 显示每会话资源占用和最近错误，便于判断多开上限。
- 新发现目标只作为候选提示，用户确认后才创建 `AutomationSession`。