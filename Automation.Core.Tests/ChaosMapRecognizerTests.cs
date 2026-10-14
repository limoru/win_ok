using Automation.Core.Frames;
using Automation.Core.Vision;
using Kes.Tasks;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosMapRecognizerTests
{
    [Fact]
    public void RowsStartAtOneWithinEachColumn()
    {
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["position_in_map"] = [Box("position_in_map", 8, 88)],
                ["enemy_in_map"] =
                [
                    Box("enemy_in_map", 28, 20),
                    Box("enemy_in_map", 28, 70)
                ],
                ["event_in_map"] = [Box("event_in_map", 68, 45)]
            });

        var result = ChaosMapRecognizer.Recognize(
            new BgraFrame(100, 100, new byte[100 * 100 * 4]),
            matcher,
            region: FeatureSearchRegion.FullScreen);

        var enemies = result.Nodes
            .Where(node => node.Type == "小怪")
            .OrderBy(node => node.Y)
            .ToArray();
        Assert.Equal([1, 2], enemies.Select(node => node.Row));
        Assert.Equal(1, result.Nodes.Single(node => node.Type == "当前位置").Row);
        Assert.Equal(1, result.Nodes.Single(node => node.Type == "事件").Row);
    }

    private static FeatureBox Box(string featureName, int x, int y)
        => new(featureName, x, y, 4, 4, 0.99);

    private sealed class StaticFeatureMatcher(
        IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> boxes) : IFeatureMatcher
    {
        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
            => boxes.TryGetValue(featureName, out var matches)
                ? matches
                : [];
    }
}