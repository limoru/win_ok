using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Automation.Vision.Onnx;

public sealed class PpOcrEngine : IDisposable, IOcrProvider
{
    private readonly PpOcrDetector _detector;
    private readonly PpOcrRecognizer _recognizer;

    private PpOcrEngine(
        PpOcrDetector detector,
        PpOcrRecognizer recognizer,
        string modelDirectory)
    {
        _detector = detector;
        _recognizer = recognizer;
        ModelDirectory = modelDirectory;
    }

    public string ModelDirectory { get; }

    public static bool TryCreateDefault(out PpOcrEngine? engine, out string status)
    {
        if (!PpOcrModelLocator.TryFind(out var paths, out status))
        {
            engine = null;
            return false;
        }

        PpOcrDetector? detector = null;
        PpOcrRecognizer? recognizer = null;
        try
        {
            detector = new PpOcrDetector(paths.DetectionModelPath);
            if (!PpOcrRecognizer.TryCreate(paths, out recognizer, out var recognizerStatus)
                || recognizer is null)
            {
                throw new InvalidOperationException(recognizerStatus);
            }

            engine = new PpOcrEngine(detector, recognizer, paths.ModelDirectory);
            status = $"PP-OCRv5 检测与识别模型已加载：{paths.ModelDirectory}";
            return true;
        }
        catch (Exception exception)
        {
            detector?.Dispose();
            recognizer?.Dispose();
            engine = null;
            status = $"PP-OCRv5 加载失败：{exception.Message}";
            return false;
        }
    }

    public OcrFrameResult Recognize(BgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var boxes = MergeNearbyBoxes(_detector.Detect(frame));
        var lines = new List<OcrTextLine>(boxes.Count);
        foreach (var box in boxes)
        {
            var lineFrame = CropWithPadding(frame, box.Bounds);
            var recognized = _recognizer.Recognize(lineFrame);
            if (string.IsNullOrWhiteSpace(recognized.Text))
            {
                continue;
            }

            lines.Add(new OcrTextLine(
                recognized.Text,
                recognized.Score,
                new OcrTextRegion(
                    box.Bounds.X,
                    box.Bounds.Y,
                    box.Bounds.Width,
                    box.Bounds.Height)));
        }

        if (lines.Count == 0)
        {
            var recognized = _recognizer.Recognize(frame);
            if (!string.IsNullOrWhiteSpace(recognized.Text))
            {
                lines.Add(new OcrTextLine(
                    recognized.Text,
                    recognized.Score,
                    new OcrTextRegion(0, 0, frame.Width, frame.Height)));
            }
        }

        var text = string.Join(Environment.NewLine, lines.Select(line => line.Text));
        var weight = lines.Sum(line => Math.Max(1, line.Text.Length));
        var score = weight == 0
            ? 0
            : lines.Sum(line => line.Score * Math.Max(1, line.Text.Length)) / weight;

        return new OcrFrameResult(text, score, lines);
    }

    private static IReadOnlyList<OcrTextBoxCandidate> MergeNearbyBoxes(
        IReadOnlyList<OcrTextBoxCandidate> boxes)
    {
        var merged = new List<OcrTextBoxCandidate>(boxes.Count);
        foreach (var box in boxes)
        {
            var previous = merged.LastOrDefault();
            if (previous is not null && ShouldMerge(previous.Bounds, box.Bounds))
            {
                merged[^1] = Merge(previous, box);
                continue;
            }

            merged.Add(box);
        }

        return merged;
    }

    private static bool ShouldMerge(OpenCvSharp.Rect first, OpenCvSharp.Rect second)
    {
        var overlapTop = Math.Max(first.Top, second.Top);
        var overlapBottom = Math.Min(first.Bottom, second.Bottom);
        var verticalOverlap = Math.Max(0, overlapBottom - overlapTop);
        var minimumHeight = Math.Min(first.Height, second.Height);
        if (minimumHeight <= 0 || verticalOverlap < minimumHeight * 0.55)
        {
            return false;
        }

        var horizontalGap = second.Left - first.Right;
        return horizontalGap <= Math.Max(4, minimumHeight * 0.45);
    }

    private static OcrTextBoxCandidate Merge(
        OcrTextBoxCandidate first,
        OcrTextBoxCandidate second)
    {
        var left = Math.Min(first.Bounds.Left, second.Bounds.Left);
        var top = Math.Min(first.Bounds.Top, second.Bounds.Top);
        var right = Math.Max(first.Bounds.Right, second.Bounds.Right);
        var bottom = Math.Max(first.Bounds.Bottom, second.Bounds.Bottom);
        var firstArea = Math.Max(1, first.Bounds.Width * first.Bounds.Height);
        var secondArea = Math.Max(1, second.Bounds.Width * second.Bounds.Height);
        var score = ((first.Score * firstArea) + (second.Score * secondArea))
            / (firstArea + secondArea);
        return new OcrTextBoxCandidate(
            new OpenCvSharp.Rect(left, top, right - left, bottom - top),
            score,
            []);
    }
    private static BgraFrame CropWithPadding(BgraFrame frame, OpenCvSharp.Rect bounds)
    {
        const int Padding = 2;
        var x = Math.Clamp(bounds.X - Padding, 0, Math.Max(0, frame.Width - 1));
        var y = Math.Clamp(bounds.Y - Padding, 0, Math.Max(0, frame.Height - 1));
        var right = Math.Clamp(bounds.Right + Padding, x + 1, frame.Width);
        var bottom = Math.Clamp(bounds.Bottom + Padding, y + 1, frame.Height);
        return frame.Crop(x, y, right - x, bottom - y);
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _detector.Dispose();
    }
}