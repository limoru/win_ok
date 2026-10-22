using System.Runtime.InteropServices;
using Automation.Core.Frames;
using Automation.Core.Vision;
using OpenCvSharp;

namespace Automation.Vision.Onnx;

/// <summary>
/// 基于 coco_annotations.json 模板的 IFeatureMatcher 实现。
/// </summary>
public sealed class TemplateFeatureMatcher : IFeatureMatcher, IRuntimeFeatureMatcher
{
    private readonly FeatureTemplateCatalog _catalog;
    private readonly double _defaultThreshold;
    private readonly int _maximumResults;
    private readonly object _runtimeTemplateGate = new();
    private const int MaximumRuntimeTemplatesPerFeature = 5;
    private readonly Dictionary<string, List<FeatureTemplate>> _runtimeTemplates = new(StringComparer.Ordinal);
    private readonly Dictionary<TemplateScaleKey, BgraFrame> _templateScaleCache = [];
    private readonly object _templateScaleGate = new();

    public TemplateFeatureMatcher(
        FeatureTemplateCatalog catalog,
        double defaultThreshold = 0.8,
        int maximumResults = 50)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (defaultThreshold is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultThreshold));
        }

        if (maximumResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        _defaultThreshold = defaultThreshold;
        _maximumResults = maximumResults;
    }

    public IReadOnlyList<FeatureBox> Find(
        BgraFrame frame,
        string featureName,
        FeatureSearchRegion? region = null,
        double? threshold = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        if (threshold is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        var effectiveThreshold = threshold ?? _defaultThreshold;
        FeatureTemplate[] templates;
        lock (_runtimeTemplateGate)
        {
            var staticTemplates = _catalog.GetTemplates(featureName);
            templates = _runtimeTemplates.TryGetValue(featureName, out var runtimeTemplates)
                ? staticTemplates.Concat(runtimeTemplates).ToArray()
                : staticTemplates.ToArray();
        }

        if (templates.Length == 0)
        {
            return [];
        }

        var bounds = ToPixelBounds(region ?? FeatureSearchRegion.FullScreen, frame);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return [];
        }

        var searchFrame = frame.Crop(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var results = new List<FeatureBox>();
        foreach (var template in templates)
        {
            var templateImage = GetTemplateForFrame(template, frame);
            if (templateImage.Width > searchFrame.Width
                || templateImage.Height > searchFrame.Height)
            {
                continue;
            }

            var matches = BgraTemplateMatcher.FindMany(
                searchFrame,
                templateImage,
                TemplateMatchOptions.SingleScale(effectiveThreshold, _maximumResults));
            results.AddRange(matches.Select(match => new FeatureBox(
                featureName,
                match.X + bounds.X,
                match.Y + bounds.Y,
                match.Width,
                match.Height,
                match.Score)));
        }

        return results
            .OrderByDescending(match => match.Confidence)
            .Take(_maximumResults)
            .ToArray();
    }

    public RuntimeFeatureTemplate RegisterRuntimeTemplate(
        string featureName,
        BgraFrame sourceFrame,
        FeatureSearchRegion sourceRegion,
        FeatureSearchRegion targetRegion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(sourceRegion);
        ArgumentNullException.ThrowIfNull(targetRegion);

        var sourceBounds = ToRoundedPixelBounds(sourceRegion, sourceFrame, nameof(sourceRegion));
        var targetBounds = ToRoundedPixelBounds(targetRegion, sourceFrame, nameof(targetRegion));
        var source = sourceFrame.Crop(
            sourceBounds.X,
            sourceBounds.Y,
            sourceBounds.Width,
            sourceBounds.Height);
        var image = Resize(source, targetBounds.Width, targetBounds.Height);
        var template = new RuntimeFeatureTemplate(
            featureName,
            image.Width,
            image.Height,
            image.Pixels.ToArray());
        RegisterRuntimeTemplate(featureName, template);
        return template;
    }

    public void RegisterRuntimeTemplate(
        string featureName,
        RuntimeFeatureTemplate template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        ArgumentNullException.ThrowIfNull(template);
        if (template.Width <= 0
            || template.Height <= 0
            || template.Pixels.Length != checked(template.Width * template.Height * 4))
        {
            throw new ArgumentException("Runtime feature template dimensions are invalid.", nameof(template));
        }

        var image = new BgraFrame(
            template.Width,
            template.Height,
            template.Pixels.ToArray());
        lock (_runtimeTemplateGate)
        {
            if (!_runtimeTemplates.TryGetValue(featureName, out var templates))
            {
                templates = [];
                _runtimeTemplates[featureName] = templates;
            }

            if (templates.Any(existing => ImagesEqual(existing.Image, image)))
            {
                return;
            }

            templates.Add(new FeatureTemplate(featureName, image));
            if (templates.Count > MaximumRuntimeTemplatesPerFeature)
            {
                templates.RemoveRange(0, templates.Count - MaximumRuntimeTemplatesPerFeature);
            }
        }
    }
    private BgraFrame GetTemplateForFrame(FeatureTemplate template, BgraFrame frame)
    {
        if (template.SourceImageWidth <= 0 || template.SourceImageHeight <= 0)
        {
            return template.Image;
        }

        if (frame.Width == template.SourceImageWidth
            && frame.Height == template.SourceImageHeight)
        {
            return template.Image;
        }

        var scale = Math.Min(
            frame.Width / (double)template.SourceImageWidth,
            frame.Height / (double)template.SourceImageHeight);
        if (scale <= 0)
        {
            return template.Image;
        }

        var width = Math.Max(1, (int)Math.Round(template.Image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(template.Image.Height * scale));
        if (width == template.Image.Width && height == template.Image.Height)
        {
            return template.Image;
        }

        var key = new TemplateScaleKey(template, frame.Width, frame.Height);
        lock (_templateScaleGate)
        {
            if (!_templateScaleCache.TryGetValue(key, out var cached))
            {
                cached = Resize(template.Image, width, height, InterpolationFlags.Linear);
                _templateScaleCache[key] = cached;
            }

            return cached;
        }
    }

    private static bool ImagesEqual(BgraFrame first, BgraFrame second)
        => first.Width == second.Width
           && first.Height == second.Height
           && first.Pixels.AsSpan().SequenceEqual(second.Pixels);

    private static PixelBounds ToRoundedPixelBounds(
        FeatureSearchRegion region,
        BgraFrame frame,
        string parameterName)
    {
        if (region.Left < 0
            || region.Top < 0
            || region.Right > 1
            || region.Bottom > 1
            || region.Right <= region.Left
            || region.Bottom <= region.Top)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Search region must be a normalized non-empty rectangle.");
        }

        var left = Math.Clamp((int)Math.Round(region.Left * frame.Width), 0, frame.Width - 1);
        var top = Math.Clamp((int)Math.Round(region.Top * frame.Height), 0, frame.Height - 1);
        var right = Math.Clamp((int)Math.Round(region.Right * frame.Width), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Round(region.Bottom * frame.Height), top + 1, frame.Height);
        return new PixelBounds(left, top, right - left, bottom - top);
    }

    private static BgraFrame Resize(
        BgraFrame source,
        int width,
        int height,
        InterpolationFlags? interpolation = null)
    {
        if (source.Width == width && source.Height == height)
        {
            return source;
        }

        using var sourceMat = new Mat(source.Height, source.Width, MatType.CV_8UC4);
        Marshal.Copy(source.Pixels, 0, sourceMat.Data, source.Pixels.Length);
        using var resized = new Mat();
        Cv2.Resize(
            sourceMat,
            resized,
            new Size(width, height),
            0,
            0,
            interpolation
            ?? (width < source.Width || height < source.Height
                ? InterpolationFlags.Area
                : InterpolationFlags.Linear));

        var pixels = new byte[width * height * 4];
        if (resized.IsContinuous())
        {
            Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
        }
        else
        {
            var rowBytes = width * 4;
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(resized.Data, row * (int)resized.Step()),
                    pixels,
                    row * rowBytes,
                    rowBytes);
            }
        }

        return new BgraFrame(width, height, pixels);
    }

    private static PixelBounds ToPixelBounds(FeatureSearchRegion region, BgraFrame frame)
    {
        if (region.Left < 0
            || region.Top < 0
            || region.Right > 1
            || region.Bottom > 1
            || region.Right <= region.Left
            || region.Bottom <= region.Top)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Search region must be a normalized non-empty rectangle.");
        }

        var left = Math.Clamp((int)Math.Floor(region.Left * frame.Width), 0, frame.Width);
        var top = Math.Clamp((int)Math.Floor(region.Top * frame.Height), 0, frame.Height);
        var right = Math.Clamp((int)Math.Ceiling(region.Right * frame.Width), 0, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Bottom * frame.Height), 0, frame.Height);
        return new PixelBounds(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private readonly record struct TemplateScaleKey(
        FeatureTemplate Template,
        int FrameWidth,
        int FrameHeight);

    private sealed record PixelBounds(int X, int Y, int Width, int Height);
}