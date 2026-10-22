using Automation.Core.Frames;
using Kes.Tasks.Configuration;

namespace Kes.Tasks;

public sealed record ChaosEquipmentCandidate(
    string OcrName,
    string Name,
    string Type,
    string Description,
    int Slot,
    IReadOnlyList<string> Priority,
    int? Rank,
    string Quality,
    double ClickX,
    double ClickY);

public sealed record ChaosEquipmentInstallDecision(bool ShouldInstall, string Reason);

/// <summary>
/// 装备选择的普通流程纯逻辑：槽位、配置优先级、品质比较。
/// </summary>
public static class ChaosEquipmentLogic
{
    private static readonly IReadOnlyDictionary<string, int> TypeSlots =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["攻击力"] = 0,
            ["防御力"] = 1,
            ["生命值"] = 2
        };

    private static readonly IReadOnlyDictionary<string, int> QualityRanks =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [""] = 0,
            ["普通"] = 1,
            ["史诗"] = 2,
            ["传说"] = 3
        };

    public static int? SlotFromType(string? typeText)
    {
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return null;
        }

        foreach (var (type, slot) in TypeSlots)
        {
            if (typeText.Contains(type, StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return null;
    }

    public static (string? Name, int? Rank) MatchEquipmentName(
        string? ocrName,
        IReadOnlyList<string> priority)
    {
        ArgumentNullException.ThrowIfNull(priority);
        var normalizedOcrName = ChaosEquipmentCatalog.Normalize(ocrName);
        if (normalizedOcrName.Length == 0)
        {
            return (null, null);
        }

        for (var index = 0; index < priority.Count; index++)
        {
            var configName = priority[index];
            var normalizedConfigName = ChaosEquipmentCatalog.Normalize(configName);
            if (normalizedConfigName.Length == 0)
            {
                continue;
            }

            if (normalizedOcrName.Contains(normalizedConfigName, StringComparison.Ordinal)
                || normalizedConfigName.Contains(normalizedOcrName, StringComparison.Ordinal))
            {
                return (configName, index);
            }
        }

        return (null, null);
    }

    public static int EquipmentRank(string? name, IReadOnlyList<string> priority)
        => MatchEquipmentName(name, priority).Rank ?? priority.Count;

    public static ChaosEquipmentInstallDecision DecideInstall(
        string? currentName,
        string? currentQuality,
        ChaosEquipmentCandidate candidate,
        IReadOnlyList<string>? otherSlotQualities = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var currentRank = MatchEquipmentName(currentName, candidate.Priority).Rank;
        if (candidate.Rank is not null || currentRank is not null)
        {
            if (candidate.Rank is not null && (currentRank is null || candidate.Rank < currentRank))
            {
                return new ChaosEquipmentInstallDecision(true, "配置优先级更高");
            }

            if (currentRank is not null && (candidate.Rank is null || currentRank < candidate.Rank))
            {
                return new ChaosEquipmentInstallDecision(false, "当前装备配置优先级更高");
            }
        }

        var currentQualityRank = QualityRanks.GetValueOrDefault(currentQuality ?? string.Empty);
        var newQualityRank = QualityRanks.GetValueOrDefault(candidate.Quality ?? string.Empty);
        var currentLabel = string.IsNullOrEmpty(currentQuality) ? "未安装" : currentQuality;
        var qualityDecision = new ChaosEquipmentInstallDecision(
            newQualityRank > currentQualityRank,
            $"品质{candidate.Quality}高于{currentLabel}");
        if (qualityDecision.ShouldInstall
            && string.Equals(candidate.Quality, "传说", StringComparison.Ordinal)
            && otherSlotQualities?.Any(quality =>
                string.Equals(quality, "传说", StringComparison.Ordinal)) == true)
        {
            return new ChaosEquipmentInstallDecision(false, "该主战员其他装备位已有传说装备");
        }

        return qualityDecision;
    }

    public static string QualityAt(BgraFrame frame, double relativeX, double relativeY, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return string.Empty;
        }

        var x = Math.Clamp((int)Math.Round(relativeX * frame.Width), 0, frame.Width - 1);
        var y = Math.Clamp((int)Math.Round(relativeY * frame.Height), 0, frame.Height - 1);
        var offset = (y * frame.Stride) + (x * 4);
        var blue = frame.Pixels[offset];
        var green = frame.Pixels[offset + 1];
        var red = frame.Pixels[offset + 2];
        if (allowEmpty && IsClose(red, green, blue, 15, 15, 15))
        {
            return string.Empty;
        }

        if (IsClose(red, green, blue, 61, 76, 138))
        {
            return "普通";
        }

        if (IsClose(red, green, blue, 160, 88, 69))
        {
            return "史诗";
        }

        return "传说";
    }

    private static bool IsClose(int red, int green, int blue, int expectedRed, int expectedGreen, int expectedBlue)
        => Math.Abs(red - expectedRed) <= 30
           && Math.Abs(green - expectedGreen) <= 30
           && Math.Abs(blue - expectedBlue) <= 30;
}
