using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OtterMonitor.Core;

namespace OtterMonitor.App;

/// <summary>What a log window shows on each refresh.</summary>
public sealed record LogSnapshot(string Text, string Status, ServiceTone Tone, string? FilePath, string Source);

/// <summary>A run action shown in the log toolbar, enabled while <see cref="Enabled"/> says so.</summary>
public sealed record LogAction(string Glyph, string Text, string Tooltip, Action Run, Func<bool> Enabled, bool Danger = false);

/// <summary>Live output of a quick start or container: colored lines, filter, follow mode and the run's own actions.</summary>
public sealed class LogWindow : Window
{
    private readonly Func<Task<LogSnapshot>> read;
    private readonly ListBox list = new();
    private readonly TextBox filter = new();
    private readonly TextBlock statusText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock statusDot = new() { Text = "●", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock footer = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock empty = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Button followButton, copyButton, folderButton, toEnd = new();
    private readonly DispatcherTimer timer;
    private ObservableCollection<LogLine> lines = [];
    private ICollectionView? view;
    private ScrollViewer? scroll;
    private string lastText = "";
    private string? filePath;
    private bool following = true, reading, truncated;
    private readonly Action? refreshActions;

    private LogWindow(string title, string subtitle, TimeSpan interval, Func<Task<LogSnapshot>> read, IReadOnlyList<LogAction>? actions = null)
    {
        this.read = read;
        Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this);
        Title = title + UiText.T(" · Logg"); Width = 820; Height = 540; MinWidth = 520; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var muted = (Brush)FindResource("MutedBrush");
        var root = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });

        // Header: name and command on the left, live status on the right.
        var header = new DockPanel();
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 10, 4), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xCF, 0xDD, 0xFB)), VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { statusDot, statusText } } };
        DockPanel.SetDock(pill, Dock.Right); header.Children.Add(pill);
        var heading = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        heading.Children.Add(new TextBlock { Text = subtitle, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 11, Foreground = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = subtitle });
        header.Children.Add(heading);
        root.Children.Add(header);

        // Toolbar: filter on the left, actions on the right.
        var toolbar = new DockPanel { Margin = new Thickness(0, 14, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        followButton = Tool("", UiText.T("Följ"), UiText.T("Följ slutet av loggen (End)")); followButton.Click += (_, _) => SetFollowing(!following, scrollNow: true);
        copyButton = Tool("", UiText.T("Kopiera"), UiText.T("Kopiera markerade rader, annars alla som visas (Ctrl+C)")); copyButton.Click += (_, _) => Copy();
        folderButton = Tool("", UiText.T("Visa fil"), UiText.T("Visa loggfilen i Utforskaren")); folderButton.Click += (_, _) => RevealFile();
        foreach (var button in new[] { followButton, copyButton, folderButton }) buttons.Children.Add(button);
        var runActions = (actions ?? []).Select(action =>
        {
            var button = Tool(action.Glyph, action.Text, action.Tooltip);
            if (action.Danger) button.Foreground = (Brush)FindResource("RoseBrush");
            button.Click += (_, _) => { action.Run(); refreshActions?.Invoke(); };
            return (Button: button, action.Enabled);
        }).ToList();
        if (runActions.Count > 0) buttons.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), Background = (Brush)FindResource("DividerBrush") });
        foreach (var (button, _) in runActions) buttons.Children.Add(button);
        refreshActions = () => { foreach (var (button, enabled) in runActions) button.IsEnabled = enabled(); };
        DockPanel.SetDock(buttons, Dock.Right); toolbar.Children.Add(buttons);
        var hint = new TextBlock { Text = UiText.T("Filtrera rader…  (Ctrl+F)"), Foreground = muted, FontSize = 11, Margin = new Thickness(30, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = muted, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        filter.Padding = new Thickness(28, 6, 10, 6); filter.FontSize = 11; filter.MaxWidth = 300; filter.HorizontalAlignment = HorizontalAlignment.Left; filter.MinWidth = 200;
        filter.TextChanged += (_, _) => { hint.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ApplyFilter(); };
        filter.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && filter.Text.Length > 0) { filter.Text = ""; e.Handled = true; } };
        AutomationProperties.SetName(filter, UiText.T("Filtrera loggrader"));
        toolbar.Children.Add(new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { filter, glyph, hint } });
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);

        // The log itself, with a jump-to-end button while reading older lines.
        list.Style = (Style)FindResource("LogList"); list.ItemContainerStyle = (Style)FindResource("LogItem"); list.ItemTemplate = (DataTemplate)FindResource("LogLineTemplate");
        AutomationProperties.SetName(list, "Loggrader");
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Copy()));
        empty.Foreground = muted;
        toEnd.Content = UiText.T("↓  Till slutet"); toEnd.Style = (Style)FindResource("PrimaryButton"); toEnd.Padding = new Thickness(12, 6, 12, 6); toEnd.FontSize = 11;
        toEnd.HorizontalAlignment = HorizontalAlignment.Right; toEnd.VerticalAlignment = VerticalAlignment.Bottom; toEnd.Margin = new Thickness(0, 0, 18, 12); toEnd.Visibility = Visibility.Collapsed;
        toEnd.Click += (_, _) => SetFollowing(true, scrollNow: true);
        var body = new Grid { Children = { list, empty, toEnd } };
        Grid.SetRow(body, 2); root.Children.Add(body);

        footer.Foreground = muted; footer.Margin = new Thickness(2, 8, 2, 0);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { filter.Focus(); filter.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.End && e.OriginalSource is not TextBox) { SetFollowing(true, scrollNow: true); e.Handled = true; }
        };
        timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => _ = Refresh();
        Loaded += async (_, _) =>
        {
            list.ApplyTemplate();
            scroll = list.Template.FindName("LogScroll", list) as ScrollViewer;
            if (scroll is not null) scroll.ScrollChanged += OnScroll;
            await Refresh(); timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    private Button Tool(string glyph, string text, string tooltip)
    {
        var button = new Button { Style = (Style)FindResource("QuietButton"), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(2, 0, 0, 0), ToolTip = tooltip,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center } } } };
        AutomationProperties.SetName(button, text);
        return button;
    }

    internal async Task Refresh()
    {
        if (reading) return;
        reading = true;
        try
        {
            var snapshot = await read();
            statusText.Text = snapshot.Status;
            statusDot.Foreground = snapshot.Tone switch
            {
                ServiceTone.Good => new SolidColorBrush(Color.FromRgb(0xA3, 0xCD, 0xBC)), ServiceTone.Bad => (Brush)FindResource("RoseBrush"),
                ServiceTone.Warning => new SolidColorBrush(Color.FromRgb(0xE4, 0xCE, 0xA0)), _ => (Brush)FindResource("MutedBrush"),
            };
            filePath = snapshot.FilePath;
            folderButton.Visibility = filePath is null ? Visibility.Collapsed : Visibility.Visible;
            refreshActions?.Invoke();
            Update(snapshot.Text);
            footer.Text = $"{lines.Count} {(lines.Count == 1 ? "rad" : UiText.T("rader"))}" + (truncated ? " · senaste 64 kB" : "") + " · " + snapshot.Source;
            footer.ToolTip = filePath ?? snapshot.Source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            statusText.Text = UiText.T("Kan inte läsa loggen"); statusDot.Foreground = (Brush)FindResource("RoseBrush");
            footer.Text = ex.Message;
        }
        finally { reading = false; }
    }

    // Appends new output when the text only grew; otherwise (a restart, or the 64 kB window moved) starts over.
    private void Update(string text)
    {
        if (text == lastText) { UpdateEmpty(); return; }
        if (lastText.Length > 0 && text.StartsWith(lastText, StringComparison.Ordinal) && !text.StartsWith(LogLines.TruncatedMarker, StringComparison.Ordinal))
        {
            var start = lastText.LastIndexOf('\n') + 1;
            if (start < lastText.Length && lines.Count > 0) lines.RemoveAt(lines.Count - 1); // The previous last line was still being written.
            foreach (var line in LogLines.Parse(text[start..]).Lines) lines.Add(line);
        }
        else
        {
            var (parsed, cut) = LogLines.Parse(text);
            truncated = cut;
            lines = new ObservableCollection<LogLine>(parsed);
            view = CollectionViewSource.GetDefaultView(lines);
            list.ItemsSource = view;
            ApplyFilter();
        }
        lastText = text;
        UpdateEmpty();
        if (following) ScrollToEnd();
    }

    private void UpdateEmpty()
    {
        var visible = view?.Cast<object>().Any() == true;
        empty.Text = lines.Count == 0 ? UiText.T("Ingen utdata ännu.") : visible ? "" : UiText.F("Inga rader matchar \u201D{0}\u201D.", filter.Text.Trim());
        empty.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyFilter()
    {
        if (view is null) return;
        var query = filter.Text.Trim();
        view.Filter = query.Length == 0 ? null : item => ((LogLine)item).Text.Contains(query, StringComparison.OrdinalIgnoreCase);
        UpdateEmpty();
        if (following) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        list.UpdateLayout();
        scroll?.ScrollToEnd();
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        // Only the reader's own scrolling changes follow mode; new lines arriving keep it as it is.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || scroll is null) return;
        SetFollowing(scroll.VerticalOffset >= scroll.ScrollableHeight - 2, scrollNow: false);
    }

    private void SetFollowing(bool value, bool scrollNow)
    {
        following = value;
        followButton.Foreground = (Brush)FindResource(following ? "AccentBrush" : "MutedBrush");
        toEnd.Visibility = following || lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (following && scrollNow) ScrollToEnd();
    }

    private void Copy()
    {
        var chosen = list.SelectedItems.Count > 0 ? list.SelectedItems.Cast<LogLine>().OrderBy(lines.IndexOf) : view?.Cast<LogLine>() ?? [];
        var text = string.Join(Environment.NewLine, chosen.Select(line => line.Text));
        if (text.Length == 0) return;
        try { Clipboard.SetDataObject(text, true); }
        catch (System.Runtime.InteropServices.ExternalException) { Flash(copyButton, "Upptaget"); return; }
        Flash(copyButton, list.SelectedItems.Count > 0 ? UiText.F("Kopierade {0}", list.SelectedItems.Count) : UiText.T("Kopierat"));
    }

    private static void Flash(Button button, string text)
    {
        if (button.Content is not StackPanel { Children: [_, TextBlock label] }) return;
        var original = label.Tag as string ?? label.Text;
        label.Tag = original; label.Text = text;
        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        reset.Tick += (_, _) => { reset.Stop(); label.Text = original; };
        reset.Start();
    }

    private void RevealFile()
    {
        if (filePath is null || !File.Exists(filePath)) return;
        // Our own log path; quoted for paths with spaces, which explorer's /select expects in this form.
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
    }

    public static LogWindow ForQuickStart(QuickStart entry, Action<QuickStart> restart, Func<QuickStart, Task> stop) =>
        new(entry.Name, entry.Command + "   ·   " + entry.Directory, TimeSpan.FromSeconds(1), () =>
        {
            // Follow the quick start into its new log after a restart.
            var session = entry.Session;
            return Task.FromResult(new LogSnapshot(session?.ReadLog() ?? "", entry.Status,
                entry.HasFailed || entry.RecoveryError is not null ? ServiceTone.Bad : entry.IsRunning ? ServiceTone.Good : ServiceTone.Neutral,
                session?.LogPath, session is null ? UiText.T("väntar på nästa körning") : Path.GetFileName(session.LogPath)));
        },
        [
            new("\uE72C", UiText.T("Starta om"), UiText.T("Avsluta och kör samma kommando igen"), () => restart(entry), () => entry.CanRestart),
            new("\uE71A", UiText.T("Stoppa"), UiText.T("Stoppa kommandot och alla processer det startade"), () => _ = stop(entry), () => entry.IsRunning && !entry.IsBusy, Danger: true),
        ]);

    public static LogWindow ForContainer(PortEntry container) => new(container.Name, "docker logs " + container.ContainerId, TimeSpan.FromSeconds(2), async () =>
        new LogSnapshot(await DockerService.LogsAsync(container), "Container", ServiceTone.Good, null, "docker logs · senaste 400 raderna, stdout och stderr i tidsordning"));

    internal const string ExampleText = """
        [nuxt] ℹ Compiled nuxt-icon-client-bundle.mjs in 556.82ms
        [nuxt] ℹ Compiled types/runtime-config.d.ts in 559.85ms
        ✓ Vite client built in 114ms
        ✓ Vite server built in 140ms
        [nitro] ✓ Nuxt Nitro server built in 2481ms
         WARN  Browserslist: browsers data (caniuse-lite) is 10 months old. Please run:
          npx update-browserslist-db@latest
        08:14:02  GET /api/auctions 200 in 41ms
        08:14:05  ERROR  [nuxt] Cannot read properties of undefined (reading 'id')
          at setup (pages/auction/[id].vue:12:18)
        ✓ Vite server hmr 1 files in 0.003ms
        """;

    internal async Task VerifyLog(Action<bool, string> check, Func<string> current, Action<string> append)
    {
        await Refresh(); UpdateLayout();
        var parsed = LogLines.Parse(current()).Lines;
        check(lines.Count == parsed.Count && lines[0].Prefix == "[nuxt] " && lines.Count(line => line.Level == LogLevel.Error) == 1
            && lines.Count(line => line.Level == LogLevel.Warning) == 1 && lines.Count(line => line.Level == LogLevel.Success) == 4,
            "Log lines are split into a dimmed prefix and colored by level");
        var errorRow = list.ItemContainerGenerator.ContainerFromItem(lines.First(line => line.Level == LogLevel.Error)) as ListBoxItem;
        check(errorRow is not null && FindLine(errorRow)?.Foreground == FindResource("RoseBrush"), "An error line is shown in rose");
        check(footer.Text.StartsWith($"{lines.Count} rader") && statusText.Text == "Körs" && folderButton.Visibility == Visibility.Collapsed && !toEnd.IsVisible,
            "The footer counts lines and the status pill shows the run state");
        for (var i = 0; i < 80; i++) append($"08:15:{i % 60:00}  GET /api/item/{i} 200 in {i}ms\n");
        await Refresh(); UpdateLayout();
        check(lines.Count == parsed.Count + 80 && scroll is not null && scroll.VerticalOffset >= scroll.ScrollableHeight - 2 && scroll.ScrollableHeight > 0,
            "New output is appended and the view follows the end");
        scroll!.ScrollToTop(); UpdateLayout(); // Raises the same ScrollChanged a mouse wheel would.
        check(!following && toEnd.IsVisible, "Scrolling up pauses following and offers a jump to the end");
        append("08:16:00  late line\n"); await Refresh(); UpdateLayout();
        check(scroll.VerticalOffset < 2 && lines[^1].Body == "late line", "New lines do not pull the reader away from older output");
        toEnd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        check(following && !toEnd.IsVisible && scroll.VerticalOffset >= scroll.ScrollableHeight - 2, "The jump button resumes following at the end");
        filter.Text = "error"; UpdateLayout();
        check(view!.Cast<LogLine>().Count() == 1 && !empty.IsVisible, "The filter shows only matching lines");
        filter.Text = "finns-inte"; UpdateLayout();
        check(empty.IsVisible && empty.Text.Contains("finns-inte"), "An empty filter result says so");
        filter.Text = "";
        check(view!.Cast<LogLine>().Count() == lines.Count, "Clearing the filter shows every line again");
    }

    private static TextBlock? FindLine(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock { Name: "Line" } line) return line;
            if (FindLine(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>For previews and checks: fixed text and status.</summary>
    internal static LogWindow ForText(string title, string subtitle, Func<string> text, string? status = null, ServiceTone tone = ServiceTone.Good) =>
        new(title, subtitle, TimeSpan.FromSeconds(1), () => Task.FromResult(new LogSnapshot(text(), status ?? UiText.T("Körs"), tone, null, UiText.T("exempel.log"))));
}
