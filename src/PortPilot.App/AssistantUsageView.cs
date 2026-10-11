using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PortPilot.Core;

namespace PortPilot.App;

public sealed record AssistantUsageRow(string Name, string Value, string Hint, string Tone)
{
    public string CompactValue { get; init; } = Value;
}

public partial class MainWindow
{
    public ObservableCollection<AssistantUsageRow> AssistantRows { get; } = [];
    public AssistantUsageRow? CodexUsageRow => AssistantRows.FirstOrDefault(row => row.Name == "Codex");
    public AssistantUsageRow? ClaudeUsageRow => AssistantRows.FirstOrDefault(row => row.Name == "Claude Code");
    private readonly CodexUsageClient codexUsage = new();
    private UsageSnapshot? codexQuota, claudeQuota;
    private UsageSnapshot? claudeDesktopQuota;
    private ClaudeUsageBrowser? claudeBrowser;
    private string? claudeDesktopError;
    private string claudeDesktopState = "pending";
    private DateTimeOffset nextClaudeStart;
    private bool usageScanning, usageReading, codexWasRunning, claudeConnected;
    private DateTimeOffset nextCodexRead;
    private string? codexUsageError;
    private AssistantActivity assistantActivity = new(false, false, null);
    private ClaudeUsageInstaller ClaudeBridge => new(store.DirectoryPath, ClaudeUsageInstaller.DefaultSettingsFile);

    private async Task RefreshAssistantUsageAsync()
    {
        if (preview || closed || usageScanning) return;
        usageScanning = true;
        try
        {
            var activity = await Task.Run(() => AssistantProcesses.Scan(codexUsage.HelperPid));
            if (closed) return;
            assistantActivity = activity;
            if (activity.ClaudeRunning)
            {
                if (activity.ClaudeDesktopRunning)
                {
                    claudeConnected = settings.ClaudeUsageEnabled;
                    if (claudeConnected && claudeBrowser is null && DateTimeOffset.UtcNow >= nextClaudeStart) await StartClaudeDesktopUsageAsync();
                    claudeQuota = claudeDesktopQuota;
                }
                else
                {
                    claudeConnected = ClaudeBridge.IsInstalled();
                    claudeQuota = claudeConnected ? AssistantUsage.ReadClaude(ClaudeBridge.CachePath) : null;
                }
            }
            if (!activity.ClaudeDesktopRunning && claudeBrowser is { IsVisible: false } inactive)
            {
                try { await inactive.FlushSessionAsync(); } finally { inactive.Dispose(); if (claudeBrowser == inactive) claudeBrowser = null; }
            }
            if (activity.CodexRunning && !codexWasRunning) nextCodexRead = DateTimeOffset.MinValue;
            codexWasRunning = activity.CodexRunning;
            UpdateAssistantRows();
            if (activity.CodexRunning && activity.CodexExecutable is { } executable && !usageReading && DateTimeOffset.UtcNow >= nextCodexRead)
                _ = ReadCodexUsageAsync(executable);
        }
        catch (Exception) { /* Denied process/config reads must not interfere with port monitoring. */ }
        finally { usageScanning = false; }
    }

    private async Task ReadCodexUsageAsync(string executable)
    {
        usageReading = true;
        nextCodexRead = DateTimeOffset.UtcNow.AddMinutes(1);
        try { codexQuota = await codexUsage.ReadAsync(executable, telemetryStop.Token); codexUsageError = null; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { codexUsageError = "Kvoten kunde inte uppdateras. Kontrollera inloggningen i Codex. Nytt försök om en minut."; }
        finally { usageReading = false; if (!closed) UpdateAssistantRows(); }
    }

    private void UpdateAssistantRows()
    {
        var rows = new List<AssistantUsageRow>();
        if (assistantActivity.CodexRunning)
            rows.Add(UsageRow("Codex", codexQuota, codexUsageError ?? (assistantActivity.CodexExecutable is null ? "Codex CLI saknas eller kunde inte hittas. Installera den lokala Codex-klienten." : null), "Läser kvot…"));
        if (assistantActivity.ClaudeRunning)
        {
            var row = UsageRow("Claude Code", claudeQuota, assistantActivity.ClaudeDesktopRunning ? claudeDesktopError : null,
                !claudeConnected ? "Klicka för att koppla" : assistantActivity.ClaudeDesktopRunning ? claudeDesktopState switch
                { "login_required" => "Logga in", "paused" => "Öppna användning", "unreadable" => "Kan inte läsa kvot", _ => "Läser kvot…" } : "Väntar på svar…",
                assistantActivity.ClaudeDesktopRunning ? "Claude-appen är öppen. Kvoten läses från kontots användningssida i DevGeeks egen session ungefär varje minut. Klicka för att logga in eller uppdatera; använd samma konto som i Claude-appen." : null);
            if (assistantActivity.ClaudeDesktopRunning && claudeQuota is null && claudeConnected && claudeDesktopState == "login_required")
                row = row with { Value = "Logga in", CompactValue = "Logga in" };
            rows.Add(row);
        }
        if (!AssistantRows.SequenceEqual(rows)) { AssistantRows.Clear(); foreach (var row in rows) AssistantRows.Add(row); }
        LayoutAssistantUsage();
    }

    private static AssistantUsageRow UsageRow(string name, UsageSnapshot? snapshot, string? error, string waiting, string? source = null)
    {
        var now = DateTimeOffset.UtcNow;
        var windows = snapshot?.Windows ?? [];
        var stale = snapshot is not null && now - snapshot.ObservedAt > TimeSpan.FromMinutes(name == "Codex" ? 2 : 5);
        var multipleBuckets = windows.Select(window => window.Name).Distinct().Count() > 1;
        var value = windows.Count == 0 ? error is null ? snapshot is null ? waiting : "Kvot saknas" : "Kan inte läsa kvot"
            : string.Join(" · ", windows.Select(window => (multipleBuckets ? window.Name + " " : "") + window.Label(now))) + " kvar";
        if (windows.Count > 0 && (stale || error is not null)) value += " · äldre";
        var hint = name + " · abonnemangets kvot, delad över kontot.\n";
        if (snapshot is not null) hint += "Senast mottaget " + snapshot.ObservedAt.ToLocalTime().ToString("g") + ".\n";
        foreach (var window in windows)
            hint += $"{(multipleBuckets ? window.Name + " · " : "")}{window.Period}: " + (window.ResetsAt <= now ? "återställningstid passerad, väntar på nytt värde"
                : $"{window.Remaining:0.#}% kvar ({window.UsedPercent:0.#}% använt)")
                + (window.ResetsAt is { } reset ? $". Återställs {reset.ToLocalTime():ddd d MMM HH:mm}." : ". Återställningstid saknas.") + "\n";
        hint += source ?? (name == "Codex" ? "Läses ungefär varje minut medan Codex körs. Klicka för att uppdatera."
            : "Uppdateras via Claude Codes statusrad efter vanliga svar. Saknade kvoter visas inte som noll. Kräver ett konto som lämnar rate_limits. Klicka för kopplingsinformation.");
        if (stale) hint += "\nÄldre avläsning; kan ha ändrats sedan dess.";
        if (error is not null) hint += "\n" + error;
        var valid = windows.Where(window => window.ResetsAt is null || window.ResetsAt > now).ToList();
        return new(name, value, hint.TrimEnd(), stale || error is not null || valid.Count == 0 ? "muted" : valid.Any(w => w.Remaining <= 10) ? "low" : "normal")
        {
            CompactValue = windows.Count == 0 ? value : string.Join(" · ", windows.Select(window => (multipleBuckets ? window.Name + " " : "") + window.Label(now)))
                + (stale || error is not null ? " · äldre" : "")
        };
    }

    private void LayoutAssistantUsage()
    {
        UsageSection.Visibility = AssistantRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageSection.Margin = new Thickness(0);
        CompactUsageBar.Visibility = Visibility.Visible;
        CompactCodexUsage.Visibility = CodexUsageRow is null ? Visibility.Collapsed : Visibility.Visible;
        CompactClaudeUsage.Visibility = ClaudeUsageRow is null ? Visibility.Collapsed : Visibility.Visible;
        UsageDivider.Visibility = CodexUsageRow is not null && ClaudeUsageRow is not null ? Visibility.Visible : Visibility.Collapsed;
        var both = CodexUsageRow is not null && ClaudeUsageRow is not null;
        Grid.SetColumn(CompactClaudeUsage, both ? 2 : 0);
        Grid.SetColumnSpan(CompactCodexUsage, both ? 1 : 3);
        Grid.SetColumnSpan(CompactClaudeUsage, both ? 1 : 3);
        Changed(nameof(CodexUsageRow)); Changed(nameof(ClaudeUsageRow));
        // Recover vertical space from section margins when quota rows are present.
        var extra = AssistantRows.Count == 0 ? 0 : 28;
        MinHeight = 166 + extra;
        DenseScroll.MaxHeight = 308 - extra;
    }

    private async void Usage_Click(object sender, RoutedEventArgs e)
    {
        if (preview || sender is not Button { DataContext: AssistantUsageRow row }) return;
        if (row.Name == "Codex")
        {
            if (!usageReading && assistantActivity.CodexExecutable is { } executable) await ReadCodexUsageAsync(executable);
            else if (!usageReading) Dialogs.Info(this, "Codex", row.Hint);
            return;
        }
        if (assistantActivity.ClaudeDesktopRunning)
        {
            try
            {
                nextClaudeStart = DateTimeOffset.MinValue;
                var previous = settings.ClaudeUsageEnabled; settings.ClaudeUsageEnabled = true;
                if (!Save()) { settings.ClaudeUsageEnabled = previous; return; }
                claudeConnected = true; await StartClaudeDesktopUsageAsync();
                if (claudeBrowser is null) throw new IOException(claudeDesktopError ?? "Claude-sessionen kunde inte öppnas.");
                await claudeBrowser.ShowAsync(); UpdateAssistantRows();
            }
            catch (Exception ex) { Error(ex); }
            return;
        }
        if (claudeConnected) { Dialogs.Info(this, "Claude Code", row.Hint); return; }
        try { ClaudeBridge.Install(); claudeConnected = true; UpdateAssistantRows(); SetStatus("Claude Code är kopplat. Kvoten visas efter nästa vanliga svar."); }
        catch (Exception ex) { Error(ex); }
    }

    private async Task StartClaudeDesktopUsageAsync()
    {
        if (preview || closed || !settings.ClaudeUsageEnabled) return;
        try
        {
            claudeBrowser ??= new(store.DirectoryPath, value =>
            {
                if (closed || !settings.ClaudeUsageEnabled) return;
                claudeDesktopState = value.State;
                if (value.Snapshot is not null) claudeDesktopQuota = value.Snapshot;
                // Keep an older reading marked as such when the page needs login or cannot be read.
                claudeDesktopError = value.State is "ok" ? null : value.State == "login_required" ? "Logga in i Claude-sessionen via kvotraden."
                    : value.State == "paused" ? "Öppna kontots användningssida via kvotraden." : "Claude-kvoten kunde inte läsas. Öppna kvotraden och kontrollera användningssidan.";
                if (assistantActivity.ClaudeDesktopRunning) { claudeQuota = claudeDesktopQuota; UpdateAssistantRows(); }
            }, message =>
            {
                if (closed || message is null) return;
                claudeDesktopError = message; UpdateAssistantRows();
            }, () =>
            {
                settings.ClaudeUsageEnabled = false; Save(); claudeConnected = false;
                claudeDesktopQuota = claudeQuota = null; claudeDesktopError = null; claudeDesktopState = "pending";
                claudeBrowser = null; UpdateAssistantRows();
            });
            await claudeBrowser.StartAsync();
        }
        catch
        {
            claudeBrowser?.Dispose(); claudeBrowser = null; nextClaudeStart = DateTimeOffset.UtcNow.AddMinutes(1);
            UpdateAssistantRows();
        }
    }

    private void LoadUsagePreview()
    {
        AssistantRows.Clear();
        var now = DateTimeOffset.UtcNow;
        AssistantRows.Add(UsageRow("Codex", new(now, [new("codex", 36, 300, now.AddHours(2)), new("codex", 18, 10080, now.AddDays(4))]), null, ""));
        AssistantRows.Add(UsageRow("Claude Code", new(now, [new("Claude Code", 22, 300, now.AddHours(3)), new("Claude Code", 41, 10080, now.AddDays(2))]), null, ""));
        LayoutAssistantUsage();
    }

    private void VerifyAssistantUsage(Action<bool, string> check)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            codexQuota = new(now, [new("codex", 19, 10080, now.AddDays(2))]);
            claudeQuota = new(now, [new("Claude Code", 50, 300, now.AddHours(1))]);
            claudeConnected = true;
            foreach (var compact in new[] { true })
            {
                ConfigureCompactWindow();
                foreach (var active in new[] { (false, false), (true, false), (false, true), (true, true) })
                {
                    assistantActivity = new(active.Item1, active.Item2, null); UpdateAssistantRows(); UpdateLayout();
                    check(AssistantRows.Count == (active.Item1 ? 1 : 0) + (active.Item2 ? 1 : 0)
                        && (UsageSection.Visibility == Visibility.Collapsed) == (!active.Item1 && !active.Item2)
                        && AssistantRows.Any(row => row.Name == "Codex") == active.Item1 && AssistantRows.Any(row => row.Name == "Claude Code") == active.Item2,
                        $"{(compact ? "Compact" : "Standard")} usage visibility follows Codex={active.Item1}, Claude={active.Item2}");
                    check(DenseScroll.ViewportHeight >= 132
                        && (!UsageSection.IsVisible || UsageSection.TranslatePoint(new Point(0, UsageSection.ActualHeight), this).Y <= ActualHeight),
                        $"{(compact ? "Compact" : "Standard")} reserves three port rows with {AssistantRows.Count} usage rows");
                    if (compact)
                    {
                        check(CompactCodexUsage.IsVisible == active.Item1 && CompactClaudeUsage.IsVisible == active.Item2
                            && UsageDivider.IsVisible == (active.Item1 && active.Item2),
                            $"Compact fixed footer slots and divider follow Codex={active.Item1}, Claude={active.Item2}");
                        if (active.Item1 != active.Item2)
                        {
                            var single = active.Item1 ? CompactCodexUsage : CompactClaudeUsage;
                            check(Grid.GetColumn(single) == 0 && Grid.GetColumnSpan(single) == 3
                                && Math.Abs(single.ActualWidth - CompactUsageBar.ActualWidth) <= 1
                                && Math.Abs(single.TranslatePoint(new Point(0, 0), CompactUsageBar).X) <= 1,
                                $"A lone {(active.Item1 ? "Codex" : "Claude")} quota fills the entire compact footer");
                        }
                    }
                }
                var buttons = RowVisuals<Button>(UsageSection).Where(button => button.IsVisible).ToList();
                check(buttons.Count == 2 && buttons.All(button =>
                {
                    var text = RowVisuals<TextBlock>(button).ToArray();
                    return text.Length == 2 && text[0].TranslatePoint(new Point(text[0].ActualWidth, 0), button).X
                        < text[1].TranslatePoint(new Point(0, 0), button).X && text[1].ActualWidth > 0;
                }), (compact ? "Compact" : "Standard") + " quota labels and values fit on separate ends of each row");
                if (compact)
                {
                    check(Math.Abs(CompactCodexUsage.TranslatePoint(new Point(0, 0), this).Y - CompactClaudeUsage.TranslatePoint(new Point(0, 0), this).Y) <= 1
                        && CompactUsageBar.ActualHeight == 27 && Grid.GetColumn(CompactCodexUsage) == 0 && Grid.GetColumn(CompactClaudeUsage) == 2,
                        "Codex and Claude share one 27-pixel footer row on opposite sides of the divider");
                    SizeToContent = SizeToContent.Manual; Width = 720; Height = MaxHeight; UpdateLayout();
                    var footerY = UsageSection.TranslatePoint(new Point(0, 0), this).Y;
                    check(Math.Abs(UsageSection.TranslatePoint(new Point(0, UsageSection.ActualHeight), this).Y - (ActualHeight - 2)) <= 1,
                        "Compact usage footer stays at the bottom when the window is manually resized");
                    var start = DateTime.UtcNow;
                    nativePorts.AddRange(Enumerable.Range(13000, 40).Select((port, i) => new PortEntry(port, "127.0.0.1", 56600 + i, "node", "node", start)));
                    ApplyFilter(); UpdateLayout(); DenseScroll.ScrollToBottom(); UpdateLayout();
                    check(DenseScroll.VerticalOffset > 0 && Math.Abs(UsageSection.TranslatePoint(new Point(0, 0), this).Y - footerY) <= 1
                        && !RowVisuals<Border>(DenseScroll).Contains(UsageSection) && CompactCodexUsage.IsVisible && CompactClaudeUsage.IsVisible,
                        "Only ports scroll; both assistant values stay visible in the fixed footer");
                    nativePorts.RemoveAll(port => port.Pid >= 56600 && port.Pid < 56640); ApplyFilter(); DenseScroll.ScrollToTop();
                    Width = MinWidth; Height = MinHeight; UpdateLayout();
                    check(DenseScroll.ViewportHeight >= 132 && buttons.All(button => button.ActualWidth >= 130),
                        "Compact footer and three port rows still fit at the minimum width and height");
                }
            }
            var expired = UsageRow("Claude Code", new(now.AddHours(-1), [new("Claude Code", 0, 300, now.AddMinutes(-1))]), null, "");
            check(expired.Value.Contains("—") && expired.Value.Contains("äldre") && !expired.Value.Contains("100%"), "Expired and stale usage never invents freshly reset capacity");
            assistantActivity = new(false, true, null, ClaudeDesktopRunning: true);
            claudeQuota = null; claudeConnected = false; UpdateAssistantRows();
            check(ClaudeUsageRow is { Value: "Klicka för att koppla" } && ClaudeUsageRow.Hint.Contains("samma konto") && !ClaudeUsageRow.Hint.Contains("statusrad"),
                "A downloaded Claude app offers its own account connection instead of the terminal statusline");
            claudeConnected = true; claudeDesktopState = "login_required"; claudeDesktopError = "Logga in"; UpdateAssistantRows();
            check(ClaudeUsageRow is { Value: "Logga in", CompactValue: "Logga in" }, "The desktop quota clearly requests sign-in without inventing usage");
        }
        finally { assistantActivity = new(false, false, null); codexQuota = claudeQuota = null; claudeDesktopError = null; claudeDesktopState = "pending"; ConfigureCompactWindow(); LoadUsagePreview(); UpdateLayout(); }
    }
}
