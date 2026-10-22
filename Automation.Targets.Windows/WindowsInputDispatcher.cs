using System.ComponentModel;
using System.Runtime.InteropServices;
using Automation.Core.Targeting;

namespace Automation.Targets.Windows;

internal sealed class WindowsInputDispatcher : IInputSink
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint WmMouseWheel = 0x020A;
    private const uint MouseEventWheel = 0x0800;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const int SwRestore = 9;

    private readonly Func<nint> _resolveWindowHandle;
    private readonly WindowsInputBackend _backend;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WindowsInputDispatcher(
        Func<nint> resolveWindowHandle,
        WindowsInputBackend backend)
    {
        _resolveWindowHandle = resolveWindowHandle
            ?? throw new ArgumentNullException(nameof(resolveWindowHandle));
        _backend = backend;
    }

    public async ValueTask ClickAsync(
        RelativePoint point,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var windowHandle = ResolveWindowHandle();

            if (ShouldUsePostMessage())
            {
                try
                {
                    await PostMessageClickAsync(
                            windowHandle,
                            ToClientPoint(windowHandle, point),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (CanFallbackToForeground(exception))
                {
                }
            }

            await ForegroundClickAsync(windowHandle, point, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask SwipeAsync(
        RelativePoint from,
        RelativePoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var windowHandle = ResolveWindowHandle();
            if (ShouldUsePostMessage())
            {
                try
                {
                    await PostMessageSwipeAsync(
                            windowHandle,
                            ToClientPoint(windowHandle, from),
                            ToClientPoint(windowHandle, to),
                            duration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (CanFallbackToForeground(exception))
                {
                }
            }

            await ForegroundSwipeAsync(
                    windowHandle,
                    from,
                    to,
                    duration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask ScrollAsync(
        RelativePoint point,
        int wheelNotches,
        CancellationToken cancellationToken = default)
    {
        if (wheelNotches == 0)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var windowHandle = ResolveWindowHandle();
            var delta = wheelNotches * 120;
            var screenPoint = ToScreenPoint(windowHandle, point);
            if (ShouldUsePostMessage())
            {
                try
                {
                    var wParam = (long)((uint)(ushort)delta << 16);
                    PostMessageOrThrow(windowHandle, WmMouseWheel, wParam, MakeLParam(screenPoint));
                    return;
                }
                catch (Exception exception) when (CanFallbackToForeground(exception))
                {
                }
            }

            ForegroundScrollAsync(windowHandle, screenPoint, delta);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask KeyAsync(
        InputKey key,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var windowHandle = ResolveWindowHandle();
            var virtualKey = ToVirtualKey(key);

            if (ShouldUsePostMessage())
            {
                try
                {
                    PostMessageKey(windowHandle, virtualKey);
                    return;
                }
                catch (Exception exception) when (CanFallbackToForeground(exception))
                {
                }
            }

            ForegroundSendKey(windowHandle, virtualKey);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask TextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var windowHandle = ResolveWindowHandle();

            if (ShouldUsePostMessage())
            {
                try
                {
                    PostMessageText(windowHandle, text);
                    return;
                }
                catch (Exception exception) when (CanFallbackToForeground(exception))
                {
                }
            }

            ForegroundSendText(windowHandle, text);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private bool CanFallbackToForeground(Exception exception)
        => _backend == WindowsInputBackend.Auto
           && exception is not OperationCanceledException;
    private bool ShouldUsePostMessage()
        => _backend is WindowsInputBackend.Auto or WindowsInputBackend.PostMessage;

    private nint ResolveWindowHandle()
    {
        var windowHandle = _resolveWindowHandle();
        if (windowHandle == 0 || !IsWindow(windowHandle))
        {
            throw new InvalidOperationException("The target window is no longer available.");
        }

        return windowHandle;
    }

    private static NativePoint ToClientPoint(nint windowHandle, RelativePoint point)
    {
        if (!GetClientRect(windowHandle, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("GetClientRect failed.");
        }

        var normalized = point.Clamp();
        return new NativePoint
        {
            X = Math.Clamp((int)Math.Round(normalized.X * (rect.Width - 1)), 0, rect.Width - 1),
            Y = Math.Clamp((int)Math.Round(normalized.Y * (rect.Height - 1)), 0, rect.Height - 1)
        };
    }

    private static NativePoint ToScreenPoint(nint windowHandle, RelativePoint point)
    {
        var clientPoint = ToClientPoint(windowHandle, point);
        if (!ClientToScreen(windowHandle, ref clientPoint))
        {
            throw new InvalidOperationException("ClientToScreen failed.");
        }

        return clientPoint;
    }

    private static Task PostMessageClickAsync(
        nint windowHandle,
        NativePoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lParam = MakeLParam(point);
        PostMessageOrThrow(windowHandle, WmMouseMove, 0, lParam);
        PostMessageOrThrow(windowHandle, WmLButtonDown, 1, lParam);
        PostMessageOrThrow(windowHandle, WmLButtonUp, 0, lParam);
        PostMessageOrThrow(windowHandle, WmMouseMove, 0, lParam);

        return Task.CompletedTask;
    }

    private static async Task PostMessageSwipeAsync(
        nint windowHandle,
        NativePoint from,
        NativePoint to,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        const int steps = 16;
        var buttonDown = false;
        try
        {
            PostMessageOrThrow(windowHandle, WmMouseMove, 0, MakeLParam(from));
            PostMessageOrThrow(windowHandle, WmLButtonDown, 1, MakeLParam(from));
            buttonDown = true;

            var stepDelay = duration <= TimeSpan.Zero
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(duration.Ticks / steps);
            for (var index = 1; index <= steps; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = index / (double)steps;
                var point = new NativePoint
                {
                    X = (int)Math.Round(from.X + ((to.X - from.X) * progress)),
                    Y = (int)Math.Round(from.Y + ((to.Y - from.Y) * progress))
                };
                PostMessageOrThrow(windowHandle, WmMouseMove, 1, MakeLParam(point));
                if (stepDelay > TimeSpan.Zero)
                {
                    await Task.Delay(stepDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            PostMessageOrThrow(windowHandle, WmLButtonUp, 0, MakeLParam(to));
            buttonDown = false;
        }
        finally
        {
            if (buttonDown)
            {
                _ = PostMessage(windowHandle, WmLButtonUp, 0, MakeLParam(to));
            }
        }
    }

    private static void PostMessageKey(nint windowHandle, uint virtualKey)
    {
        PostMessageOrThrow(windowHandle, WmKeyDown, virtualKey, 1);
        PostMessageOrThrow(windowHandle, WmKeyUp, virtualKey, unchecked((long)0xC0000001));
    }

    private static void PostMessageText(nint windowHandle, string text)
    {
        foreach (var character in text)
        {
            PostMessageOrThrow(windowHandle, WmChar, character, 1);
        }
    }

    private static ValueTask ForegroundClickAsync(
        nint windowHandle,
        RelativePoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTargetForeground(windowHandle);
        var screenPoint = ToScreenPoint(windowHandle, point);
        if (!SetCursorPos(screenPoint.X, screenPoint.Y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
        }

        SendMouseInput(MouseEventLeftDown);
        SendMouseInput(MouseEventLeftUp);

        return ValueTask.CompletedTask;
    }

    private static async Task ForegroundSwipeAsync(
        nint windowHandle,
        RelativePoint from,
        RelativePoint to,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        EnsureTargetForeground(windowHandle);
        var start = ToScreenPoint(windowHandle, from.Clamp());
        var end = ToScreenPoint(windowHandle, to.Clamp());
        if (!SetCursorPos(start.X, start.Y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
        }

        var buttonDown = false;
        var buttonReleased = false;
        try
        {
            SendMouseInput(MouseEventLeftDown);
            buttonDown = true;
            const int steps = 20;
            var stepDelay = duration <= TimeSpan.Zero
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(duration.Ticks / steps);

            for (var index = 1; index <= steps; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = index / (double)steps;
                var x = (int)Math.Round(start.X + ((end.X - start.X) * progress));
                var y = (int)Math.Round(start.Y + ((end.Y - start.Y) * progress));
                if (!SetCursorPos(x, y))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
                }

                if (stepDelay > TimeSpan.Zero)
                {
                    await Task.Delay(stepDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            SendMouseInput(MouseEventLeftUp);
            buttonReleased = true;
        }
        finally
        {
            if (buttonDown && !buttonReleased)
            {
                try
                {
                    SendMouseInput(MouseEventLeftUp);
                }
                catch
                {
                    // Preserve the original operation failure while making a best effort to release the button.
                }
            }
        }
    }

    private static void ForegroundScrollAsync(
        nint windowHandle,
        NativePoint screenPoint,
        int delta)
    {
        EnsureTargetForeground(windowHandle);
        if (!SetCursorPos(screenPoint.X, screenPoint.Y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
        }

        SendMouseWheel(delta);
    }

    private static void ForegroundSendKey(nint windowHandle, uint virtualKey)
    {
        EnsureTargetForeground(windowHandle);
        SendKeyboardInput(virtualKey, 0);
        SendKeyboardInput(virtualKey, KeyEventKeyUp);
    }

    private static void ForegroundSendText(nint windowHandle, string text)
    {
        EnsureTargetForeground(windowHandle);
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(CreateUnicodeKeyboardInput(character, 0));
            inputs.Add(CreateUnicodeKeyboardInput(character, KeyEventKeyUp));
        }

        SendInputs(inputs);
    }

    private static void EnsureTargetForeground(nint windowHandle)
    {
        if (IsIconic(windowHandle))
        {
            _ = ShowWindow(windowHandle, SwRestore);
        }

        if (GetForegroundWindow() == windowHandle)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == 0
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThread = GetWindowThreadProcessId(windowHandle, out _);

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            }

            _ = BringWindowToTop(windowHandle);
            if (!SetForegroundWindow(windowHandle))
            {
                throw new InvalidOperationException("Unable to bring the target window to the foreground.");
            }
        }
        finally
        {
            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static void SendMouseInput(uint flags)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = 0,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        Flags = flags
                    }
                }
            }
        };
        SendInputs(inputs);
    }

    private static void SendMouseWheel(int delta)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = 0,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = unchecked((uint)delta),
                        Flags = MouseEventWheel
                    }
                }
            }
        };
        SendInputs(inputs);
    }

    private static void SendKeyboardInput(uint virtualKey, uint flags)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = 1,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = (ushort)virtualKey,
                        Flags = flags
                    }
                }
            }
        };
        SendInputs(inputs);
    }

    private static Input CreateUnicodeKeyboardInput(char character, uint flags)
        => new()
        {
            Type = 1,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = character,
                    Flags = KeyEventUnicode | flags
                }
            }
        };

    private static void SendInputs(IReadOnlyList<Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var buffer = inputs.ToArray();
        var sent = SendInput(
            (uint)buffer.Length,
            buffer,
            Marshal.SizeOf<Input>());
        if (sent != buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
        }
    }

    private static void PostMessageOrThrow(nint windowHandle, uint message, long wParam, long lParam)
    {
        if (PostMessage(windowHandle, message, (nuint)wParam, (nint)lParam))
        {
            return;
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), $"PostMessage 0x{message:X4} failed.");
    }

    private static nint MakeLParam(NativePoint point)
    {
        var packed = (point.Y << 16) | (point.X & 0xFFFF);
        return (nint)packed;
    }

    private static uint ToVirtualKey(InputKey key)
        => key switch
        {
            InputKey.F1 => 0x70,
            InputKey.Escape => 0x1B,
            InputKey.Enter => 0x0D,
            InputKey.Space => 0x20,
            InputKey.Tab => 0x09,
            InputKey.Backspace => 0x08,
            InputKey.Up => 0x26,
            InputKey.Down => 0x28,
            InputKey.Left => 0x25,
            InputKey.Right => 0x27,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
        };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
