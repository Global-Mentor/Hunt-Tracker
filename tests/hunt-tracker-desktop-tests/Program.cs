using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using HuntTracker;
using HuntTracker.Desktop;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
    private static async Task Wait(Func<bool> check, string message)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!check())
        {
            if (DateTime.UtcNow > end)
                throw new InvalidOperationException(message);
            await Task.Delay(30);
        }
    }
    [STAThread]
    private static int Main()
    {
        var testRoot = Environment.GetEnvironmentVariable("HUNT_TEST_ROOT") ?? throw new InvalidOperationException("Run scripts/Test.ps1.");
        var root = Path.Combine(testRoot, "wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new AppConfig { GameFolder = "./game", NvidiaLogs = "./sdk" };
        config.Save(root);
        var stats = new Statistics();
        for (var i = 0; i < 10; i++)
        {
            stats.Start(true);
            stats.Current!.Loaded = true;
            stats.Current.Map = Match.Names.Keys.ElementAt(i % 4);
            for (var k = 0; k < i % 5; k++)
                stats.Combat("kill", stats.Current.Id);
            if (i % 3 != 0)
            {
                stats.Combat("death", stats.Current.Id);
                stats.Current.DeathOpen = true;
            }
            stats.Complete("MissionRequested");
        }
        new StatisticsStore(root).Save(stats);
        var app = new App(false);
        app.InitializeComponent();
        var engine = new TrackerEngine(root, "NoSuchWpfFixture");
        var worker = Task.Run(engine.RunAsync);
        var window = new MainWindow(engine, worker, root, config) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
        var model = (MainViewModel)window.DataContext;
        var exit = 0;
        Check(window.Icon is not null && window.Icon.Width >= 16, "Window icon missing");
        window.Show();
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Wait(() => engine.Latest is not null, "No snapshot");
                model.Refresh();
                Check(model.Stats.Kills == 20 && model.Stats.Deaths == 6 && model.Stats.Games == 10, $"Session counters differ: {model.Stats.Kills}/{model.Stats.Deaths}/{model.Stats.Games}, snapshot {engine.Latest!.Statistics.Kills}/{engine.Latest.Statistics.Deaths}/{engine.Latest.Statistics.Games}");
                Check(model.Volume == 50 && model.DelayText.EndsWith("0 мс"), "Wrong default volume or delay");
                var tabs = Descendants(window).OfType<TabControl>().Single();
                Check(tabs.Items.Count == 5, "Expected session, history, alerts, OBS and settings tabs");
                for (var tab = 0; tab < tabs.Items.Count; tab++)
                {
                    model.SelectedTab = tab;
                    if (tab == 1)
                        await Wait(() => !model.IsHistoryLoading && model.Matches.Count == 10, "History did not load");
                    await Task.Delay(80);
                    window.UpdateLayout();
                    Capture(window, Path.Combine(root, $"tab-{tab}.png"));
                    if (tab is 2 or 4)
                    {
                        var expanders = Descendants(window).OfType<Expander>().ToArray();
                        var expanded = expanders.Select(e => e.IsExpanded).ToArray();
                        foreach (var expander in expanders)
                        {
                            var peer = new ExpanderAutomationPeer(expander);
                            ((IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)).Expand();
                        }
                        window.UpdateLayout();
                        Check(expanders.All(e => e.IsExpanded && e.ActualHeight > 60), "Details did not expand");
                        Capture(window, Path.Combine(root, $"expanded-{tab}.png"));
                        var pageScroll = Descendants(window).OfType<ScrollViewer>().First();
                        pageScroll.ScrollToEnd();
                        await Task.Delay(80);
                        Capture(window, Path.Combine(root, $"expanded-bottom-{tab}.png"));
                        pageScroll.ScrollToTop();
                        for (var i = 0; i < expanders.Length; i++)
                            expanders[i].IsExpanded = expanded[i];
                    }
                }
                window.Height = window.MinHeight;
                window.Width = window.MinWidth;
                for (var tab = 0; tab < tabs.Items.Count; tab++)
                {
                    model.SelectedTab = tab;
                    await Task.Delay(80);
                    window.UpdateLayout();
                    Capture(window, Path.Combine(root, $"compact-{tab}.png"));
                    if (tab == 4)
                    {
                        var scroll = Descendants(window).OfType<ScrollViewer>().First();
                        scroll.ScrollToEnd();
                        await Task.Delay(80);
                        Check(scroll.VerticalOffset > 0, "Settings cannot scroll at minimum window size");
                        Capture(window, Path.Combine(root, "compact-settings-bottom.png"));
                    }
                }
                model.SelectedTab = 1;
                await Wait(() => !model.IsHistoryLoading, "History is still loading before capture");
                await Task.Delay(80);
                window.UpdateLayout();
                Capture(window, Path.Combine(root, "compact.png"));
                var grid = Descendants(window).OfType<DataGrid>().Single();
                Check(grid.ActualHeight > 100 && grid.ActualWidth > 650, "History unusable at minimum window size");
                var killHeader = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => h.Column == grid.Columns[2]);
                Check(killHeader.Focus(), "History header cannot receive keyboard focus");
                PressSpace(killHeader);
                await Wait(() => !model.IsHistoryLoading, "First sort did not finish");
                var direction = grid.Columns[2].SortDirection;
                Check(direction is not null, "History header did not sort");
                PressSpace(killHeader);
                await Wait(() => !model.IsHistoryLoading, "Second sort did not finish");
                Check(grid.Columns[2].SortDirection != direction, "Repeated sorting did not reverse order");
                window.UpdateLayout();
                var arrow = (TextBlock)killHeader.Template.FindName("SortArrow", killHeader);
                Check(arrow.Text == "▼" && arrow.IsVisible, "Descending sort has no visible arrow");
                Capture(window, Path.Combine(root, "history-sorted.png"));
                model.SelectedTab = 2;
                await Task.Delay(80);
                window.UpdateLayout();
                var volume = Descendants(window).OfType<Slider>().First();
                Slider.IncreaseLarge.Execute(null, volume);
                Check(model.Volume > 50, "Styled volume slider did not update its binding");
                volume.SetCurrentValue(RangeBase.ValueProperty, 50d);
                var alerts = Descendants(window).OfType<CheckBox>().First();
                var toggle = (IToggleProvider)new CheckBoxAutomationPeer(alerts).GetPattern(PatternInterface.Toggle);
                toggle.Toggle();
                Check(!model.AlertsEnabled, "Styled checkbox did not update its binding");
                toggle.Toggle();
                Check(model.AlertsEnabled, "Styled checkbox did not restore its binding");
                var mode = Descendants(window).OfType<ComboBox>().Last();
                mode.IsDropDownOpen = true;
                await Task.Delay(80);
                Check(((Popup)mode.Template.FindName("PART_Popup", mode)).IsOpen, "Styled dropdown did not open");
                mode.IsDropDownOpen = false;
                mode.SetCurrentValue(Selector.SelectedIndexProperty, 1);
                Check(model.DelayText.EndsWith("300 мс") && model.AlertExplanation.Contains("разделены"), "Alert explanation does not follow the selected mode");
                mode.SetCurrentValue(Selector.SelectedIndexProperty, 2);
                Check(model.DelayText.EndsWith("500 мс") && model.AlertExplanation.Contains("экспериментально"), "Trade mode lost its explanation");
                mode.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                var duration = Descendants(window).OfType<Slider>().Last();
                Check(duration.Minimum == 300 && duration.Maximum == 10000, "Duration control permits invalid settings");
                Slider.IncreaseSmall.Execute(null, duration);
                Check(model.Duration == 1900, "Duration control binding did not update");
                duration.SetCurrentValue(RangeBase.ValueProperty, 1800d);
                model.ShowSettingsCommand.Execute(null);
                Check(model.SelectedTab == 4 && !model.PathsChanged, "Connection status did not open settings");
                model.AutoGameFolderCommand.Execute(null);
                Check(model.GameFolder == "AUTO" && model.PathsChanged, "Automatic game path did not mark unsaved changes");
                var pickerSeen = false;
                var closePicker = Task.Run(async () =>
                {
                    await Wait(() =>
                    {
                        var picker = FindWindow("#32770", "Выберите папку Hunt: Showdown 1896");
                        if (picker == 0) return false;
                        pickerSeen = true;
                        PostMessage(picker, 0x10, 0, 0);
                        return true;
                    }, "Folder picker did not open with AUTO");
                });
                model.BrowseFolderCommand.Execute("game");
                Check(!model.HasStatusError, "Folder picker: " + model.Status);
                await closePicker;
                Check(pickerSeen && model.GameFolder == "AUTO" && !model.HasStatusError, "Cancelling folder selection changed the path or failed");
                model.GameFolder = config.GameFolder;
                Check(!model.PathsChanged, "Restored paths are still marked dirty");
                Check(model.AlertFile == Path.Combine(root, "Overlay.html") && model.StatisticsFile == Path.Combine(root, "Statistics.html"), "OBS setup paths differ");
                model.Fields[0].Selected = false;
                model.Fields[1].Selected = false;
                model.AlertsEnabled = false;
                model.StatisticsEnabled = false;
                await model.FlushSettings();
                await Wait(() => engine.Latest?.Config.AlertsEnabled == false, "Settings were not applied");
                Check(engine.Latest!.Statistics.Kills == 20 && engine.Latest.Statistics.Deaths == 6, "Display options changed counters");
                await model.TestCommand.ExecuteAsync("death");
                await Wait(() => File.ReadAllText(Path.Combine(root, "_runtime/alerts.js")).Contains("\"type\":\"death\""), "Test did not bypass disabled module");
                window.WindowState = WindowState.Minimized;
                await Task.Delay(100);
                Check(!engine.PanelVisible, "Minimized panel remained visible");
                window.Restore();
                await Task.Delay(100);
                Check(window.WindowState == WindowState.Normal && engine.PanelVisible, "Window did not restore");
                window.WindowState = WindowState.Maximized;
                await Task.Delay(100);
                window.WindowState = WindowState.Normal;
                var old = engine.Latest!.Statistics.Id;
                await model.NewSessionCommand.ExecuteAsync(null);
                await Wait(() => engine.Latest!.Statistics.Id != old, "New session failed");
                model.Refresh();
                Check(!model.HasCurrentMatch, "New session retained a match");
                model.SelectedTab = 0;
                await Task.Delay(80);
                Capture(window, Path.Combine(root, "empty-session.png"));
                model.SelectedTab = 1;
                await Wait(() => model.Sessions.Count == 2, "Archived session missing");
                model.SelectedSession = model.Sessions.Single(s => s.Id == old);
                await Wait(() => model.HistoryStats.Kills == 20, "Archive totals differ");
                Check(model.Matches.Count == 10 && engine.Latest!.Statistics.Kills == 0, "Browsing history changed live statistics");
                var another = new Statistics();
                for (var i = 0; i < 7; i++) another.Combat("kill", null);
                var archivePath = Path.Combine(root, "data/sessions", another.Id + ".json");
                new StatisticsStore(root).SaveSnapshot(another, archivePath);
                model.CurrentSessionCommand.Execute(null);
                Check(model.IsHistoryLoading, "Current history request did not start");
                model.SelectedSession = new SessionChoice(another.Id, "Synthetic archive", archivePath);
                await Wait(() => !model.IsHistoryLoading && model.HistoryStats.Id == another.Id, "Selection during history loading was lost");
                Check(model.HistoryStats.Kills == 7, "Rapid archive selection showed the previous session");
                model.SelectedSession = model.Sessions.Single(s => s.Id == old);
                await Wait(() => !model.IsHistoryLoading && model.HistoryStats.Id == old, "Previous archive did not load");
                model.SelectedTab = 0;
                model.SelectedTab = 1;
                Check(model.IsHistoryLoading, "Catalog request did not start");
                model.SelectedSession = new SessionChoice(another.Id, "Synthetic archive", archivePath);
                await Wait(() => !model.IsHistoryLoading && model.HistoryStats.Id == another.Id, "Catalog refresh discarded the latest selection");
                model.CurrentSessionCommand.Execute(null);
                await Wait(() => !model.IsHistoryLoading && model.Matches.Count == 0, "Return to current session failed");
                Capture(window, Path.Combine(root, "empty-history.png"));
                var large = new Statistics();
                for (var i = 0; i < 350; i++)
                {
                    large.Start(true);
                    for (var k = 0; k < i % 5; k++) large.Combat("kill", large.Current!.Id);
                    large.Complete("test");
                }
                var largePath = Path.Combine(root, "data/sessions", large.Id + ".json");
                new StatisticsStore(root).SaveSnapshot(large, largePath);
                model.SelectedSession = new SessionChoice(large.Id, "Paged archive", largePath);
                model.SortHistory("Number", true);
                await Wait(() => !model.IsHistoryLoading && model.Matches.Count == 100, "Paged history did not load");
                Check(model.Matches[0].Number == 350 && model.HistoryTotal == 350, "First history page is incorrect");
                model.NextPageCommand.Execute(null);
                await Wait(() => !model.IsHistoryLoading, "Next page did not load");
                Check(model.Matches[0].Number == 250 && model.HistoryPageNumber == 2, "Next history page is incorrect");
                model.PreviousPageCommand.Execute(null);
                await Wait(() => !model.IsHistoryLoading, "Previous page did not load");
                Check(model.Matches[0].Number == 350 && model.HistoryPageNumber == 1, "Previous history page is incorrect");
                model.SortHistory("Kills", false);
                await Wait(() => !model.IsHistoryLoading, "Global history sort did not load");
                Check(model.Matches.Take(70).All(m => m.Kills == 0) && model.Matches[70].Kills == 1, "Sorting was restricted to the current page");
                Capture(window, Path.Combine(root, "paged-history.png"));
                Check(engine.Latest!.Statistics.Games == 0, "Paging changed live statistics");
                var exported = await engine.ExportJournal();
                Check(File.Exists(exported), "Export not created");
                Console.WriteLine("PASS: WPF five tabs, compact layouts, empty states, scrolling, styled controls, mode explanations, duration bounds, paths, session history, display isolation, test alerts, settings, minimize/restore/maximize, export.");
                Console.WriteLine("Screenshots: " + root);
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally
            {
                engine.Stop();
                try
                {
                    await worker;
                }
                catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
                window.CompleteShutdown();
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return exit;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
    private static void PressSpace(UIElement target)
    {
        var source = PresentationSource.FromVisual(target);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
    }
    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
