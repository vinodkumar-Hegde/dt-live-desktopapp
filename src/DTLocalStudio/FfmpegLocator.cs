using System.Diagnostics;
using System.IO;
namespace DTLocalStudio;

internal static class FfmpegLocator
{
    private static string Config => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DTLocalStudio","ffmpeg-path.txt");
    private static IEnumerable<string> Candidates(string? requested)
    {
        if(!string.IsNullOrWhiteSpace(requested) && Path.IsPathFullyQualified(requested.Trim().Trim('"')))
            yield return requested.Trim().Trim('"');
        string? saved=null;
        try { if(File.Exists(Config)) saved=File.ReadAllText(Config).Trim(); }catch(Exception e){App.Log("FFMPEG_CONFIG_READ "+e.Message);}
        if(!string.IsNullOrEmpty(saved)&&Path.IsPathFullyQualified(saved))yield return saved;
        yield return Path.Combine(AppContext.BaseDirectory,"ffmpeg.exe");
        yield return Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg","bin","ffmpeg.exe");
        foreach(var scope in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})
            foreach(var entry in (Environment.GetEnvironmentVariable("PATH",scope)??"").Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){
                var dir=Environment.ExpandEnvironmentVariables(entry.Trim('"'));
                if(Path.IsPathFullyQualified(dir))yield return Path.Combine(dir,"ffmpeg.exe");
            }
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local,"Microsoft","WinGet","Links","ffmpeg.exe");
        yield return Path.Combine(local,"Microsoft","WindowsApps","ffmpeg.exe");
        var packages=Path.Combine(local,"Microsoft","WinGet","Packages");
        foreach(var path in PackageExecutables(packages))yield return path;
        var programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach(var path in PackageExecutables(Path.Combine(programFiles,"WinGet","Packages")))yield return path;
        yield return Path.Combine(programFiles,"FFmpeg","bin","ffmpeg.exe");
        yield return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)??@"C:\","ffmpeg","bin","ffmpeg.exe");
    }
    private static IEnumerable<string> PackageExecutables(string directory)
    {
        if(!Directory.Exists(directory))yield break;
        string[] roots;
        try{roots=Directory.GetDirectories(directory,"*FFmpeg*",SearchOption.TopDirectoryOnly);}catch{yield break;}
        foreach(var root in roots.OrderByDescending(x=>x,StringComparer.OrdinalIgnoreCase)){
            string[] files;
            try{files=Directory.GetFiles(root,"ffmpeg.exe",new EnumerationOptions{RecurseSubdirectories=true,MaxRecursionDepth=5,IgnoreInaccessible=true});}catch{continue;}
            foreach(var f in files)yield return f;
        }
    }
    internal static async Task<string> ResolveAsync(string? requested)
    {
        foreach(var candidate in Candidates(requested).Distinct(StringComparer.OrdinalIgnoreCase)){
            if(!File.Exists(candidate))continue;
            try{
                var info=new ProcessStartInfo(candidate){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                info.ArgumentList.Add("-hide_banner");info.ArgumentList.Add("-encoders");
                using var p=Process.Start(info)??throw new IOException("Process could not start.");
                var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
                try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
                catch{try{if(!p.HasExited)p.Kill(true);}catch{}throw;}
                var encoders=await stdout;await stderr;
                if(p.ExitCode!=0||!encoders.Contains("libx264",StringComparison.Ordinal)||!encoders.Contains(" aac ",StringComparison.Ordinal))throw new IOException("Required libx264/AAC encoders unavailable.");
                try{Directory.CreateDirectory(Path.GetDirectoryName(Config)!);File.WriteAllText(Config,candidate);}catch(Exception e){App.Log("FFMPEG_CONFIG_WRITE "+e.Message);}
                App.Log("FFMPEG_READY "+candidate);return candidate;
            }catch(Exception e){App.Log("FFMPEG_CANDIDATE_FAILED "+candidate+" "+e.Message);}
        }
        throw new FileNotFoundException("FFmpeg with libx264 and AAC was not found. Install Gyan.FFmpeg using winget or choose its executable, then retry.");
    }
}
