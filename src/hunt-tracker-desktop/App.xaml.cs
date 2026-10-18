using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HuntTracker.DesktopTests")]

namespace HuntTracker.Desktop;

public partial class App : Application
{
    private readonly bool startTracking;
    public App() : this(true) { }
    internal App(bool startTracking) => this.startTracking = startTracking;
    private Mutex? mutex;
    private bool ownsMutex;
    private string root = "";
    private TrackerEngine? engine;
    private Task? worker;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startTracking)
            return;
        string? Option(string name)
        {
            var index = Array.IndexOf(e.Args, name);
            return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null;
        }
        var baseFolder = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(baseFolder, "config.example.json")) && File.Exists(Path.Combine(baseFolder, "../config.example.json")))
            baseFolder = Path.Combine(baseFolder, "..");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Option("--root") ?? baseFolder));
        var runtime = Path.Combine(root, "_runtime");
        try
        {
            Directory.CreateDirectory(runtime);
            if (e.Args.Contains("--stop"))
            {
                var target = await SendControl("stop");
                if (target is not null)
                {
                    using (target)
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        await target.WaitForExitAsync(timeout.Token);
                }
                Shutdown();
                return;
            }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..24];
            mutex = new(false, "Local\\HuntKillFiles-" + hash);
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                (await SendControl("show"))?.Dispose();
                Shutdown();
                return;
            }
            var errorFile = Path.Combine(runtime, "startup-error.txt");
            if (File.Exists(errorFile))
                File.Delete(errorFile);
            engine = new TrackerEngine(root, Option("--game-process") ?? "HuntGame");
            worker = Task.Run(engine.RunAsync);
            if (!e.Args.Contains("--headless"))
            {
                var window = new MainWindow(engine, worker, root, AppConfig.Load(root));
                MainWindow = window;
                engine.ShowRequested += () => Dispatcher.BeginInvoke(window.Restore);
                window.Show();
            }
            try
            {
                await worker;
            }
            catch (Exception error)
            {
                AtomicFile.Write(errorFile, AtomicFile.Protect(error.ToString()));
                if (!e.Args.Contains("--headless"))
                    MessageBox.Show(AtomicFile.Protect(error.Message), "Hunt Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
                (MainWindow as MainWindow)?.CompleteShutdown();
                Shutdown(1);
                return;
            }
            (MainWindow as MainWindow)?.CompleteShutdown();
            Shutdown();
        }
        catch (Exception error)
        {
            engine?.Stop();
            if (worker is not null)
                try
                {
                    await worker;
                }
                catch (Exception) { }
            (MainWindow as MainWindow)?.CompleteShutdown();
            try
            {
                AtomicFile.Write(Path.Combine(runtime, "startup-error.txt"), AtomicFile.Protect(error.ToString()));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!e.Args.Contains("--headless"))
                MessageBox.Show(AtomicFile.Protect(error.Message), "Hunt Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private async Task<System.Diagnostics.Process?> SendControl(string type)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var target = WriteControl(type);
                if (target is not null || attempt >= 19)
                    return target;
            }
            catch (IOException) when (attempt < 19) { }
            await Task.Delay(50);
        }
    }

    private System.Diagnostics.Process? WriteControl(string type)
    {
        var path = Path.Combine(root, "_runtime/state.json");
        if (!File.Exists(path))
            return null;
        using var state = JsonDocument.Parse(AtomicFile.Read(path));
        if (!state.RootElement.GetProperty("running").GetBoolean())
            return null;
        System.Diagnostics.Process? target;
        try
        {
            target = System.Diagnostics.Process.GetProcessById(state.RootElement.GetProperty("tracker_pid").GetInt32());
        }
        catch (ArgumentException) { return null; }
        try
        {
            if (target.HasExited || target.ProcessName != "HuntTracker")
            {
                target.Dispose();
                return null;
            }
            AtomicFile.Write(Path.Combine(root, "_runtime/" + type + ".json"), JsonSerializer.Serialize(new
            {
                session = state.RootElement.GetProperty("session").GetString()
            }));
            return target;
        }
        catch (InvalidOperationException) { target.Dispose(); return null; }
        catch { target.Dispose(); throw; }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
            mutex?.ReleaseMutex();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
