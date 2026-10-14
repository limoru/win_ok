using Automation.Core.Targeting;

namespace Automation.Core.Arbitration;

public readonly record struct CommandId(Guid Value)
{
    public static CommandId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public enum InputCommandKind
{
    SkipCurrentContent,
    ConfirmCenterDialog
}

public sealed record InputCommand(
    CommandId Id,
    IntentId IntentId,
    long EvidenceFrameSequence,
    InputCommandKind Kind,
    RelativePoint Target,
    string Purpose);

public enum ActionDecisionKind
{
    None,
    Wait,
    SuppressDuplicate,
    ExecuteSkip,
    ExecuteConfirm,
    Completed,
    Failed
}

public sealed record ActionDecision(
    ActionDecisionKind Kind,
    string Reason,
    InputCommand? Command = null)
{
    public static ActionDecision None(string reason = "") => new(ActionDecisionKind.None, reason);
    public static ActionDecision Wait(string reason) => new(ActionDecisionKind.Wait, reason);
    public static ActionDecision SuppressDuplicate(string reason) => new(ActionDecisionKind.SuppressDuplicate, reason);
    public static ActionDecision Completed(string reason) => new(ActionDecisionKind.Completed, reason);
    public static ActionDecision Failed(string reason) => new(ActionDecisionKind.Failed, reason);
}