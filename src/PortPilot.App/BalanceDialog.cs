using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PortPilot.Core;

namespace PortPilot.App;

public sealed class ExtensionBalanceDialog : Window
{
    public ExtensionBalanceDialog(Func<bool, bool> configure, Func<string> hint, bool preview)
    {
        Title = "OpenAI-saldo"; Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this, enabled: !preview);
        Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "OpenAI-saldo", FontSize = 22, FontWeight = FontWeights.SemiBold });
        Text("Ditt API-saldo, direkt från Billing. Inloggningen stannar i din webbläsare.", 12, new Thickness(0, 8, 0, 16));
        var state = Text(hint(), 11, new Thickness(0, 0, 0, 12));
        Step("1 · Registrera kopplingen", "Kopplar den här DevGeek.exe till Chrome, Edge och Brave för din Windows-användare.");
        Button("Registrera koppling", () => { if (configure(true)) state.Text = "Kopplingen är registrerad. Ladda nu webbläsartillägget i steg 2."; }, primary: true);
        Step("2 · Lägg till webbläsartillägget", "Öppna brave://extensions, chrome://extensions eller edge://extensions. Aktivera utvecklarläge, välj Ladda okomprimerat / Load unpacked och välj tilläggsmappen.");
        Button("Öppna tilläggsmappen", () =>
        {
            var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "browser-extension"));
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Tilläggsmappen saknas. Packa upp hela DevGeek-paketet och behåll app och browser-extension bredvid varandra.");
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        });
        Step("3 · Anslut Billing", "Logga in på OpenAI Billing i samma webbläsare. Öppna tillägget och välj Anslut denna flik. Lämna fliken öppen för automatiska avläsningar.");
        Button("Öppna Billing", () => Process.Start(new ProcessStartInfo(BalanceNativeHost.BillingUrl) { UseShellExecute = true }));
        Text("Tillägget läser bara saldofältet på Billing-översikten och skickar USD-beloppet till appen lokalt. En inaktiv flik kan uppdateras var femte minut. Efter 12 minuter utan ny avläsning markeras saldot som äldre.", 11, new Thickness(0, 18, 0, 14));
        var bottom = new DockPanel();
        var disconnect = new Button { Content = "Koppla från", Style = (Style)FindResource("QuietButton") };
        disconnect.Click += (_, _) => { if (!preview && configure(false)) state.Text = "Kopplingen är frånkopplad. Du kan också ta bort tillägget i webbläsaren."; };
        DockPanel.SetDock(disconnect, Dock.Left); bottom.Children.Add(disconnect);
        var close = new Button { Content = "Stäng", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); bottom.Children.Add(close); panel.Children.Add(bottom);
        Content = panel;

        TextBlock Text(string text, double size, Thickness margin)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = margin, Foreground = (Brush)FindResource("MutedBrush") };
            panel.Children.Add(block); return block;
        }
        void Step(string title, string description)
        {
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 5) });
            Text(description, 11, new Thickness(0, 0, 0, 8));
        }
        void Button(string label, Action action, bool primary = false)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
            if (primary) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) =>
            {
                if (preview) return;
                try { action(); } catch (Exception ex) { state.Text = ex.Message; }
            };
            panel.Children.Add(button);
        }
    }
}

public sealed class BalanceDialog : Window
{
    public BalanceDialog(Func<Task> login, Func<Task> refresh, Func<bool> disconnect, Action extension, Func<string> hint, bool preview)
    {
        Title = "OpenAI-saldo"; Style = (Style)FindResource(typeof(Window));
        _ = new WindowMaterial(this, enabled: !preview);
        Width = 510; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "OpenAI-saldo", FontSize = 22, FontWeight = FontWeights.SemiBold });
        Text(preview ? "Designförhandsvisning · exempeldata. Öppna app/DevGeek.exe för ditt riktiga saldo och din sparade inloggning."
            : "En egen Billing-session i DevGeek. Chrome behöver inte vara öppet.", new Thickness(0, 8, 0, 18));
        var state = Text(hint(), new Thickness(0, 0, 0, 18));
        panel.Children.Add(new TextBlock { Text = "Anslut ditt konto", FontSize = 13, FontWeight = FontWeights.SemiBold });
        Text("Öppna Billing och logga in. Välj rätt organisation och vänta tills saldot visas. Sessionen sparas lokalt och krypterat för nästa appstart. Dölj sedan fönstret med Dölj eller krysset. OpenAI kan ibland kräva en ny inloggning.", new Thickness(0, 7, 0, 12));
        Button("Logga in / Öppna Billing", async () => { await login(); Close(); }, primary: true);
        Text("Avläsningen fortsätter i bakgrunden var femte minut medan DevGeek är igång. Inloggningen sparas i appens egen webbläsarsession. Dina vanliga webbläsare påverkas inte.", new Thickness(0, 18, 0, 12));
        Button("Uppdatera saldo nu", refresh);
        Text("Om inloggningen går ut eller sidan inte kan läsas visar appen att saldot är äldre. Öppna Billing här igen för att fortsätta. Microsoft Edge WebView2 Runtime krävs.", new Thickness(0, 14, 0, 12));
        var extensionButton = new Button { Content = "Använd webbläsartillägg i stället", IsEnabled = !preview, Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 8, 0, 8) };
        extensionButton.Click += (_, _) => { if (!preview) extension(); state.Text = hint(); };
        panel.Children.Add(extensionButton);
        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var stop = new Button { Content = "Koppla från", IsEnabled = !preview, Style = (Style)FindResource("QuietButton") };
        stop.Click += (_, _) => { if (!preview && disconnect()) state.Text = "Bakgrundsläsningen är stoppad. Sessionen sparas för nästa anslutning."; };
        DockPanel.SetDock(stop, Dock.Left); bottom.Children.Add(stop);
        var close = new Button { Content = "Stäng", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); bottom.Children.Add(close); panel.Children.Add(bottom); Content = panel;
        TextBlock Text(string text, Thickness margin)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = margin, Foreground = (Brush)FindResource("MutedBrush") };
            panel.Children.Add(block); return block;
        }
        Button Button(string label, Func<Task> action, bool primary = false)
        {
            var button = new Button { Content = label, IsEnabled = !preview, HorizontalAlignment = HorizontalAlignment.Left };
            if (primary) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += async (_, _) =>
            {
                if (preview) return;
                button.IsEnabled = false;
                try { await action(); state.Text = hint(); }
                catch (Exception ex) { state.Text = ex.Message; }
                finally { button.IsEnabled = true; }
            };
            panel.Children.Add(button); return button;
        }
    }
}
