using System.Globalization;
using Microsoft.Extensions.Logging;
using NexBot.App.Theme;
using NexBot.App.Views;
using NexBot.Automation;
using NexBot.Game;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Loader;
using NexBot.Map;
using NexBot.Media;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.App;

public partial class MainForm : Form
{
    private readonly BotSettings _settings;
    private readonly UiLogger _logger;
    private readonly System.Windows.Forms.Timer _mapTimer = new() { Interval = 250 };
    private ClientInfo? _clientInfo;
    private MediaImages? _tiles;
    private readonly Lock _navLock = new();
    private NexBot.Navigation.NavMap? _nav;
    private string? _navPath;
    private BotSession? _session;
    private bool _busy;
    private int _tick;

    public MainForm()
    {
        BuildUi();
        _logger = new UiLogger(txtLog, txtActivity);
        pageScript.Logger = _logger;
        pageTrade.Logger = _logger;
        _settings = BotSettings.Load();
        _settings.EnsureTrainingAreas();
        WireEvents();
        LoadSettingsIntoForm();
        UpdateDatabaseLabel();
        UpdateButtons();
        RefreshStatus();
        _mapTimer.Tick += (_, _) => OnTimer();
        _mapTimer.Start();
        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
    }

    private void WireEvents()
    {
        btnBrowse.Click += BtnBrowse_Click;
        btnReadConfig.Click += async (_, _) => await ReadServerConfigAsync();
        btnBuildDb.Click += BtnBuildDb_Click;
        cmbDivision.SelectedIndexChanged += CmbDivision_SelectedIndexChanged;
        btnStartClient.Click += async (_, _) => await StartAsync(withClient: true);
        btnStartClientless.Click += async (_, _) => await StartAsync(withClient: false);
        btnGoClientless.Click += BtnGoClientless_Click;
        btnStop.Click += async (_, _) => await StopAsync();
        chkShowClient.CheckedChanged += ChkShowClient_CheckedChanged;
        cmbLogFilter.SelectedIndex = Enum.TryParse<LogFilter>(_settings.LogFilter, out var shown) ? (int)shown : 0;
        _logger.Filter = (LogFilter)Math.Max(0, cmbLogFilter.SelectedIndex);
        cmbLogFilter.SelectedIndexChanged += (_, _) =>
        {
            var filter = (LogFilter)Math.Max(0, cmbLogFilter.SelectedIndex);
            _logger.Filter = filter;
            _settings.LogFilter = filter.ToString();
            OnTradeChanged();
        };
        txtLogSearch.Box.TextChanged += (_, _) => _logger.Search = txtLogSearch.Text;
        btnLogSave.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt", FileName = $"NexBot-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(dialog.FileName, _logger.ShownText());
                _logger.LogInformation("Log saved to {File}", dialog.FileName);
            }
        };

        mapCanvas.WalkRequested = (x, y) => _session?.Game is { } game ? MapSnapshot.WalkTo(game, x, y) : "The bot is not running.";
        mapCanvas.Message += message => lblMapMessage.Text = message;
        mapCanvas.TrainingArea = _settings.Training;
        mapCanvas.TrainingAreaChanged += OnTrainingAreaChanged;
        mapCanvas.RightClicked += ShowMapMenu;
        btnRecordPolygon.Click += (_, _) =>
        {
            mapCanvas.StartPolygon();
            btnRecordPolygon.Enabled = false;
            btnStopPolygon.Enabled = true;
        };
        btnStopPolygon.Click += (_, _) =>
        {
            mapCanvas.StopPolygon();
            btnRecordPolygon.Enabled = true;
            btnStopPolygon.Enabled = false;
        };
        btnClearPolygon.Click += (_, _) =>
        {
            mapCanvas.ClearPolygon();
            btnRecordPolygon.Enabled = true;
            btnStopPolygon.Enabled = false;
        };
        txtAreaRadius.Text = _settings.Training.Radius.ToString(CultureInfo.CurrentCulture);
        txtAreaRadius.Box.TextChanged += (_, _) =>
        {
            if (int.TryParse(txtAreaRadius.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int r) && r is >= 5 and <= 1000)
            {
                _settings.Training.Radius = r;
                mapCanvas.Invalidate();
                OnTradeChanged();
            }
        };
        pageSound.Bind(_settings.Sound, OnTradeChanged);
        pageStall.Bind(_settings.Stall, OnTradeChanged);
        pageAlchemy.Bind(_settings.Alchemy, OnTradeChanged);
        pageConditions.Bind(_settings.Conditions, OnTradeChanged);
        pageProtection.Bind(_settings.Protection, OnTradeChanged);
        pageTown.Bind(_settings.Town, OnTradeChanged);
        nearby.EntityActivated += entity => mapCanvas.Walk((entity.X, entity.Y));
        pageCharacter.BindAuto(_settings.AutoLevel, OnAutoLevelChanged);
        pageTrade.Bind(_settings.Trade, OnTradeChanged);
        pageParty.Bind(_settings.Social.Party, OnTradeChanged);
        pageGuild.Bind(_settings.Social.Guild, OnTradeChanged);
        pageAcademy.Bind(_settings.Social.Academy, OnTradeChanged);
        pagePlayers.Bind(_settings.Social.Trace, OnTradeChanged);
        pagePet.Bind(_settings.Pet, OnTradeChanged);
        pagePick.Bind(_settings.PickFilter, OnTradeChanged);
        pageAttack.Bind(_settings.Attack, _settings.Training, OnTradeChanged, StartBot, OnTrainingAreaChanged);
        pageTraining.Bind(_settings, OnTradeChanged);
        pageInventory.BindExchange(_settings.Social.Exchange, OnTradeChanged);
        pageScript.Trade = _settings.Trade;
    }

    /// <summary>The character died / Protection sent it to town since the last reverse return (Training Area, Settings tab).</summary>
    private volatile bool _diedLast, _returnedByProtection;

    /// <summary>The enabled training area was changed (map, Train here, polygon): saved and the list entry kept the same.</summary>
    private void OnTrainingAreaChanged()
    {
        _settings.SaveTrainingToList();
        mapCanvas.Invalidate();
        pageTraining.RefreshAreas();
        OnTradeChanged();
    }

    /// <summary>The Attack page's Start bot: hunts, and runs the town loop first when the character is in town.</summary>
    private void StartBot()
    {
        if (_hunt is not { } hunt)
        {
            _logger.LogWarning("Attack: start the bot (Server page) first");
            return;
        }
        hunt.Start();
        if (_protection?.InTown() == true && _town is { IsRunning: false } town)
        {
            _logger.LogInformation("Attack: in town: the town loop runs first, then walks to the training area");
            town.Start();
        }
    }

    /// <summary>
    /// After town: a reverse return scroll back to where the character died, or to where Protection returned it
    /// (Training Area, Settings tab). True when one was used.
    /// </summary>
    private bool UseReverseReturn(SroClient game)
    {
        var setup = _settings.TrainingSetup;
        byte mode = _diedLast && setup.ReverseReturnOnDeath ? (byte)3 : _returnedByProtection && setup.ReverseReturnAfterReturn ? (byte)2 : (byte)0;
        _diedLast = _returnedByProtection = false;
        if (mode == 0 || game.World.Character is not { } c)
            return false;
        for (int i = NexBot.Game.Entities.Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is { } item && item.ServerName.Contains("REVERSE_RETURN", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Town: reverse return scroll to the last {Where}", mode == 3 ? "death point" : "return point");
                game.Send(ClientPackets.UseReverseReturn(item, (byte)i, mode));
                return true;
            }
        }
        _logger.LogInformation("Town: no reverse return scroll: walking to training");
        return false;
    }

    /// <summary>Party / guild / academy / trace automation for the running session (null when stopped).</summary>
    private SocialManager? _social;

    /// <summary>Sound alerts and map statistics for the running session (null when stopped).</summary>
    private SoundAlerts? _alerts;
    private SessionStats? _stats;

    /// <summary>Stalling for the running session (null when stopped).</summary>
    private StallManager? _stall;

    /// <summary>Alchemy queue and If / Then rules for the running session (null when stopped).</summary>
    private AlchemyRunner? _alchemy;
    private ConditionEngine? _conditions;

    /// <summary>Potions, return, stats and disconnect rules for the running session (null when stopped).</summary>
    private ProtectionManager? _protection;
    private PetManager? _pets;
    private PickManager? _pick;
    private HuntManager? _hunt;
    private GameMasterConsole? _gm;

    /// <summary>The town loop for the running session (null when stopped).</summary>
    private TownRunner? _town;

    /// <summary>Game data for pages that browse it before login (the running session's, else the database file).</summary>
    private NexBot.Data.IGameData? _browseData;

    private NexBot.Data.IGameData? BrowseData() => _session?.Game?.Data ?? (_browseData ??= OpenGameData());

    /// <summary>A teleport started from the map menu.</summary>
    private AutoWalker? _mapWalker;

    /// <summary>AutoTrade or social choices changed: save them (the automation reads the same objects).</summary>
    private void OnTradeChanged()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save the settings: {Message}", ex.Message);
        }
    }

    /// <summary>The auto-level choices changed on the Character page: save them and hand them to the trainer.</summary>
    private void OnAutoLevelChanged()
    {
        if (_session?.Trainer is { } trainer)
            trainer.Options = _settings.AutoLevel.ToOptions();
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save the settings: {Message}", ex.Message);
        }
    }

    #region Settings

    private void LoadSettingsIntoForm()
    {
        txtClientPath.Text = _settings.ClientPath;
        txtUsername.Text = _settings.Username;
        txtPassword.Text = _settings.Password;
        cmbShard.Text = _settings.ShardName;
        cmbCharacter.Text = _settings.CharacterName;
        txtCaptcha.Text = _settings.Captcha;
        chkAutoLogin.Checked = _settings.AutoLogin;
        chkAutoSelect.Checked = _settings.AutoSelectCharacter;
        chkClientlessOnClose.Checked = _settings.ClientlessWhenClientCloses;
    }

    private void SaveFormIntoSettings()
    {
        if (_noSave)
            return;
        _settings.ClientPath = txtClientPath.Text.Trim();
        _settings.Username = txtUsername.Text.Trim();
        _settings.Password = txtPassword.Text;
        _settings.ShardName = cmbShard.Text.Trim();
        _settings.CharacterName = cmbCharacter.Text.Trim();
        _settings.Captcha = txtCaptcha.Text;
        _settings.AutoLogin = chkAutoLogin.Checked;
        _settings.AutoSelectCharacter = chkAutoSelect.Checked;
        _settings.ClientlessWhenClientCloses = chkClientlessOnClose.Checked;
        if (cmbDivision.SelectedIndex >= 0)
            _settings.DivisionIndex = cmbDivision.SelectedIndex;
        if (cmbGateway.SelectedIndex >= 0)
            _settings.GatewayIndex = cmbGateway.SelectedIndex;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save settings: {Message}", ex.Message);
        }
    }

    #endregion

    #region Form events

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        ActiveControl = btnStartClient; // not the path box (its text would start selected)
        _logger.LogInformation("NexBot {Version} ({Arch})", Application.ProductVersion,
            Environment.Is64BitProcess ? "x64" : "x86");
        if (Directory.Exists(_settings.ClientPath))
            await ReadServerConfigAsync();
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveFormIntoSettings();
        if (_session is null)
        {
            _mapTimer.Stop();
            mapCanvas.Tiles = null;
            pageScript.Tiles = null;
            icons.Dispose();
            _tiles?.Dispose();
            lock (_navLock)
                _nav?.Dispose();
            return;
        }

        // Stop the session first (it kills the game client it started), then close for real.
        e.Cancel = true;
        var session = _session;
        _session = null;
        await session.DisposeAsync();
        Close();
    }

    private void BtnBrowse_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Silkroad client folder (with sro_client.exe and Media.pk2)",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(txtClientPath.Text) ? txtClientPath.Text : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            txtClientPath.Text = dialog.SelectedPath;
    }

    private void CmbDivision_SelectedIndexChanged(object? sender, EventArgs e)
    {
        int division = cmbDivision.SelectedIndex;
        if (_clientInfo is null || division < 0 || division >= _clientInfo.Divisions.Count)
        {
            cmbGateway.SetItems([]);
            return;
        }
        cmbGateway.SetItems(_clientInfo.Divisions[division].Gateways);
        if (cmbGateway.Items.Count > 0)
            cmbGateway.SelectedIndex = Math.Clamp(_settings.GatewayIndex, 0, cmbGateway.Items.Count - 1);
    }

    private async void BtnBuildDb_Click(object? sender, EventArgs e)
    {
        string clientPath = txtClientPath.Text.Trim();
        string dbPath = _settings.ResolveDatabasePath();
        string key = _settings.Pk2Key;

        SetBusy(true);
        try
        {
            _logger.LogInformation("Building the game database from Media.pk2, this takes a moment...");
            var result = await Task.Run(() => MediaExtractor.Extract(clientPath, dbPath, key, _logger));
            _logger.LogInformation("Database ready: {Rows}", string.Join(", ", result.RowCounts.Select(r => $"{r.Key} {r.Value:N0}")));
        }
        catch (Exception ex)
        {
            _logger.LogError("Building the database failed: {Message}", ex.Message);
        }
        finally
        {
            SetBusy(false);
            UpdateDatabaseLabel();
        }
    }

    private void BtnGoClientless_Click(object? sender, EventArgs e)
    {
        try
        {
            _session?.GoClientless();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("{Message}", ex.Message);
        }
        UpdateButtons();
        RefreshStatus();
    }

    private void ChkShowClient_CheckedChanged(object? sender, EventArgs e)
    {
        if (_session?.ClientProcess is { } process)
            ClientLauncher.SetVisible(process, chkShowClient.Checked);
    }

    #endregion

    #region Server config

    private async Task ReadServerConfigAsync()
    {
        string clientPath = txtClientPath.Text.Trim();
        if (!Directory.Exists(clientPath))
        {
            _logger.LogWarning("Select the Silkroad client folder first");
            return;
        }

        SetBusy(true);
        try
        {
            string key = _settings.Pk2Key;
            var info = await Task.Run(() => MediaExtractor.ReadClientInfo(clientPath, key));
            ShowClientInfo(info);
            _settings.ClientPath = clientPath;
            _logger.LogInformation("Server config: version {Version}, locale {Locale}, gateway port {Port}, {Divisions} division(s): {Hosts}",
                info.Version, info.Locale, info.GatewayPort, info.Divisions.Count, string.Join(", ", info.Gateways));

            if (!File.Exists(Path.Combine(clientPath, _settings.Executable)))
                _logger.LogWarning("{Exe} was not found in the client folder", _settings.Executable);
            OpenMinimap(clientPath, key);
        }
        catch (Exception ex)
        {
            _clientInfo = null;
            _logger.LogError("Could not read the server config from Media.pk2: {Message}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// The client's map data (Data.pk2) for routes the bot finds by itself; opened on first use and again when the
    /// client folder changes; null when it cannot be opened.
    /// </summary>
    private NexBot.Navigation.NavMap? Navigation()
    {
        lock (_navLock)
        {
            string path = _settings.ClientPath;
            if (_navPath == path)
                return _nav;
            _nav?.Dispose();
            _nav = null;
            _navPath = path;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                string key = _settings.Pk2Key;
                _nav = NexBot.Navigation.NavMap.Open(path, string.IsNullOrWhiteSpace(key) ? null : key, _logger);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The map data (Data.pk2) could not be opened, routes of its own are off: {Message}", ex.Message);
            }
            return _nav;
        }
    }

    private void OpenMinimap(string clientPath, string key)
    {
        try
        {
            var tiles = new MediaImages(clientPath, string.IsNullOrWhiteSpace(key) ? null : key);
            mapCanvas.Tiles = tiles;
            pageScript.Tiles = tiles;
            icons.Images = tiles;
            _tiles?.Dispose();
            _tiles = tiles;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The minimap images could not be opened: {Message}", ex.Message);
        }
    }

    private void ShowClientInfo(ClientInfo info)
    {
        _clientInfo = info;
        var server = _settings.ToServer(info);
        lblVersion.Text = server.Version.ToString(CultureInfo.InvariantCulture);
        lblLocale.Text = server.Locale.ToString(CultureInfo.InvariantCulture);
        lblPort.Text = server.GatewayPort.ToString(CultureInfo.InvariantCulture);

        cmbDivision.SetItems(info.Divisions.Select(d => string.IsNullOrEmpty(d.Name) ? "(unnamed)" : d.Name));
        if (cmbDivision.Items.Count > 0)
            cmbDivision.SelectedIndex = Math.Clamp(_settings.DivisionIndex, 0, cmbDivision.Items.Count - 1);
    }

    private void UpdateDatabaseLabel()
    {
        string path = _settings.ResolveDatabasePath();
        if (!File.Exists(path))
        {
            lblDatabase.Text = "not built yet";
            lblDatabase.ForeColor = SroTheme.Warn;
            return;
        }

        try
        {
            _ = new SqliteGameData(path); // validates the tables
            lblDatabase.Text = string.Create(CultureInfo.CurrentCulture, $"ready ({File.GetLastWriteTime(path):g})");
            lblDatabase.ForeColor = SroTheme.Good;
        }
        catch (Exception ex)
        {
            lblDatabase.Text = "invalid, rebuild it";
            lblDatabase.ForeColor = SroTheme.Bad;
            _logger.LogWarning("Game database {Path} is not usable: {Message}", path, ex.Message);
        }
    }

    /// <summary>The game data for the world tracking (map), or null when the database is missing.</summary>
    private SqliteGameData? OpenGameData()
    {
        string path = _settings.ResolveDatabasePath();
        if (!File.Exists(path))
        {
            _logger.LogWarning("No game database yet: build it to see the map and character status");
            return null;
        }
        try
        {
            return new SqliteGameData(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Game database {Path} is not usable: {Message}", path, ex.Message);
            return null;
        }
    }

    #endregion

    #region Bot

    private async Task StartAsync(bool withClient)
    {
        if (_session is not null)
            return;

        SaveFormIntoSettings();
        if (_clientInfo is null)
            await ReadServerConfigAsync();
        if (_clientInfo is null)
            return;

        var account = _settings.ToAccount();
        if (!withClient && !account.HasCredentials)
        {
            _logger.LogWarning("Clientless mode needs a username and password");
            return;
        }
        if (withClient && account.AutoLogin && !account.HasCredentials)
            _logger.LogInformation("No account set: the client opens normally and you log in by hand");

        SroEncoding.UseCodePage(_settings.CodePage);
        var server = _settings.ToServer(_clientInfo);
        var session = new BotSession(server, account, _logger, OpenGameData());
        session.StateChanged += state => RunOnUi(() => OnSessionStateChanged(session, state));
        session.ShardsReceived += shards => RunOnUi(() => cmbShard.SetItems(shards.Select(s => s.Name)));
        session.CharactersReceived += chars => RunOnUi(() => cmbCharacter.SetItems(chars.Select(c => c.Name)));
        session.ClientExited += () => RunOnUi(() => OnClientExited(session));
        if (session.Trainer is { } trainer)
        {
            trainer.Options = _settings.AutoLevel.ToOptions();
            trainer.StatusChanged += text => RunOnUi(() => pageCharacter.ShowAutoStatus(text));
        }
        _session = session;
        _social?.Dispose();
        _social = session.Game is { } game ? new SocialManager(game, _settings.Social, _logger)
        {
            IsBotting = () => pageScript.IsWalking || pageTrade.IsRunning || _town?.IsRunning == true || _hunt?.IsRunning == true,
            InTown = () => _protection?.InTown() == true,
        } : null;
        DisposeMapServices();
        if (session.Game is { } g)
        {
            _stats = new SessionStats(g);
            _alerts = new SoundAlerts(g, _settings.Sound, _logger);
            _alerts.Alert += (_, a) => SoundPlayback.Play(a.File);
            _stall = new StallManager(g, _settings.Stall, _logger);
            g.StallSold += (_, sale) => BeginInvoke(() => pageStall.AddSale(sale));
            g.StallLayoutLearned += (_, l) => BeginInvoke(() =>
            {
                _settings.Stall.AddTrailerHex = l.TrailerHex;
                _settings.Stall.AddTrailerLearned = true;
                OnTradeChanged();
            });
            _alchemy = new AlchemyRunner(g, _settings.Alchemy, _logger);
            g.AlchemyLearned += (_, a) => BeginInvoke(() => OnAlchemyLearned(a));
            _conditions = new ConditionEngine(g, _settings.Conditions, _logger);
            _conditions.SoundRequested += SoundPlayback.Play;
            _conditions.StopRequested += () => BeginInvoke(() =>
            {
                _town?.Stop();
                _hunt?.Stop("a condition asked to stop");
                pageScript.StopAll();
                pageTrade.StopAll();
                _alchemy?.Stop();
            });
            _conditions.DisconnectRequested += () => BeginInvoke(async () => await StopAsync());
            _protection = new ProtectionManager(g, _settings.Protection, _settings.Training,
                () => pageScript.IsWalking || pageTrade.IsRunning || _hunt?.IsRunning == true, _logger) { IsWalkingScript = () => pageScript.IsWalking };
            _protection.StopRequested += reason => BeginInvoke(() =>
            {
                _logger.LogInformation("Protection: stopping ({Reason})", reason);
                // A return to town only holds the hunting (the town loop and the training script follow); other stops end it.
                if (reason.StartsWith("returning to town", StringComparison.Ordinal))
                    _hunt?.Hold(TimeSpan.FromSeconds(20));
                else
                    _hunt?.Stop(reason);
                pageScript.StopAll();
                pageTrade.StopAll();
                _alchemy?.Stop();
            });
            _protection.DisconnectRequested += _ => BeginInvoke(async () => await StopAsync());
            var protection = _protection;
            _pets = new PetManager(g, _settings.Pet, _settings.Training, _logger) { InTown = protection.InTown };
            _pets.ReturnRequested += reason =>
            {
                _logger.LogInformation("Pet: asking to return to town ({Reason})", reason);
                protection.RequestReturn(reason);
            };
            _town = new TownRunner(g, _settings.Town, _logger, Navigation)
            {
                Filter = _settings.PickFilter, Training = _settings.Training, Setup = _settings.TrainingSetup,
            };
            _town.ReverseReturn = () => UseReverseReturn(g);
            _town.ReturnRequested += reason => protection.RequestReturn(reason);
            var townForPick = _town;
            _pick = new PickManager(g, _settings.PickFilter, _logger)
            {
                CharacterMayPick = () => !pageScript.IsWalking && !pageTrade.IsRunning && !townForPick.IsRunning && (_hunt?.MayPick ?? true),
                Range = () => _settings.Training.PickRadius,
                Map = Navigation,
                CollisionDetection = () => _settings.TrainingSetup.CollisionDetection,
                NavigateToDrops = () => _settings.TrainingSetup.NavigateToDrops,
            };
            var pick = _pick;
            _diedLast = _returnedByProtection = false;
            g.CharacterDied += (_, _) => _diedLast = true;
            protection.ReturnedToTown += () => _returnedByProtection = true;
            _hunt = new HuntManager(g, _settings.Attack, _settings.Protection, _settings.Training, _settings.TrainingSetup, _logger)
            {
                Paused = () => pageScript.IsWalking || pageTrade.IsRunning || townForPick.IsRunning,
                PickWaiting = () => pick.CharacterWork,
                PickItemsFirst = () => _settings.PickFilter.PickItemsFirst,
                Map = Navigation,
                WalkToArea = () =>
                {
                    if (protection.InTown() && !townForPick.IsRunning)
                    {
                        townForPick.Start();
                        return true;
                    }
                    return townForPick.WalkToArea();
                },
                ReturnToTown = reason => protection.RequestReturn(reason),
                SwitchArea = name =>
                {
                    bool ok = _settings.EnableTrainingArea(name);
                    if (ok)
                        BeginInvoke(() =>
                        {
                            OnTradeChanged();
                            mapCanvas.Invalidate();
                            pageTraining.RefreshAreas();
                        });
                    return ok;
                },
            };
            _hunt.DevilSkillLearned += () => BeginInvoke(OnTradeChanged);
            _gm = new GameMasterConsole(g, _logger);
            g.CharacterLoaded += (_, e) => BeginInvoke(() =>
            {
                sideNav.SetShown(pageGm, e.Character.IsGameMaster);
                if (e.Character.IsGameMaster)
                    _logger.LogInformation("GM: {Name} is a game master: the GM page is in the menu", e.Character.Name);
            });
            var town = _town;
            town.StatusChanged += (_, _) => BeginInvoke(() => pageTown.ShowStatus(town.Status, town.IsRunning));
            town.StopRequested += reason => BeginInvoke(() =>
            {
                _logger.LogInformation("Town: stopping ({Reason})", reason);
                pageScript.StopAll();
                pageTrade.StopAll();
            });
            _protection.ReturnedToTown += () =>
            {
                if (_settings.Town.RunAfterReturn)
                    town.Start(afterTeleport: true);
            };
            // A return scroll (or a revive in town) from the game itself starts the town loop too, unless a script or
            // the trade route is walking (it uses return scrolls of its own).
            g.ReturnedToTownByPlayer += (_, _) => BeginInvoke(() =>
            {
                if (!_settings.Town.RunAfterReturn || town.IsRunning || pageScript.IsWalking || pageTrade.IsRunning)
                    return;
                _logger.LogInformation("Town: return scroll used in the game: the town loop starts after the teleport");
                town.Start(afterTeleport: true);
            });
        }
        UpdateButtons();
        RefreshStatus();

        try
        {
            if (withClient)
            {
                await session.StartWithClientAsync(_settings.ClientPath, _settings.Executable,
                    (byte)Math.Max(0, cmbDivision.SelectedIndex), (byte)Math.Max(0, cmbGateway.SelectedIndex), _settings.ProxyPort);
                lblProxy.Text = $"127.0.0.1:{session.ProxyPort}";
                _ = SetClientTitleWhenReadyAsync(session);
            }
            else
            {
                await session.StartClientlessAsync();
            }
        }
        catch (Exception ex) when (ex is ClientLaunchException or IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            _logger.LogError("Start failed: {Message}", ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})");
            await StopAsync();
        }
        RefreshStatus();
    }

    private async Task StopAsync()
    {
        var session = _session;
        if (session is null)
            return;
        _session = null;
        _social?.Dispose();
        _social = null;
        DisposeMapServices();
        pageScript.StopAll();
        pageTrade.StopAll();
        await session.DisposeAsync();
        lblProxy.Text = "-";
        _logger.LogInformation("Stopped");
        UpdateButtons();
        RefreshStatus();
        RefreshMap();
    }

    private void OnSessionStateChanged(BotSession session, BotState state)
    {
        if (!ReferenceEquals(session, _session))
            return;
        UpdateButtons();
        RefreshStatus();
    }

    private async void OnClientExited(BotSession session)
    {
        if (!ReferenceEquals(session, _session))
            return;
        // Clientless: the character stays online. Otherwise the session has nothing left to do.
        await Task.Delay(500);
        if (ReferenceEquals(session, _session) && !session.IsClientless)
            await StopAsync();
        else
            UpdateButtons();
        RefreshStatus();
    }

    private async Task SetClientTitleWhenReadyAsync(BotSession session)
    {
        for (int i = 0; i < 60 && ReferenceEquals(session, _session); i++)
        {
            await Task.Delay(1000);
            if (session.ClientProcess is not { } process)
                return;
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                ClientLauncher.SetTitle(process, $"NexBot - {(string.IsNullOrEmpty(_settings.Username) ? "Silkroad" : _settings.Username)}");
                if (!chkShowClient.Checked)
                    ClientLauncher.SetVisible(process, false);
                return;
            }
        }
    }

    #endregion

    #region Status + map refresh

    private string StateText()
    {
        if (_session is not { } session)
            return "Idle";
        return session.IsClientless ? $"{session.State} · clientless" : session.State.ToString();
    }

    private Color StateColor() => _session?.State switch
    {
        null or BotState.Idle => SroTheme.Muted,
        BotState.InGame => SroTheme.Good,
        _ => SroTheme.Warn,
    };

    private void RefreshStatus()
    {
        string state = StateText();
        lblState.Text = state;
        lblState.ForeColor = StateColor();
        header.SetState(state, StateColor());

        var snapshot = Snapshot();
        statusBar.SetSegments(
            ("State  ", state, StateColor()),
            ("Mode  ", _session is null ? "-" : _session.IsClientless ? "Clientless" : "Client", SroTheme.Value),
            ("Proxy  ", _session is { ProxyPort: > 0 } s ? $"127.0.0.1:{s.ProxyPort}" : "-", SroTheme.Value),
            ("Character  ", snapshot is null ? "-" : $"{snapshot.Status.Name} (Lv. {snapshot.Status.Level})", SroTheme.Value),
            ("Server  ", _clientInfo is null ? "not read" : $"v{_clientInfo.Version} · locale {_clientInfo.Locale}", SroTheme.Value));
    }

    private MapSnapshot? Snapshot()
    {
        if (_session?.Game is not { } game)
            return null;
        try
        {
            return MapSnapshot.Take(game);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            // the world changed under us; next tick
            return null;
        }
    }

    private CharacterSheet? Sheet()
    {
        if (_session?.Game is not { } game)
            return null;
        try
        {
            return CharacterSheet.Take(game);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            return null; // the character changed under us; next tick
        }
    }

    private SkillWindow? Skills()
    {
        if (_session?.Game is not { } game)
            return null;
        try
        {
            return SkillWindow.Take(game);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            return null; // the character changed under us; next tick
        }
    }

    private void RefreshMap() => RefreshPages(force: true);

    /// <summary>The game client did an alchemy: keep its type byte so the bot sends the same.</summary>
    private void OnAlchemyLearned(AlchemyLearnedEventArgs a)
    {
        var s = _settings.Alchemy;
        if (!a.Stone)
        {
            s.ElixirType = a.Type;
        }
        else
        {
            // Which stone was it? Look at the stone's slot (the 2nd slot) before the server uses it up.
            byte[] bytes = Convert.FromHexString(a.Hex);
            var stone = bytes.Length > 4 ? _session?.Game?.World.Character?.Inventory[bytes[4]] : null;
            if (stone is not null && AlchemyCatalog.IsAttributeStone(stone))
                s.AttributeStoneType = a.Type;
            else
                s.MagicStoneType = a.Type;
        }
        s.LearnedFrom = a.Hex;
        _logger.LogInformation("Alchemy: learned the {Kind} packet type {Type} from the game client ({Hex})", a.Stone ? "stone" : "elixir", a.Type, a.Hex);
        OnTradeChanged();
    }

    private void DisposeMapServices()
    {
        _mapWalker?.Dispose();
        _mapWalker = null;
        _alerts?.Dispose();
        _alerts = null;
        _stats?.Dispose();
        _stats = null;
        mapCanvas.Stats = null;
        _stall?.Dispose();
        _stall = null;
        _alchemy?.Stop();
        _alchemy = null;
        _conditions?.Dispose();
        _conditions = null;
        _protection?.Dispose();
        _protection = null;
        _pets?.Dispose();
        _pets = null;
        _hunt?.Dispose();
        _hunt = null;
        _pick?.Dispose();
        _pick = null;
        _town?.Dispose();
        _town = null;
        _gm?.Dispose();
        _gm = null;
        if (IsHandleCreated)
            sideNav.SetShown(pageGm, false);
    }

    /// <summary>The map's right-click menu (phBot's map menu: training area, teleporters ...).</summary>
    private void ShowMapMenu(Point at, double x, double y)
    {
        var menu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var game = _session?.Game;
        var snap = Snapshot();
        void Add(ToolStripItemCollection items, string text, Action click, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled, ForeColor = enabled ? SroTheme.Text : SroTheme.Muted };
            item.Click += (_, _) => click();
            items.Add(item);
        }

        Add(menu.Items, "Walk here", () => mapCanvas.Walk((x, y)), snap is not null);
        Add(menu.Items, "Center on me", () => mapCanvas.Follow = true);
        menu.Items.Add(new ToolStripSeparator());

        var area = _settings.Training;
        Add(menu.Items, string.Create(CultureInfo.CurrentCulture, $"Set training area here (radius {area.Radius})"), () =>
        {
            area.X = Math.Round(x, 1);
            area.Y = Math.Round(y, 1);
            area.Region = snap is { Me.Dungeon: true } ? snap.Me.Region : (ushort)0;
            area.UsePolygon = false;
            mapCanvas.Invalidate();
            OnTrainingAreaChanged();
            lblMapMessage.Text = string.Create(CultureInfo.CurrentCulture, $"Training area set at X {x:0}, Y {y:0} (radius {area.Radius}).");
        });
        var usePolygon = new ToolStripMenuItem("Use the polygon as training area")
        {
            Checked = area.UsePolygon, Enabled = area.HasPolygon, ForeColor = area.HasPolygon ? SroTheme.Text : SroTheme.Muted,
        };
        usePolygon.Click += (_, _) =>
        {
            area.UsePolygon = !area.UsePolygon;
            mapCanvas.Invalidate();
            OnTradeChanged();
        };
        menu.Items.Add(usePolygon);
        Add(menu.Items, "Clear the training area", () =>
        {
            area.X = area.Y = 0;
            area.Polygon = [];
            area.UsePolygon = false;
            mapCanvas.Invalidate();
            OnTradeChanged();
        }, area.IsSet || area.HasPolygon);

        // Teleporters: the one under the mouse, else the nearest to the character.
        if (game is not null && snap is not null)
        {
            var teleporter = NearestTeleporter(game, x, y, 25) ?? NearestTeleporter(game, snap.Me.X, snap.Me.Y, 60);
            menu.Items.Add(new ToolStripSeparator());
            var sub = new ToolStripMenuItem(teleporter is null ? "Teleporter (none near)" : $"Teleporter: {teleporter.Name}")
            {
                ForeColor = teleporter is null ? SroTheme.Muted : SroTheme.Text, Enabled = teleporter is not null,
            };
            if (teleporter is not null)
            {
                foreach (var link in teleporter.Links)
                {
                    var target = link;
                    string where = teleporter.DestinationName(link.DestinationId);
                    Add(sub.DropDownItems, $"Teleport to {(where.Length > 0 ? where : "#" + link.DestinationId)}", () => TeleportFromMap(game, teleporter, target));
                }
                if (teleporter.Entity is Game.Entities.Npc and not Game.Entities.Mob)
                {
                    sub.DropDownItems.Add(new ToolStripSeparator());
                    Add(sub.DropDownItems, "Designate as recall point", () => _ = DesignateRecallAsync(game, teleporter));
                }
                foreach (ToolStripItem item in sub.DropDownItems)
                    item.BackColor = SroTheme.Bg;
            }
            menu.Items.Add(sub);
        }
        menu.Show(mapCanvas, at);
    }

    private static TeleporterInfo? NearestTeleporter(SroClient game, double x, double y, double radius) =>
        game.World.Teleports.Values.Cast<Game.Entities.Entity>()
            .Concat(game.World.Npcs.Values)
            .Select(e => (Entity: e, Distance: e.Position.DistanceTo(x, y)))
            .Where(t => t.Distance <= radius)
            .OrderBy(t => t.Distance)
            .Select(t => Teleporters.Find(game, t.Entity.UniqueId))
            .FirstOrDefault(t => t is { Links.Count: > 0 });

    /// <summary>Walks to the teleporter (when needed) and teleports, like a TELEPORT script line.</summary>
    private void TeleportFromMap(SroClient game, TeleporterInfo teleporter, Data.TeleportLinkInfo link)
    {
        _mapWalker?.Dispose();
        string label = teleporter.DestinationName(link.DestinationId);
        var script = WalkScript.Parse([$"TELEPORT,{teleporter.Entity.ServerName},{link.DestinationId},{label}"], "map teleport");
        var walker = new AutoWalker(game, script, new AutoWalkOptions { Loop = false, Trade = _settings.Trade }, _logger);
        walker.StatusChanged += (_, _) => BeginInvoke(() => lblMapMessage.Text = walker.Status);
        _mapWalker = walker;
        walker.Start();
        lblMapMessage.Text = $"Teleporting to {label}…";
    }

    /// <summary>Walks next to a teleporter NPC, selects it and makes it the return point (0x7059).</summary>
    private async Task DesignateRecallAsync(SroClient game, TeleporterInfo teleporter)
    {
        var npc = teleporter.Entity;
        if (game.World.Character is not { } c)
            return;
        if (c.EstimatePosition().DistanceTo(npc.Position) > 5)
        {
            if (MapSnapshot.WalkTo(game, npc.Position.PosX, npc.Position.PosY) is { } error)
            {
                lblMapMessage.Text = error;
                return;
            }
            lblMapMessage.Text = $"Walking to {teleporter.Name}…";
            var until = DateTime.UtcNow.AddSeconds(30);
            while (game.World.Character is { } me && me.EstimatePosition().DistanceTo(npc.Position) > 5 && DateTime.UtcNow < until)
                await Task.Delay(250);
        }
        game.SelectEntity(npc.UniqueId);
        await Task.Delay(400);
        bool sent = game.DesignateRecall(npc.UniqueId);
        lblMapMessage.Text = sent ? $"Recall point set at {teleporter.Name}." : "The bot is not connected.";
    }

    /// <summary>
    /// Updates the visible page: the map every tick (250 ms, movement looks smooth), the rest every second.
    /// </summary>
    private void RefreshPages(bool force)
    {
        bool slow = force || _tick % 4 == 0;
        if (pageMap.Visible)
        {
            var snapshot = Snapshot();
            mapCanvas.SetSnapshot(snapshot);
            mapCanvas.SetRoute(_town?.Route ?? []);
            if (slow)
                mapCanvas.Stats = snapshot is null ? null : _stats?.Sample();
            if (slow)
            {
                nearby.SetSnapshot(snapshot);
                mapCharacter.SetSnapshot(snapshot, StateText());
            }
        }
        if (pageScript.Visible)
            pageScript.SetSnapshot(Snapshot());
        if (!slow)
            return;
        if (pageCharacter.Visible)
        {
            pageCharacter.SetSheet(Sheet());
            pageCharacter.SetSkills(Skills());
        }
        if (pageInventory.Visible)
            pageInventory.SetSheet(Sheet());
        if (pageTrade.Visible)
            pageTrade.RefreshView();
        if (pageParty.Visible)
            pageParty.RefreshView();
        if (pageGuild.Visible)
            pageGuild.RefreshView();
        if (pagePlayers.Visible)
            pagePlayers.RefreshView();
        if (pagePet.Visible)
            pagePet.RefreshView();
        if (pagePick.Visible)
            pagePick.RefreshView();
        if (pageAttack.Visible)
            pageAttack.RefreshView();
        if (pageTraining.Visible)
            pageTraining.RefreshAreas();
        if (pageStall.Visible)
            pageStall.RefreshView();
        if (pageAlchemy.Visible)
            pageAlchemy.RefreshView();
    }

    private void OnTimer()
    {
        _tick++;
        RefreshPages(force: false);
        if (_tick % 4 == 0)
            RefreshStatus();
    }

    #endregion

    #region UI helpers

    private void RunOnUi(Action action)
    {
        if (IsDisposed)
            return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (InvalidOperationException)
        {
            // closing
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool running = _session is not null;
        var state = _session?.State ?? BotState.Idle;
        bool clientless = _session?.IsClientless ?? false;

        btnReadConfig.Enabled = !_busy && !running;
        btnBrowse.Enabled = !running;
        btnBuildDb.Enabled = !_busy && !running;
        txtClientPath.Enabled = !running;
        cmbDivision.Enabled = cmbGateway.Enabled = !running;
        pnlAccount.Enabled = !running;

        btnStartClient.Enabled = !_busy && !running;
        btnStartClientless.Enabled = !_busy && !running;
        btnGoClientless.Enabled = running && !clientless && state is BotState.Loading or BotState.InGame or BotState.CharacterSelection;
        btnStop.Enabled = running;
        chkShowClient.Enabled = running && !clientless;
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mapTimer.Dispose();
            _tiles?.Dispose();
        }
        base.Dispose(disposing);
    }
}
