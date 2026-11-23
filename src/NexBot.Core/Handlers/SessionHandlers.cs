using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>
/// The world part of the login packets. Unlike POC-2's LoginHandlers these never send anything: the replies
/// (0x3012, 0x34B6) come from the game client, or from <see cref="Network.BotSession"/> in clientless mode.
/// </summary>
internal static class SessionHandlers
{
    /// <summary>0xB001: the character was selected, the world loads next.</summary>
    public static void CharacterSelectionJoin(SroClient client, Packet p)
    {
        if (p.ReadByte() == 1)
            client.SetState(ClientState.Loading);
    }

    /// <summary>0x3020: the character is in the world (also after every teleport).</summary>
    public static void CelestialPosition(SroClient client, Packet p)
    {
        uint uniqueId = p.ReadUInt32();
        client.World.PlayerUniqueId = uniqueId;
        if (client.World.Character is { } character)
            character.UniqueId = uniqueId;

        client.DeathReported = false;
        client.LastAttacker = default;
        client.SetState(ClientState.InGame);
        client.Logger.LogDebug("Spawned in game (unique ID {UniqueId})", uniqueId);
        client.RaiseSpawned(uniqueId);
    }

    /// <summary>0x34B5: a teleport starts loading.</summary>
    public static void TeleportReady(SroClient client, Packet p) => client.SetState(ClientState.Loading);
}
