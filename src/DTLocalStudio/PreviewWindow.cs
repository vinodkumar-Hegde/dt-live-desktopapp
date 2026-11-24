using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DTLocalStudio;

// DWM thumbnail is a display-only preview, not an encoder frame source.
// Native APIs run only in the explicitly launched preview process.
internal sealed class PreviewWindow : Window
{
    private readonly IntPtr source;
    private readonly int sourcePid;
    private readonly int parentPid;
    private IntPtr thumbnail;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Controls.Border canvas = new() { Background = Brushes.Black };
    private bool failed;
    public PreviewWindow(long hwnd, int pid, int parent)
    {
        source = new IntPtr(hwnd);
        sourcePid = pid;
        parentPid = parent;
        Title = "DT Local Studio — Zoom preview";
        Width = 960; Height = 600; MinWidth = 320; MinHeight = 240;
        Content = canvas;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ContentRendered += (_, _) => Init();
        SizeChanged += (_, _) => Refresh();
        timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => { timer.Stop(); Release(); App.Log("PREVIEW_CLOSED"); };
    }
    private void Init()
    {
        if (thumbnail != IntPtr.Zero || failed) return;
        try {
            CheckSource();
            Check(DwmRegisterThumbnail(new WindowInteropHelper(this).Handle, source, out thumbnail));
            Refresh();
            if (!failed) { timer.Start(); App.Log("PREVIEW_REGISTERED"); }
        } catch (Exception ex) { Fail(ex); }
    }
    private void CheckSource()
    {
        using var parent = Process.GetProcessById(parentPid);
        using var p = Process.GetProcessById(sourcePid);
        if (parent.HasExited || p.HasExited || GetWindowThreadProcessId(source, out uint owner) == 0 || owner != (uint)sourcePid)
            throw new InvalidOperationException("Source or studio closed. Reopen preview from the studio.");
    }
    private void Refresh()
    {
        if (failed || thumbnail == IntPtr.Zero || WindowState == WindowState.Minimized) return;
        try {
            CheckSource();
            Check(DwmQueryThumbnailSourceSize(thumbnail, out var size));
            if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("Source size unavailable.");
            var dpi = VisualTreeHelper.GetDpi(canvas);
            int w = (int)(canvas.ActualWidth * dpi.DpiScaleX), h = (int)(canvas.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0) return;
            double scale = Math.Min((double)w / size.Width, (double)h / size.Height);
            int tw = Math.Max(1, (int)(size.Width * scale)), th = Math.Max(1, (int)(size.Height * scale));
            var props = new Properties { Flags = 0x1 | 0x4 | 0x8, Destination = new Rect { Left = (w-tw)/2, Top = (h-th)/2, Right = (w-tw)/2+tw, Bottom = (h-th)/2+th }, Opacity = 255, Visible = 1 };
            Check(DwmUpdateThumbnailProperties(thumbnail, ref props));
        } catch (Exception ex) { Fail(ex); }
    }
    private void Fail(Exception ex)
    {
        if (failed) return;
        failed = true; timer.Stop(); Release(); App.Log($"PREVIEW_ERROR {ex}");
        canvas.Child = new System.Windows.Controls.TextBlock { Text = ex.Message + "\nClose this window and scan again.", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
    }
    private void Release()
    {
        if (thumbnail != IntPtr.Zero) { DwmUnregisterThumbnail(thumbnail); thumbnail = IntPtr.Zero; }
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Properties { public uint Flags; public Rect Destination, Source; public byte Opacity; public int Visible, SourceClientOnly; }
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref Properties properties);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
