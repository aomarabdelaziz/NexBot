using System.Text;

namespace NexBot.Protocol;

/// <summary>
/// Text encoding used for the length-prefixed "ASCII" strings of the Silkroad protocol.
/// </summary>
/// <remarks>
/// The original client uses the Windows ANSI code page (1252 for most vSRO servers). Code pages are not
/// available on .NET (Core) by default, so the provider is registered once here. This works on Linux too.
/// </remarks>
public static class SroEncoding
{
    private static Encoding s_ascii = Create(1252);

    /// <summary>The encoding used by <see cref="Packet.ReadAscii"/> and <see cref="Packet.WriteAscii"/>.</summary>
    public static Encoding Ascii => s_ascii;

    /// <summary>Changes the code page used for ASCII strings (e.g. 1256 for Arabic servers, 949 for Korean).</summary>
    public static void UseCodePage(int codePage) => s_ascii = Create(codePage);

    private static Encoding Create(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }
}
