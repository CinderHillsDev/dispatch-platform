namespace Dispatch.Service;

/// <summary>
/// Tracks the number of live SMTP sessions (spec §5.3 max concurrent connections). Incremented on
/// SessionCreated and decremented on session completion/fault/cancellation by <see cref="SmtpListenerService"/>;
/// <see cref="CidrMailboxFilter"/> reads <see cref="Active"/> to refuse MAIL FROM once the cap is exceeded.
/// </summary>
public sealed class ConnectionTracker
{
    private int _active;

    public int Active => Volatile.Read(ref _active);

    public void Increment() => Interlocked.Increment(ref _active);

    public void Decrement()
    {
        // Never let the counter drift below zero if a decrement is ever double-fired.
        if (Interlocked.Decrement(ref _active) < 0) Interlocked.Exchange(ref _active, 0);
    }
}
