using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>One inventory move: <paramref name="Amount"/> null moves the whole stack.</summary>
public sealed record SortStep(byte From, byte To, ushort? Amount, bool Merge);

/// <summary>What a sort did.</summary>
public sealed record SortResult(bool Ok, int Merged, int Moved, string Message);

/// <summary>
/// Auto sort for the bag (slots 13+; equipment is never touched):
/// <list type="number">
/// <item><b>Stack:</b> stacks of the same item that are not full are merged into the first one, as much as the item's
/// maximum stack allows (39 + 11 Global Chatting → 50 in one slot).</item>
/// <item><b>Sort:</b> items are packed from the first bag slot, grouped by kind (equipment, pets, then potions,
/// scrolls, stones ...), the same item next to each other, full stacks first.</item>
/// </list>
/// Each step is planned from the inventory as it is after the server's last answer, so a refused or changed move does
/// not break the plan.
/// </summary>
public static class InventorySorter
{
    /// <summary>Safety limit for one run (a full bag needs far fewer).</summary>
    public const int MaxSteps = 600;

    /// <summary>The next merge, or null when no two stacks of the same item can be put together.</summary>
    public static SortStep? NextMerge(SlotList<Item> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var open = new Dictionary<(uint Id, byte Assimilation), int>(); // first non-full stack of each item
        for (int slot = Character.FirstInventorySlot; slot < inventory.Capacity; slot++)
        {
            if (inventory[slot] is not { } item || !CanMerge(item) || item.Quantity >= item.QuantityMax)
                continue;
            var key = MergeKey(item);
            if (!open.TryGetValue(key, out int target))
            {
                open[key] = slot;
                continue;
            }
            var into = inventory[target]!;
            ushort amount = (ushort)Math.Min(item.Quantity, into.QuantityMax - into.Quantity);
            return new SortStep((byte)slot, (byte)target, amount, Merge: true);
        }
        return null;
    }

    /// <summary>The next move that puts an item where the sorted order wants it, or null when the bag is sorted.</summary>
    public static SortStep? NextSort(SlotList<Item> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        int first = Character.FirstInventorySlot;
        var items = new List<(int Slot, Item Item)>();
        for (int slot = first; slot < inventory.Capacity; slot++)
            if (inventory[slot] is { } item)
                items.Add((slot, item));

        var wanted = items.Select(x => x.Item).OrderBy(SortKey, SortKeyComparer.Instance).ToList();
        for (int i = 0; i < wanted.Count; i++)
        {
            int slot = first + i;
            var want = SortKey(wanted[i]);
            if (inventory[slot] is { } here && SortKeyComparer.Instance.Compare(SortKey(here), want) == 0)
                continue; // already right (same item and amount: interchangeable)
            // Everything before this slot is in place, so the wanted item is further down.
            for (int from = slot + 1; from < inventory.Capacity; from++)
                if (inventory[from] is { } candidate && SortKeyComparer.Instance.Compare(SortKey(candidate), want) == 0)
                    return new SortStep((byte)from, (byte)slot, null, Merge: false);
            return null; // should not happen
        }
        return null;
    }

    /// <summary>Stacks, then sorts. Stops at the first refused move.</summary>
    public static async Task<SortResult> RunAsync(SroClient client, IProgress<string>? progress = null,
        TimeSpan? pause = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var delay = pause ?? TimeSpan.FromMilliseconds(150);
        int merged = 0, moved = 0;
        for (int step = 0; step < MaxSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            if (client.World.Character?.Inventory is not { } inventory)
                return new(false, merged, moved, "The character is not in game.");

            var next = NextMerge(inventory) ?? NextSort(inventory);
            if (next is null)
                return new(true, merged, moved, merged + moved == 0
                    ? "The inventory is already sorted."
                    : $"Sorted: {merged} stack(s) merged, {moved} item(s) moved.");

            var item = inventory[next.From]!;
            progress?.Report(next.Merge ? $"Stacking {item.Name}..." : $"Sorting: {item.Name}");
            var result = await client.MoveInventoryItemAsync(next.From, next.To, next.Amount, cancellationToken: token).ConfigureAwait(false);
            if (!result.Ok)
            {
                client.Logger.LogWarning("Auto sort: moving {Item} from {From} to {To} failed: {Message}", item.Name, next.From, next.To, result.Message);
                return new(false, merged, moved, $"Stopped at {item.Name}: {result.Message}");
            }
            if (next.Merge)
                merged++;
            else
                moved++;
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        return new(false, merged, moved, "Stopped: too many moves.");
    }

    /// <summary>Stackable, not rented (time-limited items stay apart).</summary>
    private static bool CanMerge(Item item) => item.QuantityMax > 1 && item.Rentable.Type == RentableType.None;

    /// <summary>Alchemy stones only stack with the same assimilation.</summary>
    private static (uint, byte) MergeKey(Item item) => (item.Id, item is EtcItem etc ? etc.AssimilationProbability : (byte)0);

    private static (byte Tid2, byte Tid3, byte Tid4, uint Id, byte Assimilation, int Quantity) SortKey(Item item) =>
        (item.TypeId2, item.TypeId3, item.TypeId4, item.Id, item is EtcItem etc ? etc.AssimilationProbability : (byte)0, item.Quantity);

    private sealed class SortKeyComparer : IComparer<(byte Tid2, byte Tid3, byte Tid4, uint Id, byte Assimilation, int Quantity)>
    {
        public static readonly SortKeyComparer Instance = new();

        public int Compare((byte Tid2, byte Tid3, byte Tid4, uint Id, byte Assimilation, int Quantity) x,
            (byte Tid2, byte Tid3, byte Tid4, uint Id, byte Assimilation, int Quantity) y)
        {
            int c = x.Tid2.CompareTo(y.Tid2);
            if (c == 0) c = x.Tid3.CompareTo(y.Tid3);
            if (c == 0) c = x.Tid4.CompareTo(y.Tid4);
            if (c == 0) c = x.Id.CompareTo(y.Id);
            if (c == 0) c = x.Assimilation.CompareTo(y.Assimilation);
            if (c == 0) c = y.Quantity.CompareTo(x.Quantity); // full stacks first
            return c;
        }
    }
}
