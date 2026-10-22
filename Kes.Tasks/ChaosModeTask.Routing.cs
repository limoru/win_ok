using System.Text.RegularExpressions;
using Automation.Core.Dispatching;
using Automation.Core.Vision;
using Kes.Tasks.Configuration;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private static FrameRoute<ChaosModePage>[] CreateFrameRoutes() =>
    [
        Route(ClosePageConsumerId, 5,
            page => FindTextAnywhere(page, GameText(page, "点击屏幕")) is not null,
            HandleClosePageAsync,
            allowFallbackWhenUnhandled: false,
            actionRetryAfterFrames: GetActionRetryAfterFrames,
            isPriorityOverlay: true),
        Route("snow-season", 8, IsSnowSeasonPage, HandleSnowSeasonAsync,
            actionRetryAfterFrames: GetActionRetryAfterFrames),
        Route("route-selection", 10, IsRouteSelectionPage, HandleRouteSelectionActionAsync),
        Route("refine-equipment-credit", 20,
            page => HasContainedTextAtPoint(page, 0.598, 0.635, "以信用点接收"),
            HandleRefineEquipmentCreditAsync),
        Route("validation-card-prompt", 30,
            page => page.Recognized.Lines.Any(line =>
                line.Text.Contains("不使用战利品验证卡", StringComparison.Ordinal)
                || line.Text.Contains("不使用戰利品驗證卡", StringComparison.Ordinal)),
            HandleValidationCardPromptAsync),
        Route("event-task", 40, IsEventTaskPage, HandleEventTaskAsync,
            actionRetryAfterFrames: GetActionRetryAfterFrames),
        Route("shared-confirmation", 50,
            page => FindCenterConfirmation(page) is not null,
            HandleSharedConfirmationAsync,
            useActionBoundary: false),
        Route("confirm", 56, ShouldHandleConfirmButton, HandleConfirmAsync,
            allowFallbackWhenUnhandled: true,
            actionRetryAfterFrames: GetActionRetryAfterFrames),
        Route("main-member-flash", 52, IsMainMemberFlashPage, HandleMainMemberFlashAsync,
            useActionBoundary: false),
        Route("boss-equipment-reward", 53, IsBossEquipmentRewardPage,
            HandleBossEquipmentRewardAsync),
        Route("equipment", 54, IsEquipmentPage, HandleEquipmentAsync,
            useActionBoundary: false,
            allowFallbackWhenUnhandled: false),
        Route("card-assign", 55,
            page => HasContainedTextAtPoint(page, 0.863, 0.133, "请选择要接受卡牌的主战员"),
            HandleCardAssignAsync),
        Route("convert", 90,
            page => HasTextAtPoint(page, 0.945, 0.918, "转换"),
            HandleConvertAsync),
        Route("shop", 100, IsShopPage, HandleShopAsync),
        Route("rest", 110, IsRestOrMeditationPage, HandleRestAsync),
        Route("close-button", 120,
            page => HasTextAtPoint(page, 0.512, 0.929, "关闭"),
            HandleCloseButtonAsync),
        Route("remove", 125,
            page => HasTextAtPoint(page, 0.945, 0.918, "移除"),
            HandleRemoveAsync,
            allowFallbackWhenUnhandled: true),
        Route("three-choice-remove", 140, IsThreeChoiceRemovePage, HandleThreeChoiceCardRemoveAsync,
            allowFallbackWhenUnhandled: true),
        Route("flash", 150,
            page => HasExactGameTextAtPoint(page, 0.945, 0.918, "闪光"),
            HandleFlashAsync),
        Route("reflash", 160,
            page => HasTextAtPoint(page, 0.945, 0.918, "重新闪光"),
            HandleReflashAsync),
        Route("grant-flash", 170,
            page => HasExactTextAtPoint(page, 0.945, 0.918, "赋予闪光"),
            HandleGrantFlashAsync),
        Route("copy", 180,
            page => HasExactTextAtPoint(page, 0.945, 0.918, "复制"),
            HandleCopyAsync),
        Route("leave", 190,
            page => HasTextAtPoint(page, 0.945, 0.918, "离开"),
            HandleLeaveAsync),
        Route("mental-breakdown", 200,
            page => HasContainedTextAtPoint(page, 0.496, 0.186, "精神崩溃发生"),
            HandleMentalBreakdownAsync),
        Route("expedition-result", 210, IsExpeditionResultPage, HandleExpeditionResultAsync),
        Route("next-step", 220, IsNextStepPage, HandleNextStepAsync),
        Route("craft", 230,
            page => HasExactTextAtPoint(page, 0.938, 0.903, "合成"),
            HandleCraftAsync),
        Route("select", 240,
            page => HasExactTextAtPoint(page, 0.945, 0.918, "选择"),
            HandleSelectAsync),
        Route("go-to-chaos-core", 250,
            page => HasTextAtPoint(page, 0.945, 0.918, "前往卡厄思核心"),
            HandleGoToChaosCoreAsync),
        Route("equipment-recast", 260,
            page => HasContainedTextAtPoint(page, 0.501, 0.128, "装备重铸"),
            HandleEquipmentRecastAsync),
        Route("minimizemap", 270,
            page => FindFeatureOne(page, "minimizemap") is not null,
            HandleMinimizemapAsync),
        Route("weakness-info", 280,
            page => HasContainedTextAtPoint(page, 0.387, 0.107, "弱点"),
            HandleWeaknessInfoAsync),
        Route("non-battle", 285, IsNonBattlePage, HandleNonBattlePageAsync),
        Route("battle-crash", 290, IsBattleCrashPage, HandleBattleCrashAsync),
        Route("battle-auto-check", 295, IsBattleAutoCheckPage, HandleBattleAutoCheckAsync,
            useActionBoundary: false),
        Route("settlement", 300,
            page => HasTextAtPoint(page, 0.941, 0.917, "结算"),
            HandleSettlementAsync),
        Route("destiny-choice", 310,
            page => HasContainedTextAtPoint(page, 0.499, 0.932, "请选择你的命运"),
            HandleDestinyChoiceAsync),
        Route("card-reward", 320, IsCardRewardPage, HandleCardRewardAsync),
        Route("select-card", 325, IsSelectCardPage, HandleSelectCardAsync),
        Route("copy-card-choice", 330,
            page => HasContainedTextAtPoint(page, 0.498, 0.133, "请选择要复制的卡牌"),
            HandleCopyCardChoiceAsync),
        Route("copy-member", 53,
            IsCopyMemberPage,
            HandleCopyMemberAsync,
            useActionBoundary: false),
        Route("convert-card", 350,
            page => HasContainedTextAtPoint(page, 0.226, 0.046, "转换的卡牌"),
            HandleConvertCardAsync),
        Route("discovery-select", 355, IsDiscoverySelectPage, HandleDiscoverySelectAsync),
        Route("negotiation", 360,
            page => HasTextAtPoint(page, 0.498, 0.683, "失败"),
            HandleNegotiationAsync),
        Route("chaos-reward-settlement", 365,
            page => HasContainedTextAtPoint(page, 0.552, 0.067, "结算"),
            HandleChaosRewardSettlementAsync),
        Route("chaos-reward-claim", 366,
            page => HasContainedTextAtPoint(page, 0.568, 0.711, "获得"),
            HandleChaosRewardClaimAsync),
        Route("continue", 370,
            page => HasTextInRegion(page, new RelativeRegion(0.459, 0.858, 0.992, 0.988), "继续"),
            HandleContinueAsync),
        Route("enter", 380,
            page => HasTextInRegion(page, new RelativeRegion(0.017, 0.771, 0.996, 0.992), "进入"),
            HandleEnterAsync),
        Route("obtain-reward", 390,
            page => HasTextAtPoint(page, 0.924, 0.922, "获得"),
            HandleObtainRewardAsync,
            actionRetryAfterFrames: GetActionRetryAfterFrames),
        Route("flash-effect", 364, IsFlashEffectPage, HandleFlashEffectAsync, allowFallbackWhenUnhandled: true),
        Route("treating", 410,
            page => FindTextAnywhere(page, GameText(page, "选择哪种方法进行治疗")) is not null,
            HandleTreatingAsync),
        Route("treat-approve", 420,
            page => FindTextAnywhere(page, GameText(page, "点击批准")) is not null,
            HandleTreatApproveAsync),
        Route("zero-system-home", 430, IsZeroSystemHomePage, HandleZeroSystemHomeAsync),
        Route("codex-search", 440,
            page => HasTextAtPoint(page, 0.5, 0.438, "法典"),
            HandleCodexSearchAsync),
        Route("expedition-unlock", 450,
            page => HasContainedTextAtPoint(page, 0.5, 0.151, "解锁的探险记录"),
            HandleExpeditionUnlockAsync),
        Route("memory-elimination", 460,
            page => HasContainedTextAtPoint(page, 0.589, 0.703, "记忆消除"),
            HandleMemoryEliminationAsync),
        Route("chaos-craft", 470, IsChaosCraftPage, HandleChaosCraftAsync),
        Route("conquer-difficulty", 480,
            page => HasContainedTextAtPoint(page, 0.502, 0.572, "征服新难度"),
            HandleConquerDifficultyAsync),
        Route("skip", 490,
            page => HasTextAtPoint(page, 0.941, 0.917, "跳过"),
            HandleSkipAsync,
            expectsCenterConfirmation: true),
        Route("held-cards", 500,
            page => HasTextAtPoint(page, 0.5, 0.056, "持有卡牌"),
            HandleHeldCardsPageAsync),
        Route("escape", 510, ShouldHandleEscapePage, HandleEscapeAsync),
        Route("stage-end-data-details", 520, IsStageEndDataDetailsPage, HandleStageEndDataDetailsAsync)
    ];

    private static FrameRoute<ChaosModePage> Route(
        string id,
        int priority,
        Func<ChaosModePage, bool> matches,
        Func<ChaosModePage, CancellationToken, ValueTask<bool>> handler,
        bool useActionBoundary = true,
        // 对应 Python PAGE_HANDLERS：处理器返回 False 后继续尝试后续页面处理器。
        bool allowFallbackWhenUnhandled = true,
        bool expectsCenterConfirmation = false,
        Func<ChaosModePage, int>? actionRetryAfterFrames = null,
        bool isPriorityOverlay = false)
        => new(
            id,
            priority,
            matches,
            handler,
            useActionBoundary,
            allowFallbackWhenUnhandled,
            expectsCenterConfirmation,
            actionRetryAfterFrames,
            isPriorityOverlay);

    private static int GetActionRetryAfterFrames(ChaosModePage page)
        => Math.Clamp(
            page.Context.GetTaskConfig(
                ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey,
                2),
            1,
            10);

    private static int GetActionRecoveryWaitFrames(ChaosModePage page)
        => Math.Clamp(
            page.Context.GetTaskConfig(
                ChaosModeConfigSchema.ActionRecoveryWaitFramesKey,
                5),
            2,
            20);

    private static bool HasExactTextAtPoint(
        ChaosModePage page,
        double x,
        double y,
        string expected)
    {
        var line = FindBoxAtPoint(page, x, y);
        return line is not null
               && string.Equals(line.Text.Trim(), expected, StringComparison.Ordinal);
    }

    private static bool HasTextAtPoint(
        ChaosModePage page,
        double x,
        double y,
        string expected)
        => FindBoxAtPoint(page, x, y) is { } line && MatchesAny(page, line, expected);

    private static bool HasExactGameTextAtPoint(
        ChaosModePage page,
        double x,
        double y,
        string expected)
        => FindBoxAtPoint(page, x, y) is { } line
           && GameText(page, expected).Any(text => string.Equals(line.Text.Trim(), text, StringComparison.Ordinal));
    private static bool HasContainedTextAtPoint(
        ChaosModePage page,
        double x,
        double y,
        string expected)
        => FindBoxAtPoint(page, x, y) is { } line && ContainsAny(page, line, expected);

    private static bool ShouldHandleConfirmButton(ChaosModePage page)
    {
        // 主战员选择是有状态流程，选择与确认必须由同一消费者闭环处理。
        if (IsCopyMemberPage(page) || IsMainMemberFlashPage(page))
        {
            return false;
        }

        var line = FindText(
            page,
            new RelativeRegion(0.267, 0.867, 0.991, 0.979),
            GameText(page, "确认"));
        if (line is null)
        {
            return false;
        }

        return IsButtonActive(page.Frame, line)
               || !HasTextAtPoint(page, 0.499, 0.126, "装备");
    }

    private static bool HasTextInRegion(
        ChaosModePage page,
        RelativeRegion region,
        string expected)
        => FindText(page, region, GameText(page, expected)) is not null;

    private static bool IsCardRewardPage(ChaosModePage page)
    {
        var title = RegionText(page, new RelativeRegion(0.345, 0.012, 0.642, 0.141));
        return title.Contains("卡牌奖励", StringComparison.Ordinal)
               || title.Contains("获得卡牌", StringComparison.Ordinal);
    }

    private static bool IsEventTaskPage(ChaosModePage page)
    {
        var bottom = FindBoxAtPoint(page, 0.516, 0.971);
        if (bottom is not null && Regex.IsMatch(bottom.Text, @"\d+/\d+"))
        {
            return false;
        }

        return FindFeatureOne(page, "taskreward") is not null
               || RecognizeEventOptions(page).Count > 0;
    }

    private static bool IsEquipmentPage(ChaosModePage page)
    {
        var hasTitle = HasTextAtPoint(page, 0.499, 0.126, "装备");
        var hasInstallLayout = IsEquipmentInstallPage(page);
        // 装备消费者只能认领有明确装备页面证据的帧。
        // 装备页从选人到确认都由同一个消费者处理，避免旧帧确认证据跨路由误点。
        return hasTitle || hasInstallLayout;
    }

    private static bool IsBossEquipmentRewardPage(ChaosModePage page)
    {
        // The equipment reward overlay appears before the normal equipment choice page.
        // Its right-side category label is not an equipment page title.
        var hasRewardSummary = HasContainedTextAtPoint(page, 0.866, 0.208, "\u672A\u53D1\u73B0\u7684\u95EA\u5149");
        var hasEquipmentCategory = FindOcrBoxes(
                page,
                new RelativeRegion(0.820, 0.180, 0.970, 0.550))
            .Any(line => line.Text.Contains("\u88C5\u5907", StringComparison.Ordinal));
        var hasClaimButton = HasTextAtPoint(page, 0.932, 0.916, "\u83B7\u5F97");
        var hasSkipButton = HasTextAtPoint(page, 0.736, 0.916, "\u8DF3\u8FC7");
        var hasCenteredEquipmentTitle = HasExactTextAtPoint(page, 0.499, 0.126, "\u88C5\u5907");
        var hasInstallPrompt = HasContainedTextAtPoint(page, 0.921, 0.135, "\u8BF7\u9009\u62E9\u4E3B\u6218\u5458");

        return hasRewardSummary
               && hasEquipmentCategory
               && hasClaimButton
               && hasSkipButton
               && !hasCenteredEquipmentTitle
               && !hasInstallPrompt;
    }

    private static bool IsEquipmentInstallPage(ChaosModePage page)
    {
        if (HasContainedTextAtPoint(page, 0.921, 0.135, "请选择主战员"))
        {
            return true;
        }

        // 实机 OCR 偶尔只保留标题和装备详情，漏掉右上角提示。
        // 单件安装页的装备名位于该区域；两选一页面的候选名不在此处。
        var hasTitle = HasTextAtPoint(page, 0.499, 0.126, "\u88C5\u5907");
        var hasMemberComparisonHint = page.Recognized.Lines.Any(line =>
            line.Text.Contains("\u957F\u6309\u5DF2\u4F69\u6234\u88C5\u5907\u7684\u56FE\u6807\u53EF\u8FDB\u884C\u6BD4\u8F83", StringComparison.Ordinal)
            || line.Text.Contains("\u9577\u6309\u5DF2\u4F69\u6234\u88DD\u5099\u7684\u5716\u6A19\u53EF\u9032\u884C\u6BD4\u8F03", StringComparison.Ordinal));
        if (hasTitle && hasMemberComparisonHint)
        {
            return true;
        }

        var installNameRegion = new RelativeRegion(0.217, 0.379, 0.469, 0.436);
        return FindOcrBoxes(page, installNameRegion)
            .Any(line =>
                line.Score >= 0.5
                && ChaosEquipmentCatalog.Normalize(line.Text).Length >= 2
                && ChaosEquipmentCatalog.Default.FindSlot(line.Text) is not null);
    }

    private static bool IsEquipmentChoiceConfirmPage(ChaosModePage page)
    {
        // 普通装备和 Boss 装备都会先在二选一/选择页确认，再进入后续安装或主战员分配页。
        return HasTextAtPoint(page, 0.499, 0.126, "装备")
               && HasContainedTextAtPoint(page, 0.921, 0.135, "请选择装备")
               && FindFooterConfirmBox(page) is not null;
    }

    private static bool IsShopPage(ChaosModePage page)
    {
        var removeCardBox = FindBoxAtPoint(page, 0.729, 0.261);
        var soldOutBox = FindBoxAtPoint(page, 0.727, 0.286);
        return (removeCardBox is not null && ContainsAny(page, removeCardBox, "移除卡牌"))
               || (soldOutBox is not null && ContainsAny(page, soldOutBox, "售"));
    }

    private static bool IsRestOrMeditationPage(ChaosModePage page)
    {
        var restRegion = new RelativeRegion(0.157, 0.503, 0.467, 0.863);
        var freeRegion = new RelativeRegion(0.154, 0.602, 0.359, 0.847);
        var meditationRegion = new RelativeRegion(0.671, 0.433, 0.945, 0.801);
        var shopBox = FindBoxAtPoint(page, 0.360, 0.138);
        return FindFeatureOne(page, "rest", restRegion) is not null
               || FindOcrBoxes(page, freeRegion).Any(line =>
                   line.Text.Contains("免费", StringComparison.Ordinal)
                   || line.Text.Contains("免費", StringComparison.Ordinal))
               || FindFeatureOne(page, "meditate", meditationRegion) is not null
               || (shopBox is not null
                   && ContainsAny(page, shopBox, "德朗商店")
                   && page.State.Shop);
    }

    private static bool ShouldHandleEscapePage(ChaosModePage page)
    {
        var hasEscapeButton = HasTextAtPoint(page, 0.952, 0.928, "逃脱")
                              || HasTextAtPoint(page, 0.952, 0.928, "脱逃");
        if (!hasEscapeButton)
        {
            return false;
        }

        // 普通地图也常驻右下角“逃脱”。没有待处理指令时，只有确认弹窗
        // （具备“取消”按钮）才允许进入该消费者。
        return page.Commands.HasPendingEscape
               || FindTextAnywhere(page, GameText(page, "取消")) is not null;
    }

    private static bool IsThreeChoiceRemovePage(ChaosModePage page)
        => FindOcrBoxes(page, new RelativeRegion(0.507, 0.889, 0.740, 0.967))
            .Any(line => ContainsAny(page, line, "移除"));

    private static bool IsMainMemberFlashPage(ChaosModePage page)
    {
        var prompt = FindBoxAtPoint(page, 0.495, 0.936);
        return prompt is not null && ContainsAny(page, prompt, "请选择获得");
    }

    private static bool IsCopyMemberPage(ChaosModePage page)
        => HasContainedTextAtPoint(page, 0.502, 0.932, "选择要复制卡牌的主战员");

    private static bool IsNextStepPage(ChaosModePage page)
    {
        var region = new RelativeRegion(0.833, 0.885, 0.954, 0.957);
        return page.Recognized.Lines.Any(candidate =>
            region.Contains(candidate, page.Frame.Width, page.Frame.Height)
            && EditDistanceWithin(candidate.Text, "下一步", 2));
    }

    private static bool IsBattleCrashPage(ChaosModePage page)
        => page.Recognized.Lines.Any(line =>
            line.Text.Contains("出现错乱", StringComparison.Ordinal)
            || line.Text.Contains("点击重试", StringComparison.Ordinal)
            || Regex.IsMatch(line.Text, "通讯不稳定.*重新尝试", RegexOptions.CultureInvariant));

    private static bool IsSelectCardPage(ChaosModePage page)
    {
        var prompt = FindBoxAtPoint(page, 0.198, 0.039);
        if (prompt is null
            || !TryParseCardSelectionPrompt(prompt.Text, out var action, out var count))
        {
            return false;
        }

        return !string.Equals(action, "移除", StringComparison.Ordinal)
               || page.State.PendingRemovedCardCount < count;
    }

    private static bool IsFlashEffectPage(ChaosModePage page)
        => FindTextAnywhere(page, GameText(page, "请决定闪光效果")) is not null
           || HasViewOriginalFlashMarker(page);

    private static bool HasViewOriginalFlashMarker(ChaosModePage page)
    {
        var first = FindBoxAtPoint(page, 0.890, 0.051);
        var second = FindBoxAtPoint(page, 0.896, 0.131);
        return ContainsViewOriginalMarker(page, first)
               || ContainsViewOriginalMarker(page, second);
    }

    private static bool ContainsViewOriginalMarker(ChaosModePage page, OcrTextLine? line)
        => line is not null
           && (ContainsAny(page, line, "查看原件")
               || ContainsAny(page, line, "查看之前的闪光"));

    private static bool IsZeroSystemHomePage(ChaosModePage page)
    {
        var title = FindBoxAtPoint(page, 0.120, 0.046);
        var codex = FindBoxAtPoint(page, 0.812, 0.469);
        return title is not null
               && ContainsAny(page, title, "零式系统")
               && codex is not null
               && MatchesAny(page, codex, "法典");
    }

    private static bool IsChaosCraftPage(ChaosModePage page)
        => HasContainedTextAtPoint(page, 0.774, 0.925, "卡厄思合成")
           || HasContainedTextAtPoint(page, 0.563, 0.922, "免费合成");

    private static bool IsStageEndDataDetailsPage(ChaosModePage page)
    {
        var region = new RelativeRegion(0.111, 0.103, 0.338, 0.243);
        var text = RegionText(page, region);
        return GameText(page, "存储数据").Any(expected =>
            text.Contains(expected, StringComparison.Ordinal));
    }
}
