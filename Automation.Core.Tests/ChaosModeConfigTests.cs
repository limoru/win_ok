using Automation.Core.Configuration;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosModeConfigTests
{
    private static readonly TaskConfigSchema Schema = new(
        "ChaosMode",
        "Test",
        string.Empty,
        [
            new TaskConfigGroupDescriptor(
                "Route",
                [
                    new TaskConfigOptionDescriptor(
                        ChaosModeConfig.FirstLayerRoutePriorityKey,
                        "First",
                        TaskConfigValueKind.StringList,
                        new[] { "事件", "小怪" }),
                    new TaskConfigOptionDescriptor(
                        ChaosModeConfig.SecondLayerRoutePriorityKey,
                        "Second",
                        TaskConfigValueKind.StringList,
                        new[] { "精英", "休息" })
                ])
        ]);

    [Fact]
    public void GetRoutePriorityUsesLayerSpecificDefault()
    {
        var config = new ChaosModeConfig(new TaskConfigReader(Schema));

        Assert.Equal(["事件", "小怪"], config.GetRoutePriority(0));
        Assert.Equal(["精英", "休息"], config.GetRoutePriority(2));
    }

    [Fact]
    public void GetRoutePrioritySkipsEmptyLayerValueAndUsesOtherLayer()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object>
            {
                [ChaosModeConfig.FirstLayerRoutePriorityKey] = Array.Empty<string>(),
                [ChaosModeConfig.SecondLayerRoutePriorityKey] = new[] { "小怪" }
            });
        var config = new ChaosModeConfig(reader);

        Assert.Equal(["小怪"], config.GetRoutePriority(0));
    }

    [Fact]
    public void GetRoutePriorityNormalizesLegacyAliases()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object>
            {
                [ChaosModeConfig.FirstLayerRoutePriorityKey] = Array.Empty<string>(),
                [ChaosModeConfig.SecondLayerRoutePriorityKey] = Array.Empty<string>(),
                [ChaosModeConfig.LegacyRoutePriorityKey] = new[] { "boss", "shop", "事件" }
            });
        var config = new ChaosModeConfig(reader);

        Assert.Equal(["精英", "商店", "事件"], config.GetRoutePriority(1));
    }

    [Fact]
    public void GetCardListReadsRuntimeArray()
    {
        var reader = new TaskConfigReader(
            Schema,
            new Dictionary<string, object> { ["cards"] = new[] { "剑雨", "展开极光" } });
        var config = new ChaosModeConfig(reader);

        Assert.Equal(["剑雨", "展开极光"], config.GetCardList("cards"));
    }

    [Fact]
    public void RecognitionFallbackWaitRoundsIsAvailableInCommonSettings()
    {
        var option = ChaosModeConfigSchema.Instance.Options.Single(option =>
            option.Key == ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey);

        Assert.Equal(TaskConfigValueKind.Integer, option.Kind);
        Assert.Equal(2, option.DefaultValue);
        Assert.Equal(1, option.Minimum);
        Assert.Equal(10, option.Maximum);
    }

    [Fact]
    public void FrameDispatchErrorAlertThresholdUsesSupportedRange()
    {
        var option = ChaosModeConfigSchema.Instance.Options.Single(option =>
            option.Key == ChaosModeConfigSchema.FrameDispatchErrorAlertThresholdKey);

        Assert.Equal(TaskConfigValueKind.Integer, option.Kind);
        Assert.Equal(3, option.DefaultValue);
        Assert.Equal(3, option.Minimum);
        Assert.Equal(5, option.Maximum);
    }

    [Fact]
    public void ActionRecoveryWaitFramesUsesSupportedRange()
    {
        var option = ChaosModeConfigSchema.Instance.Options.Single(option =>
            option.Key == ChaosModeConfigSchema.ActionRecoveryWaitFramesKey);

        Assert.Equal(TaskConfigValueKind.Integer, option.Kind);
        Assert.Equal(5, option.DefaultValue);
        Assert.Equal(2, option.Minimum);
        Assert.Equal(20, option.Maximum);
    }

    [Fact]
    public void SnowboundPromiseSeasonExposesOnlyItsMemoryPriorityFields()
    {
        var season = ChaosModeConfigSchema.Instance.Options.Single(option =>
            option.Key == ChaosModeConfigSchema.SeasonSelectorKey);
        Assert.Contains(ChaosModeConfigSchema.SnowboundPromiseSeason, season.Choices!);

        var group = ChaosModeConfigSchema.Instance.Groups.Single(group =>
            group.DisplayName == ChaosModeConfigSchema.SnowboundPromiseGroupName);
        Assert.Equal(
            [
                ChaosModeConfigSchema.SnowMemoryPriorityKey,
                ChaosModeConfigSchema.SnowPrecisionMemoryPriorityKey
            ],
            group.Options.Select(option => option.Key));
        Assert.All(group.Options, option =>
        {
            Assert.Equal(TaskConfigValueKind.StringList, option.Kind);
            Assert.Empty((IReadOnlyList<string>)option.DefaultValue);
        });
    }

}
