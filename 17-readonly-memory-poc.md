# 只读内存页面识别 PoC

**记录日期：** 2026-10-21

## 1. 目标

验证能否通过读取 `ssr-xcent.exe` 进程内存，替代或减少当前全图 OCR。PoC 只负责回答三个问题：

1. 能否打开目标进程并取得必要权限。
2. 能否定位 PEB 和主模块。
3. 能否读取进程内存并继续扫描页面状态候选。

任何一步被拒绝都必须立即停止，不能把“读取失败”解释为“当前页面不匹配”。

## 2. 实现

独立项目：`Automation.MemoryProbe`

能力：

- 按 PID 或进程名定位目标。
- 使用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ)` 获取句柄。
- 使用 `NtQueryInformationProcess` 查询 PEB。
- 使用 `ReadProcessMemory` 读取 PEB 的 `ImageBaseAddress`。
- 内存可读时，通过 `VirtualQueryEx` 枚举已提交且可读区域。
- 可选按 UTF-8 / UTF-16LE 扫描文本，限制最大读取量和最大命中数。
- 所有失败均输出结构化状态和 Win32/NTSTATUS 错误码。

明确不包含：

- 不注入 DLL。
- 不 Hook 游戏函数。
- 不写入或修改游戏内存。
- 不规避 ACE-Safe、系统保护或其他访问控制。

## 3. 实机结果

目标：

~~~text
PID: 20452
Process: ssr-xcent
Path: D:\WeGameApps\rail_apps\czn(2002460)\bin\ssr-xcent.exe
~~~

2026-10-21 的只读验证结果：

~~~text
OpenProcess: 成功
NtQueryInformationProcess: 成功
PEB: 0x16E0791000
ReadProcessMemory(PEB + 0x10): 失败
Win32Error: 5 (AccessDenied)
~~~

PoC 输出：

~~~json
{
  "Status": "ReadDenied",
  "Win32Error": 5,
  "CanReadMemory": false,
  "Message": "ReadProcessMemory denied while reading PEB ImageBaseAddress: Win32 error 5."
}
~~~

## 4. 结论

当前 PC 客户端不允许普通只读内存访问。虽然进程句柄和 PEB 查询成功，但读取 PEB 内容已被拒绝，因此无法继续定位模块、对象、页面枚举或卡牌数据。

现阶段不能通过内存判断页面状态。OCR 仍是当前唯一可用的页面识别来源。

该结论不通过提升权限、注入、Hook 或绕过保护来改变。只有 PoC 在普通只读条件下返回 `Available`，才进入下一阶段：

1. 读取主模块并确认 PE 头。
2. 定位页面和 UI 就绪标志。
3. 定位事件选项、卡牌和装备状态。
4. 为每个游戏版本建立独立适配器。
5. 内存状态稳定后，再以“内存优先、OCR 兜底”的方式接入现有消费者。