using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Alchemy (catalog, queue against a fake server) and If / Then conditions.</summary>
public class AlchemyConditionTests
{
    private static readonly ItemInfo Sword = new(3632, "ITEM_CH_SWORD_08_A_RARE", "Sword of the Heaven", 1, 6, 2, 0, 0, "", 1, 64);
    private static readonly ItemInfo WeaponElixir = new(3795, "ITEM_ETC_ARCHEMY_REINFORCE_RECIPE_WEAPON_B", "Intensifying elixir (weapon)", 3, 3, 10, 0, 0, "", 50, 1);
    private static readonly ItemInfo ArmorElixir = new(3796, "ITEM_ETC_ARCHEMY_REINFORCE_RECIPE_ARMOR_B", "Intensifying elixir (armor)", 3, 3, 10, 0, 0, "", 50, 1);
    private static readonly ItemInfo Powder8 = new(3800, "ITEM_ETC_ARCHEMY_REINFORCE_PROB_UP_A_08", "Lucky powder (8)", 3, 3, 10, 0, 0, "", 50, 1);
    private static readonly ItemInfo Immortal8 = new(3801, "ITEM_ETC_ARCHEMY_MAGICSTONE_ATHANASIA_08", "Magic stone of immortal", 3, 3, 11, 0, 0, "", 50, 1);
    private static readonly ItemInfo Str8 = new(3802, "ITEM_ETC_ARCHEMY_MAGICSTONE_STR_08", "Magic stone of Str", 3, 3, 11, 0, 0, "", 50, 1);
    private static readonly ItemInfo AttrPa8 = new(3803, "ITEM_ETC_ARCHEMY_ATTRSTONE_PA_08", "Attribute stone of attack", 3, 3, 11, 0, 0, "", 50, 1);
    private static readonly ItemInfo Potion = new(4, "ITEM_ETC_HP_POTION_05", "HP recovery potion (XL)", 3, 3, 1, 0, 0, "", 50, 1);

    private static (SroClient Client, ConcurrentQueue<Packet> Sent) Client(Func<Packet, Packet?>? server = null)
    {
        var data = WorldTests.Data();
        foreach (var i in new[] { Sword, WeaponElixir, ArmorElixir, Powder8, Immortal8, Str8, AttrPa8, Potion })
            data.Items[i.Id] = i;
        var sent = new ConcurrentQueue<Packet>();
        SroClient? client = null;
        client = new SroClient(data, NullLogger.Instance, p =>
        {
            sent.Enqueue(p);
            if (server?.Invoke(p) is { } reply)
                client!.HandlePacket(reply);
            return true;
        });
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        var inventory = client.World.Character!.Inventory;
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
            inventory[i] = null;
        return (client, sent);
    }

    private static T Put<T>(SroClient client, int slot, ItemInfo info, ushort quantity = 1) where T : Item
    {
        var item = ItemFactory.Create(info);
        item.Quantity = quantity;
        client.World.Character!.Inventory[slot] = item;
        return (T)item;
    }

    [Fact]
    public void Catalog_matches_degrees_elixirs_powders_and_stones()
    {
        var sword = (Equipable)ItemFactory.Create(Sword);
        Assert.Equal(8, AlchemyCatalog.DegreeOf(Sword.ServerName));
        Assert.Equal(8, AlchemyCatalog.MaterialDegree(Powder8.ServerName));
        Assert.True(AlchemyCatalog.IsElixirFor(ItemFactory.Create(WeaponElixir), sword));
        Assert.False(AlchemyCatalog.IsElixirFor(ItemFactory.Create(ArmorElixir), sword));
        Assert.True(AlchemyCatalog.IsPowderFor(ItemFactory.Create(Powder8), 8));
        Assert.False(AlchemyCatalog.IsPowderFor(ItemFactory.Create(Powder8), 7));
        Assert.True(AlchemyCatalog.IsPlusStone(ItemFactory.Create(Immortal8), PlusStone.Immortal, 8));
        Assert.False(AlchemyCatalog.IsBlueStone(ItemFactory.Create(Immortal8)));
        Assert.True(AlchemyCatalog.IsBlueStone(ItemFactory.Create(Str8)));
        Assert.Equal("STR", AlchemyCatalog.BlueToken(Str8.ServerName));
        Assert.Equal("PA", AlchemyCatalog.WhiteToken(AttrPa8.ServerName));

        // Weapon white order: DUR, PAR, MAR, HR, PA ... → PA is the 5th 5-bit group.
        sword.Variance = 31UL << (4 * 5) | 15UL << (3 * 5);
        Assert.Equal(100, AlchemyCatalog.WhitePercent(sword, AttrPa8.ServerName));
        Assert.Null(AlchemyCatalog.WhitePercent(sword, "ITEM_ETC_ARCHEMY_ATTRSTONE_XYZ_08"));
        sword.MagicOptions = [new MagicOption(1, new MagicOptionInfo(1, "MATTR_STR", "Str", 9)) { Value = 4 }];
        Assert.Equal(4u, AlchemyCatalog.BlueValue(sword, Str8.ServerName));
    }

    [Fact]
    public void Alchemy_packet_puts_the_item_first()
    {
        var p = ClientPackets.AlchemyFuse(Opcodes.Client.AlchemyReinforce, 3, [13, 20, 21]);
        Assert.Equal("0203030D1415", Convert.ToHexString(p.Payload));
    }

    /// <summary>A fake server: elixirs follow <paramref name="results"/> (true = success); stones always work.</summary>
    private static Func<Packet, Packet?> Server(Queue<bool> results) => p => p.Opcode switch
    {
        Opcodes.Client.AlchemyReinforce => new Packet(Opcodes.Server.AlchemyReinforceResponse)
            .WriteByte(1).WriteByte(2).WriteBool(results.Count == 0 || results.Dequeue()).WriteByte(p.Payload[3]).WriteUInt16(0),
        Opcodes.Client.AlchemyStone => new Packet(Opcodes.Server.AlchemyStoneResponse)
            .WriteByte(1).WriteByte(2).WriteBool(true).WriteByte(p.Payload[3]).WriteUInt16(0),
        _ => null,
    };

    [Fact]
    public async Task Plus_runs_until_stop_at_with_powder_and_immortal_from_their_plus()
    {
        var results = new Queue<bool>([true, true, false, true, true, true]);
        var (client, sent) = Client(Server(results));
        using var _ = client;
        var sword = Put<Equipable>(client, 13, Sword);
        Put<EtcItem>(client, 14, WeaponElixir, 20);
        Put<EtcItem>(client, 15, Powder8, 10);
        Put<EtcItem>(client, 16, Immortal8, 1);
        var settings = new AlchemySettings { StopAt = 3, LuckyPowderAt = 2, ImmortalAt = 1 };
        var runner = new AlchemyRunner(client, settings) { DelayScale = 0 };
        runner.Queue.Add(new AlchemyEntry(AlchemyJob.Plus, 13, sword.Id, sword.Name, 3));

        string result = await runner.RunAsync(CancellationToken.None);

        Assert.Equal("Queue finished.", result);
        var elixirs = sent.Where(p => p.Opcode == Opcodes.Client.AlchemyReinforce).ToList();
        // +0→1, +1→2, +2 fails → 0, then +0→1, +1→2, +2→3: six elixirs; powder (3 slots) only at +2.
        Assert.Equal(6, elixirs.Count);
        Assert.Equal([2, 2, 3, 2, 2, 3], elixirs.Select(p => (int)p.Payload[2]));
        var stones = sent.Where(p => p.Opcode == Opcodes.Client.AlchemyStone).ToList();
        Assert.Single(stones); // the immortal stone once at +1 (then it is used up)
        Assert.Equal(14, client.World.Character!.Inventory[14]!.Quantity);
    }

    [Fact]
    public async Task Lucky_powder_from_plus_0_is_used_on_every_elixir()
    {
        var (client, sent) = Client(Server(new Queue<bool>([true, true])));
        using var _ = client;
        var sword = Put<Equipable>(client, 13, Sword);
        Put<EtcItem>(client, 14, WeaponElixir, 20);
        Put<EtcItem>(client, 15, Powder8, 10);
        var settings = new AlchemySettings { StopAt = 2, UseLuckyPowder = true, LuckyPowderAt = 0 };
        var runner = new AlchemyRunner(client, settings) { DelayScale = 0 };
        runner.Queue.Add(new AlchemyEntry(AlchemyJob.Plus, 13, sword.Id, sword.Name, 2));

        await runner.RunAsync(CancellationToken.None);

        var elixirs = sent.Where(p => p.Opcode == Opcodes.Client.AlchemyReinforce).ToList();
        Assert.Equal(2, elixirs.Count);
        Assert.All(elixirs, p => Assert.Equal(3, p.Payload[2])); // item + elixir + powder
        Assert.Equal(8, client.World.Character!.Inventory[15]!.Quantity);

        // Settings saved before the switch: a plus above 0 still means on.
        Assert.True(new AlchemySettings { LuckyPowderAt = 1 }.LuckyPowderOn);
        Assert.False(new AlchemySettings { LuckyPowderAt = 1, UseLuckyPowder = false }.LuckyPowderOn);
    }

    [Fact]
    public async Task A_destroyed_item_stops_the_whole_queue()
    {
        var (client, _) = Client(p => p.Opcode == Opcodes.Client.AlchemyReinforce
            ? new Packet(Opcodes.Server.AlchemyReinforceResponse).WriteByte(1).WriteByte(2).WriteBool(false).WriteByte(p.Payload[3])
            : null);
        using var _c = client;
        var sword = Put<Equipable>(client, 13, Sword);
        var second = Put<Equipable>(client, 17, Sword);
        Put<EtcItem>(client, 14, WeaponElixir, 20);
        var runner = new AlchemyRunner(client, new AlchemySettings { StopAt = 5 }) { DelayScale = 0 };
        runner.Queue.Add(new AlchemyEntry(AlchemyJob.Plus, 13, sword.Id, sword.Name, 5));
        runner.Queue.Add(new AlchemyEntry(AlchemyJob.Plus, 17, second.Id, second.Name, 5));

        string result = await runner.RunAsync(CancellationToken.None);

        Assert.Contains("destroyed", result, StringComparison.Ordinal);
        Assert.Null(client.World.Character!.Inventory[13]);
        Assert.Equal(2, runner.Queue.Count); // stopped: nothing else was done
    }

    [Fact]
    public async Task Condition_fires_once_when_it_becomes_true_and_again_when_repeating()
    {
        var (client, sent) = Client();
        using var _ = client;
        Put<EtcItem>(client, 13, Potion, 3);
        var rule = new ConditionRule
        {
            Name = "Few potions",
            Ifs = [new ConditionIf { Type = IfType.ItemCountBelow, Number = 5, Text = "recovery potion" }],
            Thens = [new ConditionThen { Type = ThenType.Whisper, Text = "Friend: bring potions" }],
        };
        var settings = new ConditionSettings { Rules = [rule] };
        using var engine = new ConditionEngine(client, settings, startLoop: false);

        await engine.StepAsync(CancellationToken.None);
        await engine.StepAsync(CancellationToken.None); // still true: not again
        Assert.Single(sent, p => p.Opcode == Opcodes.Client.Chat);

        client.World.Character!.Inventory[13]!.Quantity = 50; // false
        await engine.StepAsync(CancellationToken.None);
        client.World.Character.Inventory[13]!.Quantity = 1;   // true again
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(2, sent.Count(p => p.Opcode == Opcodes.Client.Chat));
    }

    [Fact]
    public async Task Condition_checks_all_ifs_and_raises_app_actions()
    {
        var (client, _) = Client();
        using var _c = client;
        var rule = new ConditionRule
        {
            Ifs = [new ConditionIf { Type = IfType.WhisperReceived, Text = "stop" }, new ConditionIf { Type = IfType.NotInParty }],
            Thens = [new ConditionThen { Type = ThenType.PlaySound, Text = "a.wav" }, new ConditionThen { Type = ThenType.StopBot }],
        };
        using var engine = new ConditionEngine(client, new ConditionSettings { Rules = [rule] }, startLoop: false);
        var done = new List<string>();
        engine.SoundRequested += f => done.Add("sound " + f);
        engine.StopRequested += () => done.Add("stop");

        client.RaiseChat(ChatType.Private, "Boss", "hello");
        await engine.StepAsync(CancellationToken.None);
        Assert.Empty(done);

        client.RaiseChat(ChatType.Private, "Boss", "please STOP now");
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(["sound a.wav", "stop"], done);
    }
}
