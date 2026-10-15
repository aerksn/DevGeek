using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PortPilot.App;

/// <summary>Short confirmations floating over the list, and the empty state.</summary>
public partial class MainWindow
{
    private DispatcherTimer? toastTimer;

    /// <summary>Shows a confirmation for a few seconds; problems stay a little longer and get a warning icon.</summary>
    private void SetStatus(string message, bool problem = false)
    {
        if (closed) return;
        ToastText.Text = message;
        ToastIcon.Text = problem ? "" : ""; // Warning or CheckMark
        ToastIcon.Foreground = (Brush)FindResource(problem ? "RoseBrush" : "AccentBrush");
        if (toastTimer is null)
        {
            toastTimer = new DispatcherTimer();
            toastTimer.Tick += (_, _) => { toastTimer.Stop(); FadeToast(0); };
        }
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(problem ? 5 : 3);
        Toast.Visibility = Visibility.Visible;
        FadeToast(1);
        toastTimer.Start();
        // Screen readers hear the message without moving focus.
        (UIElementAutomationPeer.FromElement(ToastText) ?? UIElementAutomationPeer.CreatePeerForElement(ToastText))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FadeToast(double to)
    {
        var fade = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 260));
        if (to == 0) fade.Completed += (_, _) => { if (toastTimer?.IsEnabled != true) HideToast(); };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private void HideToast()
    {
        toastTimer?.Stop();
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 0; Toast.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyState(string query)
    {
        var empty = Ports.Count == 0 && LaunchRows.Count == 0;
        EmptyState.Visibility = empty && scanned && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DenseEmptyPorts.Visibility = empty && (query.Length > 0 || !scanned) ? Visibility.Visible : Visibility.Collapsed;
        DenseEmptyPorts.Text = query.Length > 0 ? $"Inget matchar ”{query}”" : "Läser aktiva portar…";
        if (EmptyState.IsVisible || empty) EmptyHint.Text = groupCache.Count > 0 && developmentOnly
            ? $"{groupCache.Count} {(groupCache.Count == 1 ? "annan process lyssnar" : "andra processer lyssnar")} på portar. Avmarkera Bara utveckling i ⌄-menyn för att se {(groupCache.Count == 1 ? "den" : "dem")}."
            : "Starta en dev-server så dyker den upp här, eller lägg till en snabbstart.";
    }

    /// <summary>Preview: one failing and one warning service plus a visible confirmation, for screenshots.</summary>
    public void LoadStatusExample()
    {
        if (!preview) return;
        Ports.Single(group => group.Name == "demo-web").SetProbe(new("Svarar inte", Core.ServiceTone.Bad, null, "Förhandsvisning · Exempeldata", DateTime.UtcNow));
        Ports.Single(group => group.Name == "dotnet").SetProbe(new("HTTP 404", Core.ServiceTone.Warning, 31, "Förhandsvisning · Exempeldata", DateTime.UtcNow));
        SetStatus("Kopierade http://localhost:3000/.");
        Toast.BeginAnimation(OpacityProperty, null); Toast.Opacity = 1; // Skip the fade so the still image shows it.
    }

    private void VerifyFeedback(Action<bool, string> check)
    {
        var copies = previewCopies = [];
        try
        {
            Copy("http://localhost:5173/", "Kopierade http://localhost:5173/."); UpdateLayout();
            var toastBottom = Toast.TranslatePoint(new Point(0, Toast.ActualHeight), DensePorts).Y;
            check(Toast.IsVisible && !Toast.IsHitTestVisible && ToastText.Text == "Kopierade http://localhost:5173/." && ToastIcon.Text == ""
                && toastBottom <= DensePorts.ActualHeight && toastBottom >= DensePorts.ActualHeight - 12 && Toast.ActualWidth < DensePorts.ActualWidth,
                "A copy confirmation floats at the bottom of the list without taking clicks");
            SetStatus("Urklippet var upptaget. Försök igen.", problem: true); UpdateLayout();
            check(ToastIcon.Text == "" && ToastIcon.Foreground == FindResource("RoseBrush") && toastTimer!.Interval == TimeSpan.FromSeconds(5),
                "A problem replaces the confirmation with a warning icon and stays longer");
            HideToast(); UpdateLayout();
            check(!Toast.IsVisible && toastTimer?.IsEnabled != true, "The confirmation can be dismissed");
        }
        finally { previewCopies = null; HideToast(); }

        var group = Ports.First(owner => !owner.IsDocker);
        var row = RowVisuals<Border>(DensePortList).Single(border => ReferenceEquals(border.DataContext, group) && border.ContextMenu is not null);
        var wash = RowVisuals<System.Windows.Shapes.Rectangle>(row).Single(rectangle => rectangle.Name == "ToneWash");
        var height = row.ActualHeight;
        group.SetProbe(new("HTTP 500", Core.ServiceTone.Bad, 20, "Preview only", DateTime.UtcNow)); UpdateLayout();
        check(wash.Fill is SolidColorBrush { Color.A: > 0 } && Math.Abs(wash.TranslatePoint(new Point(0, 0), row).X) <= 0.5 && row.ActualHeight == height
            && !RowVisuals<System.Windows.Shapes.Rectangle>(DensePortList).Any(rectangle => rectangle.Name == "ToneEdge"),
            "A failing service gets a faint tint across the row, without an edge or a change in row size");
        group.SetProbe(new("HTTP 404", Core.ServiceTone.Warning, 20, "Preview only", DateTime.UtcNow)); UpdateLayout();
        check(wash.Fill == Brushes.Transparent, "A warning keeps only its yellow status text");
        group.SetProbe(new("HTTP 200", Core.ServiceTone.Good, 20, "Preview only", DateTime.UtcNow)); UpdateLayout();
        check(wash.Fill == Brushes.Transparent, "A healthy service has no tint");

        var savedPorts = nativePorts; var savedDocker = docker; var savedLaunches = QuickStarts.ToList();
        try
        {
            nativePorts = []; docker = new([], 0, ""); QuickStarts.Clear(); ApplyFilter(); UpdateLayout();
            check(EmptyState.IsVisible && !DenseEmptyPorts.IsVisible && EmptyMascot.Source is not null && EmptyAdd.IsVisible && EmptyHint.Text.StartsWith("Starta en dev-server")
                && EmptyState.ActualHeight <= DenseScroll.MinHeight, "With nothing running, the empty state shows the otter, a hint and a new quick start button within three rows");
            nativePorts = [new(443, "127.0.0.1", 9999, "other-service", "TCP", null)]; ApplyFilter(); UpdateLayout();
            check(EmptyState.IsVisible && EmptyHint.Text.StartsWith("1 annan process"), "The empty state explains ports hidden by the development filter");
            OpenFilter("finns-inte"); UpdateLayout();
            check(!EmptyState.IsVisible && DenseEmptyPorts.IsVisible, "An empty search shows the search message instead of the empty state");
            CloseFilter();
        }
        finally { nativePorts = savedPorts; docker = savedDocker; foreach (var entry in savedLaunches) QuickStarts.Add(entry); ApplyFilter(); UpdateLayout(); }
        check(!EmptyState.IsVisible && !DenseEmptyPorts.IsVisible && Ports.Count == 5, "The empty state disappears as soon as services return");
    }

    /// <summary>The application icon at the closest size that is at least <paramref name="size"/> pixels.</summary>
    internal static BitmapSource IconFrame(int size)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/otter-monitor.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(frame => frame.PixelWidth >= size ? frame.PixelWidth : 10_000 - frame.PixelWidth).First();
    }
}
