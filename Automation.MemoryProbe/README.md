# Automation.MemoryProbe

只读进程内存 PoC，用于验证目标进程能否读取，以及在可读前提下按文本扫描候选地址。

## 安全边界

- 仅使用 `OpenProcess`、`NtQueryInformationProcess`、`ReadProcessMemory` 和 `VirtualQueryEx` 读取数据。
- 不注入 DLL，不 Hook，不修改游戏内存，不规避进程保护。
- 读取被系统或保护组件拒绝时立即结束，并输出 Win32 错误码。
- 文本扫描默认最多读取 256 MB、最多返回 20 个命中，避免无界扫描。

## 使用

仅检查进程是否可读：

本机存在 apphost/CET 兼容问题，推荐直接运行编译后的 DLL：

~~~powershell
dotnet Automation.MemoryProbe/bin/Debug/net9.0/Automation.MemoryProbe.dll 20452
dotnet Automation.MemoryProbe/bin/Debug/net9.0/Automation.MemoryProbe.dll ssr-xcent
~~~

在内存可读时扫描文本：

~~~powershell
dotnet Automation.MemoryProbe/bin/Debug/net9.0/Automation.MemoryProbe.dll ssr-xcent --scan-text "献上赞美" --max-mb 128
~~~

输出为 JSON。退出码：

- `0`：探针完成。
- `2`：进程存在，但内存被拒绝读取或扫描失败。
- `3`：没有找到目标进程。
- `64`：参数错误。

## 当前实机结果

2026-10-21 对 `ssr-xcent.exe` 做最小只读验证时：

- `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ)` 成功。
- `NtQueryInformationProcess` 成功取得 PEB 地址。
- 通过 `ReadProcessMemory` 读取 PEB 的 `ImageBaseAddress` 时返回 Win32 `5 (AccessDenied)`。
- 因此当前版本无法继续定位页面状态，也不应通过绕过保护继续读取。

这个 PoC 的意义是快速判断“内存方案是否可用”。只有状态为 `Available` 时，才值得继续做地址扫描、指针链和版本化页面状态适配器。