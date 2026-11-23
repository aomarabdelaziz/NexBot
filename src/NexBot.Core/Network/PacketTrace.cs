using System.Text;
using NexBot.Protocol;

namespace NexBot.Network;

/// <summary>
/// The last packets that went through the proxy (server to client, client to server, bot to server), kept so they can
/// be written to the log when the game client drops: the packet the client could not handle is among the last ones.
/// Thread-safe; cheap enough to run all the time (a fixed ring, payloads cut short).
/// </summary>
internal sealed class PacketTrace
{
    public const int Capacity = 40;
    private const int MaxBytes = 48;

    private readonly Lock _lock = new();
    private readonly (DateTime Time, string Direction, ushort Opcode, int Length, string Hex)[] _ring = new (DateTime, string, ushort, int, string)[Capacity];
    private int _next;
    private int _count;

    public void Add(string direction, Packet packet)
    {
        var payload = packet.Payload;
        string hex = Convert.ToHexString(payload[..Math.Min(payload.Length, MaxBytes)]);
        lock (_lock)
        {
            _ring[_next] = (DateTime.Now, direction, packet.Opcode, payload.Length, hex);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    /// <summary>The kept packets, oldest first, one per line.</summary>
    public string Dump()
    {
        var text = new StringBuilder();
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                var (time, direction, opcode, length, hex) = _ring[(_next - _count + i + Capacity) % Capacity];
                text.Append($"  {time:HH:mm:ss.fff} {direction,-6} 0x{opcode:X4} ({length} bytes) {hex}");
                if (length > MaxBytes)
                    text.Append('…');
                text.AppendLine();
            }
        }
        return text.ToString().TrimEnd();
    }
}
