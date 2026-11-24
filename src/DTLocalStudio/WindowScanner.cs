using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace DTLocalStudio;
internal static class WindowScanner
{
    private sealed record Source(int Pid,long Handle,string Label,int Score);
    internal static void Write(string path)
    {
        var processes=new Dictionary<uint,string>();
        foreach(var p in Process.GetProcesses()){
            using(p)try { if(p.ProcessName.StartsWith("Zoom",StringComparison.OrdinalIgnoreCase))processes[(uint)p.Id]=p.ProcessName; }catch{}
        }
        var results=new List<Source>();
        var seen=new HashSet<IntPtr>();
        var foreground=GetForegroundWindow();
        // Iteration avoids unmanaged callback delegates and occurs only in the scan worker.
        IntPtr hwnd=IntPtr.Zero;
        while((hwnd=FindWindowExW(IntPtr.Zero,hwnd,null,null))!=IntPtr.Zero && seen.Add(hwnd) && seen.Count<10000) {
            GetWindowThreadProcessId(hwnd,out uint pid);
            if(!processes.TryGetValue(pid,out var process)||!IsWindowVisible(hwnd)||IsIconic(hwnd)||!GetClientRect(hwnd,out var rect))continue;
            int w=rect.Right-rect.Left,h=rect.Bottom-rect.Top;
            if(w<240||h<160)continue;
            var title=new StringBuilder(512); GetWindowTextW(hwnd,title,title.Capacity);
            var cls=new StringBuilder(256); GetClassNameW(hwnd,cls,cls.Capacity);
            string t=title.ToString(),c=cls.ToString();
            bool meeting=t.Contains("meeting",StringComparison.OrdinalIgnoreCase)||t.Contains("webinar",StringComparison.OrdinalIgnoreCase)||c.Contains("ConfWnd",StringComparison.OrdinalIgnoreCase);
            bool sharing=t.Contains("screen sharing",StringComparison.OrdinalIgnoreCase)||c.Contains("Share",StringComparison.OrdinalIgnoreCase);
            bool home=t.Equals("Zoom Workplace",StringComparison.OrdinalIgnoreCase)||t.Equals("Zoom",StringComparison.OrdinalIgnoreCase)||t.Contains("settings",StringComparison.OrdinalIgnoreCase)||t.Contains("chat",StringComparison.OrdinalIgnoreCase);
            int score=(meeting?120:0)+(sharing?65:0)+(hwnd==foreground?15:0)+Math.Min(30,(int)((long)w*h/100000))-(home?100:0);
            results.Add(new Source((int)pid,hwnd.ToInt64(),$"{(string.IsNullOrWhiteSpace(t)?"Untitled Zoom window":t)} | {w} × {h} | {c} | PID {pid}",score));
        }
        File.WriteAllText(path,JsonSerializer.Serialize(results.OrderByDescending(x=>x.Score)));
        App.Log($"WINDOW_SCAN candidates={results.Count} bestScore={results.Select(x=>x.Score).DefaultIfEmpty(0).Max()}");
    }
    [StructLayout(LayoutKind.Sequential)]private struct R{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern IntPtr FindWindowExW(IntPtr parent,IntPtr after,string? cls,string? title);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr hwnd,out R r);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int GetWindowTextW(IntPtr hwnd,StringBuilder text,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int GetClassNameW(IntPtr hwnd,StringBuilder text,int size);
}
