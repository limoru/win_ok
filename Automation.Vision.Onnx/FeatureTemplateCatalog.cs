using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Automation.Core.Frames;
using OpenCvSharp;

namespace Automation.Vision.Onnx;

public sealed record FeatureTemplate(
    string FeatureName,
    BgraFrame Image,
    int SourceImageWidth = 0,
    int SourceImageHeight = 0);

/// <summary>
/// 从 Python 版使用的 coco_annotations.json 和 images 目录构建模板集合。
/// 每个分类可以包含一个或多个标注，多个标注会作为同一特征的候选模板。
/// </summary>
public sealed class FeatureTemplateCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<FeatureTemplate>> _templates;

    private FeatureTemplateCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<FeatureTemplate>> templates)
    {
        _templates = templates;
    }

    public IReadOnlyCollection<string> FeatureNames => _templates.Keys.ToArray();

    public static FeatureTemplateCatalog FromTemplates(IEnumerable<FeatureTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return new FeatureTemplateCatalog(templates
            .GroupBy(template => template.FeatureName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<FeatureTemplate>)group.ToArray(),
                StringComparer.Ordinal));
    }

    public static FeatureTemplateCatalog Load(string assetRootOrCatalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRootOrCatalogPath);
        var catalogPath = File.Exists(assetRootOrCatalogPath)
            ? Path.GetFullPath(assetRootOrCatalogPath)
            : Path.Combine(Path.GetFullPath(assetRootOrCatalogPath), "coco_annotations.json");
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("找不到特征标注文件。", catalogPath);
        }

        var baseDirectory = Path.GetDirectoryName(catalogPath)
            ?? throw new InvalidDataException("特征标注文件没有有效目录。");
        var document = JsonSerializer.Deserialize<CocoDocument>(
            File.ReadAllText(catalogPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("特征标注文件内容为空。");

        var images = document.Images.ToDictionary(image => image.Id);
        var categories = document.Categories.ToDictionary(category => category.Id);
        var annotationsByCategory = document.Annotations
            .GroupBy(annotation => annotation.CategoryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var imageCache = new Dictionary<string, BgraFrame>(StringComparer.OrdinalIgnoreCase);
        var templates = new Dictionary<string, IReadOnlyList<FeatureTemplate>>(StringComparer.Ordinal);

        foreach (var category in document.Categories)
        {
            if (!annotationsByCategory.TryGetValue(category.Id, out var annotations)
                || annotations.Length == 0)
            {
                continue;
            }

            var categoryTemplates = new List<FeatureTemplate>(annotations.Length);
            foreach (var annotation in annotations)
            {
                if (!images.TryGetValue(annotation.ImageId, out var imageInfo))
                {
                    throw new InvalidDataException(
                        $"特征 {category.Name} 引用了不存在的 image_id={annotation.ImageId}。");
                }

                var imagePath = Path.Combine(
                    baseDirectory,
                    imageInfo.FileName.Replace('/', Path.DirectorySeparatorChar));
                if (!imageCache.TryGetValue(imagePath, out var source))
                {
                    source = ReadBgraFrame(imagePath);
                    imageCache[imagePath] = source;
                }

                categoryTemplates.Add(new FeatureTemplate(
                    category.Name,
                    CropAnnotation(source, annotation.Bbox, category.Name),
                    source.Width,
                    source.Height));
            }

            templates[category.Name] = categoryTemplates;
        }

        return new FeatureTemplateCatalog(templates);
    }

    public IReadOnlyList<FeatureTemplate> GetTemplates(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return _templates.TryGetValue(featureName, out var templates)
            ? templates
            : [];
    }

    private static BgraFrame ReadBgraFrame(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("找不到特征模板源图片。", imagePath);
        }

        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidDataException($"无法读取特征模板源图片：{imagePath}");
        }

        using var bgra = new Mat();
        Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[bgra.Rows * bgra.Cols * 4];
        if (bgra.IsContinuous())
        {
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        }
        else
        {
            var rows = bgra.Rows;
            for (var row = 0; row < rows; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(bgra.Data, row * (int)bgra.Step()),
                    pixels,
                    row * bgra.Cols * 4,
                    bgra.Cols * 4);
            }
        }

        return new BgraFrame(bgra.Cols, bgra.Rows, pixels);
    }

    private static BgraFrame CropAnnotation(
        BgraFrame source,
        IReadOnlyList<double> bbox,
        string featureName)
    {
        if (bbox.Count != 4)
        {
            throw new InvalidDataException($"特征 {featureName} 的 bbox 不是四个值。");
        }

        var x = Math.Clamp((int)Math.Round(bbox[0]), 0, source.Width - 1);
        var y = Math.Clamp((int)Math.Round(bbox[1]), 0, source.Height - 1);
        var width = Math.Clamp((int)Math.Round(bbox[2]), 1, source.Width - x);
        var height = Math.Clamp((int)Math.Round(bbox[3]), 1, source.Height - y);
        return source.Crop(x, y, width, height);
    }

    private sealed class CocoDocument
    {
        public List<CocoImage> Images { get; init; } = [];
        public List<CocoAnnotation> Annotations { get; init; } = [];
        public List<CocoCategory> Categories { get; init; } = [];
    }

    private sealed class CocoImage
    {
        public int Id { get; init; }
        [JsonPropertyName("file_name")]
        public string FileName { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
    }

    private sealed class CocoAnnotation
    {
        [JsonPropertyName("image_id")]
        public int ImageId { get; init; }
        [JsonPropertyName("category_id")]
        public int CategoryId { get; init; }
        public List<double> Bbox { get; init; } = [];
    }

    private sealed class CocoCategory
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}