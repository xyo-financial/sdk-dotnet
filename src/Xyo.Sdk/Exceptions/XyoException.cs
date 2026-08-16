namespace Xyo.Sdk.Exceptions;

/// <summary>
/// Base exception for all errors thrown by the XYO Financial SDK.
/// </summary>
public class XyoException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="XyoException"/> class.
    /// </summary>
    public XyoException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public XyoException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoException"/> class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public XyoException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
