using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace SummerCoop.Launcher
{
    // Фон вшит в EXE как ресурс SummerCoop.Launcher.Background (один файл, ничего рядом не нужно).
    // Внешний background.jpg/png рядом с EXE имеет приоритет — так можно сменить арт без пересборки.
    internal static class BackgroundArt
    {
        internal const string EmbeddedBackground = "SummerCoop.Launcher.Background";

        internal static Image Load(string exeDir)
        {
            foreach (string name in new string[] { "background.png", "background.jpg", "background.jpeg" })
            {
                try
                {
                    string direct = Path.Combine(exeDir, name);
                    if (File.Exists(direct)) { using (Image art = Image.FromFile(direct)) return new Bitmap(art); }
                    string assets = Path.Combine(exeDir, Path.Combine("assets", name));
                    if (File.Exists(assets)) { using (Image art = Image.FromFile(assets)) return new Bitmap(art); }
                }
                catch (Exception) { }
            }
            try
            {
                using (Stream embedded = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedBackground))
                    if (embedded != null)
                    {
                        // Копия в память: исходный поток ресурса нельзя держать открытым.
                        using (Image art = Image.FromStream(embedded)) return new Bitmap(art);
                    }
            }
            catch (Exception) { }
            return CreatePlaceholder(1080, 660);
        }

        internal static Bitmap CreatePlaceholder(int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // Небо.
                using (LinearGradientBrush sky = new LinearGradientBrush(new Rectangle(0, 0, w, (int)(h * 0.55)), Color.FromArgb(64, 150, 230), Color.FromArgb(200, 235, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(sky, 0, 0, w, (int)(h * 0.55));
                // Солнце слева.
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(90, 255, 250, 210)))
                    g.FillEllipse(glow, (int)(w * 0.05), (int)(h * 0.08), 170, 170);
                using (SolidBrush sun = new SolidBrush(Color.FromArgb(255, 253, 240)))
                    g.FillEllipse(sun, (int)(w * 0.085), (int)(h * 0.13), 90, 90);
                // Облака.
                using (SolidBrush cloud = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                {
                    g.FillEllipse(cloud, (int)(w * 0.25), (int)(h * 0.20), 130, 22);
                    g.FillEllipse(cloud, (int)(w * 0.42), (int)(h * 0.30), 170, 24);
                    g.FillEllipse(cloud, (int)(w * 0.62), (int)(h * 0.22), 120, 20);
                }
                // Дальний лес.
                using (SolidBrush far = new SolidBrush(Color.FromArgb(70, 110, 130)))
                    g.FillRectangle(far, 0, (int)(h * 0.46), w, (int)(h * 0.06));
                // Озеро.
                using (LinearGradientBrush lake = new LinearGradientBrush(new Rectangle(0, (int)(h * 0.52), w, (int)(h * 0.16)), Color.FromArgb(170, 205, 235), Color.FromArgb(90, 140, 190), LinearGradientMode.Vertical))
                    g.FillRectangle(lake, 0, (int)(h * 0.52), w, (int)(h * 0.16));
                using (SolidBrush glare = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                    g.FillEllipse(glare, (int)(w * 0.10), (int)(h * 0.55), 90, 26);
                // Остров.
                using (SolidBrush island = new SolidBrush(Color.FromArgb(45, 100, 70)))
                {
                    g.FillEllipse(island, (int)(w * 0.28), (int)(h * 0.52), (int)(w * 0.24), (int)(h * 0.07));
                    g.FillEllipse(island, (int)(w * 0.55), (int)(h * 0.55), (int)(w * 0.10), (int)(h * 0.04));
                }
                // Трава.
                using (LinearGradientBrush grass = new LinearGradientBrush(new Rectangle(0, (int)(h * 0.66), w, h - (int)(h * 0.66)), Color.FromArgb(150, 190, 80), Color.FromArgb(95, 140, 55), LinearGradientMode.Vertical))
                    g.FillRectangle(grass, 0, (int)(h * 0.66), w, h - (int)(h * 0.66));
                // Дорога.
                using (SolidBrush road = new SolidBrush(Color.FromArgb(205, 165, 120)))
                {
                    Point[] p = new Point[] {
                        new Point((int)(w * 0.30), h),
                        new Point((int)(w * 0.45), (int)(h * 0.68)),
                        new Point(w, (int)(h * 0.72)),
                        new Point(w, h) };
                    g.FillPolygon(road, p);
                }
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(60, 90, 60, 40)))
                {
                    Point[] p = new Point[] {
                        new Point((int)(w * 0.55), h),
                        new Point((int)(w * 0.62), (int)(h * 0.70)),
                        new Point(w, (int)(h * 0.74)),
                        new Point(w, h) };
                    g.FillPolygon(shadow, p);
                }
                // Деревья слева и справа.
                DrawPine(g, (int)(w * 0.04), (int)(h * 0.30), 90, 220);
                DrawPine(g, (int)(w * 0.10), (int)(h * 0.42), 60, 140);
                DrawPine(g, (int)(w * 0.68), (int)(h * 0.18), 100, 260);
                DrawPine(g, (int)(w * 0.80), (int)(h * 0.28), 70, 170);
                DrawPine(g, (int)(w * 0.90), (int)(h * 0.15), 110, 280);
                // Красный гараж справа.
                int bx = (int)(w * 0.78), by = (int)(h * 0.44), bw = (int)(w * 0.22), bh = (int)(h * 0.26);
                using (SolidBrush wall = new SolidBrush(Color.FromArgb(175, 60, 50)))
                    g.FillRectangle(wall, bx, by, bw, bh);
                Point[] roof = new Point[] { new Point(bx - 12, by), new Point(bx + bw + 12, by), new Point(bx + bw - 20, by - 36), new Point(bx + 20, by - 36) };
                using (SolidBrush roofB = new SolidBrush(Color.FromArgb(60, 65, 75)))
                    g.FillPolygon(roofB, roof);
                using (SolidBrush door = new SolidBrush(Color.FromArgb(90, 70, 60)))
                    g.FillRectangle(door, bx + bw / 4, by + bh / 3, bw / 2, bh * 2 / 3);
                using (SolidBrush win = new SolidBrush(Color.FromArgb(230, 240, 245)))
                    g.FillRectangle(win, bx + 12, by + bh / 3, 26, 40);
                // Бочка и шины.
                using (SolidBrush barrel = new SolidBrush(Color.FromArgb(70, 110, 160)))
                    g.FillRectangle(barrel, bx - 34, by + bh - 44, 26, 44);
                using (SolidBrush tire = new SolidBrush(Color.FromArgb(40, 40, 45)))
                {
                    g.FillEllipse(tire, bx - 30, by + bh - 16, 34, 16);
                    g.FillEllipse(tire, bx - 30, by + bh - 30, 34, 16);
                }
                // Трава на переднем плане.
                using (SolidBrush blade = new SolidBrush(Color.FromArgb(80, 130, 50)))
                {
                    Random r = new Random(516750);
                    for (int i = 0; i < 90; i++)
                    {
                        int x = r.Next(0, w), y = r.Next((int)(h * 0.78), h);
                        g.FillPolygon(blade, new Point[] { new Point(x, y), new Point(x + 5, y - 18 - r.Next(14)), new Point(x + 10, y) });
                    }
                }
                // Виньетка.
                using (LinearGradientBrush v = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(v, 0, 0, w, h);
            }
            return bmp;
        }

        private static void DrawPine(Graphics g, int x, int y, int w, int h)
        {
            using (SolidBrush trunk = new SolidBrush(Color.FromArgb(110, 70, 45)))
                g.FillRectangle(trunk, x + w / 2 - 6, y + h / 2, 12, h / 2);
            using (SolidBrush pine = new SolidBrush(Color.FromArgb(45, 105, 60)))
            {
                g.FillPolygon(pine, new Point[] { new Point(x, y + h / 2), new Point(x + w / 2, y), new Point(x + w, y + h / 2) });
                g.FillPolygon(pine, new Point[] { new Point(x - 8, y + h * 2 / 3), new Point(x + w / 2, y + h / 4), new Point(x + w + 8, y + h * 2 / 3) });
            }
        }
    }
}
