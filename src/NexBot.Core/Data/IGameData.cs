using NexBot.Game;

namespace NexBot.Data;

/// <summary>
/// Static game data extracted from the client's media files (the old <c>DataManager</c>).
/// </summary>
/// <remarks>
/// One instance is shared by every client in the process. Implementations must be thread-safe.
/// Lookups return <c>null</c> when an ID is unknown.
/// </remarks>
public interface IGameData
{
    ModelInfo? GetModel(uint id);
    ModelInfo? GetModel(string serverName);

    ItemInfo? GetItem(uint id);
    ItemInfo? GetItem(string serverName);

    /// <summary>All items of one kind (type IDs 2 and 3), e.g. (3, 3) pet / mount scrolls, (1, 7) job suits.</summary>
    IReadOnlyList<ItemInfo> GetItemsOfType(byte tid2, byte tid3);

    /// <summary>Items whose name or server name contains <paramref name="text"/> (for pickers), best matches first.</summary>
    IReadOnlyList<ItemInfo> SearchItems(string text, int limit = 20) => [];

    /// <summary>Monsters (MOB_*) whose name or server name contains <paramref name="text"/>, with their level, lowest level first.</summary>
    IReadOnlyList<(ModelInfo Model, byte Level)> SearchMonsters(string text, int limit = 100) => [];

    SkillInfo? GetSkill(uint id);
    SkillInfo? GetSkill(string serverName);

    MagicOptionInfo? GetMagicOption(uint id);
    MasteryInfo? GetMastery(uint id);

    /// <summary>All masteries (both races), by ID.</summary>
    IReadOnlyList<MasteryInfo> GetMasteries() => [];

    /// <summary>
    /// Every level of every skill shown in a mastery's skill window, or null when the database was built before the
    /// skill window existed (rebuild it).
    /// </summary>
    IReadOnlyList<SkillTreeInfo>? GetSkillTree(uint masteryId) => null;

    /// <summary>Looks up a teleport building first, then a teleport link with that ID.</summary>
    TeleportInfo? GetTeleport(uint id);
    IReadOnlyList<TeleportLinkInfo> GetTeleportLinks(uint sourceTeleportId);

    LevelInfo? GetLevel(byte level);

    ShopItemInfo? GetShopItem(string npcServerName, byte tab, byte slot);

    /// <summary>Everything an NPC sells, by tab and slot.</summary>
    IReadOnlyList<ShopSlotInfo> GetShopItems(string npcServerName);
    /// <summary>Every item any NPC sells, with the NPCs that sell it (for the town buy list).</summary>
    IReadOnlyList<(string Item, string Npc)> GetShopCatalog() => [];

    /// <summary>
    /// The good bought by the server's indexes (item mall purchase): shop group ID, tab group index, tab index and
    /// slot; null when unknown (databases built before shop goods).
    /// </summary>
    ShopGoodInfo? GetShopGood(uint groupId, byte groupIndex, byte tabIndex, byte slot) => null;

    /// <summary>Models whose code starts with <paramref name="prefix"/> (e.g. CHAR_CH_MAN_), by ID.</summary>
    IReadOnlyList<ModelInfo> GetModelsByPrefix(string prefix) => [];

    /// <summary>The specialty traders (NPCs whose shop sells trade goods: NPC_CH_SPECIAL ...).</summary>
    IReadOnlyList<ModelInfo> GetSpecialtyTraders();

    string? GetRegionName(ushort regionId);

    /// <summary>Returns the raw UI text for a <c>textuisystem</c> key, or null.</summary>
    string? GetUiText(string serverName);

    /// <summary>Basic attack skill ID for a weapon type, or 0.</summary>
    uint GetCommonAttackSkillId(WeaponType weapon);
}

/// <summary>Convenience helpers over <see cref="IGameData"/>.</summary>
public static class GameDataExtensions
{
    public static ulong GetExpMax(this IGameData data, byte level) => data.GetLevel(level)?.Player ?? 0;

    public static ulong GetPetExpMax(this IGameData data, byte level) => data.GetLevel(level)?.Pet ?? 0;

    public static uint GetJobExpMax(this IGameData data, byte level, JobType job)
    {
        var info = data.GetLevel(level);
        if (info is null)
            return 0;
        return job switch
        {
            JobType.Trader => info.Trader,
            JobType.Thief => info.Thief,
            JobType.Hunter => info.Hunter,
            _ => 0,
        };
    }

    /// <summary>Formats a UI text (e.g. <c>UIIT_MSG_STRGERR_LEVEL</c>) with arguments, or returns "".</summary>
    public static string FormatUiText(this IGameData data, string serverName, params object[] args)
    {
        string? text = data.GetUiText(serverName);
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }
}
