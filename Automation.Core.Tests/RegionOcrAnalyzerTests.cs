using Automation.Core.Frames;
using Automation.Core.Vision;
using Xunit;

namespace Automation.Core.Tests;

public sealed class RegionOcrAnalyzerTests
{
    [Fact]
    public void AnalyzeCropsInMemoryAndMapsLocalCoordinatesToFullFrame()
    {
        var source = new BgraFrame(100, 80, new byte[100 * 80 * 4]);
        var scope = new OcrScanScope("center-confirmation", 20, 10, 40, 30, IsFullFrame: false);
        var provider = new RecordingOcrProvider(new OcrFrameResult(
            "确认 取消",
            0.98,
            [
                new OcrTextLine("确认", 0.98, new OcrTextRegion(3, 4, 10, 5)),
                new OcrTextLine("取消", 0.97, new OcrTextRegion(25, 4, 10, 5))
            ]));
        var analyzer = new RegionOcrAnalyzer();

        var result = analyzer.Analyze(source, scope, provider);

        Assert.Equal((40, 30), provider.CroppedSize);
        Assert.Same(scope, result.Scope);
        Assert.False(result.Scope.IsFullFrame);
        Assert.Collection(
            result.Recognition.Lines,
            line =>
            {
                Assert.Equal("确认", line.Text);
                Assert.Equal(new OcrTextRegion(23, 14, 10, 5), line.Region);
            },
            line =>
            {
                Assert.Equal("取消", line.Text);
                Assert.Equal(new OcrTextRegion(45, 14, 10, 5), line.Region);
            });
    }

    [Fact]
    public void FromRelativeUsesFullFramePixelBounds()
    {
        var scope = OcrScanScope.FromRelative(
            "center-confirmation",
            1600,
            900,
            0.009,
            0.168,
            0.977,
            0.875);

        Assert.Equal(new OcrScanScope(
            "center-confirmation",
            14,
            151,
            1550,
            637,
            IsFullFrame: false), scope);
    }

    private sealed class RecordingOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public (int Width, int Height) CroppedSize { get; private set; }

        public OcrFrameResult Recognize(BgraFrame frame)
        {
            CroppedSize = (frame.Width, frame.Height);
            return result;
        }
    }
}