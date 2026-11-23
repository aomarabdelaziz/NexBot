using System.Buffers;
using System.Buffers.Binary;

namespace NexBot.Protocol;

/// <summary>Security options negotiated by the server in the first <c>0x5000</c> packet.</summary>
[Flags]
public enum SecurityFlags : byte
{
    None = 0x01,
    Blowfish = 0x02,
    SecurityBytes = 0x04,
    Handshake = 0x08,
    HandshakeResponse = 0x10,
}

/// <summary>
/// Implementation of the Silkroad Online security layer (client and server role): framing, Blowfish encryption, the
/// Diffie-Hellman style handshake, and the per-packet count and CRC bytes.
/// </summary>
/// <remarks>
/// <para>
/// This is a port of the client role ("clientless" mode) of the well-known <c>SilkroadSecurityApi</c> by
/// pushedx (with the security byte algorithms by jMerlin). Its wire output is byte-for-byte compatible.
/// That is checked by the test-suite against the original implementation.
/// </para>
/// <para>Differences from the legacy implementation:</para>
/// <list type="bullet">
///   <item>Frames are parsed straight from the socket buffer. Data is copied only when a frame is split across
///   reads.</item>
///   <item>Decryption happens in place in a pooled buffer, and outgoing frames are written into a caller-supplied
///   <see cref="IBufferWriter{T}"/>, so many packets can go out in a single socket send.</item>
///   <item>The server role (<see cref="GenerateServerHandshake"/>) is used by the NexBot proxy to talk to the real
///   game client. Unlike the original it does not verify the client's count/CRC bytes.</item>
/// </list>
/// <para>
/// Thread-safety: <see cref="Send"/> may be called from any thread. <see cref="Receive"/> and
/// <see cref="WriteOutgoing"/> are meant to be called by the connection's receive and send loops. All state is
/// protected by a single lock, which is cheap because it is almost never contended.
/// </para>
/// </remarks>
public sealed class SroSecurity
{
    private const ushort OpcodeHandshake = 0x5000;
    private const ushort OpcodeHandshakeAccept = 0x9000;
    private const ushort OpcodeIdentity = 0x2001;
    private const ushort OpcodeMassive = 0x600D;
    private const int MassiveChunkSize = 4089;

    private static readonly uint[] s_crcTable = GenerateSecurityTable();

    private readonly Lock _lock = new();
    private readonly Blowfish _blowfish = new();
    private readonly List<Packet> _outgoing = new(16);
    private readonly HashSet<ushort> _encryptedOpcodes = [0x2001, 0x6100, 0x6101, 0x6102, 0x6103, 0x6107];

    // Handshake values (naming follows the original API to keep the algorithm easy to audit).
    private uint _valueX, _valueG, _valueP, _valueA, _valueB, _valueK;
    private ulong _handshakeBlowfishKey;
    private ulong _clientKey;
    private readonly byte[] _countByteSeeds = new byte[3];
    private uint _crcSeed;

    private SecurityFlags _flags;
    private bool _flagsInitialized;
    private bool _startedHandshake;
    private bool _acceptedHandshake;
    private bool _isServer;
    private ulong _challengeKey;

    private string _identityName = "SR_Client";
    private byte _identityFlag;

    // Partial frame buffering (only used when a frame spans multiple socket reads).
    private byte[] _pending = new byte[8192];
    private int _pendingCount;

    // Reassembly of 0x600D "massive" packets.
    private ushort _massiveRemaining;
    private Packet? _massivePacket;

    /// <summary>True once the handshake is complete and regular packets may flow.</summary>
    public bool HandshakeAccepted
    {
        get { lock (_lock) return _acceptedHandshake; }
    }

    /// <summary>Security options the server selected (valid after the first handshake packet).</summary>
    public SecurityFlags Flags
    {
        get { lock (_lock) return _flags; }
    }

    /// <summary>Changes the data of the <c>0x2001</c> identity packet sent once the handshake is accepted.</summary>
    public void ChangeIdentity(string name, byte flag)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
        {
            _identityName = name;
            _identityFlag = flag;
        }
    }

    /// <summary>
    /// Switches this object to the server role and queues the initial <c>0x5000</c> handshake packet.
    /// Call it once, right after a game client connected.
    /// </summary>
    public void GenerateServerHandshake(bool blowfish = true, bool securityBytes = true, bool handshake = true)
    {
        lock (_lock)
        {
            if (_flagsInitialized)
                throw new SecurityException("The security flags were already initialized.");

            var flags = SecurityFlags.None;
            if (blowfish || securityBytes || handshake)
                flags = 0;
            if (blowfish) flags |= SecurityFlags.Blowfish;
            if (securityBytes) flags |= SecurityFlags.SecurityBytes;
            if (handshake) flags |= SecurityFlags.Handshake;

            _isServer = true;
            _flags = flags;
            _flagsInitialized = true;

            var response = new Packet(OpcodeHandshake);
            response.WriteByte((byte)flags);

            if (blowfish)
            {
                ulong initialKey = NextUInt64();
                _blowfish.Initialize(initialKey);
                response.WriteUInt64(initialKey);
            }

            if (securityBytes)
            {
                uint seedCount = (byte)Random.Shared.Next(256);
                SetupCountByte(seedCount);
                _crcSeed = (byte)Random.Shared.Next(256);
                response.WriteUInt32(seedCount);
                response.WriteUInt32(_crcSeed);
            }

            if (handshake)
            {
                _handshakeBlowfishKey = NextUInt64();
                _valueX = (uint)Random.Shared.Next() & 0x7FFFFFFF;
                _valueG = (uint)Random.Shared.Next() & 0x7FFFFFFF;
                _valueP = (uint)Random.Shared.Next() & 0x7FFFFFFF;
                _valueA = GPowXModP(_valueP, _valueX, _valueG);

                response.WriteUInt64(_handshakeBlowfishKey);
                response.WriteUInt32(_valueG);
                response.WriteUInt32(_valueP);
                response.WriteUInt32(_valueA);
            }

            _outgoing.Add(response);
        }
    }

    private static ulong NextUInt64()
    {
        Span<byte> buffer = stackalloc byte[8];
        Random.Shared.NextBytes(buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    /// <summary>Registers an opcode that must always be sent encrypted.</summary>
    public void AddEncryptedOpcode(ushort opcode)
    {
        lock (_lock) _encryptedOpcodes.Add(opcode);
    }

    /// <summary>Queues a packet. It is formatted during the next <see cref="WriteOutgoing"/>.</summary>
    public void Send(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Opcode is OpcodeHandshake or OpcodeHandshakeAccept)
            throw new SecurityException("Handshake packets cannot be sent through Send().");
        lock (_lock) _outgoing.Add(packet);
    }

    /// <summary>True when there are packets that can be written right now.</summary>
    public bool HasPendingOutgoing
    {
        get
        {
            lock (_lock) return CanSendFront();
        }
    }

    #region Receiving

    /// <summary>
    /// Feeds raw socket bytes into the security layer. Complete packets are appended to <paramref name="output"/>.
    /// </summary>
    /// <returns>True if the call queued handshake responses that must be flushed.</returns>
    public bool Receive(ReadOnlySpan<byte> data, List<Packet> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_lock)
        {
            int outgoingBefore = _outgoing.Count;

            if (_pendingCount == 0)
            {
                int consumed = ParseFrames(data, output);
                StorePending(data[consumed..]);
            }
            else
            {
                EnsurePendingCapacity(_pendingCount + data.Length);
                data.CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += data.Length;

                int consumed = ParseFrames(_pending.AsSpan(0, _pendingCount), output);
                int left = _pendingCount - consumed;
                if (left > 0 && consumed > 0)
                    _pending.AsSpan(consumed, left).CopyTo(_pending);
                _pendingCount = left;
            }

            return _outgoing.Count != outgoingBefore;
        }
    }

    private void StorePending(ReadOnlySpan<byte> leftover)
    {
        if (leftover.IsEmpty)
            return;
        EnsurePendingCapacity(leftover.Length);
        leftover.CopyTo(_pending);
        _pendingCount = leftover.Length;
    }

    private void EnsurePendingCapacity(int size)
    {
        if (size > _pending.Length)
            Array.Resize(ref _pending, Math.Max(size, _pending.Length * 2));
    }

    private int ParseFrames(ReadOnlySpan<byte> input, List<Packet> output)
    {
        int offset = 0;
        while (input.Length - offset >= 2)
        {
            int header = BinaryPrimitives.ReadUInt16LittleEndian(input[offset..]);
            int payloadSize = header & 0x7FFF;
            bool physicallyEncrypted = (header & 0x8000) != 0 && (_flags & SecurityFlags.Blowfish) != 0;
            int frameSize = physicallyEncrypted
                ? 2 + Blowfish.GetOutputLength(payloadSize + 4)
                : payloadSize + 6;

            if (input.Length - offset < frameSize)
                break;

            ProcessFrame(input.Slice(offset, frameSize), payloadSize, physicallyEncrypted, output);
            offset += frameSize;
        }
        return offset;
    }

    private void ProcessFrame(ReadOnlySpan<byte> frame, int payloadSize, bool encrypted, List<Packet> output)
    {
        if (!encrypted)
        {
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]);
            Dispatch(opcode, frame.Slice(6, payloadSize), encrypted: false, output);
            return;
        }

        int cipherLength = frame.Length - 2;
        byte[] rented = ArrayPool<byte>.Shared.Rent(cipherLength);
        try
        {
            var plain = rented.AsSpan(0, cipherLength);
            frame[2..].CopyTo(plain);
            _blowfish.DecryptInPlace(plain);
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(plain);
            Dispatch(opcode, plain.Slice(4, payloadSize), encrypted: true, output);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Dispatch(ushort opcode, ReadOnlySpan<byte> payload, bool encrypted, List<Packet> output)
    {
        switch (opcode)
        {
            case OpcodeHandshake:
            case OpcodeHandshakeAccept:
                if (encrypted)
                    throw new SecurityException("Received an illogical (encrypted) handshake packet.");
                Handshake(opcode, new Packet(opcode, payload));
                break;

            case OpcodeMassive:
                HandleMassive(payload, output);
                break;

            default:
                output.Add(new Packet(opcode, payload, encrypted));
                break;
        }
    }

    private void HandleMassive(ReadOnlySpan<byte> payload, List<Packet> output)
    {
        if (payload.IsEmpty)
            throw new PacketException("Received an empty 0x600D packet.");

        if (payload[0] == 1)
        {
            if (payload.Length < 5)
                throw new PacketException("Received a malformed 0x600D header.");
            _massiveRemaining = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
            ushort containedOpcode = BinaryPrimitives.ReadUInt16LittleEndian(payload[3..]);
            _massivePacket = new Packet(containedOpcode, encrypted: false, massive: true);
            return;
        }

        if (_massivePacket is null)
            throw new PacketException("Received a 0x600D data packet without a header.");

        _massivePacket.WriteBytes(payload[1..]);
        if (--_massiveRemaining == 0)
        {
            output.Add(_massivePacket);
            _massivePacket = null;
        }
    }

    private void Handshake(ushort opcode, Packet packet)
    {
        if (_isServer)
        {
            ServerHandshake(opcode, packet);
            return;
        }

        if (opcode != OpcodeHandshake)
            throw new SecurityException("Received an illogical handshake packet (0x9000 from server).");

        var flags = (SecurityFlags)packet.ReadByte();
        if (!_flagsInitialized)
        {
            _flags = flags;
            _flagsInitialized = true;
        }

        if ((flags & SecurityFlags.Blowfish) != 0)
        {
            ulong initialKey = packet.ReadUInt64();
            _blowfish.Initialize(initialKey);
        }

        if ((flags & SecurityFlags.SecurityBytes) != 0)
        {
            uint seedCount = packet.ReadUInt32();
            _crcSeed = packet.ReadUInt32();
            SetupCountByte(seedCount);
        }

        if ((flags & SecurityFlags.Handshake) != 0)
        {
            _handshakeBlowfishKey = packet.ReadUInt64();
            _valueG = packet.ReadUInt32();
            _valueP = packet.ReadUInt32();
            _valueA = packet.ReadUInt32();

            _valueX = (uint)Random.Shared.Next() & 0x7FFFFFFF;
            _valueB = GPowXModP(_valueP, _valueX, _valueG);
            _valueK = GPowXModP(_valueP, _valueX, _valueA);

            ulong keyArray = MakeULong(_valueA, _valueB);
            KeyTransformValue(ref keyArray, _valueK, (byte)(_valueK & 0x03));
            _blowfish.Initialize(keyArray);

            _clientKey = MakeULong(_valueB, _valueA);
            KeyTransformValue(ref _clientKey, _valueK, (byte)(_valueB & 0x07));
            _clientKey = _blowfish.Encrypt(_clientKey);
        }

        if ((flags & SecurityFlags.HandshakeResponse) != 0)
        {
            ulong challengeKey = packet.ReadUInt64();

            ulong expected = MakeULong(_valueA, _valueB);
            KeyTransformValue(ref expected, _valueK, (byte)(_valueA & 0x07));
            expected = _blowfish.Encrypt(expected);

            if (challengeKey != expected)
                throw new SecurityException("Server signature error.");

            KeyTransformValue(ref _handshakeBlowfishKey, _valueK, 0x3);
            _blowfish.Initialize(_handshakeBlowfishKey);
        }

        if ((flags & SecurityFlags.Handshake) != 0 && (flags & SecurityFlags.HandshakeResponse) == 0)
        {
            if (_startedHandshake || _acceptedHandshake)
                throw new SecurityException("Received an illogical handshake packet (duplicate 0x5000).");

            var challenge = new Packet(OpcodeHandshake);
            challenge.WriteUInt32(_valueB);
            challenge.WriteUInt64(_clientKey);
            _outgoing.Insert(0, challenge);
            _startedHandshake = true;
        }
        else
        {
            if (_acceptedHandshake)
                throw new SecurityException("Received an illogical handshake packet (duplicate 0x5000).");

            var identity = new Packet(OpcodeIdentity, encrypted: true);
            identity.WriteAscii(_identityName);
            identity.WriteByte(_identityFlag);

            // 0x9000 must go first, then the 0x2001 identity, then anything the user queued.
            _outgoing.Insert(0, identity);
            _outgoing.Insert(0, new Packet(OpcodeHandshakeAccept));

            _startedHandshake = true;
            _acceptedHandshake = true;
        }
    }

    /// <summary>Server role: handles the client's <c>0x5000</c> challenge and its <c>0x9000</c> accept.</summary>
    private void ServerHandshake(ushort opcode, Packet packet)
    {
        bool usesHandshake = (_flags & SecurityFlags.Handshake) != 0;

        if (opcode == OpcodeHandshakeAccept)
        {
            if (usesHandshake && !_startedHandshake)
                throw new SecurityException("Received an illogical handshake packet (out of order 0x9000).");
            if (_acceptedHandshake)
                throw new SecurityException("Received an illogical handshake packet (duplicate 0x9000).");
            _acceptedHandshake = true;
            return;
        }

        if (!usesHandshake)
            throw new SecurityException("Received an illogical handshake packet (0x5000 with no handshake).");
        if (_startedHandshake)
            throw new SecurityException("Received an illogical handshake packet (duplicate 0x5000).");
        _startedHandshake = true;

        _valueB = packet.ReadUInt32();
        _clientKey = packet.ReadUInt64();
        _valueK = GPowXModP(_valueP, _valueX, _valueB);

        ulong keyArray = MakeULong(_valueA, _valueB);
        KeyTransformValue(ref keyArray, _valueK, (byte)(_valueK & 0x03));
        _blowfish.Initialize(keyArray);

        ulong clientKey = _blowfish.Decrypt(_clientKey);
        ulong expected = MakeULong(_valueB, _valueA);
        KeyTransformValue(ref expected, _valueK, (byte)(_valueB & 0x07));
        if (clientKey != expected)
            throw new SecurityException("Client signature error.");

        _challengeKey = MakeULong(_valueA, _valueB);
        KeyTransformValue(ref _challengeKey, _valueK, (byte)(_valueA & 0x07));
        _challengeKey = _blowfish.Encrypt(_challengeKey);

        KeyTransformValue(ref _handshakeBlowfishKey, _valueK, 0x3);
        _blowfish.Initialize(_handshakeBlowfishKey);

        var response = new Packet(OpcodeHandshake);
        response.WriteByte((byte)SecurityFlags.HandshakeResponse);
        response.WriteUInt64(_challengeKey);

        // Ahead of anything queued by the user (e.g. the 0x2001 identity): those wait for the handshake.
        int index = 0;
        while (index < _outgoing.Count && _outgoing[index].Opcode is OpcodeHandshake or OpcodeHandshakeAccept)
            index++;
        _outgoing.Insert(index, response);
    }

    #endregion

    #region Sending

    /// <summary>
    /// Formats every packet that may be sent right now into <paramref name="writer"/>, in order.
    /// </summary>
    /// <returns>Number of packets written.</returns>
    public int WriteOutgoing(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (_lock)
        {
            int written = 0;
            while (CanSendFront())
            {
                var packet = _outgoing[0];
                _outgoing.RemoveAt(0);

                if (packet.Massive)
                {
                    WriteMassive(writer, packet);
                }
                else
                {
                    // Like the original API, only the client role encrypts by opcode; the server role keeps the flag.
                    bool encrypted = packet.Encrypted || (!_isServer && _encryptedOpcodes.Contains(packet.Opcode));
                    WriteFrame(writer, packet.Opcode, packet.Payload, encrypted);
                }
                written++;
            }
            return written;
        }
    }

    private bool CanSendFront()
    {
        if (_outgoing.Count == 0)
            return false;
        if (_acceptedHandshake)
            return true;
        ushort opcode = _outgoing[0].Opcode;
        return opcode is OpcodeHandshake or OpcodeHandshakeAccept;
    }

    private void WriteMassive(IBufferWriter<byte> writer, Packet packet)
    {
        var data = packet.Payload;
        int parts = (data.Length + MassiveChunkSize - 1) / MassiveChunkSize;

        Span<byte> header = stackalloc byte[5];
        header[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header[1..], (ushort)parts);
        BinaryPrimitives.WriteUInt16LittleEndian(header[3..], packet.Opcode);
        WriteFrame(writer, OpcodeMassive, header, encrypted: false);

        byte[] chunk = ArrayPool<byte>.Shared.Rent(MassiveChunkSize + 1);
        try
        {
            for (int offset = 0; offset < data.Length; offset += MassiveChunkSize)
            {
                int size = Math.Min(MassiveChunkSize, data.Length - offset);
                chunk[0] = 0;
                data.Slice(offset, size).CopyTo(chunk.AsSpan(1));
                WriteFrame(writer, OpcodeMassive, chunk.AsSpan(0, size + 1), encrypted: false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Formats a single frame (header, security bytes, optional Blowfish).</summary>
    private void WriteFrame(IBufferWriter<byte> writer, ushort opcode, ReadOnlySpan<byte> data, bool encrypted)
    {
        if (data.Length >= 0x8000)
            throw new PacketException("Payload is too large.");

        bool blowfish = (_flags & SecurityFlags.Blowfish) != 0;
        bool securityBytes = (_flags & SecurityFlags.SecurityBytes) != 0;
        bool physicallyEncrypt = encrypted && blowfish;
        bool markEncrypted = encrypted && (blowfish || securityBytes);

        int plainLength = 6 + data.Length;
        int frameLength = physicallyEncrypt ? 2 + Blowfish.GetOutputLength(4 + data.Length) : plainLength;

        Span<byte> frame = writer.GetSpan(frameLength)[..frameLength];
        ushort size = (ushort)data.Length;

        BinaryPrimitives.WriteUInt16LittleEndian(frame, markEncrypted ? (ushort)(size | 0x8000) : size);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[2..], opcode);
        frame[4] = 0;
        frame[5] = 0;
        data.CopyTo(frame[6..]);
        frame[plainLength..].Clear(); // Blowfish padding must be zero.

        // Only the client role stamps count/CRC bytes; servers send zeros.
        if (securityBytes && !_isServer)
        {
            frame[4] = GenerateCountByte();
            frame[5] = GenerateCheckByte(frame[..plainLength]);
        }

        if (physicallyEncrypt)
        {
            _blowfish.EncryptInPlace(frame[2..]);
        }
        else if (markEncrypted)
        {
            // "Encrypted" with security bytes only: the flag is part of the CRC, but not sent.
            BinaryPrimitives.WriteUInt16LittleEndian(frame, size);
        }

        writer.Advance(frameLength);
    }

    #endregion

    #region Core security functions (jMerlin, "How to generate the security bytes for SRO")

    private static uint[] GenerateSecurityTable()
    {
        var table = new uint[0x10000];
        var seeds = ProtocolTables.SecurityTableBase;
        int index = 0;
        for (int edi = 0; edi < 1024; edi += 4)
        {
            uint edx = BinaryPrimitives.ReadUInt32LittleEndian(seeds[edi..]);
            for (uint ecx = 0; ecx < 256; ++ecx)
            {
                uint eax = ecx >> 1;
                if ((ecx & 1) != 0)
                    eax ^= edx;
                for (int bit = 0; bit < 7; ++bit)
                {
                    if ((eax & 1) != 0)
                        eax = (eax >> 1) ^ edx;
                    else
                        eax >>= 1;
                }
                table[index++] = eax;
            }
        }
        return table;
    }

    private static uint GenerateValue(ref uint val)
    {
        for (int i = 0; i < 32; ++i)
        {
            val = (((((((((((val >> 2) ^ val) >> 2) ^ val) >> 1) ^ val) >> 1) ^ val) >> 1) ^ val) & 1)
                  | ((((val & 1) << 31) | (val >> 1)) & 0xFFFFFFFE);
        }
        return val;
    }

    private void SetupCountByte(uint seed)
    {
        if (seed == 0)
            seed = 0x9ABFB3B6;
        uint mut = seed;
        uint mut1 = GenerateValue(ref mut);
        uint mut2 = GenerateValue(ref mut);
        uint mut3 = GenerateValue(ref mut);
        GenerateValue(ref mut);
        byte byte1 = (byte)((mut & 0xFF) ^ (mut3 & 0xFF));
        byte byte2 = (byte)((mut1 & 0xFF) ^ (mut2 & 0xFF));
        if (byte1 == 0) byte1 = 1;
        if (byte2 == 0) byte2 = 1;
        _countByteSeeds[0] = (byte)(byte1 ^ byte2);
        _countByteSeeds[1] = byte2;
        _countByteSeeds[2] = byte1;
    }

    private byte GenerateCountByte()
    {
        byte result = (byte)(_countByteSeeds[2] * (~_countByteSeeds[0] + _countByteSeeds[1]));
        result = (byte)(result ^ (result >> 4));
        _countByteSeeds[0] = result;
        return result;
    }

    private byte GenerateCheckByte(ReadOnlySpan<byte> stream)
    {
        uint checksum = 0xFFFFFFFF;
        uint moddedSeed = _crcSeed << 8;
        var table = s_crcTable;
        foreach (byte b in stream)
            checksum = (checksum >> 8) ^ table[moddedSeed + ((b ^ checksum) & 0xFF)];
        return (byte)(((checksum >> 24) & 0xFF) + ((checksum >> 8) & 0xFF) + ((checksum >> 16) & 0xFF) + (checksum & 0xFF));
    }

    private static uint GPowXModP(uint p, uint x, uint g)
    {
        if (x == 0)
            return 1;
        long result = 1;
        long mult = g;
        while (x != 0)
        {
            if ((x & 1) != 0)
                result = mult * result % p;
            x >>= 1;
            mult = mult * mult % p;
        }
        return (uint)result;
    }

    private static void KeyTransformValue(ref ulong value, uint key, byte keyByte)
    {
        Span<byte> stream = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(stream, value);
        for (int i = 0; i < 8; i++)
        {
            byte k = (byte)(key >> (8 * (i & 3)));
            stream[i] ^= (byte)(stream[i] + k + keyByte);
        }
        value = BinaryPrimitives.ReadUInt64LittleEndian(stream);
    }

    private static ulong MakeULong(uint low, uint high) => ((ulong)high << 32) | low;

    #endregion
}
