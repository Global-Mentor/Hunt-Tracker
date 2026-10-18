using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HuntTracker;

public sealed class Match
{
    public const int CombatHistoryLimit = 256;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public string Status { get; set; } = "loading";
    public string Map { get; set; } = "";
    public bool Loaded { get; set; }
    public bool Full { get; set; }
    public bool DeathOpen { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public List<string> CombatEvents { get; set; } = [];
    public string StartedAt { get; set; } = DateTimeOffset.Now.ToString("o");
    public string EndedAt { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Reason { get; set; } = "";
    [JsonIgnore]
    public long Revision { get; set; }
    [JsonIgnore] public string MapName => Names.GetValueOrDefault(Map, string.IsNullOrEmpty(Map) ? "Неизвестная карта" : Map);
    [JsonIgnore] public string Result => Status != "complete" ? "В игре" : Outcome == "survived" ? "Выжил" : "Не выжил";
    [JsonIgnore] public DateTimeOffset Started => DateTimeOffset.Parse(StartedAt);
    public static readonly Dictionary<string, string> Names = new() { ["cemetery"] = "Stillwater Bayou", ["civilwar"] = "Lawson Delta", ["creek"] = "DeSalle", ["colorado"] = "Mammon Gulch" };

    public Match Copy()
    {
        var copy = (Match)MemberwiseClone();
        copy.CombatEvents = [.. CombatEvents];
        return copy;
    }
}

public sealed partial class Statistics
{
    public int Version { get; set; } = 1;
    public long WriteRevision { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StartedAt { get; set; } = DateTimeOffset.Now.ToString("o");
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Trades { get; set; }
    public int Games { get; set; }
    public int Survived { get; set; }
    public int Lost { get; set; }
    public int Unresolved { get; set; }
    public Dictionary<string, int> Maps { get; set; } = new() { ["cemetery"] = 0, ["civilwar"] = 0, ["creek"] = 0, ["colorado"] = 0 };
    public Match? Current { get; set; }
    public List<Match> Recent { get; set; } = [];
    public string HistoryReference { get; set; } = "";
    public int HistoryCount { get; set; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    [JsonIgnore] public string Kd => Deaths > 0 ? ((double)Kills / Deaths).ToString("0.00") : Kills > 0 ? "∞" : "0.00";
    [JsonIgnore] public string Survival => (long)Survived + Lost > 0 ? Math.Round(100d * Survived / ((long)Survived + Lost), MidpointRounding.AwayFromZero) + "%" : "Нет данных";

    public Statistics Copy(bool includeHistory = true)
    {
        var copy = (Statistics)MemberwiseClone();
        copy.Maps = new(Maps);
        copy.Recent = includeHistory ? Recent.Select(match => match.Copy()).ToList() : [];
        copy.Current = Current is null ? null : copy.Recent.LastOrDefault(match => match.Id == Current.Id) ?? Current.Copy();
        return copy;
    }
    public void Complete(string reason)
    {
        if (Current is not { Status: not "complete" } match)
            return;
        match.Status = "complete";
        match.EndedAt = DateTimeOffset.Now.ToString("o");
        match.Reason = reason;
        match.Revision++;
        if (match.Loaded && match.Full && reason == "MissionRequested" && !match.DeathOpen)
        {
            match.Outcome = "survived";
            Survived++;
        }
        else
        {
            match.Outcome = "lost";
            Lost++;
        }
        Recent.Add(match);
    }
    public void Start(bool full)
    {
        Complete("next_match");
        Games++;
        Current = new Match { Full = full, Number = Games };
    }
    public void Combat(string kind, string? matchId)
    {
        if (kind is not ("kill" or "death"))
            return;
        if (kind == "kill")
            Kills++;
        else
            Deaths++;
        var match = Current?.Id == matchId ? Current : Recent.LastOrDefault(m => m.Id == matchId);
        if (match is null)
            return;
        if (kind == "kill")
            match.Kills++;
        else
            match.Deaths++;
        match.CombatEvents.Add(kind);
        if (match.CombatEvents.Count > Match.CombatHistoryLimit)
            match.CombatEvents.RemoveRange(0, match.CombatEvents.Count - Match.CombatHistoryLimit);
        match.Revision++;
    }
    public bool ProcessLine(string line)
    {
        var changed = false;
        if ((line.Contains("Starting UI state machine 'loading/StateMachine'") || line.Contains("CGameLobby::OnGameStarting()")) && Current?.Status != "loading")
        {
            Start(true);
            changed = true;
        }
        var map = MapPattern().Match(line);
        if (map.Success)
        {
            if (Current is null || Current.Status == "complete")
                Start(false);
            if (Current!.Map.Length == 0)
            {
                Current.Map = map.Groups[1].Value;
                if (Maps.ContainsKey(Current.Map))
                    Maps[Current.Map]++;
                changed = true;
            }
        }
        if (line.Contains("transition from state(eLS_Loading) to (eLS_Game)") || line.Contains("entering state eLS_Game(7) from eLS_Loading"))
        {
            if (Current is null || Current.Status == "complete")
                Start(false);
            changed |= !Current!.Loaded;
            Current.Loaded = true;
            Current.Status = "active";
        }
        if (Current is null || Current.Status == "complete")
            return changed;
        if (line.Contains("Starting UI state machine 'death/StateMachine'"))
        {
            changed |= !Current.DeathOpen;
            Current.DeathOpen = true;
        }
        if (line.Contains("Stopping UI state machine 'death/StateMachine'"))
        {
            changed |= Current.DeathOpen;
            Current.DeathOpen = false;
        }
        var disconnect = DisconnectPattern().Match(line);
        if (disconnect.Success)
        {
            var reason = disconnect.Groups[2].Value;
            if (Current.Status == "loading" && disconnect.Groups[1].Value == "11" && reason == "Unknown" && line.Contains("desc='Nub destroyed'"))
                return changed;
            Complete(reason);
            return true;
        }
        if (line.Contains("PrepareLevel 'menu' (server)"))
        {
            Complete("menu_without_result");
            return true;
        }
        if (changed)
            Current.Revision++;
        return changed;
    }
    public object Overlay() => new { id = Id, started_at = StartedAt, kills = Kills, deaths = Deaths, trades = Trades, games = Games, survived = Survived, lost = Lost, maps = Maps, current = Current };
    [GeneratedRegex("PrepareLevel 'levels/([^']{1,128})' \\(client\\)")]
    private static partial Regex MapPattern();
    [GeneratedRegex("Client_OnDisconnect\\(cause=(\\d{1,10}),.*reasonType='([^']{1,128})'")]
    private static partial Regex DisconnectPattern();
}
