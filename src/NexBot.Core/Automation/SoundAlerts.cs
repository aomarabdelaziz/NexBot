using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>A sound to play: which event, and a short text for the log.</summary>
public sealed class SoundAlertEventArgs(SoundEvent e, string text, string file) : EventArgs
{
    public SoundEvent Event { get; } = e;
    public string Text { get; } = text;
    /// <summary>The .wav to play (empty: the Windows alert sound).</summary>
    public string File { get; } = file;
}

/// <summary>
/// Watches the game for phBot's Sound tab events and raises <see cref="Alert"/> for the ones switched on. Playing the
/// sound is up to the app. The same thing (the same player, the same unique ...) does not ring twice within
/// <see cref="Repeat"/>.
/// </summary>
public sealed class SoundAlerts : IDisposable
{
    private readonly SroClient _client;
    private readonly SoundSettings _settings;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _rung = new();

    public SoundAlerts(SroClient client, SoundSettings settings, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
        client.EntitySpawned += OnSpawned;
        client.UniqueNotice += OnUniqueNotice;
        client.CharacterDied += OnDied;
        client.DamageDealt += OnDamage;
        client.ChatReceived += OnChat;
        client.ItemPicked += OnPicked;
        client.PetDied += OnPetDied;
        client.PacketSent += OnPacketSent;
    }

    /// <summary>The same event for the same thing rings at most once in this time.</summary>
    public TimeSpan Repeat { get; set; } = TimeSpan.FromSeconds(10);

    public event EventHandler<SoundAlertEventArgs>? Alert;

    /// <summary>A GM's name starts with [GM] (the spawn packet carries no GM flag).</summary>
    public static bool IsGameMaster(Player player) =>
        player.Name.StartsWith("[GM]", StringComparison.OrdinalIgnoreCase) || player.Name.StartsWith("GM_", StringComparison.OrdinalIgnoreCase);

    private void Ring(SoundEvent e, string key, string text)
    {
        if (_settings.Muted)
            return;
        var alert = _settings.Get(e);
        if (!alert.Enabled)
            return;
        var now = DateTime.UtcNow;
        string id = e + ":" + key;
        if (_rung.TryGetValue(id, out var at) && now - at < Repeat)
            return;
        _rung[id] = now;
        _logger.LogInformation("Sound: {Text}", text);
        Alert?.Invoke(this, new SoundAlertEventArgs(e, text, alert.File));
    }

    private bool IsListedUnique(string name) =>
        name.Length > 0 && _settings.Uniques.Any(u => u.Trim().Length > 0 && name.Contains(u.Trim(), StringComparison.OrdinalIgnoreCase));

    private void OnSpawned(object? sender, EntityEventArgs e)
    {
        switch (e.Entity)
        {
            case Player player:
                if (IsGameMaster(player))
                    Ring(SoundEvent.GmSpawned, player.Name, $"GM {player.Name} is near");
                if (player.HasJobMode() && player.JobType == JobType.Thief)
                    Ring(SoundEvent.ThiefNearby, player.Name, $"Thief {player.Name} is near");
                if (player.HasJobMode() && player.JobType == JobType.Hunter)
                    Ring(SoundEvent.HunterNearby, player.Name, $"Hunter {player.Name} is near");
                break;
            case Mob mob when mob.MobType.IsUnique():
                Ring(SoundEvent.UniqueInRange, mob.UniqueId.ToString(), $"Unique {mob.Name} is near");
                Ring(SoundEvent.UniqueSpawned, mob.Name, $"Unique {mob.Name} is near");
                if (IsListedUnique(mob.Name))
                    Ring(SoundEvent.SpecificUnique, mob.Name, $"{mob.Name} is near");
                break;
            case Mob mob when mob.MobType is MobType.Titan or MobType.PartyTitan:
                Ring(SoundEvent.TitanSpawned, mob.UniqueId.ToString(), $"Titan {mob.Name} is near");
                break;
        }
    }

    private void OnUniqueNotice(object? sender, UniqueNoticeEventArgs e)
    {
        if (!e.Spawned)
            return;
        Ring(SoundEvent.UniqueSpawned, e.Name, $"{e.Name} has appeared");
        if (IsListedUnique(e.Name))
            Ring(SoundEvent.SpecificUnique, e.Name, $"{e.Name} has appeared");
    }

    private void OnDied(object? sender, CharacterDiedEventArgs e) =>
        Ring(SoundEvent.PlayerDied, "me", e.KillerName is { Length: > 0 } killer ? $"You were killed by {killer}" : "You died");

    private void OnDamage(object? sender, DamageEventArgs e)
    {
        if (_client.World.Character is not { } me)
            return;
        if (e.SourceId == me.UniqueId && _client.World.GetEntity(e.TargetId) is Player target)
            Ring(SoundEvent.AttackingPlayer, target.Name, $"You are attacking {target.Name}");
        else if (e.TargetId == me.UniqueId && _client.World.GetEntity(e.SourceId) is Player attacker)
            Ring(SoundEvent.Attacked, attacker.Name, $"{attacker.Name} is attacking you");
    }

    private void OnChat(object? sender, ChatEventArgs e)
    {
        if (e.Type == ChatType.Private)
            Ring(SoundEvent.PrivateMessage, e.Sender + e.Message, $"Whisper from {e.Sender}: {e.Message}");
    }

    private void OnPicked(object? sender, ItemPickedEventArgs e)
    {
        if (e.Item is Game.Items.Equipable { Rarity: not Game.Items.ItemRarity.None })
            Ring(SoundEvent.RareItemDrop, Guid.NewGuid().ToString(), $"Picked up a rare item: {e.Item.Name}");
    }

    private void OnPetDied(object? sender, EntityEventArgs e)
    {
        if (e.Entity is Cos { IsTransport: true } transport)
            Ring(SoundEvent.TransportDied, transport.UniqueId.ToString(), $"Your transport {transport.Name} died");
    }

    private void OnPacketSent(object? sender, PacketSentEventArgs e)
    {
        // Using a return scroll (by the bot or by the player in the game client).
        if (e.Packet.Opcode != Opcodes.Client.ItemUse || e.Packet.Payload.Length < 1)
            return;
        byte slot = e.Packet.Payload[0];
        if (_client.World.Character?.Inventory is { } inventory && slot < inventory.Capacity && inventory[slot] is { } item
            && AutoWalker.IsReturnScroll(item.ServerName))
            Ring(SoundEvent.ReturningToTown, "return", "Returning to town");
    }

    public void Dispose()
    {
        _client.EntitySpawned -= OnSpawned;
        _client.UniqueNotice -= OnUniqueNotice;
        _client.CharacterDied -= OnDied;
        _client.DamageDealt -= OnDamage;
        _client.ChatReceived -= OnChat;
        _client.ItemPicked -= OnPicked;
        _client.PetDied -= OnPetDied;
        _client.PacketSent -= OnPacketSent;
    }
}
