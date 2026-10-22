using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Kes.Tasks;

public sealed record ChaosCardInfo(
    string Name,
    string Type,
    string Description,
    string FeatureName,
    double Confidence,
    double X,
    double Y,
    double FeatureX);

/// <summary>
/// 普通卡牌选择页面的 OCR/特征识别。只处理当前可见卡牌，不包含滚动、冥想或赛季规则。
/// </summary>
public static class ChaosCardRecognition
{
    private static readonly FeatureSearchRegion DefaultRegion = new(0.021, 0.172, 0.988, 0.432);
    private static readonly FeatureSearchRegion DeckRegion = new(0.274, 0.108, 0.929, 0.874);

    private static readonly IReadOnlyDictionary<string, string> FeatureTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attack"] = "攻击/基础攻击",
            ["skill"] = "技能/基础技能",
            ["enhance"] = "强化",
            ["hex"] = "咒术",
            ["abnormal"] = "状态异常"
        };

    private static readonly IReadOnlyDictionary<string, string> DeckFeatureTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attack_in_deck"] = "攻击/基础攻击",
            ["skill_in_deck"] = "技能/基础技能",
            ["enhance_in_deck"] = "强化",
            ["hex_in_deck"] = "咒术",
            ["hex_in_deck_tw"] = "诅咒"
        };

    public static IReadOnlyList<ChaosCardInfo> Recognize(
        BgraFrame frame,
        OcrFrameResult recognized,
        IFeatureMatcher featureMatcher,
        FeatureSearchRegion? region = null)
        => RecognizeCore(
            frame,
            recognized,
            featureMatcher,
            region ?? DefaultRegion,
            FeatureTypes,
            minimumFeatureDistance: 0.1640121936,
            nameOffsets: (-0.0150, -0.0635, 0.1450, -0.0175),
            typeOffsets: (0.0120, -0.0205, 0.1090, 0.0245),
            descriptionOffsets: (-0.0565, 0.1190, 0.1495, 0.4900),
            allowEmptyTypeThreshold: 0.90);

    public static IReadOnlyList<ChaosCardInfo> RecognizeDeck(
        BgraFrame frame,
        OcrFrameResult recognized,
        IFeatureMatcher featureMatcher,
        FeatureSearchRegion? region = null)
        => RecognizeCore(
            frame,
            recognized,
            featureMatcher,
            region ?? DeckRegion,
            DeckFeatureTypes,
            minimumFeatureDistance: Math.Sqrt(Math.Pow(0.464 - 0.326, 2)),
            nameOffsets: (-0.0090, -0.0435, 0.0900, -0.0105),
            typeOffsets: (0.0070, -0.0175, 0.0880, 0.0175),
            descriptionOffsets: (-0.0370, 0.0515, 0.1000, 0.3295),
            allowEmptyTypeThreshold: 0.90);

    private static IReadOnlyList<ChaosCardInfo> RecognizeCore(
        BgraFrame frame,
        OcrFrameResult recognized,
        IFeatureMatcher featureMatcher,
        FeatureSearchRegion searchRegion,
        IReadOnlyDictionary<string, string> featureTypes,
        double minimumFeatureDistance,
        (double Left, double Top, double Right, double Bottom) nameOffsets,
        (double Left, double Top, double Right, double Bottom) typeOffsets,
        (double Left, double Top, double Right, double Bottom) descriptionOffsets,
        double? allowEmptyTypeThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(featureMatcher);

        var candidates = new List<(string FeatureName, string FeatureType, FeatureBox Box)>();
        foreach (var (featureName, featureType) in featureTypes)
        {
            foreach (var box in featureMatcher.Find(frame, featureName, searchRegion, 0.65))
            {
                candidates.Add((featureName, featureType, box));
            }
        }

        var filtered = new List<(string FeatureName, string FeatureType, FeatureBox Box)>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Box.Confidence))
        {
            if (filtered.Any(kept => FeatureDistance(frame, candidate.Box, kept.Box) < minimumFeatureDistance))
            {
                continue;
            }

            filtered.Add(candidate);
        }

        var cards = new List<ChaosCardInfo>();
        foreach (var candidate in filtered)
        {
            var centerX = candidate.Box.CenterX(frame.Width);
            var centerY = candidate.Box.CenterY(frame.Height);
            var nameRegion = OffsetRegion(
                centerX,
                centerY,
                nameOffsets.Left,
                nameOffsets.Top,
                nameOffsets.Right,
                nameOffsets.Bottom);
            var typeRegion = OffsetRegion(
                centerX,
                centerY,
                typeOffsets.Left,
                typeOffsets.Top,
                typeOffsets.Right,
                typeOffsets.Bottom);
            var descriptionRegion = OffsetRegion(
                centerX,
                centerY,
                descriptionOffsets.Left,
                descriptionOffsets.Top,
                descriptionOffsets.Right,
                descriptionOffsets.Bottom);
            var name = RegionText(recognized, frame, nameRegion).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var type = RegionText(recognized, frame, typeRegion).Trim();
            var description = RegionText(recognized, frame, descriptionRegion).Trim();
            if (description.Length == 0
                || (type.Length == 0
                    && (allowEmptyTypeThreshold is null
                        || candidate.Box.Confidence <= allowEmptyTypeThreshold.Value)))
            {
                continue;
            }

            cards.Add(new ChaosCardInfo(
                name,
                type,
                description,
                candidate.FeatureName,
                candidate.Box.Confidence,
                (nameRegion.Left + nameRegion.Right) / 2,
                (nameRegion.Top + nameRegion.Bottom) / 2,
                candidate.Box.X));
        }

        return cards.OrderBy(card => card.FeatureX).ToArray();
    }

    public static ChaosCardInfo? ChooseByFlashPriority(
        IReadOnlyList<ChaosCardInfo> cards,
        IReadOnlyList<string> flashPriority)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(flashPriority);

        var normalizedPriority = flashPriority
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => string.Concat(keyword.Where(character => !char.IsWhiteSpace(character))))
            .Where(keyword => keyword.Length > 0)
            .ToArray();

        foreach (var keyword in normalizedPriority)
        {
            foreach (var card in cards)
            {
                if (MatchesFlashPriority(card, keyword))
                {
                    return card;
                }
            }
        }

        return null;
    }

    public static bool MatchesFlashPriority(ChaosCardInfo card, string? flashPriority)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.IsNullOrWhiteSpace(flashPriority))
        {
            return false;
        }

        var keyword = string.Concat(flashPriority.Where(character => !char.IsWhiteSpace(character)));
        return keyword.Length > 0 && IsSubsequence(keyword, $"{card.Name}：:{card.Description}");
    }

    public static bool IsSubsequence(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var secondIndex = 0;
        foreach (var character in first)
        {
            while (secondIndex < second.Length && second[secondIndex] != character)
            {
                secondIndex++;
            }

            if (secondIndex >= second.Length)
            {
                return false;
            }

            secondIndex++;
        }

        return true;
    }

    private static FeatureSearchRegion OffsetRegion(
        double centerX,
        double centerY,
        double left,
        double top,
        double right,
        double bottom)
        => new(
            Math.Clamp(centerX + left, 0, 1),
            Math.Clamp(centerY + top, 0, 1),
            Math.Clamp(centerX + right, 0, 1),
            Math.Clamp(centerY + bottom, 0, 1));

    private static string RegionText(
        OcrFrameResult recognized,
        BgraFrame frame,
        FeatureSearchRegion region)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in recognized.Lines)
        {
            var centerX = (line.Region.X + (line.Region.Width / 2d)) / frame.Width;
            var centerY = (line.Region.Y + (line.Region.Height / 2d)) / frame.Height;
            if (centerX >= region.Left
                && centerX <= region.Right
                && centerY >= region.Top
                && centerY <= region.Bottom)
            {
                builder.Append(line.Text.Trim());
            }
        }

        return builder.ToString();
    }

    private static double FeatureDistance(BgraFrame frame, FeatureBox first, FeatureBox second)
        => Math.Sqrt(
            Math.Pow(first.CenterX(frame.Width) - second.CenterX(frame.Width), 2)
            + Math.Pow(first.CenterY(frame.Height) - second.CenterY(frame.Height), 2));
}
