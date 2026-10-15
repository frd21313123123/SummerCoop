using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SummerCoop.Launcher
{
    // Geometry and palette adapted from MWCoop launcher/ui.inc
    // and uikit.inc at the revision recorded in vendor/MWCoop/UPSTREAM.md.
    internal static class MwStyle
    {
        internal static bool Dark = true;
        internal static Color Ink { get { return Dark ? Color.FromArgb(238, 244, 255) : Color.FromArgb(16, 30, 48); } }
        internal static Color Grey { get { return Dark ? Color.FromArgb(164, 178, 202) : Color.FromArgb(74, 90, 112); } }
        internal static Color Accent { get { return Dark ? Color.FromArgb(166, 225, 255) : Color.FromArgb(26, 98, 172); } }
        internal static Color OnAccent { get { return Dark ? Color.FromArgb(6, 19, 37) : Color.White; } }
        internal static Color Field { get { return Dark ? Color.FromArgb(30, 43, 67) : Color.FromArgb(239, 246, 253); } }
        internal static Color Ok { get { return Dark ? Color.FromArgb(124, 242, 196) : Color.FromArgb(18, 132, 92); } }
        internal static Color Warn { get { return Dark ? Color.FromArgb(255, 196, 107) : Color.FromArgb(172, 100, 12); } }
        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        internal static void Fill(Graphics g, RectangleF r, float radius, Color fill, Color edge)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
            using (Pen pen = new Pen(edge)) { g.FillPath(b, p); if (edge.A > 0) g.DrawPath(pen, p); }
        }
        internal static void Text(Graphics g, string text, RectangleF r, float size, Color color, bool bold, StringAlignment align)
        {
            using (Font f = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat sf = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(text, f, b, r, sf);
        }
        internal static void Icon(Graphics g, string icon, RectangleF r, Color color)
        {
            GraphicsState save = g.Save();
            g.TranslateTransform(r.X, r.Y); g.ScaleTransform(r.Width / 24, r.Height / 24);
            using (Pen p = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (icon == "home" || icon == "host") {
                    g.DrawLines(p, new[] { new PointF(3,11), new PointF(12,4), new PointF(21,11) });
                    g.DrawLines(p, new[] { new PointF(5,10), new PointF(5,20), new PointF(19,20), new PointF(19,10) });
                    if (icon == "host") { g.DrawLine(p,9,14,15,14); g.DrawLine(p,12,11,12,17); }
                } else if (icon == "users") {
                    g.DrawEllipse(p,5,4,7,7); g.DrawArc(p,2,14,13,12,185,170); g.DrawArc(p,13,4,7,7,270,160); g.DrawArc(p,13,14,8,12,270,80);
                } else if (icon == "join") {
                    g.DrawLines(p,new[]{new PointF(10,4),new PointF(19,4),new PointF(19,20),new PointF(10,20)});
                    g.DrawLine(p,3,12,14,12); g.DrawLines(p,new[]{new PointF(10,8),new PointF(14,12),new PointF(10,16)});
                } else if (icon == "steam") {
                    g.DrawEllipse(p,12,3,9,9); g.DrawEllipse(p,4,13,7,7); g.DrawLine(p,8,13,13,8); g.DrawLine(p,11,17,18,12); g.DrawLine(p,1,13,5,16);
                } else if (icon == "wifi") {
                    g.DrawArc(p,1,3,22,24,220,100); g.DrawArc(p,5,9,14,17,220,100); g.DrawArc(p,9,15,6,8,220,100); g.DrawEllipse(p,11,21,1,1);
                } else if (icon == "gear") {
                    g.DrawEllipse(p,5,5,14,14); g.DrawEllipse(p,9,9,6,6);
                    for(int i=0;i<8;i++) { double a=i*Math.PI/4; g.DrawLine(p,12+(float)Math.Cos(a)*7,12+(float)Math.Sin(a)*7,12+(float)Math.Cos(a)*10,12+(float)Math.Sin(a)*10); }
                } else if (icon == "file" || icon == "book") {
                    g.DrawRectangle(p,5,3,14,18); g.DrawLine(p,9,9,15,9); g.DrawLine(p,9,13,15,13); g.DrawLine(p,9,17,14,17);
                } else if (icon == "play") { g.DrawPolygon(p,new[]{new PointF(7,4),new PointF(20,12),new PointF(7,20)});
                } else if (icon == "close") { g.DrawLine(p,6,6,18,18); g.DrawLine(p,18,6,6,18);
                } else if (icon == "min") { g.DrawLine(p,6,12,18,12);
                } else if (icon == "max") { g.DrawRectangle(p,6,6,12,12);
                } else if (icon == "sun") { g.DrawEllipse(p,8,8,8,8); for(int i=0;i<8;i++){double a=i*Math.PI/4;g.DrawLine(p,12+(float)Math.Cos(a)*7,12+(float)Math.Sin(a)*7,12+(float)Math.Cos(a)*10,12+(float)Math.Sin(a)*10);}
                } else if (icon == "moon") { g.DrawArc(p,4,3,16,17,55,265); g.DrawArc(p,10,1,14,14,85,175);
                } else if (icon == "chevron") { g.DrawLines(p,new[]{new PointF(9,6),new PointF(15,12),new PointF(9,18)});
                } else if (icon == "download") { g.DrawLine(p,12,3,12,16);g.DrawLines(p,new[]{new PointF(7,11),new PointF(12,16),new PointF(17,11)});g.DrawLine(p,5,21,19,21);
                } else { g.DrawEllipse(p,4,4,16,16); g.DrawLine(p,12,8,12,13);g.DrawLine(p,12,17,12,17.1f); }
            }
            g.Restore(save);
        }
    }

    internal sealed class MwScenePanel : Panel
    {
        private readonly Image background;
        private Bitmap scene, blur;
        private bool cachedDark;
        internal MwScenePanel()
        {
            DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.FromArgb(4,8,22);
            background = BackgroundArt.Load(AppDomain.CurrentDomain.BaseDirectory);
        }
        internal void ResetScene() { if(scene!=null) scene.Dispose(); if(blur!=null) blur.Dispose(); scene=blur=null; Invalidate(true); }
        protected override void Dispose(bool disposing) { if(disposing) { ResetScene(); background.Dispose(); } base.Dispose(disposing); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if(scene==null || scene.Size!=ClientSize || cachedDark!=MwStyle.Dark) Build();
            if(scene!=null) e.Graphics.DrawImageUnscaled(scene,0,0);
        }
        internal void Glass(Graphics g, Control card)
        {
            if(scene==null || scene.Size!=ClientSize || cachedDark!=MwStyle.Dark) Build();
            Point origin=PointToClient(card.PointToScreen(Point.Empty));
            RectangleF r=new RectangleF(.5f,.5f,card.Width-1,card.Height-1);
            using(GraphicsPath p=MwStyle.Round(r,22*Width/1280f))
            {
                // Transparent children ask their parent to paint through a
                // translated, clipped Graphics. Replacing that clip paints
                // over neighbouring labels and leaves copies of their text.
                GraphicsState state=g.Save(); g.SetClip(p,CombineMode.Intersect);
                if(blur!=null) g.DrawImage(blur,-origin.X,-origin.Y);
                using(SolidBrush b=new SolidBrush(MwStyle.Dark?Color.FromArgb(128,14,24,52):Color.FromArgb(150,255,255,255))) g.FillPath(b,p);
                g.Restore(state);
                using(Pen pen=new Pen(Color.FromArgb(MwStyle.Dark?38:190,255,255,255))) g.DrawPath(pen,p);
            }
        }
        private void Build()
        {
            if(Width<=0 || Height<=0) return;
            if(scene!=null) scene.Dispose(); if(blur!=null) blur.Dispose();
            cachedDark=MwStyle.Dark; scene=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(scene)) {
                // Cover the window without stretching the previous launcher's artwork.
                float scale = Math.Max((float)Width / background.Width, (float)Height / background.Height);
                float w = background.Width * scale, h = background.Height * scale;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(background, new RectangleF((Width-w)/2, (Height-h)/2, w, h),
                    new RectangleF(0, 0, background.Width, background.Height), GraphicsUnit.Pixel);
                if (MwStyle.Dark)
                    using (SolidBrush shade = new SolidBrush(Color.FromArgb(48, 4, 8, 22)))
                        g.FillRectangle(shade, 0, 0, Width, Height);
            }
            using(Bitmap tiny=new Bitmap(38,24)) {
                using(Graphics g=Graphics.FromImage(tiny)) { g.InterpolationMode=InterpolationMode.HighQualityBilinear; g.DrawImage(scene,0,0,38,24); }
                blur=new Bitmap(Width,Height); using(Graphics g=Graphics.FromImage(blur)) { g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.DrawImage(tiny,0,0,Width,Height); }
            }
        }
    }

    internal sealed class MwGlassPanel : Panel
    {
        internal MwGlassPanel() { DoubleBuffered=true; BackColor=Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            MwScenePanel scene=null;for(Control p=Parent;p!=null;p=p.Parent) if(p is MwScenePanel){scene=(MwScenePanel)p;break;}
            base.OnPaintBackground(e);
            if(scene==null) return;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;scene.Glass(e.Graphics,this);
        }
    }
    internal sealed class MwButton : Button
    {
        internal bool Primary, Selected, Tile, Navigation;
        internal string IconName="", Description="";
        private bool hot;
        internal MwButton() { FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;BackColor=Color.Transparent;Cursor=Cursors.Hand;UseVisualStyleBackColor=false;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true); }
        protected override void OnMouseEnter(EventArgs e){hot=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hot=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            float k=Font.SizeInPoints/10f;
            RectangleF r=new RectangleF(.5f,.5f,Width-1,Height-1);
            Color ink=Primary?MwStyle.OnAccent:MwStyle.Ink;
            Color fill=Primary?MwStyle.Accent:Color.FromArgb(hot?40:Selected?34:14,MwStyle.Dark?Color.White:Color.FromArgb(20,40,64));
            if(Tile && !Primary) fill=Color.FromArgb(hot?220:190,MwStyle.Dark?Color.FromArgb(14,24,52):Color.FromArgb(239,246,253));
            Color edge=Primary?Color.Transparent:Selected?MwStyle.Accent:Color.FromArgb(30,MwStyle.Ink);
            if(!Enabled) {fill=Color.FromArgb(55,fill);ink=MwStyle.Grey;}
            if(!Navigation || Selected || hot) MwStyle.Fill(g,r,Tile?22:12,fill,edge);
            if(Tile) {
                MwStyle.Fill(g,new RectangleF(22*k,20*k,44*k,44*k),14*k,Color.FromArgb(22,ink),Color.Transparent);
                MwStyle.Icon(g,IconName,new RectangleF(33*k,31*k,22*k,22*k),ink);
                MwStyle.Text(g,Text,new RectangleF(22*k,74*k,Width-44*k,34*k),25*k,ink,true,StringAlignment.Near);
                MwStyle.Text(g,Description,new RectangleF(22*k,115*k,Width-44*k,Height-136*k),14*k,ink,false,StringAlignment.Near);
                MwStyle.Icon(g,"chevron",new RectangleF(Width-43*k,Height-40*k,20*k,20*k),ink);
            } else {
                float left=IconName.Length==0?12:44;
                if(IconName.Length>0) MwStyle.Icon(g,IconName,new RectangleF(14*k,(Height-20*k)/2,20*k,20*k),Selected?MwStyle.Accent:ink);
                using(Font font=new Font("Segoe UI",Font.Size,Primary||Selected?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
                using(SolidBrush b=new SolidBrush(ink))
                using(StringFormat sf=new StringFormat{Alignment=Navigation?StringAlignment.Near:StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
                    g.DrawString(Text,font,b,new RectangleF(left*k,0,Width-(left+12)*k,Height),sf);
            }
            if(Focused && ShowFocusCues) {using(Pen p=new Pen(MwStyle.Accent){DashStyle=DashStyle.Dot}) g.DrawRectangle(p,4,4,Width-9,Height-9);}
        }
    }
}
