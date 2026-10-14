using Automation.Core.Arbitration;
using Automation.Core.Events;
using Automation.Core.Frames;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Xunit;

namespace Automation.Core.Tests;

public sealed class EventBusAndArbitrationTests
{
    [Fact]
    public void FullFrameAnalyzerCachesByFrameSequence()
    {
        var frame = CreateFrame();
        var lease = new TestFrameLease(frame, 7);
        var ocr = new CountingOcrProvider(CreateOcr("确认", 700, 500));
        var analyzer = new FullFrameOcrAnalyzer();

        var first = analyzer.Analyze("session", lease, ocr);
        var second = analyzer.Analyze("session", lease, ocr);

        Assert.Same(first, second);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public async Task FullFrameAnalyzerPublishesOneEventPerFrame()
    {
        var frame = CreateFrame();
        var lease = new TestFrameLease(frame, 8);
        var ocr = new CountingOcrProvider(CreateOcr("确认", 700, 500));
        var analyzer = new FullFrameOcrAnalyzer();
        using var bus = new SessionEventBus();
        var receivedTask = ReceiveFirstAsync(bus.Subscribe<FullFrameOcrCompleted>());

        analyzer.Analyze("session", lease, ocr, bus);
        analyzer.Analyze("session", lease, ocr, bus);
        var received = await receivedTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(8, received.FrameSequence);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public void PageFingerprintIgnoresDynamicNumbersButKeepsText()
    {
        var first = PageFingerprint.FromOcr(
            CreateOcr("卡牌奖励 100", 700, 500),
            1600,
            900);
        var second = PageFingerprint.FromOcr(
            CreateOcr("卡牌奖励 999", 700, 500),
            1600,
            900);
        var different = PageFingerprint.FromOcr(
            CreateOcr("事件选项 100", 700, 500),
            1600,
            900);

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void DuplicateSkipIntentOnSameSourcePageIsSuppressed()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(10, "source-page");
        var intent = SkipIntent(10, source.Fingerprint);

        var first = arbiter.SubmitSkip(intent, source, new RelativePoint(0.9, 0.9));
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(first.Command));
        var repeated = arbiter.SubmitSkip(
            SkipIntent(11, source.Fingerprint),
            Observation(11, "source-page"),
            new RelativePoint(0.9, 0.9));

        Assert.Equal(ActionDecisionKind.ExecuteSkip, first.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, repeated.Kind);
    }

    [Fact]
    public void PageActionGateSuppressesSameRouteUntilRouteChanges()
    {
        var arbiter = new ActionArbiter();
        var first = Observation(20, "expedition-result");
        var repeated = Observation(21, "expedition-result-ocr-jitter");
        var nextPage = Observation(22, "route-selection");

        arbiter.ObservePage(first);
        var firstDecision = arbiter.TryBeginPageAction("expedition-result", first);
        arbiter.MarkPageActionSent("expedition-result", first);

        var repeatedObservation = arbiter.ObservePage(repeated, "expedition-result");
        var repeatedDecision = arbiter.TryBeginPageAction("expedition-result", repeated);
        var nextPageObservation = arbiter.ObservePage(nextPage, "route-selection");
        var nextPageDecision = arbiter.TryBeginPageAction("route-selection", nextPage);

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, repeatedObservation.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, repeatedDecision.Kind);
        Assert.Equal(ActionDecisionKind.None, nextPageObservation.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, nextPageDecision.Kind);
    }

    [Fact]
    public void PageActionAllowsOneRetryAfterCapturedTimeout()
    {
        var arbiter = new ActionArbiter();
        var firstTimestamp = DateTimeOffset.Parse("2026-10-13T12:00:00+08:00");
        var first = Observation(30, "event-task", capturedAt: firstTimestamp);
        arbiter.ObservePage(first);
        var firstDecision = arbiter.TryBeginPageAction("event-task", first);
        arbiter.MarkPageActionSent("event-task", first);

        var timedOut = Observation(
            31,
            "event-task",
            capturedAt: firstTimestamp.AddSeconds(6));
        var timedOutObservation = arbiter.ObservePage(timedOut, "event-task");
        var retryDecision = arbiter.TryBeginPageAction("event-task", timedOut);

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(ActionDecisionKind.None, timedOutObservation.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, retryDecision.Kind);
        Assert.True(retryDecision.IsRetry);
    }

    [Fact]
    public void PageActionRemainsBlockedWhileConfirmationSessionOwnsTheFrame()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(40, "rest-page");
        arbiter.ObservePage(source);
        var action = arbiter.TryBeginPageAction("rest", source);
        arbiter.MarkPageActionSent("rest", source);

        var dialog = Observation(
            41,
            "confirmation-dialog",
            hasCenterConfirmation: true,
            new RelativePoint(0.66, 0.63));
        var dialogObservation = arbiter.ObservePage(dialog, "shared-confirmation");
        var confirmation = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(confirmation.Command));

        var sourceReturned = arbiter.ObservePage(Observation(42, "rest-page"), "rest");
        var duplicateAction = arbiter.TryBeginPageAction("rest", Observation(42, "rest-page"));

        Assert.Equal(PageActionDecisionKind.Execute, action.Kind);
        Assert.Equal(ActionDecisionKind.None, dialogObservation.Kind);
        Assert.Equal(ActionDecisionKind.ExecuteConfirm, confirmation.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, sourceReturned.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, duplicateAction.Kind);
    }

    [Fact]
    public void ConfirmationDoesNotCompleteUntilSourcePageLeaves()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(10, "source-page");
        var skip = arbiter.SubmitSkip(
            SkipIntent(10, source.Fingerprint),
            source,
            new RelativePoint(0.9, 0.9));
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(skip.Command));

        var dialog = Observation(
            11,
            "confirmation-dialog",
            hasCenterConfirmation: true,
            new RelativePoint(0.66, 0.63));
        var observeDialog = arbiter.ObservePage(dialog);
        var confirm = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(confirm.Command));

        var sourceReturned = arbiter.ObservePage(Observation(12, "source-page"));
        var distinctPage1 = arbiter.ObservePage(Observation(13, "next-page"));
        var distinctPage2 = arbiter.ObservePage(Observation(14, "next-page"));

        Assert.Equal(ActionDecisionKind.Wait, observeDialog.Kind);
        Assert.Equal(ActionDecisionKind.ExecuteConfirm, confirm.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, sourceReturned.Kind);
        Assert.Equal(ActionDecisionKind.Wait, distinctPage1.Kind);
        Assert.Equal(ActionDecisionKind.Completed, distinctPage2.Kind);
    }

    [Fact]
    public void ConfirmationCompletesWhenNextPageAlsoHasCenterConfirmation()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(40, "source-page");
        arbiter.ObservePage(source);
        var dialog = Observation(
            41,
            "confirmation-dialog",
            hasCenterConfirmation: true,
            new RelativePoint(0.66, 0.63));
        var confirm = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(confirm.Command));

        var nextPage1 = Observation(
            42,
            "card-reward",
            hasCenterConfirmation: true,
            new RelativePoint(0.55, 0.67));
        var nextPage2 = Observation(
            43,
            "card-reward",
            hasCenterConfirmation: true,
            new RelativePoint(0.55, 0.67));

        var observeNextPage1 = arbiter.ObservePage(nextPage1);
        var observeNextPage2 = arbiter.ObservePage(nextPage2);
        var nextSkip = arbiter.SubmitSkip(
            SkipIntent(44, nextPage2.Fingerprint),
            Observation(44, "card-reward"),
            new RelativePoint(0.74, 0.93));

        Assert.Equal(ActionDecisionKind.Wait, observeNextPage1.Kind);
        Assert.Equal(ActionDecisionKind.Completed, observeNextPage2.Kind);
        Assert.Equal(ActionDecisionKind.ExecuteSkip, nextSkip.Kind);
    }
    [Fact]
    public void GenericCenterConfirmationUsesPreviousPageAsSource()
    {
        var arbiter = new ActionArbiter();
        var restPage = Observation(30, "rest-page");
        arbiter.ObservePage(restPage);
        var dialog = Observation(
            31,
            "confirmation-dialog",
            hasCenterConfirmation: true,
            new RelativePoint(0.66, 0.63));

        var decision = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(decision.Command));

        var sourceReturned = arbiter.ObservePage(Observation(32, "rest-page"));

        Assert.Equal(ActionDecisionKind.ExecuteConfirm, decision.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, sourceReturned.Kind);
    }

    private static SkipCurrentContentIntent SkipIntent(long frameSequence, PageFingerprint sourceFingerprint)
        => new(
            IntentId.New(),
            CooldownKeys.SkipCurrentContent,
            frameSequence,
            sourceFingerprint,
            "test");

    private static PageObservation Observation(
        long frameSequence,
        string fingerprint,
        bool hasCenterConfirmation = false,
        RelativePoint? confirmationPoint = null,
        DateTimeOffset capturedAt = default)
        => new(
            "session",
            frameSequence,
            1600,
            900,
            new PageFingerprint(fingerprint),
            hasCenterConfirmation,
            confirmationPoint,
            capturedAt);

    private static OcrFrameResult CreateOcr(string text, int x, int y)
        => new(
            text,
            0.99,
            [new OcrTextLine(text, 0.99, new OcrTextRegion(x, y, 100, 30))]);

    private static BgraFrame CreateFrame()
        => new(1600, 900, new byte[1600 * 900 * 4]);

    private static async Task<T> ReceiveFirstAsync<T>(IAsyncEnumerable<T> events)
    {
        await foreach (var @event in events)
        {
            return @event;
        }

        throw new InvalidOperationException("No event was published.");
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

    private sealed class TestFrameLease(BgraFrame frame, long sequence) : FrameLease(
        sequence,
        DateTimeOffset.Now,
        frame.Width,
        frame.Height,
        "BGRA32"),
        IBgraFrameLease
    {
        public BgraFrame Frame { get; } = frame;

        public override void Dispose()
        {
        }
    }
}