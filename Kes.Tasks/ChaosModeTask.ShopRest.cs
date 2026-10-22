using System.Text.RegularExpressions;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks.Configuration;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private const string CardRewardPriorityKey = "卡牌奖励优先级";
    private const string DesireCardPriorityKey = "欲望卡牌优先级";
    private static readonly TimeSpan OwnedFooterConfirmationRetryInterval =
        TimeSpan.FromMilliseconds(2500);

    // Python handle_card_reward (utils.py:2193)：卡牌奖励页面按优先级选牌，未命中时刷新或跳过。
    private static async ValueTask<bool> HandleCardRewardAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var pageTitle = RegionText(page, new RelativeRegion(0.345, 0.012, 0.642, 0.141));
        var isGetCardPage = pageTitle.Contains("获得卡牌", StringComparison.Ordinal);
        if (!pageTitle.Contains("卡牌奖励", StringComparison.Ordinal) && !isGetCardPage)
        {
            return false;
        }

        page.Context.LogInfo(isGetCardPage ? "检测到获得卡牌页面" : "检测到卡牌奖励页面", "activity");
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.CardRewardReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var cards = page.Classification.Cards;
        if (cards.Count == 0)
        {
            if (isGetCardPage)
            {
                return await CloseGetCardPageAsync(
                        page,
                        "获得卡牌页面未识别到卡牌，点击关闭",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            page.Context.LogInfo("卡牌奖励页面未识别到卡牌，等待重新识别并记录诊断。", "warning");
            return await SkipCardRewardAsync(page, cancellationToken, "卡牌奖励未识别到卡牌，点击跳过")
                .ConfigureAwait(false);
        }
        var priority = GetCardRewardPriority(page);
        ChaosCardInfo? chosenCard = null;
        string? matchedPriority = null;
        foreach (var card in cards)
        {
            matchedPriority = ChaosShopLogic.MatchCardPriority(card.Name, priority);
            if (matchedPriority is null)
            {
                continue;
            }

            chosenCard = card;
            break;
        }

        if (chosenCard is not null)
        {
            page.Context.LogInfo(
                $"卡牌奖励页面命中优先级「{matchedPriority}」，选择「{chosenCard.Name}」",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(chosenCard.X, chosenCard.Y),
                    "选择卡牌奖励",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (isGetCardPage)
        {
            return await CloseGetCardPageAsync(
                    page,
                    "获得卡牌未命中优先级，点击关闭",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var refreshBoxes = FindOcrBoxes(page, new RelativeRegion(0.105, 0.764, 0.903, 0.851))
            .Where(line =>
            {
                var match = Regex.Match(line.Text.Trim(), @"^(\d)\s*/\s*3$");
                return match.Success && int.TryParse(match.Groups[1].Value, out var remaining) && remaining > 0;
            })
            .ToArray();
        if (refreshBoxes.Length > 0)
        {
            foreach (var refreshBox in refreshBoxes)
            {
                await ClickTextAsync(page, refreshBox, "卡牌奖励未命中优先级，点击刷新", cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }

        return await SkipCardRewardAsync(page, cancellationToken, "卡牌奖励未命中优先级，点击跳过")
            .ConfigureAwait(false);
    }

    private static async ValueTask<bool> CloseGetCardPageAsync(
        ChaosModePage page,
        string message,
        CancellationToken cancellationToken)
    {
        var closeBox = FindTextAnywhere(page, GameText(page, "关闭"));
        if (closeBox is null)
        {
            page.Context.LogInfo("获得卡牌页面未识别到关闭按钮。", "warning");
            return false;
        }

        await ClickTextAsync(page, closeBox, message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> SkipCardRewardAsync(
        ChaosModePage page,
        CancellationToken cancellationToken,
        string message,
        string actionKey = CardRewardSkipActionKey)
    {
        var skipBox = FindOcrBoxes(page, new RelativeRegion(0.620, 0.883, 0.990, 0.983))
            .FirstOrDefault(line => ContainsAny(page, line, "跳过"));
        if (skipBox is not null)
        {
            page.State.ResetRecognitionMiss(actionKey);
            await ClickSkipTextAsync(page, skipBox, message, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await HandleRecognitionFailureAsync(
                page,
                actionKey,
                "未识别到跳过按钮",
                new RelativePoint(0.745, 0.933),
                cancellationToken)
            .ConfigureAwait(false);
        return false;
    }
    // Python handle_card_assign (utils.py:5500)：普通卡牌分配和商店购买卡牌。
    private static async ValueTask<bool> HandleCardAssignAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var title = FindBoxAtPoint(page, 0.863, 0.133);
        if (title is null || !ContainsAny(page, title, "请选择要接受卡牌的主战员"))
        {
            return false;
        }

        page.Context.LogInfo("检测到卡牌分配页面", "activity");

        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.CardAssignReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var purchaseTitleBoxes = FindOcrBoxes(page, new RelativeRegion(0.326, 0.057, 0.671, 0.210));
        var isPurchasePage = purchaseTitleBoxes.Any(line => ContainsAny(page, line, "购买卡牌"));
        OcrTextLine? cancelBox = null;
        OcrTextLine? purchaseBox = null;
        OcrTextLine? priceBox = null;
        var currentCredit = 0;
        int? cardPrice = null;
        if (isPurchasePage)
        {
            page.Context.LogInfo("检测到购买卡牌页面", "activity");
            currentCredit = GetCurrentCredit(page);
            var bottomBoxes = FindOcrBoxes(page, new RelativeRegion(0.016, 0.878, 0.995, 0.996));
            cancelBox = bottomBoxes.FirstOrDefault(line => ContainsAny(page, line, "取消"));
            purchaseBox = bottomBoxes.FirstOrDefault(line => ContainsAny(page, line, "购买"));
            priceBox = bottomBoxes.FirstOrDefault(line => Regex.IsMatch(line.Text.Trim(), @"^\d+$"));
            cardPrice = priceBox is null ? null : ParseDiscountedPrice(priceBox.Text);
            page.Context.LogInfo(
                $"购买卡牌页面: 当前信用点={currentCredit}，OCR价格=「{priceBox?.Text ?? string.Empty}」，实际价格={cardPrice?.ToString() ?? "未识别"}");
            if (cancelBox is null || purchaseBox is null)
            {
                if (cancelBox is not null)
                {
                    await ClickTextAsync(page, cancelBox, "购买卡牌页面识别失败，点击取消", cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                return false;
            }

            if (ChaosShopLogic.IsNeutralLimitReached(page.State.NeutralCardCount))
            {
                page.Context.LogInfo("本局中立牌已达到3张上限，取消购买", "activity");
                await ClickTextAsync(page, cancelBox, "取消超出上限的卡牌购买", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        IReadOnlyList<ChaosCardInfo> assignedCards = page.Context.FeatureMatcher is null
            ? Array.Empty<ChaosCardInfo>()
            : ChaosCardRecognition.Recognize(
                page.Frame,
                page.Recognized,
                page.Context.FeatureMatcher,
                new FeatureSearchRegion(0.101, 0.217, 0.291, 0.365));
        var assignedCard = assignedCards.FirstOrDefault();
        var cardName = assignedCard?.Name.Trim() ?? string.Empty;
        var cardDescription = assignedCard?.Description.Trim() ?? string.Empty;
        page.Context.LogInfo($"待分配卡牌: 名称=「{cardName}」，描述=「{cardDescription}」");

        var bottomBoxesForCard = FindOcrBoxes(page, new RelativeRegion(0.290, 0.878, 0.998, 0.997));
        var refreshBox = bottomBoxesForCard.FirstOrDefault(line => ContainsAny(page, line, "刷新"));
        var skipBox = bottomBoxesForCard.FirstOrDefault(line => ContainsAny(page, line, "跳过"));
        var refreshCount = 0;
        foreach (var line in bottomBoxesForCard)
        {
            var match = Regex.Match(line.Text, @"(\d+)\s*/\s*(\d+)");
            if (match.Success)
            {
                _ = int.TryParse(match.Groups[1].Value, out refreshCount);
                break;
            }
        }

        var rewardPriority = GetCardRewardPriority(page);
        var hasRewardPriority = rewardPriority.Any(name => !string.IsNullOrWhiteSpace(name));
        var matchedCardName = ChaosShopLogic.MatchCardPriority(cardName, rewardPriority);
        if (matchedCardName is null)
        {
            page.Context.LogInfo($"卡牌「{cardName}」未命中奖励优先级");
            if (isPurchasePage && cancelBox is not null)
            {
                page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
                await ClickTextAsync(page, cancelBox, "购买卡牌未命中优先级，点击取消", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (hasRewardPriority && refreshBox is not null && refreshCount > 0)
            {
                page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
                await ClickTextAsync(page, refreshBox, "刷新非优先级卡牌", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (skipBox is not null)
            {
                page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
                await ClickSkipTextAsync(page, skipBox, "跳过非优先级卡牌", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            await SkipCardRewardAsync(
                    page,
                    cancellationToken,
                    "跳过非优先级卡牌",
                    CardAssignmentSkipActionKey)
                .ConfigureAwait(false);
            return false;
        }
        else
        {
            page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
            page.Context.LogInfo($"卡牌「{cardName}」命中奖励优先级「{matchedCardName}」", "activity");
        }

        var memberTags = FindMemberLevelTags(page, new RelativeRegion(0.426, 0.292, 0.473, 0.783));
        if (memberTags.Count == 0)
        {
            if (isPurchasePage && cancelBox is not null)
            {
                page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
                await ClickTextAsync(page, cancelBox, "未找到可分配主战员，点击取消", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            return false;
        }

        var targetMemberIndex = FindTargetMemberIndex(
            page,
            memberTags,
            new RelativeRegion(0.484, 0.169, 0.652, 0.858),
            "target_member_tiny");
        var availableMembers = new List<(int Index, OcrTextLine Tag)>();
        for (var index = 0; index < memberTags.Count; index++)
        {
            var tag = memberTags[index];
            var centerX = (tag.Region.X + (tag.Region.Width / 2d)) / page.Frame.Width;
            var centerY = (tag.Region.Y + (tag.Region.Height / 2d)) / page.Frame.Height;
            var unavailable = FindBoxAtPoint(page, centerX + 0.0615, centerY - 0.0795);
            if (unavailable is not null && ContainsAny(page, unavailable, "无法获得"))
            {
                continue;
            }

            availableMembers.Add((index, tag));
        }

        if (availableMembers.Count == 0)
        {
            if (isPurchasePage && cancelBox is not null)
            {
                page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
                await ClickTextAsync(page, cancelBox, "所有主战员均无法获得，点击取消", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (skipBox is not null)
            {
                page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
                await ClickSkipTextAsync(page, skipBox, "所有主战员均无法获得，点击跳过", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            return false;
        }

        var chosen = availableMembers.FirstOrDefault(member => member.Index == targetMemberIndex);
        if (chosen.Tag is null)
        {
            chosen = availableMembers[0];
        }

        if (isPurchasePage && cardPrice is not null && currentCredit <= cardPrice.Value)
        {
            page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
            await ClickTextAsync(page, cancelBox!, "购买卡牌信用点不足，点击取消", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var clickY = (chosen.Tag.Region.Y + (chosen.Tag.Region.Height / 2d)) / page.Frame.Height;
        page.State.ResetRecognitionMiss(CardAssignmentSkipActionKey);
        await ClickPointAsync(
                page,
                new RelativePoint(0.756, clickY),
                "选择接受卡牌的主战员",
                cancellationToken)
            .ConfigureAwait(false);
        if (isPurchasePage)
        {
            await ClickTextAsync(page, purchaseBox!, "购买卡牌", cancellationToken).ConfigureAwait(false);
            RecordNeutralCard(page);
            return true;
        }

        RecordNeutralCard(page);
        return false;
    }

    // Python handle_shop (utils.py:4853)：普通德朗商店购买、免费刷新和删牌入口。
    private static async ValueTask<bool> HandleShopAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var removeCardBox = FindBoxAtPoint(page, 0.729, 0.261);
        var soldOutBox = FindBoxAtPoint(page, 0.727, 0.286);
        var hasRemoveCard = removeCardBox is not null && ContainsAny(page, removeCardBox, "移除卡牌");
        var soldOut = soldOutBox is not null && ContainsAny(page, soldOutBox, "售");
        if (!hasRemoveCard && !soldOut)
        {
            return false;
        }

        page.Context.LogInfo("检测到德朗商店页面", "activity");

        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.ShopReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (soldOut)
        {
            page.Context.LogInfo("德朗商店移除卡牌已售罄", "activity");
            page.State.Shop = false;
            hasRemoveCard = false;
        }

        var currentCredit = GetCurrentCredit(page);
        page.Context.LogInfo($"德朗商店挑选商品: 当前信用点={currentCredit}");
        var routeRegion = new RelativeRegion(0.019, 0.761, 0.979, 0.890);
        var creditIcons = FindFeatures(page, "credit_icon", routeRegion).OrderBy(box => box.X).ToArray();
        var cardTypeFeatures = new List<FeatureBox>();
        foreach (var featureName in new[] { "attack_in_shop", "skill_in_shop", "enhance_in_shop" })
        {
            cardTypeFeatures.AddRange(FindFeatures(
                page,
                featureName,
                new RelativeRegion(0.032, 0.669, 0.968, 0.799)));
        }

        var cardIndexes = ChaosShopLogic.BindCardTypeIndexes(
            creditIcons,
            cardTypeFeatures,
            page.Frame.Width,
            page.Frame.Height);
        var cardPriority = GetCardRewardPriority(page);
        var equipmentPriorities = new[]
        {
            page.Context.Config.GetStringList("装备1号位优先级"),
            page.Context.Config.GetStringList("装备2号位优先级"),
            page.Context.Config.GetStringList("装备3号位优先级")
        };

        for (var index = 0; index < creditIcons.Length; index++)
        {
            var icon = creditIcons[index];
            var iconX = icon.CenterX(page.Frame.Width);
            var iconY = icon.CenterY(page.Frame.Height);
            var itemName = RegionText(
                page,
                new RelativeRegion(
                    Math.Clamp(iconX - 0.013, 0, 1),
                    Math.Clamp(iconY - 0.247, 0, 1),
                    Math.Clamp(iconX + 0.120, 0, 1),
                    Math.Clamp(iconY - 0.105, 0, 1))).Trim();
            var priceBox = FindBoxAtPoint(page, iconX + 0.099, iconY - 0.001);
            var price = priceBox is null ? null : ParseDiscountedPrice(priceBox.Text);
            var isCard = cardIndexes.Contains(index);
            var itemType = isCard ? "卡牌" : "装备";
            page.Context.LogInfo($"德朗商店第{index + 1}个商品: 类型={itemType}，名称=「{itemName}」，价格={price?.ToString() ?? "未识别"}");
            if (itemName.Length == 0 || price is null || price.Value >= currentCredit)
            {
                continue;
            }

            string? matchedName;
            if (isCard)
            {
                if (ChaosShopLogic.IsNeutralLimitReached(page.State.NeutralCardCount))
                {
                    page.Context.LogInfo("本局中立牌已达到3张上限，跳过商店卡牌", "activity");
                    continue;
                }

                matchedName = ChaosShopLogic.MatchCardPriority(itemName, cardPriority);
            }
            else
            {
                matchedName = null;
                foreach (var priority in equipmentPriorities)
                {
                    var matched = ChaosEquipmentLogic.MatchEquipmentName(itemName, priority);
                    if (matched.Rank is not null)
                    {
                        matchedName = matched.Name;
                        break;
                    }
                }
            }

            if (matchedName is null)
            {
                continue;
            }

            page.Context.LogInfo(
                $"德朗商店: {itemType}「{itemName}」命中配置「{matchedName}」，价格{price}小于当前信用点{currentCredit}，点击查看",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(iconX, iconY),
                    $"点击商店商品「{itemName}」",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var refreshRegion = new RelativeRegion(0.012, 0.892, 0.258, 0.979);
        var freeRefresh = FindOcrBoxes(page, refreshRegion)
            .FirstOrDefault(line => line.Text.Contains("免费", StringComparison.Ordinal)
                                    || line.Text.Contains("免費", StringComparison.Ordinal));
        if (freeRefresh is not null)
        {
            await ClickTextAsync(page, freeRefresh, "德朗商店无可购买商品，点击免费刷新", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // Spend the free refresh before the paid card removal entry.
        if (hasRemoveCard && await TryRemoveShopCardAsync(page, removeCardBox!, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        page.State.Shop = false;
        page.Context.LogInfo("德朗商店已无可执行操作，本次商店流程结束", "activity");
        return false;
    }

    // Python handle_rest (utils.py:4364)：普通休息、冥想和德朗商店入口。
    private static async ValueTask<bool> HandleRestAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var restFeature = FindFeatureOne(page, "rest", new RelativeRegion(0.157, 0.503, 0.467, 0.863));
        var freeRestBox = FindOcrBoxes(page, new RelativeRegion(0.154, 0.602, 0.359, 0.847))
            .FirstOrDefault(line =>
                line.Text.Contains("免费", StringComparison.Ordinal)
                || line.Text.Contains("免費", StringComparison.Ordinal));
        var hasFreeRest = freeRestBox is not null;
        var restTextBox = FindOcrBoxes(page, new RelativeRegion(0.157, 0.503, 0.467, 0.863))
            .FirstOrDefault(line => ContainsAny(page, line, "休息"));
        var hasRestAction = restFeature is not null || restTextBox is not null;
        var canRest = hasRestAction
                      && hasFreeRest
                      && page.State.FlashOrRest;

        var restPoint = new RelativePoint(0.267, 0.750);
        if (restFeature is not null)
        {
            var center = RelativeCenter(restFeature, page);
            restPoint = new RelativePoint(center.X, center.Y);
        }
        else if (restTextBox is not null)
        {
            restPoint = new RelativePoint(
                (restTextBox.Region.X + (restTextBox.Region.Width / 2d)) / page.Frame.Width,
                (restTextBox.Region.Y + (restTextBox.Region.Height / 2d)) / page.Frame.Height);
        }
        else if (freeRestBox is not null)
        {
            restPoint = new RelativePoint(
                (freeRestBox.Region.X + (freeRestBox.Region.Width / 2d)) / page.Frame.Width,
                (freeRestBox.Region.Y + (freeRestBox.Region.Height / 2d)) / page.Frame.Height);
        }

        var meditationRegion = new RelativeRegion(0.671, 0.433, 0.945, 0.801);
        var meditateFeature = FindFeatureOne(page, "meditate", meditationRegion);
        var meditationCost = meditateFeature is null
            ? null
            : FindNearestNumericCost(page, meditationRegion, meditateFeature);
        EnsureMeditationState(page);
        var hasPendingMeditation = page.State.MeditationCards.Values.Any(value => value);
        var currentCredit = GetCurrentCredit(page);
        var meditationCreditThreshold = page.Context.GetTaskConfig("多少信用点以上冥想", 300);
        var canMeditate = page.State.FlashOrRest
                          && meditateFeature is not null
                          && meditationCost is not null
                          && hasPendingMeditation
                          && currentCredit > meditationCreditThreshold
                          && currentCredit > meditationCost.Value;

        var chooseRest = canRest && !canMeditate;
        if (canRest && canMeditate)
        {
            page.Context.LogInfo("休息与冥想均可用，满足冥想条件，优先冥想。", "activity");
        }

        if (restFeature is not null || meditateFeature is not null)
        {
            var snapshotActionLabel = chooseRest
                ? "点击休息"
                : canMeditate
                    ? "点击冥想"
                    : null;
            var snapshotActionX = chooseRest
                ? restPoint.X
                : canMeditate && meditateFeature is not null
                    ? meditateFeature.CenterX(page.Frame.Width)
                    : (double?)null;
            var snapshotActionY = chooseRest
                ? restPoint.Y
                : canMeditate && meditateFeature is not null
                    ? meditateFeature.CenterY(page.Frame.Height)
                    : (double?)null;
            await CaptureDecisionSnapshotAsync(
                    page,
                    DecisionSnapshotCatalog.RestReasonId,
                    snapshotActionLabel,
                    snapshotActionX,
                    snapshotActionY,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (chooseRest)
        {
            if (restFeature is null)
            {
                page.Context.LogInfo("rest特征未命中，使用“休息”文字位置兜底。", "warning");
            }

            page.ExpectCenterConfirmationAfterAction("休息");
            await ClickPointAsync(
                    page,
                    restPoint,
                    "检测到休息界面，点击休息",
                    cancellationToken)
                .ConfigureAwait(false);
            page.State.AwaitingRestConfirmation = true;
            page.State.RestConfirmationStartedAt = DateTimeOffset.Now;
            return true;
        }

        if (canMeditate)
        {
            var pendingCards = page.State.MeditationCards
                .Where(pair => pair.Value)
                .Select(pair => pair.Key)
                .ToArray();
            page.Context.LogInfo(
                $"检测到可冥想卡牌[{string.Join(", ", pendingCards)}]，当前信用点={currentCredit}，冥想费用={meditationCost}，点击冥想",
                "activity");
            var center = RelativeCenter(meditateFeature!, page);
            page.ExpectCenterConfirmationAfterAction("冥想");
            await ClickPointAsync(
                    page,
                    new RelativePoint(center.X, center.Y),
                    "点击冥想",
                    cancellationToken)
                .ConfigureAwait(false);
            page.State.AwaitingRestConfirmation = true;
            page.State.RestConfirmationStartedAt = DateTimeOffset.Now;
            return true;
        }

        var shopBox = FindBoxAtPoint(page, 0.360, 0.138);
        if (shopBox is not null && ContainsAny(page, shopBox, "德朗商店") && page.State.Shop)
        {
            await ClickTextAsync(page, shopBox, "检测到德朗商店入口，进入商店", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // Python handle_remove (utils.py:3363)：通用“移除”按钮，并记录商店删牌来源。
    private static async ValueTask<bool> HandleRemoveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.945, 0.918);
        if (line is null || !MatchesAny(page, line, "移除"))
        {
            return false;
        }

        if (!IsButtonActive(page.Frame, line))
        {
            page.Context.LogInfo("移除按钮未激活，跳过点击", "activity");
            return false;
        }

        await ClickTextAsync(page, line, "检测到移除操作，点击移除", cancellationToken)
            .ConfigureAwait(false);
        var removedCount = Math.Max(1, page.State.PendingRemovedCardCount);
        page.State.RemovedCardCount += removedCount;
        if (page.State.PendingShopRemoval)
        {
            page.State.ShopRemovedCardCount += removedCount;
        }

        page.State.PendingRemovedCardCount = 0;
        page.State.PendingShopRemoval = false;
        page.Context.LogInfo(
            $"本局移除卡牌数量增加{removedCount}，当前共{page.State.RemovedCardCount}张（其中商店删卡{page.State.ShopRemovedCardCount}张）",
            "activity");
        return true;
    }

    // Python handle_three_choice_card_remove (utils.py:3385)：三选一卡牌移除页面的低优先级兜底。
    private static async ValueTask<bool> HandleThreeChoiceCardRemoveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var removeBox = FindOcrBoxes(page, new RelativeRegion(0.507, 0.889, 0.740, 0.967))
            .FirstOrDefault(line => ContainsAny(page, line, "移除"));
        if (removeBox is null)
        {
            return false;
        }

        if (!IsButtonActive(page.Frame, removeBox))
        {
            page.Context.LogInfo("三选一卡牌移除页面的移除按钮未激活，跳过点击", "activity");
            return false;
        }

        await ClickTextAsync(page, removeBox, "检测到三选一卡牌移除页面，点击移除", cancellationToken)
            .ConfigureAwait(false);
        page.State.RemovedCardCount += 1;
        if (page.State.PendingShopRemoval)
        {
            page.State.ShopRemovedCardCount += 1;
        }

        page.State.PendingRemovedCardCount = 0;
        page.State.PendingShopRemoval = false;
        page.Context.LogInfo(
            $"本局移除卡牌数量增加1，当前共{page.State.RemovedCardCount}张（其中商店删卡{page.State.ShopRemovedCardCount}张）",
            "activity");
        return true;
    }
    // Python handle_select_card (utils.py:3177)：当前页面的删牌/复制/闪光选卡兜底。
    private static async ValueTask<bool> HandleSelectCardAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var prompt = FindBoxAtPoint(page, 0.198, 0.039);
        if (prompt is null || !TryParseCardSelectionPrompt(prompt.Text, out var action, out var count))
        {
            return false;
        }

        var configKey = action switch
        {
            "移除" => "移除卡牌列表",
            "复制" => "复制卡牌列表",
            "闪光" => "闪光卡牌列表",
            _ => string.Empty
        };
        if (configKey.Length == 0)
        {
            return false;
        }

        page.Context.LogInfo($"检测到卡牌{action}选择，需选择{count}张", "activity");
        if (action == "移除")
        {
            return await HandleRemoveCardSelectionAsync(page, count, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ChaosCardInfo> cards = page.Context.FeatureMatcher is null
            ? Array.Empty<ChaosCardInfo>()
            : ChaosCardRecognition.RecognizeDeck(page.Frame, page.Recognized, page.Context.FeatureMatcher);
        var priority = page.Context.Config.GetStringList(configKey);
        var preferBase = action == "移除" && page.Context.GetTaskConfig("优先移除基础牌", true);
        var selectedIndexes = ChaosShopLogic.SelectCardIndexes(
            cards,
            priority,
            count,
            isRemoveAction: action == "移除",
            preferBaseCards: preferBase);
        if (selectedIndexes.Count == 0)
        {
            var actionRegion = new RelativeRegion(0.495, 0.878, 0.997, 1.001);
            var cancelOrSkip = FindOcrBoxes(page, actionRegion)
                .FirstOrDefault(line => ContainsAny(page, line, "跳过") || ContainsAny(page, line, "取消"));
            if (cancelOrSkip is not null)
            {
                await ClickTextAsync(page, cancelOrSkip, $"未找到{action}卡牌，取消本次选择", cancellationToken)
                    .ConfigureAwait(false);
                page.State.PendingRemovedCardCount = 0;
                page.State.PendingShopRemoval = false;
                return true;
            }

            return false;
        }

        foreach (var index in selectedIndexes)
        {
            var card = cards[index];
            await ClickPointAsync(
                    page,
                    new RelativePoint(card.X, card.Y),
                    $"选择卡牌「{card.Name}」",
                    cancellationToken)
                .ConfigureAwait(false);
            if (action == "移除")
            {
                page.State.PendingRemovedCardCount++;
            }
        }

        return true;
    }

    private const int CardSelectionMaxScrolls = 20;
    private const int CardSelectionNoChangeLimit = 2;
    private static readonly RelativePoint CardSelectionScrollPoint = new(0.251, 0.735);

    private static async ValueTask<bool> HandleRemoveCardSelectionAsync(
        ChaosModePage page,
        int count,
        CancellationToken cancellationToken)
    {
        var state = page.State;
        if (!string.Equals(state.CardSelectionAction, "移除", StringComparison.Ordinal)
            || state.CardSelectionTargetCount != count)
        {
            state.ResetCardSelectionSearch();
            state.CardSelectionAction = "移除";
            state.CardSelectionTargetCount = count;
        }

        IReadOnlyList<ChaosCardInfo> cards = page.Context.FeatureMatcher is null
            ? Array.Empty<ChaosCardInfo>()
            : ChaosCardRecognition.RecognizeDeck(page.Frame, page.Recognized, page.Context.FeatureMatcher);
        LogCardSelectionCandidates(page, cards);
        var remaining = Math.Max(1, count - state.PendingRemovedCardCount);
        var priority = page.Context.Config.GetStringList("移除卡牌列表");
        var priorityIndexes = ChaosShopLogic.SelectPriorityCardIndexes(cards, priority, remaining);
        if (priorityIndexes.Count > 0)
        {
            await ClickSelectedCardIndexesAsync(
                    page,
                    cards,
                    priorityIndexes,
                    "命中移除优先级",
                    cancellationToken)
                .ConfigureAwait(false);
            state.CardSelectionInputVersion++;
            return true;
        }

        var fingerprint = page.Observation.Fingerprint.Value;
        var unchanged = state.CardSelectionScrollCount > 0
                        && string.Equals(
                            state.CardSelectionLastFingerprint,
                            fingerprint,
                            StringComparison.Ordinal);
        state.CardSelectionUnchangedScrollCount = unchanged
            ? state.CardSelectionUnchangedScrollCount + 1
            : 0;
        state.CardSelectionLastFingerprint = fingerprint;
        if (state.CardSelectionScrollCount < CardSelectionMaxScrolls
            && state.CardSelectionUnchangedScrollCount < CardSelectionNoChangeLimit)
        {
            state.CardSelectionScrollCount++;
            state.CardSelectionInputVersion++;
            page.Context.LogInfo(
                $"移除优先级未命中，向下滚动搜索卡牌：第{state.CardSelectionScrollCount}/{CardSelectionMaxScrolls}次。",
                "activity");
            await ScrollCardSelectionPageAsync(page, cancellationToken).ConfigureAwait(false);
            return true;
        }

        page.Context.LogInfo(
            $"移除优先级搜索结束：滚动{state.CardSelectionScrollCount}次，未命中「{string.Join("、", priority)}」，进入兜底选择。",
            "activity");
        var preferBase = page.Context.GetTaskConfig("优先移除基础牌", true);
        var fallbackIndexes = ChaosShopLogic.SelectFallbackCardIndexes(cards, remaining, preferBase);
        if (fallbackIndexes.Count > 0)
        {
            await ClickSelectedCardIndexesAsync(
                    page,
                    cards,
                    fallbackIndexes,
                    "扫描到底后执行删卡兜底",
                    cancellationToken)
                .ConfigureAwait(false);
            state.CardSelectionInputVersion++;
            return true;
        }

        var cancelOrSkip = FindCardSelectionCancelOrSkip(page);
        if (cancelOrSkip is null)
        {
            page.Context.LogInfo("移除选卡搜索结束，但没有可移除卡牌，也没有找到跳过或取消按钮。", "warning");
            return false;
        }

        await ClickTextAsync(page, cancelOrSkip, "未找到可移除卡牌，取消本次选择", cancellationToken)
            .ConfigureAwait(false);
        page.State.PendingRemovedCardCount = 0;
        page.State.PendingShopRemoval = false;
        page.State.ResetCardSelectionSearch();
        return true;
    }

    private static OcrTextLine? FindCardSelectionCancelOrSkip(ChaosModePage page)
    {
        var actionRegion = new RelativeRegion(0.495, 0.878, 0.997, 1.001);
        return FindOcrBoxes(page, actionRegion)
            .FirstOrDefault(line => ContainsAny(page, line, "跳过") || ContainsAny(page, line, "取消"));
    }

    private static void LogCardSelectionCandidates(
        ChaosModePage page,
        IReadOnlyList<ChaosCardInfo> cards)
    {
        var candidates = string.Join(
            " | ",
            cards.Select(card => $"「{card.Name}」[{card.Type}]"));
        page.Context.LogDebug($"帧 #{page.FrameSequence} 移除选卡识别候选({cards.Count})：{candidates}");
    }

    private static async ValueTask ClickSelectedCardIndexesAsync(
        ChaosModePage page,
        IReadOnlyList<ChaosCardInfo> cards,
        IReadOnlyList<int> indexes,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var index in indexes)
        {
            if (index < 0 || index >= cards.Count)
            {
                continue;
            }

            var card = cards[index];
            await ClickPointAsync(
                    page,
                    new RelativePoint(card.X, card.Y),
                    $"{reason}，选择卡牌「{card.Name}」",
                    cancellationToken)
                .ConfigureAwait(false);
            page.State.PendingRemovedCardCount++;
        }
    }

    private static async ValueTask ScrollCardSelectionPageAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        page.Context.LogInfo(
            $"滚动卡牌列表：坐标({CardSelectionScrollPoint.X:0.000}, {CardSelectionScrollPoint.Y:0.000})，滚轮=-3。",
            "activity");
        await page.Context
            .ScrollRelativeAsync(CardSelectionScrollPoint, -3, cancellationToken)
            .ConfigureAwait(false);
        page.MarkInputDispatched();
    }

    // Python handle_copy_card_choice (utils.py:3208)：复制卡牌选择页面。
    private static async ValueTask<bool> HandleCopyCardChoiceAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var prompt = FindBoxAtPoint(page, 0.498, 0.133);
        if (prompt is null
            || !ContainsAny(page, prompt, "请选择要复制的卡牌"))
        {
            return false;
        }

        page.Context.LogInfo("检测到复制卡牌选择页面", "activity");
        page.State.CopyMemberAttemptIndex = 0;
        page.State.CopyMemberCandidateClickedAt = default;
        page.State.CopyMemberConfirmClickedAt = default;
        var target = FindFeatureOne(
            page,
            "target",
            new RelativeRegion(0.090, 0.179, 0.927, 0.342));
        if (target is not null)
        {
            var targetX = Math.Clamp(target.CenterX(page.Frame.Width) - 0.0975, 0, 1);
            var targetY = Math.Clamp(target.CenterY(page.Frame.Height) + 0.2460, 0, 1);
            page.Context.LogInfo(
                $"复制卡牌选择: 检测到 target 卡牌，点击位置 ({targetX:0.000}, {targetY:0.000})",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(targetX, targetY),
                    "选择复制 target 卡牌",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var cards = page.Classification.Cards;
        var priority = page.Context.Config.GetStringList("复制卡牌列表");
        foreach (var priorityName in priority.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            foreach (var card in cards)
            {
                if (card.Name.Length == 0
                    || !card.Name.Contains(priorityName, StringComparison.Ordinal))
                {
                    continue;
                }

                page.Context.LogInfo(
                    $"复制卡牌选择: 按优先级选择「{card.Name}」(匹配「{priorityName}」)",
                    "activity");
                await ClickPointAsync(
                        page,
                        new RelativePoint(card.X, card.Y),
                        $"选择复制卡牌「{card.Name}」",
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        page.Context.LogInfo("复制卡牌选择: 未命中任何优先级，return False", "activity");
        return false;
    }

    // Python handle_copy_member (utils.py:3240)：按目标主战员头像优先选择复制对象。
    private static async ValueTask<bool> HandleCopyMemberAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var promptBox = FindBoxAtPoint(page, 0.502, 0.932);
        if (promptBox is null || !ContainsAny(page, promptBox, "选择要复制卡牌的主战员"))
        {
            return false;
        }

        page.Context.LogInfo("检测到卡牌复制主战员选择事件，进行相应操作", "activity");
        var clickPositions = PrioritizeTargetMemberClicks(page, out var targetMatched);
        var confirmRegion = new RelativeRegion(0.145, 0.044, 0.856, 0.214);
        var now = page.Observation.CapturedAt == default
            ? DateTimeOffset.UtcNow
            : page.Observation.CapturedAt;
        var activeConfirm = FindActiveFooterConfirmBox(page);
        if (activeConfirm is not null)
        {
            if (page.State.CopyMemberConfirmClickedAt != default
                && now - page.State.CopyMemberConfirmClickedAt < OwnedFooterConfirmationRetryInterval)
            {
                page.Context.LogDebug("复制主战员确认已下发，等待页面变化后再决定是否重试。");
                return true;
            }

            page.State.CopyMemberAttemptIndex = 0;
            page.State.CopyMemberCandidateClickedAt = default;
            page.State.CopyMemberConfirmClickedAt = now;
            await ClickFooterConfirmAsync(
                    page,
                    activeConfirm,
                    "复制主战员确认",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (page.State.CopyMemberAttemptIndex > 0
            && FindFeatureOne(page, "copymemberconfirm", confirmRegion) is not null)
        {
            page.State.CopyMemberAttemptIndex = 0;
            page.State.CopyMemberCandidateClickedAt = default;
            page.Context.LogInfo("已出现 copymemberconfirm，等待后续确认处理。", "activity");
            return true;
        }

        if (clickPositions.Count == 0)
        {
            page.State.CopyMemberAttemptIndex = 0;
            page.State.CopyMemberCandidateClickedAt = default;
            return true;
        }

        var attemptIndex = page.State.CopyMemberAttemptIndex;
        if (attemptIndex > 0
            && page.State.CopyMemberCandidateClickedAt != default
            && now - page.State.CopyMemberCandidateClickedAt < OwnedFooterConfirmationRetryInterval)
        {
            page.Context.LogDebug("复制主战员选择已下发，等待确认特征，不切换候选人。");
            return true;
        }

        if (targetMatched && attemptIndex > 0)
        {
            attemptIndex = 0;
            page.State.CopyMemberAttemptIndex = 0;
        }

        if (attemptIndex < 0 || attemptIndex >= clickPositions.Count)
        {
            page.State.CopyMemberAttemptIndex = 0;
            page.State.CopyMemberCandidateClickedAt = default;
            page.Context.LogInfo("所有复制主战员位置均已尝试，等待页面变化。", "warning");
            return true;
        }

        var position = clickPositions[attemptIndex];
        page.State.CopyMemberAttemptIndex = attemptIndex + 1;
        page.State.CopyMemberCandidateClickedAt = now;
        page.Context.LogInfo($"点击位置 ({position.X:0.000}, {position.Y:0.000})", "activity");
        await ClickPointAsync(
                page,
                position,
                "选择要复制卡牌的主战员",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static IReadOnlyList<RelativePoint> PrioritizeTargetMemberClicks(
        ChaosModePage page,
        out bool targetMatched)
    {
        var defaults = new[]
        {
            new RelativePoint(0.228, 0.510),
            new RelativePoint(0.504, 0.504),
            new RelativePoint(0.755, 0.508)
        };
        var target = FindFeatureOne(
            page,
            "target_member_large",
            new RelativeRegion(0.173, 0.232, 0.858, 0.508),
            0.35);
        if (target is null)
        {
            targetMatched = false;
            page.Context.LogInfo(
                "刷存档主战员头像匹配跳过：未找到 target_member_large，保持原主战员点击顺序");
            return defaults;
        }

        var targetX = target.CenterX(page.Frame.Width);
        var targetY = target.CenterY(page.Frame.Height);
        var preferred = defaults
            .OrderBy(position =>
                Math.Pow(position.X - targetX, 2)
                + Math.Pow(position.Y - targetY, 2))
            .First();
        targetMatched = true;
        page.Context.LogInfo(
            $"刷存档主战员头像匹配成功，相似度={target.Confidence:0.0000}，优先绑定点击位置（{preferred.X:0.000}, {preferred.Y:0.000}）",
            "activity");
        return [preferred, .. defaults.Where(position => position != preferred)];
    }
    private static async ValueTask<bool> TryRemoveShopCardAsync(
        ChaosModePage page,
        OcrTextLine removeCardBox,
        CancellationToken cancellationToken)
    {
        var currentCredit = GetCurrentCredit(page);
        var costBox = FindBoxAtPoint(page, 0.724, 0.319);
        if (costBox is null || !int.TryParse(costBox.Text.Trim(), out var cost))
        {
            page.Context.LogInfo("德朗商店: 移除卡牌费用读取失败，继续挑选其他商品");
            page.State.Shop = false;
            return false;
        }

        if (page.State.ShopRemovedCardCount >= ChaosShopLogic.ShopRemovalLimit)
        {
            page.Context.LogInfo(
                $"本局已在商店移除{page.State.ShopRemovedCardCount}张卡牌，达到上限{ChaosShopLogic.ShopRemovalLimit}张",
                "activity");
            page.State.Shop = false;
            return false;
        }

        if (cost <= currentCredit && page.State.Shop)
        {
            page.Context.LogInfo($"德朗商店: 移除卡牌需{cost}信用点，当前{currentCredit}，点击移除", "activity");
            await ClickTextAsync(page, removeCardBox, "打开移除卡牌页面", cancellationToken)
                .ConfigureAwait(false);
            page.State.PendingShopRemoval = true;
            page.State.Shop = false;
            return true;
        }

        page.Context.LogInfo($"德朗商店: 移除卡牌需{cost}信用点，当前{currentCredit}，不足", "activity");
        page.State.Shop = false;
        return false;
    }

    private static IReadOnlyList<string> GetCardRewardPriority(ChaosModePage page)
    {
        var legacyPriority = page.Context.Config.GetStringList(CardRewardPriorityKey);
        if (legacyPriority.Count > 0)
        {
            return legacyPriority;
        }

        var season = page.Context.GetTaskConfig(
            ChaosModeConfigSchema.SeasonSelectorKey,
            ChaosModeConfigSchema.NoSeasonContent);
        if (!string.Equals(
                season,
                ChaosModeConfigSchema.BrokenLightAndClawSeason,
                StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        return page.Context.Config.GetStringList(DesireCardPriorityKey);
    }

    private static void RecordNeutralCard(ChaosModePage page)
    {
        page.State.NeutralCardCount++;
        page.Context.LogInfo($"本局获得中立牌数量增加1，当前共{page.State.NeutralCardCount}张", "activity");
    }

    private static void EnsureMeditationState(ChaosModePage page)
    {
        if (page.State.MeditationCards.Count > 0)
        {
            return;
        }

        foreach (var name in page.Context.Config.GetStringList("需要冥想的卡牌"))
        {
            var normalized = name.Trim();
            if (normalized.Length > 0)
            {
                page.State.MeditationCards.TryAdd(normalized, false);
            }
        }
    }

    private static void UpdateMeditationStateFromFlash(
        ChaosModePage page,
        IReadOnlyList<ChaosCardInfo> cards,
        bool hasChosenCard,
        bool hasTargetCard)
    {
        if (cards.Count == 0)
        {
            return;
        }

        EnsureMeditationState(page);
        var pending = !hasChosenCard && !hasTargetCard;
        foreach (var configuredName in page.State.MeditationCards.Keys.ToArray())
        {
            var matched = cards.Any(card =>
                card.Name.Length > 0
                && (card.Name.Contains(configuredName, StringComparison.Ordinal)
                    || configuredName.Contains(card.Name, StringComparison.Ordinal))
                && EditDistanceWithin(card.Name, configuredName, 1));
            if (!matched)
            {
                continue;
            }

            page.State.MeditationCards[configuredName] = pending;
            page.Context.LogInfo(
                $"冥想卡牌「{configuredName}」状态更新为{(pending ? "待冥想" : "无需冥想")}",
                "activity");
        }
    }

    private static int? GetCurrentHpPercent(ChaosModePage page)
    {
        var hpBox = FindBoxAtPoint(page, 0.209, 0.040);
        if (hpBox is null)
        {
            return null;
        }

        var match = Regex.Match(hpBox.Text, @"(\d+)/(\d+)");
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out var currentHp)
            || !int.TryParse(match.Groups[2].Value, out var maxHp)
            || maxHp <= 0)
        {
            return null;
        }

        var percent = (int)(currentHp * 100L / maxHp);
        page.Context.LogInfo($"当前生命值: {currentHp}/{maxHp} = {percent}%");
        return percent;
    }

    private static int? FindNearestNumericCost(
        ChaosModePage page,
        RelativeRegion region,
        FeatureBox anchor)
    {
        var anchorX = anchor.CenterX(page.Frame.Width);
        var anchorY = anchor.CenterY(page.Frame.Height);
        return FindOcrBoxes(page, region)
            .Where(line => int.TryParse(line.Text.Trim(), out _))
            .Select(line => new
            {
                Cost = int.Parse(line.Text.Trim()),
                Distance = Math.Pow(line.Region.X + (line.Region.Width / 2d) - anchorX, 2)
                           + Math.Pow(line.Region.Y + (line.Region.Height / 2d) - anchorY, 2)
            })
            .OrderBy(item => item.Distance)
            .Select(item => (int?)item.Cost)
            .FirstOrDefault();
    }

    private static bool TryParseCardSelectionPrompt(string text, out string action, out int count)
    {
        var match = Regex.Match(
            text,
            @"(?:请选择|請選擇)\s*(\d*)\s*(?:张|張)?.*?(移除|复制|複製|闪光|閃光|灵光|靈光).*?卡牌");
        if (!match.Success)
        {
            action = string.Empty;
            count = 1;
            return false;
        }

        count = int.TryParse(match.Groups[1].Value, out var parsedCount) && parsedCount > 0
            ? parsedCount
            : 1;
        action = match.Groups[2].Value switch
        {
            "複製" => "复制",
            "閃光" or "靈光" => "闪光",
            var value => value
        };
        return true;
    }
}
