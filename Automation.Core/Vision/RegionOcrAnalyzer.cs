using Automation.Core.Frames;

namespace Automation.Core.Vision;

public sealed record RegionOcrResult(
    OcrFrameResult Recognition,
    OcrScanScope Scope);

public sealed class RegionOcrAnalyzer
{
    public RegionOcrResult Analyze(
        BgraFrame source,
        OcrScanScope scope,
        IOcrProvider ocr)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ocr);

        if (scope.X < 0
            || scope.Y < 0
            || scope.Width <= 0
            || scope.Height <= 0
            || scope.X + scope.Width > source.Width
            || scope.Y + scope.Height > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scope),
                $"OCR scope '{scope.Id}' is outside the source frame {source.Width}x{source.Height}.");
        }

        var cropped = source.Crop(scope.X, scope.Y, scope.Width, scope.Height);
        var local = ocr.Recognize(cropped);
        var lines = local.Lines
            .Select(line => line with
            {
                Region = line.Region with
                {
                    X = line.Region.X + scope.X,
                    Y = line.Region.Y + scope.Y
                }
            })
            .ToArray();
        return new RegionOcrResult(
            new OcrFrameResult(local.Text, local.Score, lines),
            scope);
    }
}