namespace HuntTracker;

public sealed class CombatEvent(string id, double at, string mode, int epoch, bool available, int generation, string? matchId)
{
    public string Id { get; } = id;
    public double At { get; } = at;
    public string Mode { get; } = mode;
    public int Epoch { get; } = epoch;
    public bool GameAvailable { get; set; } = available;
    public int Generation { get; } = generation;
    public string? MatchId { get; } = matchId;
    public bool TradeBlocked { get; set; }
}
public sealed class DeathSignal(double at, int epoch)
{
    public double At { get; } = at;
    public int Epoch { get; } = epoch;
    public bool Used { get; set; }
}
public sealed record CombatResult(string Kind, CombatEvent? Other = null, DeathSignal? Signal = null, string Reason = "");

public static class Combat
{
    public const int ClassificationDelayMs = 300;
    public const int TradeWindowMs = 500;
    private const int DeathCorrelationWindowMs = 400;
    private const int AmbiguityToleranceMs = 25;

    public static int Delay(string mode) => mode == "kills" ? 0 : mode == "trades" ? TradeWindowMs : ClassificationDelayMs;
    public static bool Enabled(string mode, string kind) => kind == "kill" || kind == "death" && mode is "kills-deaths" or "trades" || kind == "trade" && mode == "trades";
    public static CombatResult Resolve(CombatEvent first, List<CombatEvent> pending, List<DeathSignal> signals, bool healthy, bool caughtUp, int generation, int epoch)
    {
        if (!first.TradeBlocked && first.Mode == "trades" && first.Epoch == epoch && healthy && caughtUp)
        {
            var group = pending.Where(e => e.Epoch == first.Epoch && e.MatchId == first.MatchId && Math.Abs(e.At - first.At) <= TradeWindowMs).ToArray();
            if (group.Length > 2)
                foreach (var item in group)
                    item.TradeBlocked = true;
            if (group.Length == 2 && group.All(e => !e.TradeBlocked && e.Mode == "trades" && e.GameAvailable && e.Generation == generation))
            {
                var screens = signals.Where(s => !s.Used && s.Epoch == first.Epoch && Math.Abs(s.At - first.At) <= TradeWindowMs).ToArray();
                if (screens.Length > 1)
                    foreach (var item in group)
                        item.TradeBlocked = true;
                if (screens.Length == 1)
                {
                    var screen = screens[0];
                    var times = new[] { group[0].At, group[1].At, screen.At };
                    var near = pending.Where(e => e.Epoch == first.Epoch && Math.Abs(e.At - screen.At) <= TradeWindowMs).ToArray();
                    if (near.Length != 2)
                        foreach (var item in near)
                            item.TradeBlocked = true;
                    if (times.Max() - times.Min() <= TradeWindowMs && near.Length == 2)
                    {
                        screen.Used = true;
                        return new("trade", group.First(e => e != first), screen);
                    }
                }
            }
        }
        var window = first.Mode == "trades" ? TradeWindowMs : DeathCorrelationWindowMs;
        var signal = signals.Where(s => !s.Used && s.Epoch == first.Epoch && Math.Abs(s.At - first.At) <= window).OrderBy(s => Math.Abs(s.At - first.At)).FirstOrDefault();
        var ambiguous = false;
        if (signal is not null)
        {
            var candidates = pending.Where(e => e.Epoch == signal.Epoch && Math.Abs(e.At - signal.At) <= window).OrderBy(e => Math.Abs(e.At - signal.At)).ThenBy(e => e.At).ToArray();
            ambiguous = candidates.Any(e => e != first && Math.Abs(e.At - first.At) <= AmbiguityToleranceMs && Math.Abs(Math.Abs(e.At - signal.At) - Math.Abs(first.At - signal.At)) <= AmbiguityToleranceMs);
            if (!ambiguous && candidates[0] != first)
                signal = null;
        }
        if (first.Epoch != epoch)
            return new("unknown", Reason: "сменился матч или лог");
        if (ambiguous)
            return new("unknown", Signal: signal, Reason: "несколько событий NVIDIA слишком близко к экрану смерти");
        if (signal is not null)
        {
            signal.Used = true;
            return new("death", Signal: signal);
        }
        if (!first.GameAvailable || !healthy || !caughtUp || first.Generation != generation)
            return new("unknown", Reason: "Game.log был недоступен, заменён или ещё не прочитан");
        return new("kill");
    }
}
