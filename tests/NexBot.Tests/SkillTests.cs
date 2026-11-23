using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Map;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>The skill window: mastery raise, skill learn / upgrade, and the window snapshot.</summary>
public class SkillTests
{
    private const uint Bicheon = 257, Cold = 273;
    private const uint SmashA1 = 1001, SmashA2 = 1002, SmashB1 = 1101;

    private const uint GroupA = 9101, GroupB = 9102;

    private static InMemoryGameData Data()
    {
        var data = WorldTests.Data();
        data.Masteries[Bicheon] = new MasteryInfo(Bicheon, "Bicheon Mastery", "", "UIIT_CTL_WEAPON_SKILL", 0, "skillmastery\\china\\mastery_sword.ddj");
        data.Masteries[Cold] = new MasteryInfo(Cold, "Cold Mastery", "", "UIIT_CTL_FORCE_SKILL", 1, "");
        data.Masteries[513] = new MasteryInfo(513, "Warrior", "", "UIIT_STT_SKILL_EU_PHYSICAL", 0, ""); // European: hidden for a Chinese character

        SkillTreeInfo Tree(uint id, string code, byte level, byte masteryLevel, uint sp, byte row, uint req = 0, byte reqLevel = 0) =>
            new(id, $"{code}_{level:00}", code[(code.LastIndexOf('_') - 5)..].Replace('_', ' '), code, level, Bicheon, masteryLevel, sp,
                "skill\\china\\sword_smash.ddj", 0, row, req, reqLevel, 0, 0, GroupId: code.EndsWith('A') ? GroupA : GroupB);
        data.Trees[Bicheon] =
        [
            Tree(SmashA1, "SKILL_CH_SWORD_SMASH_A", 1, 1, 10, 0),
            Tree(SmashA2, "SKILL_CH_SWORD_SMASH_A", 2, 3, 20, 0),
            // Like the game data: the requirement is Smash A's GROUP ID (not a skill ID).
            Tree(SmashB1, "SKILL_CH_SWORD_SMASH_B", 1, 3, 30, 1, req: GroupA, reqLevel: 2),
        ];
        data.Trees[Cold] = [];
        foreach (var t in data.Trees[Bicheon])
            data.Skills[t.Id] = new SkillInfo(t.Id, t.ServerName, t.Name, 0, "", 0, 0, 0, 0, true, t.MasteryLevel, "", t.Icon, default, default, t.Level);
        for (byte level = 1; level <= 10; level++)
            data.Levels[level] = new LevelInfo(level, 100, 100, 0, 0, 0, MasterySp: level * 5u);
        return data;
    }

    private static SroClient InGame(InMemoryGameData data, List<Packet> sent = null)
    {
        var client = new SroClient(data, NullLogger.Instance, p => { sent?.Add(p); return true; });
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        return client;
    }

    private static Packet SkillLearned(uint id) => new Packet(Opcodes.Server.MasterySkillLevelUpResponse).WriteByte(1).WriteUInt32(id);

    private static Packet MasteryRaised(uint id, byte level) =>
        new Packet(Opcodes.Server.MasteryLevelUpResponse).WriteByte(1).WriteUInt32(id).WriteByte(level);

    private static Packet SpUpdate(uint sp) => new Packet(Opcodes.Server.CharacterInfoUpdate).WriteByte(2).WriteUInt32(sp);

    private static SkillSlot Slot(SkillWindow window, int line, int row) =>
        window.Tabs[0].Masteries.Single(m => m.Id == Bicheon).Lines[line].Slots[row]!;

    [Fact]
    public void Packets_have_the_vsro_layout()
    {
        var learn = ClientPackets.LearnSkill(0x01020304);
        Assert.Equal(Opcodes.Client.MasterySkillLevelUp, learn.Opcode);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, learn.ToArray());

        var raise = ClientPackets.RaiseMastery(Bicheon);
        Assert.Equal(Opcodes.Client.MasteryLevelUp, raise.Opcode);
        Assert.Equal(new byte[] { 0x01, 0x01, 0, 0, 1 }, raise.ToArray());
    }

    [Fact]
    public void Answers_update_mastery_skills_and_sp()
    {
        using var client = InGame(Data());
        var c = client.World.Character!;

        client.HandlePacket(SpUpdate(12345));
        Assert.Equal(12345u, c.Sp);

        client.HandlePacket(MasteryRaised(Bicheon, 3));
        Assert.Equal(3, c.Masteries[Bicheon].Level);

        // A new level replaces the old one of the same skill line.
        client.HandlePacket(SkillLearned(SmashA1));
        client.HandlePacket(SkillLearned(SmashA2));
        Assert.Contains(SmashA2, c.Skills.Keys);
        Assert.DoesNotContain(SmashA1, c.Skills.Keys);

        SkillLearnedEventArgs refused = null;
        client.SkillLearned += (_, e) => refused = e;
        client.HandlePacket(new Packet(Opcodes.Server.MasterySkillLevelUpResponse).WriteByte(2).WriteUInt16(0x3802));
        Assert.NotNull(refused);
        Assert.False(refused.Success);
        Assert.Equal(0x3802, refused.ErrorCode);
    }

    [Fact]
    public void Window_shows_the_lines_and_what_can_be_learned()
    {
        var data = Data();
        using var client = InGame(data);
        client.HandlePacket(SpUpdate(1000));
        foreach (var m in client.World.Character!.Masteries.Values)
            m.Level = 0;

        var window = SkillWindow.Take(client)!;
        Assert.False(window.NeedsRebuild);
        Assert.Equal(["Weapon", "Force"], window.Tabs.Select(t => t.Name));
        Assert.DoesNotContain(window.Tabs.SelectMany(t => t.Masteries), m => m.Id == 513);
        var page = window.Tabs[0].Masteries.Single(m => m.Id == Bicheon);
        Assert.Equal(5u, page.RaiseSp); // LevelData of level 1
        Assert.Null(page.RaiseMissing);
        var line = Assert.Single(page.Lines);
        Assert.Equal(SkillWindow.SlotsPerLine, line.Slots.Count);
        Assert.Equal(SkillSlotState.Locked, Slot(window, 0, 0).State); // needs mastery level 1
        Assert.Contains("mastery Lv. 1", Slot(window, 0, 0).Missing);

        client.HandlePacket(MasteryRaised(Bicheon, 3));
        window = SkillWindow.Take(client)!;
        Assert.Equal(3, window.MasteryTotal);
        var a = Slot(window, 0, 0);
        Assert.Equal((SkillSlotState.Learnable, SmashA1, 10u), (a.State, a.NextId, a.NextSp));
        Assert.Equal(SkillSlotState.Locked, Slot(window, 0, 1).State); // needs Smash A level 2

        client.HandlePacket(SkillLearned(SmashA1));
        window = SkillWindow.Take(client)!;
        Assert.Equal((SkillSlotState.Upgradable, (byte)1, SmashA2), (Slot(window, 0, 0).State, Slot(window, 0, 0).Level, Slot(window, 0, 0).NextId));
        Assert.Contains("Lv. 2", Slot(window, 0, 1).Missing);

        client.HandlePacket(SkillLearned(SmashA2));
        window = SkillWindow.Take(client)!;
        Assert.Equal(SkillSlotState.Max, Slot(window, 0, 0).State);
        Assert.Equal(SkillSlotState.Learnable, Slot(window, 0, 1).State);

        // Not enough skill points: the slot says why.
        client.HandlePacket(SpUpdate(5));
        Assert.Contains("SP", SkillWindow.Take(client)!.Tabs[0].Masteries[0].Lines[0].Slots[1]!.Missing);
    }

    private static AutoLevelOptions Auto(bool masteries = true, bool skills = true, int gap = 0, uint keep = 0) =>
        new AutoLevelSettings
        {
            Masteries = masteries, Skills = skills, MasteryIds = [Bicheon], SkillGroups = ["SKILL_CH_SWORD_SMASH_A"],
            MasteryGap = gap, KeepSp = keep,
        }.ToOptions();

    [Fact]
    public void Trainer_learns_chosen_skills_first_then_the_lowest_mastery()
    {
        using var client = InGame(Data());
        client.HandlePacket(SpUpdate(1000));
        foreach (var m in client.World.Character!.Masteries.Values)
            m.Level = 0;
        client.HandlePacket(MasteryRaised(Bicheon, 3));
        var window = SkillWindow.Take(client)!;

        var step = SkillTrainer.NextStep(window, Auto())!;
        Assert.Equal((false, SmashA1, 10u), (step.IsMastery, step.Id, step.Sp));

        // Smash B is learnable later but not chosen; with skills off the mastery is next (LevelData of level 4).
        step = SkillTrainer.NextStep(window, Auto(skills: false))!;
        Assert.Equal((true, Bicheon, 20u), (step.IsMastery, step.Id, step.Sp));

        Assert.Null(SkillTrainer.NextStep(window, Auto(keep: 995))); // only 5 SP may be spent
        Assert.Null(SkillTrainer.NextStep(window, Auto(skills: false, gap: 98))); // 3 + 98 = character level 101
        Assert.Null(SkillTrainer.NextStep(window, Auto(), new HashSet<string> { "skill:SKILL_CH_SWORD_SMASH_A", "mastery:257" }));
        Assert.False(Auto(masteries: false, skills: false).Enabled);
    }

    [Fact]
    public void Locked_skills_are_skipped_and_never_block_the_others()
    {
        using var client = InGame(Data());
        client.HandlePacket(SpUpdate(1000));
        foreach (var m in client.World.Character!.Masteries.Values)
            m.Level = 0;
        client.HandlePacket(MasteryRaised(Bicheon, 3));
        var window = SkillWindow.Take(client)!;
        AutoLevelOptions Only(params string[] groups) =>
            Auto(masteries: false) with { SkillGroups = new HashSet<string>(groups, StringComparer.Ordinal) };

        // Smash B needs Smash A Lv. 2: alone it is locked, nothing is tried and the status says why.
        Assert.Null(SkillTrainer.NextStep(window, Only("SKILL_CH_SWORD_SMASH_B")));
        string why = SkillTrainer.Explain(window, Only("SKILL_CH_SWORD_SMASH_B"));
        Assert.Contains("1 locked (skipped)", why);
        Assert.Contains("Smash A Lv. 2", why, StringComparison.OrdinalIgnoreCase);

        // Chosen first, B still does not hold up A.
        Assert.Equal(SmashA1, SkillTrainer.NextStep(window, Only("SKILL_CH_SWORD_SMASH_B", "SKILL_CH_SWORD_SMASH_A"))!.Id);

        // A refused step waits longer each time: 1, 2, 4 ... 30 minutes.
        Assert.Equal([1d, 2d, 4d, 8d, 16d, 30d, 30d], Enumerable.Range(1, 7).Select(n => SkillTrainer.RefusedPauseFor(n).TotalMinutes));
    }

    [Fact]
    public async Task Trainer_step_sends_and_applies_the_answer()
    {
        var sent = new List<Packet>();
        SroClient live = null;
        // Fake server: answers every learn request at once.
        using var client = new SroClient(Data(), NullLogger.Instance, p =>
        {
            sent.Add(p);
            if (p.Opcode == Opcodes.Client.MasterySkillLevelUp)
                live.HandlePacket(SkillLearned(p.ReadUInt32()));
            return true;
        });
        live = client;
        live.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        live.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        live.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        live.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        live.HandlePacket(SpUpdate(1000));
        live.HandlePacket(MasteryRaised(Bicheon, 3));

        var trainer = new SkillTrainer(live, NullLogger.Instance) { Options = Auto(masteries: false) };
        Assert.True(await trainer.TryStepAsync());
        Assert.True(await trainer.TryStepAsync());
        Assert.False(await trainer.TryStepAsync()); // Smash A is at its max (2), nothing else chosen
        Assert.Equal(2, sent.Count);
        Assert.Contains(SmashA2, live.World.Character!.Skills.Keys);
        Assert.StartsWith("Waiting", trainer.Status);
        Assert.Contains("1 skill chosen, 1 at max", trainer.Status);

        // ALL SKILLS on Cold only: Bicheon's Smash B is not auto.
        trainer.Options = Auto(masteries: false) with { AllSkillMasteries = new HashSet<uint> { Cold } };
        Assert.False(await trainer.TryStepAsync());

        // ALL SKILLS on Bicheon: every Bicheon line, so Smash B (needs Smash A Lv. 2) comes next.
        trainer.Options = Auto(masteries: false) with { AllSkillMasteries = new HashSet<uint> { Bicheon } };
        Assert.True(await trainer.TryStepAsync());
        Assert.Contains(SmashB1, live.World.Character!.Skills.Keys);
    }

    [Fact]
    public void Old_database_asks_for_a_rebuild()
    {
        var data = Data();
        data.Trees.Clear();
        using var client = InGame(data);
        Assert.True(SkillWindow.Take(client)!.NeedsRebuild);
    }

    [Fact]
    public async Task Learn_waits_for_the_server_answer()
    {
        var sent = new List<Packet>();
        using var client = InGame(Data(), sent);

        var learn = client.LearnSkillAsync(SmashA1);
        var packet = Assert.Single(sent);
        Assert.Equal(Opcodes.Client.MasterySkillLevelUp, packet.Opcode);
        client.HandlePacket(SkillLearned(SmashA1));
        var result = await learn;
        Assert.True(result.Ok, result.Message);

        var raise = client.RaiseMasteryAsync(Bicheon);
        client.HandlePacket(new Packet(Opcodes.Server.MasteryLevelUpResponse).WriteByte(2).WriteUInt16(0x3804));
        var refused = await raise;
        Assert.False(refused.Ok);
        Assert.Equal(0x3804, refused.ErrorCode);
    }
}
