namespace NexBot.Network;

/// <summary>Packet opcodes for vSRO 1.188.</summary>
public static class Opcodes
{
    /// <summary>Opcodes used by both directions / the security layer.</summary>
    public static class Global
    {
        public const ushort Handshake = 0x5000;
        public const ushort HandshakeAccept = 0x9000;
        public const ushort Identification = 0x2001;
        public const ushort Ping = 0x2002;
        public const ushort XtrapIdentification = 0x2113;
        public const ushort Massive = 0x600D;
    }

    /// <summary>Client to server.</summary>
    public static class Client
    {
        // Gateway
        public const ushort PatchRequest = 0x6100;
        public const ushort ShardListRequest = 0x6101;
        public const ushort LoginRequest = 0x6102;
        public const ushort CaptchaAnswer = 0x6323;

        // Agent login
        public const ushort AuthRequest = 0x6103;
        public const ushort CharacterSelectionJoin = 0x7001;
        public const ushort CharacterSelectionAction = 0x7007;
        public const ushort CharacterConfirmSpawn = 0x3012;
        public const ushort GameMasterCommand = 0x7010;

        // Character
        public const ushort Movement = 0x7021;
        public const ushort MovementAngle = 0x7024;
        public const ushort Chat = 0x7025;
        public const ushort AddStr = 0x7050;
        public const ushort AddInt = 0x7051;
        public const ushort AutoResurrection = 0x3053;
        public const ushort Berserk = 0x70A7;
        public const ushort ItemRepair = 0x703E;
        public const ushort Action = 0x7074;
        public const ushort EmoteUse = 0x3091;

        // Inventory / storage
        public const ushort ItemMovement = 0x7034;
        public const ushort ItemUse = 0x704C;
        public const ushort StorageDataRequest = 0x703C;

        // Entities / NPC
        public const ushort EntitySelection = 0x7045;
        public const ushort EntityTalk = 0x7046;
        public const ushort NpcClose = 0x704B;

        // Petitions (exchange / party invitations ...)
        public const ushort PlayerPetitionResponse = 0x3080;

        // Party
        public const ushort PartyCreation = 0x7060;
        public const ushort PartyLeave = 0x7061;
        public const ushort PartyInvitation = 0x7062;
        public const ushort PartyKick = 0x7063;
        public const ushort PartyMatchCreation = 0x7069;
        public const ushort PartyMatchEdit = 0x706A;
        public const ushort PartyMatchDelete = 0x706B;
        public const ushort PartyMatchList = 0x706C;
        public const ushort PartyMatchJoin = 0x706D;
        public const ushort PartyMatchJoinResponse = 0x306E;

        // Guild / academy
        public const ushort GuildInvitation = 0x70F3;
        public const ushort UnionInvitation = 0x70FB;
        public const ushort GuildNoticeEdit = 0x70F9;
        public const ushort GuildStorage = 0x7250;
        public const ushort AcademyInvitation = 0x7472;
        public const ushort AcademyNoticeEdit = 0x7477;
        public const ushort AcademyMatchList = 0x747D;

        // Pets
        public const ushort PetAction = 0x70C5;
        public const ushort PetMount = 0x70CB;
        public const ushort PetUnsummon = 0x7116;
        public const ushort PetSettingsChange = 0x7420;

        // Stall
        public const ushort StallCreate = 0x70B1;
        public const ushort StallDestroy = 0x70B2;
        public const ushort StallTalk = 0x70B3;
        public const ushort StallBuy = 0x70B4;
        public const ushort StallLeave = 0x70B5;
        public const ushort StallUpdate = 0x70BA;

        // Skills / masteries
        public const ushort MasterySkillLevelUp = 0x70A1;
        public const ushort MasteryLevelUp = 0x70A2;
        public const ushort MasterySkillLevelDown = 0x7202;
        public const ushort MasteryLevelDown = 0x7203;

        // Teleport
        public const ushort TeleportRecall = 0x7059;

        // Alchemy
        public const ushort AlchemyReinforce = 0x7150;
        public const ushort AlchemyStone = 0x7151;
        public const ushort TeleportUse = 0x705A;
        public const ushort TeleportReadyResponse = 0x34B6;

        // Exchange
        public const ushort ExchangeInvitation = 0x7081;
        public const ushort ExchangeConfirm = 0x7082;
        public const ushort ExchangeApprove = 0x7083;
        public const ushort ExchangeExit = 0x7084;

        // Consignment / mail
        public const ushort ConsignmentRegister = 0x7508;
        public const ushort ConsignmentUnregister = 0x7509;
        public const ushort ConsignmentList = 0x750E;
        public const ushort MailSend = 0x7309;

        // PvP
        public const ushort PvpCape = 0x7516;

        // MaxiGuard filter
        public const ushort CharacterPin = 0xA179;
    }

    /// <summary>Server to client.</summary>
    public static class Server
    {
        // Gateway
        public const ushort PatchResponse = 0xA100;
        public const ushort ShardListResponse = 0xA101;
        public const ushort LoginResponse = 0xA102;
        public const ushort CaptchaImage = 0x2322;
        public const ushort CaptchaResponse = 0xA323;

        // Agent login
        public const ushort AuthResponse = 0xA103;
        public const ushort CharacterSelectionJoinResponse = 0xB001;
        public const ushort CharacterSelectionActionResponse = 0xB007;

        // Character data
        public const ushort CharacterDataBegin = 0x34A5;
        public const ushort CharacterData = 0x3013;
        public const ushort CharacterDataEnd = 0x34A6;
        public const ushort CharacterStatsUpdate = 0x303D;
        public const ushort CharacterExperienceUpdate = 0x3056;
        public const ushort CharacterInfoUpdate = 0x304E;
        public const ushort CharacterDied = 0x3011;
        public const ushort CharacterAddStrResponse = 0xB050;
        public const ushort CharacterAddIntResponse = 0xB051;
        public const ushort CharacterWearCape = 0xB516;
        public const ushort CelestialPosition = 0x3020;
        public const ushort CelestialUpdate = 0x3027;
        public const ushort WeatherUpdate = 0x3809;

        // Inventory / storage
        public const ushort ItemUseResponse = 0xB04C;
        public const ushort ItemMovementResponse = 0xB034;
        public const ushort ItemDurabilityUpdate = 0x3052;
        public const ushort ItemUpdate = 0x3040;
        public const ushort ItemRepairResponse = 0xB03E;
        public const ushort InventoryCapacityUpdate = 0x3092;
        public const ushort StorageDataBegin = 0x3047;
        public const ushort StorageDataEnd = 0x3048;
        public const ushort StorageData = 0x3049;
        public const ushort DropUnlocked = 0x304D;

        // Entities
        public const ushort EntitySpawn = 0x3015;
        public const ushort EntityDespawn = 0x3016;
        public const ushort EntityGroupSpawnBegin = 0x3017;
        public const ushort EntityGroupSpawnEnd = 0x3018;
        public const ushort EntityGroupSpawnData = 0x3019;
        public const ushort EntityMovement = 0xB021;
        public const ushort EntityMovementStuck = 0xB023;
        public const ushort EntityMovementAngle = 0xB024;
        public const ushort EntitySelection = 0xB045;
        public const ushort EntityTalkResponse = 0xB046;
        public const ushort EntityLevelUp = 0x3054;
        public const ushort EntityStatusUpdate = 0x3057;
        public const ushort EntityDisplayEffect = 0x305C;
        public const ushort EntityStateUpdate = 0x30BF;
        public const ushort EntitySpeedUpdate = 0x30D0;
        public const ushort EntityEmoteUse = 0x3091;
        public const ushort EntityStallCreate = 0x30B8;
        public const ushort EntityStallDestroy = 0x30B9;
        public const ushort EntityStallTitleUpdate = 0x30BB;
        public const ushort UniqueAction = 0x300C;

        // Skills
        public const ushort EntitySkillStart = 0xB070;
        public const ushort EntitySkillEnd = 0xB071;
        public const ushort EntitySkillBuffRemoved = 0xB072;
        public const ushort EntitySkillBuffAdded = 0xB0BD;
        public const ushort MasterySkillLevelUpResponse = 0xB0A1;
        public const ushort MasteryLevelUpResponse = 0xB0A2;
        public const ushort MasterySkillLevelDownResponse = 0xB202;
        public const ushort MasteryLevelDownResponse = 0xB203;

        // Chat / petitions / mail
        public const ushort ChatResponse = 0xB025;
        public const ushort ChatUpdate = 0x3026;
        public const ushort PlayerPetitionRequest = 0x3080;
        public const ushort MailSendResponse = 0xB309;

        // NPC
        public const ushort NpcCloseResponse = 0xB04B;

        // Party
        public const ushort PartyInvitationResponse = 0xB060;
        public const ushort PartyData = 0x3065;
        public const ushort PartyUpdate = 0x3864;
        public const ushort PartyMatchCreationResponse = 0xB069;
        public const ushort PartyMatchEditResponse = 0xB06A;
        public const ushort PartyMatchDeleteResponse = 0xB06B;
        public const ushort PartyMatchListResponse = 0xB06C;
        /// <summary>Sent by the server to the party master when someone wants to join a party match.</summary>
        public const ushort PartyMatchJoinRequest = 0x706D;

        // Pets
        public const ushort PetData = 0x30C8;
        public const ushort PetUpdate = 0x30C9;
        public const ushort PetSettingsChangeResponse = 0xB420;
        public const ushort PetPlayerMounted = 0xB0CB;

        // Stall
        public const ushort StallCreateResponse = 0xB0B1;
        public const ushort StallDestroyResponse = 0xB0B2;
        public const ushort StallTalkResponse = 0xB0B3;
        public const ushort StallBuyResponse = 0xB0B4;
        public const ushort StallLeaveResponse = 0xB0B5;
        public const ushort StallUpdateResponse = 0xB0BA;
        public const ushort StallEntityAction = 0x30B7;

        // Teleport
        public const ushort TeleportRecallResponse = 0xB059;

        // Alchemy
        public const ushort AlchemyReinforceResponse = 0xB150;
        public const ushort AlchemyStoneResponse = 0xB151;
        public const ushort TeleportUseResponse = 0xB05A;
        public const ushort TeleportReadyRequest = 0x34B5;

        // Guild / academy
        public const ushort GuildCreatedData = 0xB0F0;
        public const ushort GuildDataBegin = 0x34B3;
        public const ushort GuildDataEnd = 0x34B4;
        public const ushort GuildData = 0x3101;
        public const ushort GuildUpdate = 0x38F5;
        public const ushort GuildPlayerLog = 0x30FF;
        public const ushort GuildStorageResponse = 0xB250;
        public const ushort GuildStorageDataBegin = 0x3253;
        public const ushort GuildStorageDataEnd = 0x3254;
        public const ushort GuildStorageData = 0x3255;
        public const ushort AcademyData = 0x3C81;
        public const ushort AcademyMatchListResponse = 0xB47D;

        // Exchange
        public const ushort ExchangeStarted = 0x3085;
        public const ushort ExchangePlayerConfirmed = 0x3086;
        public const ushort ExchangeCompleted = 0x3087;
        public const ushort ExchangeCanceled = 0x3088;
        public const ushort ExchangeGoldUpdate = 0x3089;
        public const ushort ExchangeItemsUpdate = 0x308C;
        public const ushort ExchangeInvitationResponse = 0xB081;
        public const ushort ExchangeConfirmResponse = 0xB082;
        public const ushort ExchangeApproveResponse = 0xB083;
        public const ushort ExchangeExitResponse = 0xB084;

        // Consignment
        public const ushort ConsignmentRegisterResponse = 0xB508;
        public const ushort ConsignmentUnregisterResponse = 0xB509;

        // MaxiGuard filter
        public const ushort CharacterPinWindow = 0xA178;
    }
}
