namespace Kes.Tasks;

internal abstract record ChaosCommand(
    string Reason,
    DateTimeOffset DispatchedAt);

internal sealed record EscapeCommand(
    string Reason,
    DateTimeOffset DispatchedAt) : ChaosCommand(Reason, DispatchedAt);

internal sealed class ChaosCommandBus
{
    private readonly object _gate = new();
    private readonly Queue<ChaosCommand> _commands = new();

    public void Dispatch(ChaosCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            _commands.Enqueue(command);
        }
    }

    public bool HasPendingEscape
    {
        get
        {
            lock (_gate)
            {
                return _commands.Count > 0 && _commands.Peek() is EscapeCommand;
            }
        }
    }

    public bool TryConsumeEscape(out EscapeCommand? command)
    {
        lock (_gate)
        {
            if (_commands.Count > 0 && _commands.Peek() is EscapeCommand escape)
            {
                _commands.Dequeue();
                command = escape;
                return true;
            }
        }

        command = null;
        return false;
    }
}