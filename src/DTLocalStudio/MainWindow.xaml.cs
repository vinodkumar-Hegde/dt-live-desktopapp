using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
namespace DTLocalStudio;
public partial class MainWindow : Window
{
    private Process? preview;
    private readonly DispatcherTimer scanTimer = new() { Interval=TimeSpan.FromSeconds(5) };
    private bool scanning;
    private sealed record Source(int Pid, long Handle, string Label, int Score);
    private long lastAutoHandle;
    private bool closing;
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => App.Log("Window loaded");
        ContentRendered += (_, _) => { App.Log("FIRST_RENDER"); Status.Text = "Monitoring Zoom…"; scanTimer.Start(); ScanClick(this,new RoutedEventArgs()); };
        scanTimer.Tick += (_,_) => ScanClick(this,new RoutedEventArgs());
        Closed += (_, _) => { closing=true; scanTimer.Stop(); StopPreview(); App.Log("Window closed"); };
    }
    private async void ScanClick(object sender, RoutedEventArgs e)
    {
        if(scanning || closing) return;
        if(sender is not System.Windows.Controls.Button && preview!=null && !preview.HasExited) return;
        scanning=true;
        if (sender is System.Windows.Controls.Button button) button.IsEnabled = false;
        try {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
            string file=Path.Combine(Path.GetTempPath(),"dt-scan-"+Guid.NewGuid().ToString("N")+".json");
            info.ArgumentList.Add("--scan"); info.ArgumentList.Add(file);
            List<Source> items;
            try {
                using var worker=Process.Start(info)??throw new InvalidOperationException("Scanner could not start.");
                try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { if(!worker.HasExited) worker.Kill(true); throw; }
                if(worker.ExitCode!=0) throw new InvalidOperationException("Scanner failed: "+worker.ExitCode);
                items=System.Text.Json.JsonSerializer.Deserialize<List<Source>>(File.ReadAllText(file))??[];
            } finally { if(File.Exists(file)) File.Delete(file); }
            if(closing) return;
            var selected=Sources.SelectedItem as Source;
            items=items.OrderByDescending(x=>x.Score).ToList();
            if(Sources.ItemsSource is not List<Source> previous || !previous.SequenceEqual(items)){
                Sources.ItemsSource=items;
                var match=selected==null?-1:items.FindIndex(x=>x.Pid==selected.Pid && x.Handle==selected.Handle);
                Sources.SelectedIndex=match>=0?match:items.Count>0?0:-1;
            }
            Status.Text = items.Count == 0 ? "No Zoom main window found. Restore Zoom and scan again." : $"Found {items.Count} Zoom main window(s). Select the meeting window.";
            if(sender is System.Windows.Controls.Button) App.Log($"ZOOM_SCAN count={items.Count}");
            if(items.All(x=>x.Handle!=lastAutoHandle)) lastAutoHandle=0;
            if(AutoStudio.IsChecked==true && (preview==null || preview.HasExited) && items.FirstOrDefault() is Source best && best.Score>=100 && best.Handle!=lastAutoHandle) {
                Sources.SelectedIndex=0; lastAutoHandle=best.Handle;
                Launch(best,true,true);
            }
        } catch (Exception ex) { Status.Text = ex.Message; App.Log(ex.ToString()); }
        finally { scanning=false; if (sender is System.Windows.Controls.Button b) b.IsEnabled = true; }
    }
    private void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (Sources.SelectedItem is not Source s) { Status.Text = "Scan and select a Zoom source first."; return; }
        Launch(s,sender is System.Windows.Controls.Button { Tag: "capture" },false);
    }
    private void Launch(Source s,bool capture,bool automatic)
    {
        try {
            if(preview!=null && !preview.HasExited) { Status.Text="Close the active studio or preview before opening another."; return; }
            StopPreview();
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            info.ArgumentList.Add(capture ? (automatic?"--auto-capture":"--capture") : "--preview");
            info.ArgumentList.Add(s.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(s.Pid.ToString());
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            preview = Process.Start(info) ?? throw new InvalidOperationException("Preview process did not start");
            var worker = preview;
            worker.EnableRaisingEvents = true;
            worker.Exited += (_, _) => {
                int code;
                try { code = worker.ExitCode; } catch { return; }
                App.Log($"PREVIEW_EXIT code={code}");
                Dispatcher.BeginInvoke(new Action(() => {
                    if (ReferenceEquals(preview, worker)) Status.Text = $"Preview closed (exit {code}). Scan or reopen if needed.";
                }));
            };
            Status.Text = "Preview process requested. Check the separate window.";
            App.Log($"PREVIEW_REQUEST pid={s.Pid}");
        } catch (Exception ex) { Status.Text = ex.Message; App.Log(ex.ToString()); }
    }
    private void StopClick(object sender, RoutedEventArgs e) { AutoStudio.IsChecked=false; if(preview!=null && !preview.HasExited) { preview.CloseMainWindow(); Status.Text="Close requested; wait for recording finalization."; } }
    private void StopPreview()
    {
        var p = preview;
        preview = null;
        if (p == null) return;
        try { if (!p.HasExited) p.CloseMainWindow(); }
        catch (Exception ex) { App.Log($"Preview stop: {ex}"); }
        finally { p.Dispose(); }
    }
    private void TestClick(object sender, RoutedEventArgs e) { Status.Text = $"UI responded at {DateTime.Now:T}"; App.Log("UI_RESPONSE"); }
    private void OpenLogs(object sender, RoutedEventArgs e)
    {
        try { var dir = Path.GetDirectoryName(App.LogPath)!; Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch(Exception ex) { App.Log(ex.ToString()); MessageBox.Show(ex.Message); }
    }
}
