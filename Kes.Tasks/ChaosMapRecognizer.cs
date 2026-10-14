using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Kes.Tasks;

/// <summary>
/// 识别左下角小地图的节点与亮线邻接关系。
/// 本阶段只处理普通节点和地图标志，不包含任何赛季专属规则。
/// </summary>
public static class ChaosMapRecognizer
{
    private static readonly FeatureSearchRegion DefaultRegion = new(0.019, 0.633, 0.380, 0.972);

    private static readonly IReadOnlyDictionary<string, string> NodeTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["position_in_map"] = "当前位置",
            ["settlement_in_map"] = "结算",
            ["enemy_in_map"] = "小怪",
            ["safezoom_in_map"] = "休息",
            ["elite_in_map"] = "精英",
            ["event_in_map"] = "事件"
        };

    private static readonly IReadOnlyDictionary<string, int> SpecialFeaturePriorities =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["kalei_in_map"] = 1,
            ["shop_in_map"] = 1,
            ["seal_in_map"] = 1,
            ["hard_in_map"] = -1
        };

    public static ChaosMapInfo Recognize(
        BgraFrame frame,
        IFeatureMatcher featureMatcher,
        Action<string>? log = null,
        FeatureSearchRegion? region = null,
        double featureThreshold = 0.85,
        double lineThreshold = 0.30,
        double specialFeatureThreshold = 0.65)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(featureMatcher);
        var searchRegion = region ?? DefaultRegion;

        var candidates = new List<NodeCandidate>();
        foreach (var (featureName, nodeType) in NodeTypes)
        {
            foreach (var box in featureMatcher.Find(frame, featureName, searchRegion, featureThreshold))
            {
                var centerX = box.CenterX(frame.Width);
                var centerY = box.CenterY(frame.Height);
                if (featureName == "position_in_map" && frame.Height > 0)
                {
                    centerY += (box.Height / (double)frame.Height) * 0.36;
                }

                candidates.Add(new NodeCandidate(featureName, nodeType, centerX, centerY, box.Confidence));
            }
        }

        var positionCandidates = candidates
            .Where(candidate => candidate.FeatureName == "position_in_map")
            .ToArray();
        double? passedFeatureXLimit = null;
        if (positionCandidates.Length > 0)
        {
            var position = positionCandidates.OrderByDescending(candidate => candidate.Confidence).First();
            passedFeatureXLimit = position.X + 0.027;
            var originalCount = candidates.Count;
            candidates = candidates
                .Where(candidate => candidate.FeatureName == "position_in_map"
                                    || candidate.X >= passedFeatureXLimit.Value)
                .ToList();
            log?.Invoke(
                $"小地图当前位置X={position.X:0.0000}，过滤已走过节点，排除"
                + $"{originalCount - candidates.Count}个普通节点特征。");
        }

        const double dedupDistance = 0.028;
        var deduplicatedNodes = new List<NodeCandidate>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Confidence))
        {
            if (deduplicatedNodes.Any(kept => Distance(candidate, kept) < dedupDistance))
            {
                continue;
            }

            deduplicatedNodes.Add(candidate);
        }

        var columns = new List<NodeColumn>();
        foreach (var node in deduplicatedNodes.OrderBy(item => item.X))
        {
            var column = columns.FirstOrDefault(existing => Math.Abs(node.X - existing.CenterX) < 0.025);
            if (column is null)
            {
                columns.Add(new NodeColumn(node.X, [node]));
            }
            else
            {
                column.Nodes.Add(node);
                column.CenterX = column.Nodes.Average(item => item.X);
            }
        }

        var indexedNodes = new List<IndexedNode>();
        foreach (var column in columns)
        {
            var row = 1;
            foreach (var node in column.Nodes.OrderBy(item => item.Y))
            {
                indexedNodes.Add(new IndexedNode(
                    indexedNodes.Count,
                    columns.IndexOf(column),
                    row++,
                    node,
                    []));
            }
        }

        var specialFeatures = new List<SpecialFeature>();
        foreach (var (featureName, priority) in SpecialFeaturePriorities)
        {
            foreach (var box in featureMatcher.Find(frame, featureName, searchRegion, specialFeatureThreshold))
            {
                var special = new SpecialFeature(
                    featureName,
                    priority,
                    box.CenterX(frame.Width),
                    box.CenterY(frame.Height),
                    box.Confidence,
                    null);
                if (passedFeatureXLimit is null || special.X >= passedFeatureXLimit.Value)
                {
                    specialFeatures.Add(special);
                }
            }
        }

        var deduplicatedSpecials = new List<SpecialFeature>();
        foreach (var special in specialFeatures.OrderByDescending(item => item.Confidence))
        {
            if (deduplicatedSpecials.Any(kept => Distance(special, kept) < dedupDistance))
            {
                continue;
            }

            deduplicatedSpecials.Add(special);
        }

        foreach (var special in deduplicatedSpecials)
        {
            var nearest = indexedNodes
                .OrderBy(node => Distance(node.Candidate, special))
                .FirstOrDefault();
            if (nearest is null || Distance(nearest.Candidate, special) > 0.035)
            {
                continue;
            }

            special.NodeId = nearest.Id;
            nearest.SpecialFeatures.Add(new ChaosMapSpecialFeature(special.FeatureName, special.Priority));
        }

        var adjacency = indexedNodes.ToDictionary(
            node => node.Id,
            _ => (IReadOnlyList<int>)new List<int>());
        var connectionCount = 0;
        for (var firstIndex = 0; firstIndex < indexedNodes.Count; firstIndex++)
        {
            var first = indexedNodes[firstIndex];
            for (var secondIndex = firstIndex + 1; secondIndex < indexedNodes.Count; secondIndex++)
            {
                var second = indexedNodes[secondIndex];
                if (second.Column != first.Column + 1)
                {
                    continue;
                }

                var deltaX = Math.Abs(first.Candidate.X - second.Candidate.X);
                var deltaY = Math.Abs(first.Candidate.Y - second.Candidate.Y);
                var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (distance is < 0.035 or > 0.210 || deltaX > 0.125)
                {
                    continue;
                }

                if (deltaY > 0.025 && deltaX < 0.025)
                {
                    continue;
                }

                if (HasIntermediateNode(first, second, indexedNodes)
                    || HasIntermediateSpecialFeature(first, second, deduplicatedSpecials)
                    || LineBrightnessRatio(frame, first.Candidate, second.Candidate) < lineThreshold)
                {
                    continue;
                }

                ((List<int>)adjacency[first.Id]).Add(second.Id);
                connectionCount++;
            }
        }

        var nodes = indexedNodes
            .Select(node => new ChaosMapNode(
                node.Id,
                node.Candidate.NodeType,
                node.Row,
                node.Candidate.X,
                node.Candidate.Y,
                node.SpecialFeatures))
            .ToArray();
        log?.Invoke($"小地图识别到{nodes.Length}个节点、{connectionCount}条亮线连接。");
        return new ChaosMapInfo(nodes, adjacency);
    }

    private static bool HasIntermediateNode(
        IndexedNode first,
        IndexedNode second,
        IReadOnlyList<IndexedNode> nodes)
    {
        var vectorX = second.Candidate.X - first.Candidate.X;
        var vectorY = second.Candidate.Y - first.Candidate.Y;
        var lengthSquared = (vectorX * vectorX) + (vectorY * vectorY);
        if (lengthSquared <= 0)
        {
            return false;
        }

        foreach (var other in nodes)
        {
            if (other.Id == first.Id || other.Id == second.Id)
            {
                continue;
            }

            var progress = ((other.Candidate.X - first.Candidate.X) * vectorX
                            + (other.Candidate.Y - first.Candidate.Y) * vectorY) / lengthSquared;
            if (progress is <= 0.12 or >= 0.88)
            {
                continue;
            }

            var projectedX = first.Candidate.X + (vectorX * progress);
            var projectedY = first.Candidate.Y + (vectorY * progress);
            if (Math.Sqrt(
                    Math.Pow(other.Candidate.X - projectedX, 2)
                    + Math.Pow(other.Candidate.Y - projectedY, 2)) < 0.025)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasIntermediateSpecialFeature(
        IndexedNode first,
        IndexedNode second,
        IReadOnlyList<SpecialFeature> specialFeatures)
    {
        var vectorX = second.Candidate.X - first.Candidate.X;
        var vectorY = second.Candidate.Y - first.Candidate.Y;
        var lengthSquared = (vectorX * vectorX) + (vectorY * vectorY);
        if (lengthSquared <= 0)
        {
            return false;
        }

        foreach (var special in specialFeatures)
        {
            if (special.NodeId == first.Id || special.NodeId == second.Id)
            {
                continue;
            }

            var progress = ((special.X - first.Candidate.X) * vectorX
                            + (special.Y - first.Candidate.Y) * vectorY) / lengthSquared;
            if (progress is <= 0.12 or >= 0.88)
            {
                continue;
            }

            var projectedX = first.Candidate.X + (vectorX * progress);
            var projectedY = first.Candidate.Y + (vectorY * progress);
            if (Math.Sqrt(Math.Pow(special.X - projectedX, 2) + Math.Pow(special.Y - projectedY, 2)) < 0.015)
            {
                return true;
            }
        }

        return false;
    }

    private static double LineBrightnessRatio(BgraFrame frame, NodeCandidate first, NodeCandidate second)
    {
        var startX = first.X * frame.Width;
        var startY = first.Y * frame.Height;
        var endX = second.X * frame.Width;
        var endY = second.Y * frame.Height;
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var pixelDistance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (pixelDistance <= 0)
        {
            return 0;
        }

        var perpendicularX = -deltaY / pixelDistance;
        var perpendicularY = deltaX / pixelDistance;
        var sampleCount = Math.Max(8, (int)Math.Round(pixelDistance * 0.40));
        var brightSamples = 0;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var progress = 0.30 + (0.40 * sample / Math.Max(1, sampleCount - 1));
            var sampleX = startX + (deltaX * progress);
            var sampleY = startY + (deltaY * progress);
            var maximum = 0;
            var radius = Math.Max(2, (int)Math.Round(frame.Height * 0.004));
            for (var offset = -radius; offset <= radius; offset++)
            {
                var pixelX = (int)Math.Round(sampleX + (perpendicularX * offset));
                var pixelY = (int)Math.Round(sampleY + (perpendicularY * offset));
                if (pixelX < 0 || pixelX >= frame.Width || pixelY < 0 || pixelY >= frame.Height)
                {
                    continue;
                }

                maximum = Math.Max(maximum, PixelLuma(frame, pixelX, pixelY));
            }

            if (maximum >= 110)
            {
                brightSamples++;
            }
        }

        return (double)brightSamples / sampleCount;
    }

    private static int PixelLuma(BgraFrame frame, int x, int y)
    {
        var offset = (y * frame.Stride) + (x * 4);
        return (int)Math.Round(
            (0.114 * frame.Pixels[offset])
            + (0.587 * frame.Pixels[offset + 1])
            + (0.299 * frame.Pixels[offset + 2]));
    }

    private static double Distance(NodeCandidate first, NodeCandidate second)
        => Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static double Distance(NodeCandidate node, SpecialFeature special)
        => Math.Sqrt(Math.Pow(node.X - special.X, 2) + Math.Pow(node.Y - special.Y, 2));

    private static double Distance(SpecialFeature first, SpecialFeature second)
        => Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private sealed record NodeCandidate(string FeatureName, string NodeType, double X, double Y, double Confidence);

    private sealed class NodeColumn(double centerX, List<NodeCandidate> nodes)
    {
        public double CenterX { get; set; } = centerX;
        public List<NodeCandidate> Nodes { get; } = nodes;
    }

    private sealed class IndexedNode(
        int id,
        int column,
        int row,
        NodeCandidate candidate,
        List<ChaosMapSpecialFeature> specialFeatures)
    {
        public int Id { get; } = id;
        public int Column { get; } = column;
        public int Row { get; } = row;
        public NodeCandidate Candidate { get; } = candidate;
        public List<ChaosMapSpecialFeature> SpecialFeatures { get; } = specialFeatures;
    }

    private sealed class SpecialFeature(
        string featureName,
        int priority,
        double x,
        double y,
        double confidence,
        int? nodeId)
    {
        public string FeatureName { get; } = featureName;
        public int Priority { get; } = priority;
        public double X { get; } = x;
        public double Y { get; } = y;
        public double Confidence { get; } = confidence;
        public int? NodeId { get; set; } = nodeId;
    }
}
