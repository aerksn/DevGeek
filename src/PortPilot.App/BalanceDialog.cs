using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PortPilot.App;

public sealed class BalanceDialog : Window
{
    public BalanceDialog(Func<Task> login, Func<Task> refresh, Func<bool> disconnect, Func<string> hint, bool preview)
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
        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var stop = new Button { Content = "Koppla från", IsEnabled = !preview, Style = (Style)FindResource("QuietButton") };
        stop.Click += (_, _) => { if (!preview && disconnect()) state.Text = "Saldoavläsningen är frånkopplad. Anslut igen via Logga in / Öppna Billing."; };
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
