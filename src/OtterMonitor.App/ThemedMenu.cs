using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using OtterMonitor.Core;

namespace OtterMonitor.App;

// A native window lets the menu use the same DWM glass and tint as the main window.
internal sealed class ThemedMenu : Window, IDisposable
{
    private readonly MainWindow host;
    private readonly Menu menu;
    private readonly Border surface;
    private readonly WindowMaterial material;
    private bool disposed;
    public ItemCollection Items => menu.Items;
    public FrameworkElement? PlacementTarget { get; set; }
    internal bool AlignToLeft { get; set; }
    public PlacementMode Placement { get; set; } = PlacementMode.Bottom;
    public event EventHandler? Opened;
    public bool IsOpen { get => IsVisible; set { if (value) Open(); else Hide(); } }
    internal AppearanceSettings Appearance => material.Appearance;
    internal int NativeApplyCount => material.NativeApplyCount;
    internal void VerifyMaterial(Action<bool, string> check) => material.VerifyModernBlur((ok, name) => check(ok, "Menu: " + name));

    public ThemedMenu(MainWindow host, bool preview)
    {
        this.host = host;
        Title = "Otter Monitor · Meny"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight; MinWidth = 210; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        menu = new Menu { IsMainMenu = false, FontSize = 11, Foreground = (Brush)host.FindResource("TextBrush"),
            Template = (ControlTemplate)host.FindResource("ThemedMenuItemsTemplate"), ItemsPanel = (ItemsPanelTemplate)host.FindResource("VerticalMenuItems") };
        surface = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = menu };
        Content = surface;
        material = new(this, enabled: !preview, hasTintLayer: true);
        menu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((_, _) => Hide()));
        Deactivated += (_, _) => { if (!disposed) Hide(); };
        PreviewKeyDown += Navigate;
        Closed += (_, _) => disposed = true;
    }
    internal void ApplyAppearance(AppearanceSettings appearance, bool enabled)
    {
        appearance = appearance.Normalize();
        var backendChanged = material.Appearance.Mode != appearance.Mode || material.Enabled != enabled;
        material.Appearance = appearance; material.Enabled = enabled;
        var color = (Color)ColorConverter.ConvertFromString(appearance.Color);
        surface.Background = appearance.Mode == BackgroundMode.Normal ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(appearance.Mode == BackgroundMode.Opaque ? (byte)255 : appearance.TintAlpha, color.R, color.G, color.B));
        surface.BorderBrush = appearance.ShowBorder ? (Brush)host.FindResource("LineBrush") : Brushes.Transparent;
        if (backendChanged) material.Apply(); else material.ApplyTint();
    }
    private void Open()
    {
        if (disposed || IsVisible) return;
        Owner = host;
        var offscreen = host.Left < -10000;
        Left = host.Left; Top = host.Top;
        ShowActivated = !offscreen;
        Opened?.Invoke(this, EventArgs.Empty);
        Show(); UpdateLayout();
        if (!offscreen)
        {
            Place(); Activate(); menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsEnabled)?.Focus();
        }
    }
    private void Place()
    {
        Point anchor;
        if (Placement == PlacementMode.MousePoint)
        {
            if (!GetCursorPos(out var cursor)) return;
            anchor = new Point(cursor.X, cursor.Y);
        }
        else
        {
            var target = PlacementTarget ?? host;
            anchor = target.PointToScreen(new Point(AlignToLeft ? 0 : target.ActualWidth, target.ActualHeight));
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var point = new NativePoint { X = (int)anchor.X, Y = (int)anchor.Y };
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(point, 2), ref monitor)) return;
        var x = Placement == PlacementMode.MousePoint || AlignToLeft ? point.X : point.X - width;
        var y = point.Y;
        x = Math.Clamp(x, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        y = Math.Clamp(y, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // size/z-order/activation unchanged
    }
    private void Navigate(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.System && e.SystemKey == Key.F4)
        { Hide(); host.Activate(); PlacementTarget?.Focus(); e.Handled = true; return; }
        if (e.Key is not (Key.Down or Key.Up or Key.Tab)) return;
        var choices = Items.OfType<MenuItem>().Where(item => item.IsEnabled).ToArray();
        if (choices.Length == 0) return;
        var index = Array.FindIndex(choices, item => item.IsKeyboardFocusWithin);
        var step = e.Key == Key.Up || e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
        choices[index < 0 ? step > 0 ? 0 : choices.Length - 1 : (index + step + choices.Length) % choices.Length].Focus(); e.Handled = true;
    }
    public void Dispose() { if (!disposed) { disposed = true; Close(); } }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
