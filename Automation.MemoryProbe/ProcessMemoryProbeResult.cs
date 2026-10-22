namespace Automation.MemoryProbe;

public enum ProcessMemoryProbeStatus
{
    Available,
    ProcessNotFound,
    AccessDenied,
    QueryFailed,
    ReadDenied,
    ReadFailed
}

public sealed record ProcessMemoryProbeResult(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    ProcessMemoryProbeStatus Status,
    long PebAddress,
    long ImageBaseAddress,
    int Win32Error,
    int NtStatus,
    string Message)
{
    public bool CanReadMemory => Status == ProcessMemoryProbeStatus.Available;
}