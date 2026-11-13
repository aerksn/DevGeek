using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OtterMonitor.Core;

namespace OtterMonitor.App;

/// <summary>Tray icon, global hotkey, notices and type-to-filter.</summary>
public partial class MainWindow
{
    private TrayIcon? tray;
    private readonly NoticeTracker notices = new();
    private bool exitRequested, quietExit, trayHintShown, startInTray;
    private ContextMenu? trayMenu;

    private void StartTray()
    {
        if (preview || tray is not null) return;
        try { tray = new TrayIcon("Otter Monitor"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException) { tray = null; return; }
        tray.Activated += ToggleFromTray;
        tray.HotkeyPressed += ToggleFromTray;
        tray.NoticeClicked += ShowFromTray;
        tray.MenuRequested += ShowTrayMenu;
        UpdateTray();
    }

    public void ShowFromTray()
    {
        if (closed) return;
        if (!IsVisible) { ShowInTaskbar = true; Show(); }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // A focused row keeps keyboard use going straight away.
        if (Keyboard.FocusedElement is null || !IsKeyboardFocusWithin) FocusFirstRow();
    }

    private void ToggleFromTray()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) Hide(); else ShowFromTray();
    }

    private void ShowTrayMenu()
    {
        trayMenu ??= BuildTrayMenu();
        ((MenuItem)trayMenu.Items[0]).Header = IsVisible ? UiText.T("Dölj Otter Monitor") : UiText.T("Visa Otter Monitor");
        trayMenu.Placement = PlacementMode.MousePoint; trayMenu.IsOpen = true;
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu { Template = (ControlTemplate)FindResource("AppearanceMenuTemplate"), FontSize = 11, Foreground = (Brush)FindResource("TextBrush") };
        var toggle = new MenuItem(); toggle.Click += (_, _) => { if (IsVisible) Hide(); else ShowFromTray(); };
        var add = new MenuItem { Header = UiText.T("Ny snabbstart…") }; add.Click += (_, _) => { ShowFromTray(); Add_Click(this, new()); };
        var exit = new MenuItem { Header = UiText.T("Avsluta Otter Monitor") }; exit.Click += (_, _) => ExitApplication();
        foreach (var item in new[] { toggle, add, exit }) { item.Style = (Style)FindResource("AppearanceMenuItem"); menu.Items.Add(item); }
        menu.Items.Insert(2, new Separator { Style = (Style)FindResource("MenuSeparator") });
        return menu;
    }

    /// <param name="quiet">From the installer or a sign-out: no questions. Started quick starts keep running, as on every exit.</param>
    public void ExitApplication(bool quiet = false)
    {
        exitRequested = true; quietExit = quiet;
        if (!IsVisible && !quiet) Show();
        Close();
        // The window stays open when the user cancels the exit; hiding to the tray is then possible again.
        if (!closed) exitRequested = quietExit = false;
    }

    public void StartInTray()
    {
        startInTray = true;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = Top = -32000; ShowActivated = false; ShowInTaskbar = false;
        // Hiding during Loaded is undone by the rest of Show(); after the first render it sticks.
        ContentRendered += FinishTrayStart;
    }

    // Hide to the tray and place the window where it will appear later.
    private void FinishTrayStart(object? sender, EventArgs e)
    {
        ContentRendered -= FinishTrayStart;
        if (!startInTray) return;
        startInTray = false;
        // Changing ShowInTaskbar on a hidden window shows it again, so ShowFromTray restores it.
        if (tray is not null) Hide(); else ShowInTaskbar = true;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2; Top = area.Top + (area.Height - ActualHeight) / 2;
        ShowActivated = true;
    }

    /// <summary>Close (X, Alt+F4) keeps Otter Monitor watching from the notification area unless that is turned off.</summary>
    private bool HideToTray(CancelEventArgs e)
    {
        if (preview || exitRequested || tray is null || !settings.CloseToTray) return false;
        e.Cancel = true; Hide();
        if (!trayHintShown)
        {
            trayHintShown = true;
            tray.Show(new(UiText.T("Otter Monitor körs vidare"), (tray.Hotkey is { } key ? key + UiText.T(" eller klick på ikonen") : UiText.T("Klick på ikonen")) + UiText.T(" visar fönstret. Avsluta via ⌄-menyn eller högerklick på ikonen."), NoticeKind.Info));
        }
        return true;
    }

    private void Notify(Notice? notice)
    {
        if (notice is null || preview || !settings.Notifications || tray is null) return;
        tray.Show(notice);
    }

    private void TrackServiceNotices(PortGroup group, ProbeReading reading)
    {
        if (portActions.Contains(group.Key) || group.Launch?.IsBusy == true) return; // Our own stop or restart is not news.
        Notify(notices.Service(group.Key, group.Name, reading.Tone, reading.Text));
    }

    private void TrackLaunchNotices()
    {
        foreach (var entry in QuickStarts)
        {
            if (entry.IsBusy) continue;
            var session = entry.Session;
            Notify(notices.Launch(entry.Id, entry.Name, session?.Record.RunId, session is not null && IsAlive(session), session?.WasStopped == true, session?.ExitCode));
        }
        notices.KeepServices(groupCache.Keys);
    }

    private void TrackQuotaNotices()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var notice in notices.Quota("Codex", codexQuota, now, TimeSpan.FromMinutes(2)).ToList()) Notify(notice);
        foreach (var notice in notices.Quota("Claude", claudeQuota, now, TimeSpan.FromMinutes(5)).ToList()) Notify(notice);
    }

    private void UpdateTray()
    {
        if (tray is null) return;
        var failing = groupCache.Values.Where(group => group.StatusTone == ServiceTone.Bad).Select(group => group.Name)
            .Concat(QuickStarts.Where(entry => entry.HasFailed).Select(entry => entry.Name)).Distinct().ToList();
        var services = groupCache.Values.Count(group => !developmentOnly || group.IsDevelopment);
        var tip = $"Otter Monitor · {services} {(services == 1 ? UiText.T("tjänst") : UiText.T("tjänster"))}"
            + (failing.Count > 0 ? UiText.T(" · fel: ") + string.Join(", ", failing) : "")
            + (tray.Hotkey is { } key ? "\n" + key + UiText.T(" visar och döljer") : "");
        tray.Update(failing.Count > 0, tip);
    }

    // ── Type to filter ────────────────────────────────────────────────────────

    private bool FilterActive => FilterBar.Visibility == Visibility.Visible;

    private void OpenFilter(string? text = null)
    {
        FilterBar.Visibility = Visibility.Visible; CompactDragArea.Visibility = Visibility.Collapsed;
        FilterBox.Focus();
        if (text is not null) { FilterBox.Text += text; }
        FilterBox.CaretIndex = FilterBox.Text.Length;
    }

    private void CloseFilter()
    {
        var hadFocus = FilterBox.IsKeyboardFocusWithin;
        FilterBox.Text = "";
        FilterBar.Visibility = Visibility.Collapsed; CompactDragArea.Visibility = Visibility.Visible;
        if (hadFocus) FocusFirstRow();
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e) => CloseFilter();
    private void Filter_Changed(object sender, TextChangedEventArgs e) { ApplyFilter(); DenseScroll?.ScrollToTop(); }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox || string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        e.Handled = true; OpenFilter(e.Text);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OpenFilter(); FilterBox.SelectAll(); }
        else if (e.Key == Key.Escape && FilterActive && e.OriginalSource is not TextBox) { e.Handled = true; CloseFilter(); }
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; CloseFilter(); break;
            case Key.Down: e.Handled = true; FocusFirstRow(); break;
            case Key.Enter:
                e.Handled = true;
                if (Ports.FirstOrDefault() is { } group) OpenProject(group, ProjectTarget.Auto);
                else if (LaunchRows.FirstOrDefault() is { } entry) FocusRow(LaunchList, entry);
                break;
        }
    }

    private void FocusFirstRow()
    {
        if (Ports.FirstOrDefault() is { } group) FocusRow(DensePortList, group);
        else if (LaunchRows.FirstOrDefault() is { } entry) FocusRow(LaunchList, entry);
    }

    private static void FocusRow(ItemsControl list, object item)
    {
        if (list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container
            && RowVisuals<Border>(container).FirstOrDefault(border => border.Focusable) is { } row) row.Focus();
    }
}
