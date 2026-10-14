using Automation.Core.Frames;
using Automation.Core.Vision;

namespace Automation.Core.DeveloperTools;

public sealed record DecisionRegionSuggestion(
    string Id,
    string Label,
    string Why,
    double Left,
    double Top,
    double Right,
    double Bottom);

public sealed record DecisionCandidateAction(
    string Label,
    double? X,
    double? Y,
    string Source);

public sealed record DecisionSnapshotRequest(
    string SessionId,
    string ReasonId,
    string PageLabel,
    string Why,
    long FrameSequence,
    DateTimeOffset CapturedAt,
    IReadOnlyList<DecisionRegionSuggestion> SuggestedRegions,
    IReadOnlyList<DecisionCandidateAction> CandidateActions,
    string ContextLogs = "")
{
    public string DeduplicationKey => $"{SessionId}|{ReasonId}|{PageLabel}";
}

public sealed record DecisionSnapshotDefinition(
    string ReasonId,
    string PageLabel,
    string Why,
    IReadOnlyList<DecisionRegionSuggestion> SuggestedRegions)
{
    public DecisionSnapshotRequest CreateRequest(
        string sessionId,
        long frameSequence,
        DateTimeOffset capturedAt,
        IReadOnlyList<DecisionCandidateAction>? candidateActions = null,
        string contextLogs = "")
        => new(
            sessionId,
            ReasonId,
            PageLabel,
            Why,
            frameSequence,
            capturedAt,
            SuggestedRegions,
            candidateActions ?? [],
            contextLogs);
}

public interface IDecisionSnapshotSink
{
    bool IsEnabled { get; }

    ValueTask<string?> CaptureAsync(
        DecisionSnapshotRequest request,
        BgraFrame frame,
        OcrFrameResult? ocr,
        bool force = false,
        CancellationToken cancellationToken = default);
}

public sealed record DecisionSnapshotPixelRect(int X, int Y, int Width, int Height);

public sealed record DecisionSnapshotRegionMetadata(
    string Id,
    string Label,
    string Why,
    double Left,
    double Top,
    double Right,
    double Bottom,
    DecisionSnapshotPixelRect Pixels);

public sealed record DecisionSnapshotOcrLineMetadata(
    string Text,
    double Score,
    DecisionSnapshotPixelRect Region);

public sealed record DecisionSnapshotOcrMetadata(
    string Text,
    double Score,
    IReadOnlyList<DecisionSnapshotOcrLineMetadata> Lines);

public sealed record DecisionSnapshotMetadata(
    int SchemaVersion,
    string SessionId,
    string ReasonId,
    string PageLabel,
    string Why,
    string ContextLogs,
    long FrameSequence,
    DateTimeOffset CapturedAt,
    int FrameWidth,
    int FrameHeight,
    string OriginalFileName,
    string AnnotatedFileName,
    string Status,
    IReadOnlyList<DecisionCandidateAction> CandidateActions,
    IReadOnlyList<DecisionSnapshotRegionMetadata> Regions,
    DecisionSnapshotOcrMetadata? Ocr);

public static class DecisionSnapshotMetadataBuilder
{
    public static DecisionSnapshotMetadata Build(
        DecisionSnapshotRequest request,
        BgraFrame frame,
        OcrFrameResult? ocr)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);

        var regions = request.SuggestedRegions
            .Select(region => ToMetadata(region, frame.Width, frame.Height))
            .ToArray();
        var ocrMetadata = ocr is null
            ? null
            : new DecisionSnapshotOcrMetadata(
                ocr.Text,
                ocr.Score,
                ocr.Lines
                    .Select(line => new DecisionSnapshotOcrLineMetadata(
                        line.Text,
                        line.Score,
                        new DecisionSnapshotPixelRect(
                            line.Region.X,
                            line.Region.Y,
                            line.Region.Width,
                            line.Region.Height)))
                    .ToArray());

        return new DecisionSnapshotMetadata(
            1,
            request.SessionId,
            request.ReasonId,
            request.PageLabel,
            request.Why,
            request.ContextLogs,
            request.FrameSequence,
            request.CapturedAt,
            frame.Width,
            frame.Height,
            "original.png",
            "annotated.png",
            "pending-human-review",
            request.CandidateActions.ToArray(),
            regions,
            ocrMetadata);
    }

    private static DecisionSnapshotRegionMetadata ToMetadata(
        DecisionRegionSuggestion region,
        int frameWidth,
        int frameHeight)
    {
        var left = Math.Clamp(region.Left, 0, 1);
        var top = Math.Clamp(region.Top, 0, 1);
        var right = Math.Clamp(region.Right, left, 1);
        var bottom = Math.Clamp(region.Bottom, top, 1);
        var x = Math.Clamp((int)Math.Floor(left * frameWidth), 0, Math.Max(0, frameWidth - 1));
        var y = Math.Clamp((int)Math.Floor(top * frameHeight), 0, Math.Max(0, frameHeight - 1));
        var rightPixel = Math.Clamp(
            (int)Math.Ceiling(right * frameWidth),
            Math.Min(frameWidth, x + 1),
            frameWidth);
        var bottomPixel = Math.Clamp(
            (int)Math.Ceiling(bottom * frameHeight),
            Math.Min(frameHeight, y + 1),
            frameHeight);

        return new DecisionSnapshotRegionMetadata(
            region.Id,
            region.Label,
            region.Why,
            left,
            top,
            right,
            bottom,
            new DecisionSnapshotPixelRect(
                x,
                y,
                Math.Max(1, rightPixel - x),
                Math.Max(1, bottomPixel - y)));
    }
}