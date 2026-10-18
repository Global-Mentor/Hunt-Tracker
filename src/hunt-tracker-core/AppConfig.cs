using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace HuntTracker;

public sealed record DisplayConfig
{
    public int Opacity { get; set; } = 90;
    public string[] Fields { get; set; } = ["kills", "deaths", "kd", "games"];
}

public sealed record AppConfig
{
    public string GameFolder { get; set; } = "AUTO";
    public string NvidiaLogs { get; set; } = "%LOCALAPPDATA%/NVIDIA Corporation/GfeSDK";
    public double Volume { get; set; } = .5;
    public int DurationMs { get; set; } = 1800;
    public string AlertMode { get; set; } = "kills";
    public DisplayConfig Display { get; set; } = new();
    public bool AlertsEnabled { get; set; } = true;
    public bool StatisticsEnabled { get; set; } = true;
    public bool DetectTrades { get; set; } = true;
    public bool AutoRefreshMedia { get; set; } = true;
    public bool DetailedDiagnostics { get; set; }
    public int StatisticsRefreshMs { get; set; } = 1000;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static readonly string[] FieldNames = ["kills", "deaths", "kd", "games", "survival", "maps", "current"];

    public static AppConfig Load(string root)
    {
        var path = Path.Combine(root, "config.json");
        if (!File.Exists(path))
            AtomicFile.Write(path, AtomicFile.Read(Path.Combine(root, "config.example.json")));
        return Parse(AtomicFile.Read(path, 16384));
    }

    public static AppConfig Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("config.json должен содержать объект.");
        foreach (var key in new[] { "gameFolder", "nvidiaLogs", "durationMs" })
            if (!node.ContainsKey(key))
                throw new InvalidDataException($"Отсутствует параметр {key}.");
        if (node["volume"] is null || node["volume"] is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
            node["volume"] = .5;
        if (!node.ContainsKey("detectTrades"))
            node["detectTrades"] = true;
        if (!node.ContainsKey("display"))
            node["display"] = JsonSerializer.SerializeToNode(new DisplayConfig { Fields = [.. FieldNames] }, Json);
        if (node["display"] is JsonObject display)
        {
            foreach (var obsolete in new[] { "alerts", "stats", "scale" })
                display.Remove(obsolete);
            if (display["fields"] is not JsonArray fields)
                throw new InvalidDataException("display.fields должен быть массивом.");
            display["fields"] = new JsonArray(fields.Select(f => f?.GetValue<string>()).Where(f => f != "unknown").Distinct().Select(f => JsonValue.Create(f)).ToArray<JsonNode?>());
        }
        var config = node.Deserialize<AppConfig>(Json) ?? throw new InvalidDataException("Пустые настройки.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GameFolder) || string.IsNullOrWhiteSpace(NvidiaLogs))
            throw new InvalidDataException("Укажите папки игры и логов NVIDIA.");
        GameFolder = GameFolder.Trim();
        NvidiaLogs = NvidiaLogs.Trim();
        if (!double.IsFinite(Volume) || Volume is < 0 or > 1)
            throw new InvalidDataException("Громкость: от 0 до 100%.");
        if (DurationMs is < 300 or > 10000)
            throw new InvalidDataException("Длительность: от 300 до 10000 мс.");
        if (AlertMode is not ("kills" or "kills-deaths" or "trades"))
            throw new InvalidDataException("Неизвестный режим алертов.");
        if (StatisticsRefreshMs is not (1000 or 5000))
            throw new InvalidDataException("Обновление статистики: 1000 или 5000 мс.");
        if (Display is null || Display.Opacity is < 0 or > 100 || Display.Fields is null || Display.Fields.Any(f => !FieldNames.Contains(f)))
            throw new InvalidDataException("Некорректные поля виджета.");
        Display.Fields = Display.Fields.Distinct().ToArray();
    }

    public AppConfig Copy() => this with { Display = Display with { Fields = [.. Display.Fields] } };
    public void Save(string root)
    {
        Validate();
        AtomicFile.Write(Path.Combine(root, "config.json"), JsonSerializer.Serialize(this, Json));
    }
    public static string Resolve(string value, string root) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), root);

    public static string FindGame()
    {
        var roots = new List<string>();
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            if (Registry.GetValue(key, key.Contains("CURRENT_USER") ? "SteamPath" : "InstallPath", null) is string value)
                roots.Add(value);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            var path = Path.Combine(root, "steamapps/libraryfolders.vdf");
            if (!File.Exists(path))
                continue;
            try
            {
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(AtomicFile.Read(path), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (IOException) { }
        }
        return libraries.Select(l => Path.Combine(l, "steamapps/common/Hunt Showdown 1896")).FirstOrDefault(Directory.Exists)
            ?? Path.Combine(roots[^1], "steamapps/common/Hunt Showdown 1896");
    }
}
