using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PortPilot.Core;

namespace PortPilot.App;

public static class Dialogs
{
    public static bool Confirm(Window owner, string title, string message, string action) => Show(owner, title, message, action, true);
    public static void Info(Window owner, string title, string message) => Show(owner, title, message, "OK", false);
    private static bool Show(Window owner, string title, string message, string action, bool cancel)
    {
        var window = new Window { Title = title, Owner = owner, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        _ = new WindowMaterial(window);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)window.FindResource("MutedBrush"), MaxHeight = 350 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        if (cancel) { var no = new Button { Content = "Avbryt", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) }; buttons.Children.Add(no); }
        var yes = new Button { Content = action, MinWidth = 90, IsDefault = !cancel, Style = (Style)window.FindResource(cancel ? "StopButton" : "PrimaryButton") };
        yes.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(yes); panel.Children.Add(buttons); window.Content = panel;
        return window.ShowDialog() == true;
    }
}

public sealed class QuickStartDialog : Window
{
    private readonly TextBox name = new(), directory = new(), command = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(243, 194, 215)), Margin = new Thickness(0, 14, 0, 0) };
    public QuickStart? Result { get; private set; }
    public bool DeleteRequested { get; private set; }
    public QuickStartDialog(QuickStart? existing = null)
    {
        Title = existing is null ? "Ny snabbstart" : "Redigera snabbstart";
        Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this);
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(25) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "En arbetsmapp. Ett kommando. Ett klick.", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 7, 0, 8) });
        Label(panel, "Namn"); panel.Children.Add(name);
        Label(panel, "Arbetsmapp");
        var directoryRow = new DockPanel();
        var browse = new Button { Content = "Välj…", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "Välj projektets arbetsmapp" }; if (Directory.Exists(directory.Text)) picker.InitialDirectory = directory.Text; if (picker.ShowDialog(this) == true) directory.Text = picker.FolderName; };
        directoryRow.Children.Add(browse); directoryRow.Children.Add(directory); panel.Children.Add(directoryRow);
        Label(panel, "Kommando"); command.FontFamily = new FontFamily("Consolas"); panel.Children.Add(command);
        panel.Children.Add(new TextBlock { Text = "Exempel: bun run dev, npm run dev eller docker compose up -d.\nKörs i bakgrunden via cmd.exe. Utdata visas under Logg.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 9, 0, 0) });
        panel.Children.Add(error);
        var buttons = new DockPanel { Margin = new Thickness(0, 15, 0, 0) };
        if (existing is not null)
        {
            var delete = new Button { Content = "Ta bort", Style = (Style)FindResource("StopButton") }; DockPanel.SetDock(delete, Dock.Left);
            delete.Click += (_, _) => { if (Dialogs.Confirm(this, "Ta bort snabbstart?", $"Ta bort {existing.Name} från listan? Projektfilerna påverkas inte.", "Ta bort")) { DeleteRequested = true; DialogResult = true; } };
            buttons.Children.Add(delete);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new Button { Content = "Avbryt", IsCancel = true, Margin = new Thickness(0, 0, 9, 0) });
        var save = new Button { Content = "Spara snabbstart", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            var folder = Environment.ExpandEnvironmentVariables(directory.Text.Trim());
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Ange ett namn."; return; }
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) { error.Text = "Välj en befintlig arbetsmapp med absolut sökväg."; return; }
            if (string.IsNullOrWhiteSpace(command.Text) || command.Text.IndexOfAny(['\r', '\n', '\0']) >= 0) { error.Text = "Ange ett kommando på en rad."; return; }
            Result = new() { Id = existing?.Id ?? Guid.NewGuid(), Name = name.Text.Trim(), Directory = folder, Command = command.Text.Trim() }; DialogResult = true;
        };
        right.Children.Add(save); buttons.Children.Add(right); panel.Children.Add(buttons); Content = panel;
        name.Text = existing?.Name ?? ""; directory.Text = existing?.Directory ?? ""; command.Text = existing?.Command ?? "";
        Loaded += (_, _) => name.Focus();
    }
    private static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 16, 0, 7), FontWeight = FontWeights.SemiBold, FontSize = 12 });
}

public sealed class LogWindow : Window
{
    public LogWindow(QuickStart entry)
    {
        Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this);
        Title = entry.Name + " · Logg"; Width = 750; Height = 450; MinWidth = 450; MinHeight = 260; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(18) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var heading = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        grid.Children.Add(heading);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        Grid.SetRow(output, 1); grid.Children.Add(output); Content = grid;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Read()
        {
            try
            {
                // Follow the quick start into its new log after a restart.
                var session = entry.Session;
                heading.Text = entry.Command + "\n" + (session?.LogPath ?? "Väntar på nästa körning…");
                var text = session?.ReadLog() ?? "";
                if (text != output.Text) { output.Text = text; output.ScrollToEnd(); }
            }
            catch (Exception ex) { output.Text = ex.Message; }
        }
        timer.Tick += (_, _) => Read(); Loaded += (_, _) => { Read(); timer.Start(); }; Closed += (_, _) => timer.Stop();
    }
}
