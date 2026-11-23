using System.Diagnostics;
using NexBot.Data;
using NexBot.Game.Items;

namespace NexBot.Game;

/// <summary>Packed Silkroad timestamp (year since 2000, month, day, hour, minute, second in a uint).</summary>
public readonly record struct SroTimestamp(uint Raw)
{
    /// <summary>The decoded date, or <see cref="DateTime.MinValue"/> when the value is not a valid date.</summary>
    public DateTime DateTime
    {
        get
        {
            int year = (int)(Raw & 63) + 2000;
            int month = (int)(Raw >> 6) & 15;
            int day = (int)(Raw >> 10) & 31;
            int hour = (int)(Raw >> 15) & 31;
            int minute = (int)(Raw >> 20) & 63;
            int second = (int)(Raw >> 26) & 63;
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
                return DateTime.MinValue;
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
    }
}

/// <summary>A learned skill.</summary>
public class Skill
{
    private long _cooldownStarted;

    public Skill(SkillInfo info) => Info = info ?? throw new ArgumentNullException(nameof(info));

    public SkillInfo Info { get; }
    public uint Id => Info.Id;
    public string ServerName => Info.ServerName;
    public string Name => Info.Name;
    public uint GroupId => Info.GroupId;
    public byte Level => Info.Level;
    public bool Enabled { get; set; }

    /// <summary>True when the cooldown since the last cast has elapsed.</summary>
    public bool IsCastingEnabled =>
        _cooldownStarted == 0 || Stopwatch.GetElapsedTime(_cooldownStarted).TotalMilliseconds >= Info.Cooldown;

    public void StartCooldown() => _cooldownStarted = Stopwatch.GetTimestamp();

    public bool IsAttackingSkill => Info.Mana > 0 && Info.Duration == 0;
}

/// <summary>An active buff on an entity.</summary>
public sealed class Buff(SkillInfo info) : Skill(info)
{
    /// <summary>When the buff began (Stopwatch timestamp); 0 when unknown (buffs already on at login).</summary>
    public long Started { get; set; }

    /// <summary>Key in <see cref="Entities.Model.Buffs"/>: the skill group, so a new level replaces the old one.</summary>
    public uint Key => GroupId != 0 ? GroupId : Id;

    /// <summary>Time left, or null when the start or the duration is unknown.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (Started == 0 || Info.Duration == 0)
                return null;
            var left = TimeSpan.FromMilliseconds(Info.Duration) - Stopwatch.GetElapsedTime(Started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public uint UniqueId { get; set; }
    public uint TargetUniqueId { get; set; }
    public uint CasterUniqueId { get; set; }
    public bool HasAutoTransferEffect => Info.HasAutoTransferEffect;
}

public sealed class Mastery(uint id, MasteryInfo? info)
{
    public uint Id { get; } = id;
    public string Name { get; } = info?.Name ?? $"Mastery {id}";
    public string Description { get; } = info?.Description ?? string.Empty;
    public byte Level { get; set; }
}

public sealed class Quest(uint id)
{
    public uint Id { get; } = id;
    public byte Achievements { get; set; }
    public bool IsAutoShareRequired { get; set; }
    public byte QuestType { get; set; }
    public uint TimeRemain { get; set; }
    public byte State { get; set; }
    public uint[] NpcIds { get; set; } = [];
    public SlotList<QuestObjective> Objectives { get; set; } = new();
}

public sealed class QuestObjective(uint id)
{
    public uint Id { get; } = id;
    public bool IsEnabled { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint[] TaskIds { get; set; } = [];
}

public sealed class CollectionBook(uint id)
{
    public uint Id { get; } = id;
    public SroTimestamp Started { get; set; }
    public uint Pages { get; set; }
}

public sealed class Stall
{
    public string Title { get; set; } = string.Empty;
    public uint DecorationId { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>A character listed on the selection screen.</summary>
public sealed class CharacterSlot
{
    public uint ModelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Scale { get; set; }
    public byte Level { get; set; }
    public ulong Exp { get; set; }
    public ushort Str { get; set; }
    public ushort Int { get; set; }
    public ushort StatPoints { get; set; }
    public uint Hp { get; set; }
    public uint Mp { get; set; }
    public DateTime? DeletingAt { get; set; }
    public bool IsDeleting => DeletingAt.HasValue;
    public GuildMemberType GuildMemberType { get; set; }
    public string? GuildName { get; set; }
    public AcademyMemberType AcademyMemberType { get; set; }
    public SlotList<Item> Inventory { get; set; } = new();
    public SlotList<Equipable> AvatarInventory { get; set; } = new();
}
