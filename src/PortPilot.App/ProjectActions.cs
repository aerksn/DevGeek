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

    private void PortRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: PortGroup group } row || e.OriginalSource is not DependencyObject source || IsRowControl(source, row)) return;
        row.Focus();
        if (TryOpenRow(group, row, source, e.ClickCount)) e.Handled = true;
    }

    private bool TryOpenRow(PortGroup group, Border row, DependencyObject source, int clicks)
    {
        if (clicks != 2 || IsRowControl(source, row)) return false;
        OpenProject(group, zed: false); return true;
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
        e.Handled = true; OpenProject(group, zed: false);
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    { if (sender is MenuItem { DataContext: PortGroup group }) OpenProject(group, zed: false); }
    private void OpenZed_Click(object sender, RoutedEventArgs e)
    { if (sender is MenuItem { DataContext: PortGroup group }) OpenProject(group, zed: true); }

    private async void OpenProject(PortGroup group, bool zed)
    {
        if (preview) { previewProjectAction?.Invoke(group, zed); return; }
        if (openingProject || closed) return;
        openingProject = true;
        try
        {
            var directory = await Task.Run(() => ProjectOpener.ResolveDirectory(group.Entries[0]));
            if (closed) return;
            if (directory is null)
            {
                Dialogs.Info(this, "Projektmappen kunde inte hittas", group.IsDocker
                    ? "Den här containern saknar en tillgänglig lokal projektmapp. Docker Compose-projekt kan öppnas när containerns metadata anger en Windows-mapp som finns på datorn."
                    : "Processens projektmapp kunde inte läsas eller finns inte längre. Uppdatera portlistan och försök igen.");
                return;
            }
            if (zed) ProjectOpener.OpenZed(directory); else ProjectOpener.OpenTerminal(directory);
            SetStatus((zed ? "Öppnade Zed för " : "Öppnade terminal för ") + group.Name + ".");
        }
        catch (Exception ex) { Error(ex); }
        finally { openingProject = false; }
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
                ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(calls.Count == 3 && calls.All(call => ReferenceEquals(call.Group, group)) && !calls[1].Zed && calls[2].Zed,
                    (compact ? "Compact" : "Standard") + " context commands use the row's project for Terminal and Zed");
                var nextGroup = (PortGroup)names[1].DataContext;
                var nextRow = RowVisuals<Border>(list).Single(border => ReferenceEquals(border.DataContext, nextGroup) && border.ContextMenu is not null);
                var nextMenu = nextRow.ContextMenu; nextMenu.PlacementTarget = nextRow;
                nextMenu.GetBindingExpression(DataContextProperty)?.UpdateTarget();
                ((MenuItem)nextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(calls.Count == 4 && ReferenceEquals(calls[3].Group, nextGroup), (compact ? "Compact" : "Standard") + " context menu retargets when another process is selected");
            }
        }
        finally { previewProjectAction = null; ConfigureCompactWindow(); UpdateLayout(); }
    }
}
