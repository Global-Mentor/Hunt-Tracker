using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace HuntTracker.Desktop;

public sealed partial class WidgetField(string key, string label, bool selected) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] private bool selected = selected;
}
public sealed record SessionChoice(string Id, string Label, string? Path, DateTimeOffset Started = default);
public sealed record MediaRow(string Label, int Images, int Sounds);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TrackerEngine engine;
    private readonly string root;
    private AppConfig savedConfig;
    private bool initialized, historyBusy, updatingCatalog, disposed;
    private long renderedRevision = -1, historyRevision = -1;
    private string sessionId = "", catalogSession = "", cachedArchive = "", cachedStamp = "";
    private Statistics? cachedHistory;
    private CancellationTokenSource? saveDelay;
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private long historyRetryAfter;
    private bool historyRequested, catalogRequested;
    private TrackerSnapshot? renderedSnapshot;
    private string historySort = "Number";
    private bool historyDescending = true, previousPage;
    private Match? pageBoundary;
    private int pageRequest;
    private readonly CancellationTokenSource historyCancellation = new();
    [ObservableProperty] private int historyPageNumber = 1;
    [ObservableProperty] private int historyTotal;
    public string HistoryPageLabel => $"Страница {HistoryPageNumber} из {Math.Max(1, ((long)HistoryTotal + HistoryBlocks.PageSize - 1) / HistoryBlocks.PageSize)}";
    private bool CanNextPage() => !IsHistoryLoading && HistoryPageNumber * (long)HistoryBlocks.PageSize < HistoryTotal;
    private bool CanPreviousPage() => !IsHistoryLoading && HistoryPageNumber > 1;
    private void PageControls()
    {
        OnPropertyChanged(nameof(HistoryPageLabel));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (Matches.Count == 0) return;
        pageBoundary = Matches.Last().Copy();
        previousPage = false;
        HistoryPageNumber++;
        pageRequest++;
        _ = UpdateHistory(false);
    }
    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private void PreviousPage()
    {
        if (Matches.Count == 0) return;
        pageBoundary = Matches.First().Copy();
        previousPage = true;
        HistoryPageNumber--;
        pageRequest++;
        _ = UpdateHistory(false);
    }
    public void SortHistory(string sort, bool descending)
    {
        historySort = sort;
        historyDescending = descending;
        ResetHistoryPage();
        _ = UpdateHistory(false);
    }
    private void ResetHistoryPage()
    {
        pageBoundary = null;
        previousPage = false;
        HistoryPageNumber = 1;
        pageRequest++;
    }
    private static readonly HashSet<string> SettingProperties = [nameof(AlertsEnabled), nameof(StatisticsEnabled), nameof(DetectTrades), nameof(AutoRefreshMedia), nameof(DetailedDiagnostics), nameof(AlertIndex), nameof(RefreshIndex), nameof(Volume), nameof(Duration), nameof(Opacity)];
    [ObservableProperty] private Statistics stats = new();
    [ObservableProperty] private Statistics historyStats = new();
    [ObservableProperty] private string status = "Запуск трекера...";
    [ObservableProperty] private bool hasStatusError;
    [ObservableProperty] private int selectedTab;
    [ObservableProperty] private SessionChoice? selectedSession;
    [ObservableProperty] private string historyNote = "";
    [ObservableProperty] private bool isHistoryLoading;
    [ObservableProperty] private string connectionStatus = "Ожидание игры";
    [ObservableProperty] private string connectionColor = "#DEC587";
    [ObservableProperty] private string logStatus = "";
    [ObservableProperty] private string gameStatus = "Проверка…", gameLogStatus = "Проверка…", nvidiaStatus = "Проверка…";
    [ObservableProperty] private string nvidiaStatusHint = "";
    [ObservableProperty] private string gameStatusColor = "#DEC587", gameLogStatusColor = "#DEC587", nvidiaStatusColor = "#DEC587";
    [ObservableProperty] private bool alertsEnabled, statisticsEnabled, detectTrades, autoRefreshMedia, detailedDiagnostics;
    [ObservableProperty] private int alertIndex, refreshIndex, duration;
    [ObservableProperty] private double volume, opacity;
    [ObservableProperty] private string gameFolder = "AUTO", nvidiaFolder = "";
    public ObservableCollection<WidgetField> Fields { get; } = [];
    public ObservableCollection<SessionChoice> Sessions { get; } = [];
    public ObservableCollection<Match> Matches { get; } = [];
    public ObservableCollection<MediaRow> MediaRows { get; } = [];
    public string[] AlertChoices { get; } = ["Только убийства", "Убийства и смерти", "Убийства, смерти и размены"];
    public string[] RefreshChoices { get; } = ["Раз в секунду", "Раз в 5 секунд"];
    public string SessionTitle => "Сессия начата: " + DateTimeOffset.Parse(Stats.StartedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string CurrentMap => Stats.Current?.MapName ?? "";
    public bool HasCurrentMatch => Stats.Current is not null;
    public string MatchHeading => Stats.Current is { Status: not "complete" } ? "Текущий матч" : "Последний матч";
    public string CurrentStatus => Stats.Current is null ? "" : "Статус: " + (Stats.Current.Status == "complete" ? "Завершён" : Stats.Current.Status == "loading" ? "Загрузка" : "В игре");
    public string CurrentColor => Stats.Current?.Status == "complete" ? "#A4CFA0" : "#E29B91";
    public string CurrentKills => Stats.Current is null ? "" : "Убийства: " + Stats.Current.Kills;
    public string CurrentDeaths => Stats.Current is null ? "" : "Смерти: " + Stats.Current.Deaths;
    public string DelayText => "Задержка до отображения: " + Combat.Delay(Mode) + " мс";
    public string VolumeLabel => $"Громкость алерта: {Volume:0}%";
    public string OpacityLabel => $"Непрозрачность фона: {Opacity:0}%";
    public string DurationLabel => $"Длительность отображения картинки: {Duration / 1000d:0.0} с";
    public string AlertExplanation => AlertIndex switch
    {
        1 => "Алерты убийства и смерти разделены по экрану смерти.",
        2 => "Размены определяются экспериментально. Возможны ошибки сопоставления.",
        _ => "Мгновенный алерт на каждый сигнал NVIDIA. Смерть тоже отображается как алерт убийства."
    };
    public string AlertFile => Path.Combine(root, "Overlay.html");
    public string StatisticsFile => Path.Combine(root, "Statistics.html");
    public bool PathsChanged => GameFolder != savedConfig.GameFolder || NvidiaFolder != savedConfig.NvidiaLogs;
    private string Mode => AlertIndex switch { 1 => "kills-deaths", 2 => "trades", _ => "kills" };

    public MainViewModel(TrackerEngine engine, string root, AppConfig config)
    {
        this.engine = engine;
        this.root = root;
        savedConfig = config.Copy();
        AlertsEnabled = config.AlertsEnabled;
        StatisticsEnabled = config.StatisticsEnabled;
        DetectTrades = config.DetectTrades;
        AutoRefreshMedia = config.AutoRefreshMedia;
        DetailedDiagnostics = config.DetailedDiagnostics;
        AlertIndex = Array.IndexOf(new[] { "kills", "kills-deaths", "trades" }, config.AlertMode);
        RefreshIndex = config.StatisticsRefreshMs == 5000 ? 1 : 0;
        Volume = config.Volume * 100;
        Duration = config.DurationMs;
        Opacity = config.Display.Opacity;
        GameFolder = config.GameFolder;
        NvidiaFolder = config.NvidiaLogs;
        var labels = new[] { "Убийства", "Смерти", "K/D", "Матчи", "Выживаемость", "Частота карт", "Последний матч" };
        for (var i = 0; i < labels.Length; i++)
        {
            var field = new WidgetField(AppConfig.FieldNames[i], labels[i], config.Display.Fields.Contains(AppConfig.FieldNames[i]));
            field.PropertyChanged += (_, _) => ScheduleSave();
            Fields.Add(field);
        }
        initialized = true;
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(AlertIndex))
        {
            OnPropertyChanged(nameof(DelayText));
            OnPropertyChanged(nameof(AlertExplanation));
        }
        if (e.PropertyName == nameof(Duration))
            OnPropertyChanged(nameof(DurationLabel));
        if (e.PropertyName is nameof(GameFolder) or nameof(NvidiaFolder))
            OnPropertyChanged(nameof(PathsChanged));
        if (e.PropertyName == nameof(Volume))
            OnPropertyChanged(nameof(VolumeLabel));
        if (e.PropertyName == nameof(Opacity))
            OnPropertyChanged(nameof(OpacityLabel));
        if (e.PropertyName is not null && SettingProperties.Contains(e.PropertyName))
            ScheduleSave();
    }
    private async void ScheduleSave()
    {
        if (!initialized || disposed)
            return;
        saveDelay?.Cancel();
        saveDelay?.Dispose();
        saveDelay = new();
        var token = saveDelay.Token;
        try
        {
            await Task.Delay(250, token);
            await Save(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { ReportError(AtomicFile.Protect(e.Message)); }
    }
    public async Task FlushSettings()
    {
        saveDelay?.Cancel();
        await Save(false);
    }
    private async Task Save(bool paths)
    {
        await saveLock.WaitAsync();
        try
        {
            var candidate = savedConfig.Copy();
            candidate.AlertMode = Mode;
            candidate.AlertsEnabled = AlertsEnabled;
            candidate.StatisticsEnabled = StatisticsEnabled;
            candidate.DetectTrades = DetectTrades;
            candidate.AutoRefreshMedia = AutoRefreshMedia;
            candidate.DetailedDiagnostics = DetailedDiagnostics;
            candidate.Volume = Math.Round(Volume) / 100;
            candidate.DurationMs = Duration;
            candidate.StatisticsRefreshMs = RefreshIndex == 1 ? 5000 : 1000;
            candidate.Display = new()
            {
                Opacity = (int)Math.Round(Opacity),
                Fields = Fields.Where(f => f.Selected).Select(f => f.Key).ToArray()
            };
            if (paths)
            {
                candidate.GameFolder = GameFolder;
                candidate.NvidiaLogs = NvidiaFolder;
            }
            candidate.Validate();
            await engine.ApplyConfig(candidate);
            savedConfig = candidate;
            OnPropertyChanged(nameof(PathsChanged));
            Status = paths ? "Пути применены. Сбор продолжается из новых папок." : "Настройки применены и сохранены.";
        }
        finally { saveLock.Release(); }
    }
    private async Task Execute(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) { ReportError(AtomicFile.Protect(e.Message)); }
    }
    partial void OnStatusChanged(string value) => HasStatusError = false;
    private void ReportError(string message)
    {
        Status = message;
        HasStatusError = true;
    }
    [RelayCommand] private Task SavePaths() => Execute(() => Save(true));
    [RelayCommand] private Task NewSession() => Execute(async () => { await engine.NewSession(); catalogSession = ""; });
    [RelayCommand] private Task Test(string kind) => Execute(async () => { await FlushSettings(); await engine.Test(kind); });
    [RelayCommand] private Task RefreshMedia() => Execute(() => engine.RefreshMedia());
    [RelayCommand] private Task Export() => Execute(async () => { Status = "Сохраняется читаемый журнал..."; var path = await engine.ExportJournal(); Status = "Журнал сохранён в logs: " + Path.GetFileName(path); });
    [RelayCommand] private void ShowSettings() => SelectedTab = 4;
    [RelayCommand] private void CurrentSession() => SelectedSession = Sessions.FirstOrDefault(s => s.Path is null);
    [RelayCommand] private void AutoGameFolder() => GameFolder = "AUTO";
    [RelayCommand]
    private void BrowseFolder(string kind)
    {
        if (kind is not ("game" or "nvidia"))
            return;
        try
        {
            var value = kind == "game" ? GameFolder : NvidiaFolder;
            var dialog = new OpenFolderDialog { Title = kind == "game" ? "Выберите папку Hunt: Showdown 1896" : "Выберите папку логов NVIDIA Highlights", Multiselect = false };
            try
            {
                var path = value == "AUTO" ? Path.GetFullPath(AppConfig.FindGame()) : AppConfig.Resolve(value, root);
                while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent && parent != path)
                    path = parent;
                dialog.InitialDirectory = Directory.Exists(path) ? path : root;
                dialog.DefaultDirectory = root;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                dialog.InitialDirectory = root;
            }
            var owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
                return;
            if (kind == "game")
                GameFolder = dialog.FolderName;
            else
                NvidiaFolder = dialog.FolderName;
        }
        catch (Exception e) { ReportError(AtomicFile.Protect(e.Message)); }
    }
    [RelayCommand]
    private void CopyOverlayPath(string name)
    {
        if (name is not ("Overlay.html" or "Statistics.html"))
            return;
        try
        {
            Clipboard.SetText(Path.Combine(root, name));
            Status = "Путь скопирован. В OBS выберите источник «Браузер» и включите «Локальный файл».";
        }
        catch (Exception e) { ReportError("Не удалось скопировать путь. " + AtomicFile.Protect(e.Message)); }
    }
    [RelayCommand]
    private void OpenMediaFolder(string kind)
    {
        if (kind is not ("images" or "sounds"))
            return;
        try
        {
            var folder = Path.Combine(root, "source", kind);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e) { ReportError(AtomicFile.Protect(e.Message)); }
    }
    [RelayCommand]
    private void OpenOverlay(string name)
    {
        if (name is not ("Overlay.html" or "Statistics.html"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(root, name)) { UseShellExecute = true });
        }
        catch (Exception e) { ReportError("Откройте " + name + " вручную. " + AtomicFile.Protect(e.Message)); }
    }
    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1)
            _ = UpdateHistory(true);
    }
    partial void OnSelectedSessionChanged(SessionChoice? value)
    {
        if (!updatingCatalog)
        {
            ResetHistoryPage();
            _ = UpdateHistory(false);
        }
    }
    public void Refresh()
    {
        if (disposed || engine.Latest is not { } snapshot)
            return;
        if (SelectedTab == 1 && Environment.TickCount64 >= historyRetryAfter &&
            (catalogSession != snapshot.Statistics.Id || SelectedSession?.Path is null && historyRevision != snapshot.StatisticsRevision))
            _ = UpdateHistory(catalogSession != snapshot.Statistics.Id);
        if (ReferenceEquals(renderedSnapshot, snapshot))
            return;
        renderedSnapshot = snapshot;
        if (snapshot.Warning.Length > 0)
            ReportError(snapshot.Warning);
        else if (snapshot.Feedback != lastFeedback || lastWarning.Length > 0)
            Status = snapshot.Feedback;
        lastFeedback = snapshot.Feedback;
        lastWarning = snapshot.Warning;
        ConnectionStatus = snapshot.GamePid == 0 ? "Ожидание запуска игры" : snapshot.GameReadable && snapshot.SdkReadable ? "Сбор идёт" : "Игра запущена, часть логов недоступна";
        ConnectionColor = snapshot.GamePid != 0 && snapshot.GameReadable && snapshot.SdkReadable ? "#A4CFA0" : "#DEC587";
        GameStatus = snapshot.GamePid == 0 ? "Не запущена" : "Запущена";
        GameLogStatus = snapshot.GameReadable ? "Читается" : "Недоступен";
        NvidiaStatus = snapshot.SdkReadable ? "Читается" : snapshot.GamePid == 0 ? "Игра не запущена" : "Лог недоступен";
        NvidiaStatusHint = snapshot.SdkReadable ? "Сигналы Highlights доступны." : snapshot.GamePid == 0 ? "Чтение начнётся после запуска Hunt." : "Проверьте папку логов и включён ли NVIDIA Highlights в игре.";
        GameStatusColor = snapshot.GamePid != 0 ? "#A4CFA0" : "#DEC587";
        GameLogStatusColor = snapshot.GameReadable ? "#A4CFA0" : "#DEC587";
        NvidiaStatusColor = snapshot.SdkReadable ? "#A4CFA0" : "#DEC587";
        LogStatus = snapshot.GameLog + "\n" + (snapshot.SdkLog.Length > 0 ? snapshot.SdkLog : snapshot.NvidiaFolder);
        var rows = snapshot.Media.Select(m => new MediaRow(m.Key == "kill" ? "Убийство" : m.Key == "death" ? "Смерть" : "Размен", m.Value.Images.Length, m.Value.Sounds.Length)).ToArray();
        if (!MediaRows.SequenceEqual(rows))
        {
            MediaRows.Clear();
            foreach (var row in rows) MediaRows.Add(row);
        }
        if (renderedRevision != snapshot.StatisticsRevision || sessionId != snapshot.Statistics.Id)
        {
            renderedRevision = snapshot.StatisticsRevision;
            sessionId = snapshot.Statistics.Id;
            Stats = snapshot.Statistics;
            foreach (var property in new[] { nameof(SessionTitle), nameof(CurrentMap), nameof(CurrentStatus), nameof(CurrentColor), nameof(CurrentKills), nameof(CurrentDeaths), nameof(HasCurrentMatch), nameof(MatchHeading) })
                OnPropertyChanged(property);
        }
    }
    private string lastFeedback = "", lastWarning = "";
    private async Task UpdateHistory(bool catalog)
    {
        if (disposed)
            return;
        if (historyBusy)
        {
            historyRequested = true;
            catalogRequested |= catalog;
            return;
        }
        historyBusy = true;
        IsHistoryLoading = true;
        PageControls();
        var requestPage = pageRequest;
        var boundary = pageBoundary;
        var backwards = previousPage;
        var sort = historySort;
        var descending = historyDescending;
        var selectedId = SelectedSession?.Id;
        var requestRevision = engine.Latest?.StatisticsRevision ?? renderedRevision;
        var requestSession = engine.Latest?.Statistics.Id ?? sessionId;
        try
        {
            if (catalog || Sessions.Count == 0)
            {
                var (choices, errors) = await Task.Run(() =>
                {
                    var choices = new List<SessionChoice>();
                    var errors = 0;
                    var folder = Path.Combine(root, "data/sessions");
                    if (Directory.Exists(folder))
                        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                            try
                            {
                                var archived = StatisticsStore.Read(file, true);
                                if (archived.Id != requestSession)
                                    choices.Add(new(archived.Id, DateTimeOffset.Parse(archived.StartedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + $" · {archived.Games} матчей", file, DateTimeOffset.Parse(archived.StartedAt)));
                            }
                            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { errors++; }
                    return (choices.OrderByDescending(c => c.Started).ToList(), errors);
                });
                if (disposed)
                    return;
                selectedId = SelectedSession?.Id ?? selectedId;
                updatingCatalog = true;
                try
                {
                    Sessions.Clear();
                    Sessions.Add(new(requestSession, "Текущая сессия", null));
                    foreach (var choice in choices)
                        Sessions.Add(choice);
                    SelectedSession = Sessions.FirstOrDefault(c => c.Id == selectedId) ?? Sessions[0];
                }
                finally { updatingCatalog = false; }
                catalogSession = requestSession;
                catalogErrors = errors;
            }
            var selection = SelectedSession;
            Statistics selectedStats;
            if (selection?.Path is null)
            {
                cachedHistory = null;
                cachedArchive = "";
                selectedStats = await engine.CurrentHistory();
            }
            else
            {
                var loaded = await Task.Run(() =>
                {
                    var info = new FileInfo(selection.Path);
                    var stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                    return (Stats: cachedArchive == selection.Path && cachedStamp == stamp && cachedHistory is not null ? cachedHistory : StatisticsStore.Read(selection.Path, boundedHistory: true), Stamp: stamp);
                });
                cachedArchive = selection.Path;
                cachedStamp = loaded.Stamp;
                cachedHistory = loaded.Stats;
                selectedStats = loaded.Stats;
            }
            var page = await Task.Run(() => HistoryBlocks.Page(selectedStats, Path.Combine(root, "data"), sort, descending,
                boundary, backwards, historyCancellation.Token));
            if (disposed)
                return;
            if (SelectedSession != selection || requestPage != pageRequest)
            {
                historyRevision = -1;
                return;
            }
            HistoryStats = selectedStats.Copy(includeHistory: false);
            historyRetryAfter = 0;
            HistoryTotal = page.Total;
            Matches.Clear();
            foreach (var match in page.Matches)
                Matches.Add(match);
            HistoryNote = catalogErrors == 0 ? "" : $"Не прочитано сессий: {catalogErrors}";
            historyRevision = requestRevision;
        }
        catch (Exception e) { historyRetryAfter = Environment.TickCount64 + 3000; Matches.Clear(); HistoryStats = new(); HistoryNote = "Не удалось прочитать историю. " + AtomicFile.Protect(e.Message); }
        finally
        {
            historyBusy = false;
            IsHistoryLoading = false;
            PageControls();
            if (historyRequested && !disposed)
            {
                var refreshCatalog = catalogRequested;
                historyRequested = catalogRequested = false;
                if (refreshCatalog || requestPage != pageRequest || SelectedSession?.Id != HistoryStats.Id ||
                    SelectedSession?.Path is null && historyRevision != engine.Latest?.StatisticsRevision)
                    _ = UpdateHistory(refreshCatalog);
            }
        }
    }
    private int catalogErrors;
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        historyCancellation.Cancel();
        saveDelay?.Cancel();
        saveDelay?.Dispose();
    }
}
