using System.Globalization;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;

namespace NexBot.Map;

/// <summary>An item in a slot, ready to draw (ported from POC-2's ItemView).</summary>
/// <param name="Slot">Inventory slot (0-12 worn, 13+ bag) or avatar slot.</param>
/// <param name="SlotName">Worn slot name ("Weapon", "Ring" ...), null in the bag.</param>
/// <param name="Icon">Icon path under Media.pk2 <c>icon\</c>, or null.</param>
/// <param name="Rarity">"Seal of Star" ... for rare equipment, else null.</param>
public sealed record ItemView(
    int Slot, string? SlotName, string Name, string FullName, string ServerName, string? Icon, int Quantity, int QuantityMax,
    int Level, int Plus, uint? Durability, string? Rarity, IReadOnlyList<string> MagicOptions, string Kind, bool Usable,
    string Description = "", ulong Variance = 0, bool InUse = false)
{
    /// <summary>Changes when anything shown in the slot changes.</summary>
    public string Signature => $"{ServerName}|{Quantity}|{Plus}|{Durability}|{Rarity}|{Variance}|{InUse}|{string.Join(',', MagicOptions)}";
}

/// <summary>A learned mastery.</summary>
public sealed record MasteryView(uint Id, string Name, byte Level);

/// <summary>A learned skill (highest level of each skill group).</summary>
public sealed record SkillView(uint Id, string Name, string Group, byte Level, string? Icon);

/// <summary>A buff on the character (skill buffs and scroll / item effects).</summary>
/// <param name="Icon">Icon path under Media.pk2 <c>icon\</c>, or null.</param>
/// <param name="Remaining">Time left, or null when unknown (the buff was already on at login).</param>
/// <param name="Level">The skill's level (the "Lv 1" after the name).</param>
/// <param name="Effects">What the buff does ("Physical damage 20% Increase" ...), from the skill data.</param>
public sealed record BuffView(uint Id, string Name, string? Icon, TimeSpan Duration, TimeSpan? Remaining,
    byte Level = 0, IReadOnlyList<string>? Effects = null);

/// <summary>A summoned pet / mount.</summary>
/// <param name="Kind">Ride, Transport, Attack, Grab.</param>
/// <param name="Model">The pet type's name (Red Horse, Wolf ...); <paramref name="Name"/> is the name the player gave it.</param>
/// <param name="Icon">Portrait from Media.pk2 (icon path without <c>icon\</c>), or null.</param>
public sealed record PetView(uint Id, string Kind, string Name, uint Hp, uint HpMax, int? Level, int? HgpPercent, bool Riding,
    string Model = "", string? Icon = null);

/// <summary>Everything the Character and Inventory pages show, taken at one moment.</summary>
public sealed record CharacterSheet(
    string Name, string Race, byte Level, double ExpPercent, ulong Gold, uint Sp, double SpExpPercent,
    uint Hp, uint HpMax, uint Mp, uint MpMax,
    ushort Strength, ushort Intellect, ushort StatPoints,
    uint PhysicalAttackMin, uint PhysicalAttackMax, uint MagicalAttackMin, uint MagicalAttackMax,
    ushort PhysicalDefense, ushort MagicalDefense, ushort HitRate, ushort ParryRate,
    byte BerserkPoints, string JobType, byte JobLevel, string JobName, string PvpCape,
    byte PkDaily, ushort PkTotal, uint PkPenalty, string Guild, bool IsGameMaster, uint UniqueId,
    string Location, bool Riding, bool Dead,
    int InventoryCapacity, IReadOnlyList<ItemView> Equipment, IReadOnlyList<ItemView> Items, IReadOnlyList<ItemView> Avatar,
    IReadOnlyList<MasteryView> Masteries, IReadOnlyList<SkillView> Skills, IReadOnlyList<PetView> Pets,
    IReadOnlyList<BuffView>? Buffs = null, string? Portrait = null)
{
    /// <summary>
    /// The character's face portrait in Media.pk2 (interface\character\char_ch_man1.ddj ...): the game numbers the
    /// portraits of a race and gender in the order of their models (CHAR_CH_MAN_ADVENTURER is the first).
    /// </summary>
    public static string? PortraitOf(NexBot.Data.IGameData data, string modelCode, uint modelId)
    {
        ArgumentNullException.ThrowIfNull(data);
        var parts = modelCode.Split('_');
        if (parts.Length < 4 || parts[0] != "CHAR")
            return null;
        string prefix = string.Join('_', parts[..3]) + "_";
        var models = data.GetModelsByPrefix(prefix);
        int index = -1;
        for (int i = 0; i < models.Count; i++)
            if (models[i].Id == modelId)
                index = i;
        return index < 0 ? null
            : string.Create(CultureInfo.InvariantCulture, $"interface\\character\\{prefix.TrimEnd('_').ToLowerInvariant()}{index + 1}.ddj");
    }

    /// <summary>Names of the worn slots 0-12.</summary>
    public static readonly IReadOnlyList<string> EquipmentSlots =
        ["Head", "Chest", "Shoulder", "Gloves", "Pants", "Boots", "Weapon", "Shield", "Job suit", "Earring", "Necklace", "Ring", "Ring"];

    /// <summary>Names of the avatar slots 0-4.</summary>
    public static readonly IReadOnlyList<string> AvatarSlots = ["Hat", "Dress", "Accessory", "Flag", "Devil Spirit"];

    /// <summary>First bag slot of the inventory (0-12 are worn).</summary>
    public const int FirstBagSlot = Character.FirstInventorySlot;

    /// <summary>
    /// Builds the sheet (ported from POC-2's DashboardService.Detail). Called from the UI thread while the network
    /// thread updates the character, so a collection can change while it is read: that attempt is retried.
    /// </summary>
    public static CharacterSheet? Take(SroClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Build(client);
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                // "Collection was modified": read again
            }
            catch (ArgumentException) when (attempt < 3)
            {
            }
        }
    }

    private static CharacterSheet? Build(SroClient client)
    {
        if (client.World.Character is not { } c)
            return null;

        var position = c.EstimatePosition();
        var inventory = c.Inventory;
        var equipment = new List<ItemView>();
        var items = new List<ItemView>();
        var summoned = client.World.MyPets.Values.ToArray();
        int capacity = inventory.Capacity;
        for (int slot = 0; slot < capacity; slot++)
        {
            if (inventory[slot] is not { } item)
                continue;
            if (slot < FirstBagSlot)
                equipment.Add(View(item, slot, EquipmentSlots[slot]));
            else
                items.Add(View(item, slot, null) with { InUse = IsSummoned(item, summoned) });
        }
        var avatar = new List<ItemView>();
        for (int slot = 0; slot < c.AvatarInventory.Capacity; slot++)
            if (c.AvatarInventory[slot] is { } item)
                avatar.Add(View(item, slot, slot < AvatarSlots.Count ? AvatarSlots[slot] : null));

        var masteries = c.Masteries.Values.ToArray()
            .Where(m => m.Level > 0)
            .OrderByDescending(m => m.Level).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new MasteryView(m.Id, m.Name, m.Level))
            .ToArray();

        // One entry per skill group (its highest learned level), like the skill window.
        var skills = c.Skills.Values.ToArray()
            .GroupBy(s => s.Info.GroupId == 0 ? s.Id : s.Info.GroupId)
            .Select(g => g.MaxBy(s => SkillLevelOf(s.Info))!)
            .OrderBy(s => s.Info.GroupName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SkillView(s.Id, Clean(s.Info.Name, s.ServerName), s.Info.GroupName, SkillLevelOf(s.Info), NullIfEmpty(s.Info.Icon)))
            .ToArray();

        var pets = client.World.MyPets.Values.ToArray()
            .Where(p => !p.IsGuildGuard)
            .OrderBy(p => p.TypeId4)
            .Select(p => new PetView(p.UniqueId,
                p.IsHorse ? "Ride" : p.IsTransport ? "Transport" : p.IsAttackPet ? "Attack" : p.IsPickPet ? "Grab" : "Pet",
                p.Name, p.Hp, p.HpMax, (p as AttackPet)?.Level, p is AttackPet a ? (int)Math.Round(a.Hgp / 100.0) : null,
                c.RidingUniqueId == p.UniqueId, p.Info.Name, NullIfEmpty(p.Info.Icon)))
            .ToArray();

        var buffs = c.Buffs.Values.ToArray()
            .Select(b => new BuffView(b.Id, Clean(b.Info.Name, b.ServerName), NullIfEmpty(b.Info.Icon),
                TimeSpan.FromMilliseconds(b.Info.Duration), b.Remaining, SkillLevelOf(b.Info), BuffEffects.Describe(b.Info)))
            .ToArray();

        string region = client.Data.GetRegionName(position.Region) ?? position.Region.ToString(CultureInfo.InvariantCulture);
        return new CharacterSheet(
            c.Name,
            c.ServerName.Contains("_EU_", StringComparison.OrdinalIgnoreCase) ? "European" : "Chinese",
            c.Level, Math.Round(c.ExpPercent, 2), c.Gold, c.Sp, SpPercentOf(c),
            c.Hp, c.HpMax, c.Mp, c.MpMax,
            c.Strength, c.Intellect, c.StatPoints,
            c.PhysicalAttackMin, c.PhysicalAttackMax, c.MagicalAttackMin, c.MagicalAttackMax,
            c.PhysicalDefense, c.MagicalDefense, c.HitRate, c.ParryRate,
            c.BerserkPoints, c.JobType.ToString(), c.JobLevel, c.JobName, c.PvpCape.ToString(),
            c.PkDaily, c.PkTotal, c.PkPenalty, c.GuildName, c.IsGameMaster, c.UniqueId,
            string.Create(CultureInfo.InvariantCulture, $"{region}  (X {position.PosX:0}, Y {position.PosY:0})"),
            c.IsRiding, c.Hp == 0 && c.HpMax > 0,
            Math.Max(0, capacity - FirstBagSlot), equipment, items, avatar, masteries, skills, pets, buffs, PortraitOf(client.Data, c.ServerName, c.RefId));
    }

    // SP experience fills 400 per skill point in vSRO.
    private static double SpPercentOf(Character c) => Math.Round(c.SpExp % 400 / 4.0, 1);

    /// <summary>
    /// A pet scroll whose pet is out now (the game makes it glow): the pet's model is the scroll's model, or, for a
    /// scroll that had never been summoned when the bag was read, the pet's code is part of the scroll's code.
    /// </summary>
    internal static bool IsSummoned(Item item, IReadOnlyCollection<Cos> summoned) =>
        item is CosItem { IsPet: true } scroll && summoned.Any(p =>
            scroll.ModelId != 0 ? p.RefId == scroll.ModelId
                : p.ServerName.Length > 0 && scroll.ServerName.Contains(p.ServerName, StringComparison.Ordinal));

    /// <summary>
    /// The worn slot (0-12, as <see cref="EquipmentSlots"/>) an item goes in, from its type: another player's gear
    /// comes without slot numbers. The second ring goes in slot 12 when 11 is <paramref name="taken"/>. -1: none.
    /// </summary>
    public static int WornSlotOf(Item item, ICollection<int> taken)
    {
        if (item is not Equipable e)
            return -1;
        return e.EquipableType switch
        {
            EquipableType.Weapon => 6,
            EquipableType.Shield => 7,
            EquipableType.Job => 8,
            EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => item.TypeId4 switch
            {
                1 => 9,
                2 => 10,
                _ => taken.Contains(11) ? 12 : 11,
            },
            EquipableType.Avatar or EquipableType.DevilSpirit => -1,
            _ => (ArmorPart)item.TypeId4 switch
            {
                ArmorPart.Head => 0,
                ArmorPart.Chest => 1,
                ArmorPart.Shoulders => 2,
                ArmorPart.Gloves => 3,
                ArmorPart.Pants => 4,
                ArmorPart.Boots => 5,
                _ => -1,
            },
        };
    }

    /// <summary>The avatar slot (0-4, as <see cref="AvatarSlots"/>) an avatar item goes in, from its type. -1: none.</summary>
    public static int AvatarSlotOf(Item item) =>
        item is Equipable { EquipableType: EquipableType.DevilSpirit } ? 4
            : item.TypeId4 is >= 1 and <= 4 ? item.TypeId4 - 1 : -1;

    /// <summary>An item ready to draw in a slot (bag slot; for lists of items outside the Inventory page).</summary>
    public static ItemView ViewOf(Item item, int slot) => View(item, slot, null);

    private static ItemView View(Item item, int slot, string? slotName)
    {
        var equipable = item as Equipable;
        return new ItemView(
            slot,
            slotName,
            Clean(item.Name, item.ServerName),
            Clean(item.FullName, item.ServerName),
            item.ServerName,
            NullIfEmpty(item.Info.Icon),
            item.Quantity,
            item.QuantityMax,
            item.LevelRequired,
            equipable?.Plus ?? 0,
            equipable?.Durability,
            equipable is { Rarity: not ItemRarity.None } ? equipable.Rarity.ToString() : null,
            equipable?.MagicOptions.Select(m => m.ToString()).ToArray() ?? [],
            item switch
            {
                Equipable e => KindName(e.EquipableType),
                CosItem => "Pet / mount",
                _ => "Item",
            },
            SroClient.IsUsable(item),
            item.Info.Description,
            equipable?.Variance ?? 0);
    }

    private static string KindName(EquipableType type) => type switch
    {
        EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => "Accessory",
        EquipableType.LightArmor => "Light armor",
        EquipableType.HeavyArmor => "Heavy armor",
        EquipableType.Job => "Job suit",
        _ => type.ToString(),
    };

    private static string Clean(string name, string serverName) => string.IsNullOrWhiteSpace(name) ? serverName : name;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The skill's own level: the database column, or the server name's suffix (…_SMASH_C_09 → 9) for an older database.</summary>
    private static byte SkillLevelOf(Data.SkillInfo info)
    {
        if (info.SkillLevel > 0)
            return info.SkillLevel;
        int cut = info.ServerName.LastIndexOf('_');
        return cut > 0 && byte.TryParse(info.ServerName.AsSpan(cut + 1), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out byte level) ? level : (byte)1;
    }
}
