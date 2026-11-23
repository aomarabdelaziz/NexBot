using Microsoft.Extensions.Logging;
using NexBot.Game.Items;
using NexBot.Handlers;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>Result of one elixir or stone.</summary>
/// <param name="Answered">The server answered (false: refused, not sent or no answer; see <see cref="Message"/>).</param>
/// <param name="Success">The plus / stone worked.</param>
/// <param name="Destroyed">The item is gone.</param>
public sealed record AlchemyResult(bool Answered, bool Success, bool Destroyed, string Message, ushort ErrorCode = 0);

/// <summary>The game client did an alchemy: the type byte it used (to learn this server's layout).</summary>
public sealed class AlchemyLearnedEventArgs(bool stone, byte type, int slotCount, string hex) : EventArgs
{
    /// <summary>0x7151 (stone) or 0x7150 (elixir).</summary>
    public bool Stone { get; } = stone;
    public byte Type { get; } = type;
    public int SlotCount { get; } = slotCount;
    public string Hex { get; } = hex;
}

/// <summary>Alchemy: elixirs (0x7150) and magic / attribute stones (0x7151).</summary>
public sealed partial class SroClient
{
    private static readonly TimeSpan AlchemyTimeout = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _alchemyGate = new(1, 1);
    private TaskCompletionSource<AlchemyResult>? _alchemyAnswer;
    private byte _alchemySlot;

    /// <summary>The game client sent an alchemy request (see <see cref="AlchemyLearnedEventArgs"/>).</summary>
    public event EventHandler<AlchemyLearnedEventArgs>? AlchemyLearned;

    internal void RaiseAlchemyLearned(bool stone, byte type, int count, string hex) =>
        Raise(AlchemyLearned, new AlchemyLearnedEventArgs(stone, type, count, hex));

    /// <summary>Fuses an elixir (and lucky powder) into an inventory item and waits for the result.</summary>
    public Task<AlchemyResult> FuseElixirAsync(byte itemSlot, byte elixirSlot, byte? powderSlot, byte elixirType,
        CancellationToken cancellationToken = default)
    {
        byte[] slots = powderSlot is { } p ? [itemSlot, elixirSlot, p] : [itemSlot, elixirSlot];
        return AlchemyAsync(ClientPackets.AlchemyFuse(Opcodes.Client.AlchemyReinforce, elixirType, slots), itemSlot, slots[1..], cancellationToken);
    }

    /// <summary>Fuses a magic or attribute stone (and a catalyst) into an inventory item and waits for the result.</summary>
    public Task<AlchemyResult> FuseStoneAsync(byte itemSlot, byte stoneSlot, byte? catalystSlot, byte stoneType,
        CancellationToken cancellationToken = default)
    {
        byte[] slots = catalystSlot is { } c ? [itemSlot, stoneSlot, c] : [itemSlot, stoneSlot];
        return AlchemyAsync(ClientPackets.AlchemyFuse(Opcodes.Client.AlchemyStone, stoneType, slots), itemSlot, slots[1..], cancellationToken);
    }

    private async Task<AlchemyResult> AlchemyAsync(Packet packet, byte itemSlot, byte[] used, CancellationToken cancellationToken)
    {
        if (!IsInGame || World.Character is null)
            return new(false, false, false, "The character is not in game.");
        await _alchemyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var answer = new TaskCompletionSource<AlchemyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _alchemyAnswer = answer;
            _alchemySlot = itemSlot;
            if (!Send(packet))
                return new(false, false, false, "The bot is not connected.");
            AlchemyResult result;
            try
            {
                result = await answer.Task.WaitAsync(AlchemyTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new(false, false, false, "No answer from the server.");
            }
            if (result.Answered)
                Consume(used); // the elixir / stone / powder / catalyst is used up either way
            return result;
        }
        finally
        {
            _alchemyAnswer = null;
            _alchemyGate.Release();
        }
    }

    private void Consume(byte[] slots)
    {
        if (World.Character?.Inventory is not { } inventory)
            return;
        foreach (byte slot in slots)
        {
            if (inventory[slot] is not { } item)
                continue;
            if (item.Quantity > 1)
                item.Quantity--;
            else
                inventory[slot] = null;
        }
    }

    /// <summary>
    /// 0xB150 / 0xB151: [1][action 2][bool success][slot] then the item (some servers put one more byte before it;
    /// nothing when it was destroyed), or [2][ushort error].
    /// </summary>
    internal void OnAlchemyAnswer(Packet p)
    {
        if (_alchemyAnswer is not { } answer)
            return;
        if (!p.ReadSuccess())
        {
            ushort error = p.ReadErrorCode();
            answer.TrySetResult(new(false, false, false, $"The server refused (error 0x{error:X4}).", error));
            return;
        }
        byte action = p.ReadByte();
        if (action != 2 || p.Remaining < 2)
        {
            answer.TrySetResult(new(true, false, false, $"Unexpected alchemy answer (action {action})."));
            return;
        }
        bool success = p.ReadBool();
        byte slot = p.ReadByte();
        if (slot != _alchemySlot)
            Logger.LogDebug("Alchemy answer for slot {Slot}, expected {Expected}", slot, _alchemySlot);
        byte[] rest = p.ReadBytes(p.Remaining);
        var inventory = World.Character?.Inventory;
        var old = inventory?[slot] as Equipable;

        var parsed = TryReadItem(rest, 0, old) ?? TryReadItem(rest, 1, old);
        if (parsed is not null && inventory is not null)
        {
            inventory[slot] = parsed;
            answer.TrySetResult(new(true, success, false, success ? "Success." : "Failed."));
            return;
        }
        if (!success && rest.Length <= 1)
        {
            if (inventory is not null)
                inventory[slot] = null;
            answer.TrySetResult(new(true, false, true, "The item was destroyed."));
            return;
        }
        // Could not read the item: guess the new plus.
        if (old is not null && Opcode(p) == Opcodes.Server.AlchemyReinforceResponse)
            old.Plus = success ? (byte)(old.Plus + 1) : (byte)0;
        answer.TrySetResult(new(true, success, false, success ? "Success." : "Failed."));
    }

    private static ushort Opcode(Packet p) => p.Opcode;

    private Equipable? TryReadItem(byte[] bytes, int skip, Equipable? old)
    {
        if (bytes.Length <= skip + 8)
            return null;
        try
        {
            var item = new Packet(0, bytes.AsSpan(skip)).ReadItem(Data);
            return item is Equipable e && (old is null || e.Id == old.Id) ? e : null;
        }
        catch (Exception ex) when (ex is PacketException or UnknownGameObjectException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
