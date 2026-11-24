using System.IO;
using System.Windows;
using System.Reflection;
namespace DTLocalStudio;
public partial class App : Application
{
    private static readonly object Gate = new();
    internal static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DTLocalStudio", "Logs", "startup.log");
    internal static void Log(string message)
    {
        try { lock(Gate) { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {message}{Environment.NewLine}"); } }
        catch(Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log($"Unhandled: {a.ExceptionObject}");
        DispatcherUnhandledException += (_, a) => Log($"UI exception: {a.Exception}");
        TaskScheduler.UnobservedTaskException += (_, a) => Log($"Task exception: {a.Exception}");
        try {
            Log($"START version={Assembly.GetExecutingAssembly().GetName().Version} exe={Environment.ProcessPath} runtime={Environment.Version}");
            base.OnStartup(e);
            Window window;
            if (e.Args.Length == 4 && e.Args[0] == "--preview")
                window = new PreviewWindow(long.Parse(e.Args[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(e.Args[2]), int.Parse(e.Args[3]));
            else if (e.Args.Length == 4 && e.Args[0] == "--capture")
                window = new CaptureWindow(long.Parse(e.Args[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(e.Args[2]), int.Parse(e.Args[3]));
            else
                window = new MainWindow();
            MainWindow = window;
            Log("Main window constructed");
            window.Show();
            Log("Show returned");
        } catch(Exception ex) { Log(ex.ToString()); MessageBox.Show(ex.ToString(), "Startup failed"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { Log($"EXIT code={e.ApplicationExitCode}"); base.OnExit(e); }
}
