namespace Xyo.Sdk.Exceptions;

/// <summary>
/// Exception thrown when a transport layer failure occurs (connection drop, DNS resolution failure, socket timeout).
/// </summary>
public class XyoNetworkException : XyoException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="XyoNetworkException"/> class.
    /// </summary>
    /// <param name="message">The message describing the network error.</param>
    /// <param name="innerException">The underlying transport or socket exception.</param>
    public XyoNetworkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Network exceptions are considered transient transport failures and are safe to retry.
    /// </summary>
    public bool IsRetryable => true;
}
