using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PortPilot.Core;

namespace PortPilot.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--verify-claude-browser"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var reportIndex = Array.IndexOf(e.Args, "--report");
            var rootIndex = Array.IndexOf(e.Args, "--test-root");
            var phaseIndex = Array.IndexOf(e.Args, "--session-phase");
            if (reportIndex < 0 || rootIndex < 0 || reportIndex + 1 >= e.Args.Length || rootIndex + 1 >= e.Args.Length) { Shutdown(1); return; }
            try { File.WriteAllText(e.Args[reportIndex + 1], await ClaudeUsageBrowser.VerifyAsync(e.Args[rootIndex + 1], phaseIndex >= 0 && phaseIndex + 1 < e.Args.Length ? e.Args[phaseIndex + 1] : "reader")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[reportIndex + 1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] is "--install-claude-usage" or "--remove-claude-usage")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var bridge = new ClaudeUsageInstaller(SettingsStore.DefaultDirectory, ClaudeUsageInstaller.DefaultSettingsFile);
                if (e.Args[0] == "--install-claude-usage") bridge.Install(); else bridge.Uninstall();
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--verify-billing-session"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var reportIndex = Array.IndexOf(e.Args, "--report");
            var rootIndex = Array.IndexOf(e.Args, "--test-root");
            var phaseIndex = Array.IndexOf(e.Args, "--session-phase");
            if (reportIndex < 0 || rootIndex < 0 || phaseIndex < 0 || reportIndex + 1 >= e.Args.Length || rootIndex + 1 >= e.Args.Length || phaseIndex + 1 >= e.Args.Length) { Shutdown(1); return; }
            try { File.WriteAllText(e.Args[reportIndex + 1], await BillingBrowser.VerifySessionAsync(e.Args[rootIndex + 1], e.Args[phaseIndex + 1], e.Args.Contains("--baseline"))); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[reportIndex + 1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--verify-background"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var reportIndex = Array.IndexOf(e.Args, "--report");
            var rootIndex = Array.IndexOf(e.Args, "--test-root");
            if (reportIndex < 0 || rootIndex < 0 || reportIndex + 1 >= e.Args.Length || rootIndex + 1 >= e.Args.Length) { Shutdown(1); return; }
            try { File.WriteAllText(e.Args[reportIndex + 1], await BillingBrowser.VerifyAsync(e.Args[rootIndex + 1])); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[reportIndex + 1], ex.ToString()); Shutdown(1); }
            return;
        }
        // Retired browser invocations must not open a regular desktop window.
        if (e.Args.FirstOrDefault()?.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) == true)
        {
            Shutdown(1); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--keep-launch")
        {
            try { Shutdown(LaunchSession.HoldJob(e.Args[1], e.Args[2])); }
            catch { Shutdown(1); }
            return;
        }
        var preview = e.Args.Contains("--preview");
        var capture = e.Args.Contains("--capture-live");
        if ((preview || capture) && !e.Args.Contains("--stay-open"))
        {
            // Headless preview failures must report an error instead of leaving an invisible dialog open.
            DispatcherUnhandledException += (_, error) =>
            {
                var reportIndex = Array.IndexOf(e.Args, "--report");
                var outputIndex = Array.IndexOf(e.Args, "--output");
                var errorPath = reportIndex >= 0 && reportIndex + 1 < e.Args.Length ? e.Args[reportIndex + 1]
                    : outputIndex >= 0 && outputIndex + 1 < e.Args.Length ? Path.ChangeExtension(e.Args[outputIndex + 1], ".error.txt") : null;
                if (errorPath is not null) File.WriteAllText(errorPath, error.Exception.ToString());
                error.Handled = true; Shutdown(1);
            };
        }
        if (!preview && !capture)
        {
            instance = new Mutex(true, "Local\\PortPilot.Desktop", out var first);
            if (!first) { MessageBox.Show("DevGeek körs redan. Du hittar fönstret i aktivitetsfältet.", "DevGeek"); Shutdown(); return; }
        }
        var store = new SettingsStore(SettingsStore.DefaultDirectory);
        Settings settings;
        try { settings = preview || capture ? new Settings() : store.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Inställningarna kunde inte läsas. Filen har inte ändrats.\n\n{store.FilePath}\n\n{ex.Message}", "DevGeek", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        var main = new MainWindow(store, settings, preview || capture);
        MainWindow = main;
        if (preview || capture)
        {
            var stayOpen = e.Args.Contains("--stay-open");
            if (stayOpen)
            {
                main.Title = "DevGeek · Designförhandsvisning";
                if (e.Args.Contains("--glass")) main.EnableGlassPreview();
            }
            else
            {
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Left = -16000; main.Top = -16000; main.ShowInTaskbar = false; main.ShowActivated = false;
            }
            if (preview) main.LoadPreview(e.Args.Contains("--empty"));
            if (preview && e.Args.Contains("--group-example")) main.LoadGroupExamplePreview(e.Args.Contains("--expanded"));
            main.Show();
            if (capture) await main.RefreshAllForCaptureAsync();
            var materialIndex = Array.IndexOf(e.Args, "--material-report");
            if (materialIndex >= 0 && materialIndex + 1 < e.Args.Length) File.WriteAllText(e.Args[materialIndex + 1], main.DescribeMaterial());
            if (e.Args.Contains("--verify-ui"))
            {
                var reportIndex = Array.IndexOf(e.Args, "--report");
                var report = await main.VerifyPreviewControlsAsync();
                if (reportIndex >= 0 && reportIndex + 1 < e.Args.Length) File.WriteAllText(e.Args[reportIndex + 1], report);
            }
            Window renderedWindow = main;
            if (e.Args.Contains("--dialog"))
            {
                renderedWindow = new QuickStartDialog(new QuickStart { Name = "Starta dev miljö", Directory = @"C:\repos\demo-app", Command = "bun run dev" }) { Owner = main, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false };
                renderedWindow.Show();
            }
            if (e.Args.Contains("--balance-dialog"))
            {
                renderedWindow = main.CreateBalanceDialog();
                renderedWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                renderedWindow.Left = -16000; renderedWindow.Top = -16000;
                renderedWindow.ShowActivated = false;
                renderedWindow.Show();
            }
            if (e.Args.Contains("--appearance-dialog"))
            {
                renderedWindow = main.CreateAppearanceDialog();
                renderedWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                renderedWindow.Left = -16000; renderedWindow.Top = -16000; renderedWindow.ShowActivated = false;
                renderedWindow.Show();
            }
            if (stayOpen) return;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            renderedWindow.UpdateLayout();
            var pathIndex = Array.IndexOf(e.Args, "--output");
            var path = pathIndex >= 0 && pathIndex + 1 < e.Args.Length ? e.Args[pathIndex + 1] : Path.Combine(Environment.CurrentDirectory, "portpilot-preview.png");
            var visual = renderedWindow;
            // RenderTargetBitmap cannot capture the compositor's desktop blur. Static previews show the solid fallback.
            visual.Background = (Brush)FindResource("BackgroundBrush");
            var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            main.Close(); Shutdown(); return;
        }
        main.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
