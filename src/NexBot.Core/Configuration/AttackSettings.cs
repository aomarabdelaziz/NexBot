namespace NexBot.Configuration;

/// <summary>
/// phBot's Attack tab: the attack skills (and imbue) for each monster type, the attack options, and the buffs.
/// Skills are kept by their skill line (server name without the level, e.g. SKILL_CH_SWORD_SMASH_A), so a skill
/// still fits after it is levelled up.
/// </summary>
public sealed class AttackSettings
{
    /// <summary>The attack skills of each monster type (see <c>AttackPlan.Types</c>), in casting order.</summary>
    public Dictionary<string, List<string>> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The imbue of each monster type (a skill line), or none.</summary>
    public Dictionary<string, string> Imbues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cast the skills in the list's order (else the first one that is ready).</summary>
    public bool CastInOrder { get; set; } = true;

    /// <summary>Also attack monsters other players are fighting.</summary>
    public bool KillSteal { get; set; }

    /// <summary>Don't attack monsters (only buff), e.g. for a support character.</summary>
    public bool DontAttack { get; set; }

    /// <summary>Attack the monsters that attack party members first.</summary>
    public bool ProtectParty { get; set; }

    /// <summary>Only protect this party member (empty: every member).</summary>
    public string ProtectPartyName { get; set; } = "";

    /// <summary>Of the monsters attacking you, attack the weakest type first.</summary>
    public bool AttackLowerFirst { get; set; }

    /// <summary>When a type has no skills, use the next lower type's (Giant → Champion → General), not General's.</summary>
    public bool UseLowerSkills { get; set; } = true;

    /// <summary>Wait longer between skills (for laggy servers).</summary>
    public bool SlowerAttack { get; set; }

    /// <summary>How far from the character monsters are attacked (world units).</summary>
    public int Range { get; set; } = 60;

    /// <summary>The buffs to keep on (skill lines).</summary>
    public List<string> Buffs { get; set; } = [];

    /// <summary>Recast buffs while fighting a monster (else only between monsters).</summary>
    public bool BuffWhileAttacking { get; set; } = true;

    /// <summary>Emergency buffs: cast when HP or MP is low, or many monsters attack.</summary>
    public List<string> EmergencyBuffs { get; set; } = [];

    /// <summary>HP % under which the emergency buffs are cast (0 = off).</summary>
    public int EmergencyHp { get; set; } = 30;

    /// <summary>MP % under which the emergency buffs are cast (0 = off).</summary>
    public int EmergencyMp { get; set; }

    /// <summary>More monsters than this attacking cast the emergency buffs (0 = off).</summary>
    public int EmergencyMonsters { get; set; }

    // ---------------------------------------------------------------- Attack (more)

    /// <summary>Switch monster after casting this many damage-over-time skills (Warlock); 0 = off.</summary>
    public int SwitchAfterDots { get; set; }

    /// <summary>Send more packets (laggy servers): skills are sent again sooner.</summary>
    public bool Lagtastic { get; set; }

    /// <summary>Keep nearby uniques selected when not botting.</summary>
    public bool AutoSelectUniques { get; set; }

    /// <summary>Keep nearby titans selected when not botting.</summary>
    public bool AutoSelectTitans { get; set; }

    /// <summary>Use Ghost Walk / Teleport to reach far monsters faster.</summary>
    public bool UseTeleportSkills { get; set; }

    // ---------------------------------------------------------------- Buffs (more)

    /// <summary>Cast the Devil's Spirit skills (avatar) when they are off.</summary>
    public bool UseDevilSpirit { get; set; }

    /// <summary>
    /// The Devil's Spirit skill (server name) seen when the game client cast it. Only this one is cast: a guessed skill
    /// the character does not have gets it disconnected.
    /// </summary>
    public string DevilSkill { get; set; } = "";

    /// <summary>Mirror Reflect (D12 weapons; not in vSRO 1.188).</summary>
    public bool CastMirrorReflect { get; set; }

    /// <summary>Cast a Force Cure skill on a bad status (Chinese) instead of pills.</summary>
    public bool ForceCure { get; set; }

    /// <summary>When one buff ends, recast all the buffs of the list.</summary>
    public bool RecastAllWhenOneEnds { get; set; }

    /// <summary>Switch to the weapon a buff needs, cast it, and switch back.</summary>
    public bool SwitchWeaponsForBuffs { get; set; } = true;

    // ---------------------------------------------------------------- Party buffs

    /// <summary>Party buffs: a buff (skill line) and the players it is cast on.</summary>
    public List<PartyBuff> PartyBuffs { get; set; } = [];

    /// <summary>How far the character walks to buff a player.</summary>
    public int PartyBuffRadius { get; set; } = 40;

    public bool WalkBackAfterBuffing { get; set; } = true;

    /// <summary>Buff every nearby player with every party buff (phBot: probably useless).</summary>
    public bool BuffAllNearbyPlayers { get; set; }

    /// <summary>Party buffing only while one of <see cref="PartyBuffTriggerNames"/> is near (empty: always).</summary>
    public bool PartyBuffOnlyIfNear { get; set; }

    public List<string> PartyBuffTriggerNames { get; set; } = [];

    // ---------------------------------------------------------------- Resurrect

    /// <summary>The players to resurrect.</summary>
    public List<string> ResurrectNames { get; set; } = [];

    public bool ResurrectAllParty { get; set; }

    /// <summary>How far the character walks to resurrect.</summary>
    public int ResurrectRadius { get; set; } = 50;

    /// <summary>Seconds between two resurrect casts on the same player.</summary>
    public int ResurrectDelay { get; set; } = 5;

    /// <summary>Give up after this many casts on a player.</summary>
    public int ResurrectTryLimit { get; set; } = 5;

    public bool AutoAcceptResurrect { get; set; } = true;
    public bool OnlyAcceptPartyResurrect { get; set; }

    // ---------------------------------------------------------------- Healing

    /// <summary>Heal a party member when their HP % is under this (0 = off).</summary>
    public int HealMemberBelow { get; set; }

    /// <summary>Group heal when the party's average HP % is under this (0 = off).</summary>
    public int GroupHealBelow { get; set; }

    /// <summary>Heal yourself when HP % is under this (0 = off).</summary>
    public int HealSelfBelow { get; set; }

    /// <summary>The heal skills (lines), in order.</summary>
    public List<string> HealSkills { get; set; } = [];

    // ---------------------------------------------------------------- Lure

    public bool LureEnabled { get; set; }

    /// <summary>Walk this many units out (a random way), then back to the center; 0 = use the lure script.</summary>
    public int LureWalkDistance { get; set; } = 30;

    /// <summary>Cast this skill (line) at the edge (Howling Shout ...); empty = none.</summary>
    public string LureSkill { get; set; } = "";

    public bool LureBuffOnlyAfterScript { get; set; }
    public int LureStopIfDead { get; set; }
    public int LureStopIfPartyGiants { get; set; }
    public int LureStopIfPartyChampions { get; set; }
    public int LureStopIfMonsters { get; set; }
    public int LureEdgeDelay { get; set; } = 1000;
    public int LureCenterDelay { get; set; } = 5000;
    public int LureHalfDelay { get; set; } = 500;

    /// <summary>A walk script to lure along (instead of a random walk); empty = none.</summary>
    public string LureScript { get; set; } = "";

    public bool LureStopIfPartyAway { get; set; }

    public List<string> SkillsOf(string type) =>
        Skills.TryGetValue(type, out var list) ? list : Skills[type] = [];
}

/// <summary>A party buff: the buff (skill line) and the players to cast it on.</summary>
public sealed class PartyBuff
{
    public string Skill { get; set; } = "";
    public List<string> Players { get; set; } = [];
}
