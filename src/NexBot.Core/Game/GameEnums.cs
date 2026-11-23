namespace NexBot.Game;

public enum CharacterRace : byte
{
    European = 1,
    Chinese = 2,
}

public enum CharacterSelectionAction : byte
{
    Create = 1,
    List = 2,
    Delete = 3,
    CheckName = 4,
    Restore = 5,
}

/// <summary>Basic actions a character can do (0x7074).</summary>
public enum CharacterAction : byte
{
    CommonAttack = 1,
    ItemPickUp = 2,
    /// <summary>Follow a player (the game's own trace).</summary>
    Trace = 3,
    SkillCast = 4,
    SkillRemove = 5,
}

public enum WeaponType : byte
{
    Sword = 2,
    Blade = 3,
    Spear = 4,
    Glaive = 5,
    Bow = 6,
    OneHandSword = 7,
    TwoHandSword = 8,
    DualAxes = 9,
    Warlock = 10,
    TwoHandStaff = 11,
    Crossbow = 12,
    Daggers = 13,
    Harp = 14,
    Cleric = 15,
    None = byte.MaxValue,
}

public enum ChatType : byte
{
    All = 1,
    Private = 2,
    GameMaster = 3,
    Party = 4,
    Guild = 5,
    Global = 6,
    Notice = 7,
    Stall = 9,
    Union = 11,
    Npc = 13,
    Academy = 16,
}

public enum EntityStatusUpdateType : byte
{
    Hp = 1,
    Mp = 2,
    HpMp = 3,
    BadStatus = 4,
    EntityHpMp = 5,
}

/// <summary>Requests between players (0x3080).</summary>
public enum PlayerPetition : byte
{
    ExchangeRequest = 1,
    PartyCreation = 2,
    PartyInvitation = 3,
    Resurrection = 4,
    GuildInvitation = 5,
    UnionInvitation = 6,
    AcademyInvitation = 9,
}

public enum EquipableType : byte
{
    Garment = 1,
    Protector = 2,
    Armor = 3,
    Shield = 4,
    AccessoryChinese = 5,
    Weapon = 6,
    Job = 7,
    Robe = 9,
    LightArmor = 10,
    HeavyArmor = 11,
    AccessoryEuropean = 12,
    Avatar = 13,
    DevilSpirit = 14,
}

public enum ArmorPart : byte
{
    Head = 1,
    Shoulders = 2,
    Chest = 3,
    Pants = 4,
    Gloves = 5,
    Boots = 6,
}

public enum AvatarPart : byte
{
    Hat = 1,
    Dress = 2,
    Accessory = 3,
    Flag = 4,
}

public enum AccessoryPart : byte
{
    Earring = 1,
    Necklace = 2,
    Ring = 3,
}

/// <summary>Item movement types (0x7034 / 0xB034).</summary>
public enum ItemMovementType : byte
{
    InventoryToInventory = 0,
    StorageToStorage = 1,
    InventoryToStorage = 2,
    StorageToInventory = 3,
    InventoryToExchange = 4,
    ExchangeToInventory = 5,
    GroundToInventory = 6,
    InventoryToGround = 7,
    ShopToInventory = 8,
    InventoryToShop = 9,
    InventoryGoldToGround = 10,
    StorageGoldToInventory = 11,
    InventoryGoldToStorage = 12,
    InventoryGoldToExchange = 13,
    QuestToInventory = 14,
    InventoryToQuest = 15,
    TransportToTransport = 16,
    GroundToPet = 17,
    ShopToTransport = 19,
    /// <summary>Bought in the item mall (shop group, tab group, tab, slot, then the inventory slots and amount).</summary>
    ItemMallToInventory = 24,
    TransportToShop = 20,
    PetToPet = 25,
    PetToInventory = 26,
    InventoryToPet = 27,
    GroundToPetToInventory = 28,
    GuildToGuild = 29,
    InventoryToGuild = 30,
    GuildToInventory = 31,
    InventoryGoldToGuild = 32,
    GuildGoldToInventory = 33,
    ShopBuyBack = 34,
    AvatarToInventory = 35,
    InventoryToAvatar = 36,
}

public enum StallUpdateType : byte
{
    ItemUpdate = 1,
    ItemAdded = 2,
    ItemRemoved = 3,
    FleaMarketMode = 4,
    State = 5,
    Note = 6,
    Title = 7,
}

public enum SkillCastType : byte
{
    Buff = 0,
    Attack = 2,
}

[Flags]
public enum DamageEffect : byte
{
    None = 0,
    KnockBack = 1,
    Block = 2,
    Position = 4,
    Cancel = 8,
    Dead = 128,
}

[Flags]
public enum DamageType : byte
{
    None = 0,
    Normal = 1,
    Critical = 2,
    Status = 4,
}

/// <summary>Game master console commands (0x7010).</summary>
public enum GameMasterCommand : ushort
{
    FindUser = 1,
    GoTown = 2,
    ToTown = 3,
    WorldStatus = 4,
    CreateMob = 6,
    MakeItem = 7,
    MoveToUser = 8,
    Warp = 10,
    Zoe = 12,
    Ban = 13,
    Invisible = 14,
    Invincible = 15,
    RecallUser = 17,
    RecallGuild = 18,
    LieName = 19,
    KillMob = 20,
    ResetQuest = 28,
    MoveToNpc = 31,
    MakeRentItem = 38,
    SpawnUniqueLocation = 42,
}

public enum MovementSpeed : byte
{
    Walking = 0,
    Running = 1,
}

public enum MovementAction : byte
{
    Spinning = 0,
    KeyWalking = 1,
}

public enum LifeState : byte
{
    Unknown = 0,
    Alive = 1,
    Dead = 2,
}

public enum MotionState : byte
{
    StandUp = 0,
    Walking = 2,
    Running = 3,
    Sitting = 4,
}

public enum GameState : byte
{
    None = 0,
    Berserk = 1,
    Untouchable = 2,
    GameMasterInvincible = 3,
    GameMasterUntouchable = 4,
    GameMasterInvisible = 5,
    Stealth = 6,
    Invisible = 7,
}

[Flags]
public enum BadStatus : uint
{
    None = 0,
    Freezing = 0x1,
    Frostbite = 0x2,
    ElectricShock = 0x4,
    Burn = 0x8,
    Poisoning = 0x10,
    Zombie = 0x20,
    Sleep = 0x40,
    Bind = 0x80,
    Dull = 0x100,
    Fear = 0x200,
    ShortSight = 0x400,
    Bleed = 0x800,
    Petrify = 0x1000,
    Darkness = 0x2000,
    Stun = 0x4000,
    Disease = 0x8000,
    Confusion = 0x10000,
    Decay = 0x20000,
    Weaken = 0x40000,
    Impotent = 0x80000,
    Division = 0x100000,
    Panic = 0x200000,
    Combustion = 0x400000,
    Hidden = 0x1000000,
}

public enum PvpCape : byte
{
    None = 0,
    Red = 1,
    Gray = 2,
    Blue = 3,
    White = 4,
    Yellow = 5,
}

public enum ExpIcon : byte
{
    Beginner = 0,
    Helpful = 1,
    BeginnerAndHelpful = 2,
}

public enum JobType : byte
{
    None = 0,
    Trader = 1,
    Thief = 2,
    Hunter = 3,
}

public enum PvpState : byte
{
    Neutral = 0,
    Assaulter = 1,
    PlayerKiller = 2,
}

public enum ScrollingType : byte
{
    None = 0,
    ReturnScroll = 1,
    BanditReturnScroll = 2,
}

public enum InteractionType : byte
{
    None = 0,
    OnExchange = 2,
    OnStall = 4,
    OnShop = 6,
}

public enum CaptureTheFlag : byte
{
    Red = 1,
    Blue = 2,
    None = 0xFF,
}

/// <summary>
/// Monster rarity from the spawn (same values as the Rarity column of CharacterData). In vSRO 1.188 the normal uniques
/// (Tiger Girl, Uruchi, Isyutaru, Tai-Sui ...) are 3; 8 is used by boss uniques (Tomb generals, Togui / Flame captains).
/// </summary>
public enum MobType : byte
{
    General = 0,
    Champion = 1,
    Unique = 3,
    Giant = 4,
    Titan = 5,
    Strong = 6,
    Elite = 7,
    Unique2 = 8,
    PartyGeneral = 0x10,
    PartyChampion = 0x11,
    PartyUnique = 0x13,
    PartyGiant = 0x14,
    PartyTitan = 0x15,
    PartyStrong = 0x16,
    PartyElite = 0x17,
    PartyUnique2 = 0x18,
    Event = 0xFF,
}

public static class MobTypeExtensions
{
    /// <summary>Any kind of unique (normal, boss, party).</summary>
    public static bool IsUnique(this MobType type) =>
        type is MobType.Unique or MobType.Unique2 or MobType.PartyUnique or MobType.PartyUnique2;
}

/// <summary>Pet action sub-codes (0x70C5).</summary>
public enum CosAction : byte
{
    Movement = 1,
    ItemPickUp = 8,
}

public enum PortalType : byte
{
    None = 0,
    Regular = 1,
    Dimensional = 6,
}

[Flags]
public enum AttackPetSettings : uint
{
    Disabled = 0,
    Offensive = 1,
}

[Flags]
public enum PickPetSettings : uint
{
    Disabled = 0,
    Gold = 1,
    Equipment = 2,
    OtherItems = 4,
    GrabAllItems = 64,
    Enabled = 128,
}

public enum GuildMemberType : byte
{
    None = 0,
    Member = 1,
    Master = 2,
}

public enum AcademyMemberType : byte
{
    None = 0,
}

[Flags]
public enum GuildPermissions : uint
{
    None = 0,
    Join = 1,
    Kick = 2,
    UnionChat = 4,
    Storage = 8,
    Notice = 16,
    All = Join | Kick | UnionChat | Storage | Notice,
    Master = uint.MaxValue,
}

public enum PartyPurpose : byte
{
    Hunting = 0,
    Quest = 1,
    Trader = 2,
    Thief = 3,
}

[Flags]
public enum PartySetup : byte
{
    None = 0,
    ExpShared = 1,
    ItemShared = 2,
    AnyoneCanInvite = 4,
    /// <summary>Exp shared, item shared, anyone can invite.</summary>
    All = ExpShared | ItemShared | AnyoneCanInvite,
}
