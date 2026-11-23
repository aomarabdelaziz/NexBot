using System.Collections.Concurrent;
using NexBot.Game.Entities;
using NexBot.Game.Items;

namespace NexBot.Game;

/// <summary>
/// Everything the client knows about the world around its character.
/// </summary>
/// <remarks>
/// Only the receive loop mutates this state. The collections are concurrent, so other threads (a UI, a
/// script) can enumerate them safely while packets keep arriving. Entity objects themselves are not
/// synchronized: treat them as snapshots when reading from another thread.
/// </remarks>
public sealed class WorldState
{
    /// <summary>The logged-in character, available after the character data packets arrive.</summary>
    public Character? Character { get; internal set; }

    /// <summary>Unique ID assigned to our character for this session (0x3020).</summary>
    public uint PlayerUniqueId { get; internal set; }

    public Party? Party { get; internal set; }

    /// <summary>Our own guild, from the guild data packets after login (null: none, or not received yet).</summary>
    public Guild? Guild { get; internal set; }

    public uint SelectedEntityId { get; internal set; }

    /// <summary>
    /// The character's storage, once its contents came (the server sends them on the first storage open of a login,
    /// by the game or the bot); null before.
    /// </summary>
    public SlotList<Item>? Storage { get; internal set; }
    public ulong StorageGold { get; internal set; }
    /// <summary>How many times the storage's contents came (the server sends them on every storage open).</summary>
    public int StorageLoads { get; internal set; }
    /// <summary>The last NPC talk answer (0xB046): how many came, whether it was accepted, and its talk option.</summary>
    public (int Count, bool Ok, byte Option) LastTalk { get; internal set; }

    /// <summary>The open exchange window with another player (null: none).</summary>
    public ExchangeState? Exchange { get; internal set; }
    /// <summary>A player asked us to exchange and we have not answered yet.</summary>
    public (uint Id, string Name, DateTime At)? ExchangeRequest { get; internal set; }
    /// <summary>The last exchange refusal from the server (for the window to show); cleared by the next request.</summary>
    public string? ExchangeError { get; internal set; }
    /// <summary>How the last exchange ended.</summary>
    public string? LastExchange { get; internal set; }

    public ConcurrentDictionary<uint, Entity> Entities { get; } = new();
    public ConcurrentDictionary<string, Player> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<uint, Mob> Mobs { get; } = new();
    public ConcurrentDictionary<uint, Npc> Npcs { get; } = new();
    public ConcurrentDictionary<uint, Teleport> Teleports { get; } = new();
    public ConcurrentDictionary<uint, Drop> Drops { get; } = new();
    public ConcurrentDictionary<uint, Buff> Buffs { get; } = new();

    /// <summary>Pets currently summoned by our character.</summary>
    public ConcurrentDictionary<uint, Cos> MyPets { get; } = new();

    /// <summary>Finds an entity by unique ID, including our character and our pets.</summary>
    public Entity? GetEntity(uint uniqueId)
    {
        var character = Character;
        if (character is not null && character.UniqueId == uniqueId)
            return character;
        if (Entities.TryGetValue(uniqueId, out var entity))
            return entity;
        return MyPets.TryGetValue(uniqueId, out var pet) ? pet : null;
    }

    public T? GetEntity<T>(uint uniqueId) where T : Entity => GetEntity(uniqueId) as T;

    /// <summary>
    /// What moves the character: the mount while riding (the server moves the horse, at the horse's speed), otherwise
    /// the character. Estimate positions with this one, not with the rider.
    /// </summary>
    public Model? Mover =>
        Character is not { } c ? null
        : c.IsRiding && GetEntity(c.RidingUniqueId) is Model mount ? mount
        : c;

    public bool IsEntityNear(uint uniqueId) => Entities.ContainsKey(uniqueId);

    internal void AddSpawn(Entity entity)
    {
        Entities[entity.UniqueId] = entity;
        switch (entity)
        {
            case Player player:
                Players[player.Name] = player;
                break;
            case Mob mob:
                Mobs[mob.UniqueId] = mob;
                break;
            case Guide guide:
                Npcs[guide.UniqueId] = guide;
                break;
            case Teleport teleport:
                Teleports[teleport.UniqueId] = teleport;
                break;
            case Drop drop:
                Drops[drop.UniqueId] = drop;
                break;
        }

        if (entity is Model model)
        {
            foreach (var buff in model.Buffs.Values)
                Buffs[buff.UniqueId] = buff;
        }
    }

    internal Entity? RemoveSpawn(uint uniqueId)
    {
        if (!Entities.TryRemove(uniqueId, out var entity))
            return null;

        switch (entity)
        {
            case Player player:
                Players.TryRemove(player.Name, out _);
                break;
            case Mob:
                Mobs.TryRemove(uniqueId, out _);
                break;
            case Guide:
                Npcs.TryRemove(uniqueId, out _);
                break;
            case Teleport:
                Teleports.TryRemove(uniqueId, out _);
                break;
            case Drop:
                Drops.TryRemove(uniqueId, out _);
                break;
        }

        if (entity is Model model)
        {
            foreach (var buff in model.Buffs.Values)
                Buffs.TryRemove(buff.UniqueId, out _);
        }
        return entity;
    }

    /// <summary>Clears spawns (on teleport the server re-sends everything around the character).</summary>
    internal void ClearSpawns()
    {
        Entities.Clear();
        Players.Clear();
        Mobs.Clear();
        Npcs.Clear();
        Teleports.Clear();
        Drops.Clear();
        Buffs.Clear();
        MyPets.Clear();
        SelectedEntityId = 0;
    }

    /// <summary>Clears everything (on disconnect).</summary>
    internal void Reset()
    {
        ClearSpawns();
        Character = null;
        Party = null;
        PlayerUniqueId = 0;
        Storage = null;
        StorageGold = 0;
        Exchange = null;
        ExchangeRequest = null;
        ExchangeError = null;
    }
}
