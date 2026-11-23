using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace NexBot.Protocol;

/// <summary>
/// A Silkroad packet: an opcode plus a little-endian payload.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the legacy <c>Packet</c>/<c>PacketReader</c>/<c>PacketWriter</c> trio, which wrapped a
/// <c>MemoryStream</c> + <c>BinaryReader</c>/<c>BinaryWriter</c> and took a lock on every single read or write.
/// This implementation works directly on a byte array with <see cref="BinaryPrimitives"/>, so reading a field is
/// a bounds check plus a load.
/// </para>
/// <para>
/// A packet is not thread-safe. Incoming packets are owned by the session's receive loop; outgoing packets are
/// built by one caller and then handed to the connection.
/// </para>
/// </remarks>
public sealed class Packet
{
    private const int DefaultCapacity = 64;

    private byte[] _buffer;
    private int _length;
    private int _position;

    /// <summary>Creates an empty packet for writing.</summary>
    public Packet(ushort opcode, bool encrypted = false, bool massive = false)
    {
        if (encrypted && massive)
            throw new ArgumentException("Packets cannot be both massive and encrypted.");
        Opcode = opcode;
        Encrypted = encrypted;
        Massive = massive;
        _buffer = new byte[DefaultCapacity];
    }

    /// <summary>Creates a packet over a copy of <paramref name="payload"/> (used for received packets).</summary>
    public Packet(ushort opcode, ReadOnlySpan<byte> payload, bool encrypted = false, bool massive = false)
    {
        Opcode = opcode;
        Encrypted = encrypted;
        Massive = massive;
        _buffer = payload.ToArray();
        _length = payload.Length;
    }

    public ushort Opcode { get; }
    public bool Encrypted { get; }
    public bool Massive { get; }

    /// <summary>Total payload length in bytes.</summary>
    public int Length => _length;

    /// <summary>Current read offset.</summary>
    public int Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, _length);
            _position = value;
        }
    }

    /// <summary>Bytes left to read.</summary>
    public int Remaining => _length - _position;

    /// <summary>The payload written or received so far.</summary>
    public ReadOnlySpan<byte> Payload => _buffer.AsSpan(0, _length);

    /// <summary>Returns a copy of the payload.</summary>
    public byte[] ToArray() => Payload.ToArray();

    #region Reading

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int count)
    {
        if ((uint)count > (uint)(_length - _position))
            ThrowEndOfPacket(count);
        var span = new ReadOnlySpan<byte>(_buffer, _position, count);
        _position += count;
        return span;
    }

    private void ThrowEndOfPacket(int count) =>
        throw new PacketException(string.Create(CultureInfo.InvariantCulture,
            $"Tried to read {count} byte(s) at offset {_position} of packet 0x{Opcode:X4} (length {_length})."));

    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];
    public bool ReadBool() => Take(1)[0] != 0;
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Reads <paramref name="count"/> raw bytes (copied).</summary>
    public byte[] ReadBytes(int count) => Take(count).ToArray();

    /// <summary>Returns a view over the next <paramref name="count"/> bytes without copying.</summary>
    public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

    /// <summary>Reads a <c>ushort</c>-length-prefixed string in the configured ANSI code page.</summary>
    public string ReadAscii() => ReadAscii(SroEncoding.Ascii);

    public string ReadAscii(Encoding encoding)
    {
        int length = ReadUInt16();
        return length == 0 ? string.Empty : encoding.GetString(Take(length));
    }

    /// <summary>Reads a <c>ushort</c>-character-count-prefixed UTF-16LE string.</summary>
    public string ReadUnicode()
    {
        int length = ReadUInt16();
        return length == 0 ? string.Empty : Encoding.Unicode.GetString(Take(length * 2));
    }

    public uint[] ReadUInt32Array(int count)
    {
        var values = new uint[count];
        for (int i = 0; i < values.Length; i++)
            values[i] = ReadUInt32();
        return values;
    }

    #endregion

    #region Writing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Grow(int count)
    {
        int required = _length + count;
        if (required > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        var span = new Span<byte>(_buffer, _length, count);
        _length = required;
        return span;
    }

    public Packet WriteByte(byte value) { Grow(1)[0] = value; return this; }
    public Packet WriteSByte(sbyte value) { Grow(1)[0] = (byte)value; return this; }
    public Packet WriteBool(bool value) { Grow(1)[0] = value ? (byte)1 : (byte)0; return this; }
    public Packet WriteUInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value); return this; }
    public Packet WriteInt16(short value) { BinaryPrimitives.WriteInt16LittleEndian(Grow(2), value); return this; }
    public Packet WriteUInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value); return this; }
    public Packet WriteInt32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value); return this; }
    public Packet WriteUInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value); return this; }
    public Packet WriteInt64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value); return this; }
    public Packet WriteSingle(float value) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value); return this; }
    public Packet WriteDouble(double value) { BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), value); return this; }

    public Packet WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Grow(value.Length));
        return this;
    }

    /// <summary>Writes a <c>ushort</c>-length-prefixed string in the configured ANSI code page.</summary>
    public Packet WriteAscii(string value) => WriteAscii(value, SroEncoding.Ascii);

    public Packet WriteAscii(string value, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = encoding.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ArgumentException("String is too long for a Silkroad packet.", nameof(value));
        WriteUInt16((ushort)byteCount);
        encoding.GetBytes(value, Grow(byteCount));
        return this;
    }

    /// <summary>Writes a <c>ushort</c>-character-count-prefixed UTF-16LE string.</summary>
    public Packet WriteUnicode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteUInt16((ushort)value.Length);
        Encoding.Unicode.GetBytes(value, Grow(value.Length * 2));
        return this;
    }

    #endregion

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Opcode:X4}][{Convert.ToHexString(Payload)}]");
}
