using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace Automation.Targets.Windows;

internal sealed class WindowsGraphicsCaptureSource : IDisposable
{
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _gate = new();
    private readonly TimeSpan _frameWaitTimeout;
    private bool _disposed;

    private nint _windowHandle;
    private int _windowWidth;
    private int _windowHeight;
    private GraphicsCaptureItem? _item;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _direct3DDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private Texture2DDescription _stagingDescription;

    public WindowsGraphicsCaptureSource(TimeSpan frameWaitTimeout)
    {
        if (frameWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWaitTimeout));
        }

        _frameWaitTimeout = frameWaitTimeout;
    }

    public CapturedWindowFrame Capture(nint windowHandle, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (windowHandle == 0 || !IsWindow(windowHandle))
        {
            throw new InvalidOperationException("The target window is no longer available.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                EnsureSession(windowHandle);
                cancellationToken.ThrowIfCancellationRequested();
                using var frame = WaitForFrame(cancellationToken);
                return ReadFrame(windowHandle, frame);
            }
            catch
            {
                ResetCapture();
                throw;
            }
        }
    }

    private void EnsureSession(nint windowHandle)
    {
        var bounds = GetWindowBounds(windowHandle);
        if (_session is not null
            && _windowHandle == windowHandle
            && _windowWidth == bounds.WindowWidth
            && _windowHeight == bounds.WindowHeight)
        {
            return;
        }

        ResetCapture();
        EnsureDevice();

        _item = CreateCaptureItem(windowHandle);
        _windowHandle = windowHandle;
        _windowWidth = bounds.WindowWidth;
        _windowHeight = bounds.WindowHeight;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _direct3DDevice!,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
    }

    private void EnsureDevice()
    {
        if (_device is not null && _context is not null && _direct3DDevice is not null)
        {
            return;
        }

        var result = D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            Array.Empty<FeatureLevel>(),
            out var device,
            out _,
            out var context);
        result.CheckError();

        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(
                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
            try
            {
                _direct3DDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }

            _device = device;
            _context = context;
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    private Direct3D11CaptureFrame WaitForFrame(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + checked((long)Math.Ceiling(_frameWaitTimeout.TotalMilliseconds));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = _framePool!.TryGetNextFrame();
            if (frame is not null)
            {
                return frame;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                throw new TimeoutException(
                    $"Windows Graphics Capture did not produce a frame within {_frameWaitTimeout.TotalMilliseconds:0} ms.");
            }

            Thread.Sleep((int)Math.Min(5, remaining));
        }
    }

    private CapturedWindowFrame ReadFrame(nint windowHandle, Direct3D11CaptureFrame frame)
    {
        using var texture = GetTexture(frame.Surface);
        var description = texture.Description;
        EnsureStagingTexture(description);

        _context!.CopyResource(_stagingTexture!, texture);
        _context.Map(
            _stagingTexture!,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None,
            out var mapped).CheckError();

        try
        {
            var width = checked((int)description.Width);
            var height = checked((int)description.Height);
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            if (mapped.RowPitch == stride)
            {
                Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        mapped.DataPointer + (row * checked((int)mapped.RowPitch)),
                        pixels,
                        row * stride,
                        stride);
                }
            }

            return CropToClient(
                windowHandle,
                new CapturedWindowFrame(
                    width,
                    height,
                    pixels,
                    WindowsCaptureMethod.WindowsGraphicsCapture));
        }
        finally
        {
            _context.Unmap(_stagingTexture!, 0);
        }
    }

    private void EnsureStagingTexture(Texture2DDescription sourceDescription)
    {
        if (_stagingTexture is not null
            && _stagingDescription.Width == sourceDescription.Width
            && _stagingDescription.Height == sourceDescription.Height
            && _stagingDescription.Format == sourceDescription.Format)
        {
            return;
        }

        _stagingTexture?.Dispose();
        _stagingDescription = sourceDescription;
        _stagingDescription.Usage = ResourceUsage.Staging;
        _stagingDescription.BindFlags = BindFlags.None;
        _stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingDescription.MiscFlags = ResourceOptionFlags.None;
        _stagingTexture = _device!.CreateTexture2D(_stagingDescription);
    }

    private static CapturedWindowFrame CropToClient(
        nint windowHandle,
        CapturedWindowFrame frame)
    {
        var bounds = GetWindowBounds(windowHandle);
        if (bounds.ClientWidth == frame.Width && bounds.ClientHeight == frame.Height)
        {
            return frame;
        }

        var offsetX = Math.Clamp(bounds.ClientLeft - bounds.WindowLeft, 0, Math.Max(0, frame.Width - 1));
        var offsetY = Math.Clamp(bounds.ClientTop - bounds.WindowTop, 0, Math.Max(0, frame.Height - 1));
        var width = Math.Min(bounds.ClientWidth, frame.Width - offsetX);
        var height = Math.Min(bounds.ClientHeight, frame.Height - offsetY);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Windows Graphics Capture returned a frame smaller than the client area.");
        }

        var targetStride = checked(width * 4);
        var cropped = new byte[checked(targetStride * height)];
        if (offsetX == 0 && targetStride == frame.Width * 4)
        {
            Buffer.BlockCopy(
                frame.BgraPixels,
                offsetY * frame.Width * 4,
                cropped,
                0,
                cropped.Length);
        }
        else
        {
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(
                    frame.BgraPixels,
                    ((offsetY + row) * frame.Width * 4) + (offsetX * 4),
                    cropped,
                    row * targetStride,
                    targetStride);
            }
        }

        return new CapturedWindowFrame(
            width,
            height,
            cropped,
            WindowsCaptureMethod.WindowsGraphicsCapture);
    }

    private static WindowBounds GetWindowBounds(nint windowHandle)
    {
        if (!GetWindowRect(windowHandle, out var windowRect))
        {
            throw new InvalidOperationException("GetWindowRect failed.");
        }

        if (!GetClientRect(windowHandle, out var clientRect)
            || clientRect.Width <= 0
            || clientRect.Height <= 0)
        {
            throw new InvalidOperationException("GetClientRect failed.");
        }

        var clientOrigin = new NativePoint();
        if (!ClientToScreen(windowHandle, ref clientOrigin))
        {
            throw new InvalidOperationException("ClientToScreen failed.");
        }

        return new WindowBounds(
            windowRect.Left,
            windowRect.Top,
            windowRect.Width,
            windowRect.Height,
            clientOrigin.X,
            clientOrigin.Y,
            clientRect.Width,
            clientRect.Height);
    }

    private static GraphicsCaptureItem CreateCaptureItem(nint windowHandle)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hr = WindowsCreateString(className, className.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var interopIid = InteropIid;
            hr = RoGetActivationFactory(hstring, ref interopIid, out var factoryPointer);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var vtable = Marshal.ReadIntPtr(factoryPointer);
                var createForWindowPointer = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(
                    createForWindowPointer);
                var itemIid = CaptureItemIid;
                hr = createForWindow(factoryPointer, windowHandle, in itemIid, out var itemPointer);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
                }
                finally
                {
                    Marshal.Release(itemPointer);
                }
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePointer = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var accessIid = DxgiInterfaceAccessIid;
        Marshal.ThrowExceptionForHR(
            Marshal.QueryInterface(surfacePointer, in accessIid, out var accessPointer));
        try
        {
            var vtable = Marshal.ReadIntPtr(accessPointer);
            var getInterfacePointer = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(
                getInterfacePointer);
            var textureIid = Texture2DIid;
            Marshal.ThrowExceptionForHR(
                getInterface(accessPointer, in textureIid, out var texturePointer));
            return new ID3D11Texture2D(texturePointer);
        }
        finally
        {
            Marshal.Release(accessPointer);
        }
    }

    private void ResetCapture()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        _item = null;
        _windowHandle = 0;
        _windowWidth = 0;
        _windowHeight = 0;
    }

    private void ResetDevice()
    {
        _direct3DDevice?.Dispose();
        _direct3DDevice = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResetCapture();
            ResetDevice();
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid iid,
        out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateForWindowDelegate(
        nint @this,
        nint window,
        in Guid iid,
        out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetInterfaceDelegate(
        nint @this,
        in Guid iid,
        out nint result);

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

    private readonly record struct WindowBounds(
        int WindowLeft,
        int WindowTop,
        int WindowWidth,
        int WindowHeight,
        int ClientLeft,
        int ClientTop,
        int ClientWidth,
        int ClientHeight);
}