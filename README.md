# C# 自动化重写 — 设计文档

本文件夹存放将现有卡厄思梦境 Python 自动化项目重写为 C# (.NET) 版本的架构设计文档。

当前方案采用**统一目标架构、分阶段实现**：

1. **第一阶段实现 Windows 游戏窗口目标**，用于快速调试截图、输入、OCR、模板匹配、任务状态机和配置热更新。
2. **第二阶段接入 MuMu 模拟器目标**，复用同一套任务运行时、视觉层和配置系统，新增模拟器目标提供者即可。
3. Windows PC 客户端与 Android 移动端可能存在 UI 差异，因此“运行目标”和“游戏 UI 配置”必须分开建模。

本设计不再把整体架构限定为 MuMu，也不把 Windows 实现为一次性的调试分支。两种目标都必须实现统一的 `ITargetSession` 契约。

## 可行性结论

C# 重写可行，而且适合这个项目。Windows 目标实现可以直接通过 P/Invoke 调用 Win32 API，通过 COM/WinRT 使用 `Windows.Graphics.Capture`，并使用 `System.Drawing`、OpenCvSharp 和 ONNX Runtime 完成图像与 OCR 链路。

但“可以完全调用 Windows 底层”需要加一个边界：C# 可以调用公开的 Win32、COM 和 WinRT 接口，不能绕过 Windows 的安全模型。UIPI、进程完整性级别、管理员权限、会话隔离、受保护进程、反作弊以及独占图形表面，都可能让抓帧或输入失败。第一阶段必须把这些限制作为可检测、可降级、可提示的运行时能力，而不是假定所有窗口都能后台抓取和后台输入。
## 阅读顺序

1. [00-overview.md](./00-overview.md) — 项目目标、非目标、统一目标架构与分阶段路线
2. [01-architecture.md](./01-architecture.md) — 核心库、目标提供者、业务层和 GUI 的分层方式
3. [02-instance-and-window-management.md](./02-instance-and-window-management.md) — Windows/MuMu 目标发现、绑定、会话生命周期与多开管理
4. [03-capture-and-input-strategy.md](./03-capture-and-input-strategy.md) — 统一截图/输入接口、Windows 与 MuMu 的具体实现策略
5. [04-config-system-and-hot-reload.md](./04-config-system-and-hot-reload.md) — 可插拔配置来源、每会话独立配置与热更新
6. [05-task-framework-and-vision.md](./05-task-framework-and-vision.md) — 任务运行时、帧所有权、OCR 与模板匹配
7. [06-gui-design.md](./06-gui-design.md) — 多会话状态监视、单会话配置和共享预览帧
8. [07-migration-notes-from-python.md](./07-migration-notes-from-python.md) — 现有 Python 代码迁移对照与当前实际工作量
9. [08-open-questions-and-risks.md](./08-open-questions-and-risks.md) — Windows 起步验证项与 MuMu 阶段风险
10. [09-task-board-design.md](./09-task-board-design.md) — 任务方案、主任务模式、固定配置页与任务状态设计
11. [10-chaos-mode-migration-ledger.md](./10-chaos-mode-migration-ledger.md) — 混沌模式迁移台账与剩余工作
12. [11-region-slices-and-event-bus.md](./11-region-slices-and-event-bus.md) — 区域切片、事件总线和统一动作仲裁方案
13. [12-design-errors-and-guardrails.md](./12-design-errors-and-guardrails.md) — 已犯设计错误、C# 对象化修正和防复发清单

## 关键结论

- **统一目标模型**：核心库只认识 `TargetBinding`、`ITargetSession`、`IFrameSource` 和 `IInputSink`，不知道 `hwnd`、MuMu 实例索引或 Android KeyCode。
- **Windows 先落地**：第一阶段实现 Windows 目标，验证完整自动化链路和调试体验；该阶段不依赖 MuMu 原生 IPC。
- **MuMu 后接入**：第二阶段新增 MuMu 目标提供者，优先使用官方原生 IPC，ADB 作为兼容性兜底。
- **目标传输与 UI 配置分离**：`TargetKind` 表示 Windows/MuMu，`UiProfile` 表示 Windows PC/Android 移动端页面布局。共享业务规则，但页面识别、区域坐标和模板按 UI 配置分组。
- **单会话单操作序列**：同一目标同一时刻只允许一个业务任务操作。多个任务模块必须由 `SessionTaskCoordinator` 串行调度，禁止并发点击同一窗口或设备。
- **任务方案显式化**：每个会话在配置中声明一个主任务模式和有序辅助任务，主任务模式互斥，任务板块只负责方案、配置和状态。
- **共享帧源**：每个会话只有一个抓帧循环，任务和 GUI 预览订阅同一个带序号的帧，不为预览额外重复抓帧。
- **OCR 分阶段**：第一阶段复用已有 ONNX OCR 模型并放在 `IOcrEngine` 后面，先完成 C# 链路迁移；自训练模型作为后续独立工作线，不阻塞第一阶段。
- **核心库与业务层分离**：`Automation.Core` 不包含卡厄思梦境业务词汇；`Kes.Tasks` 只放游戏规则；`Kes.Layout.WindowsPc` 和 `Kes.Layout.AndroidMobile` 放不同客户端的页面配置。
- **配置以稳定 ID 为主键**：持久化键使用稳定英文 ID，显示中文名称；旧 Python 中文配置 key 通过迁移工具或别名映射兼容。

## 阶段验收

### 第一阶段：Windows 目标

1. 能发现、绑定并重新关联 Windows 游戏窗口。
2. 能通过 WGC/BitBlt 抓帧，通过 PostMessage/SendInput 输入。
3. 一个会话的截图、OCR、模板匹配、决策和点击链路可稳定运行。
4. 同一进程内多个 Windows 会话可以独立运行，且不会发生跨会话误操作。
5. 配置修改后无需重启任务即可在下一轮决策中生效。

### 第二阶段：MuMu 目标

1. 确认 MuMu 原生 IPC 函数签名、会话建立方式和版本兼容范围。
2. 验证后台保活开启后，抓到的仍是实际游戏画面。
3. 验证多实例并发截图/输入的性能、稳定性和资源上限。
4. 验证移动端客户端对触摸、滑动和按键事件的响应。
5. 确认 PC 与 Android 客户端的 UI 差异，完成对应的 `UiProfile` 配置。