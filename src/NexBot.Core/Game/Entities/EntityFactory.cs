using NexBot.Data;
using NexBot.Game.Items;

namespace NexBot.Game.Entities;

/// <summary>Creates the right <see cref="Entity"/> subclass for a reference object ID.</summary>
public static class EntityFactory
{
    public const uint SkillZoneRefId = uint.MaxValue;

    public static Entity Create(IGameData data, uint refId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (refId == SkillZoneRefId)
            return new SkillZone();

        if (data.GetModel(refId) is { } model)
            return CreateModel(model);

        if (data.GetItem(refId) is { } item)
            return new Drop(item);

        if (data.GetTeleport(refId) is { } teleport)
            return new Teleport(teleport, data.GetTeleportLinks(refId));

        throw new UnknownGameObjectException($"Object {refId} is not in the game database (models/items/teleports).");
    }

    public static Model CreateModel(ModelInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.Tid2 switch
        {
            1 => new Player(info),
            2 => info.Tid3 switch
            {
                1 => new Mob(info),
                2 => new Guide(info),
                3 => info.Tid4 switch
                {
                    3 => new AttackPet(info),
                    4 => new PickPet(info),
                    _ => new Cos(info),
                },
                4 => new FortressCos(info),
                5 => new FortressStruct(info),
                _ => new Npc(info),
            },
            _ => new Model(info),
        };
    }

    public static Character CreateCharacter(IGameData data, uint refId)
    {
        ArgumentNullException.ThrowIfNull(data);
        var info = data.GetModel(refId)
                   ?? throw new UnknownGameObjectException($"Character model {refId} is not in the game database.");
        return new Character(info);
    }
}
