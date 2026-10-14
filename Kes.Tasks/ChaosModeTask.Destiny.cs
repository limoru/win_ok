using Automation.Core.DeveloperTools;
using Automation.Core.Vision;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    // Python handle_destiny_choice (utils.py:2083)：命运选择奖励页面。
    private static async ValueTask<bool> HandleDestinyChoiceAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var prompt = FindBoxAtPoint(page, 0.499, 0.932);
        if (prompt is null || !ContainsAny(page, prompt, "请选择你的命运"))
        {
            return false;
        }

        page.Context.LogInfo("检测到命运选择奖励，进行相应操作。", "activity");

        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.DestinyChoiceReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var titleRegion = new RelativeRegion(0.202, 0.474, 0.800, 0.600);
        var titles = page.Recognized.Lines
            .Where(line => titleRegion.Contains(line, page.Frame.Width, page.Frame.Height))
            .Where(line => line.Text.Trim().Length > 1)
            .Where(line => !MatchesAny(page, line, "确认")
                           && !MatchesAny(page, line, "返回")
                           && !MatchesAny(page, line, "跳过"))
            .ToArray();
        if (titles.Length == 0)
        {
            page.Context.LogInfo("命运选择页面未识别到可选命运标题。", "warning");
            return false;
        }

        var chosen = titles.Length == 1
            ? titles[0]
            : titles[Random.Shared.Next(titles.Length)];
        page.Context.LogInfo($"随机选择命运: {chosen.Text}", "activity");
        await ClickTextAsync(page, chosen, $"选择命运「{chosen.Text}」", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}