using NexBot.Data;

namespace NexBot.Game.Items;

/// <summary>Thrown when the game database has no row for a referenced object ID.</summary>
public sealed class UnknownGameObjectException : Exception
{
    public UnknownGameObjectException() { }
    public UnknownGameObjectException(string message) : base(message) { }
    public UnknownGameObjectException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Creates the right <see cref="Item"/> subclass from the item's type IDs.</summary>
public static class ItemFactory
{
    public static Item Create(IGameData data, uint refItemId, Rentable? rentable = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var info = data.GetItem(refItemId)
                   ?? throw new UnknownGameObjectException($"Item {refItemId} is not in the game database.");
        return Create(info, rentable);
    }

    public static Item Create(IGameData data, string serverName, Rentable? rentable = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var info = data.GetItem(serverName)
                   ?? throw new UnknownGameObjectException($"Item '{serverName}' is not in the game database.");
        return Create(info, rentable);
    }

    public static Item Create(ItemInfo info, Rentable? rentable = null) => info.Tid2 switch
    {
        1 => new Equipable(info, rentable),
        2 => new CosItem(info, rentable),
        3 => new EtcItem(info, rentable),
        _ => new Item(info, rentable),
    };

    /// <summary>Builds an item sold by an NPC shop (with its fixed plus, durability and magic options).</summary>
    public static Item? CreateFromShop(IGameData data, string npcServerName, byte tab, byte slot)
    {
        ArgumentNullException.ThrowIfNull(data);
        var shop = data.GetShopItem(npcServerName, tab, slot);
        if (shop is null)
            return null;

        var item = Create(data, shop.ItemServerName);
        if (item is Equipable equipable)
        {
            equipable.Plus = shop.Plus;
            equipable.Durability = shop.Durability;
            if (!string.IsNullOrEmpty(shop.MagicParams))
            {
                var options = new List<MagicOption>();
                foreach (var part in shop.MagicParams.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ulong.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out ulong param))
                        continue;
                    uint id = (uint)(param & uint.MaxValue);
                    options.Add(new MagicOption(id, data.GetMagicOption(id)) { Value = (uint)(param >> 32) });
                }
                equipable.MagicOptions = options;
            }
        }
        return item;
    }
}
