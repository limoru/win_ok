using System.Runtime.InteropServices;
using Automation.Core.Frames;
using OpenCvSharp;

namespace Automation.Vision.Onnx;

public sealed record TemplateMatchOptions(
    double Threshold,
    double MinimumScale,
    double MaximumScale,
    double ScaleStep,
    int MaximumResults)
{
    public static TemplateMatchOptions SingleScale(double threshold, int maximumResults = 1)
        => new(threshold, 1.0, 1.0, 0.05, maximumResults);

    public static TemplateMatchOptions MultiScale(double threshold)
        => new(threshold, 0.80, 1.20, 0.05, 20);
}

public sealed record TemplateMatchResult(
    int X,
    int Y,
    int Width,
    int Height,
    double Score,
    bool Found,
    double Scale = 1.0);

public static class BgraTemplateMatcher
{
    private const int MaximumScales = 101;
    private const int MaximumCandidatesPerScale = 512;
    private const double IntersectionOverUnionThreshold = 0.25;

    public static TemplateMatchResult? Find(
        BgraFrame source,
        BgraFrame template,
        double threshold)
    {
        return FindMany(source, template, TemplateMatchOptions.SingleScale(threshold)).FirstOrDefault();
    }

    public static IReadOnlyList<TemplateMatchResult> FindMany(
        BgraFrame source,
        BgraFrame template,
        TemplateMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);

        if (source.Width <= 0 || source.Height <= 0)
        {
            return [];
        }

        if (template.Width <= 0 || template.Height <= 0)
        {
            return [];
        }

        var scales = BuildScales(options);
        using var sourceMat = CreateMat(source);
        using var templateMat = CreateMat(template);
        var candidates = new List<TemplateMatchResult>();

        foreach (var scale in scales)
        {
            var width = Math.Max(1, (int)Math.Round(template.Width * scale));
            var height = Math.Max(1, (int)Math.Round(template.Height * scale));
            if (width > source.Width || height > source.Height)
            {
                continue;
            }

            using var scaledTemplate = new Mat();
            Cv2.Resize(
                templateMat,
                scaledTemplate,
                new Size(width, height),
                0,
                0,
                scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
            using var result = new Mat();
            Cv2.MatchTemplate(
                sourceMat,
                scaledTemplate,
                result,
                TemplateMatchModes.CCoeffNormed);

            candidates.AddRange(ExtractCandidates(
                result,
                width,
                height,
                scale,
                options.Threshold));
        }

        return SuppressOverlaps(candidates, Math.Clamp(options.MaximumResults, 1, 100));
    }

    private static IReadOnlyList<TemplateMatchResult> ExtractCandidates(
        Mat result,
        int width,
        int height,
        double scale,
        double threshold)
    {
        var candidates = new List<TemplateMatchResult>();
        var rows = result.Rows;
        var columns = result.Cols;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var score = result.At<float>(y, x);
                if (float.IsNaN(score) || float.IsInfinity(score) || score < threshold)
                {
                    continue;
                }

                candidates.Add(new TemplateMatchResult(
                    x,
                    y,
                    width,
                    height,
                    score,
                    true,
                    scale));

                if (candidates.Count <= MaximumCandidatesPerScale)
                {
                    continue;
                }

                candidates.Sort(CompareCandidates);
                candidates.RemoveRange(
                    MaximumCandidatesPerScale,
                    candidates.Count - MaximumCandidatesPerScale);
            }
        }

        candidates.Sort(CompareCandidates);
        return candidates;
    }

    private static IReadOnlyList<TemplateMatchResult> SuppressOverlaps(
        IReadOnlyList<TemplateMatchResult> candidates,
        int maximumResults)
    {
        var ordered = candidates.OrderByDescending(candidate => candidate.Score).ToArray();
        var selected = new List<TemplateMatchResult>(Math.Min(maximumResults, ordered.Length));
        foreach (var candidate in ordered)
        {
            if (selected.Any(existing => IntersectionOverUnion(existing, candidate) > IntersectionOverUnionThreshold))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= maximumResults)
            {
                break;
            }
        }

        return selected;
    }

    private static double IntersectionOverUnion(
        TemplateMatchResult first,
        TemplateMatchResult second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersectionWidth = Math.Max(0, right - left);
        var intersectionHeight = Math.Max(0, bottom - top);
        var intersection = intersectionWidth * (double)intersectionHeight;
        if (intersection <= 0)
        {
            return 0;
        }

        var firstArea = first.Width * (double)first.Height;
        var secondArea = second.Width * (double)second.Height;
        return intersection / (firstArea + secondArea - intersection);
    }

    private static IReadOnlyList<double> BuildScales(TemplateMatchOptions options)
    {
        if (options.Threshold is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Threshold must be between -1 and 1.");
        }

        var minimum = Math.Min(options.MinimumScale, options.MaximumScale);
        var maximum = Math.Max(options.MinimumScale, options.MaximumScale);
        if (minimum <= 0 || maximum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Scale values must be positive.");
        }

        if (options.ScaleStep <= 0.001)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Scale step must be greater than 0.001.");
        }

        var scales = new List<double>();
        var scale = minimum;
        while (scale <= maximum + 0.0001 && scales.Count < MaximumScales)
        {
            scales.Add(Math.Round(scale, 4));
            scale += options.ScaleStep;
        }

        if (scales.Count == 0 || Math.Abs(scales[^1] - maximum) > 0.0001)
        {
            scales.Add(Math.Round(maximum, 4));
        }

        return scales;
    }

    private static int CompareCandidates(TemplateMatchResult? first, TemplateMatchResult? second)
        => (second?.Score ?? double.NegativeInfinity)
            .CompareTo(first?.Score ?? double.NegativeInfinity);

    private static Mat CreateMat(BgraFrame frame)
    {
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        try
        {
            Marshal.Copy(frame.Pixels, 0, mat.Data, frame.Pixels.Length);
            return mat.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
        finally
        {
            mat.Dispose();
        }
    }
}