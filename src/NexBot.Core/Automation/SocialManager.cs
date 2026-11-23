using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Party, guild and academy automation and player tracing (phBot's Party, Guild, Academy and Players tabs): answers
/// invitations by the accept rules, invites nearby players, leaves a party whose master is not trusted, keeps the
/// party in the matching list or joins one, and follows a player.
/// </summary>
public sealed class SocialManager : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InviteCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GuildInviteCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MatchReform = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MatchSearch = TimeSpan.FromSeconds(20);

    private readonly SroClient _client;
    private readonly SocialSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DateTime> _invited = new(StringComparer.OrdinalIgnoreCase);
    private uint _matchNumber;
    private DateTime _matchFormed = DateTime.MinValue;
    private DateTime _matchSearched = DateTime.MinValue;
    private DateTime _lastLeave = DateTime.MinValue;
    private DateTime _lastTrace = DateTime.MinValue;
    private bool _tracing;

    public SocialManager(SroClient client, SocialSettings settings, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _client.PetitionReceived += OnPetition;
        _client.PartyMatchCreated += OnMatchCreated;
        _client.PartyMatchJoinRequested += OnMatchJoinRequested;
        _client.PartyMatchListReceived += OnMatchList;
        _client.ChatReceived += OnChat;
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>True while the bot is busy (script, trade route, town loop): exchange requests may be left alone then.</summary>
    public Func<bool> IsBotting { get; set; } = () => false;

    /// <summary>True when the character is in a town.</summary>
    public Func<bool> InTown { get; set; } = () => false;

    /// <summary>The last party matching list received (for the Party page).</summary>
    public IReadOnlyList<PartyMatch> Matches { get; private set; } = [];

    /// <summary>Raised when <see cref="Matches"/> changed.</summary>
    public event EventHandler? MatchesChanged;

    /// <summary>The player being followed, or empty.</summary>
    public string Tracing => _tracing ? _settings.Trace.Player : "";

    public void StartTrace(string player)
    {
        _settings.Trace.Player = player.Trim();
        _tracing = _settings.Trace.Player.Length > 0;
        _lastTrace = DateTime.MinValue;
    }

    public void StopTrace() => _tracing = false;

    /// <summary>Asks the server for the party matching list (answer in <see cref="MatchesChanged"/>).</summary>
    public void RefreshMatches() => _client.RequestPartyMatchList(0);

    /// <summary>A rough level for another player: the highest level required by what they wear.</summary>
    public static int EstimateLevel(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        int level = 0;
        foreach (var item in player.Inventory.Where(i => i is Equipable))
            level = Math.Max(level, item!.LevelRequired);
        return level;
    }

    private static bool InList(IEnumerable<string> list, string name) =>
        name.Length > 0 && list.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private bool NotInvitedRecently(string name, TimeSpan cooldown)
    {
        var now = DateTime.UtcNow;
        if (_invited.TryGetValue(name, out var at) && now - at < cooldown)
            return false;
        _invited[name] = now;
        return true;
    }

    // ---- invitations ------------------------------------------------------------------------

    private void OnPetition(object? sender, PetitionEventArgs e)
    {
        switch (e.Type)
        {
            case PlayerPetition.PartyCreation or PlayerPetition.PartyInvitation:
            {
                var party = _settings.Party;
                var answer = !party.AcceptInvites ? (bool?)null
                    : party.AcceptAll || (party.AcceptFromList && InList(party.AcceptInviteList, e.InviterName)) ? true
                    : party.RefuseNotInList ? false : null;
                Answer(e, answer, TimeSpan.FromSeconds(Math.Clamp(party.AcceptDelay, 0, 60)), "party");
                break;
            }
            case PlayerPetition.GuildInvitation:
                Answer(e, _settings.Guild.AcceptAllInvites ? true : null, TimeSpan.FromSeconds(1), "guild");
                break;
            case PlayerPetition.ExchangeRequest:
                AnswerExchange(e);
                break;
            case PlayerPetition.AcademyInvitation:
            {
                var academy = _settings.Academy;
                var answer = !academy.AcceptInvites ? (bool?)null
                    : academy.AcceptAll || (academy.AcceptFromList && InList(academy.AcceptInviteList, e.InviterName)) ? true
                    : academy.RefuseNotInList ? false : null;
                Answer(e, answer, TimeSpan.FromSeconds(1), "academy");
                break;
            }
        }
    }

    /// <summary>
    /// Auto accept exchange: accepts when it is on, unless the bot is busy (and accepting while botting is off) or
    /// the character is outside town (and town only is on). Otherwise the request waits on the Inventory page.
    /// </summary>
    private void AnswerExchange(PetitionEventArgs e)
    {
        var s = _settings.Exchange;
        string who = e.InviterName.Length > 0 ? e.InviterName : $"#{e.InviterUniqueId}";
        string? why = ExchangeRefusal(s, IsBotting(), InTown());
        if (why is not null)
        {
            if (s.AutoAccept)
                _logger.LogInformation("Exchange request from {Who} left for you: {Why}", who, why);
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
            _client.AnswerExchangeRequest(true);
            _logger.LogInformation("Accepted the exchange request from {Who}", who);
        }, _cts.Token);
    }

    /// <summary>Why an exchange request is not accepted by itself (null: accept it).</summary>
    public static string? ExchangeRefusal(ExchangeSettings s, bool botting, bool inTown) =>
        !s.AutoAccept ? "auto accept is off"
            : botting && !s.AcceptWhileBotting ? "the bot is busy and accepting while botting is off"
            : s.TownOnly && !inTown ? "not in town (accept in town only)"
            : null;

    /// <summary>Accepts (true), refuses (false) or leaves to expire (null) after <paramref name="delay"/>.</summary>
    private void Answer(PetitionEventArgs e, bool? accept, TimeSpan delay, string what)
    {
        if (accept is not { } yes)
            return;
        string who = e.InviterName.Length > 0 ? e.InviterName : $"#{e.InviterUniqueId}";
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
            _client.RespondToPetition(yes, e.Type);
            _logger.LogInformation("{Answer} the {What} invitation from {Who}", yes ? "Accepted" : "Refused", what, who);
        }, _cts.Token);
    }

    // ---- party matching ---------------------------------------------------------------------

    private void OnMatchCreated(object? sender, PartyMatchEventArgs e) => _matchNumber = e.Number;

    private void OnMatchJoinRequested(object? sender, PartyMatchJoinRequestEventArgs e)
    {
        var party = _settings.Party;
        if (!party.AcceptMatchJoins)
            return;
        bool accept = party.AcceptAll || !party.AcceptFromList || InList(party.AcceptInviteList, e.PlayerName);
        _client.RespondToPartyMatchJoin(e.RequestId, e.JoinId, accept);
        _logger.LogInformation("{Answer} {Player} joining our party match", accept ? "Accepted" : "Refused", e.PlayerName);
    }

    private void OnMatchList(object? sender, PartyMatchListEventArgs e)
    {
        Matches = e.Matches;
        MatchesChanged?.Invoke(this, EventArgs.Empty);
        if (_client.World.Party is not null)
            return;
        var party = _settings.Party;
        var match = e.Matches.FirstOrDefault(m =>
            (party.JoinByName.Length > 0 && string.Equals(m.MasterName, party.JoinByName.Trim(), StringComparison.OrdinalIgnoreCase))
            || (party.JoinByTitle.Length > 0 && m.Title.Contains(party.JoinByTitle.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (match is not null && match.MemberCount < match.MemberMax)
        {
            _client.JoinPartyMatch(match.Number);
            _logger.LogInformation("Asking to join party match #{Number} ({Title}) of {Master}", match.Number, match.Title, match.MasterName);
        }
    }

    // ---- chat (academy password) ------------------------------------------------------------

    private void OnChat(object? sender, ChatEventArgs e)
    {
        var academy = _settings.Academy;
        if (e.Type != ChatType.Private || academy.Password.Length == 0 || e.Message.Trim() != academy.Password)
            return;
        if (_client.World.Players.TryGetValue(e.Sender, out var player))
        {
            _client.InviteToAcademy(player.UniqueId);
            _logger.LogInformation("{Player} sent the academy password: invited", e.Sender);
        }
        else
            _client.SendPrivateMessage(e.Sender, "Come near me and send the password again to be invited.");
    }

    // ---- the loop ---------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, token).ConfigureAwait(false);
                if (!_client.IsInGame || _client.World.Character is null)
                    continue;
                PartyStep();
                GuildStep();
                AcademyStep();
                TraceStep();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning("Social automation: {Message}", ex.Message);
            }
        }
    }

    private IEnumerable<Player> Nearby() => _client.World.Players.Values;

    private void PartyStep()
    {
        var settings = _settings.Party;
        var party = _client.World.Party;
        string me = _client.World.Character!.Name;

        // Leave a party whose master is not trusted.
        if (party?.Master is { } master && settings.LeaveIfLeaderNotInList && settings.LeaderList.Count > 0
            && !string.Equals(master.Name, me, StringComparison.OrdinalIgnoreCase) && !InList(settings.LeaderList, master.Name)
            && DateTime.UtcNow - _lastLeave > TimeSpan.FromSeconds(10))
        {
            _lastLeave = DateTime.UtcNow;
            _client.LeaveParty();
            _logger.LogInformation("Left the party: its master {Master} is not in the leader list", master.Name);
            return;
        }

        // Invite nearby players.
        if (settings.InvitePlayers && (settings.InviteAll || settings.InviteFromList) && party is not { IsFull: true })
        {
            bool canInvite = party is null || party.Master is not { } m || string.Equals(m.Name, me, StringComparison.OrdinalIgnoreCase)
                             || party.Setup.HasFlag(PartySetup.AnyoneCanInvite);
            if (canInvite)
            {
                foreach (var player in Nearby())
                {
                    if (party is not null && party.Members.Values.Any(x => string.Equals(x.Name, player.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!settings.InviteAll && !InList(settings.AcceptInviteList, player.Name))
                        continue;
                    if (!NotInvitedRecently("party:" + player.Name, InviteCooldown))
                        continue;
                    _client.InvitePlayerToParty(player.UniqueId, settings.Setup);
                    _logger.LogInformation("Invited {Player} to the party", player.Name);
                    break; // one at a time: the next tick invites the next one
                }
            }
        }

        // Party matching: keep ours listed, or look for one to join.
        var now = DateTime.UtcNow;
        if (settings.AutoFormMatch && (party is null || party.Master is { } owner && string.Equals(owner.Name, me, StringComparison.OrdinalIgnoreCase))
            && now - _matchFormed > MatchReform)
        {
            _matchFormed = now;
            if (_matchNumber != 0)
                _client.DeletePartyMatch(_matchNumber);
            _client.CreatePartyMatch(settings.MatchTitle, settings.Setup, PartyPurpose.Hunting, settings.MatchLevelMin, settings.MatchLevelMax);
        }
        if (party is null && (settings.JoinByName.Length > 0 || settings.JoinByTitle.Length > 0) && now - _matchSearched > MatchSearch)
        {
            _matchSearched = now;
            _client.RequestPartyMatchList(0);
        }
    }

    private void GuildStep()
    {
        var settings = _settings.Guild;
        if (!settings.AutoInvite)
            return;
        foreach (var player in Nearby())
        {
            bool hasGuild = player.GuildName.Length > 0 || player.GuildInfo is { GuildId: not 0 };
            if (hasGuild || EstimateLevel(player) <= settings.InviteAboveLevel || !NotInvitedRecently("guild:" + player.Name, GuildInviteCooldown))
                continue;
            _client.InviteToGuild(player.UniqueId);
            _logger.LogInformation("Invited {Player} to the guild", player.Name);
            break;
        }
    }

    private void AcademyStep()
    {
        var settings = _settings.Academy;
        if (!settings.InvitePlayers || !(settings.InviteAll || settings.InviteFromList))
            return;
        foreach (var player in Nearby())
        {
            if (!settings.InviteAll && !InList(settings.AcceptInviteList, player.Name))
                continue;
            if (!NotInvitedRecently("academy:" + player.Name, GuildInviteCooldown))
                continue;
            _client.InviteToAcademy(player.UniqueId);
            _logger.LogInformation("Invited {Player} to the academy", player.Name);
            break;
        }
    }

    private void TraceStep()
    {
        if (!_tracing)
            return;
        var trace = _settings.Trace;
        if (!_client.World.Players.TryGetValue(trace.Player, out var target))
            return; // not near (yet)
        var now = DateTime.UtcNow;
        if (trace.GameTrace)
        {
            if (now - _lastTrace > TimeSpan.FromSeconds(10))
            {
                _lastTrace = now;
                _client.Trace(target.UniqueId);
            }
            return;
        }
        var character = _client.World.Character!;
        var me = (_client.World.Mover ?? character).EstimatePosition();
        var there = target.EstimatePosition();
        if (me.InDungeon != there.InDungeon || me.DistanceTo(there) <= Math.Max(2, trace.Distance) || now - _lastTrace < TimeSpan.FromMilliseconds(900))
            return;
        _lastTrace = now;
        if (character.IsRiding)
            _client.MovePetTo(character.RidingUniqueId, there);
        else
            _client.MoveTo(there);
    }

    public void Dispose()
    {
        _client.PetitionReceived -= OnPetition;
        _client.PartyMatchCreated -= OnMatchCreated;
        _client.PartyMatchJoinRequested -= OnMatchJoinRequested;
        _client.PartyMatchListReceived -= OnMatchList;
        _client.ChatReceived -= OnChat;
        _cts.Cancel();
        _cts.Dispose();
    }
}
