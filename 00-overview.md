# 00. 项目概述与目标

## 1. 背景

现有卡厄思梦境自动化工具使用 Python 和 ok-script，主要面向单个 Windows 游戏窗口。重写为 C# 的主要目标不是简单更换语言，而是建立一套可同时容纳 Windows 游戏窗口和 Android 模拟器的统一自动化运行时。

Windows 客户端和 MuMu 模拟器承担不同角色：

- Windows 客户端适合第一阶段开发和调试：窗口枚举、抓帧、输入、OCR、坐标和页面状态机都更容易观察和定位问题。
- MuMu 适合后续多开运行：每个账号对应一个独立模拟器实例，多实例隔离和后台保活由模拟器提供。
- 两种目标的传输方式不同，但上层任务运行时、视觉层、配置系统和 GUI 应保持一致。

因此，架构必须先抽象出与传输方式无关的 `ITargetSession`，再分别实现 Windows 和 MuMu 目标提供者。

### 1.1 C# 可行性与 Windows 底层调用边界

C#/.NET 重写可行。Windows 下可以通过 P/Invoke、COM 和 WinRT 直接访问本阶段需要的主要能力：

- 窗口枚举、窗口消息、键盘鼠标输入和 DPI 相关 Win32 API。
- `Windows.Graphics.Capture`、`PrintWindow` 和 BitBlt 等抓帧路径。
- 多显示器、进程、窗口类和前台/后台状态查询。
- 与 OpenCvSharp、ONNX Runtime 等原生库的互操作。

这里的“底层”应理解为能够调用 Windows 提供的公开系统接口，而不是获得不受限制的进程权限。UIPI、管理员权限、完整性级别、会话隔离、受保护进程、反作弊和独占图形表面仍可能限制抓帧或输入。因此目标提供者必须声明 `TargetCapabilities`，在运行时检测失败并选择可用后端；需要内核级访问或绕过安全机制的能力不属于当前项目范围。
## 2. 核心目标

1. 建立统一目标模型，支持 Windows 游戏窗口和 MuMu 模拟器。
2. 第一阶段先打通 Windows 完整链路，降低调试成本。
3. 第二阶段接入 MuMu 原生 IPC 或 ADB，不修改业务任务的核心决策结构。
4. 每个会话拥有独立配置、独立状态和独立任务调度。
5. 同一目标同一时刻只执行一个业务操作序列，防止多个任务并发点击。
6. 将通用自动化能力与卡厄思梦境业务规则分离，核心库可独立测试和复用。
7. 将“运行目标”和“游戏 UI 配置”分开，允许 PC 与 Android 页面存在差异。

## 3. 非目标

- 第一阶段不做 MuMu 原生 IPC 的完整生产化接入。
- 不把 Windows 实现当成临时分支；Windows 目标必须遵守正式核心接口。
- 不做跨平台支持，当前运行平台仍为 Windows。
- 不实现通用的任务插件市场、在线模板标注系统或任意脚本市场。
- 不做多语言 UI，界面文案可以固定中文，内部代码标识和配置 key 使用稳定英文名。
- 不在第一阶段实现配置云同步和在线自动更新。
- 不假设 Windows PC 客户端和 Android 客户端的页面坐标、文字或流程完全一致。

## 4. 核心设计原则

### 4.1 目标与会话统一

核心库使用以下抽象：

- `TargetBinding`：如何持久化地定位一个目标。
- `ITargetProvider`：发现和连接某类目标。
- `ITargetSession`：一个已连接目标的抓帧、输入和生命周期入口。
- `IFrameSource`：单一抓帧循环和帧订阅。
- `IInputSink`：统一鼠标、触摸和键盘输入。

Windows 和 MuMu 只提供不同实现，不改变上层任务运行时。

### 4.2 运行目标与 UI 配置分离

```text
TargetKind: WindowsWindow | MuMuEmulator
UiProfile:  WindowsPc | AndroidMobile
```

业务规则可以共享，但页面处理器、区域坐标、OCR 文本、模板和页面优先级必须按 `UiProfile` 选择。

### 4.3 单会话单操作序列

一个会话同一时刻只允许一个任务操作目标。多个任务模块由 `SessionTaskCoordinator` 排队执行，禁止各自启动循环后并发点击同一目标。

### 4.4 单一帧源

每个活跃会话只运行一个抓帧循环。任务判断、调试预览和 GUI 监控订阅同一份带序号的帧，避免重复抓屏、帧状态不一致和底层接口并发冲突。

### 4.5 分阶段 OCR

第一阶段复用现有 PP-OCRv5 ONNX 模型，并通过 `IOcrEngine` 隔离模型实现；自训练游戏专用模型作为后续独立里程碑，不阻塞 C# 核心链路迁移。

### 4.6 配置热更新

每个会话使用独立配置文档。任务代码每次决策时读取当前配置引用，不长期缓存配置快照，确保 GUI 修改后在下一轮决策中生效。

## 5. 技术栈

| 用途 | 方案 | 说明 |
|---|---|---|
| 运行时 | .NET LTS 或公司允许的最新稳定版 | 优先选择长期支持版本 |
| GUI | WPF | 适合多会话监控、配置面板和 Windows 原生集成 |
| Windows 目标 | Win32 P/Invoke + COM/WinRT | 窗口枚举、WGC、BitBlt、PostMessage、SendInput |
| MuMu 目标 | 官方原生 IPC，ADB 兜底 | 第二阶段接入，先完成最小 Demo 验证 |
| 图像处理 | OpenCvSharp4 | 模板匹配、颜色和基础图像处理 |
| OCR | ONNX Runtime | 第一阶段复用现有模型，后续支持替换自训练模型 |
| 配置 | `System.Text.Json` | 原子写入、版本迁移和稳定 ID |
| 测试 | xUnit/NUnit + 假会话 | 使用假帧序列和假输入实现纯逻辑测试 |

## 6. 文档结构

- `01-architecture.md`：整体分层、项目结构、目标模型和并发模型。
- `02-instance-and-window-management.md`：目标发现、绑定、会话生命周期和多开管理。
- `03-capture-and-input-strategy.md`：Windows 与 MuMu 的抓帧和输入实现。
- `04-config-system-and-hot-reload.md`：配置来源、会话配置、热更新和迁移。
- `05-task-framework-and-vision.md`：任务运行时、帧所有权和视觉层。
- `06-gui-design.md`：多会话 GUI 和共享预览帧。
- `07-migration-notes-from-python.md`：Python 代码迁移对照和当前工作量。
- `08-open-questions-and-risks.md`：阶段验证项和风险清单。