using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Automation.MemoryProbe;

public sealed class ProcessMemorySession : IDisposable
{
    private const int PebImageBaseAddressOffset = 0x10;

    private readonly SafeProcessHandle _handle;
    private bool _disposed;

    private ProcessMemorySession(
        SafeProcessHandle handle,
        ProcessMemoryProbeResult probeResult)
    {
        _handle = handle;
        ProbeResult = probeResult;
    }

    public ProcessMemoryProbeResult ProbeResult { get; }

    public static ProcessMemorySession? TryOpen(
        int processId,
        out ProcessMemoryProbeResult probeResult)
    {
        var processName = TryGetProcessName(processId);
        var handle = ProcessMemoryNativeMethods.OpenProcess(
            ProcessMemoryNativeMethods.ProcessQueryLimitedInformation
            | ProcessMemoryNativeMethods.ProcessVmRead,
            false,
            processId);

        if (handle.IsInvalid)
        {
            var win32Error = Marshal.GetLastWin32Error();
            handle.Dispose();
            probeResult = Failure(
                processId,
                processName,
                string.Empty,
                ClassifyOpenFailure(win32Error),
                win32Error,
                ntStatus: 0,
                pebAddress: 0,
                $"OpenProcess failed with Win32 error {win32Error} ({new Win32Exception(win32Error).Message}).");
            return null;
        }

        var executablePath = TryGetExecutablePath(handle);
        var queryLength = Marshal.SizeOf<ProcessMemoryNativeMethods.ProcessBasicInformation>();
        var ntStatus = ProcessMemoryNativeMethods.NtQueryInformationProcess(
            handle,
            0,
            out var basicInformation,
            queryLength,
            out _);
        if (ntStatus < 0)
        {
            handle.Dispose();
            probeResult = Failure(
                processId,
                processName,
                executablePath,
                ProcessMemoryProbeStatus.QueryFailed,
                win32Error: 0,
                ntStatus,
                pebAddress: 0,
                $"NtQueryInformationProcess failed with NTSTATUS 0x{ntStatus:X8}.");
            return null;
        }

        var pebAddress = basicInformation.PebBaseAddress.ToInt64();
        var buffer = new byte[sizeof(long)];
        if (!TryReadNative(handle, basicInformation.PebBaseAddress + PebImageBaseAddressOffset, buffer, out var pebError))
        {
            handle.Dispose();
            probeResult = Failure(
                processId,
                processName,
                executablePath,
                ClassifyReadFailure(pebError),
                pebError,
                ntStatus,
                pebAddress,
                $"ReadProcessMemory denied while reading PEB ImageBaseAddress: Win32 error {pebError}.");
            return null;
        }

        var imageBaseAddress = BitConverter.ToInt64(buffer, 0);
        var status = ProcessMemoryProbeStatus.Available;
        var message = "Process memory is readable.";
        if (imageBaseAddress <= 0)
        {
            status = ProcessMemoryProbeStatus.ReadFailed;
            message = "PEB ImageBaseAddress is empty.";
        }

        probeResult = new ProcessMemoryProbeResult(
            processId,
            processName,
            executablePath,
            status,
            pebAddress,
            imageBaseAddress,
            Win32Error: 0,
            ntStatus,
            message);

        if (status != ProcessMemoryProbeStatus.Available)
        {
            handle.Dispose();
            return null;
        }

        return new ProcessMemorySession(handle, probeResult);
    }

    public bool TryRead(nint address, byte[] buffer, out int win32Error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        return TryReadNative(_handle, address, buffer, out win32Error);
    }

    public IEnumerable<MemoryRegion> EnumerateReadableRegions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var address = 0x10000L;
        var informationLength = (nuint)Marshal.SizeOf<ProcessMemoryNativeMethods.MemoryBasicInformation>();

        while (address > 0)
        {
            var queryAddress = (nint)address;
            if (!ProcessMemoryNativeMethods.VirtualQueryEx(
                    _handle,
                    queryAddress,
                    out var information,
                    informationLength))
            {
                yield break;
            }

            var regionSize = checked((long)information.RegionSize);
            if (regionSize <= 0)
            {
                yield break;
            }

            if (information.State == ProcessMemoryNativeMethods.MemCommit
                && IsReadableProtection(information.Protect))
            {
                yield return new MemoryRegion(
                    information.BaseAddress.ToInt64(),
                    regionSize,
                    information.Protect);
            }

            var nextAddress = checked(address + regionSize);
            if (nextAddress <= address)
            {
                yield break;
            }

            address = nextAddress;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    internal static bool IsReadableProtection(uint protection)
    {
        var baseProtection = protection & ProcessMemoryNativeMethods.ProtectionMask;
        if (baseProtection == ProcessMemoryNativeMethods.PageNoAccess
            || (protection & ProcessMemoryNativeMethods.PageGuard) != 0)
        {
            return false;
        }

        const uint readableProtections =
            ProcessMemoryNativeMethods.PageReadOnly
            | ProcessMemoryNativeMethods.PageReadWrite
            | ProcessMemoryNativeMethods.PageWriteCopy
            | ProcessMemoryNativeMethods.PageExecuteRead
            | ProcessMemoryNativeMethods.PageExecuteReadWrite
            | ProcessMemoryNativeMethods.PageExecuteWriteCopy;
        return (baseProtection & readableProtections) != 0;
    }

    internal static ProcessMemoryProbeStatus ClassifyOpenFailure(int win32Error)
        => win32Error is 87 or 1168
            ? ProcessMemoryProbeStatus.ProcessNotFound
            : ProcessMemoryProbeStatus.AccessDenied;

    internal static ProcessMemoryProbeStatus ClassifyReadFailure(int win32Error)
        => win32Error is 5 or 299
            ? ProcessMemoryProbeStatus.ReadDenied
            : ProcessMemoryProbeStatus.ReadFailed;

    private static bool TryReadNative(
        SafeProcessHandle handle,
        nint address,
        byte[] buffer,
        out int win32Error)
    {
        var succeeded = ProcessMemoryNativeMethods.ReadProcessMemory(
            handle,
            address,
            buffer,
            (nuint)buffer.Length,
            out var bytesRead);
        win32Error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded && bytesRead == (nuint)buffer.Length;
    }

    private static string TryGetProcessName(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetExecutablePath(SafeProcessHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = (uint)buffer.Capacity;
        return ProcessMemoryNativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref length)
            ? buffer.ToString()
            : string.Empty;
    }

    private static ProcessMemoryProbeResult Failure(
        int processId,
        string processName,
        string executablePath,
        ProcessMemoryProbeStatus status,
        int win32Error,
        int ntStatus,
        long pebAddress,
        string message)
        => new(
            processId,
            processName,
            executablePath,
            status,
            pebAddress,
            ImageBaseAddress: 0,
            win32Error,
            ntStatus,
            message);
}

public sealed record MemoryRegion(long BaseAddress, long Size, uint Protection);