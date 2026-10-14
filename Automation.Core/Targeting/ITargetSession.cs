using Automation.Core.Frames;

namespace Automation.Core.Targeting;

public interface ITargetSession : IAsyncDisposable
{
    TargetBinding Binding { get; }
    TargetCapabilities Capabilities { get; }
    IFrameSource Frames { get; }
    IInputSink Input { get; }
    string? LastError { get; }
}