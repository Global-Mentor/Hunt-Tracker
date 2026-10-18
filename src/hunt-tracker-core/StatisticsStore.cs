using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HuntTracker;

public sealed class StatisticsStore(string root)
{
    private readonly Dictionary<string, (Match Match, long Revision, string Reference)> cache = [];
    private string cachedSession = "";
    public string RecoveryWarning { get; private set; } = "";
    public string DataFolder => Path.Combine(root, "data");
    private string RecoveryFile => Path.Combine(root, "_runtime/statistics-recovery.json");
    public string CurrentFile => Path.Combine(DataFolder, "statistics.json");
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public Statistics Load(bool boundedHistory = false)
    {
        RecoveryWarning = "";
        Statistics? recovery = null;
        Exception? recoveryError = null;
        if (File.Exists(RecoveryFile))
            try { recovery = Read(RecoveryFile, boundedHistory: boundedHistory); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                recoveryError = error;
            }
        Statistics? stats = null;
        if (File.Exists(CurrentFile))
            try
            {
                stats = Read(CurrentFile, boundedHistory: boundedHistory);
            }
            catch (InvalidDataException) when (recovery is not null) { }
        if (recovery is not null && (stats is null || recovery.Id == stats.Id && recovery.WriteRevision > stats.WriteRevision))
            stats = recovery;
        if (stats is null && recoveryError is not null)
            throw new InvalidDataException("Резервная статистика недоступна. Исходный файл сохранён без изменений.", recoveryError);
        if (recoveryError is not null)
            RecoveryWarning = "Резервная копия недоступна. Загружена основная статистика, резервный файл сохранён без изменений.";
        stats ??= new Statistics();
        stats.Complete("tracker_restarted");
        return stats;
    }

    public static Statistics Read(string file, bool metadataOnly = false, bool boundedHistory = false)
    {
        try
        {
            var node = JsonNode.Parse(AtomicFile.Read(file)) as JsonObject ?? throw new InvalidDataException();
            foreach (var key in new[] { "id", "started_at", "current", "maps", "recent", "kills", "deaths", "games", "survived", "lost", "unresolved" })
                if (!node.ContainsKey(key))
                    throw new InvalidDataException("Неполная статистика.");
            var version = node["version"]!.GetValue<int>();
            if (version is not (1 or 2 or 3) || node["recent"] is not JsonArray recent)
                throw new InvalidDataException();
            var id = node["id"]!.GetValue<string>();
            Guid.ParseExact(id, "N");
            var loaded = new JsonArray();
            if (version is 2 or 3)
            {
                var folder = Path.GetDirectoryName(file)!;
                if (Path.GetFileName(folder) == "sessions")
                    folder = Path.GetDirectoryName(folder)!;
                var references = new HashSet<string>();
                foreach (var item in recent)
                {
                    var reference = item!.GetValue<string>();
                    if (!Regex.IsMatch(reference, "^matches/[a-f0-9]{32}/[a-f0-9]{32}-[a-f0-9]{64}\\.json$") || reference.Split('/')[1] != id || !references.Add(reference))
                        throw new InvalidDataException("Некорректная ссылка на матч.");
                    if (metadataOnly)
                        continue;
                    var json = AtomicFile.Read(Path.Combine(folder, reference));
                    if (!reference.EndsWith("-" + Hash(json) + ".json", StringComparison.Ordinal))
                        throw new InvalidDataException("Повреждён файл матча.");
                    var match = JsonNode.Parse(json)!;
                    if (match["id"]!.GetValue<string>() != reference.Split('/')[2][..32])
                        throw new InvalidDataException();
                    loaded.Add(match);
                }
                node["recent"] = loaded;
            }
            if (metadataOnly)
                node["recent"] = new JsonArray();
            node["version"] = 1;
            foreach (var match in node["recent"]!.AsArray().Concat(new[] { node["current"] }))
            {
                if (match is null)
                    continue;
                foreach (var key in new[] { "id", "map", "kills", "deaths", "started_at", "status", "outcome", "loaded", "full", "death_open", "ended_at", "reason" })
                    if (match[key] is null)
                        throw new InvalidDataException("Неполный матч.");
            }
            var stats = node.Deserialize<Statistics>(Statistics.Json) ?? throw new InvalidDataException();
            if (stats.WriteRevision < 0 || new[] { stats.Kills, stats.Deaths, stats.Trades, stats.Games, stats.Survived, stats.Lost, stats.Unresolved }.Any(n => n < 0))
                throw new InvalidDataException();
            DateTimeOffset.Parse(stats.StartedAt);
            if (stats.Maps is null || Match.Names.Keys.Any(k => !stats.Maps.ContainsKey(k)) || stats.Maps.Values.Any(n => n < 0))
                throw new InvalidDataException();
            var ids = new HashSet<string>();
            foreach (var match in stats.Recent)
                if (match is null || match.Status != "complete" || !ids.Add(match.Id))
                    throw new InvalidDataException("Повторяющийся или незавершённый матч в архиве.");
            foreach (var match in stats.Recent.Concat(stats.Current is null ? [] : new[] { stats.Current }))
            {
                ValidateMatch(match);
                if (match.Outcome == "unknown")
                    match.Outcome = "lost";
            }
            HistoryBlocks.ValidateReference(stats.Id, stats.HistoryReference, stats.HistoryCount);
            if (!metadataOnly)
            {
                if (stats.Recent.Any(m => m.Number == 0))
                    for (var i = 0; i < stats.Recent.Count; i++)
                        stats.Recent[i].Number = checked(stats.HistoryCount + i + 1);
                stats.Recent.Sort((a, b) => a.Number.CompareTo(b.Number));
                if (stats.Current is { Status: not "complete", Number: 0 })
                    stats.Current.Number = checked(stats.HistoryCount + stats.Recent.Count + 1);
                if (stats.Recent.Where((m, i) => m.Number <= stats.HistoryCount || i > 0 && m.Number <= stats.Recent[i - 1].Number).Any())
                    throw new InvalidDataException("Нарушен порядок матчей.");
            }
            if (!metadataOnly && !boundedHistory && stats.HistoryCount > 0)
            {
                stats.Recent = HistoryBlocks.Enumerate(stats, DataDirectory(file))
                    .Where(m => m.Status == "complete").OrderBy(m => m.Number).ToList();
                stats.HistoryReference = "";
                stats.HistoryCount = 0;
            }
            if (stats.Current is { Status: "complete" } current)
            {
                var index = stats.Recent.FindLastIndex(m => m.Id == current.Id);
                if (index >= 0)
                {
                    current.Number = stats.Recent[index].Number;
                    stats.Recent[index] = current;
                }
            }
            stats.Lost = checked(stats.Lost + stats.Unresolved);
            stats.Unresolved = 0;
            return stats;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new InvalidDataException("Не удалось прочитать статистику. Исходные файлы сохранены без изменений.", e);
        }
    }

    public static string DataDirectory(string file)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (Path.GetFileName(folder) == "sessions") return Path.GetDirectoryName(folder)!;
        if (Path.GetFileName(folder) == "_runtime") return Path.Combine(Path.GetDirectoryName(folder)!, "data");
        return folder;
    }

    public static void ValidateMatch(Match? match)
    {
        if (match is null || !Guid.TryParseExact(match.Id, "N", out _) ||
            !DateTimeOffset.TryParse(match.StartedAt, out _) || match.Map is null || match.Reason is null || match.Map.Length > 128 || match.Reason.Length > 128 ||
            match.Status is not ("loading" or "active" or "complete") || match.Kills < 0 || match.Deaths < 0 || match.Number < 0 ||
            match.CombatEvents is null || match.CombatEvents.Any(e => e is not ("kill" or "death")) ||
            match.Status == "complete" && match.Outcome is not ("survived" or "lost" or "unknown"))
            throw new InvalidDataException("Некорректные данные матча.");
    }

    public void Save(Statistics stats)
    {
        SaveSnapshot(stats, CurrentFile);
        RemoveRecovery(stats);
    }

    public void SaveSnapshot(Statistics stats, string file)
    {
        if (cachedSession != stats.Id)
        {
            cache.Clear();
            cachedSession = stats.Id;
        }
        Directory.CreateDirectory(Path.Combine(DataFolder, "matches", stats.Id));
        var sealedCount = stats.Recent.Count > HistoryBlocks.HotHistoryLimit
            ? (stats.Recent.Count - HistoryBlocks.BlockSize) / HistoryBlocks.BlockSize * HistoryBlocks.BlockSize : 0;
        var references = new List<string>();
        foreach (var match in stats.Recent.Skip(sealedCount))
        {
            if (!cache.TryGetValue(match.Id, out var saved) || !ReferenceEquals(saved.Match, match) || saved.Revision != match.Revision)
            {
                var json = JsonSerializer.Serialize(match, Statistics.Json);
                var reference = $"matches/{stats.Id}/{match.Id}-{Hash(json)}.json";
                var path = Path.Combine(DataFolder, reference);
                if (!File.Exists(path))
                    AtomicFile.Write(path, json);
                saved = (match, match.Revision, reference);
                cache[match.Id] = saved;
            }
            references.Add(saved.Reference);
        }
        var historyReference = stats.HistoryReference;
        var historyCount = stats.HistoryCount;
        for (var offset = 0; offset < sealedCount; offset += HistoryBlocks.BlockSize)
        {
            historyCount = checked(historyCount + HistoryBlocks.BlockSize);
            historyReference = HistoryBlocks.Write(DataFolder, stats.Id, historyCount, historyReference,
                stats.Recent.Skip(offset).Take(HistoryBlocks.BlockSize));
        }
        var snapshot = JsonSerializer.SerializeToNode(stats.Overlay(), Statistics.Json)!.AsObject();
        snapshot["version"] = historyCount > 0 ? 3 : 2;
        snapshot["history_reference"] = historyReference;
        snapshot["history_count"] = historyCount;
        snapshot["write_revision"] = checked(stats.WriteRevision + 1);
        snapshot["unresolved"] = 0;
        snapshot["recent"] = JsonSerializer.SerializeToNode(references);
        JsonObject? previous = null;
        if (File.Exists(file))
        {
            var text = AtomicFile.Read(file);
            try
            {
                previous = JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
                if (previous["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var version) || version is not (1 or 2 or 3) ||
                    previous["id"] is not JsonValue idNode || !idNode.TryGetValue<string>(out var id) || !Guid.TryParseExact(id, "N", out _) ||
                    previous["recent"] is not JsonArray)
                    throw new JsonException();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                var folder = Path.Combine(DataFolder, "recovery");
                Directory.CreateDirectory(folder);
                AtomicFile.Write(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), text);
                previous = null;
            }
            if (previous?["version"]?.GetValue<int>() == 1)
            {
                var folder = Path.Combine(DataFolder, "migrations");
                Directory.CreateDirectory(folder);
                var backup = Path.Combine(folder, Guid.ParseExact(previous["id"]!.GetValue<string>(), "N").ToString("N") + ".json");
                if (!File.Exists(backup))
                    AtomicFile.Write(backup, text);
            }
        }
        AtomicFile.Write(file, snapshot.ToJsonString());
        stats.WriteRevision++;
        stats.HistoryReference = historyReference;
        stats.HistoryCount = historyCount;
        foreach (var match in stats.Recent.Take(sealedCount)) cache.Remove(match.Id);
        stats.Recent.RemoveRange(0, sealedCount);
        try
        {
            if (previous?["version"]?.GetValue<int>() is not (2 or 3) || previous["id"]?.GetValue<string>() != stats.Id)
                return;
            var retained = references.ToHashSet();
            var other = Path.GetFileName(Path.GetDirectoryName(file)) == "sessions" ? CurrentFile : Path.Combine(DataFolder, "sessions", stats.Id + ".json");
            if (File.Exists(other) && JsonNode.Parse(AtomicFile.Read(other)) is JsonObject otherNode && otherNode["version"]?.GetValue<int>() is 2 or 3)
                foreach (var item in otherNode["recent"]!.AsArray())
                    retained.Add(item!.GetValue<string>());
            foreach (var item in previous["recent"]!.AsArray())
            {
                var reference = item!.GetValue<string>();
                if (Regex.IsMatch(reference, $"^matches/{stats.Id}/[a-f0-9]{{32}}-[a-f0-9]{{64}}\\.json$") && !retained.Contains(reference))
                    File.Delete(Path.Combine(DataFolder, reference));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    public void SaveRecovery(Statistics stats)
    {
        var snapshot = JsonSerializer.SerializeToNode(stats.Copy(includeHistory: false), Statistics.Json)!.AsObject();
        snapshot.Remove("recent");
        snapshot["version"] = 1;
        snapshot["history_reference"] = "";
        snapshot["history_count"] = 0;
        snapshot["write_revision"] = checked(stats.WriteRevision + 1);
        Directory.CreateDirectory(Path.GetDirectoryName(RecoveryFile)!);
        AtomicFile.Write(RecoveryFile, stream =>
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            foreach (var property in snapshot)
            {
                writer.WritePropertyName(property.Key);
                property.Value?.WriteTo(writer);
                if (property.Value is null) writer.WriteNullValue();
            }
            writer.WriteStartArray("recent");
            foreach (var match in HistoryBlocks.Enumerate(stats, DataFolder).Where(m => m.Status == "complete"))
                JsonSerializer.Serialize(writer, match, Statistics.Json);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
    private void RemoveRecovery(Statistics stats)
    {
        if (!File.Exists(RecoveryFile))
            return;
        try
        {
            var recovery = Read(RecoveryFile, true);
            if (recovery.Id == stats.Id && recovery.WriteRevision <= stats.WriteRevision)
                File.Delete(RecoveryFile);
        }
        catch (IOException) { }
        catch (InvalidDataException) { }
        catch (UnauthorizedAccessException) { }
    }
    public Statistics NewSession(Statistics stats)
    {
        var archived = stats.Copy();
        archived.Complete("new_session");
        Directory.CreateDirectory(Path.Combine(DataFolder, "sessions"));
        SaveSnapshot(archived, Path.Combine(DataFolder, "sessions", archived.Id + ".json"));
        var next = new Statistics();
        Save(next);
        RemoveRecovery(archived);
        return next;
    }
}
