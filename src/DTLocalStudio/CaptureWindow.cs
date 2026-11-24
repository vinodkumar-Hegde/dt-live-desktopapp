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
    private readonly Canvas overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly TextBlock selectionInfo = new() { TextWrapping = TextWrapping.Wrap };
    private int activeRegion;
    private Rect? originalDrag;
    private string dragMode = "draw";
    private bool selecting;
    private const double MinRegion = 0.025;
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
        Title = "DT Local Studio — Capture lab 0.3.1";
        Width = 1200; Height = 800; MinWidth = 700; MinHeight = 540;
        Background = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Capture lab", FontSize = 24 });
        top.Children.Add(new TextBlock { Text = "Window capture is experimental. Visible-screen mode captures anything covering Zoom; keep the lab on another monitor or beside Zoom. No recording is saved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,8) });
        var controls = new WrapPanel();
        mode.Items.Add("Window render (experimental)"); mode.Items.Add("Visible screen pixels"); mode.SelectedIndex = 0;
        
        controls.Children.Add(mode);
        AddButton(controls,"Start frames", () => { CancelDrag(); selecting = false; running = true; frozen = false; DrawBoxes(); lastSample = null; frames = 0; status.Text = "Waiting for frames"; });
        AddButton(controls,"Stop frames", () => { CancelDrag(); selecting = false; running = false; frozen = false; DrawBoxes(); });
        AddButton(controls,"Select presentation", () => BeginSelection(0));
        AddButton(controls,"Select faculty", () => BeginSelection(1));
        AddButton(controls,"Done / live", () => { CancelDrag(); selecting = false; frozen = false; running = true; DrawBoxes(); });
        AddButton(controls,"Reset regions", () => { CancelDrag(); screenCrop = facultyCrop = null; presentation.Source = faculty.Source = null; DrawBoxes(); });
        AddButton(controls,"Layout preset", () => {
            if (frame.Source is not BitmapSource b) { status.Text = "Start frames first."; return; }
            screenCrop = new Rect(0.02,0.05,0.72,0.90); facultyCrop = new Rect(0.76,0.65,0.22,0.30);
            UpdateRegions(b); DrawBoxes(); BeginSelection(0);
        });
        AddButton(controls,"Start audio meter", StartAudio);
        AddButton(controls,"Stop audio", StopAudio);
        top.Children.Add(controls);
        var audioRow = new WrapPanel { Margin = new Thickness(0,8,0,8) };
        audioRow.Children.Add(audioText); audioRow.Children.Add(meter); top.Children.Add(audioRow);
        top.Children.Add(status); top.Children.Add(selectionInfo);
        DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2,GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0,0,8,0) };
        var label = new TextBlock { Text = "Source — blue: presentation | green: faculty", Margin = new Thickness(0,8,0,8) };
        DockPanel.SetDock(label,Dock.Top); left.Children.Add(label);
        var sourceGrid = new Grid { ClipToBounds = true, Background = Brushes.Black };
        sourceGrid.Children.Add(frame); sourceGrid.Children.Add(overlay);
        left.Children.Add(sourceGrid);
        overlay.SizeChanged += (_,_) => DrawBoxes();
        grid.Children.Add(left);
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition());
        var p = PanelFor("Presentation region",presentation);
        var c = PanelFor("Faculty region",faculty);
        Grid.SetRow(c,1); right.Children.Add(p); right.Children.Add(c);
        Grid.SetColumn(right,1); grid.Children.Add(right); root.Children.Add(grid); Content = root;
        overlay.MouseLeftButtonDown += (_, e) => {
            if (!selecting || frame.Source is not BitmapSource b) return;
            var p = ImagePoint(e.GetPosition(overlay),b);
            if (p is not Point point) return;
            dragStart = point;
            originalDrag = activeRegion == 0 ? screenCrop : facultyCrop;
            dragMode = "draw";
            if (originalDrag is Rect r) {
                var pos = e.GetPosition(overlay);
                var bounds = DisplayRect(r,b);
                if (Math.Abs(pos.X-bounds.Right)<=18 && Math.Abs(pos.Y-bounds.Bottom)<=18) dragMode="resize";
                else if (r.Contains(point)) dragMode="move";
            }
            overlay.CaptureMouse();
        };
        overlay.MouseMove += (_,e) => {
            if (dragStart is not Point start || frame.Source is not BitmapSource b || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (ImagePoint(e.GetPosition(overlay),b,true) is not Point p) return;
            Rect r;
            if (dragMode=="move" && originalDrag is Rect old)
                r=new Rect(Math.Clamp(old.X+p.X-start.X,0,1-old.Width),Math.Clamp(old.Y+p.Y-start.Y,0,1-old.Height),old.Width,old.Height);
            else if (dragMode=="resize" && originalDrag is Rect size)
                r=new Rect(size.X,size.Y,Math.Clamp(p.X-size.X,Math.Min(MinRegion,1-size.X),1-size.X),Math.Clamp(p.Y-size.Y,Math.Min(MinRegion,1-size.Y),1-size.Y));
            else r=new Rect(start,p);
            if(r.Width<MinRegion || r.Height<MinRegion) return;
            if(activeRegion==0) screenCrop=r; else facultyCrop=r;
            UpdateRegions(b); DrawBoxes();
        };
        overlay.MouseLeftButtonUp += (_,_) => {
            if (dragStart == null) return;
            CancelDrag();
            App.Log($"REGION_SELECTED type={activeRegion}");
            DrawBoxes();
        };
        overlay.LostMouseCapture += (_,_) => { dragStart=null; originalDrag=null; };
        timer.Tick += Tick;
        ContentRendered += (_,_) => { timer.Start(); App.Log("CAPTURE_LAB_READY"); };
        Closed += (_,_) => { closed = true; timer.Stop(); StopAudio(); App.Log("CAPTURE_LAB_CLOSED"); };
    }
    private void CancelDrag()
    {
        dragStart=null; originalDrag=null;
        if(overlay.IsMouseCaptured) overlay.ReleaseMouseCapture();
    }
    private void BeginSelection(int type)
    {
        if(frame.Source is not BitmapSource b) { status.Text="Start frames first, then choose a region."; return; }
        CancelDrag(); activeRegion=type; selecting=true; frozen=true;
        if(type==0 && screenCrop==null) screenCrop=new Rect(0.03,0.05,0.72,0.90);
        if(type==1 && facultyCrop==null) facultyCrop=new Rect(0.76,0.65,0.22,0.30);
        UpdateRegions(b); DrawBoxes();
        status.Text="Editing "+(type==0?"presentation":"faculty")+". Drag inside to move; drag bottom-right handle to resize; drag outside to redraw. Click Done / live when finished.";
    }
    private Rect DisplayRect(Rect r, BitmapSource b)
    {
        double scale=Math.Min(overlay.ActualWidth/b.PixelWidth,overlay.ActualHeight/b.PixelHeight);
        double w=b.PixelWidth*scale,h=b.PixelHeight*scale;
        return new Rect((overlay.ActualWidth-w)/2+r.X*w,(overlay.ActualHeight-h)/2+r.Y*h,r.Width*w,r.Height*h);
    }
    private void DrawBoxes()
    {
        overlay.Children.Clear();
        selectionInfo.Text=$"Presentation: {(screenCrop.HasValue?"selected":"not selected")} | Faculty: {(facultyCrop.HasValue?"selected":"not selected")} | {(selecting?(activeRegion==0?"Editing presentation":"Editing faculty"):"Live / not editing")}";
        if(frame.Source is not BitmapSource b || overlay.ActualWidth<=0 || overlay.ActualHeight<=0) return;
        DrawBox(screenCrop, Brushes.DodgerBlue,"Presentation",0,b);
        DrawBox(facultyCrop, Brushes.LimeGreen,"Faculty",1,b);
    }
    private void DrawBox(Rect? region, Brush brush,string title,int type,BitmapSource b)
    {
        if(region is not Rect r) return;
        var d=DisplayRect(r,b);
        bool active=selecting && activeRegion==type;
        var border=new System.Windows.Shapes.Rectangle { Width=d.Width, Height=d.Height, Stroke=brush, StrokeThickness=active?3:2, IsHitTestVisible=false };
        Canvas.SetLeft(border,d.X); Canvas.SetTop(border,d.Y); overlay.Children.Add(border);
        var label=new TextBlock { Text=title, Background=brush, Foreground=Brushes.Black, Padding=new Thickness(5,2,5,2), IsHitTestVisible=false };
        Canvas.SetLeft(label,d.X); Canvas.SetTop(label,d.Y); overlay.Children.Add(label);
        if(active) {
            var handle=new System.Windows.Shapes.Rectangle { Width=16,Height=16,Fill=brush,Stroke=Brushes.White,StrokeThickness=1,IsHitTestVisible=false };
            Canvas.SetLeft(handle,d.Right-16); Canvas.SetTop(handle,d.Bottom-16); overlay.Children.Add(handle);
        }
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
        double scale = Math.Min(overlay.ActualWidth/b.PixelWidth,overlay.ActualHeight/b.PixelHeight);
        if (scale <= 0) return null;
        double w=b.PixelWidth*scale,h=b.PixelHeight*scale;
        double x=(p.X-(overlay.ActualWidth-w)/2)/w,y=(p.Y-(overlay.ActualHeight-h)/2)/h;
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
            frame.Source = result.Bitmap; UpdateRegions(result.Bitmap); DrawBoxes();
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
