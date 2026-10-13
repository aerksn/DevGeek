using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using PortPilot.Core;

namespace PortPilot.App;

/// <summary>Desktop Acrylic via DWM. Text remains opaque; only the window's material is translucent.</summary>
public sealed class WindowMaterial
{
    private readonly Window window;
    private readonly bool hasTintLayer;
    private HwndSource? source;
    private bool closed;
    private int? framePolicyResult;
    private bool applyPending;
    public bool Enabled { get; set; }
    public AppearanceSettings Appearance { get; set; } = new();
    public bool IsAcrylic { get; private set; }
    public bool IsTransparent { get; private set; }
    public string Status { get; private set; } = "Solid material";
    public int NativeApplyCount { get; private set; }

    public WindowMaterial(Window window, bool enabled = true, bool hasTintLayer = false)
    {
        this.window = window; this.hasTintLayer = hasTintLayer; Enabled = enabled;
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(WindowMessage);
            Apply();
        };
        window.Loaded += (_, _) => Apply();
        window.Activated += (_, _) => ScheduleApply();
        window.StateChanged += (_, _) => ScheduleApply();
        SystemParameters.StaticPropertyChanged += ParametersChanged;
        window.Closed += (_, _) =>
        {
            closed = true;
            SystemParameters.StaticPropertyChanged -= ParametersChanged;
            source?.RemoveHook(WindowMessage);
        };
    }

    private static bool TransparencyEnabled()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not int value || value != 0; }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Apply()
    {
        if (closed || source?.CompositionTarget is null) return;
        NativeApplyCount++;
        var handle = source.Handle;
        int dark = 1, rounded = 2, noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        var chrome = WindowChrome.GetWindowChrome(window);

        var appearance = Appearance.Normalize();
        bool eligible = Enabled && OperatingSystem.IsWindowsVersionAtLeast(10) && !SystemParameters.HighContrast && TransparencyEnabled();
        IsTransparent = false; IsAcrylic = false;
        // Reset both native backends so switching to Clear/Opaque never retains the previous blur.
        int none = 1;
        DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
        SetAccent(handle, 0);
        if (eligible)
        {
            if (appearance.Mode is BackgroundMode.Blur or BackgroundMode.Acrylic && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                // The classic accent blur reports success without blurring on newer Windows 11.
                // Use the supported DWM frosted backdrop for both glass modes, retaining the caption fix.
                int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
                IsAcrylic = IsTransparent = DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int)) >= 0;
            }
            else if (appearance.Mode is BackgroundMode.Clear or BackgroundMode.Blur or BackgroundMode.Acrylic)
            {
                int state = appearance.Mode == BackgroundMode.Clear ? 2 : appearance.Mode == BackgroundMode.Blur ? 3 : 4;
                // Tint is applied once, in the WPF surface layer.
                IsTransparent = SetAccent(handle, state, state == 4 ? 0x01000000u : 0u);
                IsAcrylic = IsTransparent && appearance.Mode == BackgroundMode.Acrylic;
            }
        }
        if (chrome is not null) chrome.GlassFrameThickness = new Thickness(IsTransparent ? -1 : 0);
        var margins = new Margins { Left = IsTransparent ? -1 : 0 };
        var frameResult = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (frameResult < 0) IsTransparent = IsAcrylic = false;
        if (chrome is not null)
        {
            // WPF owns the entire frame, including the toolbar. Disabling the native
            // non-client renderer prevents its active caption from covering this area.
            // Apply after WindowChrome's glass update, which may restore the DWM frame.
            int disabled = 1; // DWMNCRP_DISABLED
            framePolicyResult = DwmSetWindowAttribute(handle, 2, ref disabled, sizeof(int));
        }

        ApplyTint();
        Status = IsTransparent ? $"{appearance.Mode} · transparent composition target"
            : appearance.Mode is BackgroundMode.Normal or BackgroundMode.Opaque ? $"{appearance.Mode} · opaque background"
            : $"{appearance.Mode} · solid fallback (transparency unavailable or disabled)";
    }

    public void ApplyTint()
    {
        if (closed || source?.CompositionTarget is null) return;
        var appearance = Appearance.Normalize();
        var color = (Color)ColorConverter.ConvertFromString(appearance.Color);
        source.CompositionTarget.BackgroundColor = IsTransparent ? Colors.Transparent : color;
        window.Background = IsTransparent ? hasTintLayer ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(appearance.TintAlpha, color.R, color.G, color.B))
            : appearance.Mode == BackgroundMode.Normal ? (Brush)window.FindResource("BackgroundBrush") : new SolidColorBrush(color);
    }

    private static bool SetAccent(IntPtr handle, int state, uint color = 0)
    {
        var policy = new AccentPolicy { State = state, Flags = state is 2 or 3 ? 2 : 0, Color = color };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new CompositionData { Attribute = 19, Data = pointer, Size = (nuint)Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(handle, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public string Describe()
    {
        var handle = new WindowInteropHelper(window).Handle;
        var hr = DwmGetWindowAttribute(handle, 38, out int value, sizeof(int));
        var frameHr = DwmGetWindowAttribute(handle, 1, out int frameEnabled, sizeof(int));
        var frame = framePolicyResult is { } result ? $"Custom frame policy HRESULT: 0x{result:X8}" : "Standard system caption";
        return $"{Status}\nWindows: {Environment.OSVersion.Version}\nBackdrop readback: {value} (HRESULT 0x{hr:X8})\n{frame}\nNative frame enabled: {frameEnabled != 0} (HRESULT 0x{frameHr:X8})\nWindow active: {window.IsActive}\nWindow state: {window.WindowState}\nWindow opacity: {window.Opacity}\nHigh contrast: {SystemParameters.HighContrast}\nTransparency preference: {TransparencyEnabled()}";
    }

    public void VerifyModernBlur(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast || !TransparencyEnabled()) return;
        var enabled = Enabled; var appearance = Appearance;
        try
        {
            Enabled = true; Appearance = appearance with { Mode = BackgroundMode.Blur }; Apply();
            if (!IsTransparent)
            {
                check(!IsAcrylic && source?.CompositionTarget?.BackgroundColor.A == 255, "Unavailable native blur keeps an opaque fallback instead of pretending to be transparent");
                return;
            }
            var hr = DwmGetWindowAttribute(source!.Handle, 38, out int backdrop, sizeof(int));
            check(hr >= 0 && backdrop == 3 && IsTransparent && IsAcrylic, "Windows 11 Blur activates the DWM frosted backdrop instead of the ineffective classic accent blur");
            var frameHr = DwmGetWindowAttribute(source.Handle, 1, out int nativeFrame, sizeof(int));
            check(framePolicyResult == 0 && frameHr >= 0 && nativeFrame == 0, "Blur keeps the custom non-client policy that prevents the active caption stripe");
            Appearance = appearance with { Mode = BackgroundMode.Clear }; Apply();
            hr = DwmGetWindowAttribute(source.Handle, 38, out backdrop, sizeof(int));
            check(hr >= 0 && backdrop == 1 && !IsAcrylic, "Switching from Blur to Clear removes the DWM blur backend");
        }
        finally { Enabled = enabled; Appearance = appearance; Apply(); }
    }

    private void ParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleApply();
    }

    private void ScheduleApply()
    {
        if (closed || applyPending) return;
        applyPending = true;
        window.Dispatcher.BeginInvoke(() => { applyPending = false; Apply(); });
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x031A or 0x031E or 0x0320) ScheduleApply(); // settings/theme/composition/color
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public nuint Size; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
