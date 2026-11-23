using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NexBot.Media;

/// <summary>
/// Builds the bot's <c>Database.sqlite3</c> from the client's <c>Media.pk2</c> (textdata files).
/// </summary>
/// <remarks>
/// <para>
/// Port of MediaExtractor's <c>Pk2ExtractorFrm</c> database generation (minus the WinForms UI and the DevIL
/// icon export, which is Windows-only and not needed by the bot). It produces the same tables and columns, so
/// <see cref="Data.SqliteGameData"/> reads the output unchanged.
/// </para>
/// <para>Improvements over the original:</para>
/// <list type="bullet">
///   <item>Everything runs on one connection, with one prepared command per table and one transaction per
///   table. Upserts use <c>INSERT OR REPLACE</c> instead of a SELECT before every row.</item>
///   <item>The database is built in a temporary file and moved into place at the end, so a crash never leaves a
///   half-built database behind.</item>
///   <item>Skill data is decrypted in memory. The original wrote temporary files next to the database.</item>
///   <item>Malformed lines are skipped and counted instead of aborting the whole extraction. This also fixes the
///   original's broken item <c>UPDATE</c> statement and its mastery "type" fallback.</item>
/// </list>
/// </remarks>
public sealed class GameDatabaseBuilder
{
    private const string TextData = "server_dep\\silkroad\\textdata\\";
    private static readonly string[] s_cFormats = ["%d", "%s", "%ld", "%u", "%08x", "%I64d", "%l64d"];

    private readonly Pk2Archive _media;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (string Name, string Tid2, string Tid3, string Tid4)> _models = [];
    private int _languageIndex = 8;
    private int _skipped;

    public GameDatabaseBuilder(Pk2Archive media, ILogger? logger = null)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Row counts per table after <see cref="Build"/>.</summary>
    public Dictionary<string, int> RowCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Builds the database at <paramref name="outputPath"/>, replacing any existing file.</summary>
    public void Build(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temp = outputPath + ".building";
        File.Delete(temp);

        var total = Stopwatch.StartNew();
        var builder = new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using (var db = new SqliteConnection(builder.ToString()))
        {
            db.Open();
            Execute(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");

            DetectLanguage();
            Step("Name references", () => LoadNames());
            Step("Text references", () => { LoadTexts(); AddTexts(db); });
            Step("Items", () => AddItems(db));
            Step("Magic options", () => AddMagicOptions(db));
            Step("Characters & monsters", () => AddModels(db));
            Step("Masteries", () => AddMasteries(db));
            Step("Skills", () => AddSkills(db));
            Step("Level data", () => AddLevels(db));
            Step("Shops", () => AddShops(db));
            Step("Shop goods", () => AddShopGoods(db));
            var teleports = LoadTeleportData();
            Step("Teleport buildings", () => AddTeleportBuildings(db));
            Step("Teleport links", () => AddTeleportLinks(db, teleports));
            Step("Regions", () => AddRegions(db));
        }

        ReplaceDatabase(temp, outputPath);
        _logger.LogInformation("Game database generated in {Seconds:0.0}s: {Path} ({Skipped} malformed line(s) skipped)",
            total.Elapsed.TotalSeconds, outputPath, _skipped);
    }

    /// <summary>
    /// Puts the new database in place of the old one. The running bot keeps pooled connections to the old file, which
    /// lock it on Windows ("Access to the path is denied"): close them first, and try again for a few seconds in case
    /// something else (a virus scanner) holds the file for a moment.
    /// </summary>
    private void ReplaceDatabase(string temp, string outputPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                File.Move(temp, outputPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                if (attempt == 1)
                    _logger.LogInformation("  The old database is in use; waiting for it to be released...");
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"The new database is ready ({Path.GetFileName(temp)}), but '{outputPath}' is in use and could not be replaced. "
                    + "Close NexBot (and any other program using it), then rename the .building file to Database.sqlite3, or build again.", ex);
            }
        }
    }

    private void Step(string name, Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        _logger.LogInformation("  {Step} done in {Ms} ms", name, sw.ElapsedMilliseconds);
    }

    #region Reading textdata

    /// <summary>Enumerates tab-separated rows of a textdata file; <paramref name="enabledOnly"/> keeps rows starting with "1\t".</summary>
    private IEnumerable<string[]> Rows(string fileName, bool enabledOnly = true, bool crlfOnly = false, byte[]? content = null)
    {
        string text = content is null ? _media.ReadText(TextData + fileName) : Pk2Archive.DecodeText(content);
        string[] lines = crlfOnly
            ? text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            if (enabledOnly ? !line.StartsWith("1\t", StringComparison.Ordinal) : line.StartsWith("//", StringComparison.Ordinal))
                continue;
            yield return line.Split('\t');
        }
    }

    /// <summary>Index files such as ItemData.txt list the real data files, one per line.</summary>
    private IEnumerable<string> IndexFiles(string indexFile) =>
        _media.ReadText(TextData + indexFile)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.Length > 0 && !f.StartsWith("//", StringComparison.Ordinal) && _media.FileExists(TextData + f));

    private void DetectLanguage()
    {
        if (!_media.FileExists("type.txt"))
            return;
        foreach (string line in _media.ReadText("type.txt").Split('\n'))
        {
            if (!line.Contains("Language", StringComparison.Ordinal) || !line.Contains('='))
                continue;
            string type = line.Split('=')[1].Replace("\"", "", StringComparison.Ordinal).Trim();
            _languageIndex = type switch { "Vietnam" => 9, "Russia" => 10, _ => 8 };
            _logger.LogInformation("Client language: {Language} (column {Index})", type, _languageIndex);
            return;
        }
    }

    private void LoadNames()
    {
        foreach (string file in IndexFiles("TextDataName.txt"))
            foreach (var data in Rows(file))
                if (data.Length > _languageIndex && data[_languageIndex] != "0")
                    _names[data[1]] = data[_languageIndex];
    }

    private void LoadTexts()
    {
        foreach (var data in Rows("TextUISystem.txt", crlfOnly: true))
        {
            if (data.Length <= _languageIndex || data[_languageIndex] == "0")
                continue;
            // Convert C printf placeholders to .NET {n} placeholders.
            string text = data[_languageIndex];
            int count = 0;
            foreach (string format in s_cFormats)
            {
                int index;
                while ((index = text.IndexOf(format, StringComparison.Ordinal)) != -1)
                    text = $"{text[..index]}{{{count++}}}{text[(index + format.Length)..]}";
            }
            _texts[data[1]] = text;
        }
    }

    private string Name(string key) => key != "xxx" && _names.TryGetValue(key, out var v) ? v : string.Empty;
    private string Text(string key) => _texts.TryGetValue(key, out var v) ? v : string.Empty;

    #endregion

    #region Tables

    private void AddTexts(SqliteConnection db)
    {
        Execute(db, "CREATE TABLE textuisystem (fakeID INTEGER PRIMARY KEY, servername VARCHAR(64) UNIQUE, text VARCHAR(256));");
        int id = 0;
        Insert(db, "textuisystem", ["fakeID", "servername", "text"],
            _texts.Select(kv => new object?[] { id++, kv.Key, kv.Value }));
    }

    private void AddItems(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE items (id INTEGER PRIMARY KEY, servername VARCHAR(64), SN_Name VARCHAR(64) NULL, name VARCHAR(64),
              stack INTEGER, cashitem INTEGER, bionic INTEGER, tid2 INTEGER, tid3 INTEGER, tid4 INTEGER, level INTEGER, icon VARCHAR(64),
              description VARCHAR(2048), params VARCHAR(128), equip VARCHAR(512));
            CREATE INDEX ix_items_servername ON items(servername);
            """);
        Insert(db, "items", ["id", "servername", "SN_Name", "name", "stack", "cashitem", "bionic", "tid2", "tid3", "tid4", "level", "icon", "description", "params", "equip"],
            IndexFiles("ItemData.txt").SelectMany(f => Rows(f)).Select(d => Row(d, 58, () =>
            [
                d[1], d[2], d[5], Name(d[5]), d[57], d[7], d[8], d[10], d[11], d[12], d[33],
                (d.Length > 150 ? d[54] : d[50]).ToLowerInvariant(),
                ItemDescription(d[6]),
                d.Length > 124 ? string.Join('|', d[118], d[120], d[122], d[124]) : "",
                // Equipment: the stat ranges, gender, race, degree ... (EquipmentText reads them).
                d[9] == "3" && d[10] == "1" && d.Length > 158 ? string.Join('|', NexBot.Map.EquipmentText.Columns.Select(c => d[c])) : "",
            ])));
    }

    /// <summary>
    /// The item's description (DescStrID128) as the client has it: with its &lt;br&gt; line breaks and &lt;strong&gt;
    /// headings ("How To Use", "Restriction"), which the item card shows like the game.
    /// </summary>
    private string ItemDescription(string key)
    {
        string text = Name(key);
        if (text.Length == 0)
            text = Text(key);
        return text.Replace("\\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal).Trim();
    }

    private void AddMagicOptions(SqliteConnection db)
    {
        Execute(db, "CREATE TABLE magicoptions (id INTEGER PRIMARY KEY, servername VARCHAR(64), name VARCHAR(64), degree INTEGER, value FLOAT, maxvalue INTEGER, increase BOOLEAN, usage VARCHAR(64));");
        Insert(db, "magicoptions", ["id", "servername", "name", "degree", "value", "maxvalue", "increase", "usage"],
            Rows("MagicOption.txt").Select(d => Row(d, 11, () =>
            {
                var usage = new List<string>();
                for (int j = 30; j < d.Length && d[j] == "1"; j += 2)
                    usage.Add(d[j - 1]);
                return
                [
                    d[1], d[2], Text(MagicOptionTextKey(d[2])), d[4], d[5],
                    int.Parse(d[10], CultureInfo.InvariantCulture) & ushort.MaxValue, d[3] == "+", string.Join('|', usage),
                ];
            })));
    }

    private static string MagicOptionTextKey(string serverName) => serverName switch
    {
        "MATTR_INT" => "PARAM_INT",
        "MATTR_STR" => "PARAM_STR",
        "MATTR_DUR" => "PARAM_DUR",
        "MATTR_SOLID" => "PARAM_SOLID",
        "MATTR_LUCK" => "PARAM_LUCK",
        "MATTR_ASTRAL" => "PARAM_ASTRAL",
        "MATTR_ATHANASIA" => "PARAM_ATHANASIA",
        "MATTR_HP" => "PARAM_HP",
        "MATTR_MP" => "PARAM_MP",
        "MATTR_ER" => "PARAM_ER",
        "MATTR_HR" => "PARAM_HR",
        "MATTR_EVADE_BLOCK" => "PARAM_IGNORE_BLOCKING",
        "MATTR_EVADE_CRITICAL" => "PARAM_EVADE_CRITICAL",
        "MATTR_RESIST_FROSTBITE" => "PARAM_COLD_RESIST",
        "MATTR_RESIST_ESHOCK" => "PARAM_ESHOCK_RESIST",
        "MATTR_RESIST_BURN" => "PARAM_BURN_RESIST",
        "MATTR_RESIST_POISON" => "PARAM_POISON_RESIST",
        "MATTR_RESIST_ZOMBIE" => "PARAM_ZOMBI_RESIST",
        _ => string.Empty,
    };

    private void AddModels(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE models (id INTEGER PRIMARY KEY, servername VARCHAR(64), name VARCHAR(64), tid2 INTEGER, tid3 INTEGER, tid4 INTEGER, hp INTEGER, level INTEGER, icon VARCHAR(64));
            CREATE INDEX ix_models_servername ON models(servername);
            """);
        Insert(db, "models", ["id", "servername", "name", "tid2", "tid3", "tid4", "hp", "level", "icon"],
            IndexFiles("CharacterData.txt").SelectMany(f => Rows(f)).Select(d => Row(d, 60, () =>
            {
                string name = Name(d[5]);
                if (name.Length == 0)
                    name = d[2];
                if (uint.TryParse(d[1], CultureInfo.InvariantCulture, out uint id))
                    _models[id] = (name, d[10], d[11], d[12]);
                return [d[1], d[2], name, d[10], d[11], d[12], d[59], d[57], ModelIcon(d[54])];
            })));
    }

    /// <summary>Portrait column of CharacterData (AssocFileIcon); "xxx" means none.</summary>
    public static string ModelIcon(string value) =>
        value.Length == 0 || value.Equals("xxx", StringComparison.OrdinalIgnoreCase) ? string.Empty : value.ToLowerInvariant();

    private void AddMasteries(SqliteConnection db)
    {
        // tab: the skill window tab (0 weapon / physical, 1 force / magical, 2 assist); type is that tab's name.
        Execute(db, "CREATE TABLE masteries (id INTEGER PRIMARY KEY, name VARCHAR(64), description VARCHAR(256), type VARCHAR(64), weapons VARCHAR(12), icon VARCHAR(64), tab INTEGER);");
        string Resolve(string key)
        {
            string v = Text(key);
            if (v.Length == 0) v = Name(key);
            return v.Length == 0 ? key : v;
        }
        Insert(db, "masteries", ["id", "name", "description", "type", "weapons", "icon", "tab"],
            Rows("SkillMasteryData.txt", enabledOnly: false)
                .Where(d => d.Length == 13 && d[2] != "xxx")
                .Select(d => new object?[] { d[0], Resolve(d[2]), Resolve(d[4]), Resolve(d[5]), $"{d[8]},{d[9]},{d[10]}", d[11], d[6] }));
    }

    private void AddSkills(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE skills (id INTEGER PRIMARY KEY, servername VARCHAR(64), name VARCHAR(64), description VARCHAR(1024),
              casttime INTEGER, duration INTEGER, cooldown INTEGER, mana INTEGER, level INTEGER, mastery_id INTEGER, sp INTEGER,
              group_id INTEGER, group_name VARCHAR(64), skill_chain_id INTEGER, weapon_first INTEGER, weapon_second INTEGER,
              target_required BOOLEAN, params VARCHAR(256), icon VARCHAR(64),
              skill_level INTEGER, ui_tab INTEGER, ui_page INTEGER, ui_column INTEGER, ui_row INTEGER,
              req_skill1 INTEGER, req_skill_level1 INTEGER, req_skill2 INTEGER, req_skill_level2 INTEGER);
            CREATE INDEX ix_skills_servername ON skills(servername);
            CREATE INDEX ix_skills_mastery ON skills(mastery_id);
            CREATE INDEX ix_skills_group ON skills(group_id);
            """);

        // SkillDataEnc.txt lists encrypted files; fall back to plain SkillData.txt on older clients.
        string index = _media.FileExists(TextData + "SkillDataEnc.txt") ? "SkillDataEnc.txt" : "SkillData.txt";
        var rows = IndexFiles(index).SelectMany(f =>
            Rows(f, content: SkillDataDecryptor.Decrypt(_media.ReadBytes(TextData + f))));

        Insert(db, "skills",
            ["id", "servername", "name", "description", "casttime", "duration", "cooldown", "mana", "level", "mastery_id", "sp",
             "group_id", "group_name", "skill_chain_id", "weapon_first", "weapon_second", "target_required", "params", "icon",
             "skill_level", "ui_tab", "ui_page", "ui_column", "ui_row", "req_skill1", "req_skill_level1", "req_skill2", "req_skill_level2"],
            rows.Select(d => Row(d, Skill.Param1 + 1, () =>
            {
                var parameters = new string[30];
                for (int j = 0; j < parameters.Length; j++)
                    parameters[j] = Skill.Param1 + j < d.Length ? d[Skill.Param1 + j] : string.Empty;

                string duration = "0";
                if (d[Skill.Param1] is "3" or "10") // buffs
                {
                    duration = ReadParam(parameters, "1685418593"); // SKILL_DURATION
                    if (duration.Length == 0)
                        duration = "1"; // infinite
                    else if (duration.StartsWith('-') && int.TryParse(duration, CultureInfo.InvariantCulture, out int negative))
                        duration = ((uint)negative).ToString(CultureInfo.InvariantCulture);
                }

                long castTime = long.Parse(d[Skill.PreparingTime], CultureInfo.InvariantCulture)
                                + long.Parse(d[Skill.CastingTime], CultureInfo.InvariantCulture)
                                + long.Parse(d[Skill.ActionDuration], CultureInfo.InvariantCulture);
                return
                [
                    d[Skill.Id], d[Skill.Code], Name(d[Skill.UiName]), Name(d[Skill.UiDescription]), castTime, duration,
                    d[Skill.ReuseDelay], d[Skill.ConsumeMp], d[Skill.MasteryLevel1], d[Skill.Mastery1], d[Skill.LearnSp],
                    d[Skill.GroupId], d[Skill.Group], d[Skill.ChainCode], d[Skill.Weapon1], d[Skill.Weapon2],
                    d[Skill.TargetRequired], string.Join('|', parameters), d[Skill.IconFile].ToLowerInvariant(),
                    d[Skill.Level], d[Skill.UiTab], d[Skill.UiPage], d[Skill.UiColumn], d[Skill.UiRow],
                    d[Skill.ReqSkill1], d[Skill.ReqSkillLevel1], d[Skill.ReqSkill2], d[Skill.ReqSkillLevel2],
                ];
            })));
    }

    private static string ReadParam(string[] parameters, string type)
    {
        for (int i = 0; i < parameters.Length - 1; i++)
            if (parameters[i] == type)
                return parameters[i + 1];
        return string.Empty;
    }

    private void AddLevels(SqliteConnection db)
    {
        Execute(db, "CREATE TABLE leveldata (level INTEGER PRIMARY KEY, player INTEGER, sp INTEGER, pet INTEGER, trader INTEGER, thief INTEGER, hunter INTEGER);");
        static string Job(string v) => v == "-1" ? "0" : v;
        Insert(db, "leveldata", ["level", "player", "sp", "pet", "trader", "thief", "hunter"],
            Rows("LevelData.txt", enabledOnly: false).Select(d => Row(d, 9, () => [d[0], d[1], d[2], d[5], Job(d[6]), Job(d[7]), Job(d[8])])));
    }

    /// <summary>
    /// Every shop's goods by the indexes the server uses when an item is bought (item mall: 0xB034 type 24): shop
    /// group ID, tab group index, tab index, slot index.
    /// </summary>
    private void AddShopGoods(SqliteConnection db)
    {
        var storesByGroup = Rows("refMappingShopGroup.txt").Where(d => d.Length >= 4).ToLookup(d => d[2], d => d[3]);
        var tabGroupsByStore = Rows("refMappingShopWithTab.txt").Where(d => d.Length >= 4).ToLookup(d => d[2], d => d[3]);
        var tabsByGroup = Rows("refShopTab.txt").Where(d => d.Length >= 5).ToLookup(d => d[4], d => d[3]);
        var goodsByTab = Rows("refShopGoods.txt").Where(d => d.Length >= 5).ToLookup(d => d[2], d => (Package: d[3], Slot: d[4]));
        var packages = new Dictionary<string, (string Item, string Plus, string Data)>(StringComparer.Ordinal);
        foreach (var d in Rows("refScrapOfPackageItem.txt"))
            if (d.Length >= 7)
                packages[d[2]] = (d[3], d[4], d[6]);

        Execute(db, """
            CREATE TABLE shopgoods (group_id INTEGER, group_index INTEGER, tab_index INTEGER, slot INTEGER,
              item_servername VARCHAR(64), plus INTEGER, data INTEGER);
            CREATE INDEX ix_shopgoods ON shopgoods(group_id, group_index, tab_index, slot);
            """);

        IEnumerable<object?[]> GoodRows()
        {
            foreach (var d in Rows("refShopGroup.txt"))
            {
                if (d.Length < 4 || !uint.TryParse(d[2], CultureInfo.InvariantCulture, out uint id))
                    continue;
                var stores = storesByGroup[d[3]].ToList();
                if (stores.Count == 0 && d[3].StartsWith("GROUP_", StringComparison.Ordinal))
                    stores.Add(d[3][6..]); // GROUP_MALL_CONSUME -> MALL_CONSUME
                var tabGroups = stores.SelectMany(s => tabGroupsByStore[s]).ToList();
                for (int g = 0; g < tabGroups.Count; g++)
                {
                    var tabs = tabsByGroup[tabGroups[g]].ToList();
                    for (int t = 0; t < tabs.Count; t++)
                    {
                        foreach (var good in goodsByTab[tabs[t]])
                        {
                            if (packages.TryGetValue(good.Package, out var item) && int.TryParse(good.Slot, CultureInfo.InvariantCulture, out int slot))
                                yield return [id, g, t, slot, item.Item, item.Plus, item.Data];
                        }
                    }
                }
            }
        }

        Insert(db, "shopgoods", ["group_id", "group_index", "tab_index", "slot", "item_servername", "plus", "data"], GoodRows());
    }

    private void AddShops(SqliteConnection db)
    {
        var shops = new List<Shop>();
        foreach (var d in Rows("refShopGroup.txt"))
        {
            if (d.Length < 5) { _skipped++; continue; }
            shops.Add(new Shop
            {
                StoreGroupName = d[3].StartsWith("GROUP_MALL_", StringComparison.Ordinal) ? d[3][6..] : d[3],
                NpcName = d[4],
            });
        }

        foreach (var d in Rows("refMappingShopGroup.txt"))
        {
            if (d.Length < 4) continue;
            foreach (var shop in shops)
            {
                if (shop.StoreGroupName.StartsWith("MALL", StringComparison.Ordinal))
                {
                    if (shop.StoreGroupName == d[3])
                    {
                        shop.StoreName = d[3];
                        shop.StoreGroupName = d[2];
                    }
                }
                else if (shop.StoreGroupName == d[2])
                {
                    shop.StoreName = d[3];
                }
            }
        }

        var shopsByStore = shops.Where(s => s.StoreName is not null).ToLookup(s => s.StoreName!);
        foreach (var d in Rows("refMappingShopWithTab.txt"))
            if (d.Length >= 4)
                foreach (var shop in shopsByStore[d[2]])
                    shop.Groups.Add(new ShopGroup(d[3]));

        var tabsByGroup = Rows("refShopTab.txt").Where(d => d.Length >= 6).ToLookup(d => d[4], d => d[3]);
        foreach (var group in shops.SelectMany(s => s.Groups))
            group.Tabs.AddRange(tabsByGroup[group.Name].Select(name => new ShopTab(name)));

        var goodsByTab = Rows("refShopGoods.txt").Where(d => d.Length >= 5).ToLookup(d => d[2], d => (Package: d[3], Slot: d[4]));

        var packages = new Dictionary<string, (string Item, string Plus, string Durability, string Magic)>(StringComparer.Ordinal);
        foreach (var d in Rows("refScrapOfPackageItem.txt"))
        {
            if (d.Length < 8 || !byte.TryParse(d[7], CultureInfo.InvariantCulture, out byte magicCount) || d.Length < 8 + magicCount)
            {
                _skipped++;
                continue;
            }
            packages[d[2]] = (d[3], d[4], d[6], string.Join('|', d.AsSpan(8, magicCount).ToArray()));
        }

        Execute(db, "CREATE TABLE shops (model_servername VARCHAR(64), tab INTEGER, slot INTEGER, item_servername VARCHAR(64), plus INTEGER, durability INTEGER, magic_params VARCHAR(256), PRIMARY KEY (model_servername,tab,slot));");

        // The slot is the good's own SlotIndex (refShopGoods), as the game numbers the shop window and the server's
        // buy messages; counting the rows instead put the wrong items in the slots.
        var used = new HashSet<(string, int, int)>();
        IEnumerable<object?[]> ShopRows()
        {
            foreach (var shop in shops)
            {
                int tabIndex = 0;
                foreach (var group in shop.Groups)
                {
                    foreach (var tab in group.Tabs)
                    {
                        foreach (var good in goodsByTab[tab.Name])
                        {
                            if (!packages.TryGetValue(good.Package, out var item)
                                || !int.TryParse(good.Slot, CultureInfo.InvariantCulture, out int slot)
                                || !used.Add((shop.NpcName, tabIndex, slot)))
                                continue;
                            yield return [shop.NpcName, tabIndex, slot, item.Item, item.Plus, item.Durability, item.Magic];
                        }
                        tabIndex++;
                    }
                }
            }
        }
        Insert(db, "shops", ["model_servername", "tab", "slot", "item_servername", "plus", "durability", "magic_params"], ShopRows());
    }

    private Dictionary<string, string[]> LoadTeleportData()
    {
        var data = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var d in Rows("TeleportData.txt"))
            if (d.Length > 8)
                data[d[1]] = d;
        return data;
    }

    private void AddTeleportBuildings(SqliteConnection db)
    {
        Execute(db, "CREATE TABLE teleportbuildings (id INTEGER PRIMARY KEY, servername VARCHAR(64), name VARCHAR(64), tid1 INTEGER, tid2 INTEGER, tid3 INTEGER, tid4 INTEGER);");
        Insert(db, "teleportbuildings", ["id", "servername", "name", "tid1", "tid2", "tid3", "tid4"],
            Rows("TeleportBuilding.txt").Select(d => Row(d, 13, () =>
            {
                string name = Name(d[5]);
                return [d[1], d[2], name.Length == 0 ? d[2] : name, d[9], d[10], d[11], d[12]];
            })));
    }

    private void AddTeleportLinks(SqliteConnection db, Dictionary<string, string[]> teleports)
    {
        Execute(db, """
            CREATE TABLE teleportlinks (sourceid INTEGER, destinationid INTEGER, id INTEGER, servername VARCHAR(64), name VARCHAR(64),
              destination VARCHAR(64), tid1 INTEGER, tid2 INTEGER, tid3 INTEGER, tid4 INTEGER, gold INTEGER, level INTEGER,
              spawn_region INTEGER, spawn_x INTEGER, spawn_y INTEGER, spawn_z INTEGER, pos_region INTEGER, pos_x INTEGER, pos_y INTEGER, pos_z INTEGER,
              PRIMARY KEY (sourceid, destinationid));
            CREATE INDEX ix_teleportlinks_id ON teleportlinks(id);
            """);

        (string Name, string Tid1, string Tid2, string Tid3, string Tid4) Describe(string[] teleport)
        {
            if (uint.TryParse(teleport[3], CultureInfo.InvariantCulture, out uint modelId) && _models.TryGetValue(modelId, out var model))
                return (model.Name.Length == 0 ? teleport[2] : model.Name, "1", model.Tid2, model.Tid3, model.Tid4);
            // Teleports without a gate model
            string name = Name(teleport[4]);
            return (name.Length == 0 ? teleport[2] : name, "4", "0", "0", "0");
        }

        static int Int(string v) => int.Parse(v, CultureInfo.InvariantCulture);
        static ushort Region(string v) => (ushort)short.Parse(v, CultureInfo.InvariantCulture);

        Insert(db, "teleportlinks",
            ["sourceid", "destinationid", "id", "servername", "name", "destination", "tid1", "tid2", "tid3", "tid4", "gold", "level",
             "spawn_region", "spawn_x", "spawn_y", "spawn_z", "pos_region", "pos_x", "pos_y", "pos_z"],
            Rows("TeleportLink.txt").Select(d =>
            {
                if (d.Length < 9 || !teleports.TryGetValue(d[1], out var source) || !teleports.TryGetValue(d[2], out var target))
                {
                    _skipped++;
                    return null;
                }
                return Row(d, 9, () =>
                {
                    var src = Describe(source);
                    var dst = Describe(target);
                    return
                    [
                        d[1], d[2], source[3], source[2], src.Name, dst.Name, src.Tid1, src.Tid2, src.Tid3, src.Tid4, d[3], d[8],
                        Region(source[5]), Int(source[6]), Int(source[8]), Int(source[7]),
                        Region(target[5]), Int(target[6]), Int(target[8]), Int(target[7]),
                    ];
                });
            }));
    }

    private void AddRegions(SqliteConnection db)
    {
        Execute(db, "CREATE TABLE regions (id INTEGER PRIMARY KEY, name VARCHAR(64));");
        var regions = new Dictionary<uint, string>();
        foreach (var d in Rows("TextZoneName.txt"))
            if (d.Length > _languageIndex && d[_languageIndex] != "0" && uint.TryParse(d[1], CultureInfo.InvariantCulture, out uint id))
                regions[id] = d[_languageIndex];
        Insert(db, "regions", ["id", "name"], regions.Select(kv => new object?[] { kv.Key, kv.Value }));
    }

    #endregion

    #region SQL helpers

    /// <summary>Builds a row; malformed source lines (too short, bad numbers) are counted and skipped.</summary>
    private object?[]? Row(string[] data, int minLength, Func<object?[]> build)
    {
        if (data.Length < minLength)
        {
            _skipped++;
            return null;
        }
        try
        {
            return build();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
        {
            _skipped++;
            return null;
        }
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Inserts rows (later rows with the same key replace earlier ones) in one transaction.</summary>
    private void Insert(SqliteConnection db, string table, string[] columns, IEnumerable<object?[]?> rows)
    {
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder("INSERT OR REPLACE INTO ").Append(table).Append(" (").AppendJoin(',', columns).Append(") VALUES (");
        for (int i = 0; i < columns.Length; i++)
        {
            sql.Append(i == 0 ? "$p0" : $",$p{i}");
            command.Parameters.Add(new SqliteParameter($"$p{i}", null));
        }
        command.CommandText = sql.Append(')').ToString();
        command.Prepare();

        int count = 0;
        foreach (var row in rows)
        {
            if (row is null)
                continue;
            for (int i = 0; i < columns.Length; i++)
                command.Parameters[i].Value = row[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
            count++;
        }
        transaction.Commit();
        RowCounts[table] = count;
    }

    #endregion

    /// <summary>Column positions in SkillData rows.</summary>
    private static class Skill
    {
        public const int Id = 1, GroupId = 2, Code = 3, Group = 5, Level = 7, ChainCode = 9,
            PreparingTime = 11, CastingTime = 12, ActionDuration = 13, ReuseDelay = 14,
            TargetRequired = 22, Mastery1 = 34, MasteryLevel1 = 36,
            ReqSkill1 = 40, ReqSkill2 = 41, ReqSkillLevel1 = 43, ReqSkillLevel2 = 44, LearnSp = 46,
            UiTab = 57, UiPage = 58, UiColumn = 59, UiRow = 60,
            Weapon1 = 50, Weapon2 = 51, ConsumeMp = 53, IconFile = 61, UiName = 62, UiDescription = 64, Param1 = 68;
    }

    private sealed class Shop
    {
        public string StoreGroupName { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string NpcName { get; set; } = string.Empty;
        public List<ShopGroup> Groups { get; } = [];
    }

    private sealed record ShopGroup(string Name)
    {
        public List<ShopTab> Tabs { get; } = [];
    }

    private sealed record ShopTab(string Name);
}
