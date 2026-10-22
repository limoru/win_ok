using Automation.Core.Vision;

namespace Kes.Tasks;

/// <summary>
/// 德朗商店和卡牌选择页的纯决策逻辑。这里不读取帧，也不执行点击。
/// </summary>
public static class ChaosShopLogic
{
    public const int ShopRemovalLimit = 5;
    public const int NeutralCardLimit = 3;

    public static IReadOnlySet<int> BindCardTypeIndexes(
        IReadOnlyList<FeatureBox> creditIcons,
        IReadOnlyList<FeatureBox> cardTypeFeatures,
        int frameWidth,
        int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(creditIcons);
        ArgumentNullException.ThrowIfNull(cardTypeFeatures);
        if (creditIcons.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return new HashSet<int>();
        }

        var indexes = new HashSet<int>();
        foreach (var feature in cardTypeFeatures)
        {
            var featureX = feature.CenterX(frameWidth);
            var featureY = feature.CenterY(frameHeight);
            var closestIndex = Enumerable.Range(0, creditIcons.Count)
                .OrderBy(index => Math.Pow(creditIcons[index].CenterX(frameWidth) - featureX, 2)
                                  + Math.Pow(creditIcons[index].CenterY(frameHeight) - featureY, 2))
                .First();
            indexes.Add(closestIndex);
        }

        return indexes;
    }

    public static string? MatchCardPriority(
        string? itemName,
        IReadOnlyList<string> cardPriority)
    {
        ArgumentNullException.ThrowIfNull(cardPriority);
        var candidate = itemName?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return null;
        }

        return cardPriority.FirstOrDefault(
            name => !string.IsNullOrWhiteSpace(name)
                    && (candidate.Contains(name, StringComparison.Ordinal)
                        || name.Contains(candidate, StringComparison.Ordinal)));
    }

    public static bool IsNeutralLimitReached(int neutralCardCount)
        => neutralCardCount >= NeutralCardLimit;

    public static IReadOnlyList<int> SelectCardIndexes(
        IReadOnlyList<ChaosCardInfo> cards,
        IReadOnlyList<string> priority,
        int count,
        bool isRemoveAction,
        bool preferBaseCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(priority);
        if (cards.Count == 0 || count <= 0)
        {
            return Array.Empty<int>();
        }

        var targetCount = Math.Min(count, cards.Count);
        var selected = SelectPriorityCardIndexes(cards, priority, targetCount).ToList();
        var used = selected.ToHashSet();

        if (isRemoveAction && selected.Count < targetCount)
        {
            SelectFallback(
                cards,
                selected,
                used,
                targetCount,
                card => card.Type.Contains("咒术", StringComparison.Ordinal)
                        || card.Type.Contains("诅咒", StringComparison.Ordinal));
        }

        if (isRemoveAction && preferBaseCards && selected.Count < targetCount)
        {
            SelectFallback(
                cards,
                selected,
                used,
                targetCount,
                card => card.Type.Contains("基础", StringComparison.Ordinal));
        }

        if (selected.Count < targetCount)
        {
            SelectFallback(cards, selected, used, targetCount, _ => true);
        }

        return selected;
    }

    public static IReadOnlyList<int> SelectPriorityCardIndexes(
        IReadOnlyList<ChaosCardInfo> cards,
        IReadOnlyList<string> priority,
        int count)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(priority);
        if (cards.Count == 0 || count <= 0)
        {
            return Array.Empty<int>();
        }

        var targetCount = Math.Min(count, cards.Count);
        var selected = new List<int>(targetCount);
        var used = new HashSet<int>();
        foreach (var target in priority.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            for (var index = 0; index < cards.Count && selected.Count < targetCount; index++)
            {
                if (used.Contains(index))
                {
                    continue;
                }

                var cardName = cards[index].Name.Trim();
                if (cardName.Length == 0
                    || (!cardName.Contains(target, StringComparison.Ordinal)
                        && !target.Contains(cardName, StringComparison.Ordinal)))
                {
                    continue;
                }

                selected.Add(index);
                used.Add(index);
            }
        }

        return selected;
    }

    public static IReadOnlyList<int> SelectFallbackCardIndexes(
        IReadOnlyList<ChaosCardInfo> cards,
        int count,
        bool preferBaseCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0 || count <= 0)
        {
            return Array.Empty<int>();
        }

        var targetCount = Math.Min(count, cards.Count);
        var selected = new List<int>(targetCount);
        var used = new HashSet<int>();
        SelectFallback(
            cards,
            selected,
            used,
            targetCount,
            card => card.Type.Contains("咒术", StringComparison.Ordinal)
                    || card.Type.Contains("诅咒", StringComparison.Ordinal));
        if (preferBaseCards && selected.Count < targetCount)
        {
            SelectFallback(
                cards,
                selected,
                used,
                targetCount,
                card => card.Type.Contains("基础", StringComparison.Ordinal));
        }

        if (selected.Count < targetCount)
        {
            SelectFallback(cards, selected, used, targetCount, _ => true);
        }

        return selected;
    }

    private static void SelectFallback(
        IReadOnlyList<ChaosCardInfo> cards,
        List<int> selected,
        HashSet<int> used,
        int targetCount,
        Func<ChaosCardInfo, bool> predicate)
    {
        foreach (var index in Enumerable.Range(0, cards.Count)
                     .Where(index => !used.Contains(index) && predicate(cards[index]))
                     .OrderByDescending(index => cards[index].Y)
                     .ThenByDescending(index => cards[index].X))
        {
            selected.Add(index);
            used.Add(index);
            if (selected.Count >= targetCount)
            {
                return;
            }
        }
    }
}