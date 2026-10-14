# 09. 任务板块设计

> 状态：设计基线，2026-10-12。任务目录、方案解析和会话卡片状态示例已实现；固定任务配置页尚未实现。任务方案导入与导出保留版本化包设计，当前实现仍只使用本地 `task-profiles.json`。
> 范围变更：自动出击模式 `SortieMode` 明确不迁移、不实现，也不保留配置和界面占位。

## 1. 定位与边界

“任务板块”是业务任务的用户界面和应用层管理边界，不是抓帧、输入或配置文件的底层实现。

任务板块负责：

- 为每个会话选择一套任务方案。
- 选择唯一的主任务模式。
- 开关有序的辅助任务。
- 编辑主任务模式的业务参数。
- 显示当前任务、步骤、进度、状态和错误。
- 启动、暂停、恢复和停止任务循环。
- 在保存成功后将配置变化应用到下一轮调度。

任务板块不负责：

- 发现或绑定 Windows 窗口。
- 创建抓帧循环和输入通道。
- 直接读写 JSON 文件。
- 直接调用任务模块的页面处理函数。
- 根据配置元数据动态生成 WPF 控件。
- 在运行中替换目标、抓帧后端、输入后端或 `UiProfile`。

底层运行时仍由 [05-task-framework-and-vision.md](./05-task-framework-and-vision.md) 定义，配置读写由 [04-config-system-and-hot-reload.md](./04-config-system-and-hot-reload.md) 定义。

## 2. 核心对象

### 2.1 任务模块描述符

模块描述符用于目录展示、兼容性校验和固定 View 路由，不参与页面决策：

```csharp
public enum TaskModuleKind
{
    PrimaryMode,
    Auxiliary,
    DeveloperDiagnostic
}

public sealed record TaskModuleDescriptor(
    string ModuleId,
    string DisplayName,
    string Description,
    TaskModuleKind Kind,
    IReadOnlyList<UiProfile> SupportedProfiles,
    int Order,
    string ConfigViewId,
    string MutualExclusionGroup);
```

约束：

- `ModuleId` 是稳定英文 ID，例如 `ChaosMode`。
- `DisplayName` 只用于显示，不参与配置文件主键。
- `PrimaryMode` 同一互斥组只能选择一个。
- `Auxiliary` 可以不选、单选或多选。
- `DeveloperDiagnostic` 不进入普通用户任务方案。
- `ConfigViewId` 映射到固定 XAML View，不使用反射动态创建控件。
- `Automation.Core` 只提供通用类型，具体模块描述符由 `Kes.Tasks` 注册。

### 2.2 任务方案

会话配置中的 `taskPlan` 是运行时模块集合的唯一来源：

```csharp
public sealed record SessionTaskPlan(
    string? PrimaryModuleId,
    IReadOnlyList<string> AuxiliaryModuleIds);
```

示例：

```json
{
  "taskPlan": {
    "primaryModuleId": "ChaosMode",
    "auxiliaryModuleIds": []
  }
}
```

- `primaryModuleId` 为空表示尚未选择主模式。
- 没有主模式时，正常用户任务不能启动。
- 开发构建可以临时附加 `Diagnostic`，但它不属于用户任务方案。
- 辅助任务按 `auxiliaryModuleIds` 的数组顺序调度。
- 未知模块 ID 是校验错误，不能静默忽略。

### 2.3 已解析任务方案

配置校验通过后生成不可变运行方案：

```csharp
public sealed record ResolvedTaskPlan(
    string? PrimaryModuleId,
    IReadOnlyList<TaskModuleDescriptor> Modules,
    ConfigRevision SourceRevision);
```

`ResolvedTaskPlan.Modules` 的顺序：

1. 主任务模式。
2. 辅助任务，按配置顺序。
3. 开发诊断任务，仅开发模式启用。

任务协调器只接收解析后的 `Modules`，不直接读取配置 JSON，也不自行决定主模式。

## 3. 任务分类

### 3.1 主任务模式

第一阶段只有一个产品级主任务模式：

| ModuleId | 显示名称 | 互斥组 | 支持 UI |
|---|---|---|---|
| `ChaosMode` | 自动卡厄思模式 | `gameMode` | `WindowsPc`，后续 `AndroidMobile` |

规则：

- `taskPlan` 保留 `primaryModuleId`，为未来确有新增模式时提供稳定扩展点。
- 当前 `primaryModuleId` 只允许 `ChaosMode` 或空值。
- 空值表示只连接目标，不运行产品级任务。
- 第一阶段不提供运行中切换主模式，也不保留 `SortieMode` 选项。
- 将来增加新模式前，必须先补充独立业务设计、配置 schema 和验收标准。

### 3.2 辅助任务

辅助任务用于与当前游戏模式无关、但需要共享同一目标的持续行为，例如：

- 自动处理系统弹窗。
- 自动领取通用奖励。
- 运行时健康检查。

约束：

- 辅助任务不能实现另一套完整游戏模式。
- 辅助任务必须声明执行顺序。
- 辅助任务不能绕过 `SessionTaskCoordinator` 直接点击。
- 当前阶段可以先保留接口，不注册任何产品级辅助任务。

### 3.3 开发诊断

`Diagnostic` 目前用于验证抓帧链路，不操作游戏：

- 只在开发调试开关开启时出现。
- 不写入普通用户 `taskPlan`。
- 可以独立运行，用于排查抓帧和帧序号问题。
- 在正式任务方案启用后不会默认常驻，避免重复占用帧等待时间。

## 4. 任务目录与模块工厂

任务目录是“描述符 -> 模块实例”的注册中心：

```csharp
public interface ITaskModuleCatalog
{
    IReadOnlyList<TaskModuleDescriptor> GetDescriptors(UiProfile uiProfile);

    ITaskModule Create(string moduleId);
}
```

`Kes.Tasks.TaskModuleCatalog` 负责注册实际模块。示例：

```csharp
public void Register(ITaskModuleCatalogBuilder builder)
{
    builder.AddPrimaryMode<ChaosModeTask>(
        id: "ChaosMode",
        displayName: "自动卡厄思模式",
        viewId: "ChaosModeConfig",
        supportedProfiles: [UiProfile.WindowsPc]);
}
```

要求：

- 每次 `Create` 返回新的有状态模块实例。
- 一个模块实例只能属于一个会话。
- 模块不能从静态字段读取当前配置。
- 模块所需的业务快照通过 `TaskContext.Config` 获取。
- 解析任务方案失败时保留当前运行方案，不用空方案覆盖。

## 5. 页面信息架构

左侧导航中的“任务”页面是任务板块主入口。页面采用主从结构：

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│ 任务                                                                        │
│ [当前会话] WIN-01 / 账号 1 / Windows PC / 运行中      [开始] [暂停] [停止] │
│ [未保存 2 项] [保存并应用] [重新加载] [导入] [导出]                         │
├─────────────────────────────────────────────────────────────────────────────┤
│ 会话                                                                        │
│ [WIN-01 运行中] [WIN-02 已暂停] [WIN-03 未启动]                             │
├──────────────────────────────┬──────────────────────────────┬───────────────┤
│ 任务方案                     │ 任务配置                     │ 运行状态      │
│                              │                              │               │
│ 主任务模式                   │ [基础] [路线] [卡牌]         │ 当前任务      │
│ (●) 自动卡厄思模式           │ [装备] [特殊]                │ 选择卡牌      │
│ ( ) 暂不选择                 │ 语言: [简体中文 v]           │ 进度 3 / 10   │
│                              │ 信用点阈值: [300]            │               │
│ 辅助任务                     │ 路线优先级: [...]            │ 帧 #182       │
│ [ ] 暂无                     │                              │ OCR 42 ms     │
│                              │                              │ 最近错误      │
└──────────────────────────────┴──────────────────────────────┴───────────────┘
```

窄窗口规则：

- 宽屏使用三列布局。
- 中等宽度将“运行状态”移到顶部会话摘要下方。
- 不使用会遮挡配置表单的浮动面板。
- 主操作按钮始终可见，不因滚动消失。

## 6. 页面区域职责

### 6.1 会话选择区

- 显示所有已创建会话。
- 每项显示 `SessionId`、显示名、目标类型、`UiProfile` 和运行状态。
- 选择会话只切换编辑上下文，不自动启动会话。
- 有未保存修改时切换会话必须提示保存、放弃或取消。
- 任务板块不提供移除会话操作，移除仍在捕获页完成。

### 6.2 任务方案区

- 主任务模式使用单选卡片或单选按钮，不用开关表示互斥关系。
- 辅助任务使用复选框或开关，按配置顺序显示。
- 当前正在运行的主任务显示运行标识。
- 不支持当前 `UiProfile` 的模块显示禁用状态和原因，不隐藏到用户无法理解。
- “暂不选择”是显式选项，用于只做目标连接和开发调试。

### 6.3 任务配置区

- 配置区只显示当前选中主任务模式的固定 View。
- 辅助任务有独立配置时显示对应的固定折叠区或标签页。
- 配置分区使用固定标签页，例如“基础、路线、卡牌、装备、特殊”。
- 字段使用下拉框、输入框、开关和列表编辑器，不根据 schema 创建未知控件。
- 字段下方显示校验错误；模块级错误显示在配置区顶部。
- 修改字段只更新 Draft，不立即写文件。
- 点击“保存并应用”后走 `IConfigService` 校验和原子写入。
- 写到下一个任务循环才生效的字段显示“下一轮生效”，不伪装成即时生效。

### 6.4 运行状态区

运行状态只消费结构化数据：

- 会话状态来自 `SessionViewModel.State`。
- 当前模块、步骤、进度和详情来自 `SessionActivity`。
- 配置 revision 和待应用状态来自配置服务。
- 帧序号、OCR 耗时和输入失败次数来自会话诊断快照。
- 最近错误来自活动快照和 `Faulted` 状态，不解析普通日志。

状态区至少显示：

- 当前任务模式。
- 当前步骤。
- 当前进度。
- 运行时长。
- 配置是否已应用。
- 最近更新时间。
- 故障说明和处理动作。

## 7. 用户操作流程

### 7.1 首次配置

1. 在“捕获”页创建并绑定会话。
2. 进入“任务”页选择该会话。
3. 选择主任务模式。
4. 编辑配置并保存。
5. 校验通过后点击“开始”。
6. 主页和任务页同时显示当前活动。

### 7.2 启动校验

点击“开始”前必须验证：

- 会话已绑定目标。
- 会话未处于 `Faulted`。
- 已选择主任务模式。
- 主任务支持当前 `UiProfile`。
- 主任务和已启用辅助任务的配置无 `Error`。
- 当前配置 revision 已成功保存。
- 没有尚未解决的配置冲突。

不通过时按钮保持可点击并显示原因，或显示明确的禁用提示。不能只把按钮置灰而不解释。

### 7.3 任务方案变更

第一阶段没有产品级主模式切换：

- 正常使用中 `primaryModuleId` 保持为 `ChaosMode`。
- 需要“只连接不运行任务”时，可以把主模式设为空。
- `taskPlan` 变更在停止当前任务循环后重建调度器，不重建目标、帧源和输入通道。
- 业务参数修改仍走热更新，在下一轮 `RunOnceAsync` 生效。

### 7.4 暂停、恢复和停止

- 暂停只暂停任务调度，不停止抓帧。
- 恢复从下一个模块边界继续，不重放旧步骤。
- 停止释放任务协调器，但不删除配置。
- 断开目标由会话停止流程负责，不能只停任务循环而保留半死抓帧。

### 7.5 配置保存和热更新

- 保存成功后更新 Baseline，Draft 变为干净状态。
- 配置变化在下一轮 `RunOnceAsync` 可见。
- 保存失败保留 Draft，并显示字段错误或 I/O 错误。
- 外部修改发生冲突时使用 [04-config-system-and-hot-reload.md](./04-config-system-and-hot-reload.md) 的 revision 规则。
- 不允许为了热更新重启 Windows 抓帧或输入通道。

## 8. 主模式配置分区

### 8.1 ChaosMode 固定分区

界面按“常用配置优先、赛季规则按需加载”组织：

1. **赛季内容**：顶部下拉，用于切换默认装备组和赛季专属配置组。
2. **刷取目标**：刷存档主战员，固定置顶且不可折叠。
3. **常用设置**：游戏语言、存储数据阈值、奖励、停止轮数、Boss 前暂停和任务选择，可折叠。
4. **装备**：不启用赛季内容时显示，包含三个装备位优先级。
5. **卡牌策略**：闪光、复制、移除、冥想，可折叠。
6. **路线**：两层路线优先级和只打第一层，可折叠。
7. **回荡在银河中的歌声 · 刻印与面具**：选择对应赛季后显示，同时隐藏默认装备组；包含面具卡牌、装备刻印、刻印专业模式及商店删卡门槛。
8. **破碎之光与利爪 · 欲望卡牌**：选择对应赛季后显示；包含欲望卡牌优先级。

赛季选择同时作为赛季专属功能的加载开关，不再保留独立的“刷装备刻印”布尔项；切换时不清空其他赛季已经填写的值。分区只影响 GUI 和配置 schema，不改变任务运行顺序。页面处理器仍由任务模块维护。

第一阶段不设计 `SortieMode` 配置页。只有将来新增并获得明确验收标准的任务模式，才增加对应 schema 和固定 View。

## 9. 状态模型

任务板块中的状态分为三层：

### 9.1 会话状态

```text
Stopped -> Starting -> Running -> Paused
                    \-> Faulted
                    \-> NeedsAttention
```

由会话生命周期控制，不由任务页面自行修改。

### 9.2 任务方案状态

```text
NotConfigured -> Ready -> Running -> Paused
                           \-> Faulted
```

- `NotConfigured`：没有主任务模式或配置无效。
- `Ready`：配置有效，等待启动。
- `Running`：协调器正在执行方案。
- `Paused`：任务调度暂停。
- `Faulted`：模块异常或目标能力失效。

### 9.3 当前活动

继续使用 `SessionActivity`：

- `TaskId` 对应 `ModuleId`。
- `TaskLabel` 使用 `DisplayName`。
- `StepId` 是稳定步骤 ID。
- `StepLabel` 是步骤显示名。
- `Detail` 是当前补充说明。
- `Current`、`Total` 只在有明确进度时填写。

任务板块不解析日志文本来推断状态。

## 10. 协调器接入

第一阶段只有一个产品级主任务模式，因此不需要模块集合热切换：

- 会话启动时按 `taskPlan` 构造模块列表。
- 正常列表为 `[ChaosMode]`。
- 开发模式可以追加 `Diagnostic`。
- 业务配置热更新不重建协调器。
- `taskPlan` 修改要求先停止任务循环，再创建新的协调器；目标会话和抓帧循环不重建。
- 当前不实现 `UpdateModules`，避免在只有一个主模式时引入不必要的并发切换复杂度。

将来出现多个可切换主模式或产品级辅助任务时，再单独设计模块集合的边界更新协议。

## 11. 校验与错误处理

### 11.1 配置错误

- 当前选中的主任务存在 `Error`：禁止启动。
- 已启用辅助任务存在错误：禁止启动。
- 模块不支持当前 `UiProfile`：禁止选择或禁止启动，并说明原因。
- 未知 `primaryModuleId`：不运行任何主任务，显示配置错误。

### 11.2 运行时错误

- 任务模块抛出异常：会话进入 `Faulted`，保留当前模块 ID 和异常详情。
- 抓帧等待超时：模块可以报告等待状态，不一定进入故障。
- 输入连续失败：任务页面显示会话故障和输入失败次数。
- 目标窗口消失：会话进入 `NeedsAttention`，任务方案保留。

## 12. ViewModel 边界

建议新增：

```text
Kes.Gui/ViewModels/Tasks/
├── TaskBoardViewModel.cs
├── SessionTaskCardViewModel.cs
├── TaskPlanEditorViewModel.cs
├── TaskModuleItemViewModel.cs
└── ChaosModeConfigViewModel.cs
```

职责：

- `TaskBoardViewModel` 管理会话选择和页面动作。
- `TaskPlanEditorViewModel` 管理主模式、辅助任务和 Draft 校验。
- `TaskModuleItemViewModel` 表示目录中的一项及兼容状态。
- 具体配置 ViewModel 只暴露强类型字段，不读写 JSON 文件。
- 任务页面不直接持有 `FileConfigProvider` 或文件路径。

固定 View：

```text
Kes.Gui/Views/Tasks/
├── TaskBoardView.xaml
├── TaskPlanEditorView.xaml
└── ChaosModeConfigView.xaml
```

## 13. 测试策略

必须使用假目录、假配置服务和假任务模块做纯逻辑测试：

- 无主模式时只允许开发诊断，不能启动产品任务。
- `ChaosMode` 不支持当前 `UiProfile` 时不能启动。
- 未知 `primaryModuleId` 产生配置错误。
- 业务配置保存失败时 Draft 不丢失。
- 外部 revision 冲突不会静默覆盖。
- 配置修改在下一轮任务决策中可见。
- `taskPlan` 修改会重建协调器但不重建目标会话。
- 当前活动通过 `SessionActivity` 展示，不解析日志。

不依赖真实游戏窗口、截图或前台鼠标键盘。

## 14. 任务方案导出与导入（预留设计）

> 状态：设计预留。当前不要求实现导出 UI，但内部模型、版本字段和安全边界必须为后续实现保留空间。内部 `task-profiles.json` 不是对外的交换格式。

### 14.1 导出对象与边界

导出单位是一套“已保存的任务方案快照”，而不是一个正在运行的会话：

- 包含：任务方案名称、`moduleId`、业务配置值、配置 schema 版本、运行时模板及其图片数据、方案包格式版本。
- 不包含：`ProfileId`、运行时会话 ID、窗口 `HWND`、`TargetBinding`、抓帧或输入后端选择、当前选中会话、任务运行状态、日志、开发测试截图。
- 导出时使用已保存 Baseline。页面存在未保存修改时，先提示保存；不能导出一份与列表中方案内容不一致的隐式 Draft。
- 导入后默认创建新的 `ProfileId`。源方案 ID 只作为包内溯源信息，不能覆盖或占用本机已有会话身份。
- 全局设置、捕获方式、交互方式不进入任务方案包，导入后继续使用本机设置。

### 14.2 包格式

对外文件使用扩展名 `.kestask`，底层是一个 ZIP 容器。选择容器而不是单个 JSON，是因为任务方案会包含头像、模板等二进制运行时资产，后续也可能增加更多资源。

```text
方案名.kestask
├── manifest.json
└── assets/
    ├── <sha256-1>.bgra
    └── <sha256-2>.bgra
```

`manifest.json` 使用 UTF-8，不允许绝对路径、`..` 路径或把可执行文件作为资源。V1 结构示例：

```json
{
  "format": "kes-task-profile",
  "formatVersion": 1,
  "packageId": "7f42f6af-b1d0-4aa7-b23d-dc8c0fc03944",
  "exportedAt": "2026-10-12T12:00:00+08:00",
  "createdBy": {
    "application": "KesAutomation",
    "version": "0.1.0"
  },
  "profile": {
    "name": "海德玛丽+回荡在银河中的歌声",
    "moduleId": "ChaosMode",
    "configSchemaVersion": 1,
    "values": {},
    "runtimeTemplates": [
      {
        "scope": {
          "kind": "TargetMemberAvatar",
          "targetName": "海德玛丽"
        },
        "featureName": "target_member",
        "assetId": "b5f3...",
        "assetPath": "assets/b5f3....bgra",
        "contentType": "application/vnd.kes.runtime-template-bgra",
        "width": 80,
        "height": 80,
        "sha256": "b5f3..."
      }
    ]
  },
  "extensions": {}
}
```

规则：

- `format` 和 `formatVersion` 决定容器解析方式；不得用应用版本代替格式版本。
- `configSchemaVersion` 决定 `values` 的迁移链，与包格式版本相互独立。
- 运行时模板不能导出内部使用的 `ProfileId|主战员名` 和 `\u001F` 拼接 key；导出时拆成结构化 `scope`、`featureName`，导入时再按新方案 ID 重建内部 key。
- V1 的 `.bgra` 资源保存原始 BGRA 像素，导入时校验宽度、高度和 `width * height * 4` 长度。后续版本可以增加 PNG 等 `contentType`，不能改变已有类型的含义。
- 每个资源以 SHA-256 内容地址去重并校验完整性；同一图片被多个特征引用时只保存一份。
- `extensions` 用于双方版本不一致时保留未知扩展数据。未知字段不能在导入后重新导出时被静默删除。
- 包内所有资源都必须先经过大小、数量、尺寸和像素总量限制校验，防止 ZIP 炸弹及异常图片耗尽内存。初始限制建议为：解压后 128 MiB、最多 256 个资源、单图最多 4096 x 4096。

### 14.3 版本兼容

- 高于当前支持范围的 `formatVersion`：拒绝导入并提示需要升级程序，不能做部分导入。
- 低版本包：通过集中注册的 `PackageVersion -> MigrationStep` 链迁移。
- `moduleId` 未知：方案可以导入并保存，但状态标记为“当前版本不支持”，不能分配运行，也不能被静默删除。
- `featureName` 未知：资源保存在方案的运行时模板区，当前版本不注册，重新导出时继续保留。
- 旧版单头像格式：导入时迁移为候选数组，再按当前“任意一个模板命中即可”的语义使用。
- 每种包版本迁移必须有纯逻辑测试，使用临时目录和假资源，不依赖游戏窗口。

### 14.4 导入流程

1. 用户选择 `.kestask` 文件。
2. 在写入正式配置前完成容器、manifest、哈希、资源尺寸和模块兼容性校验。
3. 显示预览：方案名称、主任务模式、配置版本、头像候选数量、警告和不兼容项。
4. 默认以“复制为新方案”导入，并生成新的 `ProfileId`。
5. 名称重复时自动追加 ` (2)`、` (3)`，不能静默覆盖已有方案。
6. 用户确认后，先构造完整的新 `TaskConfigProfile`，再一次性原子写入方案存储。
7. 任一步失败都不得修改当前方案、当前会话或正在运行的任务。
8. 导入完成后方案只进入“已保存方案”列表，是否分配给会话由用户另行选择。

第一阶段不实现“原地替换已有方案”。原地替换会牵涉运行中会话、未保存 Draft 和 revision 冲突，留到有明确交互和测试后再做。

### 14.5 UI 约定

- 任务方案区提供“导出方案”和“导入方案”两个显式命令，不使用拖放作为唯一入口。
- 当前方案有未保存修改时，导出命令先执行保存确认流程。
- 导入使用独立预览页或对话框，不能选择文件后立即覆盖列表。
- 导入结果需要显示“已新增方案”，不能把导入误报为“已应用到此会话”。
- 运行中的会话可以继续运行；导入产生的新方案在停止并重新分配任务循环后才生效。

### 14.6 代码边界

后续实现建议拆成以下边界：

```text
Kes.Tasks/Configuration/Packages/
├── TaskProfilePackageManifest.cs
├── TaskProfilePackageExporter.cs
├── TaskProfilePackageImporter.cs
├── TaskProfilePackageValidator.cs
└── TaskProfilePackageMigrations.cs
```

GUI 只调用导入/导出服务，不直接操作 ZIP、JSON 或路径解析。任务模块不感知方案包格式。

### 14.7 必测场景

- 导出再导入后，业务值、数组顺序、所有头像候选和像素内容完全一致。
- 多头像方案完整往返，未知特征和 `extensions` 不丢失。
- 导出包不包含 `ProfileId`、`TargetBinding`、`HWND`、日志和运行状态。
- 损坏 ZIP、错误哈希、路径穿越、超限尺寸和超限资源数量全部拒绝。
- 旧包版本迁移成功后可用，高版本包明确拒绝。
- 名称冲突生成副本，导入失败不改变现有方案。
- 有未保存 Draft 时不能静默导出 Baseline；保存失败不导出。

## 15. 实施顺序

### 阶段 1：任务目录和方案解析

1. 在 `Automation.Core.Runtime` 增加描述符、模块种类和 `SessionTaskPlan`。
2. 在 `Kes.Tasks` 建立模块目录与工厂。
3. 增加 `ResolvedTaskPlan` 和兼容性校验。
4. 为方案解析建立纯逻辑测试。

### 阶段 2：协调器和配置接入

1. 把 `taskPlan` 加入会话配置 schema。
2. 会话启动时按 `taskPlan` 构建固定模块列表并创建模块实例。
3. 配置变化在下一轮任务决策中应用。
4. 为模块创建和生命周期建立纯逻辑测试。

### 阶段 3：任务板块骨架

1. 增加任务页面 ViewModel 和固定 XAML。
2. 显示会话选择、当前任务方案和运行状态。
3. 接入保存、重新加载、启动、暂停、停止。
4. 暂不实现完整 ChaosMode 配置时，只显示任务方案和诊断模式。

### 阶段 4：ChaosMode 配置页

1. 迁移 ChaosMode 配置 schema。
2. 建立固定分区 ViewModel。
3. 接入字段校验和 hot reload。
4. 在任务模块实现前，可以先用不点击游戏的 stub 验证配置和生命周期。

### 阶段 5：任务方案包

1. 固定 `.kestask` V1 manifest、资源类型和版本兼容规则。
2. 实现基于已保存 Baseline 的纯逻辑导出器。
3. 实现先校验、后原子提交的导入器，并生成新的 `ProfileId`。
4. 完成多头像、损坏包、版本迁移、路径安全和冲突处理测试。
5. 最后接入固定 WPF 导入/导出命令和预览界面。

## 16. 验收标准

- 用户能明确看到每个会话当前运行的是哪套任务方案。
- 产品级任务列表只包含明确批准的模式，当前只有 ChaosMode。
- 配置保存失败不会清空用户输入。
- 配置修改在下一轮任务中使用，不需要重启目标。
- 当前步骤、进度、错误和配置生效状态集中显示。
- 任务页面增长新模块时增加固定 View，不引入动态 UI 引擎。
- 所有方案解析、校验和生命周期测试都能脱离游戏运行。
- 任务方案包能完整携带配置和运行时头像模板，但不携带窗口绑定或运行状态。
- 导入始终经过校验并生成新方案 ID，失败不会破坏已有方案。
- 包格式、配置 schema 和资源类型各自使用独立版本，未知数据可以安全保留。
