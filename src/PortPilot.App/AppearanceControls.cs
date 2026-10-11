using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PortPilot.Core;

namespace PortPilot.App;

public partial class MainWindow
{
    private ContextMenu appearanceMenu = null!;
    private void InitializeAppearance()
    {
        appearanceMenu = new ContextMenu { Template = (ControlTemplate)FindResource("AppearanceMenuTemplate"), FontSize = 11,
            Foreground = (Brush)FindResource("TextBrush") };
        var refresh = new MenuItem { Header = "Uppdatera portar", Tag = "refresh" };
        refresh.Click += Refresh_Click; appearanceMenu.Items.Add(refresh);
        var development = new MenuItem { Header = "Bara utveckling", Tag = "development", IsCheckable = true };
        development.Click += DevOnly_Click; appearanceMenu.Items.Add(development);
        var pin = new MenuItem { Header = "Fäst överst", Tag = "pin", IsCheckable = true };
        pin.Click += Pin_Click; appearanceMenu.Items.Add(pin);
        var quickstarts = new MenuItem { Header = "Snabbstarter…", Tag = "quickstarts" };
        quickstarts.Click += QuickStarts_Click; appearanceMenu.Items.Add(quickstarts);
        appearanceMenu.Items.Add(new Separator());
        appearanceMenu.Items.Add(new MenuItem { Header = "BAKGRUND", IsEnabled = false, FontSize = 9 });
        var border = new MenuItem { Header = "Visa fönsterram", IsCheckable = true, Tag = "border" };
        border.Click += (_, _) => SetAppearance(settings.Appearance with { ShowBorder = border.IsChecked });
        appearanceMenu.Items.Add(border);
        var colors = new MenuItem { Header = "Färg och transparens…" };
        colors.Click += (_, _) =>
        {
            appearanceMenu.IsOpen = false;
            Dispatcher.BeginInvoke(() => CreateAppearanceDialog().ShowDialog());
        };
        appearanceMenu.Items.Add(colors); appearanceMenu.Items.Add(new Separator());
        foreach (var mode in Enum.GetValues<BackgroundMode>())
        {
            var item = new MenuItem { Header = mode.ToString(), IsCheckable = true, Tag = mode,
                ToolTip = mode switch { BackgroundMode.Normal => "DevGeeks vanliga solida bakgrund", BackgroundMode.Opaque => "En solid yta i din valda färg",
                    BackgroundMode.Clear => "Färgad transparens utan suddig bakgrund", BackgroundMode.Blur => "Suddig glasbakgrund", _ => "Windows Acrylic med färgton" } };
            item.Click += (_, _) => SetAppearance(settings.Appearance with { Mode = mode });
            appearanceMenu.Items.Add(item);
        }
        appearanceMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Återställ utseende" };
        reset.Click += (_, _) => SetAppearance(new()); appearanceMenu.Items.Add(reset);
        foreach (var item in appearanceMenu.Items.OfType<MenuItem>()) item.Style = (Style)FindResource("AppearanceMenuItem");
        appearanceMenu.Opened += (_, _) => UpdateAppearanceMenu();
        ContextMenu = appearanceMenu;
        ApplyAppearance(settings.Appearance);
    }

    private void UpdateAppearanceMenu()
    {
        foreach (var item in appearanceMenu.Items.OfType<MenuItem>())
            if (item.Tag is BackgroundMode mode) item.IsChecked = settings.Appearance.Mode == mode;
            else if (item.Tag is "border") item.IsChecked = settings.Appearance.ShowBorder;
            else if (item.Tag is "development") item.IsChecked = developmentOnly;
            else if (item.Tag is "pin") item.IsChecked = Topmost;
    }
    private bool SetAppearance(AppearanceSettings appearance)
    {
        var previous = settings.Appearance;
        settings.Appearance = appearance.Normalize();
        if (!Save()) { settings.Appearance = previous; ApplyAppearance(previous); UpdateAppearanceMenu(); return false; }
        ApplyAppearance(settings.Appearance); UpdateAppearanceMenu(); return true;
    }
    public void ApplyAppearance(AppearanceSettings appearance)
    {
        appearance = appearance.Normalize();
        var modeChanged = material.Appearance.Mode != appearance.Mode;
        material.Appearance = appearance;
        var color = (Color)ColorConverter.ConvertFromString(appearance.Color);
        MaterialSurface.Background = appearance.Mode == BackgroundMode.Normal ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(appearance.Mode == BackgroundMode.Opaque ? (byte)255 : appearance.TintAlpha, color.R, color.G, color.B));
        MaterialSurface.BorderBrush = appearance.ShowBorder ? (Brush)FindResource("LineBrush") : Brushes.Transparent;
        DropdownButton.ToolTip = $"Verktyg och inställningar\nBakgrund · {appearance.Mode}";
        if (modeChanged) material.Apply(); else material.ApplyTint();
    }
    public AppearanceDialog CreateAppearanceDialog() => new(settings.Appearance, ApplyAppearance, SetAppearance, preview) { Owner = this };
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        appearanceMenu.PlacementTarget = DropdownButton; appearanceMenu.Placement = PlacementMode.Bottom;
        UpdateAppearanceMenu(); appearanceMenu.IsOpen = true;
    }

    private async Task VerifyAppearanceAsync(Action<bool, string> check)
    {
        var original = settings.Appearance;
        try
        {
            foreach (var compact in new[] { true })
            {
                ConfigureCompactWindow(); UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Appearance_Click(DropdownButton, new()); appearanceMenu.UpdateLayout();
                check(appearanceMenu.IsOpen && DropdownButton.IsVisible && appearanceMenu.Items.OfType<MenuItem>().Count(item => item.Tag is BackgroundMode) == 5,
                    (compact ? "Compact" : "Standard") + " dropdown opens with all tools and five background materials");
                appearanceMenu.IsOpen = false;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                foreach (var item in appearanceMenu.Items.OfType<MenuItem>().Where(item => item.Tag is BackgroundMode))
                {
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    check(settings.Appearance.Mode == (BackgroundMode)item.Tag && appearanceMenu.Items.OfType<MenuItem>().Count(menu => menu.Tag is BackgroundMode && menu.IsChecked) == 1 && Opacity == 1,
                        (compact ? "Compact" : "Standard") + " selecting " + item.Tag + " preserves opaque controls and one checked material");
                }
            }
            var border = appearanceMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "border");
            border.IsChecked = false; border.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(settings.Appearance.ShowBorder == false && MaterialSurface.BorderBrush == Brushes.Transparent, "Border toggle hides the window rim without removing its hit-test area");
            var observed = new List<AppearanceSettings>(); AppearanceSettings? committed = null;
            var dialog = new AppearanceDialog(original, value => { observed.Add(value); ApplyAppearance(value); }, value => { committed = value; return true; }, true) { Owner = this };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                // A loaded dialog instantiates the real slider templates, catching missing resources.
                var nativeUpdates = material.NativeApplyCount;
                dialog.VerifyInputs(check, observed, cancel: false);
                check(material.NativeApplyCount == nativeUpdates, "RGB and transparency changes update only tint without resetting the native compositor");
            });
            dialog.ShowDialog();
            check(committed is { Color: "#6468AB", Transparency: 88 } && observed.Last() == committed, "Color save commits the actual RGB/opacity draft without rolling it back");
            committed = null; observed.Clear();
            var cancelled = new AppearanceDialog(original, observed.Add, value => { committed = value; return true; }, true) { Owner = this };
            cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(() => cancelled.VerifyInputs(check, observed, cancel: true));
            cancelled.ShowDialog();
            check(committed is null && observed.Last() == original, "Cancelling the live color preview restores the previous appearance");
        }
        finally { appearanceMenu.IsOpen = false; SetAppearance(original); ConfigureCompactWindow(); UpdateLayout(); }
    }
}

public sealed class AppearanceDialog : Window
{
    private readonly AppearanceSettings original;
    private readonly Action<AppearanceSettings> apply;
    private readonly Func<AppearanceSettings, bool> save;
    private readonly TextBox hex = new() { Width = 125, MaxLength = 7, FontFamily = new FontFamily("Consolas") };
    private readonly Slider transparency = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 4) };
    private readonly Slider red = new() { Minimum = 0, Maximum = 255, IsSnapToTickEnabled = true }, green = new() { Minimum = 0, Maximum = 255, IsSnapToTickEnabled = true }, blue = new() { Minimum = 0, Maximum = 255, IsSnapToTickEnabled = true };
    private readonly TextBlock percent = new(), error = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(243, 194, 215)), MinHeight = 16 };
    private readonly Border swatch = new() { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
    private bool updating, saved, ready;
    private AppearanceSettings draft;
    private readonly Button saveButton = new() { Content = "Spara", IsDefault = true };

    public AppearanceDialog(AppearanceSettings appearance, Action<AppearanceSettings> apply, Func<AppearanceSettings, bool> save, bool preview)
    {
        original = draft = appearance.Normalize(); this.apply = apply; this.save = save;
        Style = (Style)FindResource(typeof(Window)); Title = "Bakgrund · färg och transparens";
        _ = new WindowMaterial(this, enabled: !preview);
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Färg och transparens", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"{draft.Mode} · ändringar förhandsvisas direkt", FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 7, 0, 20) });
        var palette = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
        foreach (var value in new[] { "#372B53", "#23242D", "#142B38", "#284739", "#694755", "#716193", "#FFFFFF", "#000000" })
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var button = new Button { Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0), ToolTip = value,
                Content = new Border { Width = 21, Height = 21, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(color) } };
            button.Click += (_, _) => hex.Text = value; palette.Children.Add(button);
        }
        panel.Children.Add(palette);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch); row.Children.Add(hex);
        row.Children.Add(new TextBlock { Text = "#RRGGBB", FontSize = 10, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(row); panel.Children.Add(error);
        foreach (var (label, slider) in new[] { ("R", red), ("G", green), ("B", blue) })
        {
            slider.Style = (Style)FindResource("AppearanceSlider");
            var channel = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            var text = new TextBlock { Text = label, Width = 24, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush") };
            DockPanel.SetDock(text, Dock.Left); channel.Children.Add(text); channel.Children.Add(slider); panel.Children.Add(channel);
            slider.ValueChanged += (_, _) => { if (updating) return; hex.Text = $"#{(int)red.Value:X2}{(int)green.Value:X2}{(int)blue.Value:X2}"; };
        }
        panel.Children.Add(new TextBlock { Text = "Färgtonens transparens", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 0) });
        transparency.IsEnabled = draft.Mode is BackgroundMode.Clear or BackgroundMode.Blur or BackgroundMode.Acrylic;
        transparency.Style = (Style)FindResource("AppearanceSlider");
        panel.Children.Add(transparency); panel.Children.Add(percent);
        percent.Foreground = (Brush)FindResource("MutedBrush"); percent.FontSize = 11;
        panel.Children.Add(new TextBlock { Text = draft.Mode == BackgroundMode.Normal ? "Normal använder DevGeeks standardbakgrund. Färgen sparas för de andra lägena."
            : draft.Mode == BackgroundMode.Opaque ? "Opaque ger en helt solid yta i färgen ovan."
            : "0 % = solid färgton · 100 % = ingen färgton.\nBakgrundseffekten följer det valda läget.", FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 12, 0, 0) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        actions.Children.Add(new Button { Content = "Avbryt", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        saveButton.Style = (Style)FindResource("PrimaryButton");
        saveButton.Click += (_, _) => { if (!AppearanceSettings.IsColor(hex.Text)) { error.Text = "Ange sex hextecken efter #."; return; } if (save(draft)) { saved = true; DialogResult = true; } };
        actions.Children.Add(saveButton); panel.Children.Add(actions); Content = panel;
        hex.TextChanged += (_, _) =>
        {
            if (!AppearanceSettings.IsColor(hex.Text)) { error.Text = "Ange en färg som #372B53."; return; }
            error.Text = "";
            draft = draft with { Color = hex.Text.ToUpperInvariant() };
            var color = (Color)ColorConverter.ConvertFromString(draft.Color);
            swatch.Background = new SolidColorBrush(color);
            updating = true; red.Value = color.R; green.Value = color.G; blue.Value = color.B; updating = false;
            if (ready) apply(draft);
        };
        transparency.ValueChanged += (_, _) => { draft = draft with { Transparency = (int)transparency.Value }; percent.Text = $"{draft.Transparency} %"; if (ready) apply(draft); };
        hex.Text = draft.Color; transparency.Value = draft.Transparency; percent.Text = $"{draft.Transparency} %";
        ready = true;
        Closed += (_, _) => { if (!saved) apply(original); };
    }

    internal void VerifyInputs(Action<bool, string> check, List<AppearanceSettings> observed, bool cancel)
    {
        hex.Text = "#1468AB"; transparency.Value = 88;
        check(observed.Last() is { Color: "#1468AB", Transparency: 88 }, "Color and transparency widgets preview the combined draft immediately");
        var count = observed.Count; hex.Text = "bad";
        check(observed.Count == count && error.Text.Length > 0, "Invalid color input leaves the last valid preview intact");
        hex.Text = "#1468AB"; red.Value = 100;
        check(hex.Text == "#6468AB" && draft.Color == "#6468AB", "RGB sliders update the actual hex field and live preview");
        if (cancel) Close(); else saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
