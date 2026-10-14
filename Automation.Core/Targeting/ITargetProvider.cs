namespace Automation.Core.Targeting;

public interface ITargetProvider
{
    string ProviderId { get; }
    TargetKind TargetKind { get; }

    ValueTask<IReadOnlyList<TargetCandidate>> DiscoverAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ITargetSession> OpenAsync(
        TargetBinding binding,
        CancellationToken cancellationToken = default);
}