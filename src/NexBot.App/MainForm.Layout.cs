using NexBot.App.Theme;
using NexBot.App.Views;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App;

/// <summary>The window, built in code: header with nav, Home / Map / Log pages, status bar.</summary>
public partial class MainForm
{
    // Header / footer
    private SroHeader header = null!;
    private SroStatusBar statusBar = null!;
    private SideNav sideNav = null!;
    private Control pageServer = null!, pageMap = null!, pageLog = null!;
    private CharacterPage pageCharacter = null!;
    private InventoryPage pageInventory = null!;
    private ScriptPage pageScript = null!;
    private AutoTradePage pageTrade = null!;
    private PartyPage pageParty = null!;
    private GuildPage pageGuild = null!;
    private AcademyPage pageAcademy = null!;
    private PlayersPage pagePlayers = null!;
    private PetPage pagePet = null!;
    private PickFilterPage pagePick = null!;
    private AttackPage pageAttack = null!;
    private TrainingPage pageTraining = null!;
    private SoundPage pageSound = null!;
    private StallPage pageStall = null!;
    private AlchemyPage pageAlchemy = null!;
    private ConditionsPage pageConditions = null!;
    private ProtectionPage pageProtection = null!;
    private TownPage pageTown = null!;
    private GmPage pageGm = null!;
    private SroInput txtAreaRadius = null!;
    private SroButton btnRecordPolygon = null!, btnStopPolygon = null!, btnClearPolygon = null!;
    private IconCache icons = null!;

    // Client & server
    private SroInput txtClientPath = null!;
    private SroButton btnBrowse = null!, btnReadConfig = null!, btnBuildDb = null!;
    private SroCombo cmbDivision = null!, cmbGateway = null!;
    private Label lblVersion = null!, lblLocale = null!, lblPort = null!, lblDatabase = null!, lblProxy = null!;

    // Account
    private SroPanel pnlAccount = null!;
    private SroInput txtUsername = null!, txtPassword = null!, txtCaptcha = null!;
    private SroCombo cmbShard = null!, cmbCharacter = null!;
    private SroSwitch chkAutoLogin = null!, chkAutoSelect = null!, chkClientlessOnClose = null!;

    // Bot
    private SroButton btnStartClient = null!, btnStartClientless = null!, btnGoClientless = null!, btnStop = null!;
    private SroSwitch chkShowClient = null!;
    private Label lblState = null!;

    // Character / map / log
    private CharacterCard mapCharacter = null!;
    private MapCanvas mapCanvas = null!;
    private NearbyList nearby = null!;
    private Label lblMapMessage = null!;
    private RichTextBox txtLog = null!, txtActivity = null!;
    private SroCombo cmbLogFilter = null!;
    private SroInput txtLogSearch = null!;
    private SroButton btnLogSave = null!;

    /// <summary>The Show list's entries, in the order of <see cref="LogFilter"/>.</summary>
    private static readonly string[] LogFilterNames = ["Normal logs", "Warnings and errors", "Packet logs", "Debug logs", "Everything"];

    private void BuildUi()
    {
        SuspendLayout();
        Text = "NexBot";
        BackColor = Bg;
        ForeColor = SroTheme.Text;
        Font = Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1360, 820);
        MinimumSize = new Size(1240, 800);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // keep the default icon
        }

        header = new SroHeader();
        statusBar = new SroStatusBar();
        sideNav = new SideNav();
        icons = new IconCache(this);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Bg };
        pageServer = BuildHome();
        pageMap = BuildMap();
        pageCharacter = new CharacterPage(() => _session?.Game, icons);
        pageInventory = new InventoryPage(() => _session?.Game, icons) { SheetSource = Sheet };
        pageScript = new ScriptPage(() => _session?.Game);
        pageTrade = new AutoTradePage(() => _session?.Game, icons);
        pageParty = new PartyPage(() => _session?.Game, () => _social, icons);
        pageGuild = new GuildPage(() => _session?.Game, icons);
        pageAcademy = new AcademyPage();
        pagePlayers = new PlayersPage(() => _session?.Game, () => _social, icons);
        pagePet = new PetPage(() => _session?.Game, () => _pets, () => _pick, icons);
        pagePick = new PickFilterPage(BrowseData, () => _pick, icons);
        pageAttack = new AttackPage(() => _session?.Game, () => _hunt, icons);
        pageTraining = new TrainingPage(() => _session?.Game, () => _town);
        pageSound = new SoundPage();
        pageStall = new StallPage(() => _session?.Game, () => _stall, icons);
        pageAlchemy = new AlchemyPage(() => _session?.Game, () => _alchemy, icons);
        pageConditions = new ConditionsPage();
        pageProtection = new ProtectionPage(() => _protection, BrowseData);
        pageTown = new TownPage(BrowseData, () => _town, icons);
        pageGm = new GmPage(BrowseData, () => _gm);
        pageLog = BuildLog();
        foreach (var page in Pages)
        {
            page.Visible = false;
            content.Controls.Add(page);
        }
        sideNav.Add("Server", "Client, login, bot", NavGlyph.Server, pageServer);
        sideNav.Add("Map", "World map, nearby", NavGlyph.Map, pageMap);
        sideNav.Add("Character", "Stats, masteries, skills", NavGlyph.Character, pageCharacter);
        sideNav.Add("Attack", "Skills, imbues, buffs, hunting", NavGlyph.Attack, pageAttack);
        sideNav.Add("Training area", "Areas, conditions, scripts", NavGlyph.Training, pageTraining);
        sideNav.Add("Protection", "Potions, return, stats", NavGlyph.Protection, pageProtection);
        sideNav.Add("Town", "Buy, sell, repair loop", NavGlyph.Town, pageTown);
        sideNav.Add("Inventory", "Equipment and items", NavGlyph.Inventory, pageInventory);
        sideNav.Add("Pet", "Attack pet, summon, revive", NavGlyph.Pet, pagePet);
        sideNav.Add("Pick filter", "Pick, store, sell, drop, gold", NavGlyph.PickFilter, pagePick);
        sideNav.Add("Script", "Record and walk routes", NavGlyph.Script, pageScript);
        sideNav.Add("AutoTrade", "Transport, job suit, cargo", NavGlyph.Trade, pageTrade);
        sideNav.Add("Party", "Invites, leaders, matching", NavGlyph.Party, pageParty);
        sideNav.Add("Guild", "Guild invites", NavGlyph.Guild, pageGuild);
        sideNav.Add("Academy", "Academy invites", NavGlyph.Academy, pageAcademy);
        sideNav.Add("Players", "Nearby players, trace", NavGlyph.Players, pagePlayers);
        sideNav.Add("Stall", "Sell items to players", NavGlyph.Stall, pageStall);
        sideNav.Add("Alchemy", "Plus, white and blue stones", NavGlyph.Alchemy, pageAlchemy);
        sideNav.Add("Conditions", "If … then … rules", NavGlyph.Conditions, pageConditions);
        sideNav.Add("Sound", "Alerts for game events", NavGlyph.Sound, pageSound);
        sideNav.Add("Log", "Everything the bot did", NavGlyph.Log, pageLog);
        sideNav.Add("GM", "Spawn, kill, power level", NavGlyph.GameMaster, pageGm);
        sideNav.SetShown(pageGm, false); // shown when a GM character logs in
        sideNav.PageSelected += ShowPage;

        // Dock order: the last added docks first (header and status bar span the full width).
        Controls.Add(content);
        Controls.Add(sideNav);
        Controls.Add(statusBar);
        Controls.Add(header);

        sideNav.SelectPage(pageServer);
        ResumeLayout(true);
    }

    private Control[] Pages => [pageServer, pageMap, pageCharacter, pageAttack, pageTraining, pageProtection, pageTown, pageInventory, pagePet, pagePick, pageScript, pageTrade, pageParty, pageGuild, pageAcademy, pagePlayers, pageStall, pageAlchemy, pageConditions, pageSound, pageLog, pageGm];

    private void ShowPage(Control page)
    {
        foreach (var p in Pages)
            p.Visible = ReferenceEquals(p, page);
        RefreshPages(force: true);
    }

    #region Pages

    private Control BuildHome()
    {
        var grid = Grid(3, 2);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 430));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Client & server ---------------------------------------------------------------
        var client = Panel("Client & Server");
        var cs = Stack();
        txtClientPath = new SroInput();
        btnBrowse = new SroButton { Text = "Browse", Width = 80, Margin = new Padding(6, 0, 0, 8), Height = 28 };
        var pathRow = new TableLayoutPanel { ColumnCount = 2, Height = 36, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        txtClientPath.Dock = DockStyle.Fill;
        pathRow.Controls.Add(txtClientPath, 0, 0);
        pathRow.Controls.Add(btnBrowse, 1, 0);
        Field(cs, "Silkroad client folder", pathRow);

        btnReadConfig = new SroButton { Text = "Read server config", Style = SroButtonStyle.Gold, Height = 32 };
        btnBuildDb = new SroButton { Text = "Build database", Height = 32 };
        Add(cs, Buttons(btnReadConfig, btnBuildDb));

        cmbDivision = new SroCombo();
        cmbGateway = new SroCombo();
        Add(cs, Pair(("Division", cmbDivision), ("Gateway", cmbGateway)));

        Add(cs, new SroParchment { Text = "Server", Dock = DockStyle.Top });
        lblVersion = Sro.Value();
        lblLocale = Sro.Value();
        lblPort = Sro.Value();
        lblDatabase = Sro.Value();
        lblProxy = Sro.Value();
        Add(cs, Info(("Version", lblVersion), ("Locale", lblLocale), ("Gateway port", lblPort), ("Database", lblDatabase), ("Proxy", lblProxy)));
        client.Controls.Add(cs);
        grid.Controls.Add(client, 0, 0);

        // Account -----------------------------------------------------------------------
        pnlAccount = Panel("Auto Login");
        var acc = Stack();
        txtUsername = new SroInput();
        txtPassword = new SroInput { Password = true };
        Add(acc, Pair(("Username", txtUsername), ("Password", txtPassword)));
        cmbShard = new SroCombo(editable: true);
        cmbCharacter = new SroCombo(editable: true);
        Add(acc, Pair(("Server", cmbShard), ("Character", cmbCharacter)));
        txtCaptcha = new SroInput();
        Field(acc, "Captcha (if the server asks for one)", txtCaptcha);
        Add(acc, new SroParchment { Text = "Options", Dock = DockStyle.Top });
        chkAutoLogin = new SroSwitch { Text = "Log in automatically" };
        chkAutoSelect = new SroSwitch { Text = "Select the character automatically" };
        chkClientlessOnClose = new SroSwitch { Text = "Stay online (clientless) when the client closes" };
        Add(acc, chkAutoLogin);
        Add(acc, chkAutoSelect);
        Add(acc, chkClientlessOnClose);
        pnlAccount.Controls.Add(acc);
        grid.Controls.Add(pnlAccount, 1, 0);

        // Bot ---------------------------------------------------------------------------
        var bot = Panel("Bot");
        var bs = Stack();
        btnStartClient = new SroButton { Text = "Start client", Style = SroButtonStyle.Primary, Height = 40 };
        btnStartClientless = new SroButton { Text = "Start clientless", Height = 34 };
        btnGoClientless = new SroButton { Text = "Go clientless", Style = SroButtonStyle.Gold, Height = 34 };
        btnStop = new SroButton { Text = "Stop", Style = SroButtonStyle.Danger, Height = 34 };
        foreach (var b in new[] { btnStartClient, btnStartClientless, btnGoClientless, btnStop })
        {
            b.Dock = DockStyle.Top;
            b.Margin = new Padding(0, 0, 0, 8);
            Add(bs, b);
        }
        chkShowClient = new SroSwitch { Text = "Show the game window", Checked = true };
        Add(bs, chkShowClient);
        Add(bs, new SroParchment { Text = "Session", Dock = DockStyle.Top });
        lblState = Sro.Label("Idle", SroTheme.Value, Title(11f));
        lblState.UseCompatibleTextRendering = true; // GDI+: GDI cannot draw the embedded Cinzel font
        Add(bs, lblState);
        var help = Sro.Label("Start client opens sro_client through the loader and logs it in. Go clientless closes the window and keeps the character online.");
        help.AutoSize = false;
        help.Height = 64;
        help.Margin = new Padding(0, 6, 0, 0);
        Add(bs, help);
        bot.Controls.Add(bs);
        grid.Controls.Add(bot, 2, 0);

        // Activity (the whole width) ---------------------------------------------------------
        var activity = Panel("Activity");
        txtActivity = LogBox();
        var activityTools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        var clearActivity = new SroButton { Text = "Clear", Width = 80, Margin = new Padding(0, 0, 12, 0) };
        clearActivity.Click += (_, _) => txtActivity.Clear();
        var activityHint = Sro.Label("Normal lines only · packets, debug lines and filters: Log page");
        activityHint.Margin = new Padding(0, 8, 0, 0);
        activityTools.Controls.AddRange([clearActivity, activityHint]);
        activity.Controls.Add(Framed(txtActivity));
        activity.Controls.Add(activityTools);
        grid.Controls.Add(activity, 0, 1);
        grid.SetColumnSpan(activity, 3);
        return grid;
    }

    private Control BuildMap()
    {
        var grid = Grid(2, 1);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var map = Panel("World Map");
        mapCanvas = new MapCanvas { Dock = DockStyle.Fill };
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        var zoomOut = new SroButton { Text = "−", Width = 34, Margin = new Padding(0, 0, 6, 0) };
        var zoomIn = new SroButton { Text = "+", Width = 34, Margin = new Padding(0, 0, 6, 0) };
        var center = new SroButton { Text = "Center on me", Style = SroButtonStyle.Gold, Width = 120, Margin = new Padding(0, 0, 12, 0) };
        zoomOut.Click += (_, _) => mapCanvas.ZoomBy(-1);
        zoomIn.Click += (_, _) => mapCanvas.ZoomBy(1);
        center.Click += (_, _) => mapCanvas.Follow = true;
        btnRecordPolygon = new SroButton { Text = "Record polygon", Width = 116, Margin = new Padding(0, 0, 6, 0) };
        btnStopPolygon = new SroButton { Text = "Stop recording", Width = 112, Margin = new Padding(0, 0, 6, 0), Enabled = false };
        btnClearPolygon = new SroButton { Text = "Clear", Width = 60, Margin = new Padding(0, 0, 12, 0) };
        var radiusLabel = Sro.Label("Area radius");
        radiusLabel.Margin = new Padding(0, 8, 4, 0);
        txtAreaRadius = new SroInput { Width = 54, Margin = new Padding(0, 0, 12, 0) };
        lblMapMessage = Sro.Label("Click to walk · drag to look around · wheel to zoom · right-click for the map menu");
        lblMapMessage.Margin = new Padding(0, 8, 0, 0);
        tools.Controls.AddRange([zoomOut, zoomIn, center, btnRecordPolygon, btnStopPolygon, btnClearPolygon, radiusLabel, txtAreaRadius, lblMapMessage]);
        map.Controls.Add(mapCanvas);
        map.Controls.Add(tools);
        grid.Controls.Add(map, 0, 0);

        var side = Grid(1, 2);
        side.Margin = Padding.Empty;
        side.Padding = Padding.Empty;
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 290));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var character = Panel("Character");
        mapCharacter = new CharacterCard { Dock = DockStyle.Fill };
        character.Controls.Add(mapCharacter);
        side.Controls.Add(character, 0, 0);
        var near = Panel("Nearby");
        nearby = new NearbyList { Dock = DockStyle.Fill };
        near.Controls.Add(nearby);
        side.Controls.Add(near, 0, 1);
        grid.Controls.Add(side, 1, 0);
        return grid;
    }

    private Control BuildLog()
    {
        var panel = Panel("Log");
        panel.Dock = DockStyle.Fill;
        txtLog = LogBox();
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        var clear = new SroButton { Text = "Clear logs", Width = 100, Margin = new Padding(0, 0, 12, 0) };
        clear.Click += (_, _) => _logger.Clear();
        var showLabel = Sro.Label("Show");
        showLabel.Margin = new Padding(0, 8, 6, 0);
        cmbLogFilter = new SroCombo { Width = 180, Margin = new Padding(0, 0, 12, 0) };
        cmbLogFilter.SetItems(LogFilterNames);
        var searchLabel = Sro.Label("Search");
        searchLabel.Margin = new Padding(0, 8, 6, 0);
        txtLogSearch = new SroInput { Width = 220, Margin = new Padding(0, 0, 12, 0) };
        btnLogSave = new SroButton { Text = "Save to file", Width = 110, Margin = new Padding(0, 0, 12, 0) };
        tools.Controls.AddRange([clear, showLabel, cmbLogFilter, searchLabel, txtLogSearch, btnLogSave]);
        panel.Controls.Add(Framed(txtLog));
        panel.Controls.Add(tools);
        return panel;
    }

    #endregion

    #region Builders

    private static TableLayoutPanel Grid(int columns, int rows) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = columns,
        RowCount = rows,
        BackColor = Bg,
        Margin = Padding.Empty,
    };

    private static SroPanel Panel(string title) => new() { Title = title, Dock = DockStyle.Fill, Margin = new Padding(5) };

    /// <summary>A top-to-bottom stack of rows (each row docks to the top, in order).</summary>
    private static Panel Stack() => new() { Dock = DockStyle.Fill, BackColor = Color.Transparent, AutoScroll = false };

    private static void Add(Panel stack, Control control)
    {
        control.Dock = DockStyle.Top;
        stack.Controls.Add(control);
        stack.Controls.SetChildIndex(control, 0); // newest docks last = lowest
    }

    private static void Field(Panel stack, string caption, Control control)
    {
        var label = Sro.Label(caption);
        label.AutoSize = false;
        label.Height = 18;
        Add(stack, label);
        Add(stack, control);
    }

    /// <summary>Two labelled fields side by side.</summary>
    private static Control Pair((string Caption, Control Control) left, (string Caption, Control Control) right)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Height = 56, BackColor = Color.Transparent, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        int column = 0;
        foreach (var (caption, control) in new[] { left, right })
        {
            var label = Sro.Label(caption);
            label.Margin = new Padding(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 0);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 8);
            table.Controls.Add(label, column, 0);
            table.Controls.Add(control, column, 1);
            column++;
        }
        return table;
    }

    private static Control Buttons(params SroButton[] buttons)
    {
        var table = new TableLayoutPanel { ColumnCount = buttons.Length, Height = buttons.Max(b => b.Height) + 10, BackColor = Color.Transparent, Margin = Padding.Empty };
        for (int i = 0; i < buttons.Length; i++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length));
            buttons[i].Dock = DockStyle.Fill;
            buttons[i].Margin = new Padding(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 10);
            table.Controls.Add(buttons[i], i, 0);
        }
        return table;
    }

    private static Control Info(params (string Caption, Label Value)[] rows)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (caption, value) in rows)
        {
            value.AutoEllipsis = true;
            table.Controls.Add(Sro.Label(caption));
            table.Controls.Add(value);
        }
        return table;
    }

    private static RichTextBox LogBox() => new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        BackColor = InputBack,
        ForeColor = SroTheme.Text,
        Font = Mono,
        ReadOnly = true,
        DetectUrls = false,
        WordWrap = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        HideSelection = false,
    };

    /// <summary>Puts a control inside a 1 px input-colored border.</summary>
    private static Control Framed(Control inner)
    {
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = InputLine, Padding = new Padding(1) };
        var pad = new Panel { Dock = DockStyle.Fill, BackColor = InputBack, Padding = new Padding(8, 6, 4, 6) };
        inner.Dock = DockStyle.Fill;
        pad.Controls.Add(inner);
        frame.Controls.Add(pad);
        return frame;
    }

    #endregion
}
