using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OtterMonitor.Core;

namespace OtterMonitor.App;

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

/// <summary>A found project in the picker; <see cref="Note"/> marks one that already has a quick start.</summary>
public sealed record ProjectChoice(ProjectCandidate Candidate, string Note);

public sealed class QuickStartDialog : Window
{
    private readonly TextBox name = new(), directory = new(), command = new(), search = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(243, 194, 215)), Margin = new Thickness(0, 14, 0, 0) };
    private readonly ListBox projects = new(), commands = new();
    private readonly TextBlock scanStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 24, 0), IsHitTestVisible = false };
    private readonly Button rootsButton = new();
    private readonly TextBlock commandHint = new() { Text = "Välj ett projekt så visas dess skript här. Dubbelklick sparar direkt.", FontSize = 11, Opacity = 0.8 };
    private Thickness labelMargin = new(0, 16, 0, 7);
    private readonly FrameworkElement? commandRow;
    private readonly IReadOnlyCollection<QuickStart> added;
    private readonly Func<List<string>, bool>? saveRoots;
    private readonly Button save = new() { Content = "Spara snabbstart", IsDefault = true };
    private List<string> roots;
    private List<ProjectChoice> choices = [];
    private CancellationTokenSource? scanning;
    private string? autoName;
    private readonly QuickStart? existing;
    public QuickStart? Result { get; private set; }
    public bool DeleteRequested { get; private set; }
    /// <summary>Completes when the current project search has finished (for previews and checks).</summary>
    public Task Scan { get; private set; } = Task.CompletedTask;

    /// <param name="projectRoots">Folders to search for projects. With a new quick start, the dialog opens with a project list.</param>
    public QuickStartDialog(QuickStart? existing = null, IReadOnlyList<string>? projectRoots = null, IReadOnlyCollection<QuickStart>? added = null, Func<List<string>, bool>? saveRoots = null)
    {
        this.existing = existing; this.added = added ?? []; this.saveRoots = saveRoots;
        roots = projectRoots?.ToList() ?? [];
        var picker = existing is null && projectRoots is not null;
        Title = existing is null ? "Ny snabbstart" : "Redigera snabbstart";
        Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this);
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(25) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = picker ? "Välj ett projekt och ett kommando, eller fyll i själv." : "En arbetsmapp. Ett kommando. Ett klick.", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 7, 0, 8) });
        if (picker) { commandRow = BuildPicker(panel); labelMargin = new(0, 12, 0, 6); }
        Label(panel, "Namn"); panel.Children.Add(name);
        Label(panel, "Arbetsmapp");
        var directoryRow = new DockPanel();
        var browse = new Button { Content = "Välj…", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog { Title = "Välj projektets arbetsmapp" }; if (Directory.Exists(directory.Text)) dialog.InitialDirectory = directory.Text; if (dialog.ShowDialog(this) == true) directory.Text = dialog.FolderName; };
        directoryRow.Children.Add(browse); directoryRow.Children.Add(directory); panel.Children.Add(directoryRow);
        Label(panel, "Kommando"); command.FontFamily = new FontFamily("Consolas"); panel.Children.Add(command);
        panel.Children.Add(new TextBlock { Text = picker ? "Körs i bakgrunden via cmd.exe. Utdata visas under Logg." : "Exempel: bun run dev, npm run dev eller docker compose up -d.\nKörs i bakgrunden via cmd.exe. Utdata visas under Logg.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 9, 0, 0) });
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
        save.Style = (Style)FindResource("PrimaryButton");
        save.Click += (_, _) => Submit();
        right.Children.Add(save); buttons.Children.Add(right); panel.Children.Add(buttons); Content = panel;
        name.Text = existing?.Name ?? ""; directory.Text = existing?.Directory ?? ""; command.Text = existing?.Command ?? "";
        Loaded += (_, _) => { if (picker) { search.Focus(); StartScan(); } else name.Focus(); };
        Closed += (_, _) => scanning?.Cancel();
    }

    private FrameworkElement BuildPicker(Panel panel)
    {
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        rootsButton.Style = (Style)FindResource("QuietButton"); rootsButton.Padding = new Thickness(10, 6, 10, 6); rootsButton.Margin = new Thickness(8, 0, 0, 0);
        rootsButton.Click += (_, _) => ChooseRoots();
        DockPanel.SetDock(rootsButton, Dock.Right); header.Children.Add(rootsButton);
        var hint = new TextBlock { Text = "Sök projekt, mapp eller kommando…", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, Margin = new Thickness(15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        search.TextChanged += (_, _) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ShowChoices(); };
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Down or Key.Enter) || projects.Items.Count == 0) return;
            e.Handled = true;
            projects.SelectedIndex = Math.Max(0, projects.SelectedIndex);
            if (e.Key == Key.Down && projects.ItemContainerGenerator.ContainerFromIndex(projects.SelectedIndex) is ListBoxItem item) item.Focus();
        };
        AutomationProperties.SetName(search, "Sök projekt");
        header.Children.Add(new Grid { Children = { search, hint } });
        panel.Children.Add(header);

        projects.Style = (Style)FindResource("PickerList"); projects.ItemContainerStyle = (Style)FindResource("PickerItem");
        projects.ItemTemplate = (DataTemplate)FindResource("ProjectCandidateTemplate"); projects.Height = 160;
        projects.SelectionChanged += (_, _) => SelectProject();
        // A double-click takes the recommended command and saves straight away.
        projects.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(projects, source) is ListBoxItem) { SelectProject(); Submit(); } };
        AutomationProperties.SetName(projects, "Hittade projekt");
        scanStatus.Foreground = (Brush)FindResource("MutedBrush");
        panel.Children.Add(new Grid { Children = { projects, scanStatus } });

        // Always present, so choosing a project never changes the dialog's height.
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0), MinHeight = 52 };
        row.Children.Add(new TextBlock { Text = "Kommandon i projektet", FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 6) });
        commandHint.Foreground = (Brush)FindResource("MutedBrush");
        row.Children.Add(commandHint);
        commands.Style = (Style)FindResource("ChipList"); commands.ItemContainerStyle = (Style)FindResource("ChipItem");
        commands.ItemTemplate = (DataTemplate)FindResource("ProjectCommandTemplate");
        commands.SelectionChanged += (_, _) => SelectCommand();
        commands.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(commands, "Kommandon i projektet");
        // Some projects have dozens of scripts; two rows show the likely ones and the rest scroll.
        row.Children.Add(new ScrollViewer { Content = commands, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        panel.Children.Add(row);
        panel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("DividerBrush"), Margin = new Thickness(0, 8, 0, 0) });
        UpdateRootsButton();
        return row;
    }

    private void StartScan()
    {
        scanning?.Cancel();
        var current = scanning = new CancellationTokenSource();
        var folders = roots.ToList();
        choices = []; ShowChoices();
        scanStatus.Text = folders.Count == 0 ? "Välj en mapp med dina projekt via knappen ovan." : "Söker efter projekt i " + string.Join(", ", folders) + "…";
        scanStatus.Visibility = Visibility.Visible;
        if (folders.Count == 0) return;
        Scan = Run();
        async Task Run()
        {
            try
            {
                var found = await Task.Run(() => ProjectScanner.Scan(folders, current.Token), current.Token);
                if (current.IsCancellationRequested) return;
                choices = found.Select(candidate => new ProjectChoice(candidate, Note(candidate))).ToList();
                scanStatus.Text = choices.Count == 0 ? "Inga projekt hittades i " + string.Join(", ", folders) + ". Välj andra mappar eller fyll i nedan." : "";
                ShowChoices();
            }
            catch (OperationCanceledException) { }
        }
    }

    private string Note(ProjectCandidate candidate)
    {
        var same = added.Where(entry => string.Equals(Path.TrimEndingDirectorySeparator(entry.Directory), candidate.Directory, StringComparison.OrdinalIgnoreCase)).ToList();
        return same.Any(entry => candidate.Commands.Any(option => string.Equals(option.Command, entry.Command, StringComparison.OrdinalIgnoreCase))) ? "tillagd" : "";
    }

    private void ShowChoices()
    {
        var query = search.Text.Trim();
        var selected = (projects.SelectedItem as ProjectChoice)?.Candidate;
        var visible = choices.Where(choice => query.Length == 0 || choice.Candidate.Matches(query)).ToList();
        projects.ItemsSource = visible;
        projects.SelectedItem = visible.FirstOrDefault(choice => choice.Candidate == selected);
        if (choices.Count > 0) scanStatus.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (choices.Count > 0 && visible.Count == 0) scanStatus.Text = $"Inget projekt matchar ”{query}”.";
    }

    private void SelectProject()
    {
        if (projects.SelectedItem is not ProjectChoice { Candidate: var candidate }) return;
        directory.Text = candidate.Directory;
        commands.ItemsSource = candidate.Commands;
        commands.Visibility = Visibility.Visible; commandHint.Visibility = Visibility.Collapsed;
        commands.SelectedItem = candidate.Recommended;
        SelectCommand();
    }

    private void SelectCommand()
    {
        if (projects.SelectedItem is not ProjectChoice { Candidate: var candidate } || commands.SelectedItem is not ProjectCommand option) return;
        command.Text = option.Command;
        // Keep a name the user typed; replace only an empty field or a name this picker suggested.
        var suggested = option.Recommended ? candidate.Name : candidate.Name + " · " + option.Label;
        if (string.IsNullOrWhiteSpace(name.Text) || name.Text == autoName) name.Text = autoName = suggested;
        error.Text = "";
    }

    private void ChooseRoots()
    {
        var dialog = new OpenFolderDialog { Title = "Välj mappar med dina projekt", Multiselect = true };
        if (roots.FirstOrDefault(Directory.Exists) is { } first) dialog.InitialDirectory = first;
        if (dialog.ShowDialog(this) != true || dialog.FolderNames.Length == 0) return;
        var chosen = dialog.FolderNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (saveRoots is not null && !saveRoots(chosen)) return;
        roots = chosen; UpdateRootsButton(); StartScan();
    }

    private void UpdateRootsButton()
    {
        rootsButton.Content = roots.Count == 0 ? "Välj mapp…" : "\uE8B7  " + roots[0] + (roots.Count > 1 ? $" +{roots.Count - 1}" : "");
        rootsButton.FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Segoe Fluent Icons, Segoe MDL2 Assets");
        rootsButton.ToolTip = (roots.Count == 0 ? "Inga mappar valda." : "Söker i:\n" + string.Join("\n", roots)) + "\nKlicka för att välja andra mappar. Valet sparas.";
        AutomationProperties.SetName(rootsButton, "Välj projektmappar");
    }

    private void Submit()
    {
        var folder = Environment.ExpandEnvironmentVariables(directory.Text.Trim());
        if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Ange ett namn eller välj ett projekt."; return; }
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) { error.Text = "Välj en befintlig arbetsmapp med absolut sökväg."; return; }
        if (string.IsNullOrWhiteSpace(command.Text) || command.Text.IndexOfAny(['\r', '\n', '\0']) >= 0) { error.Text = "Ange ett kommando på en rad."; return; }
        Result = new() { Id = existing?.Id ?? Guid.NewGuid(), Name = name.Text.Trim(), Directory = folder, Command = command.Text.Trim() }; DialogResult = true;
    }

    private void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Margin = labelMargin, FontWeight = FontWeights.SemiBold, FontSize = 12 });

    /// <summary>Preview: show the first project selected, as after a click.</summary>
    public void SelectFirstProject() { projects.SelectedIndex = projects.Items.Count > 0 ? 0 : -1; UpdateLayout(); }

    internal async Task VerifyPicker(Action<bool, string> check)
    {
        await Scan; UpdateLayout();
        check(projects.Items.Count >= 3 && scanStatus.Visibility == Visibility.Collapsed && search.IsKeyboardFocused && commandHint.IsVisible && !commands.IsVisible,
            "The new quick start dialog lists found projects with the search focused");
        check(projects.Items.Cast<ProjectChoice>().Single(choice => choice.Candidate.Name == "shop").Note == "tillagd" && projects.Items.Cast<ProjectChoice>().Single(choice => choice.Candidate.Name == "api").Note == "",
            "A project that already has a quick start is marked as added");
        search.Text = "api"; UpdateLayout();
        check(projects.Items.Count == 1 && ((ProjectChoice)projects.Items[0]).Candidate.Name == "api", "Searching narrows the project list");
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); UpdateLayout();
        check(name.Text == "api" && command.Text == "bun run dev" && directory.Text.EndsWith("api") && commands.IsVisible && !commandHint.IsVisible && commands.Items.Count == 2,
            "Enter in the search picks the first project and fills name, folder and its recommended command");
        commands.SelectedIndex = 1; UpdateLayout();
        check(command.Text == "bun run build" && name.Text == "api · build" && ((ListBoxItem)commands.ItemContainerGenerator.ContainerFromIndex(1)).ActualHeight <= 28,
            "Choosing another script updates the command and the suggested name");
        name.Text = "Mitt API"; commands.SelectedIndex = 0;
        check(command.Text == "bun run dev" && name.Text == "Mitt API", "A name typed by the user is kept when the script changes");
        search.Text = "finns-inte"; UpdateLayout();
        check(projects.Items.Count == 0 && scanStatus.IsVisible && scanStatus.Text.Contains("finns-inte"), "An empty search says so in the list");
        search.Text = ""; UpdateLayout();
        check(ActualHeight <= 760 && ActualWidth == 560 && commandRow!.ActualHeight >= 52, $"The dialog with the project list fits on a laptop screen and keeps its height ({ActualHeight:0} px)");
    }
}
public sealed class LogWindow : Window
{
    private LogWindow(string title, TimeSpan interval, Func<Task<(string Heading, string Text)>> read)
    {
        Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this);
        Title = title + " · Logg"; Width = 750; Height = 450; MinWidth = 450; MinHeight = 260; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(18) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var heading = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        grid.Children.Add(heading);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        Grid.SetRow(output, 1); grid.Children.Add(output); Content = grid;
        var timer = new DispatcherTimer { Interval = interval };
        var reading = false;
        async void Read()
        {
            if (reading) return;
            reading = true;
            try
            {
                var (top, text) = await read();
                heading.Text = top;
                // Keep the reader's scroll position unless it was already following the end.
                var following = output.VerticalOffset + output.ViewportHeight >= output.ExtentHeight - 4;
                if (text != output.Text) { output.Text = text; if (following) output.ScrollToEnd(); }
            }
            catch (Exception ex) { output.Text = ex.Message; }
            finally { reading = false; }
        }
        timer.Tick += (_, _) => Read(); Loaded += (_, _) => { Read(); output.ScrollToEnd(); timer.Start(); }; Closed += (_, _) => timer.Stop();
    }

    // Follow the quick start into its new log after a restart.
    public static LogWindow ForQuickStart(QuickStart entry) => new(entry.Name, TimeSpan.FromSeconds(1), () =>
        Task.FromResult((entry.Command + "\n" + (entry.Session?.LogPath ?? "Väntar på nästa körning…"), entry.Session?.ReadLog() ?? "")));

    public static LogWindow ForContainer(PortEntry container) => new(container.Name, TimeSpan.FromSeconds(2), async () =>
        ($"docker logs · {container.Name} · {container.ContainerId}\nSenaste 400 raderna, stdout och stderr i tidsordning. Uppdateras varannan sekund.", await DockerService.LogsAsync(container)));
}
