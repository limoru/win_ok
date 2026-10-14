using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Automation.Core.Targeting;
using Automation.Targets.Windows.Interop;

namespace Automation.Targets.Windows;

internal static class WindowsTargetDiscovery
{
    private static readonly HashSet<string> PcGameExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssr-xcent.exe",
        "ssr-stove-shield.exe"
    };

    private static readonly HashSet<string> MuMuWindowExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "mumuplayer.exe",
        "mumunxdevice.exe",
        "nemuplayer.exe"
    };

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "ForegroundStaging",
        "TextInputHost"
    };

    internal static bool TryResolveWindowHandle(TargetBinding binding, out nint windowHandle)
    {
        nint resolved = 0;
        NativeMethods.EnumWindows((candidateHandle, _) =>
        {
            var candidate = TryCreateCandidate(candidateHandle);
            if (candidate?.Binding.ProviderInstanceId != binding.ProviderInstanceId)
            {
                return true;
            }

            resolved = candidateHandle;
            return false;
        }, 0);

        windowHandle = resolved;
        return resolved != 0;
    }

    internal static IReadOnlyList<TargetCandidate> Discover()
    {
        var candidates = new List<TargetCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            var candidate = TryCreateCandidate(windowHandle);
            if (candidate is not null && seen.Add(candidate.Binding.ProviderInstanceId))
            {
                candidates.Add(candidate);
            }

            return true;
        }, 0);

        return candidates
            .OrderBy(candidate => candidate.Binding.Kind)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static TargetCandidate? TryCreateCandidate(nint windowHandle)
    {
        if (!NativeMethods.IsWindowVisible(windowHandle))
        {
            return null;
        }

        var title = ReadWindowText(windowHandle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var className = ReadClassName(windowHandle);
        if (string.IsNullOrWhiteSpace(className) || ExcludedClasses.Contains(className))
        {
            return null;
        }

        var extendedStyle = NativeMethods.GetWindowLong(windowHandle, NativeMethods.GwlExStyle);
        if ((extendedStyle & NativeMethods.WsExToolWindow) != 0)
        {
            return null;
        }

        if (!NativeMethods.GetClientRect(windowHandle, out var clientRect))
        {
            return null;
        }

        var width = clientRect.Width;
        var height = clientRect.Height;
        if (width < 320 || height < 240)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return null;
        }

        if (!TryReadProcessName(processId, out var processName)
            || !TryClassifyProcess(processName, out var targetKind))
        {
            return null;
        }

        var processPath = ReadProcessPath(processId, processName);
        // Window title and client size are mutable. Keep the binding identity stable
        // while the process lives so moving or resizing the target does not detach it.
        var signature = $"{processPath}|{className}|{processId}";
        var instanceId = Hash(signature);
        var installationRoot = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(installationRoot))
        {
            installationRoot = processName;
        }

        var installationId = Hash(installationRoot);
        var profileLabel = targetKind == TargetKind.MuMuEmulator ? "模拟器" : "PC 版";

        var binding = new TargetBinding
        {
            Kind = targetKind,
            ProviderInstallationId = installationId,
            ProviderInstanceId = instanceId,
            Fingerprint = signature,
            DisplayName = $"{profileLabel} - {processName}"
        };

        return new TargetCandidate(
            binding,
            $"{profileLabel} · {processName} · {title}",
            processPath,
            className,
            title,
            width,
            height,
            true);
    }

    private static bool TryClassifyProcess(string processName, out TargetKind targetKind)
    {
        targetKind = default;
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var executableName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{processName}.exe";

        if (PcGameExecutables.Contains(executableName))
        {
            targetKind = TargetKind.WindowsWindow;
            return true;
        }

        if (MuMuWindowExecutables.Contains(executableName))
        {
            targetKind = TargetKind.MuMuEmulator;
            return true;
        }

        return false;
    }

    private static string ReadWindowText(nint windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadClassName(nint windowHandle)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassName(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static bool TryReadProcessName(uint processId, out string processName)
    {
        processName = string.Empty;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName.Trim();
            return !string.IsNullOrWhiteSpace(processName);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadProcessPath(uint processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);

            try
            {
                var fullPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    return fullPath;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Protected game processes can deny MainModule access while ProcessName remains available.
            }
            catch (InvalidOperationException)
            {
            }
        }
        catch
        {
        }

        return $"{processName}.exe";
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}