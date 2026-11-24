using System.Diagnostics;
using System.IO;
namespace DTLocalStudio;

// Fixed-rate proof-of-concept recorder: video is encoded as recoverable MKV,
// then muxed with a wall-clock-aligned PCM stream into MP4.
internal sealed class RecordingSession
{
    private const int Fps=15;
    private readonly object gate=new();
    private readonly Stopwatch active=Stopwatch.StartNew();
    private readonly Process encoder;
    private readonly Task videoTask;
    private readonly Task<string> encoderErrors;
    private readonly FileStream pcm;
    private readonly int sampleRate, channels, block;
    private readonly string ffmpeg, videoPath, audioPath;
    private byte[] latest;
    private long audioFrames;
    private bool paused, stopped;
    private Exception? failure;
    public string OutputPath {get;}
    public string WorkFolder {get;}
    public bool IsPaused {get {lock(gate) return paused;}}
    public Exception? Failure {get {lock(gate) return failure;}}
    public string Status {get {lock(gate) return failure!=null?"Recording error: "+failure.Message:$"{(paused?"PAUSED":"RECORDING")} {active.Elapsed:hh\\:mm\\:ss} | 720p / 15 fps | {OutputPath}";}}
    public RecordingSession(string executable,string folder,int rate,int channelCount,byte[] initial)
    {
        if(rate<=0 || channelCount<1 || channelCount>8) throw new ArgumentException("Unsupported audio format.");
        if(!Path.IsPathFullyQualified(executable)||!File.Exists(executable))throw new FileNotFoundException("Validated FFmpeg executable is missing.",executable);
        ffmpeg=executable; sampleRate=rate; channels=channelCount; block=channels*2; latest=initial;
        Directory.CreateDirectory(folder);
        string name="Session_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6];
        OutputPath=Path.Combine(folder,name+".mp4");
        WorkFolder=Path.Combine(folder,name+".work"); Directory.CreateDirectory(WorkFolder);
        videoPath=Path.Combine(WorkFolder,"video.mkv"); audioPath=Path.Combine(WorkFolder,"audio.pcm");
        pcm=new FileStream(audioPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        try {
            encoder=Start(ffmpeg,["-hide_banner","-loglevel","warning","-nostats","-f","rawvideo","-pixel_format","bgra","-video_size","1280x720","-framerate",Fps.ToString(),"-i","pipe:0","-an","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-b:v","1500k","-maxrate","1700k","-bufsize","3400k","-g","30","-f","matroska",videoPath],true);
            encoderErrors=encoder.StandardError.ReadToEndAsync();
            videoTask=Task.Run(WriteVideo);
        } catch { pcm.Dispose(); throw; }
    }
    private static Process Start(string exe,string[] args,bool input)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardInput=input};
        foreach(var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)??throw new InvalidOperationException("FFmpeg could not start.");
    }
    public void SetFrame(byte[] bytes){lock(gate){if(!stopped&&!paused)latest=bytes;}}
    public void Pause(){lock(gate){if(!stopped&&!paused){active.Stop(); paused=true;}}}
    public void Resume(){lock(gate){if(!stopped&&paused){paused=false;active.Start();}}}
    private void Fail(Exception e){lock(gate){failure??=e;active.Stop();stopped=true;} }
    private async Task WriteVideo()
    {
        long written=0;
        try {
            var stream=encoder.StandardInput.BaseStream;
            while(true) {
                long target; bool done; byte[] bytes;
                lock(gate){target=Math.Max(1,(long)Math.Ceiling(active.Elapsed.TotalSeconds*Fps));done=stopped;bytes=latest;}
                if(written<target){
                    if(target-written>Fps*10)throw new IOException("Encoder is more than ten seconds behind.");
                    await stream.WriteAsync(bytes);written++;continue;
                }
                if(done)break;
                if(encoder.HasExited)throw new IOException("FFmpeg exited during capture.");
                await Task.Delay(10);
            }
            await stream.FlushAsync();
        } catch(Exception e){Fail(e);}
        finally {try{encoder.StandardInput.Close();}catch(Exception e){Fail(e);}}
    }
    public void Audio(byte[] data,int count,bool floating)
    {
        lock(gate){
            if(stopped||paused)return;
            try {
                int step=floating?4:2,n=count/(step*channels);
                long end=(long)(active.Elapsed.TotalSeconds*sampleRate);
                long start=Math.Max(0,end-n);
                if(start>audioFrames) Silence(start-audioFrames);
                int skip=(int)Math.Min(n,Math.Max(0,audioFrames-start));
                var bytes=new byte[(n-skip)*block];
                for(int i=skip*channels,j=0;i<n*channels;i++,j+=2){
                    double v=floating?BitConverter.ToSingle(data,i*4):BitConverter.ToInt16(data,i*2)/32768.0;
                    short x=(short)Math.Clamp(double.IsFinite(v)?v*32768:0,-32768,32767);
                    bytes[j]=(byte)x;bytes[j+1]=(byte)(x>>8);
                }
                pcm.Write(bytes);audioFrames+=n-skip;
            }catch(Exception e){failure??=e;active.Stop();stopped=true;}
        }
    }
    private void Silence(long frames)
    {
        byte[] zero=new byte[8192/block*block];
        while(frames>0){int n=(int)Math.Min(frames,zero.Length/block);pcm.Write(zero,0,n*block);audioFrames+=n;frames-=n;}
    }
    public async Task<string> FinishAsync()
    {
        lock(gate){active.Stop();stopped=true;}
        try {
            await videoTask.WaitAsync(TimeSpan.FromSeconds(25));
            await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            string errors=await encoderErrors;
            lock(gate){
                long target=(long)Math.Ceiling(Math.Max(0.2,active.Elapsed.TotalSeconds)*sampleRate);
                if(audioFrames<target)Silence(target-audioFrames);
                pcm.Dispose();
            }
            if(encoder.ExitCode!=0 || Failure!=null)throw new IOException($"Video capture failed: {Failure?.Message} {errors}");
            string temp=Path.Combine(WorkFolder,"final.mp4");
            using var mux=Start(ffmpeg,["-hide_banner","-loglevel","warning","-i",videoPath,"-f","s16le","-ar",sampleRate.ToString(),"-ac",channels.ToString(),"-i",audioPath,"-map","0:v:0","-map","1:a:0","-c:v","copy","-c:a","aac","-b:a","96k","-shortest","-movflags","+faststart",temp],false);
            var stderr=mux.StandardError.ReadToEndAsync();
            try { await mux.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10)); }
            catch { if(!mux.HasExited)mux.Kill(true);throw; }
            if(mux.ExitCode!=0 || !File.Exists(temp) || new FileInfo(temp).Length==0)throw new IOException("MP4 mux failed: "+await stderr);
            File.Move(temp,OutputPath);
            try{Directory.Delete(WorkFolder,true);}catch(Exception e){App.Log("Recovery cleanup: "+e.Message);}
            return OutputPath;
        } finally {
            lock(gate){pcm.Dispose();}
            try{if(!encoder.HasExited)encoder.Kill(true);}catch{}
            encoder.Dispose();
        }
    }
}
