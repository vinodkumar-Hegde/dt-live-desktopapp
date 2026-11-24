using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using Drawing = System.Drawing;

namespace DTLocalStudio;

// Experimental capture laboratory. No media files are written.
internal sealed class CaptureWindow : Window
{
    private readonly IntPtr source;
    private readonly int pid, parentPid;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Image frame = new() { Stretch = Stretch.Uniform };
    private readonly Image presentation = new() { Stretch = Stretch.Uniform };
    private readonly Image faculty = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock audioText = new() { Text = "System output audio: off" };
    private readonly ProgressBar meter = new() { Minimum = 0, Maximum = 100, Height = 12, Width = 150 };
    private readonly ComboBox mode = new() { Width = 205 };
    private readonly ComboBox region = new() { Width = 140 };
    private WasapiLoopbackCapture? audio;
    private double rms;
    private long lastAudio;
    private volatile string? audioError;
    private bool busy, closed, running, frozen;
    private Point? dragStart;
    private Rect? screenCrop, facultyCrop;
    private byte[]? lastSample;
    private int frames;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public CaptureWindow(long hwnd, int sourcePid, int parent)
    {
        source = new IntPtr(hwnd); pid = sourcePid; parentPid = parent;
        Title = "DT Local Studio — Capture lab 0.3.0";
        Width = 1200; Height = 800; MinWidth = 700; MinHeight = 540;
        Background = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Capture lab", FontSize = 24 });
        top.Children.Add(new TextBlock { Text = "Window capture is experimental. Visible-screen mode captures anything covering Zoom; keep the lab on another monitor or beside Zoom. No recording is saved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,8) });
        var controls = new WrapPanel();
        mode.Items.Add("Window render (experimental)"); mode.Items.Add("Visible screen pixels"); mode.SelectedIndex = 0;
        region.Items.Add("Presentation"); region.Items.Add("Faculty"); region.SelectedIndex = 0;
        controls.Children.Add(mode);
        AddButton(controls,"Start frames", () => { running = true; frozen = false; lastSample = null; frames = 0; status.Text = "Waiting for frames"; });
        AddButton(controls,"Stop frames", () => { running = false; frozen = false; });
        AddButton(controls,"Freeze / resume", () => { frozen = !frozen; status.Text = frozen ? "Frozen. Select region type and drag on source image." : "Resumed"; });
        controls.Children.Add(region);
        AddButton(controls,"Clear regions", () => { screenCrop = facultyCrop = null; presentation.Source = faculty.Source = null; });
        AddButton(controls,"Start audio meter", StartAudio);
        AddButton(controls,"Stop audio", StopAudio);
        top.Children.Add(controls);
        var audioRow = new WrapPanel { Margin = new Thickness(0,8,0,8) };
        audioRow.Children.Add(audioText); audioRow.Children.Add(meter); top.Children.Add(audioRow);
        top.Children.Add(status);
        DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2,GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0,0,8,0) };
        var label = new TextBlock { Text = "Source — freeze and drag to select regions", Margin = new Thickness(0,8,0,8) };
        DockPanel.SetDock(label,Dock.Top); left.Children.Add(label);
        left.Children.Add(new Border { Background = Brushes.Black, Child = frame });
        grid.Children.Add(left);
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition());
        var p = PanelFor("Presentation region",presentation);
        var c = PanelFor("Faculty region",faculty);
        Grid.SetRow(c,1); right.Children.Add(p); right.Children.Add(c);
        Grid.SetColumn(right,1); grid.Children.Add(right); root.Children.Add(grid); Content = root;
        frame.MouseLeftButtonDown += (_, e) => {
            if (!frozen || frame.Source is not BitmapSource b) return;
            dragStart = ImagePoint(e.GetPosition(frame), b); if (dragStart != null) frame.CaptureMouse();
        };
        frame.MouseLeftButtonUp += (_, e) => {
            if (dragStart is not Point start || frame.Source is not BitmapSource b) return;
            dragStart = null; frame.ReleaseMouseCapture();
            var end = ImagePoint(e.GetPosition(frame), b, true);
            if (end is not Point ep || Math.Abs(ep.X-start.X)<0.01 || Math.Abs(ep.Y-start.Y)<0.01) return;
            var rect = new Rect(start,ep);
            if (region.SelectedIndex == 0) screenCrop = rect; else facultyCrop = rect;
            UpdateRegions(b); App.Log($"REGION_SELECTED type={region.SelectedIndex}"); status.Text = "Region selected. Resume frames when ready.";
        };
        timer.Tick += Tick;
        ContentRendered += (_,_) => { timer.Start(); App.Log("CAPTURE_LAB_READY"); };
        Closed += (_,_) => { closed = true; timer.Stop(); StopAudio(); App.Log("CAPTURE_LAB_CLOSED"); };
    }
    private static DockPanel PanelFor(string name, Image image)
    {
        var panel = new DockPanel { Margin = new Thickness(0,4,0,4) };
        var text = new TextBlock { Text = name, Margin = new Thickness(0,4,0,4) };
        DockPanel.SetDock(text,Dock.Top); panel.Children.Add(text);
        panel.Children.Add(new Border { Background = Brushes.WhiteSmoke, Child = image }); return panel;
    }
    private static void AddButton(Panel panel,string title,Action action)
    {
        var b = new Button { Content = title, Padding = new Thickness(9,5,9,5), Margin = new Thickness(3) };
        b.Click += (_,_) => action(); panel.Children.Add(b);
    }
    private Point? ImagePoint(Point p, BitmapSource b, bool clamp = false)
    {
        double scale = Math.Min(frame.ActualWidth/b.PixelWidth,frame.ActualHeight/b.PixelHeight);
        if (scale <= 0) return null;
        double w=b.PixelWidth*scale,h=b.PixelHeight*scale;
        double x=(p.X-(frame.ActualWidth-w)/2)/w,y=(p.Y-(frame.ActualHeight-h)/2)/h;
        if (!clamp && (x<0 || x>1 || y<0 || y>1)) return null;
        return new Point(Math.Clamp(x,0,1),Math.Clamp(y,0,1));
    }
    private void UpdateRegions(BitmapSource b)
    {
        presentation.Source = Crop(b,screenCrop); faculty.Source = Crop(b,facultyCrop);
    }
    private static BitmapSource? Crop(BitmapSource b, Rect? region)
    {
        if (region is not Rect r) return null;
        int x=Math.Clamp((int)(r.X*b.PixelWidth),0,b.PixelWidth-1), y=Math.Clamp((int)(r.Y*b.PixelHeight),0,b.PixelHeight-1);
        int w=Math.Clamp((int)(r.Width*b.PixelWidth),1,b.PixelWidth-x), h=Math.Clamp((int)(r.Height*b.PixelHeight),1,b.PixelHeight-y);
        return new CroppedBitmap(b,new Int32Rect(x,y,w,h));
    }
    private async void Tick(object? sender,EventArgs e)
    {
        if (closed) return;
        try { using var parent = Process.GetProcessById(parentPid); if (parent.HasExited) { Close(); return; } }
        catch { Close(); return; }
        var level = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastAudio)).TotalSeconds < 0.7 ? Volatile.Read(ref rms) : 0;
        double db = 20*Math.Log10(Math.Max(level,0.000001));
        meter.Value = Math.Clamp((db+60)*100/60,0,100);
        if (audio != null) audioText.Text = audioError ?? $"System output (not Zoom-only): {db:F1} dBFS | {(db > -45 ? "active" : "quiet")} ";
        if (!running || frozen || busy) return;
        busy = true;
        try {
            bool visible = mode.SelectedIndex == 1;
            var result = await Task.Run(() => ReadFrame(visible));
            if (closed || !running || frozen) return;
            frame.Source = result.Bitmap; UpdateRegions(result.Bitmap);
            int changed=0;
            if (lastSample != null) for(int i=0;i<result.Sample.Length;i++) if (Math.Abs(result.Sample[i]-lastSample[i])>12) changed++;
            double motion = 100.0*changed/result.Sample.Length;
            lastSample = result.Sample; frames++;
            status.Text = $"Frames: {frames} | {result.Bitmap.PixelWidth} × {result.Bitmap.PixelHeight} | motion {motion:F1}% | {(result.Dark ? "Dark/blank candidate — try alternate mode" : "Frame received")} | {clock.Elapsed:hh\\:mm\\:ss}";
            if (frames==1 || frames%50==0) App.Log($"FRAME_RECEIVED count={frames} dark={result.Dark} motion={motion:F1}");
        } catch(Exception ex) { running=false; status.Text=$"Capture stopped: {ex.Message}. Restore Zoom or rescan."; App.Log($"CAPTURE_ERROR {ex}"); }
        finally { busy=false; }
    }
    private sealed record FrameResult(BitmapSource Bitmap,byte[] Sample,bool Dark);
    private FrameResult ReadFrame(bool visible)
    {
        if (GetWindowThreadProcessId(source,out uint owner)==0 || owner!=(uint)pid || IsIconic(source))
            throw new InvalidOperationException("Zoom source closed or minimized.");
        if (!GetClientRect(source,out var r)) throw new InvalidOperationException("Cannot read source size.");
        int w=r.Right-r.Left,h=r.Bottom-r.Top;
        if(w<1 || h<1 || w>8192 || h>8192 || (long)w*h>33554432) throw new InvalidOperationException("Unsupported source dimensions.");
        using var bitmap = new Drawing.Bitmap(w,h,Drawing.Imaging.PixelFormat.Format32bppRgb);
        using(var g=Drawing.Graphics.FromImage(bitmap)) {
            if (visible) {
                var p=new NativePoint();
                if (!ClientToScreen(source,ref p)) throw new InvalidOperationException("Cannot locate source on screen.");
                g.CopyFromScreen(p.X,p.Y,0,0,new Drawing.Size(w,h));
            } else {
                var dc=g.GetHdc();
                try { if(!PrintWindow(source,dc,3)) throw new InvalidOperationException("Window rendering failed; try visible screen mode."); }
                finally { g.ReleaseHdc(dc); }
            }
        }
        var data=bitmap.LockBits(new Drawing.Rectangle(0,0,w,h),Drawing.Imaging.ImageLockMode.ReadOnly,Drawing.Imaging.PixelFormat.Format32bppRgb);
        byte[] bytes=new byte[checked(w*h*4)];
        try { for(int y=0;y<h;y++) Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),bytes,y*w*4,w*4); }
        finally { bitmap.UnlockBits(data); }
        var b=BitmapSource.Create(w,h,96,96,PixelFormats.Bgr32,null,bytes,w*4); b.Freeze();
        byte[] sample=new byte[32*18]; int dark=0;
        for(int y=0;y<18;y++) for(int x=0;x<32;x++){
            int i=((y*h/18)*w+x*w/32)*4;
            byte v=(byte)((bytes[i]+bytes[i+1]+bytes[i+2])/3);
            sample[y*32+x]=v; if(v<8) dark++;
        }
        return new FrameResult(b,sample,dark>sample.Length*0.98);
    }
    private void StartAudio()
    {
        if (audio != null) return;
        try {
            var a = new WasapiLoopbackCapture();
            audio = a; audioError = null;
            var format = a.WaveFormat;
            bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (!(floating && format.BitsPerSample==32) && !(!floating && format.BitsPerSample==16))
                throw new NotSupportedException($"Unsupported meter format: {format}");
            a.DataAvailable += (_,e) => {
                double sum=0; int step=floating?4:2,count=e.BytesRecorded/step;
                for(int i=0;i+step<=e.BytesRecorded;i+=step) {
                    double s=floating?BitConverter.ToSingle(e.Buffer,i):BitConverter.ToInt16(e.Buffer,i)/32768.0;
                    if(double.IsFinite(s)) sum+=s*s;
                }
                Volatile.Write(ref rms,Math.Sqrt(sum/Math.Max(1,count))); Interlocked.Exchange(ref lastAudio,Stopwatch.GetTimestamp());
            };
            a.RecordingStopped += (_,e) => { if(e.Exception!=null) { audioError=e.Exception.Message; App.Log($"AUDIO_ERROR {e.Exception}"); } };
            a.StartRecording(); App.Log($"AUDIO_STARTED {format}");
        } catch(Exception ex) { StopAudio(); audioText.Text=ex.Message; App.Log($"AUDIO_ERROR {ex}"); }
    }
    private void StopAudio()
    {
        var a=audio; audio=null;
        if(a!=null) { try { a.StopRecording(); } catch(Exception ex) { App.Log(ex.ToString()); } finally { a.Dispose(); } }
        Volatile.Write(ref rms,0); Interlocked.Exchange(ref lastAudio,0); meter.Value=0; audioText.Text="System output audio: off";
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd,out NativeRect r);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd,ref NativePoint p);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
}
