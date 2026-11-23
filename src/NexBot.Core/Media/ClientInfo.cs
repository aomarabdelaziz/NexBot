using System.Buffers.Binary;
using System.Text;
using NexBot.Protocol;

namespace NexBot.Media;

/// <summary>A division of DivisionInfo.txt with its gateway hosts.</summary>
public sealed record DivisionInfo(string Name, IReadOnlyList<string> Gateways);

/// <summary>Connection info stored in the client's Media.pk2 (SV.T, DivisionInfo.txt, Gateport.txt).</summary>
/// <param name="Gateways">Gateway hosts of every division, in file order.</param>
public sealed record ClientInfo(uint Version, byte Locale, IReadOnlyList<string> Gateways, ushort GatewayPort)
{
    /// <summary>Divisions of DivisionInfo.txt (the gateway index passed to the client counts inside one division).</summary>
    public IReadOnlyList<DivisionInfo> Divisions { get; init; } = [];

    /// <summary>Reads version, locale, gateway hosts and port from an opened Media.pk2.</summary>
    public static ClientInfo Read(Pk2Archive media)
    {
        ArgumentNullException.ThrowIfNull(media);
        byte locale = ReadLocale(media, out var divisions);
        return new ClientInfo(ReadVersion(media), locale, divisions.SelectMany(d => d.Gateways).ToList(), ReadGatePort(media))
        {
            Divisions = divisions,
        };
    }

    /// <summary>SV.T: int32 length + Blowfish("SILKROADVERSION") encrypted ASCII version.</summary>
    private static uint ReadVersion(Pk2Archive media)
    {
        byte[] raw = media.ReadBytes("SV.T");
        int length = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (length <= 0 || length > raw.Length - 4)
            throw new InvalidDataException("SV.T is malformed.");

        byte[] data = raw.AsSpan(4, Blowfish.GetOutputLength(length)).ToArray();
        // The legacy code initialized the key with "length" bytes of "SILKROADVERSION".
        byte[] key = Encoding.ASCII.GetBytes("SILKROADVERSION");
        var blowfish = new Blowfish();
        blowfish.Initialize(key.AsSpan(0, Math.Min(length, key.Length)));
        blowfish.DecryptInPlace(data);

        string text = Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length));
        return uint.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out uint version)
            ? version
            : throw new InvalidDataException($"SV.T version '{text}' is not a number.");
    }

    /// <summary>DivisionInfo.txt: locale byte, then divisions with their gateway hosts.</summary>
    private static byte ReadLocale(Pk2Archive media, out IReadOnlyList<DivisionInfo> divisions)
    {
        var p = new Packet(0, media.ReadBytes("DivisionInfo.txt"));
        byte locale = p.ReadByte();
        var list = new List<DivisionInfo>();
        byte divisionCount = p.ReadByte();
        for (int i = 0; i < divisionCount; i++)
        {
            string name = Encoding.ASCII.GetString(p.ReadSpan(p.ReadInt32())).TrimEnd('\0');
            p.ReadByte();
            var hosts = new List<string>();
            byte count = p.ReadByte();
            for (int j = 0; j < count; j++)
            {
                hosts.Add(Encoding.ASCII.GetString(p.ReadSpan(p.ReadInt32())).TrimEnd('\0'));
                p.ReadByte();
            }
            list.Add(new DivisionInfo(name, hosts));
        }
        divisions = list;
        return locale;
    }

    private static ushort ReadGatePort(Pk2Archive media)
    {
        string text = Encoding.ASCII.GetString(media.ReadBytes("Gateport.txt")).Trim('\0', ' ', '\r', '\n');
        return ushort.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out ushort port)
            ? port
            : throw new InvalidDataException($"Gateport.txt value '{text}' is not a port.");
    }
}

/// <summary>Decrypts <c>SkillDataEnc_*.txt</c> files (byte-wise additive cipher with two hash tables).</summary>
public static class SkillDataDecryptor
{
    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0xE2 && data[1] == 0xB0;

    /// <summary>Returns the decrypted bytes (or a copy of the input when it is not encrypted).</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        var output = data.ToArray();
        if (!IsEncrypted(data))
            return output;

        var hash1 = SkillDataTables.Hash1;
        var hash2 = SkillDataTables.Hash2;
        uint key = 0x8C1F;
        for (int i = 0; i < output.Length; i++, key++)
            output[i] = (byte)(output[i] + (byte)(hash1[(int)(key % 0xA7)] - hash2[(int)(key % 0x1EF)]));
        return output;
    }
}
