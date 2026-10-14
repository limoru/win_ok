using System.Text.RegularExpressions;
using Automation.Core.DeveloperTools;
using Automation.Core.Targeting;
using Automation.Core.Vision;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private static readonly string[] EventFeatureNames =
        ["event1", "event2", "event3", "event4", "event5", "event6", "event7", "event8"];

    private static readonly RelativeRegion EventOptionsRegion =
        new(0.198, 0.840, 0.803, 1.000);

    // Python handle_event_task (utils.py:3610)：普通事件任务页面。
    private static async ValueTask<bool> HandleEventTaskAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var bottom = FindBoxAtPoint(page, 0.516, 0.971);
        if (bottom is not null && Regex.IsMatch(bottom.Text, @"\d+/\d+"))
        {
            return false;
        }

        var reward = FindFeatureOne(page, "taskreward");
        if (reward is not null)
        {
            var rewardX = reward.CenterX(page.Frame.Width);
            var rewardY = reward.CenterY(page.Frame.Height);
            if (rewardX is >= 0.437 and <= 0.902
                && rewardY is >= 0.350 and <= 0.614)
            {
                await ClickPointAsync(
                        page,
                        new RelativePoint(rewardX, rewardY),
                        "检测到任务奖励图标，优先点击",
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        var options = RecognizeEventOptions(page);
        if (options.Count == 0)
        {
            return false;
        }
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.EventOptionsReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var selectable = new List<EventOption>(options.Count);
        foreach (var option in options)
        {
            var forbiddenRegion = new RelativeRegion(
                Math.Clamp(option.CenterX - 0.131, 0, 1),
                Math.Clamp(option.CenterY - 0.257, 0, 1),
                Math.Clamp(option.CenterX - 0.090, 0, 1),
                Math.Clamp(option.CenterY - 0.189, 0, 1));
            var forbidden = FindFeatureOne(page, "forbidden_event", forbiddenRegion);
            if (forbidden is not null)
            {
                page.Context.LogInfo(
                    $"事件选项已被禁止，过滤描述「{option.Description}」，forbidden_event相似度={forbidden.Confidence:0.0000}。");
                continue;
            }

            selectable.Add(option);
        }

        if (selectable.Count == 0)
        {
            page.Context.LogInfo("事件任务页面的所有选项均被禁止，跳过本次选择。", "warning");
            return false;
        }

        var checkRegion = new RelativeRegion(0.396, 0.286, 0.960, 0.718);
        var check = new[] { "check", "check2" }
            .Select(name => FindFeatureOne(page, name, checkRegion))
            .Where(feature => feature is not null)
            .OrderByDescending(feature => feature!.Confidence)
            .FirstOrDefault();
        if (check is not null)
        {
            page.Context.LogInfo(
                $"事件任务页面检测到check特征，相似度={check.Confidence:0.0000}，优先点击。",
                "activity");
            await ClickFeatureAsync(page, check, "点击事件任务check特征", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(page.State.PendingEventOptionKey))
        {
            var pendingOption = selectable.FirstOrDefault(option =>
                string.Equals(
                    BuildEventOptionKey(option.Description),
                    page.State.PendingEventOptionKey,
                    StringComparison.Ordinal));
            if (pendingOption is not null)
            {
                page.State.PendingEventOptionAttempts++;
                page.Context.LogInfo(
                    $"继续重试本轮已选事件选项（第{page.State.PendingEventOptionAttempts}次）: {pendingOption.Description}",
                    "activity");
                await ClickEventOptionAsync(page, pendingOption, cancellationToken).ConfigureAwait(false);
                return true;
            }

            ClearPendingEventOption(page.State);
        }

        var upperOption = selectable.FirstOrDefault(option => option.CenterY < 0.925);
        if (upperOption is not null)
        {
            page.Context.LogInfo(
                $"检测到Y坐标小于0.925的任务，立即选择: {upperOption.Description}",
                "activity");
            await ClickAndRememberEventOptionAsync(page, upperOption, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var chosen = FindByPriority(
            selectable,
            page.Context.Config.GetStringList("必选任务优先级"));
        if (chosen is not null)
        {
            page.Context.LogInfo(
                $"命中必选任务优先级 -> 描述: {chosen.Description}（忽略拉黑过滤）",
                "activity");
        }

        if (chosen is null)
        {
            var blacklist = page.Context.Config.GetStringList("拉黑任务");
            if (blacklist.Count > 0)
            {
                var filtered = selectable
                    .Where(option => !blacklist.Any(keyword =>
                        !string.IsNullOrWhiteSpace(keyword)
                        && ChaosCardRecognition.IsSubsequence(keyword, option.Description)))
                    .ToList();
                if (filtered.Count < selectable.Count)
                {
                    page.Context.LogInfo(
                        $"拉黑任务关键词: {string.Join('、', blacklist)}，过滤前{selectable.Count}个，过滤后{filtered.Count}个。");
                }

                if (filtered.Count == 0)
                {
                    page.Context.LogInfo("所有任务均被拉黑，兜底使用原列表。", "warning");
                }
                else
                {
                    selectable = filtered;
                }
            }

            var equipmentPriority = new[]
                {
                    page.Context.Config.GetStringList("装备1号位优先级"),
                    page.Context.Config.GetStringList("装备2号位优先级"),
                    page.Context.Config.GetStringList("装备3号位优先级")
                }
                .SelectMany(values => values)
                .Concat(page.Context.Config.GetStringList("任务优先级"));
            chosen = FindByPriority(selectable, equipmentPriority);
            if (chosen is not null)
            {
                page.Context.LogInfo($"优先选择 -> 描述: {chosen.Description}", "activity");
            }
        }

        if (chosen is null)
        {
            var attackEvent = FindFeatures(page, "attack_event", threshold: 0.95)
                .OrderByDescending(feature => feature.Confidence)
                .FirstOrDefault();
            if (attackEvent is not null)
            {
                page.Context.LogInfo(
                    $"未命中任务优先级，检测到attack_event特征，相似度={attackEvent.Confidence:0.0000}，点击进入战斗任务。",
                    "activity");
                await ClickFeatureAsync(page, attackEvent, "点击战斗事件", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        if (chosen is null)
        {
            chosen = selectable.Count == 1
                ? selectable[0]
                : selectable[Random.Shared.Next(selectable.Count)];
            page.Context.LogInfo(
                $"未命中优先级描述，从{selectable.Count}个可选任务中选择: {chosen.Description}。",
                "activity");
        }

        await ClickAndRememberEventOptionAsync(page, chosen, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static IReadOnlyList<EventOption> RecognizeEventOptions(ChaosModePage page)
        => page.Classification.EventOptions;

    private static bool ContainsCjk(string value)
        => value.Any(character => character is >= '\u4e00' and <= '\u9fff');
    private static EventOption? FindByPriority(
        IReadOnlyList<EventOption> options,
        IEnumerable<string> priorities)
    {
        foreach (var keyword in priorities.Where(keyword => !string.IsNullOrWhiteSpace(keyword)))
        {
            var matched = options.FirstOrDefault(option =>
                ChaosCardRecognition.IsSubsequence(keyword, option.Description));
            if (matched is not null)
            {
                return matched;
            }
        }

        return null;
    }

    private static ValueTask ClickEventOptionAsync(
        ChaosModePage page,
        EventOption option,
        CancellationToken cancellationToken)
    {
        page.State.MainMemberFlashAttemptIndex = 0;
        return ClickPointAsync(
            page,
            option.ClickPoint,
            $"事件任务页面选择「{option.Description}」",
            cancellationToken);
    }

    private static async ValueTask ClickAndRememberEventOptionAsync(
        ChaosModePage page,
        EventOption option,
        CancellationToken cancellationToken)
    {
        page.State.PendingEventOptionKey = BuildEventOptionKey(option.Description);
        page.State.PendingEventOptionAttempts = 1;
        await ClickEventOptionAsync(page, option, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildEventOptionKey(string description)
    {
        var normalized = new string(description.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length <= 10 ? normalized : normalized[..10];
    }

    private static void ClearPendingEventOption(ChaosRunState state)
    {
        state.PendingEventOptionKey = string.Empty;
        state.PendingEventOptionAttempts = 0;
    }

    private static ValueTask ClickFeatureAsync(
        ChaosModePage page,
        FeatureBox feature,
        string message,
        CancellationToken cancellationToken)
        => ClickPointAsync(
            page,
            new RelativePoint(
                feature.CenterX(page.Frame.Width),
                feature.CenterY(page.Frame.Height)),
            message,
            cancellationToken);

    // Python handle_main_member_flash (utils.py:2156)：主战员闪光选择页依次尝试主战员。
    private static async ValueTask<bool> HandleMainMemberFlashAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var prompt = FindBoxAtPoint(page, 0.495, 0.936);
        if (prompt is null || !ContainsAny(page, prompt, "请选择获得"))
        {
            return false;
        }

        page.Context.LogInfo("检测主战员闪光选择，进行相应操作", "activity");
        var clickPositions = PrioritizeTargetMemberClicks(page);
        var confirmRegion = new RelativeRegion(0.145, 0.044, 0.856, 0.214);
        if (page.State.MainMemberFlashAttemptIndex > 0
            && FindFeatureOne(page, "flashmemberconfirm", confirmRegion) is not null)
        {
            page.State.MainMemberFlashAttemptIndex = 0;
            page.Context.LogInfo("已出现 flashmemberconfirm，等待后续确认处理。", "activity");
            return true;
        }

        if (clickPositions.Count == 0)
        {
            page.State.MainMemberFlashAttemptIndex = 0;
            return true;
        }

        var attemptIndex = page.State.MainMemberFlashAttemptIndex;
        if (attemptIndex < 0 || attemptIndex >= clickPositions.Count)
        {
            page.State.MainMemberFlashAttemptIndex = 0;
            page.Context.LogInfo("所有主战员闪光位置均已尝试，等待页面变化。", "warning");
            return true;
        }

        var position = clickPositions[attemptIndex];
        page.State.MainMemberFlashAttemptIndex = attemptIndex + 1;
        page.Context.LogInfo(
            $"点击位置 ({position.X:0.000}, {position.Y:0.000})",
            "activity");
        await ClickPointAsync(
                page,
                position,
                "选择获得闪光的主战员",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private readonly record struct EventFeature(
        string FeatureName,
        double CenterX,
        double CenterY,
        double Confidence);

    private sealed record EventOption(
        string FeatureName,
        string Description,
        double CenterX,
        double CenterY,
        RelativePoint ClickPoint,
        double Confidence);
}
