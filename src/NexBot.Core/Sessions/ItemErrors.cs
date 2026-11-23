namespace NexBot.Sessions;

/// <summary>
/// Plain-language text for item use / movement error codes (0xB04C, 0xB034). Only codes seen on a real server are
/// listed; add more as they are reported. Unknown codes return null (callers show the hex code).
/// </summary>
public static class ItemErrors
{
    /// <summary>0x185B: the item was used too soon (a potion's cooldown is not over). Seen on vSRO 1.188 with mall potion bags.</summary>
    public const ushort CooldownError = 0x185B;

    private static readonly Dictionary<ushort, string> s_messages = new()
    {
        [0x1894] = "its effect is already active; wait until it ends",
        [CooldownError] = "its cooldown is not over yet (potions have a delay)",
        [0x180E] = "the item or quantity does not match the server (the inventory changed)",
        [0x1829] = "the server does not accept that quantity for this item",
        [0x1834] = "items cannot be moved right now (for example while a stall is up)",
        [0x1878] = "not while you are attacked (in combat)",
    };

    public static string? Describe(ushort code) => s_messages.GetValueOrDefault(code);

    /// <summary>"Can't use X: reason." with the code in brackets, or the generic text for unknown codes.</summary>
    public static string Format(string action, ushort code)
    {
        string hex = $"0x{code:X4}";
        return Describe(code) is { } reason
            ? $"Can't {action}: {reason}. ({hex})"
            : $"The server refused to {action} (error {hex}).";
    }
}
