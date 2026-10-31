using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace OtterMonitor.App;

// A real WebView2 child preserves window.opener/postMessage for Google Identity Services.
// It shares the opener's environment and profile, never the user's regular browser profile.
internal sealed class BillingAuthPopup : IDisposable
{
    private readonly Window window;
    private readonly Action<BillingAuthPopup> closed;
    private readonly TextBlock address = new() { FontSize = 11, Margin = new Thickness(12, 8, 12, 8), TextTrimming = TextTrimming.CharacterEllipsis };
    private CoreWebView2Controller? controller;
    private bool disposed, windowClosed;
    internal CoreWebView2 Core => controller!.CoreWebView2;
    internal Window Window => window;
    internal bool IsDisposed => disposed;

    internal BillingAuthPopup(Window owner, Action<BillingAuthPopup> closed)
    {
        this.closed = closed;
        window = new Window
        {
            Owner = owner, Title = UiText.T("Anthropic API · inloggning"), Width = 620, Height = 740,
            MinWidth = 420, MinHeight = 400, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel(); var header = new Border { Height = 34, Child = address };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        window.Content = panel;
        window.SizeChanged += (_, _) => Resize();
        window.IsVisibleChanged += (_, _) => { if (controller is not null) controller.IsVisible = window.IsVisible; };
        window.StateChanged += (_, _) => { if (controller is not null) controller.IsVisible = window.IsVisible && window.WindowState != WindowState.Minimized; };
        window.Closed += (_, _) => { windowClosed = true; Dispose(); };
    }

    internal async Task InitializeAsync(CoreWebView2Environment environment, Action<CoreWebView2> configureFixture,
        Action<CoreWebView2NewWindowRequestedEventArgs, Window> nestedPopup)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var options = environment.CreateCoreWebView2ControllerOptions(); options.ProfileName = "Default"; options.IsInPrivateModeEnabled = false;
        var created = await environment.CreateCoreWebView2ControllerAsync(handle, options);
        if (disposed) { created.Close(); return; }
        controller = created; controller.IsVisible = false; Resize();
        var core = controller.CoreWebView2;
        configureFixture(core);
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (disposed || !Allowed(args.Uri)) { args.Cancel = true; return; }
            address.Text = Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "";
        };
        core.NewWindowRequested += (_, args) => nestedPopup(args, window);
        core.WindowCloseRequested += (_, _) => window.Dispatcher.BeginInvoke(Dispose);
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ProcessFailed += (_, _) => window.Dispatcher.BeginInvoke(Dispose);
    }

    internal static bool Allowed(string url) => url == "about:blank" || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    internal void Show(bool fixture)
    {
        if (disposed || fixture) return;
        window.Show(); window.Activate(); Resize(); controller!.IsVisible = true;
    }
    private void Resize()
    {
        if (controller is null) return;
        if (!GetClientRect(new WindowInteropHelper(window).Handle, out var rect)) return;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var top = (int)Math.Round(34 * scale.DpiScaleY);
        controller.Bounds = new System.Drawing.Rectangle(0, top, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom - top));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        controller?.Close(); controller = null;
        if (!windowClosed) window.Close();
        closed(this);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
}
