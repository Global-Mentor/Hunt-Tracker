using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;

namespace HuntTracker.Desktop;

public partial class MainWindow : Window
{
    private readonly TrackerEngine engine;
    private readonly Task worker;
    private readonly MainViewModel model;
    private readonly DispatcherTimer timer;
    private bool exiting, completed;
    public MainWindow(TrackerEngine engine, Task worker, string root, AppConfig config)
    {
        InitializeComponent();
        this.engine = engine;
        this.worker = worker;
        model = new(engine, root, config);
        DataContext = model;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        timer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => model.Refresh(), Dispatcher);
        timer.Stop();
        StateChanged += (_, _) => UpdateVisibility();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        Closing += OnClosing;
    }
    private void UpdateVisibility()
    {
        engine.PanelVisible = IsVisible && WindowState != WindowState.Minimized && !exiting && !completed;
        if (engine.PanelVisible)
        {
            model.Refresh();
            timer.Start();
        }
        else
            timer.Stop();
    }
    public void Restore()
    {
        if (exiting)
            return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        model.Refresh();
    }
    private void OnHistorySorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var descending = e.Column.SortDirection == ListSortDirection.Ascending;
        foreach (var column in ((DataGrid)sender).Columns) column.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        model.SortHistory(e.Column.SortMemberPath, descending);
    }
    public void CompleteShutdown()
    {
        completed = true;
        timer.Stop();
        model.Dispose();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (completed)
            return;
        e.Cancel = true;
        if (exiting)
            return;
        exiting = true;
        IsEnabled = false;
        try
        {
            await model.FlushSettings();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Настройки не сохранены", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Hide();
        timer.Stop();
        engine.Stop();
        try
        {
            await worker;
        }
        catch (Exception error) { MessageBox.Show(AtomicFile.Protect(error.Message), "Hunt Tracker", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { CompleteShutdown(); Application.Current.Shutdown(); }
    }
}
