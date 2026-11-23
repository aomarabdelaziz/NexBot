using NexBot.Game;

namespace NexBot.Data;

/// <summary>A row of the <c>models</c> table (characters, monsters, NPCs, pets ...).</summary>
/// <param name="Icon">Portrait under Media.pk2 icon\ (e.g. cos\cos_p_wolf_01.ddj); empty when unknown or in an older database.</param>
public sealed record ModelInfo(uint Id, string ServerName, string Name, byte Tid2, byte Tid3, byte Tid4, uint Hp, string Icon = "");

/// <summary>A row of the <c>items</c> table.</summary>
public sealed record ItemInfo(
    uint Id, string ServerName, string Name, byte Tid2, byte Tid3, byte Tid4,
    byte CashItem, byte Bionic, string Icon, ushort Stack, byte Level, string Description = "", string Params = "",
    string Equip = "");

/// <summary>A good of a shop as the server names it when it is bought (item mall): the item, its plus and data.</summary>
/// <param name="Data">The package's data: the amount of a stack (or durability).</param>
public sealed record ShopGoodInfo(string Item, byte Plus, int Data);

/// <summary>A row of the <c>skills</c> table.</summary>
/// <param name="Level">The mastery level the skill needs (column <c>level</c>).</param>
/// <param name="SkillLevel">The skill's own level (1, 2 ...); 0 in databases built before the skill window.</param>
public sealed record SkillInfo(
    uint Id, string ServerName, string Name, uint GroupId, string GroupName,
    uint Cooldown, uint Duration, int CastTime, uint Mana, bool TargetRequired, byte Level,
    string Params, string Icon, WeaponType WeaponFirst, WeaponType WeaponSecond, byte SkillLevel = 0)
{
    /// <summary>Skill param marking buffs that transfer automatically (e.g. party buffs).</summary>
    public const uint AutoTransferEffect = 1701213281;

    /// <summary>True when the buff carries the auto-transfer param (adds a byte to spawn/character packets).</summary>
    public bool HasAutoTransferEffect { get; } = Params.Contains("|1701213281", StringComparison.Ordinal);
}

/// <summary>A row of the <c>magicoptions</c> table.</summary>
public sealed record MagicOptionInfo(uint Id, string ServerName, string Name, uint MaxValue);

/// <summary>A row of the <c>masteries</c> table.</summary>
/// <param name="TabName">Skill window tab ("Weapon", "Force" ...).</param>
/// <param name="Tab">Tab index (0, 1, 2).</param>
/// <param name="Icon">Icon path relative to Media.pk2 <c>icon\</c>.</param>
public sealed record MasteryInfo(uint Id, string Name, string Description, string TabName = "", byte Tab = 0, string Icon = "");

/// <summary>One level of a skill as placed in the skill window (from the <c>skills</c> table).</summary>
/// <param name="Group">Skill line code shared by all levels (e.g. SKILL_CH_SWORD_SMASH_C).</param>
/// <param name="Level">The skill's own level.</param>
/// <param name="MasteryLevel">Mastery level needed for this level.</param>
/// <param name="Sp">Skill points this level costs.</param>
/// <param name="Column">Line of the window (top to bottom).</param>
/// <param name="Row">Position along the line (left to right).</param>
/// <param name="ReqSkill1">A skill (ID of one of its levels) that must be learned first, or 0.</param>
public sealed record SkillTreeInfo(
    uint Id, string ServerName, string Name, string Group, byte Level, uint MasteryId, byte MasteryLevel, uint Sp, string Icon,
    byte Column, byte Row, uint ReqSkill1, byte ReqSkillLevel1, uint ReqSkill2, byte ReqSkillLevel2, uint GroupId = 0);

/// <summary>A teleport building / gate (<c>teleportbuildings</c> or <c>teleportlinks</c>).</summary>
public sealed record TeleportInfo(uint Id, string ServerName, string Name, byte Tid2, byte Tid3, byte Tid4);

/// <summary>A destination reachable from a teleport (<c>teleportlinks</c>).</summary>
public sealed record TeleportLinkInfo(uint SourceId, string SourceName, uint DestinationId, string DestinationName, string ServerName);

/// <summary>A row of the <c>leveldata</c> table.</summary>
/// <param name="MasterySp">Skill points to raise a mastery to this level.</param>
public sealed record LevelInfo(byte Level, ulong Player, ulong Pet, uint Trader, uint Thief, uint Hunter, uint MasterySp = 0);

/// <summary>A row of the <c>shops</c> table.</summary>
public sealed record ShopItemInfo(string ItemServerName, byte Plus, uint Durability, string MagicParams);

/// <summary>Where an item sits in an NPC's shop (tab and slot, as sent when buying).</summary>
public sealed record ShopSlotInfo(byte Tab, byte Slot, string ItemServerName);
