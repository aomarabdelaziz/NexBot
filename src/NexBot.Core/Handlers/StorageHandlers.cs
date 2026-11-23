using Microsoft.Extensions.Logging;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>The storage's contents: 0x3047 (gold), 0x3049 chunks (capacity, count, slot + item ...), 0x3048 (end).</summary>
internal static class StorageHandlers
{
    public static void Begin(SroClient client, Packet p)
    {
        if (p.Remaining >= 8)
            client.World.StorageGold = p.ReadUInt64();
        client.StorageDataBuffer = new Packet(Opcodes.Server.StorageData);
    }

    /// <summary>0xB046: an NPC talk answer: 1 and the talk option, or 2 and an error.</summary>
    public static void TalkAnswer(SroClient client, Packet p)
    {
        bool ok = p.Remaining >= 1 && p.ReadByte() == 1;
        byte option = ok && p.Remaining >= 1 ? p.ReadByte() : (byte)0;
        client.World.LastTalk = (client.World.LastTalk.Count + 1, ok, option);
    }

    public static void Data(SroClient client, Packet p) =>
        client.StorageDataBuffer?.WriteBytes(p.Payload);

    public static void End(SroClient client, Packet p)
    {
        var buffer = client.StorageDataBuffer;
        client.StorageDataBuffer = null;
        if (buffer is null)
            return;
        try
        {
            client.World.Storage = buffer.ReadInventory(client.Data);
            client.World.StorageLoads++;
            client.Logger.LogInformation("Storage: {Count} items in {Capacity} slots", client.World.Storage.Count(i => i is not null),
                client.World.Storage.Capacity);
        }
        catch (Exception ex) when (ex is PacketException or UnknownGameObjectException)
        {
            client.Logger.LogWarning("The storage's contents could not be read: {Message}", ex.Message);
        }
    }
}
