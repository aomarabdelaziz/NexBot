using System.Buffers.Binary;
using System.IO.Compression;

namespace NexBot.Media;

/// <summary>
/// Converts Silkroad <c>.ddj</c> textures (a 20-byte JMXV header followed by a DDS file) to PNG.
/// </summary>
/// <remarks>
/// Pure managed code, so it runs on Linux too. The old MediaExtractor used DevIL + System.Drawing, which are
/// Windows-only. Supports DXT1, DXT3, DXT5 and uncompressed 16/24/32-bit RGB(A) surfaces, which covers the item
/// and skill icons. Only the first mip level is decoded.
/// </remarks>
public static class DdjImage
{
    private const int DdjHeaderSize = 20;

    /// <summary>Decodes a .ddj (or plain .dds) file to RGBA pixels.</summary>
    public static (int Width, int Height, byte[] Rgba) Decode(ReadOnlySpan<byte> file)
    {
        var dds = file.Length >= 4 && file[..4].SequenceEqual("DDS "u8) ? file : file[DdjHeaderSize..];
        if (dds.Length < 128 || !dds[..4].SequenceEqual("DDS "u8))
            throw new InvalidDataException("Not a DDS/DDJ image.");

        int height = BinaryPrimitives.ReadInt32LittleEndian(dds[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dds[16..]);
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096)
            throw new InvalidDataException($"Unsupported DDS size {width}x{height}.");

        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(dds[80..]);
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(dds[84..]);
        var data = dds[128..];
        var rgba = new byte[width * height * 4];

        if ((pfFlags & 0x4) != 0) // DDPF_FOURCC
        {
            switch (fourCc)
            {
                case 0x31545844: DecodeBlocks(data, width, height, rgba, 8, DecodeDxt1Block); break;  // "DXT1"
                case 0x33545844: DecodeBlocks(data, width, height, rgba, 16, DecodeDxt3Block); break; // "DXT3"
                case 0x35545844: DecodeBlocks(data, width, height, rgba, 16, DecodeDxt5Block); break; // "DXT5"
                default: throw new InvalidDataException($"Unsupported DDS compression 0x{fourCc:X8}.");
            }
        }
        else
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(dds[88..]);
            uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(dds[92..]);
            uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(dds[96..]);
            uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(dds[100..]);
            uint aMask = (pfFlags & 0x1) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(dds[104..]) : 0;
            DecodeUncompressed(data, width, height, bits, rMask, gMask, bMask, aMask, rgba);
        }
        return (width, height, rgba);
    }

    /// <summary>Decodes a .ddj/.dds file and encodes it as PNG.</summary>
    public static byte[] ToPng(ReadOnlySpan<byte> file)
    {
        var (w, h, rgba) = Decode(file);
        return Png.Encode(w, h, rgba);
    }

    #region Block compression

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<uint> pixels);

    private static void DecodeBlocks(ReadOnlySpan<byte> data, int width, int height, byte[] rgba, int blockSize, BlockDecoder decode)
    {
        Span<uint> pixels = stackalloc uint[16];
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int offset = (by * blocksX + bx) * blockSize;
                if (offset + blockSize > data.Length)
                    return;
                decode(data.Slice(offset, blockSize), pixels);
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width) break;
                        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan((y * width + x) * 4), pixels[py * 4 + px]);
                    }
                }
            }
        }
    }

    /// <summary>Packs RGBA into a little-endian uint (bytes R,G,B,A in memory).</summary>
    private static uint Pack(int r, int g, int b, int a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static void Rgb565(ushort c, out int r, out int g, out int b)
    {
        r = (c >> 11 & 0x1F) * 255 / 31;
        g = (c >> 5 & 0x3F) * 255 / 63;
        b = (c & 0x1F) * 255 / 31;
    }

    private static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<uint> pixels, bool allowTransparent)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Rgb565(c0, out int r0, out int g0, out int b0);
        Rgb565(c1, out int r1, out int g1, out int b1);

        Span<uint> palette = stackalloc uint[4];
        palette[0] = Pack(r0, g0, b0, 255);
        palette[1] = Pack(r1, g1, b1, 255);
        if (c0 > c1 || !allowTransparent)
        {
            palette[2] = Pack((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3, 255);
            palette[3] = Pack((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3, 255);
        }
        else
        {
            palette[2] = Pack((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
            palette[3] = 0; // transparent black
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int i = 0; i < 16; i++)
            pixels[i] = palette[(int)(indices >> (2 * i) & 3)];
    }

    private static void DecodeDxt1Block(ReadOnlySpan<byte> block, Span<uint> pixels) =>
        DecodeColorBlock(block, pixels, allowTransparent: true);

    private static void DecodeDxt3Block(ReadOnlySpan<byte> block, Span<uint> pixels)
    {
        DecodeColorBlock(block[8..], pixels, allowTransparent: false);
        ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(block);
        for (int i = 0; i < 16; i++)
        {
            uint a = (uint)(alpha >> (4 * i) & 0xF) * 17;
            pixels[i] = (pixels[i] & 0x00FFFFFF) | (a << 24);
        }
    }

    private static void DecodeDxt5Block(ReadOnlySpan<byte> block, Span<uint> pixels)
    {
        DecodeColorBlock(block[8..], pixels, allowTransparent: false);
        int a0 = block[0], a1 = block[1];
        Span<int> alphas = stackalloc int[8];
        alphas[0] = a0;
        alphas[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++)
                alphas[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        }
        else
        {
            for (int i = 1; i <= 4; i++)
                alphas[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            alphas[6] = 0;
            alphas[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
        {
            uint a = (uint)alphas[(int)(bits >> (3 * i) & 7)];
            pixels[i] = (pixels[i] & 0x00FFFFFF) | (a << 24);
        }
    }

    #endregion

    private static void DecodeUncompressed(ReadOnlySpan<byte> data, int width, int height, int bits,
        uint rMask, uint gMask, uint bMask, uint aMask, byte[] rgba)
    {
        int bytesPerPixel = bits / 8;
        if (bytesPerPixel is < 2 or > 4)
            throw new InvalidDataException($"Unsupported DDS bit depth {bits}.");

        for (int i = 0; i < width * height; i++)
        {
            int offset = i * bytesPerPixel;
            if (offset + bytesPerPixel > data.Length)
                break;
            uint value = bytesPerPixel switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]),
                3 => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]),
            };
            rgba[i * 4] = Channel(value, rMask);
            rgba[i * 4 + 1] = Channel(value, gMask);
            rgba[i * 4 + 2] = Channel(value, bMask);
            rgba[i * 4 + 3] = aMask == 0 ? (byte)255 : Channel(value, aMask);
        }
    }

    private static byte Channel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((value & mask) >> shift) * 255 / max);
    }

    /// <summary>Minimal PNG encoder (RGBA, 8 bits per channel, no filtering).</summary>
    internal static class Png
    {
        private static readonly uint[] s_crcTable = BuildCrcTable();

        public static byte[] Encode(int width, int height, byte[] rgba)
        {
            using var output = new MemoryStream();
            output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // RGBA
            WriteChunk(output, "IHDR"u8, ihdr);

            using var raw = new MemoryStream();
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // filter: none
                    zlib.Write(rgba, y * width * 4, width * 4);
                }
            }
            WriteChunk(output, "IDAT"u8, raw.ToArray());
            WriteChunk(output, "IEND"u8, []);
            return output.ToArray();
        }

        private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            output.Write(type);
            output.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            output.Write(crcBytes);
        }

        private static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
