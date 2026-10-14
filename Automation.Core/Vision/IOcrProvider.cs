using Automation.Core.Frames;

namespace Automation.Core.Vision;

public sealed record OcrTextRegion(int X, int Y, int Width, int Height);

public sealed record OcrTextLine(string Text, double Score, OcrTextRegion Region);

public sealed record OcrFrameResult(
    string Text,
    double Score,
    IReadOnlyList<OcrTextLine> Lines);

public interface IOcrProvider
{
    OcrFrameResult Recognize(BgraFrame frame);
}