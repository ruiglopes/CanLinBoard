using System;

namespace CanBus;

public class ProtocolException : Exception
{
    public byte? StatusCode { get; }

    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, byte statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
