using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NexBot.Media;

/// <summary>Result of an extraction.</summary>
public sealed record ExtractionResult(string DatabasePath, ClientInfo? ClientInfo, IReadOnlyDictionary<string, int> RowCounts);

/// <summary>High-level entry point: client folder (or Media.pk2) to <c>Database.sqlite3</c> plus connection info.</summary>
public static class MediaExtractor
{
    /// <summary>Accepts a Silkroad client folder or a direct path to Media.pk2.</summary>
    public static string ResolveMediaPath(string clientPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);
        if (File.Exists(clientPath))
            return Path.GetFullPath(clientPath);
        if (Directory.Exists(clientPath))
        {
            string? media = Directory.EnumerateFiles(clientPath, "*.pk2")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "Media.pk2", StringComparison.OrdinalIgnoreCase));
            if (media is not null)
                return media;
        }
        throw new FileNotFoundException($"Media.pk2 not found at '{clientPath}'.");
    }

    /// <summary>Reads only the connection info (version, locale, gateways, port).</summary>
    public static ClientInfo ReadClientInfo(string clientPath, string? blowfishKey = null)
    {
        using var media = new Pk2Archive(ResolveMediaPath(clientPath), blowfishKey);
        return ClientInfo.Read(media);
    }

    /// <summary>Generates the game database from the client's Media.pk2.</summary>
    public static ExtractionResult Extract(string clientPath, string databasePath, string? blowfishKey = null, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        string mediaPath = ResolveMediaPath(clientPath);
        logger.LogInformation("Opening {Media}", mediaPath);
        using var media = new Pk2Archive(mediaPath, blowfishKey);
        logger.LogInformation("{Count:N0} files indexed", media.Files.Count);

        ClientInfo? info = null;
        try
        {
            info = ClientInfo.Read(media);
            logger.LogInformation("Client version {Version}, locale {Locale}, gateway {Gateways}:{Port}",
                info.Version, info.Locale, string.Join(", ", info.Gateways), info.GatewayPort);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or NexBot.Protocol.PacketException)
        {
            logger.LogWarning("Could not read client connection info: {Message}", ex.Message);
        }

        var builder = new GameDatabaseBuilder(media, logger);
        builder.Build(databasePath);
        return new ExtractionResult(Path.GetFullPath(databasePath), info, builder.RowCounts);
    }
}
