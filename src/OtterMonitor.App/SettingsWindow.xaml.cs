using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OtterMonitor.Core;

namespace OtterMonitor.App;

internal sealed record SettingsActions(Func<DashboardVisibility, bool> Visibility, Func<string?, bool> Editor,
    Func<bool, bool, bool> General, Func<AppearanceSettings, bool> Appearance, Action<SettingsWindow> Customize, Func<string, bool>? Language = null);

public partial class SettingsWindow : Window
{
    private DashboardVisibility visibility;
    private readonly SettingsActions actions;
    private readonly Settings settings;
    private string? editorId;
    private bool notifications, closeToTray;
    private AppearanceSettings appearanceValue;
    private readonly WindowMaterial material;
    private readonly string successHint;
    private bool ready;
    internal AppearanceSettings InheritedAppearance => material.Appearance;
    internal string DescribeMaterial() => material.Describe();
    internal int NativeApplyCount => material.NativeApplyCount;
    internal void VerifyMaterial(Action<bool, string> check) => material.VerifyModernBlur((ok, name) => check(ok, "Settings: " + name));

    internal SettingsWindow(Settings settings, SettingsActions actions, bool preview)
    {
        this.settings = settings; visibility = settings.Visibility; editorId = settings.Editor; notifications = settings.Notifications;
        closeToTray = settings.CloseToTray; appearanceValue = settings.Appearance; this.actions = actions;
        successHint = preview ? UiText.T("Förhandsvisning · dina inställningar ändras inte.") : UiText.T("Dina val sparas direkt.");
        InitializeComponent();
        material = new(this, enabled: !preview, hasTintLayer: true);
        PopulateChoices(preview);
        ApplyAppearance(appearanceValue); SyncToggles(); SyncGeneral(); ready = true;
        MaxHeight = Math.Min(720, Math.Max(300, SystemParameters.WorkArea.Height - 32));
        Height = Math.Min(590, MaxHeight);
        SavedHint.Text = successHint;
    }
    internal void ApplyAppearance(AppearanceSettings value, bool? enabled = null)
    {
        value = value.Normalize(); var modeChanged = material.Appearance.Mode != value.Mode || enabled is { } nextEnabled && nextEnabled != material.Enabled;
        if (enabled is { } active) material.Enabled = active;
        appearanceValue = value; material.Appearance = value;
        var color = (Color)ColorConverter.ConvertFromString(value.Color);
        SettingsSurface.Background = value.Mode == BackgroundMode.Normal ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(value.Mode == BackgroundMode.Opaque ? (byte)255 : value.TintAlpha, color.R, color.G, color.B));
        SettingsSurface.BorderBrush = value.ShowBorder ? (Brush)FindResource("LineBrush") : Brushes.Transparent;
        if (IsLoaded) { if (modeChanged) material.Apply(); else material.ApplyTint(); }
        SyncAppearance();
    }
    private void PopulateChoices(bool preview)
    {
        var automatic = Choice(UiText.T("Automatiskt"), "", "editor"); automatic.Checked += Editor_Changed;
        EditorChoices.Children.Add(automatic);
        foreach (var choice in Editor.All)
        {
            var button = Choice(choice.Name, choice.Id, "editor");
            button.IsEnabled = preview || choice.Find() is not null;
            button.ToolTip = button.IsEnabled ? UiText.T("Öppna projekt i ") + choice.Name : choice.Name + UiText.T(" hittades inte på den här datorn");
            button.Checked += Editor_Changed; EditorChoices.Children.Add(button);
        }
        foreach (var mode in Enum.GetValues<BackgroundMode>())
        {
            var button = Choice(mode.ToString(), mode, "background");
            button.Checked += Background_Changed; BackgroundChoices.Children.Add(button);
        }
    }
    private RadioButton Choice(string text, object tag, string group) => new()
    { Content = text, Tag = tag, GroupName = group, Style = (Style)FindResource("ChoiceButton") };
    private void SyncGeneral()
    {
        var previous = ready; ready = false;
        NotificationsToggle.IsChecked = notifications; TrayToggle.IsChecked = closeToTray;
        EnglishLanguage.IsChecked = settings.Language != "sv"; SwedishLanguage.IsChecked = settings.Language == "sv";
        foreach (var choice in EditorChoices.Children.OfType<RadioButton>()) choice.IsChecked = (string)choice.Tag == (editorId ?? "");
        ready = previous;
    }
    private void SyncAppearance()
    {
        var previous = ready; ready = false;
        BorderToggle.IsChecked = appearanceValue.ShowBorder;
        foreach (var choice in BackgroundChoices.Children.OfType<RadioButton>()) choice.IsChecked = (BackgroundMode)choice.Tag == appearanceValue.Mode;
        MaterialHint.Text = appearanceValue.Mode switch
        {
            BackgroundMode.Normal => UiText.T("Otter Monitors vanliga solida bakgrund."), BackgroundMode.Opaque => UiText.T("En solid yta i din valda färg."),
            BackgroundMode.Clear => UiText.T("Färgad transparens utan suddig bakgrund."), BackgroundMode.Blur => "Suddig glasbakgrund.", _ => UiText.T("Windows Acrylic med din valda färgton.")
        };
        ready = previous;
    }
    internal void SelectSection(string section)
    {
        switch (section) { case "general": GeneralTab.IsChecked = true; break; case "appearance": AppearanceTab.IsChecked = true; break; default: VisibilityTab.IsChecked = true; break; }
    }
    private void Section_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not RadioButton button) return;
        VisibilityPanel.Visibility = button.Tag is "visibility" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = button.Tag is "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = button.Tag is "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ScrollToTop();
    }
    private void SyncToggles()
    {
        var previous = ready; ready = false;
        OpenAiToggle.IsChecked = visibility.OpenAi; JevToggle.IsChecked = visibility.Jev;
        AnthropicToggle.IsChecked = visibility.Anthropic;
        CodexToggle.IsChecked = visibility.Codex; ClaudeToggle.IsChecked = visibility.Claude;
        ready = previous;
    }
    private void Visibility_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not ToggleButton box) return;
        var enabled = box.IsChecked == true;
        var next = box.Tag switch
        {
            "openai" => visibility with { OpenAi = enabled }, "jev" => visibility with { Jev = enabled },
            "anthropic" => visibility with { Anthropic = enabled },
            "codex" => visibility with { Codex = enabled }, "claude" => visibility with { Claude = enabled }, _ => visibility
        };
        if (actions.Visibility(next)) { visibility = next; SavedHint.Text = successHint; }
        else { SyncToggles(); SaveFailed(); }
    }
    private void SaveFailed() => SavedHint.Text = UiText.T("Kunde inte spara. Ditt tidigare val är kvar.");
    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not RadioButton { IsChecked: true, Tag: string language }) return;
        if (actions.Language?.Invoke(language) == true) SavedHint.Text = UiText.T("Språket ändras nästa gång appen öppnas.");
        else { SyncGeneral(); SaveFailed(); }
    }
    private void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not RadioButton { IsChecked: true } choice) return;
        var next = string.IsNullOrEmpty((string)choice.Tag) ? null : (string)choice.Tag;
        if (actions.Editor(next)) { editorId = next; SavedHint.Text = successHint; }
        else { SyncGeneral(); SaveFailed(); }
    }
    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        if (sender is ToggleButton { Tag: "border" }) { TryAppearance(appearanceValue with { ShowBorder = BorderToggle.IsChecked == true }); return; }
        var notify = NotificationsToggle.IsChecked == true; var tray = TrayToggle.IsChecked == true;
        if (actions.General(notify, tray)) { notifications = notify; closeToTray = tray; SavedHint.Text = successHint; }
        else { SyncGeneral(); SaveFailed(); }
    }
    private void Background_Changed(object sender, RoutedEventArgs e)
    {
        if (ready && sender is RadioButton { IsChecked: true, Tag: BackgroundMode mode }) TryAppearance(appearanceValue with { Mode = mode });
    }
    private void TryAppearance(AppearanceSettings value)
    {
        if (actions.Appearance(value)) { ApplyAppearance(value); SavedHint.Text = successHint; }
        else { SyncAppearance(); SaveFailed(); }
    }
    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => TryAppearance(new());
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Appearance_Click(object sender, RoutedEventArgs e) => actions.Customize(this);

    internal void VerifyInputs(Action<bool, string> check)
    {
        SelectSection("visibility");
        UpdateLayout();
        check(WindowStyle == WindowStyle.None && material.Appearance.Color == "#372B53" && SettingsSurface.CornerRadius.TopLeft == 8,
            "Settings uses the main window's borderless material, tint and rounded frame");
        foreach (var toggle in new[] { OpenAiToggle, JevToggle, AnthropicToggle, CodexToggle, ClaudeToggle })
        {
            toggle.IsChecked = false; UpdateLayout();
            check(((System.Windows.Shapes.Path)toggle.Template.FindName("Slash", toggle)).Visibility == Visibility.Visible,
                toggle.Tag + " uses the crossed eye when hidden");
            check(toggle.Template.FindName("Box", toggle) is null && toggle.ActualWidth >= 32 && settings.Visibility == visibility,
                "The eye is the only visibility toggle and applies the choice to the main window");
            toggle.IsChecked = true; UpdateLayout();
            check(((System.Windows.Shapes.Path)toggle.Template.FindName("Slash", toggle)).Visibility == Visibility.Collapsed,
                toggle.Tag + " uses the open eye when visible");
        }
        var originalEditor = editorId; var originalNotifications = notifications; var originalTray = closeToTray; var originalAppearance = appearanceValue; var originalLanguage = settings.Language;
        try
        {
            SelectSection("general"); UpdateLayout();
            check(GeneralPanel.IsVisible && !VisibilityPanel.IsVisible && !AppearancePanel.IsVisible, "General settings have their own section");
            EnglishLanguage.IsChecked = true;
            check(settings.Language == "en", "English is selectable and saved without changing account connections");
            SwedishLanguage.IsChecked = true;
            check(settings.Language == "sv", "Swedish is selectable and saved for the next app start");
            foreach (var choice in EditorChoices.Children.OfType<RadioButton>().Where(button => button.IsEnabled))
            {
                choice.IsChecked = true;
                check((settings.Editor ?? "") == (string)choice.Tag && EditorChoices.Children.OfType<RadioButton>().Count(button => button.IsChecked == true) == 1,
                    "Editor selection applies from Settings with exactly one selected choice");
            }
            NotificationsToggle.IsChecked = !originalNotifications; TrayToggle.IsChecked = !originalTray;
            check(settings.Notifications == !originalNotifications && settings.CloseToTray == !originalTray, "Notification and tray switches update the saved main-window options");
            SelectSection("appearance"); UpdateLayout();
            check(AppearancePanel.IsVisible && !GeneralPanel.IsVisible && BackgroundChoices.Children.Count == 5, "All five background modes are available in Settings");
            foreach (var choice in BackgroundChoices.Children.OfType<RadioButton>())
            {
                choice.IsChecked = true;
                check(settings.Appearance.Mode == (BackgroundMode)choice.Tag && material.Appearance.Mode == (BackgroundMode)choice.Tag
                    && BackgroundChoices.Children.OfType<RadioButton>().Count(button => button.IsChecked == true) == 1, "Background selection updates both windows and keeps one selected material");
            }
            BorderToggle.IsChecked = !originalAppearance.ShowBorder;
            check(settings.Appearance.ShowBorder == !originalAppearance.ShowBorder, "The window-frame switch moved into Settings");
            TryAppearance(appearanceValue with { Color = "#1468AB", Transparency = 91 });
            ResetAppearance.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(settings.Appearance == new AppearanceSettings() && material.Appearance == settings.Appearance, "Reset appearance restores the defaults in both windows");
            check(SettingsScroll.ExtentHeight <= SettingsScroll.ViewportHeight || SettingsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Long settings sections remain scrollable");
        }
        finally
        {
            EditorChoices.Children.OfType<RadioButton>().Single(button => (string)button.Tag == (originalEditor ?? "")).IsChecked = true;
            NotificationsToggle.IsChecked = originalNotifications; TrayToggle.IsChecked = originalTray;
            (originalLanguage == "sv" ? SwedishLanguage : EnglishLanguage).IsChecked = true;
            TryAppearance(originalAppearance); SelectSection("visibility");
        }
        var rejected = new SettingsWindow(new Settings(), new(_ => false, _ => false, (_, _) => false, _ => false, _ => { }), true);
        try
        {
            rejected.OpenAiToggle.IsChecked = false; rejected.NotificationsToggle.IsChecked = false; rejected.TrayToggle.IsChecked = false;
            rejected.EditorChoices.Children.OfType<RadioButton>().ElementAt(1).IsChecked = true;
            rejected.BackgroundChoices.Children.OfType<RadioButton>().First().IsChecked = true;
            rejected.BorderToggle.IsChecked = false;
            rejected.SwedishLanguage.IsChecked = true;
            check(rejected.OpenAiToggle.IsChecked == true && rejected.NotificationsToggle.IsChecked == true && rejected.TrayToggle.IsChecked == true
                && rejected.EditorChoices.Children.OfType<RadioButton>().Single(button => button.IsChecked == true).Tag is ""
                && rejected.BackgroundChoices.Children.OfType<RadioButton>().Single(button => button.IsChecked == true).Tag is BackgroundMode.Acrylic
                && rejected.BorderToggle.IsChecked == true && rejected.EnglishLanguage.IsChecked == true, "Failed saves restore the previous eye, editor, language, general and appearance controls");
        }
        finally { rejected.Close(); }
    }
}
