using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OtterMonitor.Core;

namespace OtterMonitor.App;

public partial class MainWindow
{
    private ThemedMenu appearanceMenu = null!;
    private void InitializeAppearance()
    {
        appearanceMenu = new ThemedMenu(this, preview);
        var refresh = new MenuItem { Header = UiText.T("Uppdatera portar"), Tag = "refresh" };
        var settingsItem = new MenuItem { Header = UiText.T("Inställningar…"), Tag = "settings" };
        settingsItem.Click += (_, _) => { appearanceMenu.IsOpen = false; Dispatcher.BeginInvoke(() => CreateSettingsWindow().ShowDialog()); };
        refresh.Click += Refresh_Click; appearanceMenu.Items.Add(refresh);
        var development = new MenuItem { Header = UiText.T("Bara utveckling"), Tag = "development", IsCheckable = true };
        development.Click += DevOnly_Click; appearanceMenu.Items.Add(development);
        var pin = new MenuItem { Header = UiText.T("Fäst överst"), Tag = "pin", IsCheckable = true };
        pin.Click += Pin_Click; appearanceMenu.Items.Add(pin);
        var addLaunch = new MenuItem { Header = UiText.T("Ny snabbstart…"), Tag = "add-launch" };
        addLaunch.Click += Add_Click; appearanceMenu.Items.Add(addLaunch);
        var quickstarts = new MenuItem { Header = UiText.T("Hantera snabbstarter…"), Tag = "quickstarts" };
        quickstarts.Click += QuickStarts_Click; appearanceMenu.Items.Add(quickstarts);
        var filter = new MenuItem { Header = UiText.T("Filtrera…  (Ctrl+F)"), Tag = "filter" };
        filter.Click += (_, _) => { appearanceMenu.IsOpen = false; Dispatcher.BeginInvoke(() => OpenFilter()); }; appearanceMenu.Items.Add(filter);
        appearanceMenu.Items.Add(new Separator());
        appearanceMenu.Items.Add(settingsItem);
        appearanceMenu.Items.Add(new Separator());
        var exit = new MenuItem { Header = UiText.T("Avsluta Otter Monitor"), Tag = "exit" };
        exit.Click += (_, _) => { appearanceMenu.IsOpen = false; Dispatcher.BeginInvoke(ExitApplication); }; appearanceMenu.Items.Add(exit);
        foreach (var item in appearanceMenu.Items.OfType<MenuItem>()) item.Style = (Style)FindResource("AppearanceMenuItem");
        foreach (var separator in appearanceMenu.Items.OfType<Separator>()) separator.Style = (Style)FindResource("MenuSeparator");
        appearanceMenu.Opened += (_, _) => UpdateAppearanceMenu();
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, args) =>
        {
            // Keep process and quick-start row context menus independent.
            var target = args.OriginalSource as DependencyObject;
            while (target is not null && !ReferenceEquals(target, this))
            {
                if (target is FrameworkElement element && element.ContextMenu is { } own && !ReferenceEquals(own, ContextMenu)) return;
                target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
            }
            args.Handled = true;
            appearanceMenu.PlacementTarget = null; appearanceMenu.Placement = PlacementMode.MousePoint;
            UpdateAppearanceMenu(); appearanceMenu.IsOpen = true;
        };
        ApplyAppearance(settings.Appearance);
    }

    private void UpdateAppearanceMenu()
    {
        foreach (var item in appearanceMenu.Items.OfType<MenuItem>())
            if (item.Tag is "development") item.IsChecked = developmentOnly;
            else if (item.Tag is "pin") item.IsChecked = Topmost;
    }

    private bool SetEditor(string? id)
    {
        var previous = settings.Editor;
        settings.Editor = id;
        if (!Save()) { settings.Editor = previous; return false; }
        editor = null; Changed(nameof(CurrentEditor)); Changed(nameof(EditorActionLabel)); return true;
    }

    private bool SetGeneralOptions(bool notifications, bool closeToTray)
    {
        var previous = (settings.Notifications, settings.CloseToTray);
        settings.Notifications = notifications; settings.CloseToTray = closeToTray;
        if (!Save()) { (settings.Notifications, settings.CloseToTray) = previous; return false; }
        UpdateTray(); return true;
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
        DropdownButton.ToolTip = UiText.F("Verktyg och inst\u00E4llningar\nBakgrund \u00B7 {0}", appearance.Mode);
        if (modeChanged) material.Apply(); else material.ApplyTint();
        appearanceMenu.ApplyAppearance(appearance, material.Enabled);
        UpdateOwnedAppearance(this, appearance, material.Enabled);
    }
    private static void UpdateOwnedAppearance(Window owner, AppearanceSettings appearance, bool enabled)
    {
        foreach (var child in owner.OwnedWindows.Cast<Window>().ToArray())
        {
            if (child is SettingsWindow settingsWindow) settingsWindow.ApplyAppearance(appearance, enabled);
            else if (child is AppearanceDialog appearanceDialog) appearanceDialog.ApplyInheritedAppearance(appearance, enabled);
            UpdateOwnedAppearance(child, appearance, enabled);
        }
    }
    public AppearanceDialog CreateAppearanceDialog()
    {
        var dialog = new AppearanceDialog(settings.Appearance, ApplyAppearance, SetAppearance, preview) { Owner = this };
        dialog.ApplyInheritedAppearance(settings.Appearance, material.Enabled);
        return dialog;
    }
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        appearanceMenu.PlacementTarget = sender as FrameworkElement ?? DropdownButton; appearanceMenu.Placement = PlacementMode.Bottom;
        appearanceMenu.AlignToLeft = ReferenceEquals(appearanceMenu.PlacementTarget, OtterMenuButton);
        UpdateAppearanceMenu(); appearanceMenu.IsOpen = true;
    }
    public Window OpenMenuPreview()
    {
        Appearance_Click(OtterMenuButton, new()); return appearanceMenu;
    }
    public void LoadThemePreview()
    {
        if (preview) SetAppearance(settings.Appearance with { Color = "#284739", Transparency = 35 });
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
                check(appearanceMenu.IsOpen && DropdownButton.IsVisible && appearanceMenu.Items.OfType<MenuItem>().Select(item => item.Tag?.ToString()).SequenceEqual(new[] { "refresh", "development", "pin", "add-launch", "quickstarts", "filter", "settings", "exit" }),
                    "The compact dropdown contains only quick actions, Settings and Exit, in that order");
                appearanceMenu.IsOpen = false;
                OtterMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(appearanceMenu.IsOpen && ReferenceEquals(appearanceMenu.PlacementTarget, OtterMenuButton)
                    && appearanceMenu.AlignToLeft && OtterMenuLogo.Source is not null && OtterMenuButton.IsVisible, "A single Otter-logo click opens the same menu as the dropdown arrow, aligned to the left");
                appearanceMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "refresh").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(!appearanceMenu.IsOpen, "Selecting a menu action dismisses the themed menu");
                appearanceMenu.IsOpen = false;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                foreach (var mode in Enum.GetValues<BackgroundMode>())
                {
                    check(SetAppearance(settings.Appearance with { Mode = mode }) && settings.Appearance.Mode == mode && Opacity == 1,
                        "Selecting " + mode + " preserves opaque controls and saves the chosen material");
                }
            }
            await VerifyLiveAppearanceAsync(check, original);
            SetAppearance(settings.Appearance with { ShowBorder = false });
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
    private async Task VerifyLiveAppearanceAsync(Action<bool, string> check, AppearanceSettings original)
    {
        var window = CreateSettingsWindow();
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -16000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        try
        {
            window.Show();
            Appearance_Click(DropdownButton, new());
            window.VerifyMaterial(check); appearanceMenu.VerifyMaterial(check);
            foreach (var mode in Enum.GetValues<BackgroundMode>())
            {
                var value = original with { Mode = mode, Color = "#284739", Transparency = 42, ShowBorder = false };
                SetAppearance(value);
                check(window.InheritedAppearance == value && appearanceMenu.Appearance == value && appearanceMenu.IsOpen,
                    "Open Settings and menu inherit " + mode + ", color, transparency and border changes immediately");
            }
            SetAppearance(original); appearanceMenu.IsOpen = false;
            AppearanceDialog? colors = null;
            var observed = new List<AppearanceSettings>();
            colors = new AppearanceDialog(original, value =>
            {
                observed.Add(value); ApplyAppearance(value);
                check(window.InheritedAppearance == value && appearanceMenu.Appearance == value,
                    "Live color preview updates Settings and the menu before saving");
                if (colors is { IsVisible: true }) check(colors.InheritedAppearance == value, "The color dialog inherits the same live background");
            }, SetAppearance, true) { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000, ShowActivated = false, ShowInTaskbar = false };
            colors.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var updates = (window.NativeApplyCount, appearanceMenu.NativeApplyCount);
            colors.VerifyInputs(check, observed, cancel: true);
            check(window.InheritedAppearance == original && appearanceMenu.Appearance == original && material.Appearance == original,
                "Cancelling color preview restores the background of every surface");
            check((window.NativeApplyCount, appearanceMenu.NativeApplyCount) == updates, "RGB and transparency preview keep Settings/menu native glass intact");
        }
        finally { appearanceMenu.IsOpen = false; window.Close(); SetAppearance(original); }
    }
}

public sealed class AppearanceDialog : Window
{
    private readonly WindowMaterial material;
    internal string DescribeMaterial() => material.Describe();
    internal AppearanceSettings InheritedAppearance => material.Appearance;
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
    private readonly Button saveButton = new() { Content = UiText.T("Spara"), IsDefault = true };

    public AppearanceDialog(AppearanceSettings appearance, Action<AppearanceSettings> apply, Func<AppearanceSettings, bool> save, bool preview)
    {
        original = draft = appearance.Normalize(); this.apply = apply; this.save = save;
        Style = (Style)FindResource(typeof(Window)); Title = UiText.T("Bakgrund · färg och transparens");
        material = new WindowMaterial(this, enabled: !preview) { Appearance = original };
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = UiText.T("Färg och transparens"), FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = UiText.F("{0} \u00B7 \u00E4ndringar f\u00F6rhandsvisas direkt", draft.Mode), FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 7, 0, 20) });
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
        var eyedropper = new Button { Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 10,6 L 3,13 L 2,17 L 6,16 L 13,9 M 8,4 L 15,11 M 11,5 L 14,2 C 16,0 20,4 18,6 L 15,9"),
                Stroke = (Brush)FindResource("TextBrush"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 17, Height = 17, Stretch = Stretch.Uniform }, Width = 34, Height = 34,
            Padding = new Thickness(6), Margin = new Thickness(8, 0, 0, 0), ToolTip = UiText.T("Välj färg från skärmen") };
        System.Windows.Automation.AutomationProperties.SetName(eyedropper, UiText.T("Välj färg från skärmen"));
        eyedropper.Click += async (_, _) =>
        {
            eyedropper.IsEnabled = false;
            try { if (await ScreenColorPicker.PickAsync(this) is { } picked) hex.Text = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}"; }
            catch (Exception) { error.Text = UiText.T("Skärmen kunde inte läsas. Ange färgen med HEX eller RGB."); }
            finally { eyedropper.IsEnabled = true; }
        };
        row.Children.Add(eyedropper);
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
        panel.Children.Add(new TextBlock { Text = UiText.T("Färgtonens transparens"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 0) });
        transparency.IsEnabled = draft.Mode is BackgroundMode.Clear or BackgroundMode.Blur or BackgroundMode.Acrylic;
        transparency.Style = (Style)FindResource("AppearanceSlider");
        panel.Children.Add(transparency); panel.Children.Add(percent);
        percent.Foreground = (Brush)FindResource("MutedBrush"); percent.FontSize = 11;
        panel.Children.Add(new TextBlock { Text = draft.Mode == BackgroundMode.Normal ? UiText.T("Normal använder Otter Monitors standardbakgrund. Färgen sparas för de andra lägena.")
            : draft.Mode == BackgroundMode.Opaque ? UiText.T("Opaque ger en helt solid yta i färgen ovan.")
            : UiText.T("0 % = solid färgton · 100 % = ingen färgton.\nBakgrundseffekten följer det valda läget."), FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 12, 0, 0) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        actions.Children.Add(new Button { Content = UiText.T("Avbryt"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        saveButton.Style = (Style)FindResource("PrimaryButton");
        saveButton.Click += (_, _) => { if (!AppearanceSettings.IsColor(hex.Text)) { error.Text = UiText.T("Ange sex hextecken efter #."); return; } if (save(draft)) { saved = true; DialogResult = true; } };
        actions.Children.Add(saveButton); panel.Children.Add(actions); Content = panel;
        hex.TextChanged += (_, _) =>
        {
            if (!AppearanceSettings.IsColor(hex.Text)) { error.Text = UiText.T("Ange en färg som #372B53."); return; }
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

    internal void ApplyInheritedAppearance(AppearanceSettings value, bool enabled)
    {
        var backendChanged = material.Appearance.Mode != value.Mode || material.Enabled != enabled;
        material.Appearance = value; material.Enabled = enabled;
        if (backendChanged) material.Apply(); else material.ApplyTint();
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
