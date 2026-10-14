using Automation.Core.Configuration;
using Automation.Core.Runtime;
using Automation.Core.Targeting;

namespace Automation.Core.Sessions;

public sealed record SessionSnapshot(
    string SessionId,
    TargetBinding Binding,
    UiProfile UiProfile,
    SessionState State,
    string CurrentModule,
    SessionActivity Activity,
    string AccountLabel,
    string LastMessage,
    DateTimeOffset UpdatedAt);