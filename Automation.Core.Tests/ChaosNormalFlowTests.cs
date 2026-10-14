using Automation.Core.Configuration;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosNormalFlowTests
{
    [Fact]
    public void MapRecognizerConnectsBrightAdjacentNodes()
    {
        var frame = CreateFrame(1000, 600);
        DrawLine(frame, 210, 490, 310, 490, 255);
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["position_in_map"] = [new FeatureBox("position_in_map", 50, 500, 20, 20, 0.99)],
                ["enemy_in_map"] = [new FeatureBox("enemy_in_map", 200, 480, 20, 20, 0.98)],
                ["event_in_map"] = [new FeatureBox("event_in_map", 300, 480, 20, 20, 0.97)]
            });

        var map = ChaosMapRecognizer.Recognize(frame, matcher, lineThreshold: 0.3);

        var enemy = Assert.Single(map.Nodes, node => node.Type == "小怪");
        var eventNode = Assert.Single(map.Nodes, node => node.Type == "事件");
        Assert.Contains(eventNode.Id, map.Adjacency[enemy.Id]);
    }

    [Fact]
    public void EquipmentDecisionUsesPriorityThenQuality()
    {
        var priority = new[] { "目标武器" };
        var betterPriority = new ChaosEquipmentCandidate(
            "目标武器", "目标武器", "攻击力", "", 0, priority, 0, "普通", 0, 0);
        var samePriorityLegendary = new ChaosEquipmentCandidate(
            "其他武器", "其他武器", "攻击力", "", 0, priority, null, "传说", 0, 0);

        var byPriority = ChaosEquipmentLogic.DecideInstall("普通武器", "传说", betterPriority);
        var byQuality = ChaosEquipmentLogic.DecideInstall("普通武器", "史诗", samePriorityLegendary);

        Assert.True(byPriority.ShouldInstall);
        Assert.True(byQuality.ShouldInstall);
    }

    [Fact]
    public void EquipmentCatalogMatchesOcrNameWithoutPunctuation()
    {
        var slot = ChaosEquipmentCatalog.Default.FindSlot("W52 多巴胺注射器");

        Assert.Equal(0, slot);
    }

    [Fact]
    public async Task RouteSelectionFallsBackToVisibleNodePriority()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["position"] = [new FeatureBox("position", 600, 520, 20, 20, 0.99)],
                ["enemy"] = [new FeatureBox("enemy", 1200, 300, 20, 20, 0.95)],
                ["event"] = [new FeatureBox("event", 1200, 500, 20, 20, 0.96)]
            });
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(string.Empty, 0, []),
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfig.FirstLayerRoutePriorityKey] = new[] { "事件", "小怪" }
            },
            matcher);
        var task = new ChaosModeTask();
        task.RunState.PassFinalBossCount = 0;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.660, 0.663);
        Assert.InRange(click.Y, 0.559, 0.562);
        Assert.Equal("事件", task.RunState.NodeType);
        Assert.True(task.RunState.FlashOrRest);
    }

    [Fact]
    public async Task RouteSelectionDoesNotWaitAfterClick()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["position"] = [new FeatureBox("position", 600, 520, 20, 20, 0.99)],
                ["enemy"] = [new FeatureBox("enemy", 1200, 300, 20, 20, 0.95)]
            });
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(string.Empty, 0, []),
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfig.FirstLayerRoutePriorityKey] = new[] { "小怪" }
            },
            matcher);
        var task = new ChaosModeTask();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var handled = await task.RunOnceAsync(context, cancellation.Token);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.650, 0.663);
        Assert.InRange(click.Y, 0.300, 0.350);
    }

    [Fact]
    public async Task FlashEffectChoosesCardByFlashPriority()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["attack"] = [new FeatureBox("attack", 200, 190, 20, 20, 0.96)],
                ["skill"] = [new FeatureBox("skill", 600, 190, 20, 20, 0.97)]
            });
        var ocr = new OcrFrameResult(
            "请决定闪光效果。",
            0.99,
            [
                new OcrTextLine("请决定闪光效果。", 0.99, new OcrTextRegion(1400, 30, 100, 30)),
                new OcrTextLine("攻击卡", 0.98, new OcrTextRegion(200, 160, 80, 20)),
                new OcrTextLine("攻击", 0.98, new OcrTextRegion(210, 200, 60, 20)),
                new OcrTextLine("第一张描述", 0.98, new OcrTextRegion(200, 340, 100, 20)),
                new OcrTextLine("技能卡", 0.99, new OcrTextRegion(600, 160, 80, 20)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(610, 200, 60, 20)),
                new OcrTextLine("第二张描述关键字", 0.99, new OcrTextRegion(600, 340, 120, 20))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["闪光优先级"] = new[] { "第二张描述关键字" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.445, 0.448);
        Assert.InRange(click.Y, 0.180, 0.184);
    }

    [Fact]
    public async Task FlashEffectWithoutCardsCapturesDiagnostic()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var ocr = new OcrFrameResult(
            "请决定闪光效果。 跳过",
            0.99,
            [
                new OcrTextLine("请决定闪光效果。", 0.99, new OcrTextRegion(1400, 30, 100, 30)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1480, 810, 80, 30))
            ]);
        var input = new RecordingInputSink();
        var snapshots = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            null,
            matcher,
            decisionSnapshots: snapshots);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.948, 0.952);
        Assert.InRange(click.Y, 0.914, 0.920);
        var request = Assert.Single(snapshots.Requests);
        Assert.Equal(DecisionSnapshotCatalog.FlashEffectNoCardsReasonId, request.ReasonId);
        Assert.Contains("flash-effect-cards", request.ContextLogs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShopProductMatchingClicksCreditIcon()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["credit_icon"] = [new FeatureBox("credit_icon", 170, 740, 40, 40, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "售罄",
            0.99,
            [
                new OcrTextLine("售罄", 0.99, new OcrTextRegion(1140, 240, 80, 30)),
                new OcrTextLine("蚀化臂铠", 0.99, new OcrTextRegion(230, 580, 120, 30)),
                new OcrTextLine("100", 0.99, new OcrTextRegion(322, 742, 50, 25)),
                new OcrTextLine("500", 0.99, new OcrTextRegion(1245, 35, 50, 25))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["装备1号位优先级"] = new[] { "蚀化臂铠" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.117, 0.121);
        Assert.InRange(click.Y, 0.842, 0.846);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public async Task CardAssignmentWaitsConfiguredRoundsThenBlocksFixedClickAndCapturesDiagnostic(
        int fallbackRounds,
        int waitingFrames)
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)],
                ["leveltag"] = [new FeatureBox("leveltag", 710, 300, 20, 20, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "请选择要接受卡牌的主战员",
            0.99,
            [
                new OcrTextLine("请选择要接受卡牌的主战员", 0.99, new OcrTextRegion(1340, 105, 100, 30)),
                new OcrTextLine("装备包", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25))
            ]);
        var input = new RecordingInputSink();
        var snapshots = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = fallbackRounds
            },
            matcher,
            decisionSnapshots: snapshots);
        var task = new ChaosModeTask();

        for (var index = 0; index < waitingFrames; index++)
        {
            Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
            Assert.Empty(input.Clicks);
        }

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Empty(input.Clicks);
        var request = Assert.Single(snapshots.Requests);
        Assert.Equal(DecisionSnapshotCatalog.FixedClickBlockedReasonId, request.ReasonId);
        Assert.Contains("原固定坐标", request.ContextLogs, StringComparison.Ordinal);
        Assert.Equal(0, task.RunState.NeutralCardCount);
    }

    [Fact]
    public async Task CardRewardWaitsConfiguredRoundsThenBlocksFixedClickAndCapturesDiagnostic()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var ocr = new OcrFrameResult(
            "卡牌奖励",
            0.99,
            [new OcrTextLine("卡牌奖励", 0.99, new OcrTextRegion(700, 25, 200, 30))]);
        var input = new RecordingInputSink();
        var snapshots = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 2
            },
            matcher,
            decisionSnapshots: snapshots);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Empty(input.Clicks);
        Assert.Empty(snapshots.Requests);

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Empty(input.Clicks);
        Assert.Single(snapshots.Requests);
    }
    [Fact]
    public async Task CardAssignmentPurchasesMatchingCardForFirstMember()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)],
                ["leveltag"] = [new FeatureBox("leveltag", 710, 300, 20, 20, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "请选择要接受卡牌的主战员",
            0.99,
            [
                new OcrTextLine("请选择要接受卡牌的主战员", 0.99, new OcrTextRegion(1340, 105, 100, 30)),
                new OcrTextLine("购买卡牌", 0.99, new OcrTextRegion(760, 90, 80, 30)),
                new OcrTextLine("技能卡", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25)),
                new OcrTextLine("500", 0.99, new OcrTextRegion(1245, 35, 50, 25)),
                new OcrTextLine("取消", 0.99, new OcrTextRegion(940, 815, 60, 30)),
                new OcrTextLine("100", 0.99, new OcrTextRegion(1100, 815, 50, 30)),
                new OcrTextLine("购买", 0.99, new OcrTextRegion(1260, 815, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["卡牌奖励优先级"] = new[] { "技能卡" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(2, input.Clicks.Count);
        Assert.InRange(input.Clicks[0].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[0].Y, 0.343, 0.346);
        Assert.InRange(input.Clicks[1].X, 0.805, 0.808);
        Assert.InRange(input.Clicks[1].Y, 0.920, 0.924);
        Assert.Equal(1, task.RunState.NeutralCardCount);
    }
    [Fact]
    public async Task SkipConfirmationClicksConfirmInsteadOfUnderlyingSkip()
    {
        var frame = CreateFrame();
        var ocr = new OcrFrameResult(
            "确定要跳过吗",
            0.99,
            [
                new OcrTextLine("请选择要接受卡牌的主战员。", 0.99, new OcrTextRegion(1228, 102, 319, 32)),
                new OcrTextLine("跳过后将无法获得奖励。", 0.99, new OcrTextRegion(548, 351, 476, 39)),
                new OcrTextLine("确定要跳过吗？", 0.99, new OcrTextRegion(692, 387, 193, 36)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1021, 547, 73, 42)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1152, 813, 72, 45))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            null,
            new StaticFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()));
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.659, 0.662);
        Assert.InRange(click.Y, 0.629, 0.633);
    }
    [Theory]
    [InlineData(ChaosModeConfigSchema.NoSeasonContent)]
    [InlineData(ChaosModeConfigSchema.SongInGalaxySeason)]
    public async Task CardAssignmentIgnoresDesirePriorityOutsideBrokenLightSeason(string season)
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "请选择要接受卡牌的主战员",
            0.99,
            [
                new OcrTextLine("请选择要接受卡牌的主战员", 0.99, new OcrTextRegion(1340, 105, 100, 30)),
                new OcrTextLine("装备包", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1150, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = season,
                ["欲望卡牌优先级"] = new[] { "装备包" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.736, 0.739);
        Assert.InRange(click.Y, 0.926, 0.930);
    }

    [Fact]
    public async Task CardAssignmentUsesDesirePriorityForBrokenLightSeason()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)],
                ["leveltag"] = [new FeatureBox("leveltag", 710, 300, 20, 20, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "请选择要接受卡牌的主战员",
            0.99,
            [
                new OcrTextLine("请选择要接受卡牌的主战员", 0.99, new OcrTextRegion(1340, 105, 100, 30)),
                new OcrTextLine("装备包", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1150, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.BrokenLightAndClawSeason,
                ["欲望卡牌优先级"] = new[] { "装备包" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.755, 0.757);
        Assert.InRange(click.Y, 0.343, 0.346);
    }
    [Fact]
    public async Task CardAssignmentFallsBackToConfirmAfterSelectingMember()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)],
                ["leveltag"] = [new FeatureBox("leveltag", 710, 300, 20, 20, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "请选择要接受卡牌的主战员",
            0.99,
            [
                new OcrTextLine("请选择要接受卡牌的主战员", 0.99, new OcrTextRegion(1340, 105, 100, 30)),
                new OcrTextLine("装备包", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1450, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["卡牌奖励优先级"] = new[] { "装备包" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(2, input.Clicks.Count);
        Assert.InRange(input.Clicks[0].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[0].Y, 0.343, 0.346);
        Assert.InRange(input.Clicks[1].X, 0.919, 0.926);
        Assert.InRange(input.Clicks[1].Y, 0.923, 0.937);
    }
    [Fact]
    public async Task CardRewardSkipsWhenNoConfiguredCardMatches()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "卡牌奖励",
            0.99,
            [
                new OcrTextLine("卡牌奖励", 0.99, new OcrTextRegion(700, 25, 200, 30)),
                new OcrTextLine("装备包", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1150, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.NoSeasonContent,
                ["欲望卡牌优先级"] = new[] { "梦之边境" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.736, 0.739);
        Assert.InRange(click.Y, 0.926, 0.930);
    }

    [Fact]
    public async Task CardRewardSkipsWhenRecognitionFindsNoCards()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var ocr = new OcrFrameResult(
            "卡牌奖励",
            0.99,
            [
                new OcrTextLine("卡牌奖励", 0.99, new OcrTextRegion(700, 25, 200, 30)),
                new OcrTextLine("3/3", 0.99, new OcrTextRegion(400, 690, 60, 30)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1150, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.NoSeasonContent
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.736, 0.739);
        Assert.InRange(click.Y, 0.926, 0.930);
    }
    [Fact]
    public async Task CardRewardSelectsConfiguredDesireCardForBrokenLightSeason()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 360, 230, 80, 40, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "卡牌奖励",
            0.99,
            [
                new OcrTextLine("卡牌奖励", 0.99, new OcrTextRegion(700, 25, 200, 30)),
                new OcrTextLine("装备包", 0.99, new OcrTextRegion(430, 195, 80, 25)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(450, 240, 60, 20)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(430, 390, 100, 25)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1150, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.BrokenLightAndClawSeason,
                ["欲望卡牌优先级"] = new[] { "装备包" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.313, 0.317);
        Assert.InRange(click.Y, 0.236, 0.239);
    }
    [Fact]
    public async Task TargetMemberArchivedByAvatarIsUsedForEquipmentInstall()
    {
        var matcher = new RuntimeFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["memberinfo"] = [new FeatureBox("memberinfo", 40, 50, 20, 20, 0.99)]
            });
        var task = new ChaosModeTask();
        var templateStore = new MemoryRuntimeFeatureTemplateStore();

        var mainInput = new RecordingInputSink();
        var mainContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "50/100",
                0.99,
                [new OcrTextLine("50/100", 0.99, new OcrTextRegion(300, 20, 60, 20))]),
            mainInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher);
        var openedStatistics = await task.RunOnceAsync(mainContext, CancellationToken.None);

        Assert.True(openedStatistics);
        var statisticsClick = Assert.Single(mainInput.Clicks);
        Assert.InRange(statisticsClick.X, 0.181, 0.183);
        Assert.InRange(statisticsClick.Y, 0.080, 0.082);

        var archiveInput = new RecordingInputSink();
        var archiveContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(string.Empty, 0, []),
            archiveInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher);
        var openedMemberList = await task.RunOnceAsync(archiveContext, CancellationToken.None);

        Assert.True(openedMemberList);
        Assert.Equal(2, archiveInput.Clicks.Count);
        Assert.InRange(archiveInput.Clicks[0].X, 0.030, 0.033);
        Assert.InRange(archiveInput.Clicks[0].Y, 0.065, 0.068);
        Assert.InRange(archiveInput.Clicks[1].X, 0.200, 0.202);
        Assert.InRange(archiveInput.Clicks[1].Y, 0.055, 0.057);

        var memberInput = new RecordingInputSink();
        var memberContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "海德玛丽",
                0.99,
                [
                    new OcrTextLine("菲奥娜", 0.99, new OcrTextRegion(210, 320, 90, 24)),
                    new OcrTextLine("海德玛丽", 0.99, new OcrTextRegion(650, 320, 100, 24)),
                    new OcrTextLine("琳", 0.99, new OcrTextRegion(1090, 320, 70, 24))
                ]),
            memberInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher,
            templateStore);
        var archived = await task.RunOnceAsync(memberContext, CancellationToken.None);

        Assert.True(archived);
        Assert.True(task.RunState.SaveTargetMember);
        Assert.Contains("target_member", matcher.RuntimeFeatureNames);
        Assert.Contains("target_member_small", matcher.RuntimeFeatureNames);
        Assert.Contains("target_member_in_select_card", matcher.RuntimeFeatureNames);
        Assert.Contains("target_member_tiny", matcher.RuntimeFeatureNames);
        Assert.Contains("target_member_in_mask_card", matcher.RuntimeFeatureNames);
        Assert.Contains("target_member_large", matcher.RuntimeFeatureNames);

        Assert.NotEmpty(templateStore.Load("target_member"));
        Assert.NotEmpty(templateStore.Load("target_member_small"));
        Assert.NotEmpty(templateStore.Load("target_member_in_select_card"));
        Assert.NotEmpty(templateStore.Load("target_member_tiny"));
        Assert.NotEmpty(templateStore.Load("target_member_in_mask_card"));
        Assert.NotEmpty(templateStore.Load("target_member_large"));
        var closeClick = Assert.Single(memberInput.Clicks);
        Assert.InRange(closeClick.X, 0.959, 0.961);
        Assert.InRange(closeClick.Y, 0.053, 0.055);

        var equipmentFrame = CreateFrame();
        var equipmentInput = new RecordingInputSink();
        var equipmentContext = CreateContext(
            equipmentFrame,
            new OcrFrameResult(
                "装备",
                0.99,
                [
                    new OcrTextLine("装备", 0.99, new OcrTextRegion(760, 95, 80, 30)),
                    new OcrTextLine("请选择主战员", 0.99, new OcrTextRegion(1390, 108, 170, 28)),
                    new OcrTextLine("突击护手", 0.99, new OcrTextRegion(500, 350, 120, 30)),
                    new OcrTextLine("防御力", 0.99, new OcrTextRegion(350, 405, 100, 25)),
                    new OcrTextLine("战斗开始时，反击3", 0.99, new OcrTextRegion(350, 470, 250, 30))
                ]),
            equipmentInput,
            new Dictionary<string, object>
            {
                ["刷存档主战员"] = "海德玛丽",
                ["装备2号位优先级"] = new[] { "突击护手" }
            },
            matcher.WithStaticBoxes(new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            }));
        var installed = await task.RunOnceAsync(equipmentContext, CancellationToken.None);

        Assert.True(installed);
        var equipmentClick = Assert.Single(equipmentInput.Clicks);
        Assert.InRange(equipmentClick.X, 0.755, 0.757);
        Assert.InRange(equipmentClick.Y, 0.521, 0.523);
        Assert.Equal("突击护手", task.RunState.EquipmentNames[1]);
    }
    [Fact]
    public async Task CachedTargetMemberAvatarMatchesSlotAndSkipsTextArchive()
    {
        var store = new MemoryRuntimeFeatureTemplateStore();
        foreach (var featureName in new[]
                 {
                     "target_member",
                     "target_member_small",
                     "target_member_in_select_card",
                     "target_member_tiny",
                     "target_member_in_mask_card",
                     "target_member_large"
                 })
        {
            store.Save(new RuntimeFeatureTemplate(
                featureName,
                2,
                2,
                new byte[16]));
        }

        var matcher = new RuntimeFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var input = new RecordingInputSink();
        var context = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "主战员 辅战员 命运",
                0.99,
                [
                    new OcrTextLine("主战员", 0.99, new OcrTextRegion(440, 20, 100, 30)),
                    new OcrTextLine("辅战员", 0.99, new OcrTextRegion(700, 20, 100, 30)),
                    new OcrTextLine("命运", 0.99, new OcrTextRegion(940, 20, 80, 30))
                ]),
            input,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher,
            store);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.True(task.RunState.SaveTargetMember);
        var closeClick = Assert.Single(input.Clicks);
        Assert.InRange(closeClick.X, 0.959, 0.961);
        Assert.InRange(closeClick.Y, 0.053, 0.055);
    }
    [Fact]
    public async Task TargetMemberArchiveResumesFromMemberListAfterRestart()
    {
        var store = new MemoryRuntimeFeatureTemplateStore();
        var matcher = new RuntimeFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var input = new RecordingInputSink();
        var context = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "主战员 辅战员 命运 套装效果 海德玛丽",
                0.99,
                [
                    new OcrTextLine("主战员", 0.99, new OcrTextRegion(440, 20, 100, 30)),
                    new OcrTextLine("辅战员", 0.99, new OcrTextRegion(700, 20, 100, 30)),
                    new OcrTextLine("命运", 0.99, new OcrTextRegion(940, 20, 80, 30)),
                    new OcrTextLine("套装效果", 0.99, new OcrTextRegion(1140, 20, 120, 30)),
                    new OcrTextLine("菲奥娜", 0.99, new OcrTextRegion(210, 320, 90, 24)),
                    new OcrTextLine("海德玛丽", 0.99, new OcrTextRegion(650, 320, 100, 24)),
                    new OcrTextLine("琳", 0.99, new OcrTextRegion(1090, 320, 70, 24))
                ]),
            input,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher,
            store);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.True(task.RunState.SaveTargetMember);
        Assert.Single(input.Clicks);
        Assert.NotEmpty(store.Load("target_member"));
    }

    [Fact]
    public async Task TargetMemberArchiveRetriesCloseUntilMemberListActuallyDisappears()
    {
        var store = new MemoryRuntimeFeatureTemplateStore();
        var matcher = new RuntimeFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["target_member"] = [new FeatureBox("target_member", 650, 320, 80, 24, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "主战员 辅战员 命运 套装效果 海德玛丽",
            0.99,
            [
                new OcrTextLine("主战员", 0.99, new OcrTextRegion(440, 20, 100, 30)),
                new OcrTextLine("辅战员", 0.99, new OcrTextRegion(700, 20, 100, 30)),
                new OcrTextLine("命运", 0.99, new OcrTextRegion(940, 20, 80, 30)),
                new OcrTextLine("套装效果", 0.99, new OcrTextRegion(1140, 20, 120, 30)),
                new OcrTextLine("海德玛丽", 0.99, new OcrTextRegion(650, 320, 100, 24))
            ]);
        var task = new ChaosModeTask();

        var firstInput = new RecordingInputSink();
        var firstContext = CreateContext(
            CreateFrame(),
            ocr,
            firstInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher,
            store);
        Assert.True(await task.RunOnceAsync(firstContext, CancellationToken.None));
        var firstClose = Assert.Single(firstInput.Clicks);
        Assert.InRange(firstClose.X, 0.959, 0.961);
        Assert.InRange(firstClose.Y, 0.053, 0.055);

        var retryInput = new RecordingInputSink();
        var retryContext = CreateContext(
            CreateFrame(),
            ocr,
            retryInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            matcher,
            store);
        Assert.True(await task.RunOnceAsync(retryContext, CancellationToken.None));
        Assert.Single(retryInput.Clicks);
    }
    [Fact]
    public async Task TargetMemberArchiveBlocksEventSelectionUntilArchiveCompletes()
    {
        var store = new MemoryRuntimeFeatureTemplateStore();
        var task = new ChaosModeTask();
        var startInput = new RecordingInputSink();
        var startContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "50/100",
                0.99,
                [new OcrTextLine("50/100", 0.99, new OcrTextRegion(300, 20, 60, 20))]),
            startInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            new RuntimeFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()),
            store);

        Assert.True(await task.RunOnceAsync(startContext, CancellationToken.None));
        Assert.Single(startInput.Clicks);

        var waitingInput = new RecordingInputSink();
        var waitingContext = CreateContext(
            CreateFrame(),
            CreateEventOptionsOcr(),
            waitingInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            CreateEventOptionsMatcher());
        Assert.True(await task.RunOnceAsync(waitingContext, CancellationToken.None));
        Assert.Empty(waitingInput.Clicks);
        Assert.Equal(string.Empty, task.RunState.PendingEventOptionKey);

        var statisticsInput = new RecordingInputSink();
        var statisticsContext = CreateContext(
            CreateFrame(),
            CreateEventOptionsOcr(),
            statisticsInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            new RuntimeFeatureMatcher(
                new Dictionary<string, IReadOnlyList<FeatureBox>>
                {
                    ["memberinfo"] = [new FeatureBox("memberinfo", 40, 50, 20, 20, 0.99)],
                    ["event1"] = [new FeatureBox("event1", 400, 850, 50, 20, 0.96)],
                    ["event2"] = [new FeatureBox("event2", 900, 850, 50, 20, 0.97)]
                }));
        Assert.True(await task.RunOnceAsync(statisticsContext, CancellationToken.None));
        Assert.Equal(2, statisticsInput.Clicks.Count);
        Assert.InRange(statisticsInput.Clicks[0].X, 0.030, 0.033);
        Assert.InRange(statisticsInput.Clicks[0].Y, 0.065, 0.068);
        Assert.InRange(statisticsInput.Clicks[1].X, 0.200, 0.202);
        Assert.InRange(statisticsInput.Clicks[1].Y, 0.055, 0.057);
        Assert.Equal(string.Empty, task.RunState.PendingEventOptionKey);

        var memberInput = new RecordingInputSink();
        var memberContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "菲奥娜 海德玛丽 琳",
                0.99,
                [
                    new OcrTextLine("菲奥娜", 0.99, new OcrTextRegion(210, 320, 90, 24)),
                    new OcrTextLine("海德玛丽", 0.99, new OcrTextRegion(650, 320, 100, 24)),
                    new OcrTextLine("琳", 0.99, new OcrTextRegion(1090, 320, 70, 24))
                ]),
            memberInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            new RuntimeFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()),
            store);

        Assert.True(await task.RunOnceAsync(memberContext, CancellationToken.None));
        Assert.True(task.RunState.SaveTargetMember);
        Assert.Single(memberInput.Clicks);
    }

    [Fact]
    public async Task TargetMemberArchiveTimeoutReleasesExclusiveFlow()
    {
        var task = new ChaosModeTask(targetMemberArchiveTimeout: TimeSpan.Zero);
        var startInput = new RecordingInputSink();
        var startContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "50/100",
                0.99,
                [new OcrTextLine("50/100", 0.99, new OcrTextRegion(300, 20, 60, 20))]),
            startInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            new StaticFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()));

        Assert.True(await task.RunOnceAsync(startContext, CancellationToken.None));
        Assert.Single(startInput.Clicks);

        var timeoutInput = new RecordingInputSink();
        var timeoutContext = CreateContext(
            CreateFrame(),
            CreateEventOptionsOcr(),
            timeoutInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            CreateEventOptionsMatcher());
        Assert.True(await task.RunOnceAsync(timeoutContext, CancellationToken.None));
        Assert.Empty(timeoutInput.Clicks);
        Assert.Equal(string.Empty, task.RunState.PendingEventOptionKey);

        var eventInput = new RecordingInputSink();
        var eventContext = CreateContext(
            CreateFrame(),
            CreateEventOptionsOcr(),
            eventInput,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            CreateEventOptionsMatcher());
        Assert.True(await task.RunOnceAsync(eventContext, CancellationToken.None));
        Assert.Single(eventInput.Clicks);
        Assert.NotEqual(string.Empty, task.RunState.PendingEventOptionKey);
    }
    [Fact]
    public async Task CopyMemberPrioritizesTargetMemberAvatar()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["target_member_large"] =
                    [new FeatureBox("target_member_large", 806, 453, 20, 20, 0.99)],
                ["copymemberconfirm"] =
                    [new FeatureBox("copymemberconfirm", 640, 90, 120, 30, 0.99)]
            });
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "选择要复制卡牌的主战员",
                0.99,
                [
                    new OcrTextLine(
                        "选择要复制卡牌的主战员",
                        0.99,
                        new OcrTextRegion(730, 820, 180, 30))
                ]),
            input,
            null,
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.503, 0.505);
        Assert.InRange(click.Y, 0.503, 0.505);
    }

    [Fact]
    public async Task CopyMemberFallsBackToDefaultClickOrderWithoutTargetAvatar()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "选择要复制卡牌的主战员",
                0.99,
                [
                    new OcrTextLine(
                        "选择要复制卡牌的主战员",
                        0.99,
                        new OcrTextRegion(730, 820, 180, 30))
                ]),
            input,
            null,
            matcher);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Single(input.Clicks);
        Assert.Equal(1, task.RunState.CopyMemberAttemptIndex);

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Equal(2, input.Clicks.Count);
        Assert.Equal(2, task.RunState.CopyMemberAttemptIndex);

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Equal(3, input.Clicks.Count);
        Assert.Equal(3, task.RunState.CopyMemberAttemptIndex);
        Assert.InRange(input.Clicks[0].X, 0.227, 0.229);
        Assert.InRange(input.Clicks[0].Y, 0.509, 0.511);
        Assert.InRange(input.Clicks[1].X, 0.503, 0.505);
        Assert.InRange(input.Clicks[1].Y, 0.503, 0.505);
        Assert.InRange(input.Clicks[2].X, 0.754, 0.756);
        Assert.InRange(input.Clicks[2].Y, 0.507, 0.509);
    }
    // 场景素材：TestAssets/FrameBus/main-member-flash-confirm-inactive.png；断言使用纯逻辑 OCR。
    [Fact]
    public async Task MainMemberFlashSelectsTargetMemberWhenConfirmIsInactive()
    {
        var frame = CreateUniformFrame(195);
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["target_member_large"] =
                    [new FeatureBox("target_member_large", 1150, 430, 20, 20, 0.99)]
            });
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            CreateMainMemberFlashOcr(),
            input,
            null,
            matcher);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.754, 0.756);
        Assert.InRange(click.Y, 0.507, 0.509);
        Assert.Equal(1, task.RunState.MainMemberFlashAttemptIndex);
    }

    [Fact]
    public async Task MainMemberFlashCyclesCandidatesWhenConfirmRemainsInactive()
    {
        var frame = CreateUniformFrame(195);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            CreateMainMemberFlashOcr(),
            input,
            null,
            new StaticFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()));
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Single(input.Clicks);
        Assert.Equal(1, task.RunState.MainMemberFlashAttemptIndex);

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Equal(2, input.Clicks.Count);
        Assert.Equal(2, task.RunState.MainMemberFlashAttemptIndex);

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Equal(3, input.Clicks.Count);
        Assert.Equal(3, task.RunState.MainMemberFlashAttemptIndex);
        Assert.InRange(input.Clicks[0].X, 0.227, 0.229);
        Assert.InRange(input.Clicks[0].Y, 0.509, 0.511);
        Assert.InRange(input.Clicks[1].X, 0.503, 0.505);
        Assert.InRange(input.Clicks[1].Y, 0.503, 0.505);
        Assert.InRange(input.Clicks[2].X, 0.754, 0.756);
        Assert.InRange(input.Clicks[2].Y, 0.507, 0.509);
    }
    [Fact]
    public async Task ClosePageOverlayHasPriorityOverTargetMemberArchive()
    {
        var frame = CreateFrame();
        var ocr = new OcrFrameResult(
            "1392/1392 点击屏幕可关闭",
            0.99,
            [
                new OcrTextLine("1392/1392", 0.99, new OcrTextRegion(300, 20, 100, 30)),
                new OcrTextLine("点击屏幕可关闭", 0.99, new OcrTextRegion(690, 820, 220, 35))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object> { ["刷存档主战员"] = "海德玛丽" },
            new StaticFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()));
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.49, 0.51);
        Assert.InRange(click.Y, 0.92, 0.95);
    }

    [Fact]
    public async Task ThreeChoiceCardRemoveClicksLocalRemoveButton()
    {
        var frame = CreateFrame();
        var ocr = new OcrFrameResult(
            "移除",
            0.99,
            [new OcrTextLine("移除", 0.99, new OcrTextRegion(900, 820, 100, 30))]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            null,
            new StaticFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()));
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.58, 0.61);
        Assert.InRange(click.Y, 0.91, 0.94);
        Assert.Equal(1, task.RunState.RemovedCardCount);
    }
    [Fact]
    public async Task CardSelectionPrecedesInactiveRemoveButton()
    {
        var frame = CreateFrame();
        for (var y = 813; y < 837; y++)
        {
            var row = y * frame.Stride;
            for (var x = 1441; x < 1481; x++)
            {
                var offset = row + (x * 4);
                frame.Pixels[offset] = 195;
                frame.Pixels[offset + 1] = 195;
                frame.Pixels[offset + 2] = 195;
            }
        }

        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["attack_in_deck"] = [new FeatureBox("attack_in_deck", 560, 280, 80, 40, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "请选择1张移除卡牌 移除",
            0.99,
            [
                new OcrTextLine("请选择1张移除卡牌", 0.99, new OcrTextRegion(260, 25, 180, 25)),
                new OcrTextLine("移除", 0.99, new OcrTextRegion(1485, 810, 60, 30)),
                new OcrTextLine("目标牌", 0.99, new OcrTextRegion(610, 258, 100, 25)),
                new OcrTextLine("基础攻击", 0.99, new OcrTextRegion(620, 282, 100, 25)),
                new OcrTextLine("测试描述", 0.99, new OcrTextRegion(590, 390, 100, 25))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["移除卡牌列表"] = new[] { "目标牌" },
                ["优先移除基础牌"] = true
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.414, 0.417);
        Assert.InRange(click.Y, 0.305, 0.308);
        Assert.Equal(1, task.RunState.PendingRemovedCardCount);
        Assert.Equal(0, task.RunState.RemovedCardCount);
    }
    [Fact]
    public async Task ShopRemovalSelectsCardAndRecordsShopSource()
    {
        var task = new ChaosModeTask();
        task.RunState.Shop = true;
        var frame = CreateFrame();
        var emptyMatcher = new StaticFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var firstInput = new RecordingInputSink();
        var firstContext = CreateContext(
            frame,
            new OcrFrameResult(
                "移除卡牌",
                0.99,
                [
                    new OcrTextLine("移除卡牌", 0.99, new OcrTextRegion(1130, 220, 90, 30)),
                    new OcrTextLine("50", 0.99, new OcrTextRegion(1135, 275, 45, 25)),
                    new OcrTextLine("100", 0.99, new OcrTextRegion(1245, 35, 50, 25))
                ]),
            firstInput,
            null,
            emptyMatcher);

        var enteredRemoval = await task.RunOnceAsync(firstContext, CancellationToken.None);
        Assert.True(enteredRemoval);
        Assert.Single(firstInput.Clicks);
        Assert.True(task.RunState.PendingShopRemoval);

        var secondInput = new RecordingInputSink();
        var deckMatcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["attack_in_deck"] = [new FeatureBox("attack_in_deck", 560, 280, 80, 40, 0.99)]
            });
        var secondContext = CreateContext(
            frame,
            new OcrFrameResult(
                "请选择1张移除卡牌",
                0.99,
                [
                    new OcrTextLine("请选择1张移除卡牌", 0.99, new OcrTextRegion(260, 25, 180, 25)),
                    new OcrTextLine("目标牌", 0.99, new OcrTextRegion(610, 258, 100, 25)),
                    new OcrTextLine("基础攻击", 0.99, new OcrTextRegion(620, 282, 100, 25)),
                    new OcrTextLine("测试描述", 0.99, new OcrTextRegion(590, 390, 100, 25))
                ]),
            secondInput,
            new Dictionary<string, object>
            {
                ["移除卡牌列表"] = new[] { "目标牌" },
                ["优先移除基础牌"] = true
            },
            deckMatcher);

        var selectedCard = await task.RunOnceAsync(secondContext, CancellationToken.None);
        Assert.True(selectedCard);
        var cardClick = Assert.Single(secondInput.Clicks);
        Assert.InRange(cardClick.X, 0.414, 0.417);
        Assert.InRange(cardClick.Y, 0.305, 0.308);
        Assert.Equal(1, task.RunState.PendingRemovedCardCount);

        var thirdInput = new RecordingInputSink();
        var thirdContext = CreateContext(
            frame,
            new OcrFrameResult(
                "移除",
                0.99,
                [new OcrTextLine("移除", 0.99, new OcrTextRegion(1485, 810, 60, 30))]),
            thirdInput,
            null,
            emptyMatcher);

        var removed = await task.RunOnceAsync(thirdContext, CancellationToken.None);
        Assert.True(removed);
        Assert.Single(thirdInput.Clicks);
        Assert.Equal(1, task.RunState.RemovedCardCount);
        Assert.Equal(1, task.RunState.ShopRemovedCardCount);
        Assert.False(task.RunState.PendingShopRemoval);
    }

    [Fact]
    public async Task RestClicksFreeRestAndClearsFlag()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["rest"] = [new FeatureBox("rest", 500, 500, 100, 80, 0.99)]
            });
        var firstOcr = new OcrFrameResult(
            "免费",
            0.99,
            [new OcrTextLine("免费", 0.99, new OcrTextRegion(350, 600, 80, 25))]);
        var confirmOcr = new OcrFrameResult(
            "确认",
            0.99,
            [new OcrTextLine("确认", 0.99, new OcrTextRegion(700, 580, 80, 25))]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            firstOcr,
            input,
            null,
            matcher,
            ocrProvider: new SequencedOcrProvider(firstOcr, confirmOcr));
        var task = new ChaosModeTask();
        task.RunState.FlashOrRest = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.342, 0.345);
        Assert.InRange(click.Y, 0.598, 0.601);
        Assert.False(task.RunState.FlashOrRest);
    }

    [Fact]
    public async Task RestClicksWhenFreeTextMissing()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["rest"] = [new FeatureBox("rest", 500, 500, 100, 80, 0.99)]
            });
        var firstOcr = new OcrFrameResult(string.Empty, 0, []);
        var confirmOcr = new OcrFrameResult(
            "确认",
            0.99,
            [new OcrTextLine("确认", 0.99, new OcrTextRegion(700, 580, 80, 25))]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            firstOcr,
            input,
            null,
            matcher,
            ocrProvider: new SequencedOcrProvider(firstOcr, confirmOcr));
        var task = new ChaosModeTask();
        task.RunState.FlashOrRest = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.342, 0.345);
        Assert.InRange(click.Y, 0.598, 0.601);
        Assert.False(task.RunState.FlashOrRest);
    }

    [Fact]
    public async Task ConsumedRestNodeDoesNotClickAgainWhenFreeTextRemains()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["rest"] = [new FeatureBox("rest", 500, 500, 100, 80, 0.99)]
            });
        var freeRestOcr = new OcrFrameResult(
            "休息免费",
            0.99,
            [new OcrTextLine("免费", 0.99, new OcrTextRegion(500, 690, 80, 25))]);
        var firstInput = new RecordingInputSink();
        var firstContext = CreateContext(
            frame,
            freeRestOcr,
            firstInput,
            null,
            matcher);
        var task = new ChaosModeTask();
        task.RunState.FlashOrRest = true;

        var firstHandled = await task.RunOnceAsync(firstContext, CancellationToken.None);

        Assert.True(firstHandled);
        var click = Assert.Single(firstInput.Clicks);
        Assert.InRange(click.X, 0.342, 0.345);
        Assert.InRange(click.Y, 0.598, 0.601);
        Assert.False(task.RunState.FlashOrRest);

        var secondInput = new RecordingInputSink();
        var secondContext = CreateContext(
            frame,
            freeRestOcr,
            secondInput,
            null,
            matcher);
        await task.RunOnceAsync(secondContext, CancellationToken.None);

        Assert.Empty(secondInput.Clicks);
    }
    [Fact]
    public async Task RestClicksFromFreeTextWhenRestFeatureMissing()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var firstOcr = new OcrFrameResult(
            "免费",
            0.99,
            [new OcrTextLine("免费", 0.99, new OcrTextRegion(350, 600, 80, 25))]);
        var confirmOcr = new OcrFrameResult(
            "确认",
            0.99,
            [new OcrTextLine("确认", 0.99, new OcrTextRegion(700, 580, 80, 25))]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            firstOcr,
            input,
            null,
            matcher,
            ocrProvider: new SequencedOcrProvider(firstOcr, confirmOcr));
        var task = new ChaosModeTask();
        task.RunState.FlashOrRest = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.242, 0.245);
        Assert.InRange(click.Y, 0.679, 0.682);
        Assert.False(task.RunState.FlashOrRest);
    }

    [Fact]
    public async Task MeditatesBeforeRestWhenBothAvailable()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["rest"] = [new FeatureBox("rest", 500, 500, 100, 80, 0.99)],
                ["meditate"] = [new FeatureBox("meditate", 1080, 500, 100, 80, 0.99)]
            });
        var firstOcr = new OcrFrameResult(
            "免费 250 500",
            0.99,
            [
                new OcrTextLine("免费", 0.99, new OcrTextRegion(350, 600, 80, 25)),
                new OcrTextLine("250", 0.99, new OcrTextRegion(1085, 500, 60, 25)),
                new OcrTextLine("500", 0.99, new OcrTextRegion(1245, 35, 50, 25))
            ]);
        var confirmOcr = new OcrFrameResult(
            "确认",
            0.99,
            [new OcrTextLine("确认", 0.99, new OcrTextRegion(700, 580, 80, 25))]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            firstOcr,
            input,
            null,
            matcher,
            ocrProvider: new SequencedOcrProvider(firstOcr, confirmOcr));
        var task = new ChaosModeTask();
        task.RunState.FlashOrRest = true;
        task.RunState.MeditationCards["剑雨"] = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.705, 0.708);
        Assert.InRange(click.Y, 0.598, 0.602);
        Assert.False(task.RunState.FlashOrRest);
    }    [Fact]
    public async Task RestMeditatesWhenPendingAndCreditEnough()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["meditate"] = [new FeatureBox("meditate", 1080, 500, 100, 80, 0.99)]
            });
        var firstOcr = new OcrFrameResult(
            "250",
            0.99,
            [
                new OcrTextLine("250", 0.99, new OcrTextRegion(1085, 500, 60, 25)),
                new OcrTextLine("500", 0.99, new OcrTextRegion(1245, 35, 50, 25))
            ]);
        var confirmOcr = new OcrFrameResult(
            "确认",
            0.99,
            [new OcrTextLine("确认", 0.99, new OcrTextRegion(700, 580, 80, 25))]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            firstOcr,
            input,
            null,
            matcher,
            ocrProvider: new SequencedOcrProvider(firstOcr, confirmOcr));
        var task = new ChaosModeTask();
        task.RunState.FlashOrRest = true;
        task.RunState.MeditationCards["剑雨"] = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.705, 0.708);
        Assert.InRange(click.Y, 0.598, 0.602);
        Assert.False(task.RunState.FlashOrRest);
    }

    [Fact]
    public async Task EventTaskSelectsOptionByConfiguredPriority()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 900, 850, 50, 20, 0.96)],
                ["event2"] = [new FeatureBox("event2", 900, 850, 50, 20, 0.97)]
            });
        var ocr = new OcrFrameResult(
            "慈爱与保护的传令兵",
            0.99,
            [
                new OcrTextLine("慈爱与保护的传令兵向众人传递祝福", 0.99, new OcrTextRegion(300, 760, 180, 30)),
                new OcrTextLine("复制一张稀有卡牌", 0.99, new OcrTextRegion(800, 760, 180, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["必选任务优先级"] = new[] { "复制" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.578, 0.581);
        Assert.InRange(click.Y, 0.839, 0.843);
    }

    [Fact]
    public async Task EventTaskReusesFrameClassification()
    {
        var frame = CreateFrame();
        var matcher = new CountingFeatureMatcher(CreateEventOptionsMatcher());
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            CreateEventOptionsOcr(),
            input,
            new Dictionary<string, object>
            {
                ["必选任务优先级"] = new[] { "挑战敌人" }
            },
            matcher);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));

        Assert.Equal(1, matcher.Count("taskreward"));
        Assert.Equal(1, matcher.Count("event1"));
        Assert.Equal(1, matcher.Count("event2"));
        Assert.Equal(1, matcher.Count("event3"));
    }
    [Fact]
    public async Task EventTaskTakesPriorityOverConfirmTextFragment()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 900, 850, 50, 20, 0.96)]
            });
        var ocr = new OcrFrameResult(
            "虚张声势信用点增加80",
            0.99,
            [
                new OcrTextLine("虚张声势信用点增加80", 0.99, new OcrTextRegion(800, 760, 180, 30)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1060, 570, 60, 25)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1500, 820, 60, 25))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["必选任务优先级"] = new[] { "虚张声势" }
            },
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.578, 0.581);
        Assert.InRange(click.Y, 0.840, 0.843);
    }
    [Fact]
    public async Task EventTaskDoesNotRepeatSameOptionWithinThePageActionBoundary()
    {
        var frame = CreateFrame();
        var matcher = new AlternatingEventFeatureMatcher();
        var ocr = new OcrFrameResult(
            "事件任务页面",
            0.99,
            [
                new OcrTextLine("普通奖励", 0.99, new OcrTextRegion(300, 480, 120, 30)),
                new OcrTextLine("复制一张稀有卡牌", 0.99, new OcrTextRegion(800, 480, 180, 30)),
                new OcrTextLine("普通奖励", 0.99, new OcrTextRegion(300, 760, 120, 30)),
                new OcrTextLine("复制一张稀有卡牌", 0.99, new OcrTextRegion(800, 760, 180, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            ocr,
            input,
            new Dictionary<string, object>
            {
                ["必选任务优先级"] = new[] { "复制" }
            },
            matcher);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.578, 0.581);
        Assert.InRange(click.Y, 0.839, 0.843);
        Assert.Equal(1, task.RunState.PendingEventOptionAttempts);
    }

    [Fact]
    public async Task EventTaskDoesNotClickForbiddenOption()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 900, 850, 50, 20, 0.96)],
                ["forbidden_event"] = [new FeatureBox("forbidden_event", 330, 690, 30, 30, 0.99)]
            });
        var ocr = new OcrFrameResult(
            "慈爱与保护的传令兵",
            0.99,
            [
                new OcrTextLine("慈爱与保护的传令兵向众人传递祝福", 0.99, new OcrTextRegion(300, 760, 180, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, ocr, input, null, matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(input.Clicks);
    }
    [Fact]
    public async Task DestinyChoiceSelectsTitle()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>());
        var ocr = new OcrFrameResult(
            "请选择你的命运",
            0.99,
            [
                new OcrTextLine("请选择你的命运", 0.99, new OcrTextRegion(740, 825, 120, 25)),
                new OcrTextLine("命运一", 0.99, new OcrTextRegion(500, 470, 100, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, ocr, input, null, matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.342, 0.345);
        Assert.InRange(click.Y, 0.537, 0.541);
    }

    [Fact]
    public async Task EventTaskIgnoresLowConfidenceLatinFalsePositive()
    {
        var frame = CreateFrame();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event4"] = [new FeatureBox("event4", 1180, 760, 40, 20, 0.7162)]
            });
        var ocr = new OcrFrameResult(
            "30Enter",
            0.99,
            [
                new OcrTextLine("30Enter", 0.99, new OcrTextRegion(1200, 760, 80, 25))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, ocr, input, null, matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(input.Clicks);
    }
    [Fact]
    public async Task EquipmentInstallToOtherMemberClicksOnceUntilPageChanges()
    {
        var frame = CreateFrame();
        var qualityOffset = (368 * frame.Stride) + (187 * 4);
        frame.Pixels[qualityOffset] = 138;
        frame.Pixels[qualityOffset + 1] = 76;
        frame.Pixels[qualityOffset + 2] = 61;
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var ocr = new OcrFrameResult(
            "装备",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(760, 95, 80, 30)),
                new OcrTextLine("请选择主战员", 0.99, new OcrTextRegion(1390, 108, 170, 28)),
                new OcrTextLine("突击护手", 0.99, new OcrTextRegion(500, 350, 120, 30)),
                new OcrTextLine("防御力", 0.99, new OcrTextRegion(350, 405, 100, 25)),
                new OcrTextLine("战斗开始时，反击3", 0.99, new OcrTextRegion(350, 470, 250, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, ocr, input, null, matcher);
        var task = new ChaosModeTask();
        task.RunState.EquipmentNames[1] = "突击护手";
        task.RunState.EquipmentQualities[1] = "普通";

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.755, 0.757);
        Assert.InRange(click.Y, 0.521, 0.523);
        Assert.Single(input.HoverClicks);

        var repeatedInput = new RecordingInputSink();
        var repeatedContext = CreateContext(
            CreateFrame(),
            ocr,
            repeatedInput,
            null,
            matcher);
        task.RunState.EquipmentNames[1] = "突击护手";
        task.RunState.EquipmentQualities[1] = "普通";

        var repeatedHandled = await task.RunOnceAsync(repeatedContext, CancellationToken.None);

        Assert.True(repeatedHandled);
        Assert.Empty(repeatedInput.Clicks);
    }
    [Fact]
    public async Task EquipmentPageClicksConfirmAfterSelectingMember()
    {
        var frame = CreateFrame();
        var qualityOffset = (368 * frame.Stride) + (187 * 4);
        frame.Pixels[qualityOffset] = 138;
        frame.Pixels[qualityOffset + 1] = 76;
        frame.Pixels[qualityOffset + 2] = 61;
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var installOcr = new OcrFrameResult(
            "装备",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(760, 95, 80, 30)),
                new OcrTextLine("请选择主战员", 0.99, new OcrTextRegion(1390, 108, 170, 28)),
                new OcrTextLine("突击护手", 0.99, new OcrTextRegion(500, 350, 120, 30)),
                new OcrTextLine("防御力", 0.99, new OcrTextRegion(350, 405, 100, 25)),
                new OcrTextLine("战斗开始时，反击3", 0.99, new OcrTextRegion(350, 470, 250, 30))
            ]);
        var confirmOcr = new OcrFrameResult(
            "装备 确认",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(760, 95, 80, 30)),
                new OcrTextLine("请选择主战员", 0.99, new OcrTextRegion(1390, 108, 170, 28)),
                new OcrTextLine("突击护手", 0.99, new OcrTextRegion(500, 350, 120, 30)),
                new OcrTextLine("防御力", 0.99, new OcrTextRegion(350, 405, 100, 25)),
                new OcrTextLine("战斗开始时，反击3", 0.99, new OcrTextRegion(350, 470, 250, 30)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1450, 820, 60, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            installOcr,
            input,
            null,
            matcher,
            ocrProvider: new SequencedOcrProvider(installOcr, confirmOcr));
        var task = new ChaosModeTask();
        task.RunState.EquipmentNames[1] = "突击护手";
        task.RunState.EquipmentQualities[1] = "普通";

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));

        Assert.Equal(2, input.Clicks.Count);
        Assert.InRange(input.Clicks[0].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[0].Y, 0.521, 0.523);
        Assert.InRange(input.Clicks[1].X, 0.920, 0.925);
        Assert.InRange(input.Clicks[1].Y, 0.925, 0.935);
    }
    private static OcrFrameResult CreateEventOptionsOcr()
        => new(
            "获得资源 挑战敌人",
            0.99,
            [
                new OcrTextLine("获得资源", 0.99, new OcrTextRegion(260, 700, 180, 30)),
                new OcrTextLine("挑战敌人", 0.99, new OcrTextRegion(760, 700, 180, 30))
            ]);

    private static StaticFeatureMatcher CreateEventOptionsMatcher()
        => new(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 400, 850, 50, 20, 0.96)],
                ["event2"] = [new FeatureBox("event2", 900, 850, 50, 20, 0.97)]
            });
    private static TaskContext CreateContext(
        BgraFrame frame,
        OcrFrameResult ocr,
        IInputSink input,
        IReadOnlyDictionary<string, object>? config,
        IFeatureMatcher featureMatcher,
        IRuntimeFeatureTemplateStore? runtimeFeatureTemplates = null,
        IOcrProvider? ocrProvider = null,
        IDecisionSnapshotSink? decisionSnapshots = null)
        => new(
            "normal-flow-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            taskConfig: config,
            ocr: ocrProvider ?? new StaticOcrProvider(ocr),
            featureMatcher: featureMatcher,
            runtimeFeatureTemplates: runtimeFeatureTemplates,
            decisionSnapshots: decisionSnapshots);

    private static OcrFrameResult CreateMainMemberFlashOcr()
        => new(
            "请选择获得闪光的主战员 确认",
            0.99,
            [
                new OcrTextLine(
                    "请选择获得闪光的主战员",
                    0.99,
                    new OcrTextRegion(640, 825, 320, 32)),
                new OcrTextLine(
                    "确认",
                    0.99,
                    new OcrTextRegion(1450, 828, 70, 30))
            ]);

    private static BgraFrame CreateUniformFrame(byte value, int width = 1600, int height = 900)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }

        return new BgraFrame(width, height, pixels);
    }
    private static BgraFrame CreateFrame(int width = 1600, int height = 900)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0;
            pixels[index + 1] = 0;
            pixels[index + 2] = 0;
            pixels[index + 3] = 255;
        }

        return new BgraFrame(width, height, pixels);
    }

    private static void DrawLine(
        BgraFrame frame,
        int startX,
        int startY,
        int endX,
        int endY,
        byte value)
    {
        var steps = Math.Max(Math.Abs(endX - startX), Math.Abs(endY - startY));
        for (var step = 0; step <= steps; step++)
        {
            var progress = steps == 0 ? 0 : (double)step / steps;
            var x = (int)Math.Round(startX + ((endX - startX) * progress));
            var y = (int)Math.Round(startY + ((endY - startY) * progress));
            var offset = (y * frame.Stride) + (x * 4);
            frame.Pixels[offset] = value;
            frame.Pixels[offset + 1] = value;
            frame.Pixels[offset + 2] = value;
        }
    }

    private sealed class CountingFeatureMatcher(IFeatureMatcher inner) : IFeatureMatcher
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int Count(string featureName)
            => _calls.GetValueOrDefault(featureName);

        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
        {
            _calls[featureName] = Count(featureName) + 1;
            return inner.Find(frame, featureName, region, threshold);
        }
    }
    private sealed class RecordingDecisionSnapshotSink : IDecisionSnapshotSink
    {
        public bool IsEnabled { get; set; }
        public List<DecisionSnapshotRequest> Requests { get; } = [];

        public ValueTask<string?> CaptureAsync(
            DecisionSnapshotRequest request,
            BgraFrame frame,
            OcrFrameResult? ocr,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult<string?>("recorded-diagnostic");
        }
    }

    private sealed class StaticFrameSource(FrameLease frame) : IFrameSource
    {
        private static long _lastSequence = 1000;

        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Next());

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Next());

        private FrameLease Next()
        {
            if (frame is not IBgraFrameLease bgraFrame)
            {
                return frame;
            }

            var sequence = Interlocked.Increment(ref _lastSequence);
            return new TestBgraFrameLease(bgraFrame.Frame, sequence);
        }
    }

    private sealed class TestBgraFrameLease(BgraFrame frame, long sequence = 1) : FrameLease(
        sequence,
        DateTimeOffset.Now,
        frame.Width,
        frame.Height,
        "BGRA32"), IBgraFrameLease
    {
        public BgraFrame Frame { get; } = frame;

        public override void Dispose()
        {
        }
    }

    private sealed class StaticOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public OcrFrameResult Recognize(BgraFrame frame) => result;
    }

    private sealed class SequencedOcrProvider(params OcrFrameResult[] results) : IOcrProvider
    {
        private int _index;

        public OcrFrameResult Recognize(BgraFrame frame)
        {
            if (results.Length == 0)
            {
                throw new InvalidOperationException("At least one OCR result is required.");
            }

            var index = Math.Min(_index, results.Length - 1);
            _index++;
            return results[index];
        }
    }

    private sealed class AlternatingEventFeatureMatcher : IFeatureMatcher
    {
        private int _event1Calls;

        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
        {
            if (featureName == "event1")
            {
                _event1Calls++;
                var y = _event1Calls == 1 ? 850 : 450;
                return [new FeatureBox("event1", 400, y, 50, 20, 0.96)];
            }

            return featureName == "event2"
                ? [new FeatureBox("event2", 900, 850, 50, 20, 0.97)]
                : [];
        }
    }

    private sealed class StaticFeatureMatcher(
        IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> boxes) : IFeatureMatcher
    {
        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
            => boxes.TryGetValue(featureName, out var matches) ? matches : [];
    }

    private sealed class MemoryRuntimeFeatureTemplateStore : IRuntimeFeatureTemplateStore
    {
        private readonly Dictionary<string, List<RuntimeFeatureTemplate>> _templates =
            new(StringComparer.Ordinal);

        public IReadOnlyList<RuntimeFeatureTemplate> Load(string featureName)
            => _templates.TryGetValue(featureName, out var stored)
                ? stored
                    .Select(template => template with { Pixels = template.Pixels.ToArray() })
                    .ToArray()
                : [];

        public void Save(RuntimeFeatureTemplate template)
        {
            if (!_templates.TryGetValue(template.FeatureName, out var templates))
            {
                templates = [];
                _templates[template.FeatureName] = templates;
            }

            templates.Add(template with { Pixels = template.Pixels.ToArray() });
        }
    }

    private sealed class RuntimeFeatureMatcher : IRuntimeFeatureMatcher
    {
        private readonly Dictionary<string, FeatureBox> _runtimeBoxes = new(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> _staticBoxes;

        public RuntimeFeatureMatcher(
            IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> staticBoxes)
        {
            _staticBoxes = staticBoxes;
        }

        public IReadOnlyCollection<string> RuntimeFeatureNames => _runtimeBoxes.Keys;

        public RuntimeFeatureMatcher WithStaticBoxes(
            IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> staticBoxes)
        {
            _staticBoxes = staticBoxes;
            return this;
        }

        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
        {
            if (_runtimeBoxes.TryGetValue(featureName, out var runtimeBox)
                && (region is null || Contains(region, runtimeBox, frame)))
            {
                return [runtimeBox];
            }

            return _staticBoxes.TryGetValue(featureName, out var boxes)
                ? boxes.Where(box => region is null || Contains(region, box, frame)).ToArray()
                : [];
        }

        public RuntimeFeatureTemplate RegisterRuntimeTemplate(
            string featureName,
            BgraFrame sourceFrame,
            FeatureSearchRegion sourceRegion,
            FeatureSearchRegion targetRegion)
        {
            var x = Math.Clamp((int)Math.Round(targetRegion.Left * sourceFrame.Width), 0, sourceFrame.Width - 1);
            var y = Math.Clamp((int)Math.Round(targetRegion.Top * sourceFrame.Height), 0, sourceFrame.Height - 1);
            var width = Math.Max(1, (int)Math.Round((targetRegion.Right - targetRegion.Left) * sourceFrame.Width));
            var height = Math.Max(1, (int)Math.Round((targetRegion.Bottom - targetRegion.Top) * sourceFrame.Height));
            _runtimeBoxes[featureName] = new FeatureBox(featureName, x, y, width, height, 1);
            return new RuntimeFeatureTemplate(
                featureName,
                width,
                height,
                new byte[width * height * 4]);
        }

        public void RegisterRuntimeTemplate(
            string featureName,
            RuntimeFeatureTemplate template)
        {
            var (x, y, width, height) = string.Equals(
                featureName,
                "target_member",
                StringComparison.Ordinal)
                ? (850, 190, 80, 80)
                : (0, 0, template.Width, template.Height);
            _runtimeBoxes[featureName] = new FeatureBox(
                featureName,
                x,
                y,
                width,
                height,
                1);
        }

        private static bool Contains(
            FeatureSearchRegion region,
            FeatureBox box,
            BgraFrame frame)
        {
            var centerX = box.CenterX(frame.Width);
            var centerY = box.CenterY(frame.Height);
            return centerX >= region.Left
                   && centerX <= region.Right
                   && centerY >= region.Top
                   && centerY <= region.Bottom;
        }
    }
    private sealed class RecordingInputSink : IInputSink
    {
        public List<RelativePoint> Clicks { get; } = [];
        public List<RelativePoint> HoverClicks { get; } = [];

        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            Clicks.Add(point);
            return ValueTask.CompletedTask;
        }

        public ValueTask HoverClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            HoverClicks.Add(point);
            Clicks.Add(point);
            return ValueTask.CompletedTask;
        }

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
