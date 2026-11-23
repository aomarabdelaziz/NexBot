using NexBot.Game.Items;

namespace NexBot.Game;

/// <summary>
/// An open exchange window with another player: what each side put in (items and gold) and how far each side got
/// (Confirm locks the window, then both approve). Replaced as a whole by the receive loop; read it as a snapshot.
/// </summary>
public sealed class ExchangeState(uint partnerId, string partnerName)
{
    public uint PartnerId { get; } = partnerId;
    public string PartnerName { get; } = partnerName;

    /// <summary>Our items in the window, by the inventory slot they came from (they stay in the bag until the end).</summary>
    public IReadOnlyDictionary<byte, Item> MyItems { get; internal set; } = new Dictionary<byte, Item>();

    /// <summary>The other player's items in the window.</summary>
    public IReadOnlyList<Item> PartnerItems { get; internal set; } = [];

    public ulong MyGold { get; internal set; }
    public ulong PartnerGold { get; internal set; }

    /// <summary>We pressed Confirm (the window is locked for us).</summary>
    public bool MyConfirmed { get; internal set; }
    public bool PartnerConfirmed { get; internal set; }

    /// <summary>We approved after both confirmed; the exchange ends when the other player approves too.</summary>
    public bool MyApproved { get; internal set; }

    /// <summary>Counts changes, so a window can redraw only when something changed.</summary>
    public int Version { get; internal set; }

    /// <summary>The most an exchange window holds per player.</summary>
    public const int Slots = 12;
}
