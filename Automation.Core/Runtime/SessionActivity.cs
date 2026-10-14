namespace Automation.Core.Runtime;

public enum SessionActivityKind
{
    Idle,
    Running,
    Waiting,
    Progress,
    Paused,
    Completed,
    Faulted
}

public sealed record SessionActivity(
    SessionActivityKind Kind,
    string TaskId,
    string TaskLabel,
    string StepId,
    string StepLabel,
    string Detail,
    int? Current,
    int? Total,
    DateTimeOffset UpdatedAt)
{
    public bool HasProgress => Current is int current && Total is int total && total > 0 && current >= 0;

    public string ProgressText => HasProgress ? $"{Current} / {Total}" : "-";

    public double ProgressValue => HasProgress ? Math.Clamp(Current!.Value * 100d / Total!.Value, 0, 100) : 0;

    public static SessionActivity Idle(string detail, DateTimeOffset? updatedAt = null)
        => new(
            SessionActivityKind.Idle,
            "idle",
            "等待任务",
            "idle",
            "暂无运行步骤",
            detail,
            null,
            null,
            updatedAt ?? DateTimeOffset.Now);
}

public interface IActivityReporter
{
    void Report(SessionActivity activity);
}