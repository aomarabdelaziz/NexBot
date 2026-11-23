using System.Globalization;
using NexBot.Game;

namespace NexBot.Automation;

public enum WalkCommandType
{
    Move,
    Wait,
    Repeat,
    /// <summary>Use a teleporter (gate / NPC) near the character to go to a destination.</summary>
    Teleport,
    /// <summary>Use a return scroll (back to the last town / recall point).</summary>
    Return,
    /// <summary>Buy from an NPC shop, into the transport (trade goods) or the inventory.</summary>
    Buy,
    /// <summary>Sell to an NPC: everything the transport carries, or an inventory item.</summary>
    Sell,
    /// <summary>Summon and get on a mount (the preferred trade transport, a named scroll, or the one out already).</summary>
    Ride,
    /// <summary>Get off the mount.</summary>
    Dismount,
    /// <summary>
    /// Stop and do the town business (storage, repair, sell, buy) with the NPC standing here (phBot's DoStorage,
    /// DoBlacksmith ...). <see cref="WalkCommand.Target"/> names the NPC kind, for the reader.
    /// </summary>
    Visit,
}

/// <summary>Where a BUY puts the item, and what a SELL sells.</summary>
public static class ShopTarget
{
    /// <summary>The transport's cargo (trade goods).</summary>
    public const string Transport = "TRANSPORT";
    /// <summary>The character's bag.</summary>
    public const string Inventory = "INVENTORY";

    /// <summary>NPC talk option that opens a specialty trader's goods.</summary>
    public const byte TradeTalk = 0x0C;
    /// <summary>NPC talk option that opens an ordinary shop.</summary>
    public const byte StoreTalk = 1;

    public static byte DefaultTalk(string target) =>
        string.Equals(target, Transport, StringComparison.OrdinalIgnoreCase) ? TradeTalk : StoreTalk;
}

/// <summary>One line of a walk script.</summary>
/// <param name="Teleporter">TELEPORT: server name of the teleporter (STORE_CH_GATE, NPC_CH_FERRY ...).</param>
/// <param name="DestinationId">TELEPORT: the destination's teleport ID (as sent by the game).</param>
/// <param name="Label">TELEPORT: readable "from → to" text kept after the IDs.</param>
/// <param name="Npc">BUY / SELL: server name of the shop NPC (NPC_CH_SPECIAL ...).</param>
/// <param name="Target">BUY: TRANSPORT or INVENTORY. SELL: TRANSPORT (all cargo) or an item's server name.
/// RIDE: TRANSPORT (the preferred trade transport), an item's server name, or empty (the mount already out).</param>
/// <param name="Quantity">BUY: amount per purchase. SELL of an item: amount (0 = all of it).</param>
/// <param name="Times">BUY: how many purchases (0 = until the server refuses, e.g. the transport is full).</param>
/// <param name="Talk">BUY / SELL: the NPC talk option that opens the shop.</param>
public sealed record WalkCommand(
    WalkCommandType Type, Position Position = default, TimeSpan Delay = default, int LineNumber = 0,
    string Teleporter = "", uint DestinationId = 0, string Label = "",
    string Npc = "", string Target = "", byte Tab = 0, byte Slot = 0, ushort Quantity = 0, int Times = 1, byte Talk = 0)
{
    /// <summary>The line as written in a script file.</summary>
    public string ToLine()
    {
        var ci = CultureInfo.InvariantCulture;
        return Type switch
        {
            WalkCommandType.Move when Position.InDungeon =>
                string.Create(ci, $"MOVE,{Position.Region},{Position.X},{Position.Y},{Position.Z}"),
            WalkCommandType.Move => string.Create(ci, $"MOVE,{Position.PosX:0},{Position.PosY:0}"),
            WalkCommandType.Wait => string.Create(ci, $"WAIT,{(int)Delay.TotalMilliseconds}"),
            WalkCommandType.Repeat => "REPEAT",
            WalkCommandType.Teleport => string.Create(ci, $"TELEPORT,{Teleporter},{DestinationId}") + (Label.Length > 0 ? "," + Label : ""),
            WalkCommandType.Return => "RETURN",
            WalkCommandType.Buy => string.Create(ci, $"BUY,{Npc},{Target},{Tab},{Slot},{Quantity},{Times},{Talk}"),
            WalkCommandType.Sell => string.Create(ci, $"SELL,{Npc},{Target},{Quantity},{Talk}"),
            WalkCommandType.Ride => Target.Length > 0 ? "RIDE," + Target : "RIDE",
            WalkCommandType.Dismount => "DISMOUNT",
            WalkCommandType.Visit => Target.Length > 0 ? "VISIT," + Target : "VISIT",
            _ => "",
        };
    }
}

/// <summary>
/// A walk script, in the text format of the old SRAutoWalk tool (from POC-2), plus teleports:
/// <code>
/// // comment
/// MOVE,3563,2030                      world coordinates (X,Y)
/// MOVE,24744,993,1653,0               region,X,Y,Z (required inside dungeons)
/// WAIT,2000                           pause in milliseconds
/// TELEPORT,STORE_CH_GATE,2,Jangan → Donwhang   use that teleporter nearby, go to destination 2
/// RETURN                              use a return scroll
/// RIDE,TRANSPORT                      summon the preferred trade transport and get on (RIDE,ITEM_COS_... a scroll;
///                                     RIDE alone: get on the mount already out)
/// DISMOUNT                            get off the mount
/// BUY,NPC_CH_SPECIAL,TRANSPORT,0,1,40,5,12   from that NPC: tab 0 slot 1, 40 at a time, 5 times (0 = until
///                                     refused), opened with talk option 12; INVENTORY buys into the bag
/// SELL,NPC_WC_SPECIAL,TRANSPORT,0,12  sell all the transport carries (or an item: SELL,NPC,ITEM_...,amount,talk)
/// VISIT,STORAGE                       do the town business with the NPC here (storage, repair, sell, buy)
/// REPEAT                              jump back to the start
/// </code>
/// phBot scripts are read too: <c>walk,X,Y,Z</c> is a point (world X,Y; the height Z is not needed),
/// <c>DoStorage</c>, <c>DoBlacksmith</c>, <c>DoStable</c> ... are VISITs, <c>wait,ms</c> a WAIT, and <c>DoScript</c>
/// (go on with the training script) needs nothing: the town loop walks the training script after the town script.
/// Unknown or malformed lines are ignored and reported in <see cref="Warnings"/>.
/// </summary>
public sealed class WalkScript
{
    private WalkScript(string name, List<WalkCommand> commands, List<string> warnings)
    {
        Name = name;
        Commands = commands;
        Warnings = warnings;
    }

    public string Name { get; }
    public IReadOnlyList<WalkCommand> Commands { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static WalkScript Load(string path) => Parse(File.ReadAllLines(path), Path.GetFileName(path));

    public static WalkScript Parse(string text, string name = "script") =>
        Parse(text.Split('\n').Select(l => l.TrimEnd('\r')), name);

    public static WalkScript Parse(IEnumerable<string> lines, string name = "script")
    {
        ArgumentNullException.ThrowIfNull(lines);
        var commands = new List<WalkCommand>();
        var warnings = new List<string>();
        int lineNumber = 0;
        var ci = CultureInfo.InvariantCulture;

        foreach (string raw in lines)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            string[] args = line.Split(',', StringSplitOptions.TrimEntries);
            string keyword = args[0].ToUpperInvariant();
            // phBot: "DoStorage", "DoBlacksmith" ... (DoScript: the training script follows anyway)
            if (keyword == "DOSCRIPT")
                continue;
            if (keyword.Length > 2 && keyword.StartsWith("DO", StringComparison.Ordinal) && args.Length == 1)
            {
                commands.Add(new WalkCommand(WalkCommandType.Visit, LineNumber: lineNumber, Target: keyword[2..]));
                continue;
            }
            switch (keyword)
            {
                case "WALK" when args.Length == 4 && double.TryParse(args[1], ci, out double wx) && double.TryParse(args[2], ci, out double wy):
                    commands.Add(new WalkCommand(WalkCommandType.Move, new Position(wx, wy), LineNumber: lineNumber));
                    break;
                case "VISIT":
                    commands.Add(new WalkCommand(WalkCommandType.Visit, LineNumber: lineNumber, Target: args.Length > 1 ? args[1].ToUpperInvariant() : ""));
                    break;
                case "MOVE" when TryParseMove(args, out var position):
                    commands.Add(new WalkCommand(WalkCommandType.Move, position, LineNumber: lineNumber));
                    break;
                case "WAIT" when args.Length == 2 && int.TryParse(args[1], ci, out int ms) && ms >= 0:
                    commands.Add(new WalkCommand(WalkCommandType.Wait, Delay: TimeSpan.FromMilliseconds(ms), LineNumber: lineNumber));
                    break;
                case "TELEPORT" when args.Length >= 3 && args[1].Length > 0 && uint.TryParse(args[2], ci, out uint destination):
                    commands.Add(new WalkCommand(WalkCommandType.Teleport, LineNumber: lineNumber, Teleporter: args[1],
                        DestinationId: destination, Label: string.Join(",", args.Skip(3))));
                    break;
                case "RETURN":
                    commands.Add(new WalkCommand(WalkCommandType.Return, LineNumber: lineNumber));
                    break;
                case "BUY" when args.Length >= 6 && args[1].Length > 0 && args[2].Length > 0
                    && byte.TryParse(args[3], ci, out byte tab) && byte.TryParse(args[4], ci, out byte tabSlot)
                    && ushort.TryParse(args[5], ci, out ushort amount) && amount > 0:
                {
                    int times = args.Length > 6 && int.TryParse(args[6], ci, out int t) && t >= 0 ? t : 1;
                    byte talk = args.Length > 7 && byte.TryParse(args[7], ci, out byte k) ? k : ShopTarget.DefaultTalk(args[2]);
                    commands.Add(new WalkCommand(WalkCommandType.Buy, LineNumber: lineNumber, Npc: args[1], Target: args[2].ToUpperInvariant() is
                        ShopTarget.Transport or ShopTarget.Inventory ? args[2].ToUpperInvariant() : args[2],
                        Tab: tab, Slot: tabSlot, Quantity: amount, Times: times, Talk: talk));
                    break;
                }
                case "SELL" when args.Length >= 3 && args[1].Length > 0 && args[2].Length > 0:
                {
                    ushort amount = args.Length > 3 && ushort.TryParse(args[3], ci, out ushort a) ? a : (ushort)0;
                    string target = string.Equals(args[2], ShopTarget.Transport, StringComparison.OrdinalIgnoreCase) ? ShopTarget.Transport : args[2];
                    byte talk = args.Length > 4 && byte.TryParse(args[4], ci, out byte k) ? k : ShopTarget.DefaultTalk(target);
                    commands.Add(new WalkCommand(WalkCommandType.Sell, LineNumber: lineNumber, Npc: args[1], Target: target, Quantity: amount, Talk: talk));
                    break;
                }
                case "RIDE":
                {
                    string target = args.Length > 1 ? args[1] : "";
                    if (string.Equals(target, ShopTarget.Transport, StringComparison.OrdinalIgnoreCase))
                        target = ShopTarget.Transport;
                    commands.Add(new WalkCommand(WalkCommandType.Ride, LineNumber: lineNumber, Target: target));
                    break;
                }
                case "DISMOUNT":
                    commands.Add(new WalkCommand(WalkCommandType.Dismount, LineNumber: lineNumber));
                    break;
                case "REPEAT":
                    commands.Add(new WalkCommand(WalkCommandType.Repeat, LineNumber: lineNumber));
                    break;
                default:
                    warnings.Add($"line {lineNumber}: ignored '{line}'");
                    break;
            }
        }
        return new WalkScript(name, commands, warnings);
    }

    /// <summary>Writes commands as script text, one per line.</summary>
    public static string Format(IEnumerable<WalkCommand> commands) =>
        string.Join(Environment.NewLine, commands.Select(c => c.ToLine()));

    private static bool TryParseMove(string[] args, out Position position)
    {
        position = default;
        var ci = CultureInfo.InvariantCulture;
        if (args.Length == 3 && double.TryParse(args[1], ci, out double x) && double.TryParse(args[2], ci, out double y))
        {
            position = new Position(x, y);
            return true;
        }
        if (args.Length == 5 && ushort.TryParse(args[1], ci, out ushort region) && int.TryParse(args[2], ci, out int lx)
            && int.TryParse(args[3], ci, out int ly) && int.TryParse(args[4], ci, out int lz))
        {
            position = new Position(region, lx, lz, ly);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Index of the MOVE closest to <paramref name="position"/> within <paramref name="maxDistance"/>, or -1. Of equally
    /// close points the first wins (a looped route that ends where it starts begins at the top).
    /// </summary>
    public int FindNearestMove(Position position, double maxDistance) => FindStartMove(position, maxDistance, 0);

    /// <summary>
    /// Index of the earliest MOVE within <paramref name=slack/> units of the nearest one (and within
    /// <paramref name=maxDistance/>), or -1: a town route that ends where it starts begins at its first line, not at
    /// its last (which may be a step nearer).
    /// </summary>
    public int FindStartMove(Position position, double maxDistance, double slack)
    {
        int nearest = FindNearest(position, maxDistance);
        if (nearest < 0 || slack <= 0)
            return nearest;
        double limit = Commands[nearest].Position.DistanceTo(position) + slack;
        for (int i = 0; i < nearest; i++)
            if (Commands[i].Type == WalkCommandType.Move && Commands[i].Position.InDungeon == position.InDungeon
                && Commands[i].Position.DistanceTo(position) <= Math.Min(limit, maxDistance))
                return i;
        return nearest;
    }

    private int FindNearest(Position position, double maxDistance)
    {
        int best = -1;
        double bestDistance = maxDistance + 1e-9;
        for (int i = 0; i < Commands.Count; i++)
        {
            if (Commands[i].Type != WalkCommandType.Move || Commands[i].Position.InDungeon != position.InDungeon)
                continue;
            double distance = Commands[i].Position.DistanceTo(position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }
}
