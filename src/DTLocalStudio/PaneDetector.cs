using System.Windows;
using System.Windows.Media.Imaging;
namespace DTLocalStudio;
internal sealed class PaneDetector
{
    internal sealed record Layout(Rect Presentation,Rect Faculty,double Score);
    private const int W=160,H=90;
    private byte[]? previous;
    private double[] motion=new double[W*H];
    private int observations;
    internal void Reset(){previous=null; Array.Clear(motion); observations=0;}
    internal Layout? Observe(BitmapSource source)
    {
        // Source frames use Bgr32. Sample to a tiny luminance image for cheap layout analysis.
        int sw=source.PixelWidth,sh=source.PixelHeight;
        var pixels=new byte[checked(sw*sh*4)];source.CopyPixels(pixels,sw*4,0);
        byte[] cur=new byte[W*H];
        for(int y=0;y<H;y++)for(int x=0;x<W;x++){
            int i=((y*sh/H)*sw+x*sw/W)*4;
            cur[y*W+x]=(byte)((pixels[i]+2*pixels[i+1]+pixels[i+2])/4);
        }
        if(previous!=null){
            for(int i=0;i<cur.Length;i++) motion[i]=motion[i]*.75+Math.Min(1,Math.Abs(cur[i]-previous[i])/18.0)*.25;
            observations++;
        }
        previous=cur;
        if(observations<4)return null;
        // Find content rectangles bounded by dark neutral Zoom gutters. A white
        // slide may contain dark text, so a row/column is a gutter only if almost
        // all of its samples are dark. Never use pane position to classify it.
        var seen=new bool[W*H];
        var boxes=new List<(int X,int Y,int R,int B,int N)>();
        var queue=new int[W*H];
        for(int y=4;y<H-5;y++)for(int x=2;x<W-2;x++){
            int i=y*W+x;if(seen[i]||cur[i]<35)continue;
            int head=0,tail=0,l=x,r=x,t=y,b=y;queue[tail++]=i;seen[i]=true;
            while(head<tail){
                int q=queue[head++],qx=q%W,qy=q/W;l=Math.Min(l,qx);r=Math.Max(r,qx);t=Math.Min(t,qy);b=Math.Max(b,qy);
                void Visit(int xx,int yy){if(xx<2||xx>=W-2||yy<4||yy>=H-5)return;int k=yy*W+xx;if(seen[k]||cur[k]<35)return;seen[k]=true;queue[tail++]=k;}
                Visit(qx-1,qy);Visit(qx+1,qy);Visit(qx,qy-1);Visit(qx,qy+1);
            }
            int bw=r-l+1,bh=b-t+1;
            if(bw>=12&&bh>=8&&tail>=bw*bh*.35)boxes.Add((l,t,r+1,b+1,tail));
        }
        double Area((int X,int Y,int R,int B,int N) q)=>(q.R-q.X)*(q.B-q.Y);
        double Motion((int X,int Y,int R,int B,int N) q){
            double sum=0;for(int y=q.Y;y<q.B;y++)for(int x=q.X;x<q.R;x++)sum+=motion[y*W+x];return sum/Area(q);
        }
        Rect ToRect((int X,int Y,int R,int B,int N) q)=>new((double)q.X/W,(double)q.Y/H,(double)(q.R-q.X)/W,(double)(q.B-q.Y)/H);
        var sorted=boxes.OrderByDescending(Area).ToList();
        if(sorted.Count<2)return null;
        var big=sorted[0];if(Area(big)<W*H*.23)return null;
        var candidates=new List<Layout>();
        foreach(var small in sorted.Skip(1)){
            double ratio=Area(small)/Area(big);
            double aspect=(small.R-small.X)*(double)sw/W/((small.B-small.Y)*(double)sh/H);
            if(ratio<.025||ratio>.42||aspect<.65||aspect>2.5)continue;
            var p=ToRect(big);var f=ToRect(small);
            if(!Rect.Intersect(p,f).IsEmpty)continue;
            double m=Motion(small),largeMotion=Motion(big);
            // Reject static controls, logos and chat unless a distinct moving pane exists.
            if(m<.018||m<largeMotion*1.25)continue;
            double score=m+Math.Min(.15,ratio);
            candidates.Add(new Layout(p,f,score));
        }
        var ranked=candidates.OrderByDescending(x=>x.Score).ToList();
        if(ranked.Count==0||ranked.Count>1&&ranked[0].Score<ranked[1].Score*1.35)return null;
        return ranked[0];
    }
    internal static bool Similar(Layout a,Layout b)=>Near(a.Presentation,b.Presentation)&&Near(a.Faculty,b.Faculty);
    private static bool Near(Rect a,Rect b)=>Math.Abs(a.X-b.X)<.045&&Math.Abs(a.Y-b.Y)<.045&&Math.Abs(a.Width-b.Width)<.06&&Math.Abs(a.Height-b.Height)<.06;
}
