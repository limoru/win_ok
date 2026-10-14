using Automation.Core.Frames;

namespace Automation.Targets.Windows;

public sealed class BgraFrameLease : FrameLease, IBgraFrameLease
{
    public BgraFrameLease(
        long sequence,
        DateTimeOffset capturedAt,
        BgraFrame frame,
        WindowsCaptureMethod captureMethod)
        : base(sequence, capturedAt, frame.Width, frame.Height, "BGRA32")
    {
        Frame = frame;
        CaptureMethod = captureMethod;
    }

    public BgraFrame Frame { get; }
    public WindowsCaptureMethod CaptureMethod { get; }

    public override void Dispose()
    {
    }
}