using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>Pets: summon, heal, feed, revive, attack mode and unsummon.</summary>
public sealed partial class SroClient
{
    /// <summary>Our attack pet when it is out and alive.</summary>
    public AttackPet? AttackPet => World.MyPets.Values.OfType<AttackPet>().FirstOrDefault(p => p.LifeState != LifeState.Dead);

    /// <summary>An attack pet summon scroll (ITEM_COS_P_*, type 2/1/1; grab pets are 2/1/2).</summary>
    public static bool IsAttackPetScroll(Item? item) => item is CosItem { IsPet: true, TypeId4: 1 };

    /// <summary>Summons the pet from its scroll in <paramref name="slot"/> and waits for the answer.</summary>
    public Task<ItemMoveResult> SummonPetAsync(byte slot, CancellationToken cancellationToken = default) =>
        UseOnAsync(slot, usage => ClientPackets.UseItem(slot, usage), "summon", cancellationToken);

    /// <summary>Uses an item (recovery kit, HGP potion) on a summoned pet [slot][usage][uint pet].</summary>
    public Task<ItemMoveResult> UseItemOnPetAsync(byte slot, uint petUniqueId, CancellationToken cancellationToken = default) =>
        UseOnAsync(slot, usage => ClientPackets.UseItem(slot, usage, petUniqueId), "use on the pet", cancellationToken);

    /// <summary>Uses a pet revive item on the dead pet's scroll [slot][usage][byte scroll slot].</summary>
    public Task<ItemMoveResult> RevivePetAsync(byte reviveSlot, byte scrollSlot, CancellationToken cancellationToken = default) =>
        UseOnAsync(reviveSlot, usage => ClientPackets.UseItemOnSlot(reviveSlot, usage, scrollSlot), "revive the pet", cancellationToken);

    /// <summary>Our grab pet when it is out.</summary>
    public PickPet? PickPet => World.MyPets.Values.OfType<PickPet>().FirstOrDefault();

    /// <summary>The character picks up an item on the ground (walks there by itself).</summary>
    public bool PickUp(uint itemUniqueId) => Send(ClientPackets.PickUp(itemUniqueId));

    /// <summary>The grab pet picks up an item on the ground.</summary>
    public bool PetPickUp(uint petUniqueId, uint itemUniqueId) => Send(ClientPackets.PetPickUp(petUniqueId, itemUniqueId));

    /// <summary>Sends a summoned pet (attack, grab, horse, transport) back into its scroll.</summary>
    public bool UnsummonPet(uint petUniqueId) => Send(ClientPackets.UnsummonPet(petUniqueId));

    /// <summary>The attack pet's mode: attacks monsters (offensive) or stays passive.</summary>
    public bool SetAttackPetMode(uint petUniqueId, bool offensive) =>
        Send(ClientPackets.AttackPetMode(petUniqueId, offensive ? AttackPetSettings.Offensive : AttackPetSettings.Disabled));

    private async Task<ItemMoveResult> UseOnAsync(byte slot, Func<ushort, Packet> build, string what, CancellationToken cancellationToken)
    {
        if (!IsInGame || World.Character?.Inventory[slot] is not { } item)
            return new(false, "There is no item in that slot.");
        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (e, sent) = await UseAndWaitAsync(item, slot, build, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!sent)
                return new(false, "The bot is not connected.");
            if (e is null)
                return new(false, $"The server did not answer ({what}).");
            if (e.Success)
                return new(true, $"Used {item.Name}.");
            Logger.LogWarning("Pet: {What} with {Item} refused, error 0x{Error:X4}", what, item.ServerName, e.ErrorCode);
            return new(false, ItemErrors.Format(what, e.ErrorCode), e.ErrorCode);
        }
        finally
        {
            _moveGate.Release();
        }
    }
}
