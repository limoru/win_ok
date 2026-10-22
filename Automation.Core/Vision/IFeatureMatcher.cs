using Automation.Core.Frames;

namespace Automation.Core.Vision;

/// <summary>
/// 归一化图像区域，坐标范围为 0..1，语义对应 Python 的 box_of_screen。
/// </summary>
public sealed record FeatureSearchRegion(double Left, double Top, double Right, double Bottom)
{
    public static FeatureSearchRegion FullScreen { get; } = new(0, 0, 1, 1);
}

/// <summary>
/// 特征匹配结果。坐标始终使用原图像素坐标。
/// </summary>
public sealed record FeatureBox(
    string FeatureName,
    int X,
    int Y,
    int Width,
    int Height,
    double Confidence)
{
    public double CenterX(int frameWidth)
        => frameWidth <= 0 ? 0 : (X + (Width / 2d)) / frameWidth;

    public double CenterY(int frameHeight)
        => frameHeight <= 0 ? 0 : (Y + (Height / 2d)) / frameHeight;
}

/// <summary>
/// 任务层使用的特征匹配接缝，游戏资产解析和 OpenCV 实现留在视觉实现层。
/// </summary>
public sealed record RuntimeFeatureTemplate(
    string FeatureName,
    int Width,
    int Height,
    byte[] Pixels);

/// <summary>
/// 保存任务运行期间生成的模板，例如目标主战员头像。
/// 实现方负责把模板跟任务方案持久化，加载后重新注册到特征匹配器。
/// </summary>
public interface IRuntimeFeatureTemplateStore
{
    IReadOnlyList<RuntimeFeatureTemplate> Load(string featureName);

    void Save(RuntimeFeatureTemplate template);

    void SaveBundle(IReadOnlyDictionary<string, RuntimeFeatureTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        foreach (var template in templates.Values)
        {
            Save(template);
        }
    }
}

public interface IFeatureMatcher
{
    IReadOnlyList<FeatureBox> Find(
        BgraFrame frame,
        string featureName,
        FeatureSearchRegion? region = null,
        double? threshold = null);
}

/// <summary>
/// 支持任务运行时注册模板的特征匹配器。目标主战员头像等模板不属于静态游戏素材，
/// 由任务在信息统计页面截取后注册。
/// </summary>
public interface IRuntimeFeatureMatcher : IFeatureMatcher
{
    RuntimeFeatureTemplate RegisterRuntimeTemplate(
        string featureName,
        BgraFrame sourceFrame,
        FeatureSearchRegion sourceRegion,
        FeatureSearchRegion targetRegion);

    void RegisterRuntimeTemplate(
        string featureName,
        RuntimeFeatureTemplate template);
}

public static class FeatureMatcherExtensions
{
    public static FeatureBox? FindOne(
        this IFeatureMatcher matcher,
        BgraFrame frame,
        string featureName,
        FeatureSearchRegion? region = null,
        double? threshold = null)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        return matcher
            .Find(frame, featureName, region, threshold)
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
    }
}
