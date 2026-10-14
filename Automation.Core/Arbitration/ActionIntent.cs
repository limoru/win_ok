namespace Automation.Core.Arbitration;

public readonly record struct IntentId(Guid Value)
{
    public static IntentId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct CooldownKey(string Value)
{
    public override string ToString() => Value;
}

public static class CooldownKeys
{
    public const string SkipCurrentContentValue = "skip-current-content";
    public const string CenterConfirmationValue = "center-confirmation";

    public static readonly CooldownKey SkipCurrentContent = new(SkipCurrentContentValue);
    public static readonly CooldownKey CenterConfirmation = new(CenterConfirmationValue);
}

public abstract record ActionIntent(
    IntentId Id,
    CooldownKey CooldownKey,
    long FrameSequence,
    PageFingerprint SourcePageFingerprint,
    string Reason);

public sealed record SkipCurrentContentIntent(
    IntentId Id,
    CooldownKey CooldownKey,
    long FrameSequence,
    PageFingerprint SourcePageFingerprint,
    string Reason)
    : ActionIntent(Id, CooldownKey, FrameSequence, SourcePageFingerprint, Reason);

public sealed record ConfirmCenterDialogIntent(
    IntentId Id,
    CooldownKey CooldownKey,
    long FrameSequence,
    PageFingerprint SourcePageFingerprint,
    string Reason)
    : ActionIntent(Id, CooldownKey, FrameSequence, SourcePageFingerprint, Reason);