# 07. 从 Python 项目迁移的对照说明

## 1. 目标

记录现有 Python 自动化项目中需要迁移的内容及对应关系，作为后续实现阶段的核对清单。本文档按当前仓库状态重新统计，不再沿用早期版本中的旧行数。

## 2. 当前代码体量

以下统计基于当前仓库中的 `.py` 文件，按空白行计入，结果为近似值：

| 文件 | 当前行数 | 内容 |
|---|---:|---|
| `ok_tasks/utils.py` | 5168 | 核心页面处理函数、文本归一化、共享辅助逻辑 |
| `ok_tasks/utils_chaos.py` | 972 | ChaosMode 业务逻辑 |
| `ok_tasks/utils_sortie.py` | 909 | SortieMode 业务逻辑，明确不迁移 |
| `ok_tasks/config_io.py` | 552 | 配置导入导出与本地配置 |
| `ok_tasks/utils_equipment_engraving.py` | 455 | 装备刻印分配逻辑 |
| `ok_tasks/engraving_log.py` | 425 | 刻印日志 |
| `ok_tasks/config_sync.py` | 415 | 云配置和热门配置，本次不迁移 |
| `ok_tasks/equipment_words.py` | 246 | 装备词条 |
| `ok_tasks/ChaosMode.py` | 193 | ChaosMode 入口与配置 schema |
| `ok_tasks/utils_story.py` | 109 | 剧情模式辅助逻辑 |
| `ok_tasks/SortieMode.py` | 102 | SortieMode 入口与配置 schema，明确不迁移 |
| 其他脚本 | 约 75 | StoryMode、GetMengbian、TestTrigger 等 |

合计约 9600 行 Python 业务和辅助代码。`src/` 下约 530 行主要属于应用启动、全局配置和示例 UI，不直接迁移。

实际迁移前应再次运行统计命令，避免在业务迭代后继续使用过期数字。

## 3. 明确不迁移的部分

| 现有功能 | 原因 | 后续处理 |
|---|---|---|
| `config_sync.py` | 不属于第一阶段多开和热更新目标 | 未来可作为 `IConfigProvider` 实现接入 |
| `SortieMode.py` / `utils_sortie.py` | 产品范围明确不做自动出击模式 | 仅保留历史参考，不新增 C# 配置、View、任务模块或占位 ID |
| 现有 PySide6 动态任务/配置 UI | 新版本使用固定 WPF View | 只迁移字段和交互含义 |
| 赞赏对话框 | 与自动化核心无关 | 不迁移 |
| ok-script 任务注册和动态控件工厂 | 新架构不依赖动态插件模型 | 用显式任务模块注册替代 |
| 多语言 UI 系统 | 第一阶段固定中文界面 | 内部 key 使用英文 |
| 现有模板标注工具链 | 首版简化为按 UI Profile/任务分目录 | 后续可单独建设 |

## 4. 新架构映射

| Python 内容 | C# 目标位置 |
|---|---|
| Windows 抓屏、窗口查找、输入 | `Automation.Targets.Windows` |
| MuMu/ADB 相关能力 | `Automation.Targets.MuMu`，第二阶段 |
| 任务调度和配置热更新 | `Automation.Core.Runtime` |
| OCR/模板匹配引擎 | `Automation.Vision.Onnx` |
| `PAGE_HANDLERS` 页面处理函数 | `Kes.Tasks` |
| PC/Android 页面差异 | `Kes.Layout.WindowsPc` / `Kes.Layout.AndroidMobile` |
| 文本归一化和业务算法 | `Kes.Tasks.Shared` |
| 固定配置界面 | `Kes.Gui` |

## 5. 迁移要求

### 5.1 页面处理器

现有约数十个 `handle_xxx` 函数按优先级列表执行。迁移时：

- 每个处理器保持单一职责。
- 处理器只接收 `TaskContext` 和取消令牌。
- 处理器按 `UiProfile` 选择区域、文字和模板。
- 不使用 Python 中的调用栈反射，日志名称通过参数或 `[CallerMemberName]` 显式传入。
- 给定固定帧和假输入即可测试。

### 5.2 配置

- 旧中文配置 key 通过 `LegacyKey` 映射到稳定英文 key。
- 列表和字典结构重新定义为强类型模型或明确的 `JsonNode` 结构。
- 保留导入导出能力，但第一阶段优先保证单会话本地配置。
- 配置迁移脚本最后执行，避免在业务逻辑中兼容每个历史字段。

### 5.3 OCR

当前 Python 项目使用 onnxocr/PP-OCRv5。第一阶段：

1. 复用现有 ONNX 模型。
2. 在 C# 中实现相同预处理、检测后处理、识别解码和缩放规则。
3. 建立固定帧对比测试，比较原 Python 与 C# OCR 的文本框和文字结果。
4. 自训练模型作为后续独立项目，不阻塞任务迁移。

### 5.4 文本归一化

当前 `_normalize_text`、简繁转换、编辑距离和子序列匹配属于业务层：

- 简繁/字形转换迁移到 `Kes.Tasks.Shared.TextNormalization`。
- 编辑距离、子序列和卡牌匹配留在业务层。
- 核心 `ConfigDocument` 不负责游戏文本归一化。

### 5.5 装备与刻印

装备词条、目标主战员判断和刻印分配是纯业务算法，适合通过固定输入输出做单元测试。不要把装备规则放入 `Automation.Core`。

## 6. 推荐迁移顺序

1. 定义 `TargetBinding`、`ITargetSession`、`IFrameSource`、`IInputSink` 和 `TaskContext`。
2. 实现 Windows 目标提供者，完成抓帧、输入和重新绑定。
3. 实现单会话 `SessionTaskCoordinator` 和共享帧源。
4. 用一个最小诊断任务验证抓帧、OCR、模板匹配和点击全链路。
5. 迁移通用页面处理器和文本归一化。
6. 迁移 ChaosMode；SortieMode 明确不迁移，不保留配置和界面占位。
7. 迁移装备词条和刻印规则。
8. 实现旧配置导入工具。
9. 第二阶段新增 MuMu 目标提供者和 AndroidMobile 布局配置。

MuMu 不在第一阶段阻塞开发。Windows 实现必须通过正式目标接口，不能写成之后无法复用的临时分支。