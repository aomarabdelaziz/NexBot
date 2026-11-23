using System.Collections.Concurrent;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Records a walk script while the player moves the character (in the game client, or by clicking on the bot's map):
/// <list type="bullet">
/// <item>a MOVE every <see cref="MinDistance"/> units walked, and where the character stops;</item>
/// <item>a TELEPORT when a teleporter is used (the game's 0x705A: teleporter and destination), and a RETURN for a
/// return scroll; points are paused through the loading screen and resume at the arrival point;</item>
/// <item>BUY / SELL at NPC shops (into / out of the transport or the bag), written once the server accepted them;
/// repeated purchases of the same goods become one line with a count;</item>
/// <item>RIDE when a mount scroll is used or the character gets on, DISMOUNT when it gets off.</item>
/// </list>
/// </summary>
public sealed class ScriptRecorder : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly SroClient _client;
    private readonly Lock _lock = new();
    private readonly List<WalkCommand> _commands = [];
    private CancellationTokenSource? _cts;
    private Position? _last;
    private Position? _previous;
    private bool _teleporting;
    private DateTime _teleportedAt;
    /// <summary>Last talk option sent to each NPC (it opened the shop the next purchase comes from).</summary>
    private readonly ConcurrentDictionary<uint, byte> _talk = new();
    /// <summary>Shop moves sent, waiting for the server's answer (in order).</summary>
    private readonly ConcurrentQueue<(ItemMovementType Type, WalkCommand Command)> _pendingShop = new();

    public ScriptRecorder(SroClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Distance between recorded points while walking (world units).</summary>
    public double MinDistance { get; set; } = 25;

    /// <summary>Points closer than this to the last one are not recorded (also when the character stops).</summary>
    public double MinStop { get; set; } = 4;

    /// <summary>
    /// A point that only continues the last straight line (turning less than <see cref="StraightAngle"/> degrees)
    /// moves the last point instead of adding one, up to this long a line: fewer, longer moves walk smoother.
    /// </summary>
    public double MaxStraight { get; set; } = 150;

    /// <summary>Largest turn (degrees) still counted as a straight line.</summary>
    public double StraightAngle { get; set; } = 10;

    public bool IsRecording { get; private set; }

    /// <summary>Raised (on a background thread) whenever a line was added.</summary>
    public event EventHandler? Changed;

    /// <summary>The recorded lines so far.</summary>
    public IReadOnlyList<WalkCommand> Commands
    {
        get { lock (_lock) return [.. _commands]; }
    }

    /// <summary>Starts recording, appending to what was recorded before (<see cref="Clear"/> to start over).</summary>
    public string? Start()
    {
        if (IsRecording)
            return null;
        if (!_client.IsInGame || _client.World.Character is null)
            return "The character is not in game.";

        _cts = new CancellationTokenSource();
        IsRecording = true;
        _teleporting = false;
        _previous = null;
        _client.PacketSent += OnPacketSent;
        _client.StateChanged += OnStateChanged;
        _client.ItemMoved += OnItemMoved;
        _pendingShop.Clear();
        AddMove(Here(), force: _last is null);
        _ = Task.Run(() => LoopAsync(_cts.Token));
        return null;
    }

    public void Stop()
    {
        if (!IsRecording)
            return;
        IsRecording = false;
        _client.PacketSent -= OnPacketSent;
        _client.StateChanged -= OnStateChanged;
        _client.ItemMoved -= OnItemMoved;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (!_teleporting && _client.World.Character is not null)
            AddMove(Here(), force: false); // where it ended
    }

    public void Clear()
    {
        lock (_lock)
            _commands.Clear();
        _last = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a WAIT line (e.g. to let the character rest or buff at this point).</summary>
    public void AddWait(TimeSpan delay) => Add(new WalkCommand(WalkCommandType.Wait, Delay: delay));

    private Position Here() => (_client.World.Mover ?? _client.World.Character!).EstimatePosition();

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Poll, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // A teleport / return scroll that never happened (cancelled cast, refused): go on recording.
            if (_teleporting && DateTime.UtcNow - _teleportedAt > TimeSpan.FromSeconds(45) && _client.IsInGame)
                _teleporting = false;
            if (_teleporting || !_client.IsInGame || _client.World.Character is null)
                continue;

            var here = Here();
            if (_last is { } last && (last.InDungeon != here.InDungeon || last.DistanceTo(here) >= MinDistance))
                AddMove(here, force: true);
            else if (_previous is { } prev && prev.DistanceTo(here) < 0.5)
                AddMove(here, force: false); // stopped: mark the spot
            _previous = here;
        }
    }

    private void AddMove(Position position, bool force)
    {
        if (!force && _last is { } last && last.InDungeon == position.InDungeon && last.DistanceTo(position) < MinStop)
            return;
        _last = position;
        var move = new WalkCommand(WalkCommandType.Move, position);
        bool merged = false;
        lock (_lock)
        {
            int n = _commands.Count;
            if (n >= 2 && _commands[n - 1] is { Type: WalkCommandType.Move } b && _commands[n - 2] is { Type: WalkCommandType.Move } a
                && IsStraight(a.Position, b.Position, position))
            {
                _commands[n - 1] = move;
                merged = true;
            }
        }
        if (merged)
            Changed?.Invoke(this, EventArgs.Empty);
        else
            Add(move);
    }

    /// <summary>True when a → b → c is (nearly) one straight line no longer than <see cref="MaxStraight"/>.</summary>
    private bool IsStraight(Position a, Position b, Position c)
    {
        if (a.InDungeon != c.InDungeon || b.InDungeon != c.InDungeon || a.DistanceTo(c) > MaxStraight)
            return false;
        double x1 = b.PosX - a.PosX, y1 = b.PosY - a.PosY, x2 = c.PosX - b.PosX, y2 = c.PosY - b.PosY;
        double l1 = Math.Sqrt(x1 * x1 + y1 * y1), l2 = Math.Sqrt(x2 * x2 + y2 * y2);
        if (l1 < 0.5 || l2 < 0.5)
            return false;
        double cos = (x1 * x2 + y1 * y2) / (l1 * l2);
        return cos >= Math.Cos(StraightAngle * Math.PI / 180);
    }

    private void Add(WalkCommand command)
    {
        lock (_lock)
            _commands.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPacketSent(object? sender, PacketSentEventArgs e)
    {
        var p = e.Packet;
        try
        {
            switch (p.Opcode)
            {
                case Opcodes.Client.TeleportUse:
                {
                    uint uniqueId = p.ReadUInt32();
                    byte type = p.ReadByte();
                    // A gate between towns, or a teleport NPC (soldier across town, ferry, tunnel ...).
                    if (type != 2 || Teleporters.Find(_client, uniqueId) is not { } teleporter)
                        return;
                    uint destination = p.ReadUInt32();
                    AddMove(Here(), force: false); // stand at the teleporter
                    Add(new WalkCommand(WalkCommandType.Teleport, Teleporter: teleporter.Entity.ServerName, DestinationId: destination,
                        Label: $"{teleporter.Name} → {teleporter.DestinationName(destination)}".Replace(",", " ", StringComparison.Ordinal)));
                    (_teleporting, _teleportedAt) = (true, DateTime.UtcNow);
                    break;
                }
                case Opcodes.Client.ItemUse:
                {
                    byte slot = p.ReadByte();
                    var character = _client.World.Character;
                    if (character?.Inventory[slot] is not { } item)
                        break;
                    if (AutoWalker.IsReturnScroll(item.ServerName))
                    {
                        AddMove(Here(), force: false);
                        Add(new WalkCommand(WalkCommandType.Return));
                        (_teleporting, _teleportedAt) = (true, DateTime.UtcNow);
                    }
                    else if (item.IsType(3, 3, 2) && item.ServerName.StartsWith("ITEM_COS_", StringComparison.Ordinal) && !character.IsRiding)
                    {
                        // A trade transport rides the preferred one (AutoTrade page); other mounts ride that exact scroll.
                        AddMove(Here(), force: false);
                        Add(new WalkCommand(WalkCommandType.Ride, Target: TradeCatalog.IsTradeTransport(item.Info) ? ShopTarget.Transport : item.ServerName));
                    }
                    break;
                }
                case Opcodes.Client.PetMount:
                {
                    bool mount = p.ReadBool();
                    if (mount)
                    {
                        // Getting on right after summoning is part of that RIDE.
                        if (LastCommand() is not { Type: WalkCommandType.Ride })
                            Add(new WalkCommand(WalkCommandType.Ride));
                    }
                    else if (LastCommand() is not { Type: WalkCommandType.Dismount })
                    {
                        AddMove(Here(), force: false);
                        Add(new WalkCommand(WalkCommandType.Dismount));
                    }
                    break;
                }
                case Opcodes.Client.EntityTalk:
                {
                    uint npc = p.ReadUInt32();
                    _talk[npc] = p.ReadByte();
                    break;
                }
                case Opcodes.Client.ItemMovement:
                    OnShopMove(p);
                    break;
            }
        }
        catch (Exception ex) when (ex is NexBot.Protocol.PacketException or ArgumentException or InvalidOperationException)
        {
            // not the packet layout we expected: nothing to record
        }
    }

    private WalkCommand? LastCommand()
    {
        lock (_lock)
            return _commands.Count > 0 ? _commands[^1] : null;
    }

    private string NpcName(uint uniqueId) => _client.World.GetEntity(uniqueId)?.ServerName ?? "";

    private byte TalkFor(uint npc, string target) => _talk.TryGetValue(npc, out byte talk) ? talk : ShopTarget.DefaultTalk(target);

    /// <summary>A buy / sell sent to an NPC shop: kept until the server accepts it.</summary>
    private void OnShopMove(NexBot.Protocol.Packet p)
    {
        var type = (ItemMovementType)p.ReadByte();
        WalkCommand? command = null;
        switch (type)
        {
            case ItemMovementType.ShopToTransport:
            case ItemMovementType.ShopToInventory:
            {
                if (type == ItemMovementType.ShopToTransport)
                    p.ReadUInt32(); // 0 (the transport ridden gets the goods)
                byte tab = p.ReadByte(), slot = p.ReadByte();
                ushort quantity = p.ReadUInt16();
                uint npc = p.ReadUInt32();
                string target = type == ItemMovementType.ShopToTransport ? ShopTarget.Transport : ShopTarget.Inventory;
                if (NpcName(npc) is { Length: > 0 } name)
                    command = new WalkCommand(WalkCommandType.Buy, Npc: name, Target: target, Tab: tab, Slot: slot, Quantity: quantity,
                        Times: 1, Talk: TalkFor(npc, target));
                break;
            }
            case ItemMovementType.TransportToShop:
            {
                p.ReadUInt32(); // transport
                p.ReadByte();   // slot
                p.ReadUInt16(); // quantity
                uint npc = p.ReadUInt32();
                if (NpcName(npc) is { Length: > 0 } name)
                    command = new WalkCommand(WalkCommandType.Sell, Npc: name, Target: ShopTarget.Transport, Talk: TalkFor(npc, ShopTarget.Transport));
                break;
            }
            case ItemMovementType.InventoryToShop:
            {
                byte slot = p.ReadByte();
                ushort quantity = p.ReadUInt16();
                uint npc = p.ReadUInt32();
                if (NpcName(npc) is { Length: > 0 } name && _client.World.Character?.Inventory[slot] is { } item)
                    command = new WalkCommand(WalkCommandType.Sell, Npc: name, Target: item.ServerName, Quantity: quantity,
                        Talk: TalkFor(npc, ShopTarget.Inventory));
                break;
            }
        }
        if (command is not null)
            _pendingShop.Enqueue((type, command));
    }

    private void OnItemMoved(object? sender, ItemMovedEventArgs e)
    {
        if (!_pendingShop.TryPeek(out var pending))
            return;
        if (!e.Success)
        {
            _pendingShop.TryDequeue(out _); // refused: not recorded
            return;
        }
        if (e.Type != pending.Type)
            return; // some other move (inventory sort, pick up ...)
        _pendingShop.TryDequeue(out _);
        RecordShop(pending.Command);
    }

    /// <summary>Adds an accepted buy / sell, folding repeats into the last line.</summary>
    private void RecordShop(WalkCommand command)
    {
        bool folded = false;
        lock (_lock)
        {
            if (_commands.Count > 0 && _commands[^1] is var last && last.Type == command.Type
                && string.Equals(last.Npc, command.Npc, StringComparison.Ordinal) && string.Equals(last.Target, command.Target, StringComparison.Ordinal))
            {
                if (command.Type == WalkCommandType.Buy)
                {
                    if (last.Tab == command.Tab && last.Slot == command.Slot && last.Quantity == command.Quantity)
                    {
                        _commands[^1] = last with { Times = last.Times + 1 }; // bought the same again
                        folded = true;
                    }
                }
                else if (command.Target == ShopTarget.Transport)
                    folded = true; // one line sells all the cargo
                else
                {
                    _commands[^1] = last with { Quantity = (ushort)Math.Min(ushort.MaxValue, last.Quantity + command.Quantity) };
                    folded = true;
                }
            }
        }
        if (folded)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        AddMove(Here(), force: false); // stand at the shop
        Add(command);
    }

    private void OnStateChanged(object? sender, ClientStateChangedEventArgs e)
    {
        if (e.Current != ClientState.InGame || !_teleporting)
            return;
        // Arrived after the loading screen: continue from here.
        _teleporting = false;
        _previous = null;
        if (_client.World.Character is not null)
            AddMove(Here(), force: true);
    }

    public void Dispose() => Stop();
}
