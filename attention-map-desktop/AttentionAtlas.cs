using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AttentionAtlas {
    internal static class Native {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X; public int Y; }
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        internal delegate IntPtr MouseHook(int code,IntPtr message,IntPtr data);
        [StructLayout(LayoutKind.Sequential)] internal struct MouseHookData {internal POINT Point;internal uint MouseData,Flags,Time;internal UIntPtr ExtraInfo;}
        [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id,MouseHook callback,IntPtr module,uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Auto)] internal static extern IntPtr GetModuleHandle(string name);
    }
    public sealed class ScreenArea {
        public int X {get;set;} public int Y {get;set;} public int Width {get;set;} public int Height {get;set;}
        public string Name {get;set;}
        public Rectangle Rectangle { get { return new Rectangle(X,Y,Width,Height); } }
        public static ScreenArea From(Rectangle r, string name) { return new ScreenArea {X=r.X,Y=r.Y,Width=r.Width,Height=r.Height,Name=name}; }
    }
    public sealed class MouseSample {
        public int X {get;set;} public int Y {get;set;} public int Ms {get;set;}
        public double Dt {get;set;} public bool Break {get;set;}
    }
    public sealed class Session {
        public Session(){Clicks=new List<MouseClick>();}
        public int Version {get;set;}
        public string Id {get;set;} public string StartedUtc {get;set;} public string EndReason {get;set;}
        public int DurationMs {get;set;} public int ElapsedMs {get;set;}
        public bool Completed {get;set;}
        public ScreenArea Bounds {get;set;} public List<ScreenArea> Screens {get;set;}
        public List<MouseSample> Samples {get;set;}
        public List<MouseClick> Clicks {get;set;}
    }
    public sealed class MouseClick {
        public int X {get;set;} public int Y {get;set;} public int Ms {get;set;} public int Button {get;set;}
    }
    public sealed class CaptureSnapshot {
        public Session Session; public bool Running; public int ElapsedMs;
    }
    public sealed class CursorCapture : IDisposable {
        private readonly object gate=new object();
        private readonly Stopwatch clock=new Stopwatch();
        private System.Threading.Timer timer;
        private Session session; private bool running; private bool broken=true;
        private long previousMs;
        private IntPtr mouseHook;
        private readonly Native.MouseHook hookCallback;
        public CursorCapture(){hookCallback=OnMouse;}
        public event Action<Session> Finished;
        public bool Running { get { lock(gate) return running; } }
        public void Start(int durationMs, Rectangle bounds, List<ScreenArea> screens) {
            if(durationMs<100 || durationMs>3600000) throw new ArgumentOutOfRangeException("durationMs");
            lock(gate) {
                if(running) throw new InvalidOperationException("已经在记录");
                if(timer!=null) timer.Dispose();
                mouseHook=Native.SetWindowsHookEx(14,hookCallback,Native.GetModuleHandle(null),0);
                if(mouseHook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法开启全局点击记录");
                session=new Session {Version=3,Id=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6),StartedUtc=DateTime.UtcNow.ToString("o"),EndReason="正在记录",DurationMs=durationMs,Bounds=ScreenArea.From(bounds,"全屏"),Screens=screens,Samples=new List<MouseSample>(),Completed=false};
                previousMs=0;broken=true;running=true;clock.Restart();
                timer=new System.Threading.Timer(Poll,null,0,50);
            }
        }
        private IntPtr OnMouse(int code,IntPtr message,IntPtr data) {
            int kind=message.ToInt32(),button=kind==0x0201?0:kind==0x0204?2:kind==0x0207?1:-1;
            if(code>=0&&button>=0) {
                var point=(Native.MouseHookData)Marshal.PtrToStructure(data,typeof(Native.MouseHookData));
                RecordClick(point.Point.X,point.Point.Y,button);
            }
            return Native.CallNextHookEx(IntPtr.Zero,code,message,data);
        }
        internal void RecordClick(int x,int y,int button) {
            lock(gate) {
                int elapsed=(int)clock.ElapsedMilliseconds;
                if(running&&elapsed<session.DurationMs&&button>=0&&button<=2) {
                    if(!session.Bounds.Rectangle.Contains(x,y))session.Bounds=ScreenArea.From(Rectangle.Union(session.Bounds.Rectangle,new Rectangle(x,y,1,1)),"全屏");
                    session.Clicks.Add(new MouseClick {X=x,Y=y,Ms=elapsed,Button=button});session.ElapsedMs=Math.Max(session.ElapsedMs,elapsed);
                }
            }
        }
        private void ReleaseHook(){if(mouseHook!=IntPtr.Zero){Native.UnhookWindowsHookEx(mouseHook);mouseHook=IntPtr.Zero;}}
        private void Poll(object state) {
            Session done=null;
            lock(gate) {
                if(!running) return;
                long now=clock.ElapsedMilliseconds;
                int elapsed=(int)Math.Min(session.DurationMs,now);
                Native.POINT point;
                if(Native.GetCursorPos(out point)) {
                    if(!session.Bounds.Rectangle.Contains(point.X,point.Y)) {
                        Rectangle expanded=Rectangle.Union(session.Bounds.Rectangle,new Rectangle(point.X,point.Y,1,1));
                        session.Bounds=ScreenArea.From(expanded,"全屏");
                    }
                    // Long scheduler / sleep gaps are explicitly disconnected, never treated as continuous dwell.
                    double dt=Math.Min(.2,Math.Max(0,(elapsed-previousMs)/1000.0));
                    session.Samples.Add(new MouseSample {X=point.X,Y=point.Y,Ms=elapsed,Dt=dt,Break=broken||now-previousMs>500});
                    broken=false;
                } else broken=true;
                previousMs=elapsed;session.ElapsedMs=elapsed;
                if(now>=session.DurationMs)done=FinishLocked("时间到");
            }
            if(done!=null)RaiseFinished(done);
        }
        public Session Stop(string reason) {
            Session done;
            lock(gate) { if(!running) return session==null?null:CopySession(session);done=FinishLocked(reason); }
            RaiseFinished(done);return done;
        }
        private Session FinishLocked(string reason) {
            running=false;clock.Stop();ReleaseHook();if(timer!=null)timer.Change(Timeout.Infinite,Timeout.Infinite);
            session.ElapsedMs=(int)Math.Min(session.DurationMs,clock.ElapsedMilliseconds);
            session.EndReason=reason;session.Completed=true;return CopySession(session);
        }
        private void RaiseFinished(Session done) { var handler=Finished;if(handler!=null)handler(done); }
        public CaptureSnapshot Snapshot() {
            lock(gate) {
                if(session==null)return new CaptureSnapshot();
                Session copy=CopySession(session);
                copy.ElapsedMs=running?(int)Math.Min(copy.DurationMs,clock.ElapsedMilliseconds):copy.ElapsedMs;
                return new CaptureSnapshot {Session=copy,Running=running,ElapsedMs=copy.ElapsedMs};
            }
        }
        internal static Session CopySession(Session s) {
            return new Session {Version=s.Version,Id=s.Id,StartedUtc=s.StartedUtc,EndReason=s.EndReason,DurationMs=s.DurationMs,ElapsedMs=s.ElapsedMs,Completed=s.Completed,Bounds=s.Bounds,Screens=new List<ScreenArea>(s.Screens),Samples=new List<MouseSample>(s.Samples),Clicks=new List<MouseClick>(s.Clicks??new List<MouseClick>())};
        }
        public void Dispose() { lock(gate){running=false;ReleaseHook();if(timer!=null)timer.Dispose();clock.Stop();} }
    }
    internal sealed class ClickCluster {
        internal double X,Y,AnchorX,AnchorY;
        internal int Count,Ms;
    }
    internal static class TrailStyle {
        internal static readonly Color[] Colors=MakeColors();
        private static Color[] MakeColors() {
            var colors=new List<Color>{Color.FromArgb(118,202,213),Color.FromArgb(146,172,238),Color.FromArgb(193,155,219),Color.FromArgb(234,170,142),Color.FromArgb(221,199,130),Color.FromArgb(141,203,170)};
            for(int i=6;i<60;i++) {
                double h=((183+i*137.508)%360)/60,c=.52*(1-Math.Abs(2*.68-1)),x=c*(1-Math.Abs(h%2-1)),m=.68-c/2;
                double r=0,g=0,b=0;
                if(h<1){r=c;g=x;}else if(h<2){r=x;g=c;}else if(h<3){g=c;b=x;}else if(h<4){g=x;b=c;}else if(h<5){r=x;b=c;}else{r=c;b=x;}
                colors.Add(Color.FromArgb((int)Math.Round((r+m)*255),(int)Math.Round((g+m)*255),(int)Math.Round((b+m)*255)));
            }
            return colors.ToArray();
        }
        internal static int Minute(int ms){return Math.Max(0,ms)/60000;}
        internal static Color ColorAt(int ms){return Colors[Minute(ms)%Colors.Length];}
        internal static void Segments(MouseSample a,MouseSample b,int until,Action<PointF,PointF,int> draw) {
            if(b.Break||a.Ms>until||(a.X==b.X&&a.Y==b.Y))return;
            int end=Math.Min(b.Ms,until),start=a.Ms;
            if(end<start)return;
            if(b.Ms==a.Ms){draw(new PointF(a.X,a.Y),new PointF(b.X,b.Y),Minute(start));return;}
            while(start<end) {
                int next=Math.Min(end,(Minute(start)+1)*60000);
                double f0=(start-a.Ms)/(double)(b.Ms-a.Ms),f1=(next-a.Ms)/(double)(b.Ms-a.Ms);
                draw(new PointF((float)(a.X+(b.X-a.X)*f0),(float)(a.Y+(b.Y-a.Y)*f0)),new PointF((float)(a.X+(b.X-a.X)*f1),(float)(a.Y+(b.Y-a.Y)*f1)),Minute(start));
                start=next;
            }
        }
        internal static List<ClickCluster> Clusters(IEnumerable<MouseClick> clicks,int until) {
            var clusters=new List<ClickCluster>();var cells=new Dictionary<string,List<ClickCluster>>();
            if(clicks==null)return clusters;
            foreach(var p in clicks) {
                if(p.Ms>until)break;
                int cx=(int)Math.Floor(p.X/24.0),cy=(int)Math.Floor(p.Y/24.0);ClickCluster nearest=null;double best=24*24+0.001;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) {
                    List<ClickCluster> bucket;
                    if(!cells.TryGetValue((cx+dx)+","+(cy+dy),out bucket))continue;
                    foreach(var c in bucket){double d=Math.Pow(p.X-c.AnchorX,2)+Math.Pow(p.Y-c.AnchorY,2);if(d<best){nearest=c;best=d;}}
                }
                if(nearest==null) {
                    nearest=new ClickCluster {X=p.X,Y=p.Y,AnchorX=p.X,AnchorY=p.Y};clusters.Add(nearest);
                    string key=cx+","+cy;List<ClickCluster> bucket;if(!cells.TryGetValue(key,out bucket)){bucket=new List<ClickCluster>();cells.Add(key,bucket);}bucket.Add(nearest);
                }
                nearest.Count++;nearest.X+=(p.X-nearest.X)/nearest.Count;nearest.Y+=(p.Y-nearest.Y)/nearest.Count;nearest.Ms=p.Ms;
            }
            return clusters;
        }
        internal static void Legend(Graphics g,Rectangle area,int elapsed,bool showHeat=true,bool showClicks=true) {
            int count=Math.Max(1,(Math.Max(1,elapsed)-1)/60000+1),visible=Math.Min(count,Math.Max(1,(area.Width-38)/66));
            int start=Math.Max(0,count-visible);float x=area.X+18,y=area.Bottom-39;
            for(int i=start;i<count;i++) {
                Color c=Colors[i%Colors.Length];using(var pen=new Pen(c,2))g.DrawLine(pen,x,y+7,x+14,y+7);
                Theme.TextAt(g,(i+1)+" min",9,Theme.Muted,x+20,y);x+=66;
            }
            Theme.TextAt(g,"每分钟换色    /    "+DwellStyle.Legend(showHeat,showClicks),9,Theme.Muted,area.X+18,area.Bottom-22);
        }
    }
    internal sealed class DwellSpot {
        internal double X,Y,AnchorX,AnchorY,Seconds;
    }
    internal static class DwellStyle {
        internal static List<DwellSpot> Measure(IEnumerable<MouseSample> samples,int until) {
            var spots=new List<DwellSpot>();var cells=new Dictionary<string,List<DwellSpot>>();
            MouseSample anchor=null;double seconds=0,weightedX=0,weightedY=0;
            Action commit=()=>{
                if(seconds<1-1e-6)return;
                double x=weightedX/seconds,y=weightedY/seconds;
                int cx=(int)Math.Floor(x/24),cy=(int)Math.Floor(y/24);DwellSpot nearest=null;double best=24*24+0.001;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) {
                    List<DwellSpot> bucket;if(!cells.TryGetValue((cx+dx)+","+(cy+dy),out bucket))continue;
                    foreach(var spot in bucket){double d=Math.Pow(x-spot.AnchorX,2)+Math.Pow(y-spot.AnchorY,2);if(d<best){nearest=spot;best=d;}}
                }
                if(nearest==null) {
                    nearest=new DwellSpot{AnchorX=x,AnchorY=y};spots.Add(nearest);
                    string key=cx+","+cy;List<DwellSpot> bucket;if(!cells.TryGetValue(key,out bucket)){bucket=new List<DwellSpot>();cells.Add(key,bucket);}bucket.Add(nearest);
                }
                double total=nearest.Seconds+seconds;
                nearest.X=(nearest.X*nearest.Seconds+weightedX)/total;nearest.Y=(nearest.Y*nearest.Seconds+weightedY)/total;nearest.Seconds=total;
            };
            foreach(var p in samples) {
                if(p.Ms>until)break;
                if(anchor==null||p.Break||Math.Pow(p.X-anchor.X,2)+Math.Pow(p.Y-anchor.Y,2)>24*24) {
                    commit();anchor=p;seconds=0;weightedX=0;weightedY=0;
                }
                seconds+=p.Dt;weightedX+=p.X*p.Dt;weightedY+=p.Y*p.Dt;
            }
            commit();return spots;
        }
        // A single shared scale preserves area ratios even when the longest stop is hours long.
        internal static double Unit(RectangleF fit,double longest) {
            double side=Math.Min(fit.Width,fit.Height);
            return Math.Min(side/100,side*.22/Math.Sqrt(Math.Max(1,longest)));
        }
        internal static float Radius(double seconds,double unit){return (float)(Math.Sqrt(seconds)*unit);}
        internal static string Legend(bool heat,bool clicks) {
            return (heat?"停留圆面积与时长成比例 · 数字为秒 (s)":"")+(heat&&clicks?"    /    ":"")+(clicks?"点击圆数字为次数":"");
        }
        internal static void Draw(Graphics g,RectangleF fit,Rectangle bounds,double mapScale,IEnumerable<MouseSample> samples,int until,bool spectrum) {
            var spots=Measure(samples,until);if(spots.Count==0)return;
            double unit=Unit(fit,spots.Max(s=>s.Seconds));Color color=spectrum?Color.FromArgb(85,197,159):Theme.Accent;
            var state=g.Save();g.SetClip(fit,CombineMode.Intersect);
            try {
                using(var fill=new SolidBrush(Color.FromArgb(29,color)))using(var pen=new Pen(Color.FromArgb(180,color),1))
                foreach(var spot in spots.OrderByDescending(s=>s.Seconds)) {
                    float x=(float)(fit.X+(spot.X-bounds.X)*mapScale),y=(float)(fit.Y+(spot.Y-bounds.Y)*mapScale),r=Radius(spot.Seconds,unit);
                    g.FillEllipse(fill,x-r,y-r,2*r,2*r);g.DrawEllipse(pen,x-r,y-r,2*r,2*r);
                }
                using(var font=Theme.Font(10))using(var text=new SolidBrush(color))using(var background=new SolidBrush(Color.FromArgb(215,Theme.Background)))
                foreach(var spot in spots.Where(s=>s.Seconds>=5-1e-6)) {
                    string label=spot.Seconds.ToString(spot.Seconds<10?"0.0":"0",CultureInfo.InvariantCulture)+" s";
                    float x=(float)(fit.X+(spot.X-bounds.X)*mapScale),y=(float)(fit.Y+(spot.Y-bounds.Y)*mapScale),r=Radius(spot.Seconds,unit);
                    SizeF size=g.MeasureString(label,font);
                    float left=Math.Max(fit.Left+2,Math.Min(fit.Right-size.Width-6,x-size.Width/2));
                    float top=Math.Max(fit.Top+2,Math.Min(fit.Bottom-size.Height-4,y+r+3));
                    g.FillRectangle(background,left-3,top-1,size.Width+6,size.Height+2);g.DrawString(label,font,text,left,top);
                }
            }finally{g.Restore(state);}
        }
    }
    internal sealed class Statistics {
        internal double Distance, Coverage, LongestDwell;
        internal static Statistics Calculate(Session s, int until) {
            var result=new Statistics();if(s==null)return result;
            var cells=new HashSet<int>();MouseSample previous=null,anchor=null;double dwell=0;
            foreach(var p in s.Samples) {
                if(p.Ms>until)break;
                bool connected=previous!=null&&!p.Break;
                double distance=connected?Math.Sqrt(Math.Pow(p.X-previous.X,2)+Math.Pow(p.Y-previous.Y,2)):0;
                result.Distance+=distance;
                int steps=Math.Max(1,(int)Math.Ceiling(distance/16));
                for(int i=1;i<=steps;i++) {
                    double x=connected?previous.X+(p.X-previous.X)*(double)i/steps:p.X;
                    double y=connected?previous.Y+(p.Y-previous.Y)*(double)i/steps:p.Y;
                    int cx=Math.Max(0,Math.Min(47,(int)((x-s.Bounds.X)/s.Bounds.Width*48)));
                    int cy=Math.Max(0,Math.Min(26,(int)((y-s.Bounds.Y)/s.Bounds.Height*27)));
                    cells.Add(cy*48+cx);
                }
                if(anchor==null || p.Break || Math.Pow(p.X-anchor.X,2)+Math.Pow(p.Y-anchor.Y,2)>24*24) {anchor=p;dwell=0;}
                dwell+=p.Dt;result.LongestDwell=Math.Max(result.LongestDwell,dwell);previous=p;
            }
            result.Coverage=cells.Count/1296.0*100;return result;
        }
    }
    internal sealed class HeatField : IDisposable {
        internal int Width,Height,Count;
        private readonly Rectangle bounds;
        private readonly float[] values;
        private readonly List<Kernel> kernel=new List<Kernel>();
        private MouseSample previous;
        private struct Kernel {internal int X,Y;internal float Weight;}
        internal HeatField(Rectangle area) {
            bounds=area;double scale=Math.Min(420.0/area.Width,280.0/area.Height);
            Width=Math.Max(1,(int)Math.Round(area.Width*scale));Height=Math.Max(1,(int)Math.Round(area.Height*scale));values=new float[Width*Height];
            int r=Math.Max(3,(int)Math.Round(48*scale));
            for(int y=-r;y<=r;y++)for(int x=-r;x<=r;x++) {
                double d=(x*x+y*y)/(double)(r*r);
                if(d<=1)kernel.Add(new Kernel {X=x,Y=y,Weight=(float)(Math.Exp(-d*3.7)-Math.Exp(-3.7))});
            }
        }
        internal void Add(MouseSample p) {
            bool connected=previous!=null&&!p.Break;
            double distance=connected?Math.Sqrt(Math.Pow(p.X-previous.X,2)+Math.Pow(p.Y-previous.Y,2)):0;
            int steps=Math.Max(1,(int)Math.Ceiling(distance/10));float strength=(float)(p.Dt*10/steps);
            for(int i=1;i<=steps;i++) {
                double x=connected?previous.X+(p.X-previous.X)*(double)i/steps:p.X;
                double y=connected?previous.Y+(p.Y-previous.Y)*(double)i/steps:p.Y;
                int px=(int)Math.Round((x-bounds.X)/bounds.Width*(Width-1)),py=(int)Math.Round((y-bounds.Y)/bounds.Height*(Height-1));
                foreach(var k in kernel) {int xx=px+k.X,yy=py+k.Y;if(xx>=0&&xx<Width&&yy>=0&&yy<Height)values[yy*Width+xx]+=k.Weight*strength;}
            }
            previous=p;Count++;
        }
        internal Bitmap Bitmap(bool spectrum) {
            Color[] colors=spectrum?new[]{Color.FromArgb(36,48,84),Color.FromArgb(43,93,133),Color.FromArgb(49,159,170),Color.FromArgb(85,197,159),Color.FromArgb(236,193,94),Color.FromArgb(247,104,83)}:new[]{Color.FromArgb(69,53,38),Color.FromArgb(126,83,47),Color.FromArgb(206,137,65),Color.FromArgb(244,176,89),Color.FromArgb(237,100,68),Color.FromArgb(255,222,161)};
            var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
            BitmapData data=bitmap.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            var bytes=new byte[data.Stride*Height];
            for(int y=0;y<Height;y++)for(int x=0;x<Width;x++) {
                float density=values[y*Width+x];if(density<.004)continue;
                double intensity=1-Math.Exp(-density*.34),position=Math.Min(4.999,intensity*5);int lo=(int)position;double f=position-lo;
                int offset=y*data.Stride+x*4;
                bytes[offset]=(byte)(colors[lo].B*(1-f)+colors[lo+1].B*f);bytes[offset+1]=(byte)(colors[lo].G*(1-f)+colors[lo+1].G*f);bytes[offset+2]=(byte)(colors[lo].R*(1-f)+colors[lo+1].R*f);bytes[offset+3]=(byte)Math.Min(242,Math.Pow(intensity,.55)*285);
            }
            Marshal.Copy(bytes,0,data.Scan0,bytes.Length);bitmap.UnlockBits(data);return bitmap;
        }
        internal double Peak {get{return values.Max();}}
        public void Dispose() { }
    }
    internal static class Theme {
        internal static readonly Color Background=Color.FromArgb(11,14,18),Panel=Color.FromArgb(20,26,33),Text=Color.FromArgb(233,232,225),Muted=Color.FromArgb(135,151,163),Accent=Color.FromArgb(232,180,126),Line=Color.FromArgb(40,51,61);
        internal static Font Font(float size) {return new Font("Microsoft YaHei UI",size,FontStyle.Regular,GraphicsUnit.Pixel);}
        internal static void TextAt(Graphics g,string text,float size,Color color,float x,float y) {using(var font=Font(size))using(var brush=new SolidBrush(color))g.DrawString(text,font,brush,x,y);}
        internal static Icon MakeIcon() {
            using(var bitmap=new Bitmap(32,32))using(var g=Graphics.FromImage(bitmap)) {
                g.Clear(Background);g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var pen=new Pen(Accent,1.7f)){g.DrawEllipse(pen,7,7,18,18);g.DrawEllipse(pen,13,13,6,6);g.DrawLine(pen,16,2,16,8);g.DrawLine(pen,16,24,16,30);g.DrawLine(pen,2,16,8,16);g.DrawLine(pen,24,16,30,16);}
                IntPtr handle=bitmap.GetHicon();try{return (Icon)Icon.FromHandle(handle).Clone();}finally{Native.DestroyIcon(handle);}
            }
        }
    }
    internal sealed class MapView : Control {
        internal Session Session;internal bool ShowPath=true,Spectrum,ShowHeat=true,ShowClicks=true;
        internal int Until=int.MaxValue;
        private HeatField field;private Bitmap heat;private Rectangle fieldBounds;private string sessionId;private bool lastSpectrum;
        internal MapView() {SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);BackColor=Theme.Background;}
        internal void UpdateMap(Session s,int until,bool path,bool spectrum,bool showHeat=true,bool showClicks=true) {
            Session=s;Until=until;ShowPath=path;Spectrum=spectrum;ShowHeat=showHeat;ShowClicks=showClicks;
            if(s==null){ClearField();Invalidate();return;}
            int target=0;while(target<s.Samples.Count&&s.Samples[target].Ms<=until)target++;
            if(field==null||sessionId!=s.Id||fieldBounds!=s.Bounds.Rectangle||target<field.Count) {
                ClearField();field=new HeatField(s.Bounds.Rectangle);fieldBounds=s.Bounds.Rectangle;sessionId=s.Id;
            }
            bool changed=target!=field.Count||spectrum!=lastSpectrum||heat==null;
            for(int i=field.Count;i<target;i++)field.Add(s.Samples[i]);
            if(changed){if(heat!=null)heat.Dispose();heat=field.Bitmap(spectrum);lastSpectrum=spectrum;}
            Invalidate();
        }
        private void ClearField(){if(field!=null)field.Dispose();if(heat!=null)heat.Dispose();field=null;heat=null;sessionId=null;}
        protected override void OnPaint(PaintEventArgs e) {base.OnPaint(e);Draw(e.Graphics,ClientRectangle,Session,heat,ShowPath,Until,ShowHeat,ShowClicks,Spectrum);}
        internal static void Draw(Graphics g,Rectangle area,Session session,Bitmap image,bool path,int until,bool showHeat=true,bool showClicks=true,bool spectrum=false) {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Theme.Background);
            using(var brush=new SolidBrush(Color.FromArgb(31,42,50)))for(int x=20;x<area.Width;x+=25)for(int y=20;y<area.Height;y+=25)g.FillEllipse(brush,area.X+x,area.Y+y,1,1);
            using(var pen=new Pen(Theme.Line))g.DrawRectangle(pen,area.X,area.Y,Math.Max(0,area.Width-1),Math.Max(0,area.Height-1));
            Theme.TextAt(g,"SCREEN SPACE  /  全屏坐标地图",10,Theme.Muted,area.X+18,area.Y+15);
            if(session==null) {
                Theme.TextAt(g,"让你的日常，留下形状。",31,Theme.Text,area.X+50,area.Y+area.Height*.33f);
                Theme.TextAt(g,"开始记录，然后照常使用电脑。",14,Theme.Muted,area.X+53,area.Y+area.Height*.33f+68);
                Theme.TextAt(g,"浏览网页、打开软件、最小化窗口，都不会打断记录。",12,Theme.Muted,area.X+53,area.Y+area.Height*.33f+102);
                return;
            }
            Rectangle b=session.Bounds.Rectangle;double scale=Math.Max(.001,Math.Min((area.Width-48.0)/b.Width,(area.Height-126.0)/b.Height));
            var fit=new RectangleF((float)(area.X+(area.Width-b.Width*scale)/2),(float)(area.Y+43+(area.Height-126-b.Height*scale)/2),(float)(b.Width*scale),(float)(b.Height*scale));
            using(var brush=new SolidBrush(Color.FromArgb(15,21,28)))g.FillRectangle(brush,fit);
            if(image!=null&&showHeat) {
                g.InterpolationMode=InterpolationMode.HighQualityBilinear;
                using(var attributes=new ImageAttributes()) {
                    var matrix=new ColorMatrix();matrix.Matrix33=path ? .24f : 1f;attributes.SetColorMatrix(matrix);
                    g.DrawImage(image,Rectangle.Round(fit),0,0,image.Width,image.Height,GraphicsUnit.Pixel,attributes);
                }
            }
            using(var pen=new Pen(Color.FromArgb(77,99,115),1))foreach(var screen in session.Screens) {
                var monitor=new RectangleF((float)(fit.X+(screen.X-b.X)*scale),(float)(fit.Y+(screen.Y-b.Y)*scale),(float)(screen.Width*scale),(float)(screen.Height*scale));
                g.DrawRectangle(pen,monitor.X,monitor.Y,monitor.Width,monitor.Height);
                Theme.TextAt(g,screen.Name+"  "+screen.Width+" × "+screen.Height,9,Color.FromArgb(100,124,140),monitor.X+9,monitor.Y+9);
            }
            if(path) {
                MouseSample previous=null,first=null,last=null;
                var pens=TrailStyle.Colors.Select(c=>new Pen(Color.FromArgb(172,c),.85f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}).ToArray();
                var state=g.Save();g.SetClip(fit,CombineMode.Intersect);
                try {foreach(var p in session.Samples) {
                    if(previous!=null)TrailStyle.Segments(previous,p,until,(a,z,m)=>g.DrawLine(pens[m%pens.Length],(float)(fit.X+(a.X-b.X)*scale),(float)(fit.Y+(a.Y-b.Y)*scale),(float)(fit.X+(z.X-b.X)*scale),(float)(fit.Y+(z.Y-b.Y)*scale)));
                    if(p.Ms>until)break;
                    if(first==null)first=p;last=p;previous=p;
                }}finally{foreach(var pen in pens)pen.Dispose();g.Restore(state);}
                if(first!=null)using(var brush=new SolidBrush(TrailStyle.ColorAt(first.Ms)))g.FillEllipse(brush,(float)(fit.X+(first.X-b.X)*scale)-2,(float)(fit.Y+(first.Y-b.Y)*scale)-2,4,4);
                if(last!=null)using(var pen=new Pen(TrailStyle.ColorAt(Math.Max(0,last.Ms-1))))g.DrawEllipse(pen,(float)(fit.X+(last.X-b.X)*scale)-4,(float)(fit.Y+(last.Y-b.Y)*scale)-4,8,8);
            }
            if(showHeat)DwellStyle.Draw(g,fit,b,scale,session.Samples,until,spectrum);
            if(showClicks) {
                var state=g.Save();g.SetClip(fit,CombineMode.Intersect);
                foreach(var c in TrailStyle.Clusters(session.Clicks,until).OrderBy(c=>c.Count)) {
                    float x=(float)(fit.X+(c.X-b.X)*scale),y=(float)(fit.Y+(c.Y-b.Y)*scale),r=c.Count==1?2.2f:(float)Math.Min(23,5+Math.Sqrt(c.Count)*2.2);
                    Color color=TrailStyle.ColorAt(c.Ms);
                    if(c.Count>1)using(var halo=new SolidBrush(Color.FromArgb(15,color)))g.FillEllipse(halo,x-r-5,y-r-5,2*r+10,2*r+10);
                    using(var brush=new SolidBrush(Color.FromArgb(c.Count==1?160:232,Theme.Background)))g.FillEllipse(brush,x-r,y-r,2*r,2*r);
                    using(var pen=new Pen(Color.FromArgb(c.Count==1?125:220,color),1))g.DrawEllipse(pen,x-r,y-r,2*r,2*r);
                    if(c.Count>1) {
                        string label=c.Count>99?"99+":c.Count.ToString();using(var font=Theme.Font(9))using(var brush=new SolidBrush(color))using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})g.DrawString(label,font,brush,new RectangleF(x-r,y-r,2*r,2*r),format);
                    }
                }
                g.Restore(state);
            }
            if(path)TrailStyle.Legend(g,area,Math.Min(until,session.ElapsedMs),showHeat,showClicks);
            else Theme.TextAt(g,DwellStyle.Legend(showHeat,showClicks),9,Theme.Muted,area.X+18,area.Bottom-26);
        }
        internal static Bitmap Export(Session s,bool path,bool spectrum,bool showHeat=true,bool showClicks=true) {
            int width=2000,height=Math.Max(750,Math.Min(1800,(int)(2000.0*s.Bounds.Height/s.Bounds.Width)+150));
            var output=new Bitmap(width,height);using(var g=Graphics.FromImage(output)) {
                using(var f=new HeatField(s.Bounds.Rectangle)) {
                    foreach(var p in s.Samples)f.Add(p);
                    using(var image=f.Bitmap(spectrum))Draw(g,new Rectangle(0,0,width,height-90),s,image,path,int.MaxValue,showHeat,showClicks,spectrum);
                }
                using(var brush=new SolidBrush(Theme.Background))g.FillRectangle(brush,0,height-90,width,90);
                Statistics stats=Statistics.Calculate(s,int.MaxValue);
                Theme.TextAt(g,"注意力地图 / WINDOWS 全局版",21,Theme.Accent,27,height-77);
                Theme.TextAt(g,Clock(s.ElapsedMs)+" · "+s.EndReason+"    距离 "+Math.Round(stats.Distance).ToString("N0")+" px    探索 "+stats.Coverage.ToString("F1")+"%    最长停留 "+stats.LongestDwell.ToString("F1")+" s",13,Theme.Muted,29,height-42);
                Theme.TextAt(g,"鼠标行为 ≠ 真实视线 · 本地记录",11,Theme.Muted,width-260,height-42);
            }
            return output;
        }
        internal static string Clock(int ms){int seconds=Math.Max(0,(int)Math.Ceiling(ms/1000.0));return (seconds/60).ToString("00")+":"+(seconds%60).ToString("00");}
        protected override void Dispose(bool disposing){if(disposing)ClearField();base.Dispose(disposing);}
    }
    internal sealed class RingTimer : Control {
        internal double Progress;internal string Time="05:00",Caption="记录时长";
        internal RingTimer(){DoubleBuffered=true;BackColor=Theme.Panel;}
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            float size=Math.Min(Width,Height)-20,x=(Width-size)/2,y=4;
            using(var pen=new Pen(Theme.Line,1))g.DrawEllipse(pen,x,y,size,size);
            if(Progress>0)using(var pen=new Pen(Theme.Accent,2))g.DrawArc(pen,x,y,size,size,-90,(float)Math.Min(360,Progress*360));
            using(var font=new Font("Segoe UI Light",36,FontStyle.Regular,GraphicsUnit.Pixel))using(var brush=new SolidBrush(Theme.Text)) {
                SizeF text=g.MeasureString(Time,font);g.DrawString(Time,font,brush,(Width-text.Width)/2,y+size*.35f);
            }
            using(var font=Theme.Font(10))using(var brush=new SolidBrush(Theme.Muted)){SizeF text=g.MeasureString(Caption,font);g.DrawString(Caption,font,brush,(Width-text.Width)/2,y+size*.65f);}
        }
    }
    internal sealed class SessionStore {
        internal readonly string DirectoryPath;
        private readonly object saveGate=new object();
        internal SessionStore(string path){DirectoryPath=path;Directory.CreateDirectory(path);}
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=50000000,RecursionLimit=40};}
        internal string Save(Session session,bool final) {
            lock(saveGate) {
                string file=Path.Combine(DirectoryPath,final?session.Id+".json":"最近记录-恢复用.json");
                string text=Serializer().Serialize(session);AtomicWrite(file,text);return file;
            }
        }
        internal static void AtomicWrite(string path,string content) {
            string tmp=path+".tmp";File.WriteAllText(tmp,content,new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
        }
        internal Session Load(string file) {
            if(new FileInfo(file).Length>50000000)throw new InvalidDataException("记录文件过大");
            Session s=Serializer().Deserialize<Session>(File.ReadAllText(file,Encoding.UTF8));Validate(s);return s;
        }
        internal static void Validate(Session s) {
            if(s==null||(s.Version!=2&&s.Version!=3)||String.IsNullOrWhiteSpace(s.Id)||s.Id.Length>100||s.Bounds==null||s.Bounds.Width<=0||s.Bounds.Height<=0||s.Bounds.Width>65536||s.Bounds.Height>65536||s.Samples==null||s.Samples.Count>100000||s.Screens==null||s.Screens.Count>32||s.DurationMs<100||s.DurationMs>3600000||s.ElapsedMs<0||s.ElapsedMs>s.DurationMs||(s.Clicks==null&&s.Version==3))throw new InvalidDataException("文件不是有效的 Windows 注意力地图记录");
            int previous=-1;foreach(var p in s.Samples){if(p==null||p.Ms<previous||p.Ms<0||p.Ms>s.DurationMs||Double.IsNaN(p.Dt)||Double.IsInfinity(p.Dt)||p.Dt<0||p.Dt>.201||!s.Bounds.Rectangle.Contains(p.X,p.Y))throw new InvalidDataException("轨迹坐标或时间有误");previous=p.Ms;}
            foreach(var screen in s.Screens)if(screen==null||screen.Width<=0||screen.Height<=0)throw new InvalidDataException("显示器数据有误");
            if(s.Clicks==null)s.Clicks=new List<MouseClick>();
            if(s.Clicks.Count>100000)throw new InvalidDataException("点击记录过多");
            previous=-1;foreach(var p in s.Clicks){if(p==null||p.Ms<previous||p.Ms<0||p.Ms>s.ElapsedMs||p.Button<0||p.Button>2||!s.Bounds.Rectangle.Contains(p.X,p.Y))throw new InvalidDataException("点击坐标或时间有误");previous=p.Ms;}
        }
        internal static void Csv(Session s,string path) {
            var rows=new List<KeyValuePair<int,string>>();
            foreach(var p in s.Samples)rows.Add(new KeyValuePair<int,string>(p.Ms,p.Ms+","+p.X+","+p.Y+","+p.Dt.ToString("F3",CultureInfo.InvariantCulture)+","+(p.Break?"1":"0")+",move,"));
            foreach(var p in s.Clicks??new List<MouseClick>())rows.Add(new KeyValuePair<int,string>(p.Ms,p.Ms+","+p.X+","+p.Y+",0,0,click,"+p.Button));
            var text=new StringBuilder("elapsed_ms,screen_x_px,screen_y_px,dwell_seconds,segment_start,event_type,button\r\n");
            foreach(var row in rows.OrderBy(r=>r.Key))text.Append(row.Value).Append("\r\n");
            File.WriteAllText(path,text.ToString(),new UTF8Encoding(true));
        }
    }
    internal sealed class MainForm : Form {
        private readonly CursorCapture capture=new CursorCapture();
        private readonly SessionStore store;
        private readonly MapView map=new MapView();
        private readonly Panel card=new Panel();private readonly RingTimer ring=new RingTimer();
        private readonly Label title=new Label(),subtitle=new Label(),status=new Label(),cardTitle=new Label(),durationLabel=new Label(),note=new Label(),stats=new Label(),footnote=new Label(),replayLabel=new Label();
        private readonly NumericUpDown minutes=new NumericUpDown();
        private readonly Button start=new Button(),stop=new Button(),minimize=new Button(),export=new Button(),csv=new Button(),history=new Button(),folder=new Button(),replayPlay=new Button(),full=new Button();
        private readonly CheckBox pathToggle=new CheckBox(),paletteToggle=new CheckBox(),heatToggle=new CheckBox(),clickToggle=new CheckBox();
        private readonly TrackBar replay=new TrackBar();
        private readonly NotifyIcon tray=new NotifyIcon();private readonly ToolStripMenuItem trayStop=new ToolStripMenuItem("停止并保存"),trayStart=new ToolStripMenuItem("开始一次记录");
        private readonly System.Windows.Forms.Timer uiTimer=new System.Windows.Forms.Timer();
        private Session shown;private bool exitAllowed,settingReplay,replaying,hotkeyRegistered,fullScreen;private int seekMs=int.MaxValue,lastSavedMs;private DateTime lastReplay;
        private FormBorderStyle oldBorder;private Rectangle oldBounds;private bool saveBusy;private string lastError;
        internal MainForm(string directory) {
            store=new SessionStore(directory);
            Text="注意力地图 · Windows 全局版";BackColor=Theme.Background;ForeColor=Theme.Text;Icon=Theme.MakeIcon();
            ClientSize=new Size(1200,790);MinimumSize=new Size(1000,720);StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.None;Font=Theme.Font(12);DoubleBuffered=true;
            LabelStyle(title,"注意力地图",24,Theme.Text);LabelStyle(subtitle,"ATTENTION ATLAS  /  WINDOWS GLOBAL",9,Theme.Muted);
            LabelStyle(status,"准备好了，开始记录你的日常。",12,Theme.Muted);LabelStyle(cardTitle,"全屏记录 · 后台持续运行",12,Theme.Text);
            LabelStyle(durationLabel,"本次记录时长（分钟）",11,Theme.Muted);LabelStyle(note,"开始后可以最小化，照常使用电脑。\n时间到或手动停止，才会结束。",11,Theme.Muted);
            LabelStyle(stats,"游走距离   0 px          探索面积   0.0%          最长停留   0.0 s",13,Theme.Muted);
            LabelStyle(footnote,"坐标、点击和时间留在本机 · 不截屏、不读取键盘",10,Theme.Muted);
            LabelStyle(replayLabel,"回看轨迹",10,Theme.Muted);
            card.BackColor=Theme.Panel;card.Paint+=(s,e)=>{using(var p=new Pen(Theme.Line))e.Graphics.DrawRectangle(p,0,0,card.Width-1,card.Height-1);};
            StyleButton(start,"开始记录",true);StyleButton(stop,"停止并保存",false);StyleButton(minimize,"最小化到托盘",false);StyleButton(export,"导出 PNG 图片",false);StyleButton(csv,"导出 CSV 坐标",false);StyleButton(history,"打开历史记录",false);StyleButton(folder,"打开记录文件夹",false);StyleButton(replayPlay,"▶ 回放",false);StyleButton(full,"全屏地图",false);
            minutes.Minimum=1;minutes.Maximum=60;minutes.Value=5;minutes.BackColor=Theme.Background;minutes.ForeColor=Theme.Text;minutes.BorderStyle=BorderStyle.FixedSingle;minutes.Font=Theme.Font(13);
            pathToggle.Text="显示路径";pathToggle.Checked=true;pathToggle.ForeColor=Theme.Muted;pathToggle.AutoSize=true;
            heatToggle.Text="热力底图";heatToggle.Checked=true;heatToggle.ForeColor=Theme.Muted;heatToggle.AutoSize=true;
            clickToggle.Text="点击标记";clickToggle.Checked=true;clickToggle.ForeColor=Theme.Muted;clickToggle.AutoSize=true;
            paletteToggle.Text="光谱配色";paletteToggle.ForeColor=Theme.Muted;paletteToggle.AutoSize=true;
            replay.Minimum=0;replay.Maximum=300000;replay.TickStyle=TickStyle.None;replay.BackColor=Theme.Background;replay.Enabled=false;replay.AutoSize=false;
            card.Controls.AddRange(new Control[]{cardTitle,durationLabel,minutes,ring,start,stop,minimize,note,export,csv,history,folder});
            Controls.AddRange(new Control[]{title,subtitle,status,card,map,stats,footnote,pathToggle,paletteToggle,heatToggle,clickToggle,replay,replayLabel,replayPlay,full});
            start.Click+=(s,e)=>StartRecording();stop.Click+=(s,e)=>StopRecording("手动停止");minimize.Click+=(s,e)=>MinimizeToTray();
            export.Click+=(s,e)=>ExportPng();csv.Click+=(s,e)=>ExportCsv();history.Click+=(s,e)=>OpenHistory();folder.Click+=(s,e)=>OpenFolder();
            pathToggle.CheckedChanged+=(s,e)=>RefreshMap();paletteToggle.CheckedChanged+=(s,e)=>RefreshMap();
            heatToggle.CheckedChanged+=(s,e)=>RefreshMap();clickToggle.CheckedChanged+=(s,e)=>RefreshMap();
            replay.Scroll+=(s,e)=>{if(settingReplay)return;replaying=false;replayPlay.Text="▶ 回放";seekMs=replay.Value;RefreshMap();};
            replayPlay.Click+=(s,e)=>{if(shown==null||capture.Running)return;replaying=!replaying;if(replaying){if(replay.Value>=replay.Maximum){seekMs=0;replay.Value=0;}else seekMs=replay.Value;lastReplay=DateTime.UtcNow;}replayPlay.Text=replaying?"Ⅱ 暂停":"▶ 回放";};
            full.Click+=(s,e)=>ToggleFullScreen();
            var menu=new ContextMenuStrip();menu.Items.Add("打开地图",null,(s,e)=>RestoreWindow());menu.Items.Add(trayStart);menu.Items.Add(trayStop);menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出程序",null,(s,e)=>ExitProgram());
            trayStart.Click+=(s,e)=>StartRecording();trayStop.Click+=(s,e)=>StopRecording("手动停止");tray.Icon=Icon;tray.Text="注意力地图 · 未开始";tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>RestoreWindow();tray.Visible=true;
            capture.Finished+=OnFinished;uiTimer.Interval=250;uiTimer.Tick+=(s,e)=>TickUI();uiTimer.Start();
            Resize+=(s,e)=>{LayoutControls();if(WindowState==FormWindowState.Minimized)Hide();};
            FormClosing+=ClosingForm;Shown+=(s,e)=>{hotkeyRegistered=Native.RegisterHotKey(Handle,1101,0x4003,0x77);UpdateControls();};
            KeyPreview=true;KeyDown+=(s,e)=>{if(e.KeyCode==Keys.F11)ToggleFullScreen();};
            string recent=Path.Combine(store.DirectoryPath,"最近记录-恢复用.json");
            if(File.Exists(recent))try{shown=store.Load(recent);status.Text="上次的轨迹已保留 · 点击开始可进行新记录";}catch(Exception){status.Text="上次的恢复文件无法读取，仍可开始新记录。";}
            LayoutControls();UpdateControls();RefreshMap();
        }
        private static void LabelStyle(Label label,string text,float size,Color color){label.Text=text;label.Font=Theme.Font(size);label.ForeColor=color;label.AutoSize=false;label.BackColor=Color.Transparent;}
        private static void StyleButton(Button button,string text,bool primary) {
            button.Text=text;button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=primary?Theme.Accent:Theme.Line;button.FlatAppearance.BorderSize=1;button.BackColor=primary?Theme.Accent:Theme.Panel;button.ForeColor=primary?Color.FromArgb(43,31,22):Theme.Text;button.Font=Theme.Font(12);button.Cursor=Cursors.Hand;button.UseVisualStyleBackColor=false;
            button.Paint+=(s,e)=>{if(!button.Enabled){using(var brush=new SolidBrush(button.BackColor))e.Graphics.FillRectangle(brush,2,2,button.Width-4,button.Height-4);TextRenderer.DrawText(e.Graphics,button.Text,button.Font,button.ClientRectangle,Color.FromArgb(86,106,119),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}};
        }
        private void LayoutControls() {
            int w=ClientSize.Width,h=ClientSize.Height,cardX=w-296,canvasHeight=h-246;
            title.SetBounds(32,22,400,35);subtitle.SetBounds(34,64,480,20);status.SetBounds(Math.Max(460,w-580),34,420,35);full.SetBounds(w-135,27,103,35);
            map.SetBounds(32,107,w-348,canvasHeight);card.SetBounds(cardX,107,264,h-167);
            cardTitle.SetBounds(20,18,224,27);durationLabel.SetBounds(20,55,165,20);minutes.SetBounds(184,51,60,27);
            ring.SetBounds(37,86,190,185);start.SetBounds(20,285,224,39);stop.SetBounds(20,334,224,36);minimize.SetBounds(20,380,224,34);
            note.SetBounds(20,428,224,44);export.SetBounds(20,484,224,32);csv.SetBounds(20,524,224,32);history.SetBounds(20,564,108,32);folder.SetBounds(136,564,108,32);
            // Compact the side card on shorter displays without changing the global coordinate system.
            if(card.Height<610){ring.SetBounds(52,83,160,156);start.Top=244;stop.Top=291;minimize.Top=334;note.Top=378;export.Top=432;csv.Top=472;history.Top=512;folder.Top=512;}
            replayLabel.SetBounds(35,h-122,70,22);replayPlay.SetBounds(106,h-129,82,30);replay.SetBounds(200,h-124,w-548,26);
            stats.SetBounds(34,h-82,w-370,26);pathToggle.Location=new Point(34,h-46);heatToggle.Location=new Point(137,h-46);clickToggle.Location=new Point(240,h-46);paletteToggle.Location=new Point(343,h-46);footnote.SetBounds(456,h-44,w-480,27);
        }
        private void StartRecording() {
            if(capture.Running)return;
            try {
                replaying=false;seekMs=int.MaxValue;lastSavedMs=0;lastError=null;
                Rectangle bounds=SystemInformation.VirtualScreen;
                var screens=Screen.AllScreens.Select((screen,i)=>ScreenArea.From(screen.Bounds,"屏幕 "+(i+1))).ToList();
                capture.Start((int)minutes.Value*60000,bounds,screens);shown=capture.Snapshot().Session;
                status.Text="正在全局记录 · 最小化后继续";UpdateControls();RefreshMap();
            }catch(Exception e){ShowError("无法开始记录",e);}
        }
        private void StopRecording(string reason){capture.Stop(reason);}
        private void OnFinished(Session session) {
            if(IsDisposed)return;
            if(InvokeRequired){try{BeginInvoke(new Action<Session>(OnFinished),session);}catch(InvalidOperationException){}return;}
            shown=session;seekMs=int.MaxValue;replaying=false;
            string saveMessage="记录已保留在内存，可导出";
            try {
                store.Save(session,true);store.Save(session,false);
                using(var png=MapView.Export(session,pathToggle.Checked,paletteToggle.Checked,heatToggle.Checked,clickToggle.Checked))png.Save(Path.Combine(store.DirectoryPath,session.Id+".png"),ImageFormat.Png);
                saveMessage="JSON 与 PNG 已保存到记录文件夹";
            }catch(Exception e){lastError=e.Message;saveMessage="自动保存失败，请点导出另存";}
            status.Text=(session.EndReason=="时间到"?"时间到了 · ":"已停止 · ")+saveMessage;
            tray.Text="注意力地图 · 已停止";tray.BalloonTipTitle="注意力地图 · "+session.EndReason;tray.BalloonTipText=saveMessage;tray.ShowBalloonTip(4000);
            UpdateControls();RefreshMap();
        }
        private void TickUI() {
            var snapshot=capture.Snapshot();
            if(snapshot.Running) {
                shown=snapshot.Session;seekMs=int.MaxValue;int remaining=shown.DurationMs-snapshot.ElapsedMs;
                ring.Time=MapView.Clock(remaining);ring.Caption="后台全局记录中";ring.Progress=snapshot.ElapsedMs/(double)shown.DurationMs;
                tray.Text="注意力地图 · 正在记录 · "+MapView.Clock(remaining);ring.Invalidate();
                if(snapshot.ElapsedMs-lastSavedMs>=10000&&!saveBusy) {
                    lastSavedMs=snapshot.ElapsedMs;saveBusy=true;Session copy=shown;
                    ThreadPool.QueueUserWorkItem(s=>{try{store.Save(copy,false);}catch(Exception e){lastError=e.Message;}finally{saveBusy=false;}});
                }
            } else if(shown!=null) {ring.Time=MapView.Clock(shown.ElapsedMs);ring.Caption="已记录的时间";ring.Progress=shown.ElapsedMs/(double)shown.DurationMs;ring.Invalidate();}
            else {ring.Time=((int)minutes.Value).ToString("00")+":00";ring.Progress=0;ring.Invalidate();}
            if(replaying&&shown!=null&&!snapshot.Running) {
                int delta=(int)((DateTime.UtcNow-lastReplay).TotalMilliseconds*10);lastReplay=DateTime.UtcNow;seekMs=Math.Min(shown.ElapsedMs,seekMs+delta);
                settingReplay=true;replay.Value=Math.Min(replay.Maximum,seekMs);settingReplay=false;
                if(seekMs>=shown.ElapsedMs){replaying=false;replayPlay.Text="▶ 回放";seekMs=int.MaxValue;}
            }
            if(Visible&&WindowState!=FormWindowState.Minimized)RefreshMap();
        }
        private void UpdateControls() {
            bool running=capture.Running,has=shown!=null&&shown.Samples.Count>0;
            start.Enabled=!running;start.Text=shown==null?"开始记录":"开始新的一次";stop.Enabled=running;minutes.Enabled=!running;
            export.Enabled=has;csv.Enabled=has;history.Enabled=!running;replay.Enabled=has&&!running;replayPlay.Enabled=has&&!running;trayStop.Enabled=running;trayStart.Enabled=!running;
            note.Text=running?"已开始全局记录，切换软件不会暂停。\n"+(hotkeyRegistered?"Ctrl + Alt + F8 可随时停止。":"可从托盘菜单停止并保存。") : "开始后可以最小化，照常使用电脑。\n时间到或手动停止，才会结束。";
        }
        private void RefreshMap() {
            map.UpdateMap(shown,seekMs,pathToggle.Checked,paletteToggle.Checked,heatToggle.Checked,clickToggle.Checked);
            if(shown!=null) {
                Statistics s=Statistics.Calculate(shown,int.MaxValue);
                stats.Text="游走距离   "+Math.Round(s.Distance).ToString("N0")+" px       探索面积   "+s.Coverage.ToString("F1")+"%       最长停留   "+s.LongestDwell.ToString("F1")+" s";
                settingReplay=true;replay.Maximum=Math.Max(1,shown.ElapsedMs);if(seekMs==int.MaxValue)replay.Value=replay.Maximum;settingReplay=false;
                replayLabel.Text="回看 "+MapView.Clock(seekMs==int.MaxValue?shown.ElapsedMs:seekMs);
            }
            UpdateControls();
        }
        private void MinimizeToTray(){WindowState=FormWindowState.Minimized;Hide();tray.BalloonTipTitle="注意力地图";tray.BalloonTipText=capture.Running?"正在后台记录整个屏幕。双击图标可打开，右键可停止。":"程序已收起到托盘。双击图标重新打开。";tray.ShowBalloonTip(2500);}
        private void RestoreWindow(){Show();WindowState=FormWindowState.Normal;Activate();RefreshMap();}
        private void ClosingForm(object sender,FormClosingEventArgs e) {
            if(exitAllowed||e.CloseReason==CloseReason.WindowsShutDown){if(capture.Running)capture.Stop("退出程序");return;}
            e.Cancel=true;MinimizeToTray();
        }
        private void ExitProgram(){if(capture.Running)capture.Stop("退出程序");exitAllowed=true;Close();}
        private void ExportPng(){if(shown==null)return;using(var dialog=new SaveFileDialog{Filter="PNG 图片|*.png",FileName=shown.Id+".png",InitialDirectory=store.DirectoryPath})if(dialog.ShowDialog(this)==DialogResult.OK)try{using(var image=MapView.Export(shown,pathToggle.Checked,paletteToggle.Checked,heatToggle.Checked,clickToggle.Checked))image.Save(dialog.FileName,ImageFormat.Png);status.Text="图片已保存："+Path.GetFileName(dialog.FileName);}catch(Exception e){ShowError("图片导出失败",e);}}
        private void ExportCsv(){if(shown==null)return;using(var dialog=new SaveFileDialog{Filter="CSV 坐标|*.csv",FileName=shown.Id+".csv",InitialDirectory=store.DirectoryPath})if(dialog.ShowDialog(this)==DialogResult.OK)try{SessionStore.Csv(shown,dialog.FileName);status.Text="坐标已导出："+Path.GetFileName(dialog.FileName);}catch(Exception e){ShowError("坐标导出失败",e);}}
        private void OpenHistory(){using(var dialog=new OpenFileDialog{Filter="Windows 注意力地图记录|*.json",InitialDirectory=store.DirectoryPath})if(dialog.ShowDialog(this)==DialogResult.OK)try{shown=store.Load(dialog.FileName);seekMs=int.MaxValue;replaying=false;status.Text="正在查看："+Path.GetFileName(dialog.FileName);RefreshMap();}catch(Exception e){ShowError("无法打开记录",e);}}
        private void OpenFolder(){try{Process.Start(new ProcessStartInfo{FileName=store.DirectoryPath,UseShellExecute=true});}catch(Exception e){ShowError("无法打开文件夹",e);}}
        private void ToggleFullScreen(){if(!fullScreen){oldBounds=Bounds;oldBorder=FormBorderStyle;FormBorderStyle=FormBorderStyle.None;Bounds=Screen.FromControl(this).Bounds;fullScreen=true;full.Text="退出全屏";}else{FormBorderStyle=oldBorder;Bounds=oldBounds;fullScreen=false;full.Text="全屏地图";}LayoutControls();}
        private void ShowError(string message,Exception error){status.Text=message;MessageBox.Show(this,message+"\n"+error.Message,"注意力地图",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        protected override void WndProc(ref Message m){if(m.Msg==0x0312&&m.WParam.ToInt32()==1101){StopRecording("快捷键停止");return;}base.WndProc(ref m);}
        protected override void Dispose(bool disposing){if(disposing){uiTimer.Stop();uiTimer.Dispose();if(hotkeyRegistered)Native.UnregisterHotKey(Handle,1101);capture.Dispose();tray.Visible=false;tray.Dispose();if(Icon!=null)Icon.Dispose();}base.Dispose(disposing);}
    }
    internal static class Program {
        [STAThread] internal static int Main(string[] args) {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>=2&&args[0]=="--make-icon"){using(var icon=Theme.MakeIcon())using(var stream=File.Create(args[1]))icon.Save(stream);return 0;}
            if(args.Length>=2&&args[0]=="--self-test"){Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);return SelfTest.Run(args[1]);}
            bool first;
            using(var instance=new Mutex(true,"Local\\AttentionAtlasDesktop-v02",out first)) {
            if(!first){MessageBox.Show("注意力地图已经在运行。\n请双击系统托盘里的地图图标打开窗口。","注意力地图",MessageBoxButtons.OK,MessageBoxIcon.Information);return 0;}
            try {
                string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"注意力地图记录");
                Directory.CreateDirectory(folder);
                using(var window=new MainForm(folder))Application.Run(window);
                return 0;
            }catch(Exception e){MessageBox.Show("无法启动注意力地图：\n"+e.Message,"注意力地图",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
            }
        }
    }
    internal static class SelfTest {
        private static void Require(bool value,string message){if(!value)throw new Exception(message);}
        private static Session VisualSession() {
            var s=new Session {Version=3,Id="visual-test",StartedUtc=DateTime.UtcNow.ToString("o"),EndReason="模拟轨迹 · 样式预览",DurationMs=300000,ElapsedMs=300000,Completed=true,Bounds=ScreenArea.From(new Rectangle(0,0,1920,1080),"全屏"),Screens=new List<ScreenArea>{ScreenArea.From(new Rectangle(0,0,1920,1080),"屏幕 1")},Samples=new List<MouseSample>()};
            Func<int,PointF> anchor=i=>i%4==0?new PointF(710,390):i%4==1?new PointF(1280,710):new PointF((float)(240+1400*(Math.Sin(i*2.399)+1)/2),(float)(200+650*(Math.Cos(i*1.71)+1)/2));
            for(int ms=0;ms<=300000;ms+=50) {
                int visit=ms/4200;double f=Math.Min(1,(ms%4200)/1200.0);f=f*f*(3-2*f);PointF a=anchor(visit),b=anchor(visit+1);
                int x=(int)Math.Round(a.X+(b.X-a.X)*f+Math.Sin(f*Math.PI)*Math.Sin(visit)*70),y=(int)Math.Round(a.Y+(b.Y-a.Y)*f+Math.Sin(f*Math.PI)*45);
                s.Samples.Add(new MouseSample {X=x,Y=y,Ms=ms,Dt=.05,Break=ms==0});
                if(ms%4200==1600)s.Clicks.Add(new MouseClick {X=x,Y=y,Ms=ms,Button=0});
                if(ms%4200==2300&&(visit%4==0||visit%4==3))s.Clicks.Add(new MouseClick {X=x+5,Y=y-3,Ms=ms,Button=0});
            }
            return s;
        }
        private static void AddDwell(List<MouseSample> samples,int x,int y,int tenths,ref int elapsed) {
            for(int i=0;i<tenths;i++){elapsed+=100;samples.Add(new MouseSample{X=x,Y=y,Ms=elapsed,Dt=.1,Break=i==0});}
        }
        private static void CheckDwell(string directory,List<string> checks) {
            int elapsed=0;var samples=new List<MouseSample>();
            AddDwell(samples,-750,500,300,ref elapsed);AddDwell(samples,-250,500,1200,ref elapsed);
            AddDwell(samples,250,500,6000,ref elapsed);AddDwell(samples,750,500,18000,ref elapsed);
            var spots=DwellStyle.Measure(samples,int.MaxValue);
            Require(spots.Count==4&&Math.Abs(spots[3].Seconds-1800)<.0001,"长停留时间被截断");
            double unit=DwellStyle.Unit(new RectangleF(0,0,800,400),1800);
            double a=DwellStyle.Radius(spots[0].Seconds,unit),b=DwellStyle.Radius(spots[1].Seconds,unit),c=DwellStyle.Radius(spots[2].Seconds,unit),d=DwellStyle.Radius(spots[3].Seconds,unit);
            Require(a<b&&b<c&&c<d&&Math.Abs(b*b/(a*a)-4)<.001&&Math.Abs(d*d/(a*a)-60)<.001,"停留圆面积比例错误");
            var replay=DwellStyle.Measure(samples,90000);
            Require(replay.Count==2&&Math.Abs(replay[1].Seconds-60)<.0001,"回放包含未来停留");
            var breaks=new List<MouseSample>();elapsed=0;
            AddDwell(breaks,-800,500,5,ref elapsed);AddDwell(breaks,-800,500,5,ref elapsed);
            Require(DwellStyle.Measure(breaks,int.MaxValue).Count==0,"断点被合并为连续停留");
            breaks.Clear();elapsed=0;AddDwell(breaks,-800,500,40,ref elapsed);AddDwell(breaks,-790,500,60,ref elapsed);
            var merged=DwellStyle.Measure(breaks,int.MaxValue);
            Require(merged.Count==1&&Math.Abs(merged[0].Seconds-10)<.0001,"同一区域重复停留未累计");
            checks.Add("30/120/600/1800 second dwell areas stay proportional, including shared rescaling; replay and disconnected visits are correct");
            var session=new Session{Version=3,Id="proportional-dwell-test",StartedUtc=DateTime.UtcNow.ToString("o"),EndReason="停留比例示例",DurationMs=3600000,ElapsedMs=2550000,Completed=true,Bounds=ScreenArea.From(new Rectangle(-1000,0,2000,1000),"全屏"),Screens=new List<ScreenArea>{ScreenArea.From(new Rectangle(-1000,0,2000,1000),"屏幕")},Samples=samples};
            using(var png=MapView.Export(session,true,false,true,false))png.Save(Path.Combine(directory,"proportional-dwell.png"),ImageFormat.Png);
            using(var png=MapView.Export(session,false,true,true,false))png.Save(Path.Combine(directory,"proportional-dwell-spectrum.png"),ImageFormat.Png);
            using(var png=MapView.Export(session,true,false,false,false))png.Save(Path.Combine(directory,"proportional-dwell-hidden.png"),ImageFormat.Png);
            using(var view=new MapView()) {
                view.Size=new Size(850,544);view.UpdateMap(session,int.MaxValue,true,false,true,false);
                using(var png=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(png,view.ClientRectangle);png.Save(Path.Combine(directory,"proportional-dwell-preview.png"),ImageFormat.Png);}
                view.UpdateMap(session,90000,true,false,true,false);
                using(var png=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(png,view.ClientRectangle);png.Save(Path.Combine(directory,"proportional-dwell-replay.png"),ImageFormat.Png);}
            }
            checks.Add("proportional dwell preview, replay, both PNG palettes, and hidden heat layer render");
        }
        internal static int Run(string directory) {
            Directory.CreateDirectory(directory);var checks=new List<string>();string report=Path.Combine(directory,"self-test.json");
            try {
                var samples=new List<MouseSample>{new MouseSample{X=-900,Y=100,Ms=50,Dt=.05,Break=true},new MouseSample{X=-900,Y=100,Ms=100,Dt=.05},new MouseSample{X=-800,Y=100,Ms=150,Dt=.05}};
                var session=new Session{Version=2,Id="test-session",StartedUtc=DateTime.UtcNow.ToString("o"),EndReason="测试",DurationMs=300000,ElapsedMs=150,Completed=true,Bounds=ScreenArea.From(new Rectangle(-1000,0,2000,1000),"全屏"),Screens=new List<ScreenArea>{ScreenArea.From(new Rectangle(-1000,0,1000,1000),"屏幕 1"),ScreenArea.From(new Rectangle(0,0,1000,1000),"屏幕 2")},Samples=samples};
                Statistics stats=Statistics.Calculate(session,int.MaxValue);Require(Math.Abs(stats.Distance-100)<.001&&Math.Abs(stats.LongestDwell-.1)<.001&&stats.Coverage>0,"统计错误");checks.Add("negative multi-monitor coordinates and distance/dwell metrics");
                var boundary=new List<int>();var ends=new List<PointF>();
                TrailStyle.Segments(new MouseSample {X=0,Y=0,Ms=59950},new MouseSample {X=100,Y=0,Ms=60050},60025,(a,b,m)=>{boundary.Add(m);ends.Add(b);});
                Require(boundary.SequenceEqual(new[]{0,1})&&Math.Abs(ends[0].X-50)<.01&&Math.Abs(ends[1].X-75)<.01,"分钟边界或回放插值错误");
                boundary.Clear();TrailStyle.Segments(new MouseSample {X=0,Y=0,Ms=299950},new MouseSample {X=100,Y=0,Ms=300000},int.MaxValue,(a,b,m)=>boundary.Add(m));
                Require(boundary.SequenceEqual(new[]{4})&&TrailStyle.Colors.Distinct().Count()==60,"终点颜色或长记录配色错误");checks.Add("exact minute colors, boundary interpolation and 60 distinct colors");
                var clicks=new List<MouseClick>{new MouseClick{X=-25,Y=10,Ms=10},new MouseClick{X=-23,Y=10,Ms=20},new MouseClick{X=-10,Y=10,Ms=30},new MouseClick{X=200,Y=10,Ms=40}};
                Require(TrailStyle.Clusters(clicks,int.MaxValue).Select(c=>c.Count).SequenceEqual(new[]{3,1})&&TrailStyle.Clusters(clicks,25)[0].Count==2,"点击聚合或回放错误");checks.Add("nearby clicks cluster across negative grid borders; replay excludes future clicks");
                using(var field=new HeatField(session.Bounds.Rectangle)){field.Add(samples[0]);double first=field.Peak;field.Add(samples[1]);Require(field.Peak>first,"热力没有积累");using(var bitmap=field.Bitmap(false))Require(bitmap.GetPixel((int)Math.Round(.05*(field.Width-1)),(int)Math.Round(.1*(field.Height-1))).A>0,"热区不在负坐标对应位置");}checks.Add("heat accumulates at the correct normalized physical position");
                CheckDwell(directory,checks);
                var store=new SessionStore(directory);string file=store.Save(session,true);Session loaded=store.Load(file);Require(loaded.Samples.Count==3&&loaded.Samples[0].X==-900&&loaded.Clicks.Count==0,"旧记录保存读取错误");
                session.Version=3;session.Clicks.Add(new MouseClick{X=-900,Y=100,Ms=110,Button=2});file=store.Save(session,true);loaded=store.Load(file);Require(loaded.Clicks.Count==1&&loaded.Clicks[0].Button==2,"点击保存读取错误");
                loaded.Clicks[0].Ms=1000;bool rejected=false;try{SessionStore.Validate(loaded);}catch(InvalidDataException){rejected=true;}Require(rejected,"没有拒绝未来点击");loaded.Clicks[0].Ms=110;
                SessionStore.Csv(loaded,Path.Combine(directory,"test.csv"));Require(File.ReadAllText(Path.Combine(directory,"test.csv")).Contains(",click,2"),"CSV 丢失点击");using(var png=MapView.Export(loaded,true,false))png.Save(Path.Combine(directory,"test-map.png"),ImageFormat.Png);checks.Add("legacy and new JSON roundtrip, click validation, CSV export, PNG render");
                int afterStop=0,finalCount=0;string reason=null;bool minimized=false;
                using(var capture=new CursorCapture())using(var form=new Form())using(var ticker=new System.Windows.Forms.Timer()) {
                    form.ShowInTaskbar=false;form.Opacity=0;form.WindowState=FormWindowState.Minimized;
                    var clock=Stopwatch.StartNew();int phase=0;
                    capture.Finished+=s=>{reason=s.EndReason;finalCount=s.Samples.Count;};
                    form.Shown+=(s,e)=>{minimized=form.WindowState==FormWindowState.Minimized;capture.Start(5000,SystemInformation.VirtualScreen,Screen.AllScreens.Select((screen,i)=>ScreenArea.From(screen.Bounds,"屏幕 "+i)).ToList());};
                    ticker.Interval=80;ticker.Tick+=(s,e)=>{
                        if(phase==0&&clock.ElapsedMilliseconds>=700){Native.POINT p;Native.GetCursorPos(out p);capture.RecordClick(p.X,p.Y,0);capture.RecordClick(p.X+2,p.Y,2);Require(capture.Snapshot().Session.Clicks.Count>=2,"点击未进入快照");capture.Stop("手动停止");afterStop=capture.Snapshot().Session.Samples.Count;int before=capture.Snapshot().Session.Clicks.Count;capture.RecordClick(p.X,p.Y,0);Require(capture.Snapshot().Session.Clicks.Count==before,"停止后仍记录点击");checks.Add("click capture pipeline and immutable stopped snapshots");phase=1;clock.Restart();}
                        else if(phase==1&&clock.ElapsedMilliseconds>=250){Require(capture.Snapshot().Session.Samples.Count==afterStop,"手动停止后仍新增数据");Require(afterStop>=5&&minimized,"最小化后没有持续采样");checks.Add("real GetCursorPos sampling continues in a minimized form");checks.Add("manual stop freezes samples");reason=null;capture.Start(1200,SystemInformation.VirtualScreen,Screen.AllScreens.Select((screen,i)=>ScreenArea.From(screen.Bounds,"屏幕 "+i)).ToList());phase=2;clock.Restart();}
                        else if(phase==2&&!capture.Running){Require(reason=="时间到"&&finalCount>=8,"自动结束失败");Require(capture.Snapshot().ElapsedMs==1200,"结束时间超出时长");checks.Add("deadline stops background capture without focus");form.Close();}
                        else if(clock.ElapsedMilliseconds>7000)throw new Exception("后台采样测试超时");
                    };
                    ticker.Start();Application.Run(form);ticker.Stop();
                }
                Session visual=VisualSession();store.Save(visual,false);SessionStore.Validate(visual);
                using(var png=MapView.Export(visual,true,false))png.Save(Path.Combine(directory,"minute-clicks-preview.png"),ImageFormat.Png);
                using(var png=MapView.Export(visual,true,false,false,true))png.Save(Path.Combine(directory,"lines-only-preview.png"),ImageFormat.Png);
                using(var form=new MainForm(directory)){form.ShowInTaskbar=false;form.Opacity=0;form.Show();form.Hide();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(directory,"desktop-ui.png"),ImageFormat.Png);}form.Dispose();}checks.Add("five-minute artwork, independent layers and desktop controls render");
                SessionStore.AtomicWrite(report,new JavaScriptSerializer().Serialize(new{passed=true,checks=checks,backgroundSamples=afterStop,deadlineSamples=finalCount}));return 0;
            }catch(Exception e){SessionStore.AtomicWrite(report,new JavaScriptSerializer().Serialize(new{passed=false,checks=checks,error=e.ToString()}));return 1;}
        }
    }
}
