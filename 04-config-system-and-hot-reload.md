# 04. 配置系统与热更新

> 状态：设计基线，2026-10-11。当前只有设计，代码尚未实现。

## 1. 目标与边界

配置系统必须满足：

- 每个会话拥有独立的业务配置。
- 全局设置与会话配置分离，切换 Windows/MuMu 时不复制或覆盖业务规则。
- 业务参数修改后无需重启目标或任务，在下一轮任务决策开始生效。
- 配置 key 使用稳定英文 ID，界面显示中文名称。
- 配置损坏、校验失败或写入中断时，继续使用上一份有效配置。
- 支持后续导入旧 Python 配置，但迁移逻辑不进入业务运行时代码。
- GUI 使用固定 WPF 视图；配置 schema 只负责字段定义、默认值和校验，不动态创建 UI 控件。

第一阶段不实现：

- 云同步、热门配置上传和账号系统。
- 配置的多设备合并。
- 运行中热切换 `UiProfile`。
- 运行中替换当前会话的抓帧或输入后端。
- 用户自定义脚本式配置界面。

## 2. 核心原则

1. **配置读取是不可变快照**：任务一次决策只使用一个版本的配置，不读取半写入状态。
2. **作用域分层**：内置默认值 -> 全局配置 -> 会话配置。会话文件只保存覆盖项。
3. **稳定身份与显示名称分离**：文件主键不使用“账号 1”或运行时 `WIN-01`。
4. **只在边界生效**：热更新不会打断当前页面点击序列，也不会替换正在使用的帧源。
5. **最后有效版本优先**：新文件无效时保留内存中的 last-known-good，并显示错误状态。
6. **固定 UI，强类型字段**：任务声明字段 schema，WPF 页面按固定布局绑定 ViewModel。
7. **未知字段保留**：新版本写出的字段不能在旧版本保存时被静默删除。

## 3. 配置作用域与文件布局

部署环境使用用户目录，避免程序安装在 `Program Files` 后无写权限：

```text
%LocalAppData%/KesAutomation/
├── configs/
│   ├── global.json
│   ├── bindings.json
│   └── sessions/
│       ├── <profileId-1>.json
│       └── <profileId-2>.json
└── backups/
    └── configs/
```

开发阶段允许通过环境变量覆盖根目录：

```text
KES_CONFIG_ROOT=D:/work/win_ok/.local-config
```

根目录解析必须集中在一个 `IConfigRootLocator` 中，测试使用临时目录，禁止任务代码自行拼路径。

### 3.1 稳定会话档案 ID

`SessionId` 是本次运行期间的短标识，例如 `WIN-01`，不能作为长期文件主键，因为它会随添加顺序变化。

每个用户确认的会话创建一个稳定 `ProfileId`：

- 首次创建时生成 UUID，例如 `7f42...`。
- `ProfileId` 创建后永不改变。
- 会话显示名、账号标签、`UiProfile` 和 `TargetBinding` 都挂在 `ProfileId` 下。
- 配置文件始终命名为 `sessions/<profileId>.json`。
- 日志同时记录 `ProfileId`、运行时 `SessionId` 和显示名，便于定位。

### 3.2 `bindings.json`

该文件保存“会话档案如何重新找到目标”，不保存运行时 `hwnd`：

```json
{
  "schemaVersion": 1,
  "profiles": [
    {
      "profileId": "7f42f6af-b1d0-4aa7-b23d-dc8c0fc03944",
      "displayName": "账号 1",
      "accountLabel": "账号 1",
      "uiProfile": "WindowsPc",
      "binding": {
        "kind": "WindowsWindow",
        "providerInstallationId": "windows",
        "providerInstanceId": "123456:Game.exe:UnityWndClass",
        "fingerprint": "Game.exe|UnityWndClass|1600x900",
        "displayName": "游戏客户端",
        "manualPin": true
      }
    }
  ]
}
```

约束：

- Windows 的 `HWND` 只在运行时解析和缓存。
- 重新发现失败时保留档案，不删除会话配置。
- 同一 `ProfileId` 只允许绑定一个目标。
- `bindings.json` 由 `ISessionProfileStore` 读写，不复用业务配置 Provider。
- 后续可增加 `lastSeenAt` 等诊断字段，但不能影响会话身份。

### 3.3 `global.json`

全局配置保存所有会话共享、但不属于任务规则的设置：

```json
{
  "schemaVersion": 1,
  "runtime": {
    "WindowsWindow": {
      "capturePreference": "Auto",
      "inputPreference": "Auto"
    },
    "MuMuEmulator": {
      "capturePreference": "Auto",
      "inputPreference": "Auto"
    }
  },
  "vision": {
    "ocrConcurrency": 1,
    "templateConcurrency": 1,
    "ocrModelDirectory": ""
  },
  "logging": {
    "minimumLevel": "Information",
    "retentionDays": 14
  }
}
```

- GUI 的设置页只显示当前目标类型对应的 `runtime` 分组。
- `Auto` 表示按目标提供者声明顺序探测和回退。
- `capturePreference` 和 `inputPreference` 修改后只在新会话启动或显式重连时生效。
- 旧版本缺字段时由内置默认值补齐，不要求用户手工升级文件。

### 3.4 `sessions/<profileId>.json`

会话文件保存业务任务值和显式覆盖项：

```json
{
  "schemaVersion": 1,
  "profileId": "7f42f6af-b1d0-4aa7-b23d-dc8c0fc03944",
  "uiProfile": "WindowsPc",
  "taskPlan": {
    "primaryModuleId": "ChaosMode",
    "auxiliaryModuleIds": []
  },
  "modules": {
    "ChaosMode": {
      "values": {
        "gameLanguage": "简体中文",
        "creditMeditationThreshold": 300,
        "routePriorityLayer1": ["商店", "休息", "事件", "小怪", "精英"],
        "blacklistedTasks": ["咒术卡牌", "压力"]
      }
    }
  },
  "unknownValues": {}
}
```

- `taskPlan` 是运行时模块集合的唯一来源，主任务模式和辅助任务在此明确声明。
- `primaryModuleId` 为空表示尚未选择主任务模式，只允许开发诊断任务运行。
- `auxiliaryModuleIds` 的顺序就是辅助任务的调度顺序。
- 缺失模块的值使用默认值，不表示模块自动启用。
- `taskPlan` 修改在下一轮调度边界生效；切换主任务模式时重建有状态模块实例。
- `values` 只保存与默认值不同或用户明确确认过的值。
- `uiProfile` 必须与绑定档案一致；修改它标记为“重启会话后生效”。
- 本地配置方案将来放在 `configs/presets/<mode>/<name>.json`，不混入当前运行配置。

## 4. 核心契约

### 4.1 配置快照

核心层不直接暴露可变的 `JsonObject`：

```csharp
public enum ConfigScope
{
    Global,
    Session
}

public readonly record struct ConfigRevision(string Value);

public sealed record ConfigSnapshot(
    ConfigScope Scope,
    string ScopeId,
    ConfigRevision Revision,
    JsonObject Values,
    JsonObject UnknownValues,
    DateTimeOffset UpdatedAt,
    ConfigOrigin Origin);

public enum ConfigOrigin
{
    BuiltInDefaults,
    LocalFile,
    ImportedLegacy,
    Migration
}
```

`Revision` 由规范化 JSON 内容计算，不使用文件时间作为唯一依据。相同内容重新写入时保持同一 revision。

### 4.2 Provider 与监听

```csharp
public interface IConfigProvider
{
    ValueTask<ConfigSnapshot> GetAsync(
        ConfigScope scope,
        string scopeId,
        CancellationToken ct = default);

    ValueTask<ConfigWriteResult> SaveAsync(
        ConfigScope scope,
        string scopeId,
        JsonObject document,
        ConfigRevision expectedRevision,
        CancellationToken ct = default);

    IAsyncEnumerable<ConfigChangedEventArgs> WatchAsync(
        ConfigScope scope,
        string scopeId,
        CancellationToken ct = default);
}

public sealed record ConfigWriteResult(
    bool Succeeded,
    ConfigSnapshot? Snapshot,
    ConfigWriteFailure Failure);

public enum ConfigWriteFailure
{
    None,
    RevisionConflict,
    ValidationFailed,
    IoError
}
```

- 不使用长期 `event`，避免 GUI 关闭后仍持有订阅者。
- `WatchAsync` 使用有界 Channel，同一作用域只保留最新一个待处理版本。
- `SaveAsync` 使用 `expectedRevision` 做乐观并发控制，防止 GUI 覆盖外部修改。
- 第一阶段实现 `FileConfigProvider`，不实现远程 Provider。

### 4.3 配置服务

`IConfigProvider` 只负责可靠的文件读写。默认值合并、schema 版本迁移、字段校验和业务规则由 `IConfigService` 负责：

```csharp
public interface IConfigService
{
    ValueTask<ConfigSnapshot> GetGlobalAsync(CancellationToken ct = default);

    ValueTask<ConfigSnapshot> GetSessionAsync(
        string profileId,
        CancellationToken ct = default);

    ValueTask<ConfigValidationResult> ValidateSessionAsync(
        string profileId,
        JsonObject document,
        CancellationToken ct = default);

    ValueTask<ConfigWriteResult> SaveSessionAsync(
        string profileId,
        JsonObject document,
        ConfigRevision expectedRevision,
        CancellationToken ct = default);

    IAsyncEnumerable<ConfigChangedEventArgs> WatchSessionAsync(
        string profileId,
        CancellationToken ct = default);
}
```

调用关系：

```text
任务 -> IConfigView
GUI  -> IConfigService
Service -> schema / validator / migrator -> IConfigProvider -> 文件
```

- `IConfigProvider` 不理解游戏字段。
- `IConfigService` 不理解 WPF 控件，也不直接依赖具体任务模块实现。
- GUI 不直接调用 `FileConfigProvider`，也不直接拼接配置文件路径。

### 4.4 强类型字段定义

任务层声明字段，核心层提供通用描述和验证能力：

```csharp
public sealed record ConfigKey<T>(
    string Id,
    string DisplayName,
    T DefaultValue);

public sealed record ConfigFieldDescriptor<T>(
    ConfigKey<T> Key,
    Func<JsonNode?, ConfigFieldResult<T>> Parser,
    string LegacyKey = "",
    string Description = "");
```

示例：

```csharp
public static class ChaosModeConfigKeys
{
    public static readonly ConfigKey<string> GameLanguage =
        new("gameLanguage", "游戏语言", "简体中文");

    public static readonly ConfigKey<int> CreditMeditationThreshold =
        new("creditMeditationThreshold", "多少信用点以上冥想", 300);
}

public static class ChaosModeConfigSchema
{
    public static readonly ConfigFieldDescriptor<string> GameLanguage =
        new(
            ChaosModeConfigKeys.GameLanguage,
            ConfigFieldParsers.OneOf("简体中文", "繁体中文"),
            LegacyKey: "游戏语言");

    public static readonly ConfigFieldDescriptor<int> CreditMeditationThreshold =
        new(
            ChaosModeConfigKeys.CreditMeditationThreshold,
            ConfigFieldParsers.Integer(min: 0, max: 9999),
            LegacyKey: "多少信用点以上冥想");
}
```

约束：

- `Automation.Core` 只提供 `ConfigKey`、descriptor、parser、snapshot 和 provider。
- 游戏字段定义放在 `Kes.Tasks` 对应模块中。
- WPF 页面固定绑定 ViewModel，不读取 descriptor 反射生成控件树。
- `LegacyKey` 只用于导入，不参与日常读写。

## 5. 任务读取语义

`TaskContext` 增加：

```csharp
public IConfigView Config { get; }
```

任务在每次 `RunOnceAsync` 开始时获取一次快照：

```csharp
public ValueTask<bool> RunOnceAsync(
    TaskContext context,
    CancellationToken cancellationToken)
{
    var config = context.Config;
    var threshold = config.Get(ChaosModeConfigKeys.CreditMeditationThreshold);
    var language = config.Get(ChaosModeConfigKeys.GameLanguage);

    // 本轮页面决策只使用这一份快照。
    return HandleCurrentPageAsync(context, config, cancellationToken);
}
```

规则：

- 可以保存快照到本轮方法结束，禁止保存到 `static` 或长期任务字段。
- 不在循环中反复读取配置，避免同一页面决策前后使用不同版本。
- 配置变化不会取消当前 `RunOnceAsync`，下一轮开始时才使用新值。
- 页面点击、滑动已经开始后，不允许因热更新中途改变目标或输入后端。
- `SessionTaskCoordinator` 负责在模块调度边界获取最新快照并传入 `TaskContext` 的执行范围。

## 6. 热更新生效矩阵

| 配置项 | 生效时机 | 是否中断当前步骤 |
|---|---|---|
| 任务业务参数 | 下一轮 `RunOnceAsync` | 否 |
| 模块 `enabled` | 下一次模块选择 | 否 |
| 日志级别 | 下一次日志写入 | 否 |
| OCR/模板模型路径 | 下一次视觉请求，需要引擎支持动态重载 | 否 |
| 视觉并发度 | 下一次调度请求 | 否 |
| `uiProfile` | 会话停止并重新启动后 | 是，必须重连 |
| 抓帧后端 | 新会话启动或显式重连后 | 是，需要重建帧源 |
| 输入后端 | 新会话启动或显式重连后 | 是，需要重建输入通道 |
| `TargetBinding` | 重新绑定目标后 | 是，必须重建会话 |

热更新事件不得直接调用 `WindowsFrameSource.Stop()`、替换 `IInputSink` 或修改正在执行的 `TaskContext`。

## 7. 文件读写与监听协议

### 7.1 原子写入

1. 校验完整文档。
2. 生成规范化 UTF-8 JSON。
3. 写入同目录临时文件 `name.tmp.<guid>`。
4. 刷新文件后使用原子替换覆盖目标文件。
5. 成功后把上一份有效文件保存到 `backups/`。
6. 发布新 `ConfigSnapshot`。

禁止直接覆盖正在读取的目标文件。

### 7.2 外部修改

`FileConfigProvider` 使用 `FileSystemWatcher`：

- 事件先经过 250 ms 防抖。
- 读取失败时按 50、100、200 ms 重试。
- 识别并忽略自己产生的临时文件和已发布 revision。
- 多个事件合并后只发布最后一个有效内容。
- 只监视 `global.json`、`bindings.json` 和当前已加载的 session 文件。

### 7.3 异常处理

| 情况 | 行为 |
|---|---|
| 文件不存在 | 使用默认值，并生成默认文件 |
| JSON 语法错误 | 保留 last-known-good，状态标记 `Invalid` |
| 字段类型错误 | 保留 last-known-good，记录字段错误 |
| 未知字段 | 保留在 `unknownValues` |
| 新 schemaVersion 不支持 | 不加载，提示升级程序 |
| 写入失败 | 内存快照不变，GUI 显示错误 |
| revision 冲突 | GUI 提示重新加载或确认覆盖 |

## 8. 校验与业务规则

校验分三层：

1. **字段校验**：类型、范围、枚举值、列表元素格式。
2. **模块校验**：互斥开关和依赖关系，例如“刷初始卡牌”与“刷空档”不能同时开启。
3. **会话校验**：`UiProfile` 与绑定、任务集合和布局配置是否匹配。

校验结果：

```csharp
public sealed record ConfigValidationIssue(
    string Path,
    ConfigIssueSeverity Severity,
    string Message);

public enum ConfigIssueSeverity
{
    Warning,
    Error
}
```

- `Error` 阻止保存和发布。
- `Warning` 允许运行，但在 GUI 中显示。
- 业务规则不进入 `Automation.Core`。
- 迁移器只做数据转换，不运行游戏逻辑。

## 9. GUI 配置模型

每个固定配置页使用以下状态：

```text
BaselineSnapshot  <- 当前已生效的不可变快照
DraftDocument     <- 用户正在编辑的文档
Revision          <- 保存时使用的 expectedRevision
IsDirty           <- Draft 是否与 Baseline 不同
ValidationIssues  <- 字段和模块校验结果
```

交互规则：

- 无本地修改时，外部变化自动刷新 Baseline 和页面。
- 页面有未保存修改时，外部变化只更新磁盘 baseline，不覆盖用户输入。
- 保存发生 revision 冲突时，提供“重新加载”和“保留我的修改并覆盖”两个明确操作。
- 只有磁盘写入成功后才更新 Baseline 并发布 Activity/日志。
- 保存失败时保留 Draft，不清空用户输入。
- `uiProfile` 等需要重连的字段显示“重新连接后生效”，不做伪热切换。

设置页继续使用固定分段控件、下拉框、输入框和开关，不根据配置文件动态生成界面。

## 10. 版本迁移与旧配置导入

### 10.1 schemaVersion

- 每个根文件独立维护 `schemaVersion`。
- 迁移链使用 `Version -> MigrationStep` 注册，不把旧版本判断散落到业务读取处。
- 迁移成功后保留 `origin=Migration` 和原文件备份。
- 单个版本迁移必须有纯逻辑测试。

### 10.2 Python key 映射

旧 Python 中文 key 通过显式 descriptor 导入：

```text
游戏语言                  -> gameLanguage
多少信用点以上冥想        -> creditMeditationThreshold
第一层路线优先级          -> routePriorityLayer1
拉黑任务                  -> blacklistedTasks
```

导入流程：

1. 读取旧 JSON 或旧配置码。
2. 先跑 Python 已知历史迁移，例如闪光优先级结构和路线优先级分层。
3. 按 `LegacyKey` 映射到稳定英文 ID。
4. 用 C# 字段 parser 做类型和范围校验。
5. 丢弃已废弃字段并生成迁移报告。
6. 为目标 `ProfileId` 写出新版本会话文件。

导入失败不得修改当前生效配置。

### 10.3 任务方案包导入边界

任务方案包导入与旧配置迁移是两条流程：

- 旧配置导入面向 Python 字段映射，最终写入某个已有或新建会话档案。
- 任务方案包导入面向完整的 C# 任务方案，包含业务值和运行时头像模板。
- 内部 `task-profiles.json` 只是本地存储格式，不作为公开交换格式。
- 对外任务方案使用版本化 `.kestask` 包；格式、资源类型和安全规则见 [09-task-board-design.md](./09-task-board-design.md) 第 14 节。
- 方案包不包含 `TargetBinding`、`HWND`、抓帧/输入偏好或运行状态。导入时始终生成新的本地方案 `ProfileId`，不能复用来源机器的 ID。
- 包内 `formatVersion` 与方案 `configSchemaVersion` 独立迁移，任何失败都不能修改当前生效配置。

## 11. 计划中的代码边界

```text
Automation.Core/Configuration/
├── ConfigScope.cs
├── ConfigRevision.cs
├── ConfigSnapshot.cs
├── ConfigKey.cs
├── ConfigFieldDescriptor.cs
├── ConfigValidation.cs
├── IConfigProvider.cs
├── IConfigService.cs
├── IConfigView.cs
├── ISessionProfileStore.cs
├── FileConfigProvider.cs
├── FileSessionProfileStore.cs
├── ConfigService.cs
├── ConfigRootLocator.cs
└── ConfigChangeBus.cs

Kes.Tasks/Configuration/
├── ChaosModeConfigSchema.cs
└── LegacyPythonConfigImporter.cs

Kes.Gui/ViewModels/Configuration/
├── GlobalSettingsViewModel.cs
├── SessionConfigViewModel.cs
└── ChaosModeConfigViewModel.cs
```

`Automation.Core` 不引用 WPF、游戏模块或具体任务类型。`Kes.Gui` 不直接操作 JSON 文件，只依赖 `IConfigService` 和 `IConfigView`。

## 12. 实施顺序

### 阶段 A：核心配置运行时

1. 完成快照、revision、key、descriptor 和校验模型。
2. 实现 `ConfigRootLocator` 和 `FileConfigProvider`。
3. 实现原子写入、备份、Watcher 防抖和 last-known-good。
4. 为配置读写和错误回退建立纯逻辑测试。

### 阶段 B：会话接入

1. 为会话生成并持久化稳定 `ProfileId`。
2. 把当前抓帧/输入偏好迁移到 `global.json`。
3. 把 `ProfileId`、`TargetBinding` 和 `UiProfile` 接入 `bindings.json`。
4. 会话启动时加载配置，停止时只释放运行时资源，不删除配置。

### 阶段 C：任务接入与 GUI

1. 把 `IConfigView` 加入 `TaskContext`。
2. 在调度边界发布新快照。
3. 为 ChaosMode 建立固定配置 ViewModel。
4. 接入 Python 旧配置导入。

## 13. 验收标准

- 修改会话业务参数后，无需重启，下一轮任务使用新值。
- 外部写入无效 JSON 时，当前任务继续使用上一份有效配置。
- 保存过程中断电或强杀程序，不留下半份目标配置。
- GUI 与外部编辑同时写入时，不静默覆盖 revision 冲突。
- 重启程序后能恢复 `ProfileId`、配置和可重新绑定的目标信息。
- PC 会话的设置页不显示模拟器传输选项。
- 所有迁移、默认值、校验、原子写入和热更新测试均不依赖真实游戏窗口。