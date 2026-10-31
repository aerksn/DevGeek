using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OtterMonitor.Core;

namespace OtterMonitor.App;

// Freeze the desktop before displaying the overlay so its own UI cannot tint the sampled pixel.
internal static class ScreenColorPicker
{
    internal static Color Pixel(byte[] pixels, int width, int height, int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) throw new ArgumentOutOfRangeException(nameof(x));
        var offset = checked((y * width + x) * 4);
        return Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    internal static async Task<Color?> PickAsync(Window owner)
    {
        var windows = new[] { owner, owner.Owner }.OfType<Window>().Distinct().ToArray();
        var opacity = windows.Select(window => window.Opacity).ToArray();
        try
        {
            foreach (var window in windows) window.Opacity = 0;
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(150); // Allow DWM to compose the desktop without the color dialog.
            var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            var pixels = Capture(left, top, width, height);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, checked(width * 4));
            bitmap.Freeze();
            var canvas = new Canvas();
            var overlay = new Window
            {
                Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross, WindowStartupLocation = WindowStartupLocation.Manual,
                Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill }, Content = canvas,
                Title = UiText.T("Välj färg från skärmen"), UseLayoutRounding = true
            };
            var sample = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 9, 0) };
            var value = new TextBlock { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(sample); label.Children.Add(value);
            var hint = new Border { Background = new SolidColorBrush(Color.FromRgb(35, 32, 43)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = label, IsHitTestVisible = false };
            canvas.Children.Add(hint);
            Color? result = null;
            void Update()
            {
                if (!GetCursorPos(out var point)) return;
                var color = Pixel(pixels, width, height, Math.Clamp(point.X - left, 0, width - 1), Math.Clamp(point.Y - top, 0, height - 1));
                sample.Background = new SolidColorBrush(color);
                value.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}  ·  " + UiText.T("Klicka för att välja · Esc avbryter");
                var position = Mouse.GetPosition(overlay);
                hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(hint, Math.Clamp(position.X + 20, 0, Math.Max(0, overlay.ActualWidth - hint.DesiredSize.Width)));
                Canvas.SetTop(hint, Math.Clamp(position.Y + 24, 0, Math.Max(0, overlay.ActualHeight - hint.DesiredSize.Height)));
            }
            overlay.SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(overlay).Handle, new IntPtr(-1), left, top, width, height, 0x0010);
            overlay.Loaded += (_, _) => { SetWindowPos(new WindowInteropHelper(overlay).Handle, new IntPtr(-1), left, top, width, height, 0x0040); Update(); overlay.Activate(); };
            overlay.MouseMove += (_, _) => Update();
            overlay.MouseLeftButtonDown += (_, e) =>
            {
                if (!GetCursorPos(out var point)) return;
                if (point.X < left || point.X >= left + width || point.Y < top || point.Y >= top + height) return;
                result = Pixel(pixels, width, height, point.X - left, point.Y - top);
                e.Handled = true; overlay.Close();
            };
            overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; overlay.Close(); } };
            overlay.Deactivated += (_, _) => { if (overlay.IsVisible) overlay.Close(); };
            overlay.ShowDialog();
            return result;
        }
        finally
        {
            for (var i = 0; i < windows.Length; i++) windows[i].Opacity = opacity[i];
            owner.Activate();
        }
    }

    private static byte[] Capture(int left, int top, int width, int height)
    {
        var length = checked((long)width * height * 4);
        if (width <= 0 || height <= 0 || length > 512 * 1024 * 1024) throw new InvalidOperationException(UiText.T("Skärmen kunde inte läsas."));
        var screen = GetDC(IntPtr.Zero); var memory = IntPtr.Zero; var bitmap = IntPtr.Zero; var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException(UiText.T("Skärmen kunde inte läsas."));
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, 0x40CC0020)) throw new InvalidOperationException(UiText.T("Skärmen kunde inte läsas."));
            var pixels = new byte[(int)length]; Marshal.Copy(bits, pixels, 0, pixels.Length); return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
