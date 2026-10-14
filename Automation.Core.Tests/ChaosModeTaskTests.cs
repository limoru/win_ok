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

public sealed class ChaosModeTaskTests
{
    [Fact]
    public async Task ContinueClicksMatchingTextInsideRegion()
    {
        var frame = CreateFrame();
        var result = CreateOcr("继 续！", 1200, 850, 90, 34);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.77, 0.79);
        Assert.InRange(click.Y, 0.94, 0.98);
    }

    [Fact]
    public async Task ConfirmClicksActiveButton()
    {
        var frame = CreateFrame();
        var region = new OcrTextRegion(1400, 810, 90, 34);
        var result = CreateOcr("确认", region);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task ConfirmDoesNotClickDisabledGrayButton()
    {
        var frame = CreateFrame();
        var region = new OcrTextRegion(1400, 810, 90, 34);
        FillDisabledGray(frame, region);
        var result = CreateOcr("确认", region);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task ContinueSupportsTraditionalChineseConfiguration()
    {
        var frame = CreateFrame();
        var result = CreateOcr("繼 續", 1200, 850, 90, 34);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            result,
            input,
            new Dictionary<string, object>
            {
                ["游戏语言"] = "繁体中文"
            });
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task TextOutsideButtonRegionIsIgnored()
    {
        var frame = CreateFrame();
        var result = CreateOcr("继续", 180, 180, 90, 34);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(input.Clicks);
    }    [Fact]
    public async Task MinimizemapClicksFeatureCenter()
    {
        var frame = CreateFrame();
        var input = new RecordingInputSink();
        var featureMatcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["minimizemap"] =
                [
                    new FeatureBox("minimizemap", 400, 300, 100, 50, 0.96)
                ]
            });
        var context = CreateContext(
            frame,
            new OcrFrameResult(string.Empty, 0, []),
            input,
            featureMatcher: featureMatcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.275, 0.287);
        Assert.InRange(click.Y, 0.355, 0.367);
    }

    [Theory]
    [InlineData("出现错乱")]
    [InlineData("点击重试")]
    [InlineData("通讯不稳定，正在重新尝试")]
    public async Task BattleCrashClicksScreenCenter(string text)
    {
        var frame = CreateFrame();
        var result = CreateOcr(text, 600, 350, 220, 50);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.Equal(0.5, click.X, 3);
        Assert.Equal(0.5, click.Y, 3);
    }


    [Fact]


    public void CatalogCanCreateAvailableChaosModeTask()
    {
        var descriptor = Assert.Single(
            TaskModuleCatalog.Instance.GetDescriptors(),
            item => item.ModuleId == "ChaosMode");
        var task = TaskModuleCatalog.Instance.Create("ChaosMode");

        Assert.Equal(TaskModuleAvailability.Available, descriptor.Availability);
        Assert.IsType<ChaosModeTask>(task);
    }

    [Fact]
    public async Task ClosePageClicksPromptAnywhere()
    {
        var frame = CreateFrame();
        var result = CreateOcr("点击屏幕继续", 200, 120, 140, 40);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.15, 0.25);
        Assert.InRange(click.Y, 0.14, 0.18);
    }

    [Fact]
    public async Task SkipClicksOcrBoxCenterWhenSkipTextExists()
    {
        var frame = CreateFrame();
        var result = CreateOcr("跳过", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.938, 0.944);
        Assert.InRange(click.Y, 0.914, 0.920);
    }

    [Fact]
    public async Task SettlementClicksFixedRelativePoint()
    {
        var frame = CreateFrame();
        var result = CreateOcr("结算", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.935, 0.947);
        Assert.InRange(click.Y, 0.911, 0.923);
    }

    [Fact]
    public async Task LeaveClicksActiveButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("离开", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task LeaveDoesNotClickDisabledGrayButton()
    {
        var frame = CreateFrame();
        var region = new OcrTextRegion(1450, 800, 110, 50);
        FillDisabledGray(frame, region);
        var result = CreateOcr("离开", region);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task NextStepMatchesByEditDistance()
    {
        var frame = CreateFrame();
        var result = CreateOcr("下—步", new OcrTextRegion(1380, 815, 70, 30));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }
    [Fact]
    public async Task FlashClicksActiveRightBottomButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("闪光", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.935, 0.947);
        Assert.InRange(click.Y, 0.911, 0.923);
    }

    [Fact]
    public async Task FlashDoesNotClickDisabledGrayButton()
    {
        var frame = CreateFrame();
        var region = new OcrTextRegion(1450, 800, 110, 50);
        FillDisabledGray(frame, region);
        var result = CreateOcr("闪光", region);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task DisabledFlashFallsBackToNextStepHandler()
    {
        var frame = CreateFrame();
        var flashRegion = new OcrTextRegion(1450, 800, 110, 50);
        FillDisabledGray(frame, flashRegion);
        var result = new OcrFrameResult(
            "闪光 下一步",
            0.99,
            [
                new OcrTextLine("闪光", 0.99, flashRegion),
                new OcrTextLine("下一步", 0.99, new OcrTextRegion(1350, 810, 90, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.865, 0.875);
        Assert.InRange(click.Y, 0.915, 0.930);
    }
    [Fact]
    public async Task CloseButtonClicksCenterOfButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("关闭", new OcrTextRegion(760, 810, 120, 45));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.505, 0.520);
        Assert.InRange(click.Y, 0.918, 0.933);
    }

    [Fact]
    public async Task ConvertWaitsConfiguredRoundsThenBlocksFixedClickAndCapturesDiagnostic()
    {
        var frame = CreateFrame();
        var region = new OcrTextRegion(1450, 800, 110, 50);
        FillDisabledGray(frame, region);
        var result = CreateOcr("转换", region);
        var input = new RecordingInputSink();
        var snapshots = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            result,
            input,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 2
            },
            decisionSnapshots: snapshots);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Empty(input.Clicks);
        Assert.Empty(snapshots.Requests);

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.Empty(input.Clicks);
        var request = Assert.Single(snapshots.Requests);
        Assert.Equal(DecisionSnapshotCatalog.FixedClickBlockedReasonId, request.ReasonId);
    }

    [Fact]
    public async Task CraftClicksBoxAtProbePoint()
    {
        var frame = CreateFrame();
        var result = CreateOcr("合成", new OcrTextRegion(1450, 790, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task SelectClicksActiveButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("选择", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task WeaknessInfoClosesAtFixedPoint()
    {
        var frame = CreateFrame();
        var result = CreateOcr("弱点", new OcrTextRegion(560, 70, 120, 45));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.496, 0.508);
        Assert.InRange(click.Y, 0.086, 0.098);
    }

    [Fact]
    public async Task EquipmentRecastClicksFixedPoint()
    {
        var frame = CreateFrame();
        var result = CreateOcr("装备重铸", new OcrTextRegion(750, 90, 110, 45));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.743, 0.755);
        Assert.InRange(click.Y, 0.926, 0.938);
    }

    [Fact]
    public async Task ObtainRewardClicksBox()
    {
        var frame = CreateFrame();
        var result = CreateOcr("获得", new OcrTextRegion(1420, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }
    [Fact]
    public async Task GoToChaosCoreClicksActiveButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("前往卡厄思核心", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task ZeroSystemHomeClicksCodex()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("零式系统", 0.99, new OcrTextRegion(120, 20, 160, 45)),
            new("法典", 0.99, new OcrTextRegion(1240, 395, 120, 50))
        };
        var result = new OcrFrameResult("零式系统法典", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.80, 0.83);
        Assert.InRange(click.Y, 0.45, 0.49);
    }

    [Fact]
    public async Task CodexSearchClicksFixedPoint()
    {
        var frame = CreateFrame();
        var result = CreateOcr("法典", new OcrTextRegion(740, 370, 120, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.494, 0.506);
        Assert.InRange(click.Y, 0.754, 0.766);
    }

    [Fact]
    public async Task ExpeditionUnlockClicksPage()
    {
        var frame = CreateFrame();
        var result = CreateOcr("解锁的探险记录", new OcrTextRegion(740, 110, 120, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.494, 0.506);
        Assert.InRange(click.Y, 0.944, 0.956);
    }

    [Fact]
    public async Task MemoryEliminationClicksButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("记忆消除", new OcrTextRegion(880, 610, 120, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task ChaosCraftClicksFreeCraft()
    {
        var frame = CreateFrame();
        var result = CreateOcr("免费合成", new OcrTextRegion(860, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task ConquerDifficultyClicksClose()
    {
        var frame = CreateFrame();
        var result = CreateOcr("征服新难度", new OcrTextRegion(750, 490, 120, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.496, 0.508);
        Assert.InRange(click.Y, 0.937, 0.949);
    }

    [Fact]
    public async Task TreatingClicksMethod()
    {
        var frame = CreateFrame();
        var result = CreateOcr("选择哪种方法进行治疗", 700, 300, 240, 40);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.759, 0.771);
        Assert.InRange(click.Y, 0.494, 0.506);
    }

    [Fact]
    public async Task TreatApproveClicksApprove()
    {
        var frame = CreateFrame();
        var result = CreateOcr("点击批准", 700, 300, 140, 40);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.762, 0.774);
        Assert.InRange(click.Y, 0.804, 0.816);
    }

    [Fact]
    public async Task HeldCardsPageClicksClose()
    {
        var frame = CreateFrame();
        var result = CreateOcr("持有卡牌", new OcrTextRegion(760, 25, 120, 45));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.960, 0.972);
        Assert.InRange(click.Y, 0.047, 0.059);
    }

    [Fact]
    public async Task StageEndDataDetailsClosesDetails()
    {
        var frame = CreateFrame();
        var result = CreateOcr("存储数据", new OcrTextRegion(200, 110, 140, 40));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.877, 0.889);
        Assert.InRange(click.Y, 0.150, 0.162);
    }
    [Fact]
    public async Task EscapeCommandClicksAndMarksEscaped()
    {
        var frame = CreateFrame();
        var result = CreateOcr("逃脱", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();
        task.DispatchEscapeCommand("unit-test");

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
        Assert.True(task.RunState.IsEscaped);
    }

    [Fact]
    public async Task EscapePageWithoutCommandCancelsAndDoesNotMarkEscaped()
    {
        var frame = CreateFrame();
        var result = new OcrFrameResult(
            "取消",
            0.99,
            [
                new OcrTextLine("取消", 0.99, new OcrTextRegion(700, 815, 70, 30)),
                new OcrTextLine("逃脱", 0.99, new OcrTextRegion(1450, 800, 110, 50))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.458, 0.461);
        Assert.InRange(click.Y, 0.919, 0.923);
        Assert.False(task.RunState.IsEscaped);
    }
    [Fact]
    public async Task SettlementCountsFinalBossLayerWhenFlagSet()
    {
        var frame = CreateFrame();
        var result = CreateOcr("结算", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();
        task.RunState.ReachFinalBoss = true;
        task.RunState.NodeCount = 7;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(1, task.RunState.PassFinalBossCount);
        Assert.False(task.RunState.ReachFinalBoss);
        Assert.Equal(0, task.RunState.NodeCount);
    }

    [Fact]
    public void ResetLayerPreservesProgressCounters()
    {
        var state = new ChaosRunState
        {
            PassFinalBossCount = 3,
            TotalRounds = 12,
            SuccessRounds = 4,
            NodeCount = 5,
            ReachFinalBoss = true,
            IsEscaped = true
        };

        state.ResetLayer();

        Assert.Equal(3, state.PassFinalBossCount);
        Assert.Equal(12, state.TotalRounds);
        Assert.Equal(4, state.SuccessRounds);
        Assert.Equal(0, state.NodeCount);
        Assert.False(state.ReachFinalBoss);
        Assert.False(state.IsEscaped);
    }
    [Fact]
    public async Task CenterConfirmClicksMatchingButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("确认", new OcrTextRegion(700, 300, 90, 34));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.46, 0.47);
        Assert.InRange(click.Y, 0.35, 0.36);
    }

    [Fact]
    public async Task ValidationCardPromptChecksDontShowThenConfirms()
    {
        var frame = CreateFrame();
        var result = new OcrFrameResult(
            "战利品验证卡",
            0.99,
            [
                new OcrTextLine("是否确定不使用战利品验证卡，继续进行游戏？", 0.99, new OcrTextRegion(700, 370, 400, 30)),
                new OcrTextLine("下次登录前不再显示", 0.99, new OcrTextRegion(780, 470, 200, 24)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1060, 568, 80, 25))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(2, input.Clicks.Count);
        Assert.InRange(input.Clicks[0].X, 0.479, 0.481);
        Assert.InRange(input.Clicks[0].Y, 0.534, 0.537);
        Assert.InRange(input.Clicks[1].X, 0.687, 0.689);
        Assert.InRange(input.Clicks[1].Y, 0.644, 0.646);
        var continueFrame = CreateFrame();
        var continueResult = CreateOcr("继续", new OcrTextRegion(1500, 820, 60, 25));
        var continueInput = new RecordingInputSink();
        var continueContext = CreateContext(continueFrame, continueResult, continueInput);

        var continueHandled = await task.RunOnceAsync(continueContext, CancellationToken.None);

        Assert.True(continueHandled);
        Assert.Empty(continueInput.Clicks);
    }
    [Fact]
    public async Task CenterConfirmDoesNotClickDisabledGrayButton()
    {
        var frame = CreateFrame();
        var region = new OcrTextRegion(700, 300, 90, 34);
        FillDisabledGray(frame, region);
        var result = CreateOcr("确认", region);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task CopyClicksActiveButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("复制", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task ReflashTextUsesReflashBranch()
    {
        var frame = CreateFrame();
        var result = CreateOcr("重新闪光", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var log = new RecordingLogSink();
        var context = CreateContext(frame, result, input, logSink: log);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
        // 日志文案来自分支自己的 label：若被闪光分支抢先，只会出现"闪光"而非"重新闪光"。
        Assert.Contains(log.Messages, message => message.Contains("重新闪光", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GrantFlashTextUsesGrantFlashBranch()
    {
        var frame = CreateFrame();
        var result = CreateOcr("赋予闪光", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var log = new RecordingLogSink();
        var context = CreateContext(frame, result, input, logSink: log);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
        Assert.Contains(log.Messages, message => message.Contains("赋予闪光", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlashSupportsTraditionalVocabularyVariant()
    {
        var frame = CreateFrame();
        var result = CreateOcr("灵光一闪", new OcrTextRegion(1450, 800, 110, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            result,
            input,
            new Dictionary<string, object>
            {
                ["游戏语言"] = "繁体中文"
            });
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task ZeroSystemHomeRequiresBothConditions()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("法典", 0.99, new OcrTextRegion(1240, 395, 120, 50))
        };
        var result = new OcrFrameResult("法典", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task ConfirmMatchesWithOcrNoise()
    {
        var frame = CreateFrame();
        var result = CreateOcr("确 认!", new OcrTextRegion(1400, 810, 90, 34));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task NextStepAcceptsEditDistanceTwo()
    {
        var frame = CreateFrame();
        var result = CreateOcr("下二卡", new OcrTextRegion(1380, 815, 70, 30));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(input.Clicks);
    }

    [Fact]
    public async Task NextStepRejectsEditDistanceThree()
    {
        var frame = CreateFrame();
        var result = CreateOcr("卡二下", new OcrTextRegion(1380, 815, 70, 30));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task FindBoxAtPointPrefersSmallestBox()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("跳过", 0.99, new OcrTextRegion(1400, 780, 180, 80)),
            new("跳过", 0.99, new OcrTextRegion(1500, 820, 60, 20))
        };
        var result = new OcrFrameResult("跳过", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.950, 0.962);
        Assert.InRange(click.Y, 0.916, 0.928);
    }

    [Fact]
    public async Task RegionTextJoinsMultipleBoxesForDataDetails()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("存储", 0.99, new OcrTextRegion(200, 110, 70, 40)),
            new("数据", 0.99, new OcrTextRegion(280, 110, 70, 40))
        };
        var result = new OcrFrameResult("存储数据", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.877, 0.889);
        Assert.InRange(click.Y, 0.150, 0.162);
    }

    [Fact]
    public void ResetMissionPreservesOnlyRunTotals()
    {
        var state = new ChaosRunState
        {
            PassFinalBossCount = 3,
            TotalRounds = 12,
            SuccessRounds = 4,
            SaveTargetMember = true,
            NodeCount = 5,
            ReachFinalBoss = true,
            IsEscaped = true
        };

        state.ResetMission();

        Assert.Equal(0, state.PassFinalBossCount);
        Assert.Equal(12, state.TotalRounds);
        Assert.Equal(4, state.SuccessRounds);
        Assert.True(state.SaveTargetMember);
        Assert.Equal(0, state.NodeCount);
        Assert.False(state.ReachFinalBoss);
        Assert.False(state.IsEscaped);
    }
    [Fact]
    public async Task EnterClicksMatchingButton()
    {
        var frame = CreateFrame();
        var result = CreateOcr("进入", new OcrTextRegion(700, 800, 100, 40));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.462, 0.475);
        Assert.InRange(click.Y, 0.905, 0.917);
    }
    [Fact]
    public async Task RefineEquipmentCreditClicksReceive()
    {
        var frame = CreateFrame();
        var result = CreateOcr("以信用点接收", new OcrTextRegion(900, 550, 120, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.594, 0.606);
        Assert.InRange(click.Y, 0.633, 0.645);
    }

    [Fact]
    public async Task ConvertCardClicksTwoSkipPoints()
    {
        var frame = CreateFrame();
        var result = CreateOcr("转换的卡牌", new OcrTextRegion(300, 20, 140, 45));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(2, input.Clicks.Count);
        Assert.InRange(input.Clicks[0].X, 0.770, 0.782);
        Assert.InRange(input.Clicks[0].Y, 0.920, 0.932);
        Assert.InRange(input.Clicks[1].X, 0.655, 0.667);
        Assert.InRange(input.Clicks[1].Y, 0.626, 0.638);
    }

    [Fact]
    public async Task NegotiationClicksSkip()
    {
        var frame = CreateFrame();
        var result = CreateOcr("失败", new OcrTextRegion(740, 590, 120, 50));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.659, 0.671);
        Assert.InRange(click.Y, 0.893, 0.905);
    }
    [Fact]
    public async Task ExpeditionResultCountsSuccessWhenCompleted()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("探险结果", 0.99, new OcrTextRegion(940, 90, 120, 40)),
            new("完成", 0.99, new OcrTextRegion(1440, 90, 90, 40))
        };
        var result = new OcrFrameResult("探险结果完成", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        // Python 版这个处理器末尾 return False：它结算状态，但不算“已处理”。
        Assert.True(handled);
        Assert.Empty(input.Clicks);
        Assert.Equal(1, task.RunState.TotalRounds);
        Assert.Equal(1, task.RunState.SuccessRounds);
    }

    [Fact]
    public async Task ExpeditionResultFallsBackToNextStepInSameFrame()
    {
        var frame = CreateFrame();
        var result = new OcrFrameResult(
            "探险结果 下一步",
            0.99,
            [
                new OcrTextLine("探险结果", 0.99, new OcrTextRegion(940, 90, 120, 40)),
                new OcrTextLine("下一步", 0.99, new OcrTextRegion(1340, 810, 90, 30))
            ]);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.858, 0.870);
        Assert.InRange(click.Y, 0.915, 0.930);
        Assert.Equal(1, task.RunState.TotalRounds);
    }
    [Fact]
    public async Task ExpeditionResultOnSamePageCountsOnlyOnce()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("探险结果", 0.99, new OcrTextRegion(940, 90, 120, 40)),
            new("完成", 0.99, new OcrTextRegion(1440, 90, 90, 40))
        };
        var result = new OcrFrameResult("探险结果完成", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));
        Assert.True(await task.RunOnceAsync(context, CancellationToken.None));

        Assert.Equal(1, task.RunState.TotalRounds);
        Assert.Equal(1, task.RunState.SuccessRounds);
    }
    [Fact]
    public async Task ExpeditionResultCountsFailureWhenEscaped()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("探险结果", 0.99, new OcrTextRegion(940, 90, 120, 40))
        };
        var result = new OcrFrameResult("探险结果", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();
        task.RunState.IsEscaped = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(1, task.RunState.TotalRounds);
        Assert.Equal(0, task.RunState.SuccessRounds);
    }

    [Fact]
    public async Task ExpeditionResultOnlyFirstLayerLogsSuccessWithoutCounting()
    {
        var frame = CreateFrame();
        var lines = new List<OcrTextLine>
        {
            new("探险结果", 0.99, new OcrTextRegion(940, 90, 120, 40))
        };
        var result = new OcrFrameResult("探险结果", 0.99, lines);
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            result,
            input,
            new Dictionary<string, object>
            {
                ["只打第一层"] = true
            });
        var task = new ChaosModeTask();
        task.RunState.PassFinalBossCount = 1;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        // 忠实复刻 Python：该分支只记日志，不累加 success_rounds。
        Assert.True(handled);
        Assert.Equal(1, task.RunState.TotalRounds);
        Assert.Equal(0, task.RunState.SuccessRounds);
    }
    [Fact]
    public async Task MentalBreakdownGoesToTraumaCenterByDefault()
    {
        var frame = CreateFrame();
        var result = CreateOcr("精神崩溃发生", new OcrTextRegion(740, 150, 120, 40));
        var input = new RecordingInputSink();
        var context = CreateContext(frame, result, input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.700, 0.712);
        Assert.InRange(click.Y, 0.909, 0.921);
    }

    [Fact]
    public async Task MentalBreakdownDoesNothingWhenTreatmentDisabled()
    {
        var frame = CreateFrame();
        var result = CreateOcr("精神崩溃发生", new OcrTextRegion(740, 150, 120, 40));
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            result,
            input,
            new Dictionary<string, object>
            {
                ["治疗崩溃"] = false
            });
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(input.Clicks);
    }
    [Fact]
    public async Task SkipConfirmationDoesNotRepeatUntilSourcePageLeaves()
    {
        var task = new ChaosModeTask();
        var firstInput = new RecordingInputSink();
        var firstContext = CreateContext(
            CreateFrame(),
            CreateOcr("跳过", 1450, 800, 110, 50),
            firstInput);

        var firstHandled = await task.RunOnceAsync(firstContext, CancellationToken.None);

        Assert.True(firstHandled);
        Assert.Single(firstInput.Clicks);

        var confirmationInput = new RecordingInputSink();
        var confirmationContext = CreateContext(
            CreateFrame(),
            new OcrFrameResult(
                "确定要跳过吗",
                0.99,
                [
                    new OcrTextLine("确定要跳过吗", 0.99, new OcrTextRegion(690, 390, 200, 35)),
                    new OcrTextLine("确认", 0.99, new OcrTextRegion(1060, 568, 80, 25))
                ]),
            confirmationInput);

        var confirmationHandled = await task.RunOnceAsync(
            confirmationContext,
            CancellationToken.None);

        Assert.True(confirmationHandled);
        var confirmationClick = Assert.Single(confirmationInput.Clicks);
        Assert.InRange(confirmationClick.X, 0.66, 0.70);
        Assert.InRange(confirmationClick.Y, 0.61, 0.65);

        var repeatedInput = new RecordingInputSink();
        var repeatedContext = CreateContext(
            CreateFrame(),
            CreateOcr("跳过", 1450, 800, 110, 50),
            repeatedInput);

        var repeatedHandled = await task.RunOnceAsync(
            repeatedContext,
            CancellationToken.None);

        Assert.True(repeatedHandled);
        Assert.Empty(repeatedInput.Clicks);
    }
    [Fact]
    public async Task OcrRunsOnEveryFrameEvenWhenNoHandlerMatches()
    {
        var task = new ChaosModeTask();
        var ocrProvider = new CountingOcrProvider(
            CreateOcr("无处理器页面", 320, 240, 120, 32));

        for (var index = 0; index < 3; index++)
        {
            var context = CreateContext(
                CreateFrame(),
                CreateOcr("无处理器页面", 320, 240, 120, 32),
                new RecordingInputSink(),
                ocrProvider: ocrProvider);

            await task.RunOnceAsync(context, CancellationToken.None);
        }

        Assert.Equal(3, ocrProvider.CallCount);
    }
    [Fact]
    public async Task UnhandledPageLogsOnFirstFrameWithoutThreeFrameGate()
    {
        var task = new ChaosModeTask();
        var logSink = new RecordingLogSink();
        var context = CreateContext(
            CreateFrame(),
            CreateOcr("主页动画A", 320, 240, 120, 32),
            new RecordingInputSink(),
            logSink: logSink);

        await task.RunOnceAsync(context, CancellationToken.None);

        Assert.Contains(
            logSink.Messages,
            message => message.Contains("未识别页面", StringComparison.Ordinal)
                       && message.Contains("主页动画A", StringComparison.Ordinal));
    }
    private static TaskContext CreateContext(
        BgraFrame frame,
        OcrFrameResult ocr,
        IInputSink input,
        IReadOnlyDictionary<string, object>? config = null,
        ITaskLogSink? logSink = null,
        IFeatureMatcher? featureMatcher = null,
        IOcrProvider? ocrProvider = null,
        IDecisionSnapshotSink? decisionSnapshots = null)
        => new(
            "chaos-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            logSink: logSink,
            taskConfig: config,
            ocr: ocrProvider ?? new StaticOcrProvider(ocr),
            featureMatcher: featureMatcher,
            decisionSnapshots: decisionSnapshots);

    private static OcrFrameResult CreateOcr(
        string text,
        int x,
        int y,
        int width,
        int height)
        => CreateOcr(text, new OcrTextRegion(x, y, width, height));

    private static OcrFrameResult CreateOcr(string text, OcrTextRegion region)
    {
        var line = new OcrTextLine(text, 0.99, region);
        return new OcrFrameResult(text, 0.99, [line]);
    }

    private static BgraFrame CreateFrame(int width = 1600, int height = 900)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 1] = 255;
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        return new BgraFrame(width, height, pixels);
    }

    private static void FillDisabledGray(BgraFrame frame, OcrTextRegion region)
    {
        var leftWidth = (int)(region.Width * 0.67);
        var leftHeight = (int)(region.Height * 0.83);
        var leftX = Math.Max(0, region.X - (int)(leftWidth * 1.1));
        var leftY = Math.Max(0, region.Y + (int)(region.Height * 0.1));
        leftWidth = Math.Min(leftWidth, frame.Width - leftX);
        leftHeight = Math.Min(leftHeight, frame.Height - leftY);

        for (var row = 0; row < leftHeight; row++)
        {
            var offset = ((leftY + row) * frame.Stride) + (leftX * 4);
            for (var column = 0; column < leftWidth; column++)
            {
                frame.Pixels[offset] = 195;
                frame.Pixels[offset + 1] = 195;
                frame.Pixels[offset + 2] = 195;
                frame.Pixels[offset + 3] = 255;
                offset += 4;
            }
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

    private sealed class CountingOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public int CallCount { get; private set; }

        public OcrFrameResult Recognize(BgraFrame frame)
        {
            CallCount++;
            return result;
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
            => boxes.TryGetValue(featureName, out var matches)
                ? matches
                : [];
    }

    private sealed class RecordingLogSink : ITaskLogSink
    {
        public List<string> Messages { get; } = [];

        public void Log(string sessionId, string message, string? tag = null)
            => Messages.Add(message);
    }

    private sealed class RecordingInputSink : IInputSink
    {
        public List<RelativePoint> Clicks { get; } = [];

        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            Clicks.Add(point);
            return ValueTask.CompletedTask;
        }

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(
            InputKey key,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
