using System.Runtime.InteropServices;
using System.Text;
using Automation.Core.Frames;
using Automation.Core.Vision;
using Automation.Vision.Onnx;
using OpenCvSharp;
using Xunit;

namespace Automation.Core.Tests;

public sealed class FeatureMatcherTests
{
    [Fact]
    public void TemplateMatcherFindsSyntheticPatternWithinRegion()
    {
        var template = CreatePatternFrame(12, 10);
        var source = CreateSolidFrame(160, 100, 40, 40, 40, 255);
        DrawPattern(source, template, 50, 30);
        var catalog = FeatureTemplateCatalog.FromTemplates(
            [new FeatureTemplate("position", template)]);
        var matcher = new TemplateFeatureMatcher(catalog, defaultThreshold: 0.95);

        var results = matcher.Find(
            source,
            "position",
            new FeatureSearchRegion(0.2, 0.2, 0.6, 0.7));

        var match = Assert.Single(results);
        Assert.InRange(match.X, 49, 52);
        Assert.InRange(match.Y, 29, 32);
        Assert.InRange(match.Width, 11, 13);
        Assert.InRange(match.Height, 9, 11);
        Assert.True(match.Confidence >= 0.95);
    }
    [Fact]
    public void TemplateMatcherScalesTemplateToFrameResolution()
    {
        var source = CreateSolidFrame(160, 100, 10, 10, 10, 255);
        var pattern = CreatePatternFrame(24, 12);
        DrawPattern(source, pattern, 48, 32);
        var template = source.Crop(48, 32, 24, 12);
        var resizedSource = ResizeFrame(source, 80, 50);
        var catalog = FeatureTemplateCatalog.FromTemplates(
            [new FeatureTemplate("position", template, source.Width, source.Height)]);
        var matcher = new TemplateFeatureMatcher(catalog, defaultThreshold: 0.80);

        var results = matcher.Find(
            resizedSource,
            "position",
            new FeatureSearchRegion(0.1, 0.1, 0.9, 0.9));

        var match = Assert.Single(results);
        Assert.InRange(match.X, 23, 25);
        Assert.InRange(match.Y, 15, 17);
        Assert.InRange(match.Width, 11, 13);
        Assert.InRange(match.Height, 5, 7);
    }
    [Fact]
    public void RuntimeTemplateCanBeRegisteredAndMatched()
    {
        var source = CreateSolidFrame(160, 100, 40, 40, 40, 255);
        var pattern = CreatePatternFrame(48, 20);
        DrawPattern(source, pattern, 16, 10);
        var matcher = new TemplateFeatureMatcher(
            FeatureTemplateCatalog.FromTemplates([]),
            defaultThreshold: 0.95);
        var region = new FeatureSearchRegion(0.1, 0.1, 0.4, 0.3);

        matcher.RegisterRuntimeTemplate("runtime", source, region, region);
        var match = Assert.Single(matcher.Find(source, "runtime", region));

        Assert.InRange(match.X, 15, 17);
        Assert.InRange(match.Y, 9, 11);
        Assert.InRange(match.Width, 47, 49);
        Assert.InRange(match.Height, 19, 21);
    }
    [Fact]
    public void TemplateMatcherReturnsEmptyForUnknownFeature()
    {
        var catalog = FeatureTemplateCatalog.FromTemplates(
            [new FeatureTemplate("known", CreatePatternFrame(8, 8))]);
        var matcher = new TemplateFeatureMatcher(catalog);

        var results = matcher.Find(CreateSolidFrame(80, 60), "missing");

        Assert.Empty(results);
    }    [Fact]
    public void BundledCatalogLoadsAllPythonCategories()
    {
        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ChaosFeatures");
        var catalog = FeatureTemplateCatalog.Load(assetPath);

        Assert.Equal(79, catalog.FeatureNames.Count);
        Assert.Contains("position", catalog.FeatureNames);
        Assert.Contains("minimizemap", catalog.FeatureNames);
        Assert.NotEmpty(catalog.GetTemplates("enemy_in_map"));
    }


    [Fact]


    public void CatalogLoadsCocoAnnotationAndCropsTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kes-feature-tests-{Guid.NewGuid():N}");
        try
        {
            var imagesDirectory = Path.Combine(root, "images");
            Directory.CreateDirectory(imagesDirectory);
            var image = CreateSolidFrame(100, 80, 10, 10, 10, 255);
            DrawPattern(image, CreatePatternFrame(12, 10), 20, 15);
            WritePng(Path.Combine(imagesDirectory, "0.png"), image);
            File.WriteAllText(
                Path.Combine(root, "coco_annotations.json"),
                """
                {
                  "images": [
                    { "id": 1, "file_name": "images/0.png", "width": 100, "height": 80 }
                  ],
                  "annotations": [
                    { "id": 1, "image_id": 1, "category_id": 1, "bbox": [20, 15, 12, 10] }
                  ],
                  "categories": [
                    { "id": 1, "name": "position", "supercategory": "" }
                  ]
                }
                """,
                new UTF8Encoding(false));

            var catalog = FeatureTemplateCatalog.Load(root);
            var template = Assert.Single(catalog.GetTemplates("position"));

            Assert.Equal(12, template.Image.Width);
            Assert.Equal(10, template.Image.Height);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static BgraFrame CreateSolidFrame(
        int width,
        int height,
        byte blue = 255,
        byte green = 255,
        byte red = 255,
        byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }

        return new BgraFrame(width, height, pixels);
    }

    private static BgraFrame CreatePatternFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)((x * 31 + y * 17) & 0xff);
                pixels[offset + 1] = (byte)((x * 13 + y * 47) & 0xff);
                pixels[offset + 2] = (byte)((x * 7 + y * 29) & 0xff);
                pixels[offset + 3] = 255;
            }
        }

        return new BgraFrame(width, height, pixels);
    }

    private static void DrawPattern(BgraFrame target, BgraFrame pattern, int x, int y)
    {
        for (var row = 0; row < pattern.Height; row++)
        {
            var sourceOffset = row * pattern.Stride;
            var targetOffset = ((y + row) * target.Stride) + (x * 4);
            Buffer.BlockCopy(
                pattern.Pixels,
                sourceOffset,
                target.Pixels,
                targetOffset,
                pattern.Stride);
        }
    }

    private static BgraFrame ResizeFrame(BgraFrame source, int width, int height)
    {
        using var sourceMat = new Mat(source.Height, source.Width, MatType.CV_8UC4);
        Marshal.Copy(source.Pixels, 0, sourceMat.Data, source.Pixels.Length);
        using var resized = new Mat();
        Cv2.Resize(sourceMat, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);
        var pixels = new byte[checked(width * height * 4)];
        Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
        return new BgraFrame(width, height, pixels);
    }

    private static void WritePng(string path, BgraFrame frame)
    {
        using var bgra = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        Marshal.Copy(frame.Pixels, 0, bgra.Data, frame.Pixels.Length);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        if (!Cv2.ImWrite(path, bgr))
        {
            throw new InvalidOperationException($"Failed to write test image: {path}");
        }
    }
}