using System.Runtime.InteropServices;

namespace Automation.Targets.Windows;

public enum WindowsCaptureMethod
{
    WindowsGraphicsCapture,
    PrintWindow,
    BitBlt
}

public sealed record CapturedWindowFrame(
    int Width,
    int Height,
    byte[] BgraPixels,
    WindowsCaptureMethod Method);

public static class WindowsWindowCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    public static CapturedWindowFrame CaptureClient(nint windowHandle)
        => CaptureClient(windowHandle, WindowsCaptureBackend.Auto);

    public static CapturedWindowFrame CaptureClient(
        nint windowHandle,
        WindowsCaptureBackend backend)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        if (!IsWindow(windowHandle))
        {
            throw new InvalidOperationException("The target window handle is no longer valid.");
        }

        if (backend == WindowsCaptureBackend.Compatibility)
        {
            return CaptureWithBitBlt(windowHandle);
        }

        if (backend == WindowsCaptureBackend.Auto)
        {
            try
            {
                var frame = CaptureWithPrintWindow(windowHandle);
                if (!IsLikelyBlack(frame))
                {
                    return frame;
                }
            }
            catch
            {
            }

            return CaptureWithBitBlt(windowHandle);
        }

        if (backend == WindowsCaptureBackend.WindowsGraphicsCapture)
        {
            throw new NotSupportedException("Windows Graphics Capture requires the stateful Windows frame pipeline.");
        }

        throw new NotSupportedException($"Unsupported Windows capture backend: {backend}.");
    }

    internal static bool IsLikelyBlack(CapturedWindowFrame frame)
    {
        const int sampleLimit = 4096;
        const byte nearBlackThreshold = 3;
        var pixelCount = checked(frame.Width * frame.Height);
        if (pixelCount == 0)
        {
            return true;
        }

        var step = Math.Max(1, pixelCount / sampleLimit);
        var sampled = 0;
        var nearBlack = 0;
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel * 4;
            sampled++;
            if (frame.BgraPixels[offset] <= nearBlackThreshold
                && frame.BgraPixels[offset + 1] <= nearBlackThreshold
                && frame.BgraPixels[offset + 2] <= nearBlackThreshold)
            {
                nearBlack++;
            }
        }

        return sampled > 0 && nearBlack >= sampled * 0.995;
    }

    private static CapturedWindowFrame CaptureWithPrintWindow(nint windowHandle)
    {
        if (!GetWindowRect(windowHandle, out var windowRect))
        {
            throw new InvalidOperationException("GetWindowRect failed.");
        }

        if (!GetClientRect(windowHandle, out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            throw new InvalidOperationException("GetClientRect failed.");
        }

        var clientOrigin = new NativePoint();
        if (!ClientToScreen(windowHandle, ref clientOrigin))
        {
            throw new InvalidOperationException("ClientToScreen failed.");
        }

        var windowWidth = windowRect.Width;
        var windowHeight = windowRect.Height;
        var screenDc = GetDC(0);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, windowWidth, windowHeight);
        if (screenDc == 0 || memoryDc == 0 || bitmap == 0)
        {
            ReleaseGdi(screenDc, memoryDc, bitmap, 0);
            throw new InvalidOperationException("Unable to create GDI capture resources.");
        }

        var previousObject = SelectObject(memoryDc, bitmap);
        try
        {
            if (!PrintWindow(windowHandle, memoryDc, PwRenderFullContent))
            {
                throw new InvalidOperationException("PrintWindow failed.");
            }

            var fullPixels = ReadPixels(memoryDc, bitmap, windowWidth, windowHeight);
            var offsetX = Math.Clamp(clientOrigin.X - windowRect.Left, 0, windowWidth - clientRect.Width);
            var offsetY = Math.Clamp(clientOrigin.Y - windowRect.Top, 0, windowHeight - clientRect.Height);
            var clientPixels = Crop(fullPixels, windowWidth, windowHeight, offsetX, offsetY, clientRect.Width, clientRect.Height);
            return new CapturedWindowFrame(clientRect.Width, clientRect.Height, clientPixels, WindowsCaptureMethod.PrintWindow);
        }
        finally
        {
            if (previousObject != 0)
            {
                SelectObject(memoryDc, previousObject);
            }

            ReleaseGdi(screenDc, memoryDc, bitmap, 0);
        }
    }

    private static CapturedWindowFrame CaptureWithBitBlt(nint windowHandle)
    {
        if (!GetClientRect(windowHandle, out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            throw new InvalidOperationException("GetClientRect failed.");
        }

        var sourceDc = GetDC(windowHandle);
        var memoryDc = CreateCompatibleDC(sourceDc);
        var bitmap = CreateCompatibleBitmap(sourceDc, clientRect.Width, clientRect.Height);
        if (sourceDc == 0 || memoryDc == 0 || bitmap == 0)
        {
            ReleaseGdi(sourceDc, memoryDc, bitmap, windowHandle);
            throw new InvalidOperationException("Unable to create GDI capture resources.");
        }

        var previousObject = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, clientRect.Width, clientRect.Height, sourceDc, 0, 0, SrcCopy))
            {
                throw new InvalidOperationException("BitBlt failed.");
            }

            var pixels = ReadPixels(memoryDc, bitmap, clientRect.Width, clientRect.Height);
            return new CapturedWindowFrame(clientRect.Width, clientRect.Height, pixels, WindowsCaptureMethod.BitBlt);
        }
        finally
        {
            if (previousObject != 0)
            {
                SelectObject(memoryDc, previousObject);
            }

            ReleaseGdi(sourceDc, memoryDc, bitmap, windowHandle);
        }
    }

    private static byte[] Crop(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int offsetX,
        int offsetY,
        int width,
        int height)
    {
        var stride = sourceWidth * 4;
        var targetStride = width * 4;
        var target = new byte[targetStride * height];

        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source,
                ((offsetY + row) * stride) + (offsetX * 4),
                target,
                row * targetStride,
                targetStride);
        }

        return target;
    }

    private static byte[] ReadPixels(nint memoryDc, nint bitmap, int width, int height)
    {
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
            SizeImage = (uint)(width * height * 4)
        };
        var info = new BitmapInfo
        {
            Header = header,
            Colors = new uint[3]
        };
        var pixels = new byte[width * height * 4];
        var scanLines = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors);
        if (scanLines != height)
        {
            throw new InvalidOperationException("GetDIBits failed.");
        }

        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = byte.MaxValue;
        }

        return pixels;
    }

    private static void ReleaseGdi(nint sourceDc, nint memoryDc, nint bitmap, nint windowHandle)
    {
        if (bitmap != 0)
        {
            DeleteObject(bitmap);
        }

        if (memoryDc != 0)
        {
            DeleteDC(memoryDc);
        }

        if (sourceDc != 0)
        {
            if (windowHandle != 0)
            {
                _ = ReleaseDC(windowHandle, sourceDc);
            }
            else
            {
                _ = ReleaseDC(0, sourceDc);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint windowHandle, nint deviceContext, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startScan,
        uint scanLines,
        byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint usage);

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
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] Colors;
    }
}