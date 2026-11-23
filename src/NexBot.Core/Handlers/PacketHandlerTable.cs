using System.Collections.Frozen;
using NexBot.Network;
using NexBot.Sessions;
using S = NexBot.Network.Opcodes.Server;

namespace NexBot.Handlers;

/// <summary>
/// Opcode → built-in handler table. A frozen dictionary gives a fast lookup with no per-packet allocation,
/// replacing the legacy 300-line <c>switch</c> inside the socket loop.
/// </summary>
internal static class PacketHandlerTable
{
    public static FrozenDictionary<ushort, PacketHandler> Handlers { get; } = new Dictionary<ushort, PacketHandler>
    {
        // Login is done by BotSession; only the world part is tracked here.
        [S.CelestialPosition] = SessionHandlers.CelestialPosition,
        [S.TeleportReadyRequest] = SessionHandlers.TeleportReady,
        [S.CharacterSelectionJoinResponse] = SessionHandlers.CharacterSelectionJoin,

        // Character
        [S.CharacterDataBegin] = CharacterHandlers.DataBegin,
        [S.CharacterData] = CharacterHandlers.Data,
        [S.CharacterDataEnd] = CharacterHandlers.DataEnd,
        [S.CharacterStatsUpdate] = CharacterHandlers.StatsUpdate,
        [S.CharacterInfoUpdate] = SkillHandlers.InfoUpdate,
        [S.MasterySkillLevelUpResponse] = SkillHandlers.SkillLearned,
        [S.MasteryLevelUpResponse] = SkillHandlers.MasteryRaised,
        [S.CharacterExperienceUpdate] = CharacterHandlers.ExperienceUpdate,
        [S.EntityLevelUp] = CharacterHandlers.LevelUp,
        [S.CharacterAddStrResponse] = (client, p) => CharacterHandlers.StatAdded(client, p, true),
        [S.CharacterAddIntResponse] = (client, p) => CharacterHandlers.StatAdded(client, p, false),
        [S.ItemMovementResponse] = CharacterHandlers.ItemMovement,
        [S.ItemUseResponse] = CharacterHandlers.ItemUse,
        [S.ItemUpdate] = CharacterHandlers.ItemUpdate,
        [S.ItemDurabilityUpdate] = CharacterHandlers.ItemDurabilityUpdate,
        [S.InventoryCapacityUpdate] = CharacterHandlers.InventoryCapacityUpdate,
        [S.StorageDataBegin] = StorageHandlers.Begin,
        [S.ExchangeStarted] = ExchangeHandlers.Started,
        [S.ExchangeInvitationResponse] = ExchangeHandlers.RequestAnswer,
        [S.ExchangeItemsUpdate] = ExchangeHandlers.Items,
        [S.ExchangeGoldUpdate] = ExchangeHandlers.Gold,
        [S.ExchangePlayerConfirmed] = ExchangeHandlers.PartnerConfirmed,
        [S.ExchangeConfirmResponse] = ExchangeHandlers.ConfirmAnswer,
        [S.ExchangeApproveResponse] = ExchangeHandlers.ApproveAnswer,
        [S.ExchangeCompleted] = ExchangeHandlers.Completed,
        [S.ExchangeCanceled] = ExchangeHandlers.Canceled,
        [S.ExchangeExitResponse] = ExchangeHandlers.CancelAnswer,
        [S.StorageData] = StorageHandlers.Data,
        [S.StorageDataEnd] = StorageHandlers.End,
        [S.EntityTalkResponse] = StorageHandlers.TalkAnswer,

        // Spawns
        [S.EntityGroupSpawnBegin] = SpawnHandlers.GroupSpawnBegin,
        [S.EntityGroupSpawnData] = SpawnHandlers.GroupSpawnData,
        [S.EntityGroupSpawnEnd] = SpawnHandlers.GroupSpawnEnd,
        [S.EntitySpawn] = SpawnHandlers.Spawn,
        [S.EntityDespawn] = SpawnHandlers.Despawn,
        [S.EntitySelection] = SpawnHandlers.Selection,

        // World
        [S.EntityMovement] = WorldHandlers.Movement,
        [S.EntityMovementStuck] = WorldHandlers.MovementStuck,
        [S.EntityMovementAngle] = WorldHandlers.MovementAngle,
        [S.EntityStatusUpdate] = WorldHandlers.StatusUpdate,
        [S.EntityStateUpdate] = WorldHandlers.StateUpdate,
        [S.EntitySpeedUpdate] = WorldHandlers.SpeedUpdate,
        [S.EntitySkillStart] = WorldHandlers.SkillStart,
        [S.EntitySkillEnd] = WorldHandlers.SkillEnd,
        [S.EntitySkillBuffAdded] = WorldHandlers.BuffAdded,
        [S.EntitySkillBuffRemoved] = WorldHandlers.BuffRemoved,
        [S.PetData] = WorldHandlers.PetData,
        [S.PetUpdate] = WorldHandlers.PetUpdate,
        [S.PetSettingsChangeResponse] = WorldHandlers.PetSettingsChanged,
        [S.PetPlayerMounted] = WorldHandlers.PlayerMounted,
        [S.CharacterWearCape] = WorldHandlers.PvpCapeResponse,

        // Social
        [S.ChatUpdate] = SocialHandlers.Chat,
        [S.GuildDataBegin] = SocialHandlers.GuildDataBegin,
        [S.GuildData] = SocialHandlers.GuildData,
        [S.GuildDataEnd] = SocialHandlers.GuildDataEnd,
        [S.ChatResponse] = SocialHandlers.ChatResponse,
        [S.PartyMatchCreationResponse] = SocialHandlers.PartyMatchCreated,
        [S.PartyMatchDeleteResponse] = SocialHandlers.PartyMatchDeleted,
        [S.PartyMatchJoinRequest] = SocialHandlers.PartyMatchJoinRequest,
        [S.PartyMatchListResponse] = SocialHandlers.PartyMatchList,
        [S.UniqueAction] = WorldHandlers.UniqueAction,
        [S.StallCreateResponse] = StallHandlers.Answer,
        [S.AlchemyReinforceResponse] = static (c, p) => c.OnAlchemyAnswer(p),
        [S.AlchemyStoneResponse] = static (c, p) => c.OnAlchemyAnswer(p),
        [S.StallDestroyResponse] = StallHandlers.Answer,
        [S.StallUpdateResponse] = StallHandlers.Answer,
        [S.StallLeaveResponse] = StallHandlers.Leave,
        [S.StallEntityAction] = StallHandlers.EntityAction,
        [S.EntityStallCreate] = StallHandlers.EntityCreate,
        [S.EntityStallDestroy] = StallHandlers.EntityDestroy,
        [S.EntityStallTitleUpdate] = StallHandlers.EntityTitle,
        [S.PartyData] = SocialHandlers.PartyData,
        [S.PartyUpdate] = SocialHandlers.PartyUpdate,
        [S.PlayerPetitionRequest] = SocialHandlers.PetitionRequest,
    }.ToFrozenDictionary();
}
