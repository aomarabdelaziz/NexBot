using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>Stall packets: answers to our stall requests, stalls of players around us, sales.</summary>
internal static class StallHandlers
{
    /// <summary>0xB0B1 / 0xB0B2 / 0xB0BA: [1] success, or [2][ushort error].</summary>
    public static void Answer(SroClient client, Packet p)
    {
        bool ok = p.ReadSuccess();
        if (!ok)
            client.Logger.LogInformation("Stall answer 0x{Opcode:X4}: {Hex}", p.Opcode, Convert.ToHexString(p.Payload));
        client.CompleteStallRequest(ok, ok ? (ushort)0 : p.ReadErrorCode());
    }

    /// <summary>0xB0B5: we left a stall (ours or someone else's).</summary>
    public static void Leave(SroClient client, Packet p) => client.OnStallGone();

    /// <summary>0x30B7: something happened in our stall; 3 = sold [slot][buyer].</summary>
    public static void EntityAction(SroClient client, Packet p)
    {
        byte action = p.ReadByte();
        if (action != 3)
            return;
        byte slot = p.ReadByte();
        string buyer = p.Remaining > 2 ? p.ReadAscii() : "";
        client.OnStallSold(slot, buyer);
    }

    /// <summary>0x30B8: a player opened a stall [uid][title][decoration].</summary>
    public static void EntityCreate(SroClient client, Packet p)
    {
        uint uid = p.ReadUInt32();
        string title = p.ReadAscii();
        uint decoration = p.Remaining >= 4 ? p.ReadUInt32() : 0;
        if (client.World.GetEntity(uid) is Player player)
            player.Stall = new Stall { Title = title, DecorationId = decoration };
        if (client.World.Character is { } me && me.UniqueId == uid)
            me.Stall = new Stall { Title = title, DecorationId = decoration };
    }

    /// <summary>0x30B9: a player closed their stall [uid].</summary>
    public static void EntityDestroy(SroClient client, Packet p)
    {
        uint uid = p.ReadUInt32();
        if (client.World.GetEntity(uid) is Player player)
            player.Stall = null;
        if (client.World.Character is { } me && me.UniqueId == uid)
        {
            me.Stall = null;
            client.OnStallGone();
        }
    }

    /// <summary>0x30BB: a stall's title changed [uid][title].</summary>
    public static void EntityTitle(SroClient client, Packet p)
    {
        uint uid = p.ReadUInt32();
        string title = p.ReadAscii();
        if (client.World.GetEntity(uid) is Player { Stall: { } stall })
            stall.Title = title;
        client.Logger.LogDebug("Stall title of {Uid}: {Title}", uid, title);
    }
}
