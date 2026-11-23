namespace NexBot.Protocol;

/// <summary>Raised when a packet is malformed or read past its end.</summary>
public class PacketException : Exception
{
    public PacketException() { }
    public PacketException(string message) : base(message) { }
    public PacketException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when the security handshake fails or receives packets in an illogical order.</summary>
public sealed class SecurityException : PacketException
{
    public SecurityException() { }
    public SecurityException(string message) : base(message) { }
    public SecurityException(string message, Exception innerException) : base(message, innerException) { }
}
