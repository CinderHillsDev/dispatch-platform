namespace Dispatch.Core.Configuration;

/// <summary>
/// Pure decision logic for which SMTP ports to bind (spec §5). Kept separate from the listener so it can be
/// unit-tested without real sockets: callers pass a <paramref name="canBind"/> predicate. We prefer the
/// configured ports (25 + 587 by default) and fall back to <see cref="ListenerOptions.FallbackPort"/> (2525)
/// only when port 25 can't be bound (already in use or no privilege), or when nothing else bound.
/// </summary>
public static class SmtpPortResolver
{
    /// <param name="requested">Configured ports, in preference order.</param>
    /// <param name="canBind">Returns true if the given port can be bound right now.</param>
    /// <param name="warn">Optional sink for human-readable warnings about dropped ports / fallback.</param>
    /// <returns>The ports to actually listen on (may be empty if nothing can bind).</returns>
    public static int[] Resolve(int[] requested, Func<int, bool> canBind, Action<string>? warn = null)
    {
        var result = new List<int>();
        var dropped = new List<int>();
        foreach (var p in requested)
            (canBind(p) ? result : dropped).Add(p);

        foreach (var p in dropped)
            warn?.Invoke($"SMTP port {p} is unavailable (already in use or insufficient privilege) - skipping");

        // Fall back to 2525 only when port 25 was wanted but couldn't be bound, or nothing bound at all.
        if ((dropped.Contains(25) || result.Count == 0)
            && !result.Contains(ListenerOptions.FallbackPort)
            && canBind(ListenerOptions.FallbackPort))
        {
            warn?.Invoke($"Falling back to SMTP port {ListenerOptions.FallbackPort} (port 25 unavailable)");
            result.Add(ListenerOptions.FallbackPort);
        }

        return [.. result];
    }
}
