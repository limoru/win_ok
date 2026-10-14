using System.Runtime.InteropServices;
using Automation.Core.Frames;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Automation.Vision.Onnx;

internal sealed record OcrTextBoxCandidate(
    Rect Bounds,
    double Score,
    Point2f[] Polygon);

internal sealed class PpOcrDetector : IDisposable
{
    private const int LimitSideLength = 960;
    private const int Stride = 32;
    private const float Threshold = 0.30f;
    private const float BoxThreshold = 0.50f;
    private const float UnclipRatio = 1.60f;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StandardDeviation = [0.229f, 0.224f, 0.225f];

    private readonly object _gate = new();
    private readonly InferenceSession _session;
    private readonly string _inputName;

    public PpOcrDetector(string modelPath)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public IReadOnlyList<OcrTextBoxCandidate> Detect(BgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return [];
        }

        var ratio = Math.Min(1d, LimitSideLength / (double)Math.Max(frame.Width, frame.Height));
        var resizedWidth = ResizeToStride(frame.Width * ratio);
        var resizedHeight = ResizeToStride(frame.Height * ratio);
        using var source = CreateBgrMat(frame);
        using var resized = source.Resize(new Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Linear);
        var input = CreateInputTensor(resized);

        float[] probabilities;
        int probabilityHeight;
        int probabilityWidth;
        lock (_gate)
        {
            using var results = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var output = results[0].AsTensor<float>();
            if (output.Dimensions.Length != 4
                || output.Dimensions[0] != 1
                || output.Dimensions[1] != 1)
            {
                throw new InvalidOperationException(
                    $"Unexpected OCR detection output shape: {string.Join("x", output.Dimensions.ToArray())}");
            }

            probabilityHeight = output.Dimensions[2];
            probabilityWidth = output.Dimensions[3];
            probabilities = new float[probabilityHeight * probabilityWidth];
            for (var y = 0; y < probabilityHeight; y++)
            {
                for (var x = 0; x < probabilityWidth; x++)
                {
                    probabilities[(y * probabilityWidth) + x] = output[0, 0, y, x];
                }
            }
        }

        using var probabilityMap = new Mat(
            probabilityHeight,
            probabilityWidth,
            MatType.CV_32FC1);
        Marshal.Copy(probabilities, 0, probabilityMap.Data, probabilities.Length);

        var boxes = ExtractBoxes(probabilityMap);
        return SortReadingOrder(boxes)
            .Select(box => MapToFrame(box, frame.Width, frame.Height, resizedWidth, resizedHeight))
            .Where(box => box.Bounds.Width >= 3 && box.Bounds.Height >= 3)
            .ToArray();
    }

    private static List<OcrTextBoxCandidate> ExtractBoxes(Mat probabilityMap)
    {
        using var binaryFloat = new Mat();
        Cv2.Threshold(probabilityMap, binaryFloat, Threshold, 1, ThresholdTypes.Binary);
        using var binary = new Mat();
        binaryFloat.ConvertTo(binary, MatType.CV_8UC1, 255);

        Cv2.FindContours(
            binary,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        var boxes = new List<OcrTextBoxCandidate>(contours.Length);
        foreach (var contour in contours)
        {
            var rotated = Cv2.MinAreaRect(contour);
            if (Math.Min(rotated.Size.Width, rotated.Size.Height) < 3)
            {
                continue;
            }

            var points = rotated.Points();
            var score = BoxScoreFast(probabilityMap, points);
            if (score < BoxThreshold)
            {
                continue;
            }

            var firstSide = Distance(points[0], points[1]);
            var secondSide = Distance(points[1], points[2]);
            var perimeter = 2 * (firstSide + secondSide);
            if (perimeter <= 0)
            {
                continue;
            }

            var distance = firstSide * secondSide * UnclipRatio / perimeter;
            var expanded = new RotatedRect(
                rotated.Center,
                new Size2f(
                    rotated.Size.Width + (2 * distance),
                    rotated.Size.Height + (2 * distance)),
                rotated.Angle);
            var expandedPoints = expanded.Points();
            var bounds = GetBounds(expandedPoints);
            if (bounds.Width < 3 || bounds.Height < 3)
            {
                continue;
            }

            boxes.Add(new OcrTextBoxCandidate(bounds, score, expandedPoints));
        }

        return boxes;
    }

    private static OcrTextBoxCandidate MapToFrame(
        OcrTextBoxCandidate box,
        int frameWidth,
        int frameHeight,
        int resizedWidth,
        int resizedHeight)
    {
        var ratioX = resizedWidth / (double)frameWidth;
        var ratioY = resizedHeight / (double)frameHeight;
        var mappedPoints = box.Polygon
            .Select(point => new Point2f(
                (float)(point.X / ratioX),
                (float)(point.Y / ratioY)))
            .ToArray();
        var mappedBounds = GetBounds(mappedPoints);
        var left = Math.Clamp(mappedBounds.Left - 1, 0, Math.Max(0, frameWidth - 1));
        var top = Math.Clamp(mappedBounds.Top - 1, 0, Math.Max(0, frameHeight - 1));
        var right = Math.Clamp(mappedBounds.Right + 1, left + 1, frameWidth);
        var bottom = Math.Clamp(mappedBounds.Bottom + 1, top + 1, frameHeight);

        return new OcrTextBoxCandidate(
            new Rect(left, top, right - left, bottom - top),
            box.Score,
            mappedPoints);
    }

    private static DenseTensor<float> CreateInputTensor(Mat bgr)
    {
        var width = bgr.Width;
        var height = bgr.Height;
        using var continuous = bgr.IsContinuous() ? bgr : bgr.Clone();
        var pixels = new byte[height * width * 3];
        Marshal.Copy(continuous.Data, pixels, 0, pixels.Length);

        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = ((y * width) + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = (pixels[pixelIndex + channel] / 255f);
                    tensor[0, channel, y, x] =
                        (value - Mean[channel]) / StandardDeviation[channel];
                }
            }
        }

        return tensor;
    }

    private static double BoxScoreFast(Mat probabilityMap, Point2f[] polygon)
    {
        var bounds = GetBounds(polygon);
        var left = Math.Clamp(bounds.Left, 0, probabilityMap.Width - 1);
        var top = Math.Clamp(bounds.Top, 0, probabilityMap.Height - 1);
        var right = Math.Clamp(bounds.Right, left + 1, probabilityMap.Width);
        var bottom = Math.Clamp(bounds.Bottom, top + 1, probabilityMap.Height);
        var roiRect = new Rect(left, top, right - left, bottom - top);

        using var roi = new Mat(probabilityMap, roiRect);
        using var mask = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.All(0));
        var shifted = polygon
            .Select(point => new Point(
                (int)Math.Round(point.X) - left,
                (int)Math.Round(point.Y) - top))
            .ToArray();
        Cv2.FillPoly(mask, new[] { shifted }, Scalar.All(1));
        return Cv2.Mean(roi, mask).Val0;
    }

    private static IReadOnlyList<OcrTextBoxCandidate> SortReadingOrder(
        IReadOnlyList<OcrTextBoxCandidate> boxes)
    {
        var rows = new List<List<OcrTextBoxCandidate>>();
        foreach (var box in boxes
                     .OrderBy(item => item.Bounds.Top + (item.Bounds.Height / 2d))
                     .ThenBy(item => item.Bounds.Left))
        {
            var centerY = box.Bounds.Top + (box.Bounds.Height / 2d);
            var row = rows.FirstOrDefault(candidateRow =>
            {
                var rowCenter = candidateRow.Average(item => item.Bounds.Top + (item.Bounds.Height / 2d));
                var tolerance = Math.Max(8, Math.Max(box.Bounds.Height, candidateRow.Max(item => item.Bounds.Height)) * 0.60);
                return Math.Abs(rowCenter - centerY) <= tolerance;
            });

            if (row is null)
            {
                rows.Add([box]);
            }
            else
            {
                row.Add(box);
            }
        }

        return rows
            .OrderBy(row => row.Average(item => item.Bounds.Top + (item.Bounds.Height / 2d)))
            .SelectMany(row => row.OrderBy(item => item.Bounds.Left))
            .ToArray();
    }

    private static Rect GetBounds(IReadOnlyCollection<Point2f> points)
    {
        var minX = (int)Math.Floor(points.Min(point => point.X));
        var minY = (int)Math.Floor(points.Min(point => point.Y));
        var maxX = (int)Math.Ceiling(points.Max(point => point.X));
        var maxY = (int)Math.Ceiling(points.Max(point => point.Y));
        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    private static float Distance(Point2f first, Point2f second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static int ResizeToStride(double value)
        => Math.Max(Stride, (int)Math.Round(value / Stride, MidpointRounding.AwayFromZero) * Stride);

    private static Mat CreateBgrMat(BgraFrame frame)
    {
        var bgra = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        try
        {
            Marshal.Copy(frame.Pixels, 0, bgra.Data, frame.Pixels.Length);
            return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
        finally
        {
            bgra.Dispose();
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}