using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NexBot.Protocol;

namespace NexBot.Media;

/// <summary>A file inside a pk2 archive.</summary>
public sealed record Pk2File(string Path, string Name, long Offset, uint Size);

/// <summary>
/// Read-only access to a Silkroad <c>.pk2</c> archive (e.g. <c>Media.pk2</c>).
/// </summary>
/// <remarks>
/// <para>
/// Port of the MediaExtractor/xBot <c>Pk2Reader</c>. Differences from that version:
/// </para>
/// <list type="bullet">
///   <item>Entries are decoded with spans instead of <c>Marshal.PtrToStructure</c>. The legacy reader leaked an
///   unmanaged <c>AllocHGlobal</c> buffer for every 2560-byte block.</item>
///   <item>Reads use <see cref="RandomAccess"/>, so there is no shared stream position and concurrent reads are
///   safe.</item>
///   <item>Corrupt chains (cycles, offsets outside the file) are detected instead of recursing forever.</item>
/// </list>
/// <para>
/// Layout: a 256-byte plain header, then Blowfish-encrypted blocks of 20 entries of 128 bytes. Entry type
/// 1 = folder, 2 = file. Entry 19 of each block may chain to the next block of the same folder.
/// </para>
/// </remarks>
public sealed class Pk2Archive : IDisposable
{
    /// <summary>Default vSRO / iSRO archive password.</summary>
    public const string DefaultKey = "169841";

    private const int HeaderSize = 256;
    private const int EntrySize = 128;
    private const int EntriesPerBlock = 20;
    private const int BlockSize = EntrySize * EntriesPerBlock;
    private static readonly byte[] s_baseKey = [0x03, 0xF8, 0xE4, 0x44, 0x88, 0x99, 0x3F, 0x64, 0xFE, 0x35];

    private readonly SafeFileHandle _handle;
    private readonly Blowfish _blowfish = new();
    private readonly Dictionary<string, Pk2File> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

    public Pk2Archive(string path, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FullPath = Path.GetFullPath(path);
        if (!File.Exists(FullPath))
            throw new FileNotFoundException($"pk2 archive not found: {FullPath}", FullPath);

        _handle = File.OpenHandle(FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            Length = RandomAccess.GetLength(_handle);
            if (Length < HeaderSize + BlockSize)
                throw new InvalidDataException($"'{FullPath}' is too small to be a pk2 archive.");

            Span<byte> header = stackalloc byte[HeaderSize];
            RandomAccess.Read(_handle, header, 0);
            Signature = Encoding.ASCII.GetString(header[..30]).TrimEnd('\0');

            _blowfish.Initialize(CreateKey(string.IsNullOrEmpty(key) ? DefaultKey : key));
            ReadIndex();
        }
        catch
        {
            _handle.Dispose();
            throw;
        }

        if (_files.Count == 0)
            throw new InvalidDataException($"No files found in '{FullPath}'. The Blowfish key is probably wrong.");
    }

    public string FullPath { get; }
    public long Length { get; }

    /// <summary>Header signature, normally "JoyMax File Manager!".</summary>
    public string Signature { get; }

    public IReadOnlyCollection<Pk2File> Files => _files.Values;

    /// <summary>Derives the Blowfish key from the ASCII password (xor with the Silkroad base key, max 56 bytes).</summary>
    public static byte[] CreateKey(string asciiKey)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(asciiKey);
        int length = Math.Min(ascii.Length, 56);
        var key = new byte[length];
        for (int i = 0; i < length; i++)
            key[i] = (byte)(ascii[i] ^ (i < s_baseKey.Length ? s_baseKey[i] : 0));
        return key;
    }

    private void ReadIndex()
    {
        var visited = new HashSet<long>();
        var pending = new Stack<(long Offset, string Path)>();
        pending.Push((HeaderSize, string.Empty));
        byte[] block = new byte[BlockSize];

        while (pending.Count > 0)
        {
            var (offset, folderPath) = pending.Pop();
            var subFolders = new List<(long, string)>();

            // A folder may span several chained blocks.
            while (offset != 0)
            {
                if (!visited.Add(offset) || offset < HeaderSize || offset + BlockSize > Length)
                    break;

                RandomAccess.Read(_handle, block, offset);
                _blowfish.DecryptInPlace(block);

                long next = 0;
                for (int i = 0; i < EntriesPerBlock; i++)
                {
                    var entry = block.AsSpan(i * EntrySize, EntrySize);
                    byte type = entry[0];
                    if (type is 1 or 2)
                    {
                        string name = ReadName(entry.Slice(1, 81));
                        long position = BinaryPrimitives.ReadInt64LittleEndian(entry[106..]);
                        if (type == 1)
                        {
                            if (name is not ("." or "..") && name.Length > 0)
                            {
                                string path = folderPath + name;
                                _folders.Add(path);
                                subFolders.Add((position, path + "\\"));
                            }
                        }
                        else
                        {
                            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[114..]);
                            string path = folderPath + name;
                            _files[path] = new Pk2File(path, name, position, size);
                        }
                    }
                    if (i == EntriesPerBlock - 1)
                        next = BinaryPrimitives.ReadInt64LittleEndian(entry[118..]);
                }
                offset = next;
            }

            // Same visiting order as the legacy reader (later duplicates override earlier ones).
            for (int i = subFolders.Count - 1; i >= 0; i--)
                pending.Push(subFolders[i]);
        }
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool FolderExists(string path) => _folders.Contains(Normalize(path).TrimEnd('\\'));

    public Pk2File? GetFile(string path) => _files.GetValueOrDefault(Normalize(path));

    /// <summary>Files directly inside a folder.</summary>
    public IEnumerable<Pk2File> GetFiles(string folder)
    {
        string prefix = Normalize(folder).TrimEnd('\\') + "\\";
        return _files.Values.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                        && f.Path.IndexOf('\\', prefix.Length) < 0);
    }

    public byte[] ReadBytes(string path) =>
        ReadBytes(GetFile(path) ?? throw new FileNotFoundException($"'{path}' not found in {Path.GetFileName(FullPath)}."));

    public byte[] ReadBytes(Pk2File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Offset < 0 || file.Offset + file.Size > Length)
            throw new InvalidDataException($"'{file.Path}' points outside the archive.");
        var data = new byte[file.Size];
        RandomAccess.Read(_handle, data, file.Offset);
        return data;
    }

    /// <summary>Reads a text file, detecting UTF-16/UTF-8 byte order marks (textdata files are UTF-16 LE).</summary>
    public string ReadText(string path) => DecodeText(ReadBytes(path));

    internal static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data[2..]);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data[3..]);
        return Encoding.UTF8.GetString(data);
    }

    public void Dispose() => _handle.Dispose();
}
