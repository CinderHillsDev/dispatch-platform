namespace Dispatch.Core.Providers;

/// <summary>Thrown by a provider when a delivery failure is transient and the message should be retried.</summary>
public sealed class TransientRelayException : Exception
{
    public TransientRelayException(string message) : base(message) { }
    public TransientRelayException(string message, Exception inner) : base(message, inner) { }
}
