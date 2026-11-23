using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Game.Entities;
using NexBot.Protocol;
using Xunit;

namespace NexBot.Tests;

/// <summary>Pet page: what to do with the attack pet.</summary>
public class PetTests
{
    [Fact]
    public void A_dead_attack_pet_is_revived_or_the_character_returns_by_the_options()
    {
        var s = new PetSettings { AutoRevive = true };
        Assert.Equal("revive", PetManager.DeadPetPlan(s, 0, 2, 0));
        Assert.NotEqual("revive", PetManager.DeadPetPlan(s, 0, 0, 5)); // no revive item
        s.ReviveTimes = 2;
        Assert.NotEqual("revive", PetManager.DeadPetPlan(s, 2, 3, 5)); // the limit
        s.ReviveTimes = 0;
        s.ReviveOnlyWithPotions = true;
        Assert.NotEqual("revive", PetManager.DeadPetPlan(s, 0, 3, 0)); // no recovery kits
        Assert.Equal("revive", PetManager.DeadPetPlan(s, 0, 3, 1));

        s.ReturnWhenDies = true; // only when there are no revive items (default)
        Assert.Equal("revive", PetManager.DeadPetPlan(s, 0, 3, 1));
        Assert.Equal("return", PetManager.DeadPetPlan(s, 0, 0, 1));
        s.ReturnOnlyIfNoRevive = false;
        Assert.Equal("return", PetManager.DeadPetPlan(s, 0, 3, 1));
    }

    private static readonly ItemInfo Wolf = new(1, "ITEM_COS_P_WOLF_SCROLL", "Wolf", 2, 1, 1, 0, 0, "", 1, 1);
    private static readonly ItemInfo Bear = new(2, "ITEM_COS_P_BEAR_SCROLL", "Bear", 2, 1, 1, 0, 0, "", 1, 1);
    private static readonly ItemInfo Tiger = new(3, "ITEM_COS_P_TIGER_SCROLL", "Tiger", 2, 1, 1, 0, 0, "", 1, 1);

    private static Character Bag(params (int Slot, ItemInfo Info, PetItemState State)[] scrolls)
    {
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        var c = client.World.Character!;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            c.Inventory[i] = null;
        foreach (var (slot, info, state) in scrolls)
        {
            var item = (CosItem)ItemFactory.Create(info);
            item.State = state;
            c.Inventory[slot] = item;
        }
        return c;
    }

    [Fact]
    public void Preferred_pets_are_summoned_or_revived_in_their_order()
    {
        var c = Bag((13, Wolf, PetItemState.Unsummoned), (14, Bear, PetItemState.Dead), (15, Tiger, PetItemState.Unsummoned));
        // No order: an alive one first, by slot.
        Assert.Equal((13, false), PetManager.Choose(c, [], true));
        // Bear first: it is dead, so it is revived when that is allowed ...
        Assert.Equal((14, true), PetManager.Choose(c, [Bear.ServerName, Tiger.ServerName], true));
        // ... else pet 2 (Tiger) is summoned.
        Assert.Equal((15, false), PetManager.Choose(c, [Bear.ServerName, Tiger.ServerName], false));
        // Pet 1 not in the bag: pet 2.
        Assert.Equal((15, false), PetManager.Choose(c, ["ITEM_COS_P_LION_SCROLL", Tiger.ServerName], true));
        // Only a dead pet and no revive: nothing.
        Assert.Null(PetManager.Choose(Bag((14, Bear, PetItemState.Dead)), [], false));
    }

    [Fact]
    public void After_three_failed_revives_or_summons_it_returns_to_town_when_asked()
    {
        var s = new PetSettings();
        Assert.False(PetManager.GiveUp(s, 5)); // return when it dies is off
        s.ReturnWhenDies = true;
        Assert.False(PetManager.GiveUp(s, 2));
        Assert.True(PetManager.GiveUp(s, 3));
    }

    [Fact]
    public void The_attack_pet_is_summoned_only_where_asked()
    {
        var s = new PetSettings();
        Assert.Null(PetManager.SummonRefusal(s, false, false));
        s.SummonOnlyInTown = true;
        Assert.NotNull(PetManager.SummonRefusal(s, false, true));
        Assert.Null(PetManager.SummonRefusal(s, true, false));
        s.SummonOnlyInTown = false;
        s.SummonOnlyAtTrainingArea = true;
        Assert.NotNull(PetManager.SummonRefusal(s, true, false));
        Assert.Null(PetManager.SummonRefusal(s, false, true));
        s.AutoSummon = false;
        Assert.NotNull(PetManager.SummonRefusal(s, true, true));
    }

    [Fact]
    public void Attack_pet_scrolls_and_pet_packets()
    {
        var attack = ItemFactory.Create(new ItemInfo(1, "ITEM_COS_P_WOLF_SCROLL", "Wolf", 2, 1, 1, 0, 0, "", 1, 1));
        var grab = ItemFactory.Create(new ItemInfo(2, "ITEM_COS_P_RABBIT_SCROLL", "Rabbit", 2, 1, 2, 0, 0, "", 1, 1));
        Assert.True(SroClient.IsAttackPetScroll(attack));
        Assert.False(SroClient.IsAttackPetScroll(grab));

        Assert.Equal("0D20002E", Convert.ToHexString(ClientPackets.UseItemOnSlot(0x0D, 0x0020, 0x2E).Payload));
        Assert.Equal("39300000", Convert.ToHexString(ClientPackets.UnsummonPet(12345).Payload));
        Assert.Equal("393000000100000000", Convert.ToHexString(ClientPackets.AttackPetMode(12345, AttackPetSettings.Disabled).Payload));
    }
}
