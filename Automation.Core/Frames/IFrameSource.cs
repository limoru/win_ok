namespace Automation.Core.Frames;

public interface IFrameSource
{
    ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default);

    ValueTask<FrameLease> WaitNextAsync(
        long afterSequence,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}