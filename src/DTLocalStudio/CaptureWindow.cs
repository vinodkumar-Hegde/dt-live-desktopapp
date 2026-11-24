using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
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
    private RecordingSession? recording;
    private bool finishing, allowClose;
    private readonly CheckBox autoRecord = new() { Content="Arm automatic recording", Margin=new Thickness(6), VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock automationInfo = new() { Foreground=Brushes.SteelBlue, TextWrapping=TextWrapping.Wrap };
    private bool confirmedLayout;
    private readonly bool autoLaunch;
    private string? lastWait;
    private int layoutWidth,layoutHeight;
    private long activeSince,lastGoodFrame,lastUiUpdate,lastParentCheck;
    private bool lastDark=true;
    private readonly Stopwatch captureRate=Stopwatch.StartNew();
    private int rateFrames;
    private double measuredFps;
    private string ProfilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DTLocalStudio","layout.json");
    private sealed record LayoutProfile(int Width,int Height,double[] Presentation,double[] Faculty);
    private readonly TextBlock recordingStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
    private readonly TextBox outputFolder = new() { Width = 380, Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"DTLocalStudio") };
    private readonly TextBox ffmpegPath = new() { Width = 220, Text = "ffmpeg.exe" };
    private double rms;
    private long lastAudio;
    private volatile string? audioError;
    private bool busy, closed, running, frozen;
    private Point? dragStart;
    private Rect? screenCrop, facultyCrop;
    private byte[]? lastSample;
    private int frames;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public CaptureWindow(long hwnd, int sourcePid, int parent,bool automatic=false)
    {
        source = new IntPtr(hwnd); pid = sourcePid; parentPid = parent; autoLaunch=automatic;
        Title = "DT Local Studio — Recording Studio 0.6.0";
        Width = 1320; Height = 880; MinWidth = 900; MinHeight = 680;
        Background = new SolidColorBrush(Color.FromRgb(244,247,251));
        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        var header = new StackPanel { Orientation=Orientation.Horizontal };
        header.Children.Add(new Image { Source=new BitmapImage(new Uri("pack://application:,,,/Assets/doctutorials_logo.png")), Width=100, Height=73, Margin=new Thickness(0,0,18,0) });
        header.Children.Add(new TextBlock { Text="Recording Studio", FontSize=28, Foreground=Brushes.DarkSlateGray, VerticalAlignment=VerticalAlignment.Center });
        top.Children.Add(header);
        top.Children.Add(new TextBlock { Text = "Authorized sessions only. Keep Zoom restored. Automatic mode requires a confirmed layout and uses system audio (not Zoom-only). Visible-screen mode includes overlapping windows.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,8) });
        var controls = new WrapPanel();
        mode.Items.Add("Window render (experimental)"); mode.Items.Add("Visible screen pixels"); mode.SelectedIndex = 0;
        
        controls.Children.Add(mode);
        AddButton(controls,"Start frames", () => { CancelDrag(); selecting = false; running = true; frozen = false; DrawBoxes(); lastSample = null; frames = 0; status.Text = "Waiting for frames"; });
        AddButton(controls,"Stop frames", () => { CancelDrag(); selecting = false; running = false; frozen = false; DrawBoxes(); });
        AddButton(controls,"Select presentation", () => BeginSelection(0));
        AddButton(controls,"Select faculty", () => BeginSelection(1));
        AddButton(controls,"Confirm layout / live", ConfirmLayout);
        AddButton(controls,"Reset regions", () => { CancelDrag(); confirmedLayout=false; screenCrop = facultyCrop = null; presentation.Source = faculty.Source = null; DrawBoxes(); });
        AddButton(controls,"Suggest layout", () => {
            if (frame.Source is not BitmapSource b) { status.Text = "Start frames first."; return; }
            screenCrop = new Rect(0.02,0.05,0.72,0.90); facultyCrop = new Rect(0.76,0.65,0.22,0.30);
            confirmedLayout=false; UpdateRegions(b); DrawBoxes(); BeginSelection(0);
        });
        AddButton(controls,"Restore saved layout", RestoreLayout);
        AddButton(controls,"Start audio meter", StartAudio);
        AddButton(controls,"Stop audio", StopAudio);
        top.Children.Add(controls);
        var storage = new WrapPanel { Margin = new Thickness(0,4,0,4) };
        storage.Children.Add(new TextBlock { Text="Output folder: ", VerticalAlignment=VerticalAlignment.Center }); storage.Children.Add(outputFolder);
        AddButton(storage,"Browse folder", () => { var d=new Microsoft.Win32.OpenFolderDialog { Title="Choose recording folder" }; if(d.ShowDialog(this)==true) outputFolder.Text=d.FolderName; });
        AddButton(storage,"Open folder", () => { try { Directory.CreateDirectory(outputFolder.Text); Process.Start(new ProcessStartInfo(outputFolder.Text) { UseShellExecute=true }); } catch(Exception ex) { recordingStatus.Text=ex.Message; } });
        storage.Children.Add(new TextBlock { Text="FFmpeg: ", VerticalAlignment=VerticalAlignment.Center }); storage.Children.Add(ffmpegPath);
        AddButton(storage,"Choose FFmpeg", () => { var d=new Microsoft.Win32.OpenFileDialog { Filter="FFmpeg executable|ffmpeg.exe|Executables|*.exe" }; if(d.ShowDialog(this)==true) ffmpegPath.Text=d.FileName; });
        top.Children.Add(storage);
        var recordControls = new WrapPanel();
        AddButton(recordControls,"Start recording", StartRecord);
        AddButton(recordControls,"Pause recording", () => { recording?.Pause(); });
        AddButton(recordControls,"Resume recording", () => { recording?.Resume(); });
        AddButton(recordControls,"Stop and save MP4", async () => { autoRecord.IsChecked=false; await FinishRecord(); });
        recordControls.Children.Add(autoRecord);
        autoRecord.Checked += (_,_) => { activeSince=0; StartAudio(); automationInfo.Text="Armed: confirm both regions. Starts after sustained audio and valid frames."; };
        autoRecord.Unchecked += (_,_) => { activeSince=0; automationInfo.Text="Automatic start disarmed."; };
        top.Children.Add(recordControls); top.Children.Add(recordingStatus); top.Children.Add(automationInfo);
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
        ContentRendered += (_,_) => { running=true; timer.Start(); if(autoLaunch) autoRecord.IsChecked=true; App.Log("CAPTURE_LAB_READY"); };
        Closing += async (_, e) => {
            if (allowClose || (recording==null && !finishing)) return;
            e.Cancel=true;
            if(finishing) return;
            await FinishRecord(); allowClose=true; Close();
        };
        Closed += (_,_) => { closed = true; timer.Stop(); StopAudio(); App.Log("CAPTURE_LAB_CLOSED"); };
    }
    private void ConfirmLayout()
    {
        if(frame.Source is not BitmapSource b || screenCrop is not Rect p || facultyCrop is not Rect f || Rect.Intersect(p,f).Width>0 && Rect.Intersect(p,f).Height>0) { status.Text="Select two separate, non-overlapping regions first."; return; }
        CancelDrag(); selecting=false; frozen=false; running=true; confirmedLayout=true;
        layoutWidth=b.PixelWidth; layoutHeight=b.PixelHeight;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
            File.WriteAllText(ProfilePath,JsonSerializer.Serialize(new LayoutProfile(layoutWidth,layoutHeight,[p.X,p.Y,p.Width,p.Height],[f.X,f.Y,f.Width,f.Height])));
            App.Log("LAYOUT_CONFIRMED");
        } catch(Exception ex) { App.Log("LAYOUT_SAVE_ERROR "+ex.Message); }
        DrawBoxes();
    }
    private void RestoreLayout()
    {
        if(frame.Source is not BitmapSource b) { status.Text="Wait for live frames."; return; }
        try {
            var profile=JsonSerializer.Deserialize<LayoutProfile>(File.ReadAllText(ProfilePath))??throw new InvalidOperationException("No layout.");
            if(profile.Width!=b.PixelWidth || profile.Height!=b.PixelHeight) throw new InvalidOperationException("Source dimensions changed; select regions again.");
            static Rect Decode(double[] a) {
                if(a.Length!=4 || a.Any(x=>!double.IsFinite(x)) || a[0]<0 || a[1]<0 || a[2]<MinRegion || a[3]<MinRegion || a[0]+a[2]>1.000001 || a[1]+a[3]>1.000001) throw new InvalidDataException("Invalid region.");
                return new Rect(a[0],a[1],a[2],a[3]);
            }
            screenCrop=Decode(profile.Presentation); facultyCrop=Decode(profile.Faculty);
            confirmedLayout=false; UpdateRegions(b); DrawBoxes();
            status.Text="Saved layout restored. Verify content and confirm before recording.";
        } catch(Exception ex) { status.Text="Layout unavailable: "+ex.Message; }
    }
    private void SuggestRegions(BitmapSource b)
    {
        // Layout heuristic only: a shared presentation is normally the largest pane.
        // Never label an unverified crop as a detected person.
        if(b.PixelWidth/(double)b.PixelHeight>=1.4) {
            screenCrop=new Rect(.015,.07,.745,.86);
            facultyCrop=new Rect(.775,.60,.21,.32);
        } else {
            screenCrop=new Rect(.03,.06,.94,.64);
            facultyCrop=new Rect(.58,.73,.38,.22);
        }
        confirmedLayout=false;
        automationInfo.Text="Suggested regions (not identity detection). Adjust and confirm once.";
    }
    private void CancelDrag()
    {
        dragStart=null; originalDrag=null;
        if(overlay.IsMouseCaptured) overlay.ReleaseMouseCapture();
    }
    private void BeginSelection(int type)
    {
        if(frame.Source is not BitmapSource b) { status.Text="Start frames first, then choose a region."; return; }
        CancelDrag(); confirmedLayout=false; activeRegion=type; selecting=true; frozen=true;
        if(type==0 && screenCrop==null) screenCrop=new Rect(0.03,0.05,0.72,0.90);
        if(type==1 && facultyCrop==null) facultyCrop=new Rect(0.76,0.65,0.22,0.30);
        UpdateRegions(b); DrawBoxes();
        status.Text="Editing "+(type==0?"presentation":"faculty")+". Drag inside to move; drag bottom-right handle to resize; drag outside to redraw. Click Confirm layout / live when finished.";
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
    private void AddButton(Panel panel,string title,Action action)
    {
        var b = new Button { Content = title, Padding = new Thickness(9,5,9,5), Margin = new Thickness(3) };
        b.Click += (_,_) => {
            if (finishing) return;
            if (recording!=null && title is not ("Pause recording" or "Resume recording" or "Stop and save MP4" or "Open folder")) { recordingStatus.Text="Stop recording before changing capture or storage settings."; return; }
            action();
        }; panel.Children.Add(b);
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
        long now=Stopwatch.GetTimestamp();
        if(Stopwatch.GetElapsedTime(lastParentCheck,now).TotalSeconds>=1) {
            lastParentCheck=now;
            try { using var parent = Process.GetProcessById(parentPid); if (parent.HasExited) { Close(); return; } }
            catch { Close(); return; }
        }
        var level = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastAudio)).TotalSeconds < 0.7 ? Volatile.Read(ref rms) : 0;
        double db = 20*Math.Log10(Math.Max(level,0.000001));
        meter.Value = Math.Clamp((db+60)*100/60,0,100);
        if (audio != null) audioText.Text = audioError ?? $"System output (not Zoom-only): {db:F1} dBFS | {(db > -45 ? "active" : "quiet")} ";
        if (recording is RecordingSession rec) {
            recordingStatus.Text=rec.Status;
            if(rec.Failure!=null || audioError!=null) { await FinishRecord(); return; }
        }
        if (recording!=null && lastGoodFrame!=0 && Stopwatch.GetElapsedTime(lastGoodFrame).TotalSeconds>5) {
            autoRecord.IsChecked=false; automationInfo.Text="Capture stalled: finalizing recording."; await FinishRecord(); return;
        }
        if (!running || frozen || busy) return;
        busy = true;
        try {
            bool visible = mode.SelectedIndex == 1;
            var currentRecording=recording;
            var p=screenCrop; var f=facultyCrop;
            var result = await Task.Run(() => ReadFrame(visible));
            if (closed || !running || frozen) return;
            lastDark=result.Dark;
            if(!result.Dark) lastGoodFrame=Stopwatch.GetTimestamp();
            if(confirmedLayout && (layoutWidth!=result.Bitmap.PixelWidth || layoutHeight!=result.Bitmap.PixelHeight)) {
                confirmedLayout=false; autoRecord.IsChecked=false;
                if(recording!=null) await FinishRecord();
                status.Text="Zoom layout size changed. Confirm regions again.";
            }
            if(screenCrop==null && facultyCrop==null && !result.Dark) {
                frame.Source=result.Bitmap;
                if(File.Exists(ProfilePath)) RestoreLayout();
                if(screenCrop==null || facultyCrop==null) SuggestRegions(result.Bitmap);
            }
            if (currentRecording!=null && ReferenceEquals(recording,currentRecording) && !currentRecording.IsPaused && !result.Dark && p is Rect pr && f is Rect fr)
                currentRecording.SetFrame(await Task.Run(()=>ComposePixels(result.Bitmap,pr,fr)));
            if(closed || !running || frozen) return;
            rateFrames++;
            if(captureRate.Elapsed.TotalSeconds>=1) { measuredFps=rateFrames/captureRate.Elapsed.TotalSeconds; rateFrames=0; captureRate.Restart(); }
            if(Stopwatch.GetElapsedTime(lastUiUpdate).TotalMilliseconds>=200) {
                lastUiUpdate=Stopwatch.GetTimestamp(); frame.Source=result.Bitmap; UpdateRegions(result.Bitmap); DrawBoxes();
            }
            if(autoRecord.IsChecked==true && recording==null && !finishing) {
                string wait=!confirmedLayout?"Waiting for confirmed faculty/presentation layout":result.Dark?"Waiting for nonblank frames":mode.SelectedIndex!=0?"Automatic recording requires window-render mode":audio==null||audioError!=null?"Waiting for audio device":db<=-40?"Waiting for audio activity":"Ready — checking sustained activity";
                automationInfo.Text=wait;
                if(lastWait!=wait){lastWait=wait; App.Log("AUTO_STATE "+wait);}
            }
            if(autoRecord.IsChecked==true && recording==null && !finishing && confirmedLayout && !result.Dark && mode.SelectedIndex==0 && audio!=null && audioError==null && db> -40) {
                if(activeSince==0) activeSince=Stopwatch.GetTimestamp();
                if(Stopwatch.GetElapsedTime(activeSince).TotalSeconds>=3) {
                    activeSince=0; StartRecord();
                    if(recording==null) autoRecord.IsChecked=false;
                }
            } else activeSince=0;
            int changed=0;
            if (lastSample != null) for(int i=0;i<result.Sample.Length;i++) if (Math.Abs(result.Sample[i]-lastSample[i])>12) changed++;
            double motion = 100.0*changed/result.Sample.Length;
            lastSample = result.Sample; frames++;
            status.Text = $"Capture: {measuredFps:F1} FPS | Frames: {frames} | {result.Bitmap.PixelWidth} × {result.Bitmap.PixelHeight} | motion {motion:F1}% | {(result.Dark ? "Dark/blank candidate — try alternate mode" : "Frame received")} | {clock.Elapsed:hh\\:mm\\:ss}";
            if (frames==1 || frames%50==0) App.Log($"FRAME_RECEIVED count={frames} dark={result.Dark} motion={motion:F1}");
        } catch(Exception ex) { running=false; status.Text=$"Capture stopped: {ex.Message}. Restore Zoom or rescan."; App.Log($"CAPTURE_ERROR {ex}"); autoRecord.IsChecked=false; if(recording!=null) await FinishRecord(); }
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
    private void StartRecord()
    {
        if(recording!=null || finishing) return;
        if(frame.Source is not BitmapSource b || screenCrop is not Rect p || facultyCrop is not Rect f || !running || frozen || !confirmedLayout || lastDark || lastGoodFrame==0 || Stopwatch.GetElapsedTime(lastGoodFrame).TotalSeconds>2) { recordingStatus.Text="Wait for valid live frames and confirm both regions first."; return; }
        try {
            if(!System.IO.Path.IsPathFullyQualified(outputFolder.Text)) throw new InvalidOperationException("Choose an absolute output folder.");
            if(mode.SelectedIndex==1 && MessageBox.Show("Visible-screen mode records overlapping windows as well. Continue?","Capture warning",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
            StartAudio();
            if(audio==null || audioError!=null) throw new InvalidOperationException("System audio could not start.");
            var initial=ComposePixels(b,p,f);
            var session=new RecordingSession(ffmpegPath.Text,outputFolder.Text,audio.WaveFormat.SampleRate,audio.WaveFormat.Channels,initial);
            Volatile.Write(ref recording,session);
            mode.IsEnabled=false; outputFolder.IsEnabled=false; ffmpegPath.IsEnabled=false;
            recordingStatus.Text=session.Status;
            App.Log("RECORD_STARTED "+session.OutputPath);
        } catch(Exception ex) { recordingStatus.Text=ex.Message; App.Log($"RECORD_ERROR {ex}"); }
    }
    private async Task FinishRecord()
    {
        autoRecord.IsChecked=false;
        var session=Interlocked.Exchange(ref recording,null);
        if(session==null) return;
        finishing=true;
        recordingStatus.Text="Finalizing MP4 — do not close the app.";
        try {
            var path=await session.FinishAsync();
            recordingStatus.Text="Saved: "+path;
            App.Log("RECORD_SAVED "+path);
        } catch(Exception ex) { recordingStatus.Text="Recording finalization failed: "+ex.Message+" | Recovery: "+session.WorkFolder; App.Log($"RECORD_ERROR {ex}"); }
        finally { finishing=false; mode.IsEnabled=true; outputFolder.IsEnabled=true; ffmpegPath.IsEnabled=true; }
    }
    private static byte[] ComposePixels(BitmapSource source,Rect p,Rect f)
    {
        // Frozen WPF frame is safe to copy on a worker thread. GDI composition
        // avoids rendering and allocating WPF visuals at the capture cadence.
        int w=source.PixelWidth,h=source.PixelHeight;
        byte[] input=new byte[checked(w*h*4)];
        source.CopyPixels(input,w*4,0);
        using var src=new Drawing.Bitmap(w,h,Drawing.Imaging.PixelFormat.Format32bppRgb);
        var rect=new Drawing.Rectangle(0,0,w,h);
        var bits=src.LockBits(rect,Drawing.Imaging.ImageLockMode.WriteOnly,Drawing.Imaging.PixelFormat.Format32bppRgb);
        try { for(int y=0;y<h;y++) Marshal.Copy(input,y*w*4,IntPtr.Add(bits.Scan0,y*bits.Stride),w*4); }
        finally { src.UnlockBits(bits); }
        using var dest=new Drawing.Bitmap(1280,720,Drawing.Imaging.PixelFormat.Format32bppRgb);
        using(var g=Drawing.Graphics.FromImage(dest)) {
            g.Clear(Drawing.Color.White);
            g.InterpolationMode=Drawing.Drawing2D.InterpolationMode.Bilinear;
            Fit(g,src,p,new Drawing.Rectangle(8,8,952,704));
            Fit(g,src,f,new Drawing.Rectangle(968,432,304,280));
        }
        var output=new byte[1280*720*4];
        var d=dest.LockBits(new Drawing.Rectangle(0,0,1280,720),Drawing.Imaging.ImageLockMode.ReadOnly,Drawing.Imaging.PixelFormat.Format32bppRgb);
        try { for(int y=0;y<720;y++) Marshal.Copy(IntPtr.Add(d.Scan0,y*d.Stride),output,y*1280*4,1280*4); }
        finally { dest.UnlockBits(d); }
        return output;
    }
    private static void Fit(Drawing.Graphics g,Drawing.Bitmap b,Rect r,Drawing.Rectangle area)
    {
        int x=Math.Clamp((int)(r.X*b.Width),0,b.Width-1),y=Math.Clamp((int)(r.Y*b.Height),0,b.Height-1);
        int w=Math.Clamp((int)(r.Width*b.Width),1,b.Width-x),h=Math.Clamp((int)(r.Height*b.Height),1,b.Height-y);
        double scale=Math.Min(area.Width/(double)w,area.Height/(double)h);
        int dw=Math.Max(1,(int)(w*scale)),dh=Math.Max(1,(int)(h*scale));
        g.DrawImage(b,new Drawing.Rectangle(area.X+(area.Width-dw)/2,area.Y+(area.Height-dh)/2,dw,dh),new Drawing.Rectangle(x,y,w,h),Drawing.GraphicsUnit.Pixel);
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
                var active = Volatile.Read(ref recording);
                if(active!=null) active.Audio(e.Buffer,e.BytesRecorded,floating);
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
