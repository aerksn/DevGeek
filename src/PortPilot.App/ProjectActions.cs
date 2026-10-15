using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PortPilot.Core;

namespace PortPilot.App;

public partial class MainWindow
{
    private bool openingProject;
    private Action<PortGroup, bool>? previewProjectAction;
    private Action<Uri>? previewBrowserAction;
    private List<string>? previewCopies;

    private void PortRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: PortGroup group } row || e.OriginalSource is not DependencyObject source || IsRowControl(source, row)) return;
        row.Focus();
        if (TryOpenRow(group, row, source, e.ClickCount)) e.Handled = true;
    }

    private bool TryOpenRow(PortGroup group, Border row, DependencyObject source, int clicks)
    {
        if (clicks != 2 || IsRowControl(source, row)) return false;
        OpenProject(group, ProjectTarget.Auto); return true;
    }

    private static bool IsRowControl(DependencyObject? source, DependencyObject row)
    {
        while (source is not null && !ReferenceEquals(source, row))
        {
            if (source is ButtonBase or ScrollBar) return true;
            source = source is ContentElement content ? ContentOperations.GetParent(content) : VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void PortRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || !ReferenceEquals(sender, e.OriginalSource) || sender is not Border { DataContext: PortGroup group }) return;
        e.Handled = true; OpenProject(group, ProjectTarget.Auto);
    }

    private void LaunchRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: QuickStart entry } row || e.OriginalSource is not DependencyObject source || IsRowControl(source, row)) return;
        row.Focus();
        // Never launch on a double-click; it only opens the log of an earlier run.
        if (e.ClickCount == 2 && entry.Session is not null) { e.Handled = true; ShowLaunchLog(entry); }
    }

    private static PortGroup? RowGroup(object sender) => (sender as FrameworkElement)?.DataContext as PortGroup;
    private static QuickStart? RowLaunch(object sender) => (sender as FrameworkElement)?.DataContext as QuickStart;
    private void Browse_Click(object sender, RoutedEventArgs e) { if (RowGroup(sender) is { } group) OpenProject(group, ProjectTarget.Browser); }
    private void OpenTerminal_Click(object sender, RoutedEventArgs e) { if (RowGroup(sender) is { } group) OpenProject(group, ProjectTarget.Terminal); }
    private void OpenEditor_Click(object sender, RoutedEventArgs e) { if (RowGroup(sender) is { } group) OpenProject(group, ProjectTarget.Editor); }
    private void CopyAddress_Click(object sender, RoutedEventArgs e) { if (RowGroup(sender) is { } group) Copy(group.Address, "Kopierade " + group.Address + "."); }
    private void CopyId_Click(object sender, RoutedEventArgs e) { if (RowGroup(sender) is { } group) Copy(group.CopyId, (group.IsDocker ? "Kopierade container-ID för " : "Kopierade PID för ") + group.Name + "."); }
    private void LaunchTerminal_Click(object sender, RoutedEventArgs e) { if (RowLaunch(sender) is { } entry) OpenDirectory(entry.Directory, entry.Name, editor: false); }
    private void LaunchEditor_Click(object sender, RoutedEventArgs e) { if (RowLaunch(sender) is { } entry) OpenDirectory(entry.Directory, entry.Name, editor: true); }

    private void Copy(string text, string message)
    {
        if (preview) { previewCopies?.Add(text); SetStatus(message); return; }
        // Another program can briefly hold the clipboard open.
        try { Clipboard.SetDataObject(text, true); SetStatus(message); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Urklippet var upptaget. Försök igen.", problem: true); }
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag is "editor") is { } item) item.Header = EditorActionLabel;
    }

    private void RowLog_Click(object sender, RoutedEventArgs e)
    {
        if (preview || RowGroup(sender) is not { } group) return;
        if (group.Launch is { } launch) { ShowLaunchLog(launch); return; }
        if (group.IsDocker && group.StopTarget is { } container) ShowLog("docker|" + container.ContainerId, () => LogWindow.ForContainer(container));
    }

    private void RowRestart_Click(object sender, RoutedEventArgs e)
    {
        if (RowGroup(sender) is not { CanRestart: true } group) return;
        if (group.IsDocker) RestartContainer(group);
        else if (group.Launch is { } launch) RestartLaunch(launch);
    }

    private enum ProjectTarget { Auto, Browser, Terminal, Editor }

    private async void OpenProject(PortGroup group, ProjectTarget target)
    {
        var browse = target is ProjectTarget.Auto or ProjectTarget.Browser;
        if (preview)
        {
            if (browse && group.BrowserUrl is { } uri) previewBrowserAction?.Invoke(uri);
            else if (target != ProjectTarget.Browser) previewProjectAction?.Invoke(group, target == ProjectTarget.Editor);
            return;
        }
        if (openingProject || closed) return;
        openingProject = true;
        try
        {
            var entries = group.Entries.ToArray();
            if (browse && entries.Any(WebServiceOpener.IsCandidate))
            {
                // Recheck ownership and HTTP on activation, rather than opening an old cached port.
                var current = await Task.Run(() => new PortService().Scan());
                if (closed) return;
                var owned = entries.Where(entry => current.Any(port => PortGroup.OwnerKey(port) == group.Key
                    && port.Port == entry.Port && port.Address == entry.Address && port.Protocol == entry.Protocol)).ToArray();
                if (owned.Length == 0) { SetStatus("Processen lyssnar inte längre på de här portarna. Uppdatera portlistan.", problem: true); return; }
                var reading = await serviceProbe.MeasureAsync(owned, telemetryStop.Token);
                if (closed || !group.Entries.SequenceEqual(entries)) return;
                group.SetProbe(reading);
                if (WebServiceOpener.SelectUrl(owned, reading, DateTime.UtcNow) is { } uri)
                {
                    WebServiceOpener.Open(uri);
                    SetStatus("Öppnade " + uri + " för " + group.Name + ".");
                    return;
                }
            }
            if (target == ProjectTarget.Browser) { SetStatus(group.Name + " svarar inte med en webbsida just nu.", problem: true); return; }
            var directory = await Task.Run(() => ProjectOpener.ResolveDirectory(group.Entries[0]));
            if (closed) return;
            if (directory is null)
            {
                Dialogs.Info(this, "Projektmappen kunde inte hittas", group.IsDocker
                    ? "Den här containern saknar en tillgänglig lokal projektmapp. Docker Compose-projekt kan öppnas när containerns metadata anger en Windows-mapp som finns på datorn."
                    : "Processens projektmapp kunde inte läsas eller finns inte längre. Uppdatera portlistan och försök igen.");
                return;
            }
            OpenDirectory(directory, group.Name, target == ProjectTarget.Editor);
        }
        catch (OperationCanceledException) when (closed || telemetryStop.IsCancellationRequested) { }
        catch (Exception ex) { Error(ex); }
        finally { openingProject = false; }
    }

    private void OpenDirectory(string directory, string name, bool editor)
    {
        if (preview) return;
        try
        {
            var chosen = CurrentEditor;
            if (editor) ProjectOpener.OpenEditor(directory, chosen); else ProjectOpener.OpenTerminal(directory);
            SetStatus((editor ? "Öppnade " + chosen.Name + " för " : "Öppnade terminal för ") + name + ".");
        }
        catch (Exception ex) { Error(ex); }
    }

    private static IEnumerable<T> RowVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) yield return found;
            foreach (var nested in RowVisuals<T>(child)) yield return nested;
        }
    }

    private void VerifyProjectRows(Action<bool, string> check)
    {
        try
        {
            foreach (var compact in new[] { true })
            {
                ConfigureCompactWindow(); UpdateLayout();
                var list = (FrameworkElement)DensePorts;
                var names = RowVisuals<TextBlock>(list).Where(text => text.Name == "RowName").ToList();
                var portLabels = RowVisuals<TextBlock>(list).Where(text => text.Name == "RowPorts").ToList();
                check(names.Count == Ports.Count && names.All(name =>
                {
                    var ports = portLabels.Single(port => ReferenceEquals(port.DataContext, name.DataContext));
                    var a = name.TranslatePoint(new Point(name.ActualWidth, name.ActualHeight / 2), this);
                    var b = ports.TranslatePoint(new Point(0, ports.ActualHeight / 2), this);
                    return Math.Abs(a.Y - b.Y) <= 1 && a.X <= b.X && ports.ActualWidth > 0;
                }), (compact ? "Compact" : "Standard") + " project names and ports share one line without overlap");
                var first = names[0]; var group = (PortGroup)first.DataContext;
                var row = RowVisuals<Border>(list).Single(border => ReferenceEquals(border.DataContext, group) && border.ContextMenu is not null);
                var button = RowVisuals<ButtonBase>(row).First();
                var calls = new List<(PortGroup Group, bool Zed)>();
                previewProjectAction = (owner, zed) => calls.Add((owner, zed));
                check(!TryOpenRow(group, row, first, 1) && TryOpenRow(group, row, first, 2) && !TryOpenRow(group, row, button, 2)
                    && calls.Count == 1 && ReferenceEquals(calls[0].Group, group) && !calls[0].Zed,
                    (compact ? "Compact" : "Standard") + " double-click opens the selected project once and ignores action buttons");
                var menu = row.ContextMenu; menu.PlacementTarget = row;
                // Resolve the real placement binding, then invoke the same routed menu commands.
                menu.GetBindingExpression(DataContextProperty)?.UpdateTarget();
                ClickMenu(menu, "terminal"); ClickMenu(menu, "editor");
                check(calls.Count == 3 && calls.All(call => ReferenceEquals(call.Group, group)) && !calls[1].Zed && calls[2].Zed,
                    (compact ? "Compact" : "Standard") + " context commands use the row's project for Terminal and the chosen editor");
                check(menu.Items.OfType<MenuItem>().Select(item => item.Tag as string).SequenceEqual(["browse", "copy", "copy-id", "terminal", "editor", "log", "restart", "stop"])
                    && menu.Items.OfType<Separator>().Count() == 2, "The row menu offers browser, copy, project, log, restart and stop commands in groups");
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                check(menu.Items.OfType<MenuItem>().Single(item => item.Tag is "editor").Header as string == EditorActionLabel, "The editor command names the chosen editor");
                var nextGroup = (PortGroup)names[1].DataContext;
                var nextRow = RowVisuals<Border>(list).Single(border => ReferenceEquals(border.DataContext, nextGroup) && border.ContextMenu is not null);
                var nextMenu = nextRow.ContextMenu; nextMenu.PlacementTarget = nextRow;
                nextMenu.GetBindingExpression(DataContextProperty)?.UpdateTarget();
                ClickMenu(nextMenu, "terminal");
                check(calls.Count == 4 && ReferenceEquals(calls[3].Group, nextGroup), (compact ? "Compact" : "Standard") + " context menu retargets when another process is selected");
                var webGroup = Ports.First(owner => owner.Entries[0].Name == "node");
                var webName = names.Single(name => ReferenceEquals(name.DataContext, webGroup));
                var webRow = RowVisuals<Border>(list).Single(border => ReferenceEquals(border.DataContext, webGroup) && border.ContextMenu is not null);
                var webUri = new Uri("http://127.0.0.1:" + webGroup.Entries[0].Port + "/");
                var browserCalls = new List<Uri>(); previewBrowserAction = browserCalls.Add;
                webGroup.SetProbe(new("HTTP 200", ServiceTone.Good, 1, "Preview only", DateTime.UtcNow, [webUri]));
                calls.Clear();
                var webStop = RowVisuals<Button>(webRow).Single(candidate => candidate.Name == "DenseStop");
                check(TryOpenRow(webGroup, webRow, webName, 2) && !TryOpenRow(webGroup, webRow, webStop, 2)
                    && browserCalls.SequenceEqual([webUri]) && calls.Count == 0, "Node double-click opens its verified web URL once and never launches from the stop button");
                var webMenu = webRow.ContextMenu; webMenu.PlacementTarget = webRow; webMenu.GetBindingExpression(DataContextProperty)?.UpdateTarget();
                ClickMenu(webMenu, "terminal"); ClickMenu(webMenu, "editor");
                check(calls.Count == 2 && !calls[0].Zed && calls[1].Zed && browserCalls.Count == 1, "Web-enabled Node keeps explicit Terminal and editor context actions");
                ClickMenu(webMenu, "browse");
                check(browserCalls.Count == 2 && calls.Count == 2, "The explicit browser command opens the verified URL without falling back to a terminal");
                var copies = previewCopies = [];
                ClickMenu(webMenu, "copy"); ClickMenu(webMenu, "copy-id");
                check(copies.SequenceEqual([webUri.ToString(), webGroup.Entries[0].Pid.ToString()]), "Copy commands place the verified address and the PID on the clipboard");

                // Row actions appear for the hovered or focused row, in place of the resource line.
                var actions = RowVisuals<StackPanel>(webRow).Single(panel => panel.Name == "RowActions");
                var resources = RowVisuals<TextBlock>(webRow).Single(text => text.Name == "RowResources");
                check(!actions.IsVisible && resources.IsVisible, "Row actions stay hidden until the row is hovered or focused");
                webRow.Focus(); UpdateLayout();
                var visibleActions = RowVisuals<Button>(actions).Where(action => action.IsVisible).ToList();
                check(actions.IsVisible && !resources.IsVisible && visibleActions.Count == 4 && visibleActions.All(action => action.ActualHeight <= 16 && action.ActualWidth == 22),
                    "A focused web row shows browser, terminal, editor and copy actions within the resource line");
                check(Math.Abs(webRow.ActualHeight - RowVisuals<Border>(list).First(border => border.ContextMenu is not null && !ReferenceEquals(border, webRow)).ActualHeight) <= 0.5,
                    "Showing row actions does not change the row height");
                visibleActions[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); visibleActions[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                visibleActions[3].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(calls.Count == 4 && !calls[2].Zed && calls[3].Zed && copies.Count == 3 && copies[2] == webUri.ToString(), "Row action buttons open terminal and editor and copy the address");
                visibleActions[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(browserCalls.Count == 3, "The globe row action opens the browser");
                Keyboard.ClearFocus(); DensePortList.Focus(); UpdateLayout();
                webGroup.SetProbe(new("TCP", ServiceTone.Good, 1, "No HTTP response", DateTime.UtcNow));
                calls.Clear();
                TryOpenRow(webGroup, webRow, webName, 2);
                check(calls.Count == 1 && ReferenceEquals(calls[0].Group, webGroup) && !calls[0].Zed && browserCalls.Count == 3, "Node without a verified web response retains terminal activation");
                check(webGroup.Address == "http://localhost:" + webGroup.Entries[0].Port, "Copy falls back to a localhost URL for web runtimes before HTTP is verified");
            }
        }
        finally { previewProjectAction = null; previewBrowserAction = null; previewCopies = null; ConfigureCompactWindow(); UpdateLayout(); }
    }

    private static void ClickMenu(ContextMenu menu, string tag) =>
        menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == tag).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private void VerifyLaunchRowsAndFilter(Action<bool, string> check)
    {
        var idle = QuickStarts.Single(entry => entry.Name == "Starta dev miljö");
        var linked = QuickStarts.Single(entry => entry.Name == "demo-app dev");
        var owner = Ports.Single(group => group.Name == "demo-app");
        check(owner.IsLaunch && ReferenceEquals(owner.Launch, linked) && linked.OwnsPorts && !idle.OwnsPorts && LaunchRows.SequenceEqual([idle]),
            "A quick start that owns a port is shown on that port row; one without ports gets its own row");
        var mark = RowVisuals<TextBlock>(DensePortList).Single(text => text.Name == "LaunchMark" && ReferenceEquals(text.DataContext, owner));
        check(mark.IsVisible && RowVisuals<TextBlock>(DensePortList).Where(text => text.Name == "LaunchMark").Count(text => text.IsVisible) == 1 && owner.Hint.StartsWith("Startad av snabbstarten demo-app dev"),
            "Only the quick start's own port row carries the quick start mark");
        var launchRow = RowVisuals<Border>(LaunchList).Single(border => ReferenceEquals(border.DataContext, idle) && border.ContextMenu is not null);
        var body = RowVisuals<StackPanel>(launchRow).Single(panel => panel.Name == "LaunchBody");
        var action = RowVisuals<Button>(launchRow).Single(button => button.Name == "LaunchAction");
        check(body.Opacity < 1 && action.IsEnabled && action.ActualWidth == 36 && RowVisuals<System.Windows.Shapes.Path>(action).Single().IsVisible
            && Math.Abs(launchRow.ActualHeight - RowVisuals<Border>(DensePortList).First(border => border.ContextMenu is not null).ActualHeight) <= 0.5,
            "An idle quick start row is dimmed, matches the port row height and offers a play button");
        var launchMenu = launchRow.ContextMenu; launchMenu.PlacementTarget = launchRow; launchMenu.GetBindingExpression(DataContextProperty)?.UpdateTarget();
        check(launchMenu.Items.OfType<MenuItem>().Select(item => item.Tag as string).SequenceEqual(["toggle", "restart", "log", "edit", "terminal", "editor"]),
            "The quick start menu offers start, restart, log, edit, terminal and editor");
        var toggle = launchMenu.Items.OfType<MenuItem>().First();
        launchMenu.IsOpen = true; launchMenu.UpdateLayout(); // Item bindings resolve when the menu is shown, as for a real right-click.
        var header = toggle.Header; launchMenu.IsOpen = false;
        check(Equals(header, idle.ActionLabel) && !launchMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "restart").IsEnabled,
            "An idle quick start's menu says start and disables restart");

        OpenFilter("5173"); UpdateLayout();
        check(FilterBar.IsVisible && !CompactDragArea.IsVisible && FilterBox.IsKeyboardFocused && Ports.Count == 1 && Ports[0].Name == "demo-web" && LaunchRows.Count == 0,
            "Typing opens the filter, focuses it and narrows the list to the matching port");
        FilterBox.Text = "miljö"; UpdateLayout();
        check(Ports.Count == 0 && LaunchRows.SequenceEqual([idle]) && !DenseEmptyPorts.IsVisible, "The filter also matches quick starts by name");
        FilterBox.Text = "dev"; UpdateLayout();
        check(Ports.Single().Name == "demo-app" && LaunchRows.SequenceEqual([idle]), "A port row matches through the quick start that owns it");
        FilterBox.Text = "ingen-sådan-tjänst"; UpdateLayout();
        check(DenseEmptyPorts.IsVisible && DenseEmptyPorts.Text.Contains("ingen-sådan-tjänst"), "An empty search names what was searched for");
        nativePorts.Add(new(443, "127.0.0.1", 9999, "other-service", "TCP", null));
        FilterBox.Text = "443"; UpdateLayout();
        check(developmentOnly && Ports.Single().Name == "other-service", "A search also finds ports outside the development filter");
        nativePorts.RemoveAll(p => p.Name == "other-service");
        FilterBox_PreviewKeyDown(FilterBox, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(FilterBox)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        UpdateLayout();
        check(!FilterBar.IsVisible && CompactDragArea.IsVisible && FilterBox.Text.Length == 0 && Ports.Count == 5 && LaunchRows.SequenceEqual([idle]),
            "Escape clears the filter and restores the drag area and full list");
        var typed = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, this, "w")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent, Source = this };
        Window_PreviewTextInput(this, typed); UpdateLayout();
        check(typed.Handled && FilterBox.Text == "w" && Ports.All(group => group.Matches("w")), "A printable key typed anywhere in the window starts the filter");
        CloseFilter(); UpdateLayout();
    }
}
