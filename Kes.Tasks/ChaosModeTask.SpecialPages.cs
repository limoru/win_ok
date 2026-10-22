using System.Text.RegularExpressions;
using Automation.Core.Frames;
using Automation.Core.Targeting;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private static bool IsNonBattlePage(ChaosModePage page)
        => HasExactGameTextAtPoint(page, 0.887, 0.160, "故事")
           || HasExactGameTextAtPoint(page, 0.101, 0.046, "营救")
           || HasExactGameTextAtPoint(page, 0.124, 0.049, "方舟城市");

    private static ValueTask<bool> HandleNonBattlePageAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var pageName = GetNonBattlePageName(page);
        if (pageName is null)
        {
            return ValueTask.FromResult(false);
        }

        var reason = $"检测到{pageName}页面，停止当前模式。";
        page.Context.LogInfo(reason, "activity");
        return ValueTask.FromResult(page.Context.RequestStop(reason));
    }

    private static string? GetNonBattlePageName(ChaosModePage page)
    {
        if (HasExactGameTextAtPoint(page, 0.887, 0.160, "故事"))
        {
            return "故事";
        }

        if (HasExactGameTextAtPoint(page, 0.101, 0.046, "营救"))
        {
            return "营救";
        }

        return HasExactGameTextAtPoint(page, 0.124, 0.049, "方舟城市")
            ? "方舟城市"
            : null;
    }

    private static bool IsBattleAutoCheckPage(ChaosModePage page)
    {
        var handCount = FindBoxAtPoint(page, 0.512, 0.969);
        return handCount is not null
               && Regex.IsMatch(handCount.Text, @"\d+/10", RegexOptions.CultureInvariant);
    }

    private static async ValueTask<bool> HandleBattleAutoCheckAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var handCount = FindBoxAtPoint(page, 0.512, 0.969);
        if (handCount is null
            || !Regex.IsMatch(handCount.Text, @"\d+/10", RegexOptions.CultureInvariant))
        {
            return false;
        }

        var attack = FindFeatureOne(
            page,
            "start_attack_event",
            new RelativeRegion(0.394, 0.779, 0.600, 0.858));
        if (attack is not null)
        {
            var requestedAt = ResolveOperationTimestamp(page);
            if (!page.State.BattleEvent.TryBeginStartAttack(requestedAt))
            {
                page.Context.LogDebug(
                    "发起攻击已经点击过，忽略战斗预览残留帧，等待战斗状态变化。");
                return true;
            }

            page.Context.LogInfo(
                $"战斗页面检测到start_attack_event特征，相似度={attack.Confidence:0.0000}，点击发起攻击。",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(
                        attack.CenterX(page.Frame.Width),
                        attack.CenterY(page.Frame.Height)),
                    "点击发起攻击",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var whiteRatio = CalculateWhitePixelRatio(
            page.Frame,
            (int)(0.877 * page.Frame.Width),
            (int)(0.050 * page.Frame.Height),
            4,
            4);
        page.Context.LogInfo($"自动战斗按钮区域白色占比: {whiteRatio:P2}", "activity");
        if (whiteRatio > 0.02)
        {
            await ClickPointAsync(
                    page,
                    new RelativePoint(0.880, 0.056),
                    "自动战斗处于关闭状态，点击开启",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (page.State.ReachFinalBoss)
        {
            page.State.FinalBossBattle = true;
            page.Context.LogInfo("检测到最终boss战斗开始，FinalBossBattle=True。", "activity");
        }

        return true;
    }

    private static bool IsDiscoverySelectPage(ChaosModePage page)
        => RegionContainsGameText(
            page,
            new RelativeRegion(0.313, 0.010, 0.670, 0.193),
            "获得法典");

    private static async ValueTask<bool> HandleDiscoverySelectAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var titleText = RegionText(
            page,
            new RelativeRegion(0.313, 0.010, 0.670, 0.193));
        if (!GameText(page, "获得法典")
                .Any(expected => titleText.Contains(expected, StringComparison.Ordinal)))
        {
            return false;
        }

        page.Context.LogInfo($"检测到发现选择页面，标题区域文本=「{titleText}」", "activity");
        var requiredLevel = page.Context.GetTaskConfig("存储数据保存上限大于多少", 120);
        var optionRegions = new[]
        {
            new RelativeRegion(0.058, 0.358, 0.315, 0.800),
            new RelativeRegion(0.370, 0.356, 0.629, 0.796),
            new RelativeRegion(0.682, 0.356, 0.940, 0.796)
        };

        for (var index = 0; index < optionRegions.Length; index++)
        {
            var region = optionRegions[index];
            var optionText = RegionText(page, region);
            var value = MatchStorageDataValue(page, optionText);
            if (value is null)
            {
                page.Context.LogInfo(
                    $"发现选项{index + 1}未识别到存储数据保存上限，区域文本=「{optionText}」",
                    "activity");
                continue;
            }

            page.Context.LogInfo(
                $"发现选项{index + 1}存储数据保存上限={value.Value}，要求大于等于{requiredLevel}。",
                "activity");
            if (value.Value < requiredLevel)
            {
                continue;
            }

            var point = new RelativePoint(
                (region.Left + region.Right) / 2,
                (region.Top + region.Bottom) / 2);
            await ClickPointAsync(
                    page,
                    point,
                    $"发现选项{index + 1}满足保存上限要求，点击该选项",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.961, 0.050),
                "没有发现选项满足存储数据保存上限要求，点击取消",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static int? MatchStorageDataValue(ChaosModePage page, string text)
    {
        foreach (var storageDataText in GameText(page, "存储数据"))
        {
            var match = Regex.Match(
                text,
                Regex.Escape(storageDataText) + @"[^0-9]*?(\d+)",
                RegexOptions.CultureInvariant);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool RegionContainsGameText(
        ChaosModePage page,
        RelativeRegion region,
        string simplified)
    {
        var text = RegionText(page, region);
        return GameText(page, simplified)
            .Any(expected => text.Contains(expected, StringComparison.Ordinal));
    }

    private static double CalculateWhitePixelRatio(
        BgraFrame frame,
        int left,
        int top,
        int width,
        int height)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || width <= 0 || height <= 0)
        {
            return 0;
        }

        left = Math.Clamp(left, 0, frame.Width - 1);
        top = Math.Clamp(top, 0, frame.Height - 1);
        var right = Math.Min(frame.Width, left + width);
        var bottom = Math.Min(frame.Height, top + height);
        var whitePixels = 0;
        var totalPixels = 0;
        for (var y = top; y < bottom; y++)
        {
            var pixelOffset = (y * frame.Stride) + (left * 4);
            for (var x = left; x < right; x++)
            {
                totalPixels++;
                if (frame.Pixels[pixelOffset] == 255
                    && frame.Pixels[pixelOffset + 1] == 255
                    && frame.Pixels[pixelOffset + 2] == 255)
                {
                    whitePixels++;
                }

                pixelOffset += 4;
            }
        }

        return totalPixels == 0 ? 0 : (double)whitePixels / totalPixels;
    }
}
