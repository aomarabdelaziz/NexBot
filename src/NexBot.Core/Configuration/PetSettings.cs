namespace NexBot.Configuration;

/// <summary>The attack pet (phBot's Pet tab).</summary>
public sealed class PetSettings
{
    /// <summary>Look after the attack pet: heal and feed it, keep its attack mode, revive it or return when it dies.</summary>
    public bool UseAttackPet { get; set; }

    /// <summary>Use a pet recovery kit when the pet's HP falls below this percent (0: never).</summary>
    public int HealBelow { get; set; } = 50;

    /// <summary>Feed the pet (HGP potion) when its HGP falls below this percent (0: never).</summary>
    public int FeedBelow { get; set; } = 30;

    /// <summary>Summon the attack pet whenever it is not out (also right after it was revived).</summary>
    public bool AutoSummon { get; set; } = true;
    /// <summary>Summon it only in a town.</summary>
    public bool SummonOnlyInTown { get; set; }
    /// <summary>Summon it only inside the training area.</summary>
    public bool SummonOnlyAtTrainingArea { get; set; }

    /// <summary>Revive the dead attack pet with a pet revive item.</summary>
    public bool AutoRevive { get; set; } = true;
    /// <summary>How many times per session (0: no limit).</summary>
    public int ReviveTimes { get; set; }
    /// <summary>Revive only when there are pet recovery kits (HP potions for the pet) in the bag.</summary>
    public bool ReviveOnlyWithPotions { get; set; }

    /// <summary>Return to town when the attack pet dies (to buy revive items).</summary>
    public bool ReturnWhenDies { get; set; }
    /// <summary>Return only when there are no pet revive items left.</summary>
    public bool ReturnOnlyIfNoRevive { get; set; } = true;

    /// <summary>
    /// Attack pet scrolls (server names) in order of preference: pet 1 is summoned (or revived) when it is in the bag,
    /// else pet 2, and so on; with none of them, any attack pet in the bag.
    /// </summary>
    public List<string> Preferred { get; set; } = [];

    /// <summary>Keep the pet passive: it does not attack monsters.</summary>
    public bool DontAttack { get; set; }
}
