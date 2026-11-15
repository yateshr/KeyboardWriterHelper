using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace KeyboardWriterHelper
{
    // ---- Pictures for the installer, drawn at the exact size they will be shown ----

    // The installer asks for these while it starts (KeyboardWriterHelper.exe
    // --wizard-art out.bmp large|small width height version [background colour]),
    // with the real pixel size of the picture on this screen. Nothing is stretched,
    // so the logo and the text are as sharp as the screen allows at any scaling.
    static class WizardArt
    {
        static TextRenderingHint hint = TextRenderingHint.AntiAlias;

        public static int Run(string[] args)
        {
            try
            {
                string path = args[1], kind = args[2];
                int width = int.Parse(args[3]), height = int.Parse(args[4]);
                string version = args.Length > 5 ? args[5] : "";
                int back = args.Length > 6 ? int.Parse(args[6]) : 0xFFFFFF;
                if (args.Length > 7 && args[7] == "ct") hint = TextRenderingHint.ClearTypeGridFit;
                if (width < 8 || height < 8 || width > 4000 || height > 4000) return 2;
                using (Bitmap bmp = kind == "small" ? Small(width, height, back) : Large(width, height, version))
                    bmp.Save(path, ImageFormat.Bmp);
                return 0;
            }
            catch (Exception) { return 1; }
        }

        // Drawn straight at the final size: shapes and text are anti-aliased by GDI+, in grey
        // (not ClearType, which leaves colour fringes on a picture). Drawing larger and
        // scaling down looked softer.
        const int Oversample = 1;

        static Bitmap Finish(Bitmap big, int width, int height)
        {
            if (big.Width == width && big.Height == height) return big;
            Bitmap result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(big, new Rectangle(0, 0, width, height), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel);
            }
            big.Dispose();
            return result;
        }

        static Bitmap Small(int width, int height, int backColor)
        {
            int s = Oversample;
            Bitmap big = new Bitmap(width * s, height * s, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(big))
            {
                // The page colour behind it cannot be known (dark or light), so the corners are the
                // key's own dark green: a small square tile, with no white edge.
                Color green = Color.FromArgb(22, 163, 74);
                g.Clear(Theme.Shade(green, 0.33));
                int size = Math.Min(big.Width, big.Height);
                Theme.DrawLogo(g, new Rectangle((big.Width - size) / 2, (big.Height - size) / 2, size, size), green);
            }
            return Finish(big, width, height);
        }

        static Bitmap Large(int width, int height, string version)
        {
            int s = Oversample;
            int W = width * s, H = height * s;
            // One unit is a pixel of the 100 % design (164 x 314), scaled to this size.
            float k = Math.Min(width / 164f, height / 314f) * s;
            Bitmap big = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = hint;

                using (LinearGradientBrush bg = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                    Color.FromArgb(4, 40, 24), Color.FromArgb(22, 128, 62), 70f))
                    g.FillRectangle(bg, 0, 0, W, H);

                // soft keycaps across the lower part, fading out towards the bottom
                float keyTop = H * 0.58f;
                float key = 27 * k, gap = 6 * k, round = 6 * k;
                using (SolidBrush capFill = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                using (Pen capEdge = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1f, 1.2f * k)))
                {
                    for (int row = 0; row < 6; row++)
                    {
                        float y = keyTop + row * (key + gap);
                        float offset = (row % 2) * ((key + gap) / 2) - 14 * k;
                        for (int col = 0; col < 9; col++)
                        {
                            float x = offset + col * (key + gap);
                            if (x > W) break;
                            using (GraphicsPath cap = Theme.Round(new RectangleF(x, y, key, key), round))
                            {
                                g.FillPath(capFill, cap);
                                g.DrawPath(capEdge, cap);
                            }
                        }
                    }
                }
                float fadeTop = H * 0.48f;
                using (LinearGradientBrush fade = new LinearGradientBrush(new RectangleF(0, fadeTop, W, H - fadeTop + 1),
                    Color.FromArgb(0, 4, 40, 24), Color.FromArgb(175, 4, 40, 24), 90f))
                    g.FillRectangle(fade, 0, fadeTop, W, H - fadeTop + 1);

                // the logo, large and centred, with a soft glow behind it
                int logo = (int)(Math.Min(100 * k, W * 0.62f));
                int lx = (W - logo) / 2, ly = (int)(H * 0.075f);
                using (GraphicsPath glowPath = new GraphicsPath())
                {
                    glowPath.AddEllipse(lx - 26 * k, ly - 20 * k, logo + 52 * k, logo + 52 * k);
                    using (PathGradientBrush glow = new PathGradientBrush(glowPath))
                    {
                        glow.CenterColor = Color.FromArgb(90, 74, 222, 128);
                        glow.SurroundColors = new Color[] { Color.FromArgb(0, 74, 222, 128) };
                        g.FillPath(glow, glowPath);
                    }
                }
                Theme.DrawLogo(g, new Rectangle(lx, ly, logo, logo), Color.FromArgb(22, 163, 74));

                using (StringFormat centre = new StringFormat())
                using (Font title = new Font(Theme.Semibold, 16 * k, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font sub = new Font(Theme.Regular, 11.5f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush soft = new SolidBrush(Color.FromArgb(225, 236, 247, 240)))
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(190, 236, 247, 240)))
                {
                    centre.Alignment = StringAlignment.Center;
                    g.DrawString("Keyboard Writer\nHelper", title, Brushes.White, new RectangleF(0, H * 0.445f, W, 56 * k), centre);
                    g.DrawString("Spelling fixed as you type,\nin every program.", sub, soft, new RectangleF(0, H * 0.645f, W, 40 * k), centre);
                    g.DrawString((version.Length > 0 ? "Version " + version + "\n" : "") + "by YATESH ROHIT", sub, dim,
                        new RectangleF(0, H - 50 * k, W, 44 * k), centre);
                }
            }
            return Finish(big, width, height);
        }
    }
}
