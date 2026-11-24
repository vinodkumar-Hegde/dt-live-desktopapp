using System.Diagnostics;
using System.IO;
using System.Windows;
namespace DTLocalStudio;
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => App.Log("Window loaded");
        ContentRendered += (_, _) => { App.Log("FIRST_RENDER"); Status.Text = "Window rendered successfully"; };
        Closed += (_, _) => App.Log("Window closed");
    }
    private void TestClick(object sender, RoutedEventArgs e) { Status.Text = $"UI responded at {DateTime.Now:T}"; App.Log("UI_RESPONSE"); }
    private void OpenLogs(object sender, RoutedEventArgs e)
    {
        try { var dir = Path.GetDirectoryName(App.LogPath)!; Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch(Exception ex) { App.Log(ex.ToString()); MessageBox.Show(ex.Message); }
    }
}
