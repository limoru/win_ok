using Kes.Tasks;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosMapRoutePlannerTests
{
    [Fact]
    public void ReturnsNullWithoutCurrentPositionNode()
    {
        var result = ChaosMapRoutePlanner.FindBestRouteByPriority(
            ChaosMapInfo.Empty,
            ["小怪", "精英"]);

        Assert.Null(result);
    }

    [Fact]
    public void ChoosesHigherPriorityNodeType()
    {
        var map = CreateBranchingMap();

        var result = ChaosMapRoutePlanner.FindBestRouteByPriority(map, ["事件", "小怪"]);

        Assert.NotNull(result);
        Assert.Equal([1, 3], result.Route);
        Assert.Equal(3, result.NextNodeId);
        Assert.Equal(1, result.NextRow);
        Assert.Equal("事件", result.NextNodeType);
    }

    [Fact]
    public void ShopGetsLegacyBonusWhenShopIsNotInPriority()
    {
        var map = CreateBranchingMap(shopOnSecondChild: true);

        var result = ChaosMapRoutePlanner.FindBestRouteByPriority(map, ["小怪", "事件"]);

        Assert.NotNull(result);
        Assert.Equal([1, 3], result.Route);
        Assert.Equal(1, result.ShopCount);
        Assert.Equal(4, result.ShopBonus);
    }

    [Fact]
    public void ShopUsesItsOwnPriorityRankWhenConfigured()
    {
        var map = CreateBranchingMap(shopOnSecondChild: true);

        var result = ChaosMapRoutePlanner.FindBestRouteByPriority(map, ["小怪", "商店", "事件"]);

        Assert.NotNull(result);
        Assert.Equal([1, 2], result.Route);
        Assert.Equal(0, result.ShopCount);
    }

    private static ChaosMapInfo CreateBranchingMap(bool shopOnSecondChild = false)
    {
        var specialFeatures = shopOnSecondChild
            ? new[] { new ChaosMapSpecialFeature("shop_in_map", 0) }
            : [];
        return new ChaosMapInfo(
            [
                new ChaosMapNode(1, "当前位置", 1, 0.10, 0.90, []),
                new ChaosMapNode(2, "小怪", 1, 0.20, 0.70, []),
                new ChaosMapNode(3, "事件", 1, 0.30, 0.70, specialFeatures)
            ],
            new Dictionary<int, IReadOnlyList<int>>
            {
                [1] = [2, 3]
            });
    }
}