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
    public void PageActionWithoutRetryPolicyUsesExplicitNoRetryReason()
    {
        var arbiter = new ActionArbiter();
        var first = Observation(70, "route-selection");
        arbiter.ObservePage(first, "route-selection");
        var firstDecision = arbiter.TryBeginPageAction("route-selection", first);
        arbiter.MarkPageActionSent("route-selection", first);

        var repeated = Observation(71, "route-selection");
        arbiter.ObservePage(repeated, "route-selection");
        var repeatedDecision = arbiter.TryBeginPageAction("route-selection", repeated);

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, repeatedDecision.Kind);
        Assert.Contains("未声明自动重试", repeatedDecision.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("2147483647", repeatedDecision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PageActionSurvivesDifferentRouteUntilThatRouteDispatches()
    {
        var arbiter = new ActionArbiter();
        var continuePage = Observation(24, "continue-page");
        var firstAction = arbiter.TryBeginPageAction("continue", continuePage);
        arbiter.MarkPageActionSent("continue", continuePage);

        var transientPage = Observation(25, "validation-prompt-partial");
        arbiter.ObservePage(transientPage, "validation-card-prompt");
        var transientAction = arbiter.TryBeginPageAction("validation-card-prompt", transientPage);
        arbiter.CancelPageAction("validation-card-prompt", transientPage);

        var repeatedContinue = Observation(26, "continue-page");
        var repeatedObservation = arbiter.ObservePage(repeatedContinue, "continue");
        var repeatedAction = arbiter.TryBeginPageAction("continue", repeatedContinue);

        Assert.Equal(PageActionDecisionKind.Execute, firstAction.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, transientAction.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, repeatedObservation.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, repeatedAction.Kind);

        var handledTransient = arbiter.TryBeginPageAction("validation-card-prompt", transientPage);
        arbiter.MarkPageActionSent("validation-card-prompt", transientPage);
        var laterContinue = Observation(27, "later-continue-page");
        var laterObservation = arbiter.ObservePage(laterContinue, "continue");
        var laterAction = arbiter.TryBeginPageAction("continue", laterContinue);

        Assert.Equal(PageActionDecisionKind.Execute, handledTransient.Kind);
        Assert.Equal(ActionDecisionKind.None, laterObservation.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, laterAction.Kind);
    }

    [Fact]
    public void PageActionIsReleasedAfterSourceRouteLeavesObservedCandidates()
    {
        var arbiter = new ActionArbiter();
        var bossReward = Observation(
            28,
            "boss-reward",
            routeIds: ["boss-equipment-reward", "equipment"]);
        var firstDecision = arbiter.TryBeginPageAction(
            "boss-equipment-reward",
            bossReward,
            retryAfterFrames: 2,
            maxAttempts: 3,
            sourceRouteId: "boss-equipment-reward");
        arbiter.MarkPageActionSent("boss-equipment-reward", bossReward);

        var equipmentPage = Observation(
            29,
            "equipment-choice",
            routeIds: ["equipment"]);
        arbiter.ObservePage(equipmentPage);

        var returnedBossReward = Observation(
            30,
            "boss-reward-second-stage",
            routeIds: ["boss-equipment-reward"]);
        var returnedDecision = arbiter.TryBeginPageAction(
            "boss-equipment-reward",
            returnedBossReward,
            retryAfterFrames: 2,
            maxAttempts: 3,
            sourceRouteId: "boss-equipment-reward");

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, returnedDecision.Kind);
    }

    [Fact]
    public void PageActionRetriesSameRouteAfterConfiguredFrames()
    {
        var arbiter = new ActionArbiter();
        var first = Observation(50, "event-task");
        arbiter.ObservePage(first, "event-task");
        var firstDecision = arbiter.TryBeginPageAction(
            "event-task",
            first,
            retryAfterFrames: 2);
        arbiter.MarkPageActionSent("event-task", first);

        var firstUnchangedFrame = Observation(51, "event-task");
        arbiter.ObservePage(firstUnchangedFrame, "event-task");
        var waitingDecision = arbiter.TryBeginPageAction(
            "event-task",
            firstUnchangedFrame,
            retryAfterFrames: 2);

        var retryFrame = Observation(52, "event-task");
        arbiter.ObservePage(retryFrame, "event-task");
        var retryDecision = arbiter.TryBeginPageAction(
            "event-task",
            retryFrame,
            retryAfterFrames: 2);

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, waitingDecision.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, retryDecision.Kind);
    }

    [Fact]
    public void PageActionCoolsDownAndRecoversAfterFastAttemptLimit()
    {
        var arbiter = new ActionArbiter();
        var first = Observation(60, "confirm");
        arbiter.ObservePage(first, "confirm");
        var firstDecision = arbiter.TryBeginPageAction(
            "confirm",
            first,
            retryAfterFrames: 2,
            maxAttempts: 3);
        arbiter.MarkPageActionSent("confirm", first);

        var beforeSecondAttempt = Observation(61, "confirm");
        arbiter.ObservePage(beforeSecondAttempt, "confirm");
        var beforeSecondDecision = arbiter.TryBeginPageAction(
            "confirm",
            beforeSecondAttempt,
            retryAfterFrames: 2,
            maxAttempts: 3);

        var secondAttempt = Observation(62, "confirm");
        arbiter.ObservePage(secondAttempt, "confirm");
        var secondDecision = arbiter.TryBeginPageAction(
            "confirm",
            secondAttempt,
            retryAfterFrames: 2,
            maxAttempts: 3);
        arbiter.MarkPageActionSent("confirm", secondAttempt);

        var beforeThirdAttempt = Observation(63, "confirm");
        arbiter.ObservePage(beforeThirdAttempt, "confirm");
        var beforeThirdDecision = arbiter.TryBeginPageAction(
            "confirm",
            beforeThirdAttempt,
            retryAfterFrames: 2,
            maxAttempts: 3);

        var thirdAttempt = Observation(64, "confirm");
        arbiter.ObservePage(thirdAttempt, "confirm");
        var thirdDecision = arbiter.TryBeginPageAction(
            "confirm",
            thirdAttempt,
            retryAfterFrames: 2,
            maxAttempts: 3);
        arbiter.MarkPageActionSent("confirm", thirdAttempt);

        var afterLimit = Observation(66, "confirm");
        arbiter.ObservePage(afterLimit, "confirm");
        var afterLimitDecision = arbiter.TryBeginPageAction(
            "confirm",
            afterLimit,
            retryAfterFrames: 2,
            maxAttempts: 3);

        var recoveryFrame = Observation(67, "confirm");
        arbiter.ObservePage(recoveryFrame, "confirm");
        var recoveryDecision = arbiter.TryBeginPageAction(
            "confirm",
            recoveryFrame,
            retryAfterFrames: 2,
            maxAttempts: 3,
            recoveryAfterFrames: 3);
        arbiter.MarkPageActionSent("confirm", recoveryFrame);

        var nextCooldownFrame = Observation(69, "confirm");
        arbiter.ObservePage(nextCooldownFrame, "confirm");
        var nextCooldownDecision = arbiter.TryBeginPageAction(
            "confirm",
            nextCooldownFrame,
            retryAfterFrames: 2,
            maxAttempts: 3,
            recoveryAfterFrames: 3);

        var secondRecoveryFrame = Observation(70, "confirm");
        arbiter.ObservePage(secondRecoveryFrame, "confirm");
        var secondRecoveryDecision = arbiter.TryBeginPageAction(
            "confirm",

            secondRecoveryFrame,
            retryAfterFrames: 2,
            maxAttempts: 3,
            recoveryAfterFrames: 3);
        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, beforeSecondDecision.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, secondDecision.Kind);
        Assert.Contains("第 2/3 次尝试", secondDecision.Reason, StringComparison.Ordinal);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, beforeThirdDecision.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, thirdDecision.Kind);
        Assert.Contains("第 3/3 次尝试", thirdDecision.Reason, StringComparison.Ordinal);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, afterLimitDecision.Kind);
        Assert.Contains("快速重试上限", afterLimitDecision.Reason, StringComparison.Ordinal);
        Assert.Equal(PageActionDecisionKind.Recover, recoveryDecision.Kind);
        Assert.Equal(1, recoveryDecision.RecoveryCount);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, nextCooldownDecision.Kind);
        Assert.Equal(PageActionDecisionKind.Recover, secondRecoveryDecision.Kind);
        Assert.Equal(2, secondRecoveryDecision.RecoveryCount);
    }

    [Fact]
    public void PageActionDoesNotRetryStablePageAfterCapturedTimeouts()
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
        var suppressedDecision = arbiter.TryBeginPageAction("event-task", timedOut);

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, timedOutObservation.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, suppressedDecision.Kind);

        arbiter.MarkPageActionSent("event-task", timedOut);
        var secondTimeout = Observation(
            32,
            "event-task",
            capturedAt: firstTimestamp.AddSeconds(12));
        var secondTimeoutObservation = arbiter.ObservePage(secondTimeout, "event-task");
        var secondSuppressedDecision = arbiter.TryBeginPageAction("event-task", secondTimeout);

        Assert.Equal(ActionDecisionKind.SuppressDuplicate, secondTimeoutObservation.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, secondSuppressedDecision.Kind);
    }

    [Fact]
    public void PageActionIgnoresOcrLayoutJitterOnTheSameRoute()
    {
        var arbiter = new ActionArbiter();
        var firstTimestamp = DateTimeOffset.Parse("2026-10-13T12:00:00+08:00");
        var first = Observation(
            40,
            "event-before-selection",
            capturedAt: firstTimestamp);
        arbiter.ObservePage(first, "event-task");
        var firstDecision = arbiter.TryBeginPageAction("event-task", first);
        arbiter.MarkPageActionSent("event-task", first);

        var selectionChanged = Observation(
            41,
            "event-after-selection",
            capturedAt: firstTimestamp.AddMilliseconds(300));
        var changedObservation = arbiter.ObservePage(selectionChanged, "event-task");
        var suppressedDecision = arbiter.TryBeginPageAction("event-task", selectionChanged);

        var sameChangedPage = Observation(
            42,
            "event-after-selection",
            capturedAt: firstTimestamp.AddMilliseconds(600));
        var unchangedObservation = arbiter.ObservePage(sameChangedPage, "event-task");

        Assert.Equal(PageActionDecisionKind.Execute, firstDecision.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, changedObservation.Kind);
        Assert.Equal(PageActionDecisionKind.SuppressDuplicate, suppressedDecision.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, unchangedObservation.Kind);
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
    public void ConfirmedDialogWithOcrJitterDoesNotIssueRepeatedConfirm()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(
            40,
            "rest-page",
            routeIds: ["rest", "skip"]);
        var skip = arbiter.SubmitSkip(
            SkipIntent(40, source.Fingerprint),
            source,
            new RelativePoint(0.94, 0.92));
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(skip.Command));

        var dialogPoint = new RelativePoint(0.66, 0.63);
        var dialog = Observation(
            41,
            "confirmation-dialog-a",
            hasCenterConfirmation: true,
            dialogPoint,
            routeIds: ["shared-confirmation", "confirm", "rest"]);
        var confirm = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(confirm.Command));

        for (var frameSequence = 42L; frameSequence <= 50; frameSequence++)
        {
            var jitteredDialog = Observation(
                frameSequence,
                $"confirmation-dialog-jitter-{frameSequence}",
                hasCenterConfirmation: true,
                dialogPoint,
                routeIds: ["shared-confirmation", "confirm", "rest"]);

            var observation = arbiter.ObservePage(jitteredDialog);
            var repeatedConfirm = arbiter.ObserveConfirmation(jitteredDialog);

            Assert.NotEqual(ActionDecisionKind.Completed, observation.Kind);
            Assert.NotEqual(ActionDecisionKind.ExecuteConfirm, repeatedConfirm.Kind);
        }
    }


    [Fact]
    public void ConfirmationKeepsSourceBlockedWhileSemanticRouteRemains()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(
            20,
            "rest-ocr-a",
            routeIds: ["rest", "skip"]);
        var skip = arbiter.SubmitSkip(
            SkipIntent(20, source.Fingerprint),
            source,
            new RelativePoint(0.9, 0.9));
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(skip.Command));

        var jitterOne = arbiter.ObservePage(Observation(
            21,
            "rest-ocr-b",
            routeIds: ["rest", "shop"]));
        var jitterTwo = arbiter.ObservePage(Observation(
            22,
            "rest-ocr-c",
            routeIds: ["skip"]));
        var nextPageOne = arbiter.ObservePage(Observation(
            23,
            "next-page-a",
            routeIds: ["route-selection", "skip"]));
        var nextPageTwo = arbiter.ObservePage(Observation(
            24,
            "next-page-b",
            routeIds: ["event-task", "skip"]));

        Assert.Equal(ActionDecisionKind.ExecuteSkip, skip.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, jitterOne.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, jitterTwo.Kind);
        Assert.Equal(ActionDecisionKind.Wait, nextPageOne.Kind);
        Assert.Equal(ActionDecisionKind.Completed, nextPageTwo.Kind);
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
        var restAction = arbiter.TryBeginPageAction("rest", restPage);
        arbiter.MarkPageActionSent("rest", restPage);
        var dialog = Observation(
            31,
            "confirmation-dialog",
            hasCenterConfirmation: true,
            new RelativePoint(0.66, 0.63));

        var decision = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(decision.Command));

        var sourceReturned = arbiter.ObservePage(Observation(32, "rest-page"));
        var transition1 = arbiter.ObservePage(Observation(33, "transition-page"));
        var transition2 = arbiter.ObservePage(Observation(34, "transition-page"));
        var sourceAfterCompletion = arbiter.ObservePage(Observation(35, "rest-page"));

        Assert.Equal(ActionDecisionKind.ExecuteConfirm, decision.Kind);
        Assert.Equal(ActionDecisionKind.SuppressDuplicate, sourceReturned.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, restAction.Kind);
        Assert.Equal(ActionDecisionKind.Wait, transition1.Kind);
        Assert.Equal(ActionDecisionKind.Completed, transition2.Kind);
        Assert.Equal(ActionDecisionKind.None, sourceAfterCompletion.Kind);
    }

    [Fact]
    public void GenericConfirmationCompletesWhenChangedSourcePageReturns()
    {
        var arbiter = new ActionArbiter();
        var source = Observation(30, "rest-page-a", routeIds: ["rest"]);
        arbiter.ObservePage(source);
        var action = arbiter.TryBeginPageAction("rest", source);
        arbiter.MarkPageActionSent("rest", source);

        var dialog = Observation(
            31,
            "confirmation-dialog",
            hasCenterConfirmation: true,
            new RelativePoint(0.66, 0.63),
            routeIds: ["shared-confirmation", "rest"]);
        var confirmation = arbiter.ObserveConfirmation(dialog);
        arbiter.MarkCommandSent(Assert.IsType<InputCommand>(confirmation.Command));

        var changedSource = Observation(32, "rest-page-b", routeIds: ["rest"]);
        var sourceReturned = arbiter.ObservePage(changedSource, "rest");
        var nextAction = arbiter.TryBeginPageAction("rest", changedSource);

        Assert.Equal(PageActionDecisionKind.Execute, action.Kind);
        Assert.Equal(ActionDecisionKind.ExecuteConfirm, confirmation.Kind);
        Assert.Equal(ActionDecisionKind.Completed, sourceReturned.Kind);
        Assert.Equal(PageActionDecisionKind.Execute, nextAction.Kind);
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
        DateTimeOffset capturedAt = default,
        IReadOnlyList<string>? routeIds = null)
        => new(
            "session",
            frameSequence,
            1600,
            900,
            new PageFingerprint(fingerprint),
            hasCenterConfirmation,
            confirmationPoint,
            capturedAt)
        {
            RouteIds = routeIds ?? Array.Empty<string>()
        };

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
