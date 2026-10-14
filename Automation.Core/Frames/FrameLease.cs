namespace Automation.Core.Frames;

public abstract class FrameLease : IDisposable
{
    protected FrameLease(long sequence, DateTimeOffset capturedAt, int width, int height, string pixelFormat)
    {
        Sequence = sequence;
        CapturedAt = capturedAt;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
    }

    public long Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public int Width { get; }
    public int Height { get; }
    public string PixelFormat { get; }

    public abstract void Dispose();
}