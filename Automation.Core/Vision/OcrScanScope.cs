namespace Automation.Core.Vision;

public sealed record OcrScanScope(
    string Id,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsFullFrame)
{
    public static OcrScanScope FullFrame(int width, int height)
        => new("full-frame", 0, 0, width, height, IsFullFrame: true);

    public static OcrScanScope FromRelative(
        string id,
        int frameWidth,
        int frameHeight,
        double left,
        double top,
        double right,
        double bottom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (frameWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }

        if (frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameHeight));
        }

        left = Math.Clamp(left, 0d, 1d);
        top = Math.Clamp(top, 0d, 1d);
        right = Math.Clamp(right, 0d, 1d);
        bottom = Math.Clamp(bottom, 0d, 1d);
        if (right <= left || bottom <= top)
        {
            throw new ArgumentException("The OCR scope must have a positive area.");
        }

        var x = Math.Clamp((int)Math.Floor(left * frameWidth), 0, frameWidth - 1);
        var y = Math.Clamp((int)Math.Floor(top * frameHeight), 0, frameHeight - 1);
        var rightPixel = Math.Clamp((int)Math.Ceiling(right * frameWidth), x + 1, frameWidth);
        var bottomPixel = Math.Clamp((int)Math.Ceiling(bottom * frameHeight), y + 1, frameHeight);
        return new OcrScanScope(
            id,
            x,
            y,
            rightPixel - x,
            bottomPixel - y,
            IsFullFrame: false);
    }
}