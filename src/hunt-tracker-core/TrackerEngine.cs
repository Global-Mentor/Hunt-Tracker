using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace HuntTracker;

public sealed record TrackerSnapshot(Statistics Statistics, long StatisticsRevision, int GamePid, bool GameReadable, bool SdkReadable, string GameLog, string SdkLog, string NvidiaFolder, Dictionary<string, EventMedia> Media, string Warning, string Feedback, AppConfig Config);
public sealed record Alert(long Id, string Type, string At, bool Test, string? Sound, string? Image);

public sealed partial class TrackerEngine
{
    private const int ActivePollMs = 25;
    private const int IdlePollMs = 100;
    private const int DiscoveryIntervalMs = 1000;
    private const int MediaRefreshMs = 2000;
    private const int PublicationIntervalMs = 3000;
    private const int SaveRetryMs = 3000;
    private const int CommandPollMs = 100;
    private const int SignalRetentionMs = 1500;
    private const int TestRetentionMs = 70000;
    private const int MaxQueuedAlerts = 64;
    private const int MaxPendingTests = 16;
    private const int MaxRememberedHighlights = 8192;
    private const int MaxRememberedCommands = 10000;
    private const int MaxCommandsPerPoll = 32;
    private const int ControlFileLimitBytes = 16384;
    private const int MaxPendingSignals = 128;
    private Process? gameProcess;
    private readonly string root, runtime, gameProcessName;
    private string sdkFolder;
    private readonly StatisticsStore store;
    private LogTail game;
    private LogTail? sdk;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Channel<Action> commands = Channel.CreateBounded<Action>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<CombatEvent> pending = [], alertPending = [];
    private readonly List<DeathSignal> signals = [], alertSignals = [];
    private readonly HashSet<string> seen = [], commandIds = [];
    private readonly Queue<string> seenOrder = [];
    private readonly List<(double At, string Kind, string Mode)> tests = [];
    private readonly List<Alert> events = [];
    private Dictionary<string, EventMedia> media = MediaCatalog.Empty();
    private readonly Dictionary<string, string> errors = [];
    private readonly Journal? journal;
    private AppConfig config;
    private Statistics statistics;
    private int gamePid, generation, epoch;
    private bool firstDiscovery = true, deathOpen, rebasing, mediaLoaded, dirty = true, statsDirty = true, savePending, stopping;
    private double nextDiscovery, nextMedia, nextCommands, nextPublish, nextSave, testActiveUntil;
    private double signalOverloadUntil;
    private long sequence, publicationRevision, statisticsRevision;
    private (bool Game, bool CaughtUp, bool? Sdk)? health;
    private string feedback = "Настройки сохранены";
    private TrackerSnapshot? latest;
    private volatile bool stopRequested, finished;
    public volatile bool PanelVisible;
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public TrackerSnapshot? Latest => Volatile.Read(ref latest);
    public event Action? ShowRequested;

    public TrackerEngine(string root, string gameProcessName = "HuntGame")
    {
        this.root = Path.GetFullPath(root);
        this.gameProcessName = gameProcessName;
        root = this.root;
        runtime = Path.Combine(root, "_runtime");
        Directory.CreateDirectory(runtime);
        config = AppConfig.Load(root);
        store = new(root);
        statistics = store.Load(boundedHistory: true);
        if (store.RecoveryWarning.Length > 0)
            errors["recovery"] = store.RecoveryWarning;
        var folder = config.GameFolder == "AUTO" ? AppConfig.FindGame() : AppConfig.Resolve(config.GameFolder, root);
        sdkFolder = AppConfig.Resolve(config.NvidiaLogs, root);
        game = new(Path.Combine(folder, "USER/Game.log"), true);
        generation = game.Generation;
        try
        {
            journal = new(root, Session);
        }
        catch (Exception e) when (Operational(e)) { errors["journal"] = "Не удалось открыть журнал. Сбор продолжается."; }
    }

    public void Stop() => stopRequested = true;
    public Task ApplyConfig(AppConfig candidate) => ApplyConfigSnapshot(candidate.Copy());
    private Task ApplyConfigSnapshot(AppConfig candidate) => Request(() =>
    {
        candidate.Validate();
        var changed = candidate.GameFolder != config.GameFolder || candidate.NvidiaLogs != config.NvidiaLogs;
        var nextGame = changed ? new LogTail(Path.Combine(candidate.GameFolder == "AUTO" ? AppConfig.FindGame() : AppConfig.Resolve(candidate.GameFolder, root), "USER/Game.log"), true) : game;
        var nextSdkFolder = changed ? AppConfig.Resolve(candidate.NvidiaLogs, root) : sdkFolder;
        var nextSdk = changed && gamePid != 0 ? new LogTail(Path.Combine(nextSdkFolder, $"HuntGame_{gamePid}.log"), true) : sdk;
        candidate.Save(root);
        if (candidate.AlertMode != config.AlertMode || candidate.AlertsEnabled != config.AlertsEnabled)
        {
            alertPending.Clear();
            alertSignals.Clear();
        }
        if (candidate.AlertsEnabled && !config.AlertsEnabled)
        {
            mediaLoaded = false;
            nextMedia = 0;
        }
        if (changed)
        {
            game = nextGame;
            sdkFolder = nextSdkFolder;
            sdk = nextSdk;
            generation = game.Generation;
            pending.Clear();
            signals.Clear();
            alertPending.Clear();
            alertSignals.Clear();
            epoch++;
            deathOpen = rebasing = false;
            if (statistics.Current is { Status: not "complete" } match)
            {
                match.Full = false;
                match.Revision++;
                statsDirty = true;
            }
            Record("sources_changed", "Пути к логам изменены. Чтение продолжено с конца новых файлов.");
        }
        config = candidate.Copy();
        dirty = true;
        feedback = changed ? "Пути применены. Сбор продолжается из новых папок." : "Настройки применены и сохранены.";
    });
    public Task Test(string kind) => Request(() => QueueTest(kind));
    public Task RefreshMedia() => Request(() => { ScanMedia(); feedback = errors.GetValueOrDefault("media", "Список медиа обновлён."); dirty = true; });
    public Task NewSession() => Request(() =>
    {
        statistics = store.NewSession(statistics);
        pending.Clear();
        alertPending.Clear();
        signals.Clear();
        alertSignals.Clear();
        tests.Clear();
        events.Clear();
        statsDirty = savePending = false;
        statisticsRevision++;
        dirty = true;
        errors.Remove("storage");
        feedback = "Начата новая сессия. Предыдущая сохранена в истории.";
    });
    public Task<Statistics> CurrentHistory() => Request(() => statistics.Copy());
    public async Task<string> ExportJournal()
    {
        var path = await Request(() => journal?.Path ?? throw new IOException("Журнал недоступен."));
        return await Task.Run(() => Journal.Export(path));
    }
    private Task Request(Action action) => Request(() => { action(); return true; });
    private Task<T> Request<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stopRequested || finished)
            return Task.FromException<T>(new InvalidOperationException("Трекер остановлен."));
        if (!commands.Writer.TryWrite(() => { try { if (finished) throw new InvalidOperationException("Трекер остановлен."); completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } }))
            completion.SetException(new InvalidOperationException("Очередь команд заполнена или трекер остановлен."));
        return completion.Task;
    }
    private static bool Operational(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or System.Security.SecurityException;
    private void Record(string type, string message, object? data = null)
    {
        if (journal is null || !config.DetailedDiagnostics && type is "highlight" or "death_screen_opened")
            return;
        try
        {
            journal.Write(type, message, data);
            errors.Remove("journal");
        }
        catch (Exception e) when (Operational(e)) { errors["journal"] = "Не удалось записать журнал. Проверьте папку logs."; }
    }
    private void QueueTest(string kind)
    {
        if (kind is not ("kill" or "death" or "trade"))
            throw new ArgumentException("Неизвестное событие.");
        if (tests.Count >= MaxPendingTests)
            throw new InvalidOperationException("Очередь тестов заполнена. Подождите.");
        tests.Add((clock.Elapsed.TotalMilliseconds + Combat.Delay(config.AlertMode), kind, config.AlertMode));
        Record("test", "Тест: " + kind, new
        {
            event_type = kind
        });
    }
    private void ScanMedia()
    {
        nextMedia = clock.Elapsed.TotalMilliseconds + MediaRefreshMs;
        try
        {
            var updated = MediaCatalog.Read(root);
            if (updated.Any(kv => !kv.Value.Images.SequenceEqual(media[kv.Key].Images) || !kv.Value.Sounds.SequenceEqual(media[kv.Key].Sounds)))
            {
                media = updated;
                dirty = true;
            }
            mediaLoaded = true;
            errors.Remove("media");
        }
        catch (Exception e) when (Operational(e)) { errors["media"] = "Не удалось прочитать медиа. Используется прежний список."; Record("error", errors["media"], new { category = "media" }); }
    }
    private void Emit(string kind, bool test, string mode)
    {
        if (!test && (!config.AlertsEnabled || !Combat.Enabled(mode, kind)))
            return;
        if (!mediaLoaded && clock.Elapsed.TotalMilliseconds >= nextMedia)
            ScanMedia();
        if (test)
            testActiveUntil = clock.Elapsed.TotalMilliseconds + TestRetentionMs;
        var files = media[kind];
        string? Pick(string[] values) => values.Length == 0 ? null : values[Random.Shared.Next(values.Length)];
        var alert = new Alert(++sequence, kind, DateTimeOffset.UtcNow.ToString("o"), test, Pick(files.Sounds), Pick(files.Images));
        events.Add(alert);
        if (events.Count > MaxQueuedAlerts)
            events.RemoveAt(0);
        dirty = true;
        if (test)
            Record("test_alert", "Тестовый алерт отправлен. Звук: " + (alert.Sound is null ? "нет файлов" : Uri.UnescapeDataString(alert.Sound)));
    }

    public async Task RunAsync()
    {
        try
        {
            Record("started", "Трекер запущен.");
            while (true)
            {
                for (var count = 0; count < MaxCommandsPerPoll && commands.Reader.TryRead(out var command); count++)
                    command();
                if (stopRequested && !stopping)
                {
                    stopping = true;
                    alertPending.Clear();
                    tests.Clear();
                    events.Clear();
                }
                var now = clock.Elapsed.TotalMilliseconds;
                if (now >= nextDiscovery && !stopping)
                    Discover(now);
                ReadGame(pending.Count != 0 || alertPending.Count != 0);
                if (sdk is not null && !stopping)
                    ReadHighlights();
                now = clock.Elapsed.TotalMilliseconds;
                ResolvePending(now);
                if (now >= nextCommands)
                {
                    nextCommands = now + CommandPollMs;
                    ReadCommands();
                }
                if (stopping && pending.Count == 0)
                    break;
                foreach (var test in tests.ToArray())
                    if (now >= test.At)
                    {
                        Emit(test.Kind, true, test.Mode);
                        tests.Remove(test);
                    }
                if (config.AlertsEnabled && now >= nextMedia && (!mediaLoaded || config.AutoRefreshMedia))
                    ScanMedia();
                var status = (game.Healthy, game.CaughtUp, sdk?.Healthy);
                if (status != health)
                {
                    health = status;
                    dirty = true;
                    Record("log_status", "Состояние логов обновлено.", new
                    {
                        game = game.Healthy && game.CaughtUp,
                        nvidia = sdk?.Healthy == true
                    });
                }
                if (dirty || now >= nextPublish || savePending && now >= nextSave)
                {
                    Publish(true);
                    nextPublish = now + PublicationIntervalMs;
                }
                await Task.Delay(gamePid != 0 || tests.Count != 0 || stopping ? ActivePollMs : IdlePollMs).ConfigureAwait(false);
            }
            events.Clear();
            statistics.Complete("tracker_stopped");
            statsDirty = true;
            Record("stopped", $"Трекер остановлен. Убийств: {statistics.Kills}; смертей: {statistics.Deaths}");
            Publish(false);
        }
        catch (Exception e)
        {
            Record("error", AtomicFile.Protect(e.Message));
            try
            {
                statistics.Complete("tracker_stopped");
                statsDirty = true;
                Publish(false);
            }
            catch (Exception failure) when (Operational(failure)) { store.SaveRecovery(statistics); }
            throw;
        }
        finally
        {
            gameProcess?.Dispose();
            finished = true;
            commands.Writer.TryComplete();
            while (commands.Reader.TryRead(out var command))
                command();
            journal?.Dispose();
        }
    }

    private void Discover(double now)
    {
        nextDiscovery = now + DiscoveryIntervalMs;
        if (gameProcess is not null)
            try
            {
                if (!gameProcess.HasExited)
                    return;
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        gameProcess?.Dispose();
        gameProcess = null;
        var processes = Process.GetProcessesByName(gameProcessName);
        int active;
        try
        {
            active = processes.Select(p => p.Id).DefaultIfEmpty().Max();
            gameProcess = processes.FirstOrDefault(p => p.Id == active);
        }
        finally { foreach (var process in processes) if (!ReferenceEquals(process, gameProcess)) process.Dispose(); }
        if (active == gamePid && !firstDiscovery)
            return;
        var skip = firstDiscovery;
        firstDiscovery = false;
        gamePid = active;
        sdk = active == 0 ? null : new(Path.Combine(sdkFolder, $"HuntGame_{active}.log"), skip);
        foreach (var item in pending)
            Record("unknown", "Сменился процесс игры. Счётчики не изменены.", new
            {
                request = item.Id,
                wait_ms = now - item.At
            });
        statistics.Complete("process_changed");
        statsDirty = dirty = true;
        pending.Clear();
        signals.Clear();
        alertPending.Clear();
        alertSignals.Clear();
        seen.Clear();
        seenOrder.Clear();
        epoch++;
        deathOpen = false;
        Record("game_connection", active == 0 ? "Ожидание игры." : "Игра запущена.", new
        {
            pid = active
        });
    }
    private void ReadGame(bool verifyUnchanged = true)
    {
        var lines = game.Read(verifyUnchanged);
        var at = clock.Elapsed.TotalMilliseconds;
        if (game.Generation != generation)
        {
            rebasing = true;
            generation = game.Generation;
            epoch++;
            deathOpen = false;
            statistics.Complete("log_replaced");
            statsDirty = dirty = true;
        }
        if (!game.Healthy || !game.CaughtUp)
        {
            foreach (var item in pending.Concat(alertPending))
                item.GameAvailable = false;
            if (statistics.Current is { Status: not "complete", Full: true } match)
            {
                match.Full = false;
                match.Revision++;
                statsDirty = dirty = true;
            }
        }
        foreach (var line in lines)
        {
            if (!rebasing && statistics.ProcessLine(line))
                statsDirty = dirty = true;
            if (line.Contains("Starting UI state machine 'loading/StateMachine'"))
            {
                epoch++;
                deathOpen = false;
            }
            if (line.Contains("Starting UI state machine 'death/StateMachine'"))
            {
                if (!deathOpen)
                {
                    if (signals.Count >= MaxPendingSignals || alertSignals.Count >= MaxPendingSignals)
                        SignalOverload(at);
                    if (at < signalOverloadUntil)
                    {
                        deathOpen = true;
                        continue;
                    }
                    signals.Add(new(at, epoch));
                    if (config.AlertsEnabled && config.AlertMode != "kills")
                        alertSignals.Add(new(at, epoch));
                    Record("death_screen_opened", "Открылся экран смерти.", new
                    {
                        read_ms = at,
                        epoch
                    });
                }
                deathOpen = true;
            }
            if (line.Contains("Stopping UI state machine 'death/StateMachine'"))
                deathOpen = false;
        }
        if (game.CaughtUp)
            rebasing = false;
    }
    private void ReadHighlights()
    {
        var lines = sdk!.Read(verifyUnchanged: false);
        if (lines.Length != 0)
            ReadGame();
        var at = clock.Elapsed.TotalMilliseconds;
        foreach (var line in lines)
        {
            if (at < signalOverloadUntil)
                break;
            var match = HighlightPattern().Match(line);
            if (!match.Success || !seen.Add(match.Groups[1].Value))
                continue;
            var id = match.Groups[1].Value;
            seenOrder.Enqueue(id);
            if (seenOrder.Count > MaxRememberedHighlights)
                seen.Remove(seenOrder.Dequeue());
            if (pending.Count >= MaxPendingSignals || alertPending.Count >= MaxPendingSignals)
            {
                SignalOverload(at);
                break;
            }
            CombatEvent Event(string mode) => new(id, at, mode, epoch, game.Healthy && game.CaughtUp, generation, statistics.Current?.Id);
            pending.Add(Event(config.DetectTrades ? "trades" : "kills-deaths"));
            if (config.AlertsEnabled)
            {
                if (config.AlertMode == "kills")
                    Emit("kill", false, "kills");
                else
                    alertPending.Add(Event(config.AlertMode));
            }
            Record("highlight", "Событие NVIDIA. Запрос: " + id, new
            {
                request = id,
                read_ms = at,
                epoch
            });
        }
    }
    private void SignalOverload(double at)
    {
        pending.Clear();
        alertPending.Clear();
        signals.Clear();
        alertSignals.Clear();
        epoch++;
        signalOverloadUntil = at + SignalRetentionMs;
        errors["signals"] = "Слишком много событий в логах. Неоднозначный пакет пропущен, счётчики не изменены.";
        dirty = true;
        Record("signal_overload", errors["signals"]);
    }
    [GeneratedRegex(@"Posted Request HLSetVideo\[([a-zA-Z0-9]{1,128})\]")]
    private static partial Regex HighlightPattern();
    private void ResolvePending(double now)
    {
        foreach (var item in alertPending.ToArray())
        {
            if (!alertPending.Contains(item) || now - item.At < Combat.Delay(item.Mode))
                continue;
            var result = Combat.Resolve(item, alertPending, alertSignals, game.Healthy, game.CaughtUp, generation, epoch);
            Emit(result.Kind, false, item.Mode);
            alertPending.Remove(item);
            if (result.Other is not null)
                alertPending.Remove(result.Other);
        }
        foreach (var item in pending.ToArray())
        {
            if (!pending.Contains(item) || now - item.At < Combat.Delay(item.Mode))
                continue;
            var result = Combat.Resolve(item, pending, signals, game.Healthy, game.CaughtUp, generation, epoch);
            if (result.Kind == "trade")
            {
                statistics.Trades++;
                statistics.Combat("kill", item.MatchId);
                statistics.Combat("death", item.MatchId);
                pending.Remove(result.Other!);
                Record("trade", "Предполагаемый размен.", new
                {
                    requests = new[] { item.Id, result.Other!.Id },
                    wait_ms = now - item.At
                });
            }
            else
            {
                statistics.Combat(result.Kind, item.MatchId);
                var message = result.Kind == "kill" ? "Предполагаемое убийство." : result.Kind == "death" ? "Смерть по экрану смерти." : "Счётчики не изменены. " + result.Reason;
                Record(result.Kind, message, new
                {
                    request = item.Id,
                    wait_ms = now - item.At,
                    reason = result.Reason,
                    screen_gap_ms = result.Signal is null ? (double?)null : result.Signal.At - item.At
                });
            }
            pending.Remove(item);
            statsDirty = dirty = true;
        }
        signals.RemoveAll(s => s.Used || now - s.At > SignalRetentionMs);
        alertSignals.RemoveAll(s => s.Used || now - s.At > SignalRetentionMs);
    }
    private void ReadCommands()
    {
        void Control(string name, Action action)
        {
            var path = Path.Combine(runtime, name + ".json");
            if (!File.Exists(path))
                return;
            try
            {
                using var doc = JsonDocument.Parse(AtomicFile.Read(path, ControlFileLimitBytes));
                if (doc.RootElement.TryGetProperty("session", out var value) && value.GetString() == Session)
                {
                    action();
                    dirty = true;
                }
                File.Delete(path);
            }
            catch (Exception e) when (Operational(e) || e is InvalidOperationException) { }
        }
        Control("stop", () => stopRequested = true);
        Control("show", () => ShowRequested?.Invoke());
        try
        {
            var folder = Path.Combine(runtime, "commands");
            Directory.CreateDirectory(folder);
            foreach (var path in Directory.EnumerateFiles(folder, "*.json").Take(MaxCommandsPerPoll))
            {
                try
                {
                    using var doc = JsonDocument.Parse(AtomicFile.Read(path, ControlFileLimitBytes));
                    var cmd = doc.RootElement;
                    if (cmd.TryGetProperty("session", out var session) && session.GetString() == Session && cmd.TryGetProperty("type", out var type) && type.GetString() is "kill" or "death" or "trade" && cmd.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var guid) && tests.Count < MaxPendingTests && commandIds.Count < MaxRememberedCommands && commandIds.Add(guid.ToString("N")))
                        QueueTest(type.GetString()!);
                    File.Delete(path);
                }
                catch (Exception e) when (Operational(e) || e is InvalidOperationException)
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddSeconds(-10))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (IOException) { }
                    }
                }
            }
            errors.Remove("commands");
        }
        catch (Exception e) when (Operational(e)) { errors["commands"] = "Не удалось прочитать тестовые команды. Сбор продолжается."; }
    }
    private void Publish(bool running)
    {
        if (statsDirty)
        {
            savePending = true;
            statsDirty = false;
            statisticsRevision++;
        }
        publicationRevision++;
        var now = clock.Elapsed.TotalMilliseconds;
        var updated = DateTimeOffset.UtcNow.ToString("o");
        var gamePath = AtomicFile.Protect(game.Path).Replace('\\', '/');
        var sdkPath = AtomicFile.Protect(sdk?.Path ?? "").Replace('\\', '/');
        var folder = AtomicFile.Protect(sdkFolder).Replace('\\', '/');
        var overlay = statistics.Overlay();
        var warning = string.Join(" ", errors.Values);
        var alertState = new
        {
            revision = publicationRevision,
            session = Session,
            running,
            updated_at = updated,
            alerts_enabled = config.AlertsEnabled,
            alert_mode = config.AlertMode,
            volume = config.Volume,
            duration_ms = config.DurationMs,
            events = config.AlertsEnabled || now <= testActiveUntil ? events.ToArray() : []
        };
        var statsState = new
        {
            revision = publicationRevision,
            session = Session,
            running,
            updated_at = updated,
            statistics_enabled = config.StatisticsEnabled,
            statistics_refresh_ms = config.StatisticsRefreshMs,
            game_pid = gamePid,
            game_readable = game.Healthy && game.CaughtUp,
            sdk_readable = sdk?.Healthy == true,
            statistics = config.StatisticsEnabled ? overlay : null,
            display = config.Display
        };
        var state = new
        {
            revision = publicationRevision,
            statistics_revision = statisticsRevision,
            session = Session,
            running,
            updated_at = updated,
            delay_ms = Combat.Delay(config.AlertMode),
            game_log = gamePath,
            game_readable = game.Healthy && game.CaughtUp,
            nvidia_folder = folder,
            sdk_log = sdkPath,
            sdk_readable = sdk?.Healthy == true,
            game_pid = gamePid,
            kills = statistics.Kills,
            deaths = statistics.Deaths,
            media,
            display = config.Display,
            alert_mode = config.AlertMode,
            statistics = overlay,
            volume = config.Volume,
            duration_ms = config.DurationMs,
            events = events.ToArray(),
            alerts_enabled = config.AlertsEnabled,
            statistics_enabled = config.StatisticsEnabled,
            statistics_refresh_ms = config.StatisticsRefreshMs,
            warning,
            tracker_pid = Environment.ProcessId,
            panel_visible = PanelVisible && running
        };
        void Output(string name, object value, bool script)
        {
            try
            {
                AtomicFile.Write(Path.Combine(runtime, name + (script ? ".js" : ".json")), (script ? "window.HuntKillState=" : "") + JsonSerializer.Serialize(value, Statistics.Json) + (script ? ";" : ""));
                errors.Remove(name);
            }
            catch (Exception e) when (Operational(e)) { errors[name] = "Не удалось обновить " + name + ". Сбор продолжается."; }
        }
        Output("alerts", alertState, true);
        Output("statistics", statsState, true);
        Output("state", state, false);
        var summary = statistics.Copy(includeHistory: false);
        Volatile.Write(ref latest, new(summary, statisticsRevision, gamePid, game.Healthy && game.CaughtUp, sdk?.Healthy == true, gamePath, sdkPath, folder, media, warning, feedback, config.Copy()));
        if (savePending && (now >= nextSave || !running))
        {
            try
            {
                store.Save(statistics);
                savePending = false;
                errors.Remove("storage");
            }
            catch (Exception e) when (Operational(e))
            {
                errors["storage"] = "Статистика пока не сохранена. Проверьте доступ к data и свободное место. Повтор через 3 секунды.";
                nextSave = now + SaveRetryMs;
                if (!running)
                {
                    store.SaveRecovery(statistics);
                    Record("error", "Статистика сохранена в резервной копии и будет восстановлена при запуске.");
                }
            }
        }
        dirty = false;
    }
}
