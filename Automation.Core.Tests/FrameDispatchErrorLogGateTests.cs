using Automation.Core.Dispatching;
using Xunit;

namespace Automation.Core.Tests;

public sealed class FrameDispatchErrorLogGateTests
{
    [Fact]
    public void RepeatedErrorsAlertOnceAtConfiguredThreshold()
    {
        var gate = new FrameDispatchErrorLogGate();

        var first = gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 1), 3);
        var second = gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 2), 3);
        var third = gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 3), 3);
        var fourth = gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 4), 3);

        Assert.Equal(FrameDispatchErrorLogDecisionKind.Observe, first.Kind);
        Assert.Equal(1, first.ConsecutiveCount);
        Assert.Equal(FrameDispatchErrorLogDecisionKind.Observe, second.Kind);
        Assert.Equal(2, second.ConsecutiveCount);
        Assert.Equal(FrameDispatchErrorLogDecisionKind.Alert, third.Kind);
        Assert.Equal(3, third.ConsecutiveCount);
        Assert.Equal(FrameDispatchErrorLogDecisionKind.Suppress, fourth.Kind);
    }

    [Fact]
    public void ResetStartsANewConsecutiveStreak()
    {
        var gate = new FrameDispatchErrorLogGate();
        gate.Record(Error(FrameDispatchErrorKind.ConsumerRejected, 1), 3);
        gate.Record(Error(FrameDispatchErrorKind.ConsumerRejected, 2), 3);

        gate.Reset();
        var afterReset = gate.Record(Error(FrameDispatchErrorKind.ConsumerRejected, 3), 3);

        Assert.Equal(FrameDispatchErrorLogDecisionKind.Observe, afterReset.Kind);
        Assert.Equal(1, afterReset.ConsecutiveCount);
    }

    [Fact]
    public void DifferentConsumerStartsANewConsecutiveStreak()
    {
        var gate = new FrameDispatchErrorLogGate();
        gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 1, "first"), 3);
        gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 2, "first"), 3);

        var other = gate.Record(Error(FrameDispatchErrorKind.NoConsumer, 3, "second"), 3);

        Assert.Equal(FrameDispatchErrorLogDecisionKind.Observe, other.Kind);
        Assert.Equal(1, other.ConsecutiveCount);
    }

    [Theory]
    [InlineData(FrameDispatchErrorKind.ConsumerFailed)]
    [InlineData(FrameDispatchErrorKind.DeliveryRejected)]
    public void CriticalErrorsRemainImmediate(FrameDispatchErrorKind kind)
    {
        var gate = new FrameDispatchErrorLogGate();

        var decision = gate.Record(Error(kind, 1), 5);

        Assert.Equal(FrameDispatchErrorLogDecisionKind.Alert, decision.Kind);
        Assert.True(decision.IsImmediate);
        Assert.Equal(1, decision.ConsecutiveCount);
    }

    private static FrameDispatchError Error(
        FrameDispatchErrorKind kind,
        long frameSequence,
        string consumerId = "event-task")
        => new(
            "session",
            frameSequence,
            consumerId,
            kind,
            "error");
}
