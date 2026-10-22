using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Kes.Tasks.Configuration;

public sealed record TargetMemberAvatarFeature(
    string Name,
    int BaselineWidth,
    int BaselineHeight,
    FeatureSearchRegion? TargetRegion);

public sealed record TargetMemberAvatarTemplate(
    int Index,
    int Width,
    int Height,
    byte[] Pixels);

/// <summary>
/// Owns the feature bundle used to recognize one target-member avatar variant.
/// A variant is stored as one image per matching feature so all lists remain aligned.
/// </summary>
public static class TargetMemberAvatarCatalog
{
    public const string BaseFeatureName = "target_member";

    public static IReadOnlyList<TargetMemberAvatarFeature> Features { get; } =
    [
        new(BaseFeatureName, 109, 106, null),
        new("target_member_small", 85, 83, new FeatureSearchRegion(0.534, 0.225, 0.587125, 0.316667)),
        new("target_member_in_select_card", 93, 90, new FeatureSearchRegion(0.117708333, 0.178703704, 0.175520833, 0.278703704)),
        new("target_member_tiny", 67, 67, new FeatureSearchRegion(0.65790625, 0.470962963, 0.70009375, 0.545037037)),
        new("target_member_in_mask_card", 49, 48, new FeatureSearchRegion(0.663635417, 0.481148148, 0.694364583, 0.534851852)),
        new("target_member_large", 110, 107, new FeatureSearchRegion(0.218, 0.320, 0.28675, 0.439444444))
    ];

    public static IReadOnlyList<TargetMemberAvatarTemplate> Load(
        TaskConfigProfile profile,
        string targetName)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return [];
        }

        var key = CreateKey(profile.Id, targetName, BaseFeatureName);
        if (!profile.RuntimeTemplates.TryGetValue(key, out var templates))
        {
            return [];
        }

        return templates
            .Select((template, index) => new TargetMemberAvatarTemplate(
                index,
                template.Width,
                template.Height,
                template.Pixels.ToArray()))
            .ToArray();
    }

    public static bool AddAvatar(
        TaskConfigProfileStore store,
        string profileId,
        string targetName,
        BgraFrame avatar)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(avatar);

        var bundle = Features.ToDictionary(
            feature => CreateKey(profileId, targetName, feature.Name),
            feature =>
            {
                var resized = CenterCropAndResize(
                    avatar,
                    feature.BaselineWidth,
                    feature.BaselineHeight);
                return new RuntimeFeatureTemplate(
                    feature.Name,
                    resized.Width,
                    resized.Height,
                    resized.Pixels);
            },
            StringComparer.Ordinal);

        return store.SaveRuntimeTemplateBundle(profileId, bundle);
    }

    public static bool RemoveAvatarAt(
        TaskConfigProfileStore store,
        string profileId,
        string targetName,
        int index)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var keys = Features
            .Select(feature => CreateKey(profileId, targetName, feature.Name))
            .ToArray();
        return store.RemoveRuntimeTemplateAt(profileId, keys, index);
    }

    private static string CreateKey(string profileId, string targetName, string featureName)
    {
        var scope = TaskConfigProfileRuntimeTemplateStore.CreateScope(profileId, targetName);
        return TaskConfigProfileRuntimeTemplateStore.CreateKey(scope, featureName);
    }

    private static BgraFrame CenterCropAndResize(BgraFrame source, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var targetAspect = width / (double)height;
        var sourceAspect = source.Width / (double)source.Height;
        var cropWidth = source.Width;
        var cropHeight = source.Height;
        if (sourceAspect > targetAspect)
        {
            cropWidth = Math.Max(1, (int)Math.Round(source.Height * targetAspect));
        }
        else if (sourceAspect < targetAspect)
        {
            cropHeight = Math.Max(1, (int)Math.Round(source.Width / targetAspect));
        }

        var cropped = source.Crop(
            Math.Max(0, (source.Width - cropWidth) / 2),
            Math.Max(0, (source.Height - cropHeight) / 2),
            cropWidth,
            cropHeight);
        return Resize(cropped, width, height);
    }

    private static BgraFrame Resize(BgraFrame source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
        {
            return new BgraFrame(width, height, source.Pixels.ToArray());
        }

        var pixels = new byte[checked(width * height * 4)];
        for (var targetY = 0; targetY < height; targetY++)
        {
            var sourceY = Math.Clamp(
                ((targetY + 0.5) * source.Height / height) - 0.5,
                0,
                source.Height - 1d);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var yWeight = sourceY - y0;
            for (var targetX = 0; targetX < width; targetX++)
            {
                var sourceX = Math.Clamp(
                    ((targetX + 0.5) * source.Width / width) - 0.5,
                    0,
                    source.Width - 1d);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var xWeight = sourceX - x0;
                var targetOffset = ((targetY * width) + targetX) * 4;

                for (var channel = 0; channel < 4; channel++)
                {
                    var topLeft = source.Pixels[(((y0 * source.Width) + x0) * 4) + channel];
                    var topRight = source.Pixels[(((y0 * source.Width) + x1) * 4) + channel];
                    var bottomLeft = source.Pixels[(((y1 * source.Width) + x0) * 4) + channel];
                    var bottomRight = source.Pixels[(((y1 * source.Width) + x1) * 4) + channel];
                    var top = topLeft + ((topRight - topLeft) * xWeight);
                    var bottom = bottomLeft + ((bottomRight - bottomLeft) * xWeight);
                    pixels[targetOffset + channel] = (byte)Math.Clamp(
                        Math.Round(top + ((bottom - top) * yWeight)),
                        byte.MinValue,
                        byte.MaxValue);
                }
            }
        }

        return new BgraFrame(width, height, pixels);
    }
}
