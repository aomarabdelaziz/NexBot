using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexBot.Media;
using NexBot.Network;

namespace NexBot.Configuration;

/// <summary>
/// Everything the user sets in the window, saved to <c>settings.json</c> next to the exe.
/// </summary>
/// <remarks>The password is stored encrypted with DPAPI (only this Windows user can read it).</remarks>
[SupportedOSPlatform("windows")]
public sealed class BotSettings
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Client
    public string ClientPath { get; set; } = "";
    public string Executable { get; set; } = "sro_client.exe";

    /// <summary>pk2 Blowfish password; empty = the vSRO default (169841).</summary>
    public string Pk2Key { get; set; } = "";

    public int DivisionIndex { get; set; }
    public int GatewayIndex { get; set; }

    /// <summary>Windows code page of packet strings (1252 western, 1256 arabic, 949 korean ...).</summary>
    public int CodePage { get; set; } = 1252;

    /// <summary>Local proxy port, 0 = any free port.</summary>
    public ushort ProxyPort { get; set; }

    /// <summary>Game database built from Media.pk2 (relative to the exe folder).</summary>
    public string DatabasePath { get; set; } = "Database.sqlite3";

    // Optional overrides of what Media.pk2 says (null / empty = use Media.pk2).
    public string GatewayHostOverride { get; set; } = "";
    public ushort? GatewayPortOverride { get; set; }
    public uint? VersionOverride { get; set; }
    public byte? LocaleOverride { get; set; }

    // Account
    public string Username { get; set; } = "";

    /// <summary>DPAPI-protected password (base64).</summary>
    public string ProtectedPassword { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => Unprotect(ProtectedPassword);
        set => ProtectedPassword = Protect(value);
    }

    public string ShardName { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public string Captcha { get; set; } = "";
    public bool AutoLogin { get; set; } = true;
    public bool AutoSelectCharacter { get; set; } = true;
    public bool ClientlessWhenClientCloses { get; set; } = true;

    /// <summary>What the Log page shows: Normal, Warnings, Packets, Debug or All.</summary>
    public string LogFilter { get; set; } = "Normal";

    /// <summary>Automatic mastery raise and skill learning (Character page, skill window).</summary>
    public AutoLevelSettings AutoLevel { get; set; } = new();

    /// <summary>Auto trade: preferred transport and job suit.</summary>
    public TradeSettings Trade { get; set; } = new();

    /// <summary>Party, guild, academy and tracing automation.</summary>
    public SocialSettings Social { get; set; } = new();

    /// <summary>The training area set on the map.</summary>
    public TrainingAreaSettings Training { get; set; } = new();

    /// <summary>Sounds for game events.</summary>
    public SoundSettings Sound { get; set; } = new();

    /// <summary>Stall prices and options.</summary>
    public StallSettings Stall { get; set; } = new();

    /// <summary>Alchemy options and the learned packet layout.</summary>
    public AlchemySettings Alchemy { get; set; } = new();

    /// <summary>If / Then rules.</summary>
    public ConditionSettings Conditions { get; set; } = new();

    /// <summary>Potions, returning to town, stats and disconnect rules.</summary>
    public ProtectionSettings Protection { get; set; } = new();

    /// <summary>Town buy list, town scripts and options.</summary>
    public TownSettings Town { get; set; } = new();

    /// <summary>The attack pet (Pet page).</summary>
    public PetSettings Pet { get; set; } = new();

    /// <summary>The pick filter: what to pick up, store, sell and drop, and the gold to keep.</summary>
    public PickFilterSettings PickFilter { get; set; } = new();

    /// <summary>The Attack tab: skills per monster type, imbues, attack options and buffs.</summary>
    public AttackSettings Attack { get; set; } = new();

    /// <summary>The training area list, conditions and options (the enabled area is copied into <see cref="Training"/>).</summary>
    public TrainingSetupSettings TrainingSetup { get; set; } = new();

    /// <summary>
    /// Settings saved before the training area list: the area in use becomes the list's first area ("Training"),
    /// with the town loop's training script.
    /// </summary>
    public void EnsureTrainingAreas()
    {
        if (TrainingSetup.Areas.Count > 0 || !(Training.IsSet || Training.HasPolygon))
            return;
        var first = new TrainingAreaSettings();
        first.CopyFrom(Training);
        first.Name = Training.Name.Length > 0 ? Training.Name : "Training";
        first.Script = Town.TrainingScript;
        TrainingSetup.Areas.Add(first);
        TrainingSetup.Enabled = first.Name;
    }

    /// <summary>Enables a training area of the list: it becomes <see cref="Training"/>, its script the town loop's.</summary>
    public bool EnableTrainingArea(string name)
    {
        if (TrainingSetup.Find(name) is not { } area)
            return false;
        Training.CopyFrom(area);
        Town.TrainingScript = area.Script;
        TrainingSetup.Enabled = area.Name;
        return true;
    }

    /// <summary>The enabled area was changed (on the map, or Train here): keep its list entry the same.</summary>
    public void SaveTrainingToList()
    {
        if (TrainingSetup.Find(TrainingSetup.Enabled) is { } area)
        {
            string name = area.Name, script = area.Script;
            int pick = area.PickRadius;
            area.CopyFrom(Training);
            (area.Name, area.Script, area.PickRadius) = (name, script, pick);
        }
        else
        {
            EnsureTrainingAreas();
        }
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public string ResolveDatabasePath() =>
        Path.IsPathRooted(DatabasePath) ? DatabasePath : Path.Combine(AppContext.BaseDirectory, DatabasePath);

    public static BotSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new BotSettings();
        try
        {
            return JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(path), s_json) ?? new BotSettings();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".broken", overwrite: true);
            return new BotSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, s_json));
        File.Move(temp, path, overwrite: true);
    }

    public AccountProfile ToAccount() => new()
    {
        Username = Username.Trim(),
        Password = Password,
        ShardName = ShardName.Trim(),
        CharacterName = CharacterName.Trim(),
        Captcha = Captcha,
        AutoLogin = AutoLogin,
        AutoSelectCharacter = AutoSelectCharacter,
        ClientlessWhenClientCloses = ClientlessWhenClientCloses,
    };

    /// <summary>Builds the server profile: the selected gateway first, then the other gateways of the division.</summary>
    public ServerProfile ToServer(ClientInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        List<string> hosts;
        if (!string.IsNullOrWhiteSpace(GatewayHostOverride))
        {
            hosts = [GatewayHostOverride.Trim()];
        }
        else
        {
            var division = info.Divisions.Count > 0
                ? info.Divisions[Math.Clamp(DivisionIndex, 0, info.Divisions.Count - 1)]
                : new DivisionInfo("", info.Gateways);
            hosts = [.. division.Gateways];
            int index = Math.Clamp(GatewayIndex, 0, Math.Max(0, hosts.Count - 1));
            if (index > 0)
            {
                string selected = hosts[index];
                hosts.RemoveAt(index);
                hosts.Insert(0, selected);
            }
        }

        return new ServerProfile(
            hosts,
            GatewayPortOverride ?? info.GatewayPort,
            VersionOverride ?? info.Version,
            LocaleOverride ?? info.Locale);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // saved by another Windows user or edited by hand
        }
    }
}
