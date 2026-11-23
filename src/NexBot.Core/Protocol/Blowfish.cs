using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace NexBot.Protocol;

/// <summary>
/// Blowfish (ECB, little-endian 32-bit halves) exactly as used by Silkroad Online.
/// </summary>
/// <remarks>
/// Works in place on spans: no per-call allocations, unlike the legacy implementation which allocated a new array
/// for every packet plus two <c>BitConverter.GetBytes</c> arrays per 8-byte block.
/// </remarks>
public sealed class Blowfish
{
    private const int Rounds = 16;
    private readonly uint[] _p = new uint[Rounds + 2];
    private readonly uint[] _s = new uint[4 * 256];

    /// <summary>Rounds <paramref name="length"/> up to the 8-byte block size.</summary>
    public static int GetOutputLength(int length) => (length + 7) & ~7;

    /// <summary>Initializes the key schedule.</summary>
    public void Initialize(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Blowfish key cannot be empty.", nameof(key));

        ProtocolTables.BlowfishP.CopyTo(_p);
        ProtocolTables.BlowfishS.CopyTo(_s);

        int j = 0;
        for (int i = 0; i < Rounds + 2; i++)
        {
            uint data = (uint)key[j] << 24
                        | (uint)key[(j + 1) % key.Length] << 16
                        | (uint)key[(j + 2) % key.Length] << 8
                        | key[(j + 3) % key.Length];
            _p[i] ^= data;
            j = (j + 4) % key.Length;
        }

        uint l = 0, r = 0;
        for (int i = 0; i < Rounds + 2; i += 2)
        {
            Encipher(ref l, ref r);
            _p[i] = l;
            _p[i + 1] = r;
        }

        for (int i = 0; i < _s.Length; i += 2)
        {
            Encipher(ref l, ref r);
            _s[i] = l;
            _s[i + 1] = r;
        }
    }

    /// <summary>Convenience overload for 64-bit keys, serialized little-endian like <c>BitConverter.GetBytes</c>.</summary>
    public void Initialize(ulong key)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, key);
        Initialize(bytes);
    }

    /// <summary>Encrypts <paramref name="data"/> in place. Length must be a multiple of 8.</summary>
    public void EncryptInPlace(Span<byte> data)
    {
        EnsureBlockAligned(data.Length);
        for (int x = 0; x < data.Length; x += 8)
        {
            var block = data.Slice(x, 8);
            uint l = BinaryPrimitives.ReadUInt32LittleEndian(block);
            uint r = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            Encipher(ref l, ref r);
            BinaryPrimitives.WriteUInt32LittleEndian(block, l);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], r);
        }
    }

    /// <summary>Decrypts <paramref name="data"/> in place. Length must be a multiple of 8.</summary>
    public void DecryptInPlace(Span<byte> data)
    {
        EnsureBlockAligned(data.Length);
        for (int x = 0; x < data.Length; x += 8)
        {
            var block = data.Slice(x, 8);
            uint l = BinaryPrimitives.ReadUInt32LittleEndian(block);
            uint r = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            Decipher(ref l, ref r);
            BinaryPrimitives.WriteUInt32LittleEndian(block, l);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], r);
        }
    }

    /// <summary>Encrypts a single 64-bit value (used by the handshake).</summary>
    public ulong Encrypt(ulong value)
    {
        Span<byte> block = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(block, value);
        EncryptInPlace(block);
        return BinaryPrimitives.ReadUInt64LittleEndian(block);
    }

    /// <summary>Decrypts a single 64-bit value (used by the handshake).</summary>
    public ulong Decrypt(ulong value)
    {
        Span<byte> block = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(block, value);
        DecryptInPlace(block);
        return BinaryPrimitives.ReadUInt64LittleEndian(block);
    }

    private static void EnsureBlockAligned(int length)
    {
        if ((length & 7) != 0)
            throw new ArgumentException("Blowfish data length must be a multiple of 8.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint F(uint x)
    {
        var s = _s;
        return ((s[x >> 24] + s[256 + ((x >> 16) & 0xFF)]) ^ s[512 + ((x >> 8) & 0xFF)]) + s[768 + (x & 0xFF)];
    }

    private void Encipher(ref uint xl, ref uint xr)
    {
        var p = _p;
        uint l = xl ^ p[0];
        uint r = xr;
        for (int i = 1; i <= Rounds; i += 2)
        {
            r ^= F(l) ^ p[i];
            l ^= F(r) ^ p[i + 1];
        }
        r ^= p[Rounds + 1];
        xl = r;
        xr = l;
    }

    private void Decipher(ref uint xl, ref uint xr)
    {
        var p = _p;
        uint l = xl ^ p[Rounds + 1];
        uint r = xr;
        for (int i = Rounds; i >= 1; i -= 2)
        {
            r ^= F(l) ^ p[i];
            l ^= F(r) ^ p[i - 1];
        }
        r ^= p[0];
        xl = r;
        xr = l;
    }
}
