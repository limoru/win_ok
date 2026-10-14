namespace Automation.Core.Vision;

public sealed record FullFrameOcrObservation(
    string SessionId,
    long FrameSequence,
    DateTimeOffset CapturedAt,
    int Width,
    int Height,
    OcrFrameResult Recognition);