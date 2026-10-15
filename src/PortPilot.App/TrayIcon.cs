using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PortPilot.Core;

namespace PortPilot.App;

/// <summary>
/// Notification-area icon, balloon notices and the global show/hide hotkey through the Win32 shell API.
/// Windows 10/11 present the balloons as regular notifications, without registering an app identity.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 41, HotkeyId = 0x4F4D; // WM_APP + n, "OM"
    private const int WmHotkey = 0x0312, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, NinBalloonUserClick = 0x0405;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private readonly HwndSource window;
    private readonly int taskbarCreated;
    private readonly IntPtr normalIcon, alertIcon;
    private NotifyIconData data;
    private bool alert, added, disposed;
    public event Action? Activated, MenuRequested, NoticeClicked, HotkeyPressed;
    /// <summary>The registered hotkey, or null when another program already uses both candidates.</summary>
    public string? Hotkey { get; }

    public TrayIcon(string tooltip)
    {
        // A hidden top-level window (not message-only) can take the foreground, which popup menus need to close on outside clicks.
        window = new HwndSource(new HwndSourceParameters("OtterMonitorTray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */ });
        window.AddHook(Hook);
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        normalIcon = RenderIcon(false); alertIcon = RenderIcon(true);
        data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(), Window = window.Handle, Id = 1,
            Flags = NifMessage | NifIcon | NifTip | NifShowTip, CallbackMessage = CallbackMessage, Icon = normalIcon, Tip = Trim(tooltip, 127),
        };
        Add();
        // Win+Shift+O first; Ctrl+Alt+O when Windows or another program has reserved it.
        if (Native.RegisterHotKey(window.Handle, HotkeyId, 0x8 | 0x4 | 0x4000, 'O')) Hotkey = "Win+Shift+O";
        else if (Native.RegisterHotKey(window.Handle, HotkeyId, 0x2 | 0x1 | 0x4000, 'O')) Hotkey = "Ctrl+Alt+O";
    }

    public IntPtr Handle => window.Handle;

    public void Update(bool showAlert, string tooltip)
    {
        tooltip = Trim(tooltip, 127);
        if (disposed || showAlert == alert && tooltip == data.Tip) return;
        alert = showAlert; data.Icon = alert ? alertIcon : normalIcon; data.Tip = tooltip;
        data.Flags = NifMessage | NifIcon | NifTip | NifShowTip;
        if (!Native.Shell_NotifyIcon(1, ref data)) Add();
    }

    public void Show(Notice notice)
    {
        if (disposed) return;
        var info = data;
        info.Flags = NifInfo;
        info.InfoTitle = Trim(notice.Title, 63); info.Info = Trim(notice.Message, 255);
        // NIIF_INFO/WARNING/ERROR plus NIIF_RESPECT_QUIET_TIME, so focus assist and presentations stay quiet.
        info.InfoFlags = (notice.Kind switch { NoticeKind.Error => 3u, NoticeKind.Warning => 2u, _ => 1u }) | 0x80;
        Native.Shell_NotifyIcon(1, ref info);
    }

    private void Add()
    {
        added = Native.Shell_NotifyIcon(0, ref data);
        var version = data; version.TimeoutOrVersion = 3; // NOTIFYICON_VERSION: mouse messages in lParam plus balloon clicks.
        if (added) Native.Shell_NotifyIcon(4, ref version);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == taskbarCreated) { Add(); return IntPtr.Zero; } // Explorer restarted and lost every icon.
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId) { handled = true; HotkeyPressed?.Invoke(); return IntPtr.Zero; }
        if (message != CallbackMessage) return IntPtr.Zero;
        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WmLButtonUp: Activated?.Invoke(); break;
            case WmRButtonUp: Native.SetForegroundWindow(window.Handle); MenuRequested?.Invoke(); break;
            case NinBalloonUserClick: NoticeClicked?.Invoke(); break;
        }
        handled = true; return IntPtr.Zero;
    }

    // Render the application icon at the shell's small-icon size, optionally with a status dot, into an HICON.
    private static IntPtr RenderIcon(bool withAlert)
    {
        var size = Math.Max(16, Native.GetSystemMetrics(49)); // SM_CXSMICON
        var frame = MainWindow.IconFrame(size);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(frame, new Rect(0, 0, size, size));
            if (withAlert)
            {
                var radius = size * 0.24;
                var center = new Point(size - radius - 0.5, size - radius - 0.5);
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF0, 0x4A, 0x6E)), new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1A, 0x2C)), size / 16.0), center, radius, radius);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        straight.CopyPixels(pixels, size * 4, 0);
        var color = Native.CreateBitmap(size, size, 1, 32, pixels);
        var mask = Native.CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        try
        {
            var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
            return Native.CreateIconIndirect(ref info);
        }
        finally { Native.DeleteObject(color); Native.DeleteObject(mask); }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Native.UnregisterHotKey(window.Handle, HotkeyId);
        if (added) Native.Shell_NotifyIcon(2, ref data);
        Native.DestroyIcon(normalIcon); Native.DestroyIcon(alertIcon);
        window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public int HotspotX, HotspotY; public IntPtr Mask, Color; }

    private static class Native
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] internal static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr handle);
    }
}
