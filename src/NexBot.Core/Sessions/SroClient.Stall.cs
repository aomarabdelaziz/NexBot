using Microsoft.Extensions.Logging;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>One slot of our stall.</summary>
public sealed record StallSlot(byte Slot, byte InventorySlot, string ItemServerName, string ItemName, ushort Quantity, ulong Price);

/// <summary>Our own stall, as far as the bot knows it.</summary>
public sealed class MyStall
{
    /// <summary>Most slots a stall has.</summary>
    public const int Capacity = 10;

    /// <summary>The stall window exists (created, maybe not open to buyers yet).</summary>
    public bool Exists { get; internal set; }
    /// <summary>Open to buyers.</summary>
    public bool IsOpen { get; internal set; }
    public string Title { get; internal set; } = "";
    public string Note { get; internal set; } = "";
    private readonly List<StallSlot> _slots = [];
    public IReadOnlyList<StallSlot> Slots => _slots;

    internal void Set(StallSlot slot)
    {
        _slots.RemoveAll(s => s.Slot == slot.Slot);
        _slots.Add(slot);
        _slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
    }

    internal StallSlot? Remove(byte slot)
    {
        var found = _slots.FirstOrDefault(s => s.Slot == slot);
        if (found is not null)
            _slots.Remove(found);
        return found;
    }

    internal void Reset()
    {
        Exists = IsOpen = false;
        Title = Note = "";
        _slots.Clear();
    }

    /// <summary>The lowest free stall slot, or null when full.</summary>
    public byte? FreeSlot()
    {
        for (byte i = 0; i < Capacity; i++)
            if (_slots.All(s => s.Slot != i))
                return i;
        return null;
    }
}

/// <summary>Someone bought from our stall.</summary>
public sealed class StallSoldEventArgs(string buyer, StallSlot slot) : EventArgs
{
    public string Buyer { get; } = buyer;
    public StallSlot Slot { get; } = slot;
}

/// <summary>The add-item bytes after the price, as the game client sends them (hex).</summary>
public sealed class StallLayoutEventArgs(string trailerHex) : EventArgs
{
    public string TrailerHex { get; } = trailerHex;
}

/// <summary>Result of a stall request.</summary>
public sealed record StallResult(bool Ok, string Message, ushort ErrorCode = 0);

/// <summary>Our stall: create, add / change / remove items, open, close (0x70B1 / 0x70BA / 0x70B2).</summary>
public sealed partial class SroClient
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _stallGate = new(1, 1);
    private TaskCompletionSource<(bool Ok, ushort Error)>? _stallAnswer;

    public MyStall Stall { get; } = new();

    /// <summary>
    /// Bytes after the price in the add-item packet (0x70BA type 2). Defaults to the flea market group; replaced by
    /// what the game client sends when the player adds an item to a stall there (<see cref="StallLayoutLearned"/>).
    /// </summary>
    public byte[] StallAddTrailer { get; set; } = [.. ClientPackets.DefaultStallAddTrailer];

    /// <summary>The game client added a stall item: the bytes after its price (hex), to save.</summary>
    public event EventHandler<StallLayoutEventArgs>? StallLayoutLearned;

    /// <summary>Looks at stall packets from the game client: logs them and learns the add-item layout.</summary>
    internal void ObserveStallPacket(Packet packet)
    {
        string hex = Convert.ToHexString(packet.Payload);
        Logger.LogInformation("Stall packet from the game client: 0x{Opcode:X4} {Hex}", packet.Opcode, hex);
        // [2][stall slot][inventory slot][ushort quantity][ulong price][trailer…]
        if (packet.Opcode == Opcodes.Client.StallUpdate && packet.Payload.Length >= 13 && packet.Payload[0] == (byte)Game.StallUpdateType.ItemAdded)
        {
            byte[] trailer = packet.Payload[13..].ToArray();
            if (!trailer.AsSpan().SequenceEqual(StallAddTrailer))
            {
                StallAddTrailer = trailer;
                Logger.LogInformation("Stall: learned the add-item layout from the game client (after the price: {Trailer})",
                    trailer.Length == 0 ? "nothing" : Convert.ToHexString(trailer));
                Raise(StallLayoutLearned, new StallLayoutEventArgs(Convert.ToHexString(trailer)));
            }
        }
    }

    /// <summary>Our stall changed (created, item added / sold, opened, closed).</summary>
    public event EventHandler<EventArgs>? StallChanged;
    public event EventHandler<StallSoldEventArgs>? StallSold;

    internal void RaiseStallChanged() => Raise(StallChanged, EventArgs.Empty);
    internal void RaiseStallSold(string buyer, StallSlot slot) => Raise(StallSold, new StallSoldEventArgs(buyer, slot));

    /// <summary>A stall answer (create 0xB0B1, update 0xB0BA, close 0xB0B2) for the request waiting on it.</summary>
    internal void CompleteStallRequest(bool ok, ushort error) => _stallAnswer?.TrySetResult((ok, error));

    private async Task<StallResult> StallRequestAsync(Packet packet, string what, Action onSuccess, CancellationToken cancellationToken)
    {
        if (!IsInGame || World.Character is null)
            return new(false, "The character is not in game.");
        await _stallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var answer = new TaskCompletionSource<(bool Ok, ushort Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stallAnswer = answer;
            if (!Send(packet))
                return new(false, "The bot is not connected.");
            try
            {
                var (ok, error) = await answer.Task.WaitAsync(StallTimeout, cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    Logger.LogWarning("Stall: {What} refused (error 0x{Error:X4})", what, error);
                    return new(false, $"The server refused to {what} (error 0x{error:X4}).", error);
                }
                onSuccess();
                RaiseStallChanged();
                return new(true, "");
            }
            catch (TimeoutException)
            {
                return new(false, $"No answer from the server to {what}.");
            }
        }
        finally
        {
            _stallAnswer = null;
            _stallGate.Release();
        }
    }

    /// <summary>Creates our stall with a title (not open to buyers until <see cref="OpenStallAsync"/>).</summary>
    public Task<StallResult> CreateStallAsync(string title, CancellationToken cancellationToken = default)
    {
        if (World.Character is { IsRiding: true })
            return Task.FromResult(new StallResult(false, "Get off the horse or transport first."));
        return StallRequestAsync(ClientPackets.StallCreate(title), "create the stall", () =>
        {
            Stall.Reset();
            Stall.Exists = true;
            Stall.Title = title;
        }, cancellationToken);
    }

    public Task<StallResult> SetStallNoteAsync(string note, CancellationToken cancellationToken = default) =>
        StallRequestAsync(ClientPackets.StallNote(note), "set the stall note", () => Stall.Note = note, cancellationToken);

    public Task<StallResult> SetStallTitleAsync(string title, CancellationToken cancellationToken = default) =>
        StallRequestAsync(ClientPackets.StallTitle(title), "set the stall title", () => Stall.Title = title, cancellationToken);

    /// <summary>Puts an inventory slot into a stall slot for a price.</summary>
    public Task<StallResult> AddStallItemAsync(byte stallSlot, byte inventorySlot, ushort quantity, ulong price,
        CancellationToken cancellationToken = default)
    {
        if (World.Character?.Inventory[inventorySlot] is not { } item)
            return Task.FromResult(new StallResult(false, $"Inventory slot {inventorySlot} is empty."));
        return StallRequestAsync(ClientPackets.StallAddItem(stallSlot, inventorySlot, quantity, price, StallAddTrailer), "add an item to the stall",
            () => Stall.Set(new StallSlot(stallSlot, inventorySlot, item.ServerName, item.Name, quantity, price)), cancellationToken);
    }

    /// <summary>Changes the price (and quantity) of a stall slot.</summary>
    public Task<StallResult> UpdateStallItemAsync(byte stallSlot, ushort quantity, ulong price, CancellationToken cancellationToken = default)
    {
        if (Stall.Slots.FirstOrDefault(s => s.Slot == stallSlot) is not { } slot)
            return Task.FromResult(new StallResult(false, $"Stall slot {stallSlot} is empty."));
        return StallRequestAsync(ClientPackets.StallUpdateItem(stallSlot, quantity, price), "change the stall price",
            () => Stall.Set(slot with { Quantity = quantity, Price = price }), cancellationToken);
    }

    public Task<StallResult> RemoveStallItemAsync(byte stallSlot, CancellationToken cancellationToken = default) =>
        StallRequestAsync(ClientPackets.StallRemoveItem(stallSlot), "remove an item from the stall", () => Stall.Remove(stallSlot), cancellationToken);

    /// <summary>Opens the stall to buyers (true) or closes it for editing (false).</summary>
    public Task<StallResult> OpenStallAsync(bool open = true, CancellationToken cancellationToken = default) =>
        StallRequestAsync(ClientPackets.StallState(open), open ? "open the stall" : "pause the stall", () => Stall.IsOpen = open, cancellationToken);

    /// <summary>Closes the stall (the items stay in the inventory). A refusal means there is no stall either way.</summary>
    public async Task<StallResult> CloseStallAsync(CancellationToken cancellationToken = default)
    {
        var result = await StallRequestAsync(ClientPackets.StallDestroy(), "close the stall", Stall.Reset, cancellationToken).ConfigureAwait(false);
        if (!result.Ok && result.ErrorCode != 0 && Stall.Exists)
        {
            Stall.Reset();
            RaiseStallChanged();
        }
        return result;
    }

    /// <summary>A buyer bought stall slot <paramref name="slot"/>: the item leaves the inventory.</summary>
    internal void OnStallSold(byte slot, string buyer)
    {
        if (Stall.Remove(slot) is not { } sold)
            return;
        if (World.Character is { } c && c.Inventory[sold.InventorySlot] is { } item)
        {
            if (item.Quantity > sold.Quantity && sold.Quantity > 0)
                item.Quantity -= sold.Quantity;
            else
                c.Inventory[sold.InventorySlot] = null;
        }
        Logger.LogInformation("Stall: {Buyer} bought {Item} x{Quantity} for {Price:N0} gold", buyer, sold.ItemName, sold.Quantity, sold.Price);
        RaiseStallSold(buyer, sold);
        RaiseStallChanged();
    }

    /// <summary>The server closed our stall (0x30B9 for our character).</summary>
    internal void OnStallGone()
    {
        if (!Stall.Exists)
            return;
        Stall.Reset();
        RaiseStallChanged();
    }
}
