using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Kes.Gui.DeveloperTools;

public sealed class DecisionSnapshotSavedEventArgs(
    string directory,
    DecisionSnapshotRequest request) : EventArgs
{
    public string Directory { get; } = directory;
    public DecisionSnapshotRequest Request { get; } = request;
}

public sealed class DecisionSnapshotService : IDecisionSnapshotSink
{
    private static readonly Color[] RegionColors =
    [
        Color.FromRgb(0, 213, 255),
        Color.FromRgb(255, 176, 32),
        Color.FromRgb(126, 231, 135),
        Color.FromRgb(255, 99, 132),
        Color.FromRgb(179, 136, 255),
        Color.FromRgb(255, 214, 102)
    ];

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DateTimeOffset> _lastAutomaticCapture = new(StringComparer.Ordinal);
    private readonly Typeface _regularTypeface = new(
        new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);
    private readonly Typeface _boldTypeface = new(
        new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    public DecisionSnapshotService(string outputDirectory, Dispatcher dispatcher)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        }

        OutputDirectory = outputDirectory;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event EventHandler<DecisionSnapshotSavedEventArgs>? SnapshotSaved;

    public bool IsEnabled { get; set; }
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromSeconds(45);
    public string OutputDirectory { get; }
    public string? LatestDirectory { get; private set; }

    public async ValueTask<string?> CaptureAsync(
        DecisionSnapshotRequest request,
        BgraFrame frame,
        OcrFrameResult? ocr,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsEnabled && !force)
        {
            return null;
        }

        if (_dispatcher.CheckAccess())
        {
            return CaptureCore(request, frame, ocr, force);
        }

        return await _dispatcher
            .InvokeAsync(() => CaptureCore(request, frame, ocr, force), DispatcherPriority.Background)
            .Task
            .ConfigureAwait(false);
    }

    private string? CaptureCore(
        DecisionSnapshotRequest request,
        BgraFrame frame,
        OcrFrameResult? ocr,
        bool force)
    {
        var now = request.CapturedAt == default ? DateTimeOffset.Now : request.CapturedAt;
        if (!force
            && _lastAutomaticCapture.TryGetValue(request.DeduplicationKey, out var lastCapturedAt)
            && now - lastCapturedAt < DeduplicationWindow)
        {
            return null;
        }

        Directory.CreateDirectory(OutputDirectory);
        var directory = CreateUniqueDirectory(request, now);
        Directory.CreateDirectory(directory);

        var source = CreateBitmapSource(frame);
        SavePng(source, Path.Combine(directory, "original.png"));

        var metadata = DecisionSnapshotMetadataBuilder.Build(request, frame, ocr);
        File.WriteAllText(
            Path.Combine(directory, "metadata.json"),
            DecisionSnapshotJson.SerializeMetadata(metadata),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!string.IsNullOrWhiteSpace(request.ContextLogs))
        {
            File.WriteAllText(
                Path.Combine(directory, "context.log"),
                request.ContextLogs,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        File.WriteAllText(
            Path.Combine(directory, "regions.suggested.json"),
            DecisionSnapshotJson.SerializeSuggestedRegions(metadata),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        SavePng(
            RenderAnnotated(source, metadata),
            Path.Combine(directory, "annotated.png"));

        _lastAutomaticCapture[request.DeduplicationKey] = now;
        LatestDirectory = directory;
        SnapshotSaved?.Invoke(this, new DecisionSnapshotSavedEventArgs(directory, request));
        return directory;
    }

    private string CreateUniqueDirectory(DecisionSnapshotRequest request, DateTimeOffset capturedAt)
    {
        var baseName = string.Join(
            "-",
            capturedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
            SanitizePathPart(request.SessionId),
            SanitizePathPart(request.ReasonId));
        var candidate = Path.Combine(OutputDirectory, baseName);
        var suffix = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(OutputDirectory, $"{baseName}-{suffix++}");
        }

        return candidate;
    }

    private BitmapSource RenderAnnotated(
        BitmapSource source,
        DecisionSnapshotMetadata metadata)
    {
        var width = metadata.FrameWidth;
        var height = metadata.FrameHeight;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(source, new Rect(0, 0, width, height));
            DrawHeader(drawing, metadata, width);
            DrawSuggestedRegions(drawing, metadata);
            DrawOcrLines(drawing, metadata);
            DrawCandidateActions(drawing, metadata);
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, width),
            Math.Max(1, height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void DrawHeader(DrawingContext drawing, DecisionSnapshotMetadata metadata, int width)
    {
        var titleSize = Math.Clamp(width / 70d, 13, 25);
        var bodySize = Math.Clamp(width / 120d, 9, 15);
        var headerHeight = Math.Clamp((titleSize * 4.8) + (bodySize * 2.4), 82, 164);
        drawing.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(232, 5, 16, 25)),
            null,
            new Rect(0, 0, width, headerHeight));
        drawing.DrawLine(
            new Pen(new SolidColorBrush(Color.FromRgb(0, 213, 255)), 2),
            new Point(0, headerHeight),
            new Point(width, headerHeight));

        var margin = Math.Clamp(width * 0.018, 14, 28);
        var title = CreateText(
            $"判断样本 · {metadata.PageLabel}",
            titleSize,
            Brushes.White,
            _boldTypeface,
            width - (margin * 2),
            titleSize * 1.5);
        drawing.DrawText(title, new Point(margin, margin * 0.65));

        var reason = CreateText(
            $"为什么标注：{metadata.Why}",
            bodySize,
            new SolidColorBrush(Color.FromRgb(219, 239, 255)),
            _regularTypeface,
            width - (margin * 2),
            Math.Max(bodySize * 2.4, headerHeight - (margin + titleSize * 1.5)));
        drawing.DrawText(reason, new Point(margin, margin + titleSize * 1.45));

        var details = string.Join(
            "  ·  ",
            $"原因 {metadata.ReasonId}",
            $"会话 {metadata.SessionId}",
            $"帧 #{metadata.FrameSequence}",
            $"{metadata.FrameWidth}x{metadata.FrameHeight}",
            $"ROI {metadata.Regions.Count}",
            $"OCR {metadata.Ocr?.Lines.Count ?? 0}",
            metadata.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        var detailText = CreateText(
            details,
            Math.Max(9, bodySize - 1),
            new SolidColorBrush(Color.FromRgb(122, 219, 255)),
            _regularTypeface,
            width - (margin * 2),
            bodySize * 1.5);
        drawing.DrawText(detailText, new Point(margin, headerHeight - (bodySize * 1.55)));
    }

    private void DrawSuggestedRegions(DrawingContext drawing, DecisionSnapshotMetadata metadata)
    {
        for (var index = 0; index < metadata.Regions.Count; index++)
        {
            var region = metadata.Regions[index];
            var color = RegionColors[index % RegionColors.Length];
            var rect = new Rect(
                region.Pixels.X,
                region.Pixels.Y,
                region.Pixels.Width,
                region.Pixels.Height);
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
                new Pen(new SolidColorBrush(color), 2.5),
                rect);

            var labelSize = Math.Clamp(metadata.FrameWidth / 125d, 10, 15);
            var label = CreateText(
                $"{index + 1}. {region.Label}",
                labelSize,
                Brushes.White,
                _boldTypeface,
                Math.Max(120, rect.Width - 8),
                labelSize * 1.8);
            var labelY = Math.Max(0, rect.Y - (label.Height + 5));
            if (labelY < 72)
            {
                labelY = rect.Y + 5;
            }

            var background = new Rect(
                rect.X + 3,
                labelY,
                Math.Min(rect.Width - 6, label.Width + 12),
                label.Height + 4);
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)),
                null,
                background);
            drawing.DrawText(label, new Point(background.X + 6, background.Y + 2));

            if (rect.Height >= label.Height + 34 && rect.Width >= 150)
            {
                var why = CreateText(
                    region.Why,
                    Math.Max(9, labelSize - 2),
                    new SolidColorBrush(Color.FromRgb(245, 250, 255)),
                    _regularTypeface,
                    Math.Max(80, rect.Width - 16),
                    Math.Min(58, Math.Max(18, rect.Height - label.Height - 14)));
                drawing.DrawText(why, new Point(rect.X + 8, rect.Y + label.Height + 7));
            }
        }
    }

    private void DrawOcrLines(DrawingContext drawing, DecisionSnapshotMetadata metadata)
    {
        if (metadata.Ocr is null)
        {
            return;
        }

        var textSize = Math.Clamp(metadata.FrameWidth / 145d, 8, 13);
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(220, 74, 222, 128)), 1.4);
        foreach (var line in metadata.Ocr.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            var rect = new Rect(line.Region.X, line.Region.Y, line.Region.Width, line.Region.Height);
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(18, 74, 222, 128)),
                stroke,
                rect);
            var text = CreateText(
                $"{line.Text}  {line.Score:0.00}",
                textSize,
                new SolidColorBrush(Color.FromRgb(205, 255, 218)),
                _regularTypeface,
                Math.Max(90, rect.Width * 2.6),
                textSize * 1.8);
            var labelY = Math.Max(0, rect.Y - text.Height - 2);
            var labelRect = new Rect(
                rect.X,
                labelY,
                Math.Min(metadata.FrameWidth - rect.X, text.Width + 8),
                text.Height + 3);
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(205, 14, 42, 27)),
                null,
                labelRect);
            drawing.DrawText(text, new Point(labelRect.X + 4, labelRect.Y + 1));
        }
    }

    private void DrawCandidateActions(DrawingContext drawing, DecisionSnapshotMetadata metadata)
    {
        var actionSize = Math.Clamp(metadata.FrameWidth / 135d, 9, 14);
        foreach (var action in metadata.CandidateActions)
        {
            if (action.X is not double x || action.Y is not double y)
            {
                continue;
            }

            var pixelX = Math.Clamp(x, 0, 1) * metadata.FrameWidth;
            var pixelY = Math.Clamp(y, 0, 1) * metadata.FrameHeight;
            var markerColor = new SolidColorBrush(Color.FromRgb(255, 80, 98));
            var markerPen = new Pen(markerColor, 2.2);
            const double radius = 18;
            drawing.DrawEllipse(null, markerPen, new Point(pixelX, pixelY), radius, radius);
            drawing.DrawLine(markerPen, new Point(pixelX - radius - 8, pixelY), new Point(pixelX + radius + 8, pixelY));
            drawing.DrawLine(markerPen, new Point(pixelX, pixelY - radius - 8), new Point(pixelX, pixelY + radius + 8));

            var label = CreateText(
                $"候选动作：{action.Label}  ({x:0.0000}, {y:0.0000})",
                actionSize,
                Brushes.White,
                _boldTypeface,
                Math.Min(420, metadata.FrameWidth * 0.42),
                actionSize * 2);
            var labelRect = new Rect(
                Math.Clamp(pixelX + radius + 10, 0, Math.Max(0, metadata.FrameWidth - label.Width - 16)),
                Math.Clamp(pixelY - label.Height - 8, 0, Math.Max(0, metadata.FrameHeight - label.Height - 8)),
                label.Width + 12,
                label.Height + 5);
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(225, 92, 15, 28)),
                null,
                labelRect);
            drawing.DrawText(label, new Point(labelRect.X + 6, labelRect.Y + 2));
        }
    }

    private FormattedText CreateText(
        string text,
        double fontSize,
        Brush brush,
        Typeface typeface,
        double maxWidth,
        double maxHeight)
    {
        var formatted = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            1.0);
        formatted.MaxTextWidth = Math.Max(1, maxWidth);
        formatted.MaxTextHeight = Math.Max(1, maxHeight);
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        return formatted;
    }

    private static BitmapSource CreateBitmapSource(BgraFrame frame)
    {
        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Pixels,
            frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            (value ?? string.Empty)
                .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
                .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim('-');
    }
}