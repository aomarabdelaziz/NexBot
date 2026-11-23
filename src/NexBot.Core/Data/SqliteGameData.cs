using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Data.Sqlite;
using NexBot.Game;

namespace NexBot.Data;

/// <summary>
/// <see cref="IGameData"/> backed by the bot's <c>Database.sqlite3</c> file.
/// </summary>
/// <remarks>
/// <para>The legacy <c>DataManager</c> had several problems that this class fixes:</para>
/// <list type="bullet">
///   <item>Queries were built by string concatenation, open to SQL injection and broken by names containing
///   quotes. Every query here is parameterized.</item>
///   <item>Every entity spawn ran a query, separately for every bot. Rows are now cached in memory and shared by
///   all clients, so each ID is read from disk at most once per process.</item>
///   <item>It used <c>System.Data.SQLite</c>, which is Windows-centric. <c>Microsoft.Data.Sqlite</c> ships native
///   libraries for Linux, macOS and Windows.</item>
/// </list>
/// <para>The database is opened read-only and connections come from the provider's pool.</para>
/// </remarks>
public sealed class SqliteGameData : IGameData
{
    private readonly string _connectionString;

    private readonly ConcurrentDictionary<uint, ModelInfo?> _modelsById = new();
    private readonly ConcurrentDictionary<string, ModelInfo?> _modelsByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, ItemInfo?> _itemsById = new();
    private readonly ConcurrentDictionary<string, ItemInfo?> _itemsByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, SkillInfo?> _skillsById = new();
    private readonly ConcurrentDictionary<string, SkillInfo?> _skillsByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, MagicOptionInfo?> _magicOptions = new();
    private readonly ConcurrentDictionary<uint, MasteryInfo?> _masteries = new();
    private readonly ConcurrentDictionary<uint, TeleportInfo?> _teleports = new();
    private readonly ConcurrentDictionary<uint, IReadOnlyList<TeleportLinkInfo>> _teleportLinks = new();
    private readonly ConcurrentDictionary<(string, byte, byte), ShopItemInfo?> _shopItems = new();
    private readonly ConcurrentDictionary<ushort, string?> _regions = new();
    private readonly ConcurrentDictionary<string, string?> _uiTexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<WeaponType, uint> _commonAttacks = new();
    private readonly Lazy<FrozenDictionary<byte, LevelInfo>> _levels;
    private readonly Lazy<IReadOnlyList<MasteryInfo>> _allMasteries;
    private readonly Lazy<bool> _hasSkillTree;
    private readonly ConcurrentDictionary<uint, IReadOnlyList<SkillTreeInfo>> _skillTrees = new();

    public SqliteGameData(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException($"Game database not found: {databasePath}", databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        _levels = new Lazy<FrozenDictionary<byte, LevelInfo>>(LoadLevels, LazyThreadSafetyMode.ExecutionAndPublication);
        _allMasteries = new Lazy<IReadOnlyList<MasteryInfo>>(
            () => QueryMany("SELECT * FROM masteries WHERE $v=1 ORDER BY id", 1, ReadMastery), LazyThreadSafetyMode.ExecutionAndPublication);
        _hasSkillTree = new Lazy<bool>(
            () => QueryMany("SELECT name FROM pragma_table_info('skills') WHERE name=$v", "ui_column", r => r.String("name")).Count > 0,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DatabasePath => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    /// <summary>Opens the database once to fail fast on a corrupt or wrong file.</summary>
    public void Validate()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('models','items','skills')";
        long tables = (long)(command.ExecuteScalar() ?? 0L);
        if (tables < 3)
            throw new InvalidDataException($"'{DatabasePath}' does not look like a NexBot game database (missing models/items/skills tables).");
    }

    #region IGameData

    public ModelInfo? GetModel(uint id) =>
        _modelsById.GetOrAdd(id, static (key, self) => self.QuerySingle("SELECT * FROM models WHERE id=$v", key, ReadModel), this);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ModelInfo>> _modelsByPrefix = new(StringComparer.Ordinal);

    public IReadOnlyList<ModelInfo> GetModelsByPrefix(string prefix) =>
        _modelsByPrefix.GetOrAdd(prefix, static (key, self) =>
            self.QueryMany("SELECT * FROM models WHERE substr(servername, 1, length($v)) = $v ORDER BY id", key, ReadModel), this);

    public ModelInfo? GetModel(string serverName) =>
        _modelsByName.GetOrAdd(serverName, static (key, self) => self.QuerySingle("SELECT * FROM models WHERE servername=$v", key, ReadModel), this);

    public ItemInfo? GetItem(uint id) =>
        _itemsById.GetOrAdd(id, static (key, self) => self.QuerySingle("SELECT * FROM items WHERE id=$v", key, ReadItem), this);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<ItemInfo>> _itemsOfType = new();

    public IReadOnlyList<ItemInfo> GetItemsOfType(byte tid2, byte tid3) =>
        _itemsOfType.GetOrAdd(tid2 << 8 | tid3, static (key, self) =>
            self.QueryMany("SELECT * FROM items WHERE tid2*256+tid3=$v ORDER BY id", key, ReadItem), this);

    public ItemInfo? GetItem(string serverName) =>
        _itemsByName.GetOrAdd(serverName, static (key, self) => self.QuerySingle("SELECT * FROM items WHERE servername=$v", key, ReadItem), this);

    public IReadOnlyList<ItemInfo> SearchItems(string text, int limit = 20)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length < 2)
            return [];
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Exact server name first, then names starting with the text, then names containing it.
        command.CommandText = """
            SELECT * FROM items
            WHERE name LIKE $like ESCAPE '\' OR servername LIKE $like ESCAPE '\'
            ORDER BY CASE WHEN servername = $text THEN 0 WHEN name LIKE $prefix ESCAPE '\' THEN 1 ELSE 2 END, length(name), name
            LIMIT $limit
            """;
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$like", "%" + escaped + "%");
        command.Parameters.AddWithValue("$prefix", escaped + "%");
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var list = new List<ItemInfo>();
        while (reader.Read())
            list.Add(ReadItem(new Row(reader)));
        return list;
    }

    public IReadOnlyList<(ModelInfo Model, byte Level)> SearchMonsters(string text, int limit = 100)
    {
        text = text?.Trim() ?? string.Empty;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM models
            WHERE servername LIKE 'MOB\_%' ESCAPE '\' AND (name LIKE $like ESCAPE '\' OR servername LIKE $like ESCAPE '\')
            ORDER BY level, name
            LIMIT $limit
            """;
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$like", "%" + escaped + "%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var list = new List<(ModelInfo, byte)>();
        while (reader.Read())
        {
            var row = new Row(reader);
            list.Add((ReadModel(row), row.Byte("level")));
        }
        return list;
    }

    public SkillInfo? GetSkill(uint id) =>
        _skillsById.GetOrAdd(id, static (key, self) => self.QuerySingle("SELECT * FROM skills WHERE id=$v", key, ReadSkill), this);

    public SkillInfo? GetSkill(string serverName) =>
        _skillsByName.GetOrAdd(serverName, static (key, self) => self.QuerySingle("SELECT * FROM skills WHERE servername=$v", key, ReadSkill), this);

    public MagicOptionInfo? GetMagicOption(uint id) =>
        _magicOptions.GetOrAdd(id, static (key, self) => self.QuerySingle("SELECT * FROM magicoptions WHERE id=$v", key,
            r => new MagicOptionInfo(r.UInt32("id"), r.String("servername"), r.String("name"), r.UInt32("maxvalue"))), this);

    public MasteryInfo? GetMastery(uint id) =>
        _masteries.GetOrAdd(id, static (key, self) => self.QuerySingle("SELECT * FROM masteries WHERE id=$v", key, ReadMastery), this);

    public IReadOnlyList<MasteryInfo> GetMasteries() => _allMasteries.Value;

    public IReadOnlyList<SkillTreeInfo>? GetSkillTree(uint masteryId) =>
        !_hasSkillTree.Value ? null : _skillTrees.GetOrAdd(masteryId, static (key, self) => self.QueryMany(
            "SELECT * FROM skills WHERE mastery_id=$v AND ui_column < 255 AND sp > 0 ORDER BY ui_column, ui_row, skill_level", key,
            ReadSkillTree), this);

    public TeleportInfo? GetTeleport(uint id) =>
        _teleports.GetOrAdd(id, static (key, self) =>
            self.QuerySingle("SELECT * FROM teleportbuildings WHERE id=$v", key, ReadTeleport)
            ?? self.QuerySingle("SELECT * FROM teleportlinks WHERE id=$v", key, ReadTeleport), this);

    public IReadOnlyList<TeleportLinkInfo> GetTeleportLinks(uint sourceTeleportId) =>
        _teleportLinks.GetOrAdd(sourceTeleportId, static (key, self) =>
            self.QueryMany("SELECT * FROM teleportlinks WHERE id=$v", key,
                r => new TeleportLinkInfo(r.UInt32("id"), r.String("name"), r.UInt32("destinationid"), r.String("destination"), r.String("servername"))), this);

    public LevelInfo? GetLevel(byte level) => _levels.Value.GetValueOrDefault(level);

    public ShopItemInfo? GetShopItem(string npcServerName, byte tab, byte slot) =>
        _shopItems.GetOrAdd((npcServerName, tab, slot), static (key, self) =>
        {
            using var connection = self.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM shops WHERE model_servername=$npc AND tab=$tab AND slot=$slot LIMIT 1";
            command.Parameters.AddWithValue("$npc", key.Item1);
            command.Parameters.AddWithValue("$tab", key.Item2);
            command.Parameters.AddWithValue("$slot", key.Item3);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            var r = new Row(reader);
            return new ShopItemInfo(r.String("item_servername"), r.Byte("plus"), r.UInt32("durability"), r.String("magic_params"));
        }, this);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ShopSlotInfo>> _shopSlots = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModelInfo>? _traders;

    public IReadOnlyList<ShopSlotInfo> GetShopItems(string npcServerName) =>
        _shopSlots.GetOrAdd(npcServerName, static (key, self) =>
            self.QueryMany("SELECT tab, slot, item_servername FROM shops WHERE model_servername=$v ORDER BY tab, slot", key,
                r => new ShopSlotInfo(r.Byte("tab"), r.Byte("slot"), r.String("item_servername"))), this);

    private IReadOnlyList<(string Item, string Npc)>? _catalog;

    public IReadOnlyList<(string Item, string Npc)> GetShopCatalog() =>
        _catalog ??= QueryMany("SELECT DISTINCT item_servername, model_servername FROM shops WHERE model_servername LIKE $v ORDER BY item_servername", "%",
            r => (r.String("item_servername"), r.String("model_servername")));

    public IReadOnlyList<ModelInfo> GetSpecialtyTraders() =>
        _traders ??= QueryMany(
            "SELECT * FROM models WHERE servername IN (SELECT DISTINCT model_servername FROM shops WHERE model_servername LIKE $v) ORDER BY id",
            "%SPECIAL%", ReadModel);

    public string? GetRegionName(ushort regionId) =>
        _regions.GetOrAdd(regionId, static (key, self) =>
            self.QuerySingle("SELECT name FROM regions WHERE id=$v LIMIT 1", key, r => r.String("name")), this);

    public string? GetUiText(string serverName) =>
        _uiTexts.GetOrAdd(serverName, static (key, self) =>
            self.QuerySingle("SELECT text FROM textuisystem WHERE servername=$v LIMIT 1", key, r => r.String("text")), this);

    public uint GetCommonAttackSkillId(WeaponType weapon) =>
        _commonAttacks.GetOrAdd(weapon, static (key, self) =>
        {
            using var connection = self.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM skills WHERE (weapon_first=$w OR weapon_second=$w) AND group_name LIKE '%_BASE' LIMIT 1";
            command.Parameters.AddWithValue("$w", (byte)key);
            object? result = command.ExecuteScalar();
            return result is null or DBNull ? 0u : Convert.ToUInt32(result, CultureInfo.InvariantCulture);
        }, this);

    #endregion

    #region Mapping

    private static ModelInfo ReadModel(Row r) =>
        new(r.UInt32("id"), r.String("servername"), r.String("name"), r.Byte("tid2"), r.Byte("tid3"), r.Byte("tid4"), r.UInt32("hp"),
            r.String("icon")); // missing in databases built before 2.10 (empty)

    public ShopGoodInfo? GetShopGood(uint groupId, byte groupIndex, byte tabIndex, byte slot)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_servername, plus, data FROM shopgoods WHERE group_id=$g AND group_index=$i AND tab_index=$t AND slot=$s LIMIT 1";
            command.Parameters.AddWithValue("$g", groupId);
            command.Parameters.AddWithValue("$i", groupIndex);
            command.Parameters.AddWithValue("$t", tabIndex);
            command.Parameters.AddWithValue("$s", slot);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new ShopGoodInfo(reader.GetString(0), (byte)Math.Clamp(reader.GetInt64(1), 0, 255), (int)Math.Clamp(reader.GetInt64(2), 0, int.MaxValue));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null; // no shopgoods table: a database built before it
        }
    }

    private static ItemInfo ReadItem(Row r) =>
        new(r.UInt32("id"), r.String("servername"), r.String("name"), r.Byte("tid2"), r.Byte("tid3"), r.Byte("tid4"),
            r.Byte("cashitem"), r.Byte("bionic"), r.String("icon"), r.UInt16("stack"), r.Byte("level"),
            r.String("description"), r.String("params"), r.String("equip")); // empty in databases built before these columns

    private static SkillInfo ReadSkill(Row r) =>
        new(r.UInt32("id"), r.String("servername"), r.String("name"), r.UInt32("group_id"), r.String("group_name"),
            r.UInt32("cooldown"), r.UInt32("duration"), r.Int32("casttime"), r.UInt32("mana"), r.UInt32("target_required") == 1,
            r.Byte("level"), r.String("params"), r.String("icon"), (WeaponType)r.Byte("weapon_first"), (WeaponType)r.Byte("weapon_second"),
            r.Byte("skill_level")); // missing in databases built before the skill window (0)

    private static SkillTreeInfo ReadSkillTree(Row r) =>
        new(r.UInt32("id"), r.String("servername"), r.String("name"), r.String("group_name"), r.Byte("skill_level"), r.UInt32("mastery_id"),
            r.Byte("level"), r.UInt32("sp"), r.String("icon"), r.Byte("ui_column"), r.Byte("ui_row"),
            r.UInt32("req_skill1"), r.Byte("req_skill_level1"), r.UInt32("req_skill2"), r.Byte("req_skill_level2"), r.UInt32("group_id"));

    private static MasteryInfo ReadMastery(Row r)
    {
        // Mastery icons are stored with the icon\ folder (icon\skillmastery\...); items and skills without it.
        string icon = r.String("icon");
        if (icon.StartsWith("icon\\", StringComparison.OrdinalIgnoreCase))
            icon = icon[5..];
        return new MasteryInfo(r.UInt32("id"), r.String("name"), r.String("description"), r.String("type"), r.Byte("tab"), icon);
    }

    private static TeleportInfo ReadTeleport(Row r) =>
        new(r.UInt32("id"), r.String("servername"), r.String("name"), r.Byte("tid2"), r.Byte("tid3"), r.Byte("tid4"));

    private FrozenDictionary<byte, LevelInfo> LoadLevels()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM leveldata";
        using var reader = command.ExecuteReader();
        var levels = new Dictionary<byte, LevelInfo>();
        while (reader.Read())
        {
            var r = new Row(reader);
            var info = new LevelInfo(r.Byte("level"), r.UInt64("player"), r.UInt64("pet"), r.UInt32("trader"), r.UInt32("thief"), r.UInt32("hunter"),
                r.UInt32("sp"));
            levels[info.Level] = info;
        }
        return levels.ToFrozenDictionary();
    }

    #endregion

    #region Query helpers

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private T? QuerySingle<TKey, T>(string sql, TKey value, Func<Row, T> map) where T : class
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$v", value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? map(new Row(reader)) : null;
    }

    private List<T> QueryMany<TKey, T>(string sql, TKey value, Func<Row, T> map)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$v", value);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
            list.Add(map(new Row(reader)));
        return list;
    }

    /// <summary>Reads columns by name, accepting INTEGER or TEXT storage (the bot DB stores numbers either way).</summary>
    private readonly struct Row(SqliteDataReader reader)
    {
        private object? Value(string column)
        {
            int ordinal;
            try { ordinal = reader.GetOrdinal(column); }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { return null; }
            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        public string String(string column) => Convert.ToString(Value(column), CultureInfo.InvariantCulture) ?? string.Empty;
        public byte Byte(string column) => (byte)Number(column);
        public ushort UInt16(string column) => (ushort)Number(column);
        public uint UInt32(string column) => (uint)Number(column);
        public int Int32(string column) => (int)Number(column);
        public ulong UInt64(string column) => Value(column) switch
        {
            null => 0,
            string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) => u,
            string => 0,
            var v => Convert.ToUInt64(v, CultureInfo.InvariantCulture),
        };

        private long Number(string column) => Value(column) switch
        {
            null => 0,
            long l => l,
            double d => (long)d,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            string => 0,
            var v => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        };
    }

    #endregion
}
