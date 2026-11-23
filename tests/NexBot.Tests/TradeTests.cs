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

/// <summary>AutoTrade: picking transports / job suits, recording RIDE / BUY / SELL and walking them again.</summary>
public class TradeTests
{
    private const uint TraderId = 700, TransportId = 7100;

    private static readonly ItemInfo Buffalo = new(23342, "ITEM_COS_T_BUFFALO_SCROLL", "Buffalo", 3, 3, 2, 0, 0, "", 50, 20);
    private static readonly ItemInfo Cow = new(3906, "ITEM_COS_T_COW1", "Fire bull", 3, 3, 2, 0, 0, "", 50, 60);
    private static readonly ItemInfo RideHorse = new(2192, "ITEM_COS_C_HORSE2", "Shadow Horse", 3, 3, 2, 0, 0, "", 50, 1);
    private static readonly ItemInfo Flag = new(2160, "ITEM_CH_M_TRADE_TRADER_02", "White flag", 1, 7, 1, 0, 0, "", 1, 20);
    private static readonly ItemInfo ThiefSuit = new(2163, "ITEM_CH_M_TRADE_THIEF_02", "Black suit", 1, 7, 2, 0, 0, "", 1, 20);
    private static readonly ItemInfo Silk = new(9001, "ITEM_ETC_TRADE_CH_01", "White Silk", 3, 8, 1, 0, 0, "", 40, 1);

    private static InMemoryGameData Data()
    {
        var data = WorldTests.Data();
        foreach (var item in new[] { Buffalo, Cow, RideHorse, Flag, ThiefSuit, Silk })
            data.Items[item.Id] = item;
        return data;
    }

    private static void EnterGame(SroClient client)
    {
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
    }

    private static void AddTrader(SroClient client)
    {
        var trader = new Guide(new ModelInfo(2010, "NPC_CH_SPECIAL", "Specialty Trader Jodaesan", 2, 2, 0, 0))
        { UniqueId = TraderId, Position = client.World.Character!.Position };
        client.World.Entities[TraderId] = trader;
        client.World.Npcs[TraderId] = trader;
    }

    private static Cos AddTransport(SroClient client, params Item[] cargo)
    {
        var transport = new Cos(new ModelInfo(2002, "COS_T_BUFFALO", "Buffalo", 2, 3, 2, 3000)) { UniqueId = TransportId, Position = client.World.Character!.Position };
        var goods = new SlotList<Item>(8);
        for (int i = 0; i < cargo.Length; i++)
            goods[i] = cargo[i];
        transport.Inventory = goods;
        client.World.MyPets[TransportId] = transport;
        client.World.Character.RidingUniqueId = TransportId;
        return transport;
    }

    private static Item Make(ItemInfo info, ushort quantity = 1)
    {
        var item = ItemFactory.Create(info);
        item.Quantity = quantity;
        return item;
    }

    private static async Task Until(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50);
    }

    [Fact]
    public void Trade_lines_round_trip()
    {
        string[] lines =
        [
            "RIDE,TRANSPORT",
            "BUY,NPC_CH_SPECIAL,TRANSPORT,0,1,40,5,12",
            "BUY,NPC_CH_POTION,inventory,0,3,100",
            "SELL,NPC_WC_SPECIAL,TRANSPORT,0,12",
            "SELL,NPC_CH_SMITH,ITEM_CH_SWORD_01_A,1",
            "DISMOUNT",
            "RIDE",
        ];
        var script = WalkScript.Parse(lines);
        Assert.Empty(script.Warnings);
        var buy = script.Commands[1];
        Assert.Equal(("NPC_CH_SPECIAL", ShopTarget.Transport, (byte)0, (byte)1, (ushort)40, 5, (byte)12),
            (buy.Npc, buy.Target, buy.Tab, buy.Slot, buy.Quantity, buy.Times, buy.Talk));
        // Defaults: one purchase, store talk option for the bag, trade talk option for the cargo.
        Assert.Equal((ShopTarget.Inventory, 1, ShopTarget.StoreTalk), (script.Commands[2].Target, script.Commands[2].Times, script.Commands[2].Talk));
        Assert.Equal("SELL,NPC_CH_SMITH,ITEM_CH_SWORD_01_A,1,1", script.Commands[4].ToLine());
        var again = WalkScript.Parse(WalkScript.Format(script.Commands));
        Assert.Equal(script.Commands.Select(c => c.ToLine()), again.Commands.Select(c => c.ToLine()));
    }

    [Fact]
    public void Catalog_lists_only_trade_transports_and_suits_of_the_job()
    {
        using var client = new SroClient(Data(), NullLogger.Instance, _ => true);
        EnterGame(client);
        var c = client.World.Character!;
        c.Inventory[20] = Make(Cow);
        c.Inventory[21] = Make(RideHorse);
        c.Inventory[22] = Make(Flag);

        var transports = TradeCatalog.Transports(client.Data, c);
        Assert.Equal(["ITEM_COS_T_COW1", "ITEM_COS_T_BUFFALO_SCROLL"], transports.Select(t => t.Info.ServerName)); // owned first, no riding horse
        Assert.Equal(1, transports[0].Owned);

        var traderSuits = TradeCatalog.TraderSuits(client.Data, null); // the thief suit is not listed
        Assert.Equal(["ITEM_CH_M_TRADE_TRADER_02"], traderSuits.Select(t => t.Info.ServerName));
        Assert.Equal(20, TradeCatalog.FindTransportScroll(c, ""));                          // any trade transport
        Assert.Equal(20, TradeCatalog.FindTransportScroll(c, "ITEM_COS_T_BUFFALO_SCROLL")); // preferred not owned: any
        Assert.Equal(TradeCatalog.JobOf(ThiefSuit), JobType.Thief);
    }

    [Fact]
    public void Recorder_writes_ride_buy_sell_and_dismount()
    {
        using var client = new SroClient(Data(), NullLogger.Instance, _ => true);
        EnterGame(client);
        AddTrader(client);
        var c = client.World.Character!;
        c.Inventory[20] = Make(Buffalo);
        using var recorder = new ScriptRecorder(client);
        recorder.Start();

        // Summon the buffalo and get on it: one RIDE line.
        client.ObservePacketToServer(ClientPackets.SummonTransport(20), fromGameClient: true);
        client.ObservePacketToServer(ClientPackets.MountPet(TransportId, true), fromGameClient: true);
        AddTransport(client);

        // Open the trader with option 12 and buy 40 silk three times; the server accepts each.
        client.ObservePacketToServer(ClientPackets.TalkToNpc(TraderId, 12), fromGameClient: true);
        for (int i = 0; i < 3; i++)
        {
            client.ObservePacketToServer(ClientPackets.BuyFromNpc(TraderId, 0, 0, 40), fromGameClient: true);
            client.HandlePacket(new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(1).WriteByte(19)
                .WriteUInt32(TransportId).WriteByte(0).WriteByte(0).WriteByte(0));
        }
        // A fourth purchase is refused (transport full): not recorded.
        client.ObservePacketToServer(ClientPackets.BuyFromNpc(TraderId, 0, 0, 40), fromGameClient: true);
        client.HandlePacket(new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(2).WriteUInt16(0x1805));

        // Sell two stacks: one SELL line for the whole cargo. Then get off.
        for (byte slot = 0; slot < 2; slot++)
        {
            client.ObservePacketToServer(ClientPackets.SellToNpc(TraderId, TransportId, slot, 40), fromGameClient: true);
            client.HandlePacket(new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(1).WriteByte(20)
                .WriteUInt32(TransportId).WriteByte(slot).WriteUInt16(40).WriteUInt32(TraderId).WriteByte(0));
        }
        client.ObservePacketToServer(ClientPackets.MountPet(TransportId, false), fromGameClient: true);
        recorder.Stop();

        var lines = recorder.Commands.Where(x => x.Type != WalkCommandType.Move).Select(x => x.ToLine()).ToList();
        Assert.Equal(
        [
            "RIDE,TRANSPORT",
            "BUY,NPC_CH_SPECIAL,TRANSPORT,0,0,40,3,12",
            "SELL,NPC_CH_SPECIAL,TRANSPORT,0,12",
            "DISMOUNT",
        ], lines);
    }

    [Fact]
    public async Task Walker_buys_until_refused_and_sells_the_cargo()
    {
        var sent = new List<Packet>();
        SroClient live = null;
        int buys = 0;
        using var client = new SroClient(Data(), NullLogger.Instance, p =>
        {
            lock (sent)
                sent.Add(new Packet(p.Opcode, p.Payload));
            if (p.Opcode != Opcodes.Client.ItemMovement)
                return true;
            byte type = p.ReadByte();
            Packet answer = type switch
            {
                // The transport takes three purchases, then it is full.
                0x13 => ++buys <= 3
                    ? new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(1).WriteByte(19).WriteUInt32(TransportId).WriteByte(0).WriteByte(0).WriteByte(0)
                    : new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(2).WriteUInt16(0x1805),
                0x14 => SellAnswer(p),
                _ => null,
            };
            if (answer is not null)
                _ = Task.Run(() => live.HandlePacket(answer));
            return true;
        });
        live = client;
        EnterGame(client);
        AddTrader(client);
        var transport = AddTransport(client, Make(Silk, 40), Make(Silk, 25));
        var here = client.World.Character!.Position;

        var script = WalkScript.Parse(
        [
            $"MOVE,{here.PosX:0},{here.PosY:0}",
            "SELL,NPC_CH_SPECIAL,TRANSPORT,0,12",
            "BUY,NPC_CH_SPECIAL,TRANSPORT,0,0,40,0,12",
        ]);
        using var walker = new AutoWalker(client, script, new AutoWalkOptions
        {
            Loop = false, PollInterval = TimeSpan.FromMilliseconds(50), ShopPause = TimeSpan.Zero,
        });
        walker.Start();
        await Until(() => walker.IsRunning, 2);
        await Until(() => !walker.IsRunning, 20);

        Assert.Equal("Finished.", walker.Status);
        Assert.Equal(0, transport.Inventory.Count(i => i is not null)); // both stacks sold
        Assert.Equal(4, buys);                                          // 3 accepted, the 4th refused: stop
        List<Packet> all;
        lock (sent)
            all = [.. sent];
        // Each visit: select, talk 12, the moves, close.
        Assert.Equal(2, all.Count(p => p.Opcode == Opcodes.Client.NpcClose));
        var talk = all.First(p => p.Opcode == Opcodes.Client.EntityTalk);
        Assert.Equal(TraderId, talk.ReadUInt32());
        Assert.Equal(12, talk.ReadByte());
        Assert.True(all.FindIndex(p => p.Opcode == Opcodes.Client.EntitySelection) < all.FindIndex(p => p.Opcode == Opcodes.Client.EntityTalk));
    }

    /// <summary>A client whose fake server accepts every purchase into the transport and records its quantity.</summary>
    private static SroClient TradingClient(List<ushort> purchases, InMemoryGameData data = null)
    {
        SroClient live = null;
        var client = new SroClient(data ?? Data(), NullLogger.Instance, p =>
        {
            if (p.Opcode != Opcodes.Client.ItemMovement || p.ReadByte() != 0x13)
                return true;
            p.ReadUInt32();
            byte tab = p.ReadByte(), slot = p.ReadByte();
            ushort quantity = p.ReadUInt16();
            lock (purchases)
                purchases.Add(quantity);
            _ = Task.Run(() => live.HandlePacket(new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(1).WriteByte(19)
                .WriteUInt32(TransportId).WriteByte(tab).WriteByte(slot).WriteByte(0)));
            return true;
        });
        live = client;
        EnterGame(client);
        AddTrader(client);
        AddTransport(client);
        return client;
    }

    [Fact]
    public async Task Runner_repeats_the_loops()
    {
        var purchases = new List<ushort>();
        using var client = TradingClient(purchases);
        var here = client.World.Character!.Position;
        string folder = Path.Combine(Path.GetTempPath(), "nexbot-trade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string a = Path.Combine(folder, "jangan-donwhang.txt"), b = Path.Combine(folder, "donwhang-hotan.txt");
            File.WriteAllLines(a, [$"MOVE,{here.PosX:0},{here.PosY:0}", "BUY,NPC_CH_SPECIAL,TRANSPORT,0,0,10,1,12"]);
            File.WriteAllLines(b, [$"MOVE,{here.PosX:0},{here.PosY:0}", "BUY,NPC_CH_SPECIAL,TRANSPORT,0,1,20,1,12"]);
            var settings = new TradeSettings { Loops = [a, b], RepeatLoops = true, LoopCount = 2 };
            using var runner = new TradeRunner(client, settings) { PollInterval = TimeSpan.FromMilliseconds(50) };

            Assert.True(await runner.RunAsync(CancellationToken.None), runner.Status);
            Assert.Equal("Auto trade finished.", runner.Status);
            Assert.Equal([10, 20, 10, 20], purchases); // loop A, loop B, then both again
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Goods_list_buys_the_ticked_goods_in_stack_sized_lots()
    {
        var data = Data();
        data.Shops["NPC_CH_SPECIAL"] = [new ShopSlotInfo(0, 0, Silk.ServerName), new ShopSlotInfo(0, 1, "ITEM_ETC_TRADE_CH_02")];
        var purchases = new List<ushort>();
        using var client = TradingClient(purchases, data);
        var here = client.World.Character!.Position;
        var settings = new TradeSettings { BuyMode = TradeBuyMode.GoodsList };
        settings.Good(Silk.ServerName).Enabled = true;
        settings.Good(Silk.ServerName).Quantity = 100;                 // silk stacks by 40: 40 + 40 + 20
        settings.Good("ITEM_ETC_TRADE_CH_02").Quantity = 50;            // not ticked

        var script = WalkScript.Parse([$"MOVE,{here.PosX:0},{here.PosY:0}", "BUY,NPC_CH_SPECIAL,TRANSPORT,0,1,5,1,12"]);
        using var walker = new AutoWalker(client, script, new AutoWalkOptions
        {
            Loop = false, PollInterval = TimeSpan.FromMilliseconds(50), ShopPause = TimeSpan.Zero, Trade = settings,
        });
        Assert.True(await walker.WalkAsync(CancellationToken.None), walker.Status);
        Assert.Equal([40, 40, 20], purchases);
    }

    [Fact]
    public async Task Guard_gets_off_and_attacks_thieves_near_the_transport()
    {
        var attacks = new List<uint>();
        SroClient live = null;
        using var client = new SroClient(Data(), NullLogger.Instance, p =>
        {
            if (p.Opcode == Opcodes.Client.PetMount && !p.ReadBool())
                live.World.Character!.RidingUniqueId = 0; // the server lets us off
            else if (p.Opcode == Opcodes.Client.Action)
            {
                lock (attacks)
                    attacks.Add(0);
                if (attacks.Count >= 2) // the thief dies on the second hit
                    live.World.Mobs.TryRemove(9001, out _);
            }
            return true;
        });
        live = client;
        EnterGame(client);
        AddTransport(client); // riding
        var at = client.World.Character!.Position;
        var thief = new Mob(new ModelInfo(2222, "MOB_THIEF_NPC_0001", "Thief", 2, 1, 2, 100)) { UniqueId = 9001, Position = new Position(at.PosX + 10, at.PosY) };
        var far = new Mob(new ModelInfo(2223, "MOB_THIEF_NPC_0002", "Thief", 2, 1, 2, 100)) { UniqueId = 9002, Position = new Position(at.PosX + 300, at.PosY) };
        client.World.Mobs[9001] = thief;
        client.World.Mobs[9002] = far;

        // Staying on the transport: never fight.
        var settings = new TradeSettings { AttackThieves = true, AttackRadius = 30, MountMode = TradeMountMode.StayOn };
        var guard = new TradeGuard(client, settings, NullLogger.Instance) { AttackInterval = TimeSpan.FromMilliseconds(20) };
        Assert.Equal([thief], guard.ThievesNear()); // the far one is out of the radius
        Assert.False(await guard.CheckAsync(CancellationToken.None));
        Assert.Empty(attacks);

        // Stay off: get off, kill the near thief, leave the far one.
        settings.MountMode = TradeMountMode.StayOff;
        Assert.True(await guard.CheckAsync(CancellationToken.None));
        Assert.False(client.World.Character.IsRiding);
        Assert.Equal(2, attacks.Count);
        Assert.Empty(guard.ThievesNear());
    }

    private static Packet SellAnswer(Packet sell)
    {
        uint pet = sell.ReadUInt32();
        byte slot = sell.ReadByte();
        ushort amount = sell.ReadUInt16();
        uint npc = sell.ReadUInt32();
        return new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(1).WriteByte(20)
            .WriteUInt32(pet).WriteByte(slot).WriteUInt16(amount).WriteUInt32(npc).WriteByte(0);
    }
}
