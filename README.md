# Kes Automation

面向《卡厄思梦境》Windows 客户端的图像识别自动化工具。通过窗口抓帧、OCR、模板匹配和键鼠输入完成重复性流程，不修改游戏文件或数据。

当前版本以 Windows PC 客户端为主，提供后台抓帧、多窗口独立任务、可视化任务方案，以及用于排查识别问题的开发测试工具。

## 主要能力

### 自动卡厄思模式

- 识别路线与节点类型，按任务方案推进探索流程。
- 处理事件选择、战斗衔接、结算与继续等常见页面。
- 处理卡牌奖励、分配、复制、移除、闪光与装备选择。
- 支持商店、休息、宝库、存储数据等流程。
- 按配置处理主战员、卡牌优先级、路线优先级和赛季专属内容。
- 主战员头像可随任务方案保存，减少重复识别并便于查看。

### 窗口与任务

- 自动发现可绑定的游戏窗口，只保留与游戏相关的运行目标。
- 每个窗口可使用独立任务方案、配置和运行状态。
- 同一窗口的操作按顺序执行，避免多个任务同时点击。
- 任务方案可保存、切换和复用，方便为不同窗口分配不同目标。

### 识别与交互

- 支持 Windows Graphics Capture、PrintWindow 和 BitBlt 抓帧，并根据运行环境自动选择可用方式。
- 使用 ONNX OCR、OpenCvSharp 模板匹配和区域识别理解画面。
- 支持后台窗口消息输入和前台键鼠输入，点击前会结合当前画面确认目标。
- 共享每个会话的抓帧循环，任务判断和界面预览使用同一份带序号的画面。

### 开发测试

- 查看 1:1 游戏画面，读取坐标并保存截图。
- 框选区域进行 OCR、模板查找和裁剪预览。
- 记录消费者收到的页面样本、OCR 文本和识别信息，便于复现问题。
- 查看会话日志、任务状态、当前动作和性能信息。

## 运行环境

- Windows 10/11 x64
- .NET 9 SDK（仓库通过 `global.json` 固定 SDK 版本）
- 16:9 比例的游戏画面

后台抓帧和后台输入能力取决于 Windows 版本、游戏显示模式和运行权限。程序会在启动和运行过程中报告实际使用的抓帧方式及失败原因。

## 快速开始

1. 启动游戏并进入需要自动化的界面。
2. 打开终端，进入项目目录。
3. 运行启动脚本：

```powershell
.\run-gui.cmd
```

也可以直接使用 .NET CLI：

```powershell
dotnet restore
dotnet run --project Kes.Gui\Kes.Gui.csproj
```

程序启动后，选择游戏窗口，为窗口配置或选择一个任务方案，然后启动任务。

## 项目结构

| 目录 | 说明 |
|---|---|
| `Automation.Core` | 平台无关的目标、会话、抓帧、输入、任务和配置契约 |
| `Automation.Targets.Windows` | Windows 窗口发现、抓帧与输入实现 |
| `Automation.Vision.Onnx` | ONNX OCR 与视觉识别能力 |
| `Kes.Tasks` | 卡厄思梦境任务、页面判断与业务配置 |
| `Kes.Gui` | WPF 桌面界面、任务方案和开发测试工具 |
| `Kes.Layout.WindowsPc` | Windows PC 客户端页面布局与识别配置 |
| `Kes.Layout.AndroidMobile` | 移动端页面布局资料与后续兼容配置 |
| `Automation.Core.Tests` | 纯逻辑、页面路由与回归测试 |

## 设计文档

- [00-overview.md](./00-overview.md)：项目目标、能力边界与阶段范围
- [01-architecture.md](./01-architecture.md)：整体分层与核心模型
- [02-instance-and-window-management.md](./02-instance-and-window-management.md)：窗口发现、绑定与会话生命周期
- [03-capture-and-input-strategy.md](./03-capture-and-input-strategy.md)：抓帧和输入策略
- [04-config-system-and-hot-reload.md](./04-config-system-and-hot-reload.md)：配置与热更新
- [05-task-framework-and-vision.md](./05-task-framework-and-vision.md)：任务运行时与视觉层
- [06-gui-design.md](./06-gui-design.md)：桌面界面与多会话监视
- [09-task-board-design.md](./09-task-board-design.md)：任务方案与状态展示
- [11-region-slices-and-event-bus.md](./11-region-slices-and-event-bus.md)：区域切片与帧派发
- [12-design-errors-and-guardrails.md](./12-design-errors-and-guardrails.md)：设计边界与防复发约定
- [14-ocr-robustness-proposals.md](./14-ocr-robustness-proposals.md)：OCR 稳定性改进

## 开发与验证

```powershell
dotnet build AutomationPlatform.slnx
dotnet test Automation.Core.Tests\Automation.Core.Tests.csproj
```

界面启动脚本会先构建整个解决方案，再运行 WPF 客户端。

## 使用须知

本项目用于减少重复操作，使用时请遵守游戏服务条款和所在地法律法规。自动化行为存在账号限制、页面变化和运行环境差异等风险，请自行评估并承担使用后果。

当前自动化流程基于截图与输入模拟，不修改游戏文件或数据。遇到无法识别的页面时，建议保留开发测试样本和日志，以便定位具体流程。
