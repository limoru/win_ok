namespace Kes.Tasks;

public sealed record ChaosMapSpecialFeature(string FeatureName, int Priority);

public sealed record ChaosMapNode(
    int Id,
    string Type,
    int Row,
    double X,
    double Y,
    IReadOnlyList<ChaosMapSpecialFeature> SpecialFeatures);

public sealed record ChaosMapInfo(
    IReadOnlyList<ChaosMapNode> Nodes,
    IReadOnlyDictionary<int, IReadOnlyList<int>> Adjacency)
{
    public static ChaosMapInfo Empty { get; } = new(
        [],
        new Dictionary<int, IReadOnlyList<int>>());
}

public sealed record ChaosMapRoutePlan(
    IReadOnlyList<string> Priority,
    IReadOnlyDictionary<string, int> PriorityWeights,
    int ShopBonus,
    int WeightedScore,
    int ShopCount,
    IReadOnlyDictionary<string, int> TypeCounts,
    int SpecialPriorityScore,
    IReadOnlyList<int> Route,
    int? NextNodeId,
    int? NextRow,
    string? NextNodeType,
    IReadOnlyList<string> NextSpecialFeatures);

/// <summary>
/// Python find_best_map_route_by_priority 的等价实现。输入小地图节点和邻接关系，
/// 输出当前应进入的下一列节点。
/// </summary>
public static class ChaosMapRoutePlanner
{
    private const string ShopType = "商店";
    private const string ShopFeature = "shop_in_map";

    public static ChaosMapRoutePlan? FindBestRouteByPriority(
        ChaosMapInfo mapInfo,
        IReadOnlyList<string> nodeTypePriority)
    {
        ArgumentNullException.ThrowIfNull(mapInfo);
        ArgumentNullException.ThrowIfNull(nodeTypePriority);

        var nodeById = mapInfo.Nodes.ToDictionary(node => node.Id);
        var currentNode = mapInfo.Nodes.FirstOrDefault(node => node.Type == "当前位置");
        if (currentNode is null)
        {
            return null;
        }

        var priorityWeights = nodeTypePriority
            .Select((type, index) => new { type, weight = nodeTypePriority.Count - index })
            .ToDictionary(item => item.type, item => item.weight, StringComparer.Ordinal);
        var highestPriorityWeight = priorityWeights.Count > 0 ? priorityWeights.Values.Max() : 1;
        var shopBonus = highestPriorityWeight * 2;
        var shopInPriority = nodeTypePriority.Contains(ShopType, StringComparer.Ordinal);
        var routeCache = new Dictionary<int, RouteScore>();

        RouteScore BestRouteFrom(int nodeId)
        {
            if (routeCache.TryGetValue(nodeId, out var cached))
            {
                return cached;
            }

            var node = nodeById[nodeId];
            var hasShop = node.SpecialFeatures.Any(feature => feature.FeatureName == ShopFeature);
            var scoringType = shopInPriority && hasShop ? ShopType : node.Type;
            var ownCounts = nodeTypePriority
                .Select(nodeType => string.Equals(scoringType, nodeType, StringComparison.Ordinal) ? 1 : 0)
                .ToArray();
            var ownSpecialScore = node.SpecialFeatures
                .Where(feature => feature.FeatureName != ShopFeature)
                .Sum(feature => feature.Priority);
            var ownWeightedScore = priorityWeights.GetValueOrDefault(scoringType) + ownSpecialScore;
            if (hasShop && !shopInPriority)
            {
                ownWeightedScore += shopBonus;
            }

            var ownShopCount = hasShop && !shopInPriority ? 1 : 0;
            if (!mapInfo.Adjacency.TryGetValue(nodeId, out var nextNodeIds) || nextNodeIds.Count == 0)
            {
                var leaf = new RouteScore(
                    ownWeightedScore,
                    ownShopCount,
                    ownCounts,
                    ownSpecialScore,
                    [nodeId]);
                routeCache[nodeId] = leaf;
                return leaf;
            }

            var candidates = new List<RouteCandidate>(nextNodeIds.Count);
            foreach (var nextNodeId in nextNodeIds)
            {
                var child = BestRouteFrom(nextNodeId);
                var totalCounts = ownCounts
                    .Zip(child.TypeCounts, (own, descendant) => own + descendant)
                    .ToArray();
                candidates.Add(new RouteCandidate(
                    new RouteScore(
                        ownWeightedScore + child.WeightedScore,
                        ownShopCount + child.ShopCount,
                        totalCounts,
                        ownSpecialScore + child.SpecialScore,
                        [nodeId, .. child.Route]),
                    nodeById[nextNodeId].Row));
            }

            var best = candidates[0];
            for (var index = 1; index < candidates.Count; index++)
            {
                if (IsBetter(candidates[index], best))
                {
                    best = candidates[index];
                }
            }

            routeCache[nodeId] = best.Score;
            return best.Score;
        }

        var result = BestRouteFrom(currentNode.Id);
        var nextNode = result.Route.Count > 1 ? nodeById[result.Route[1]] : null;
        return new ChaosMapRoutePlan(
            nodeTypePriority.ToArray(),
            priorityWeights,
            shopBonus,
            result.WeightedScore,
            result.ShopCount,
            nodeTypePriority
                .Select((type, index) => new KeyValuePair<string, int>(type, result.TypeCounts[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            result.SpecialScore,
            result.Route,
            nextNode?.Id,
            nextNode?.Row,
            nextNode?.Type,
            nextNode?.SpecialFeatures.Select(feature => feature.FeatureName).ToArray() ?? []);
    }

    private static bool IsBetter(RouteCandidate candidate, RouteCandidate current)
    {
        if (candidate.Score.WeightedScore != current.Score.WeightedScore)
        {
            return candidate.Score.WeightedScore > current.Score.WeightedScore;
        }

        if (candidate.Score.ShopCount != current.Score.ShopCount)
        {
            return candidate.Score.ShopCount > current.Score.ShopCount;
        }

        for (var index = 0; index < candidate.Score.TypeCounts.Count; index++)
        {
            if (candidate.Score.TypeCounts[index] != current.Score.TypeCounts[index])
            {
                return candidate.Score.TypeCounts[index] > current.Score.TypeCounts[index];
            }
        }

        if (candidate.Score.SpecialScore != current.Score.SpecialScore)
        {
            return candidate.Score.SpecialScore > current.Score.SpecialScore;
        }

        return candidate.NextRow < current.NextRow;
    }

    private sealed record RouteScore(
        int WeightedScore,
        int ShopCount,
        IReadOnlyList<int> TypeCounts,
        int SpecialScore,
        IReadOnlyList<int> Route);

    private sealed record RouteCandidate(RouteScore Score, int NextRow);
}