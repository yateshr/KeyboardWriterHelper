using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyboardWriterHelper
{
    // ---- Floating Candidate Popup ----

    // The bar in the top right corner of the screen being typed on: the choices
    // after a correction, or the Tab suggestion. Everything is measured from
    // `scale`, which is worked out again for the screen it is shown on, so it is
    // the same size to the eye on a laptop, a 4K display with Windows scaling,
    // a large screen without scaling, or several screens of different density.
    sealed class CandidatePopup : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        string currentFix = "";
        string[] currentChoices = new string[0];
        bool isPredictionMode;
        string predictionWord = "";
        string predictionPrefix = "";

        // Text size in pixels at 96 DPI (9.5 pt), and the size of a 1080 p screen.
        const float BaseFontPixels = 12.67f;
        const int ReferenceHeight = 1080;
        // A big screen without Windows scaling still gets a bigger bar, but never
        // more than this many times the normal size.
        const double MaxSizeFactor = 2.5;

        double scale = 1.0;
        Font popupFont;

        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public CandidatePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            // The size is set here, once for each time it is shown, and never by
            // WinForms' own scaling when the window lands on another screen.
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 34, 38);
            ForeColor = Color.White;
            AccessibleName = "Word Suggestions";
            AccessibleRole = AccessibleRole.ToolTip;
            ApplyScale(1.0);

            hideTimer.Interval = 2500;
            hideTimer.Tick += delegate { Hide(); hideTimer.Stop(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hideTimer.Dispose();
                if (popupFont != null) popupFont.Dispose();
            }
            base.Dispose(disposing);
        }

        int S(int value) { return (int)Math.Round(value * scale); }

        void ApplyScale(double newScale)
        {
            if (popupFont != null && Math.Abs(newScale - scale) < 0.01) return;
            scale = newScale;
            Font old = popupFont;
            popupFont = new Font("Segoe UI", (float)(BaseFontPixels * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            Font = popupFont;
            if (old != null) old.Dispose();
            Height = Math.Max(S(36), popupFont.Height + S(16));
        }

        // Windows' scaling for the screen (150 % = 1.5) times how much bigger the
        // screen is than a 1080 p one in the scaled units it reports.
        static double ScaleFor(Screen screen)
        {
            double dpiScale = 1.0;
            try
            {
                Rectangle b = screen.Bounds;
                IntPtr monitor = MonitorFromPoint(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2), 2); // nearest
                uint dx, dy;
                if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out dx, out dy) == 0 && dx > 0) dpiScale = dx / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            // Height in the units the scaling leaves: a 4K screen at 200 % is 1080.
            double logicalHeight = screen.WorkingArea.Height / dpiScale;
            double sizeFactor = Math.Max(1.0, Math.Min(MaxSizeFactor, logicalHeight / ReferenceHeight));
            return dpiScale * sizeFactor;
        }

        Screen TargetScreen(IntPtr foreground)
        {
            try { return foreground != IntPtr.Zero ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position); }
            catch (Exception) { return Screen.PrimaryScreen; }
        }

        public void ShowCandidates(string fix, string[] choices, IntPtr foreground)
        {
            if (choices == null || choices.Length == 0) { Hide(); return; }
            Screen screen = TargetScreen(foreground);
            ApplyScale(ScaleFor(screen));
            isPredictionMode = false;
            currentFix = fix;
            currentChoices = choices;
            AccessibleDescription = string.Join(", ", choices);
            hideTimer.Stop();
            hideTimer.Start();

            int width = S(24);
            using (Graphics g = CreateGraphics())
            {
                for (int i = 0; i < choices.Length; i++)
                    width += (int)g.MeasureString((i + 1) + ". " + choices[i], popupFont).Width + S(16);
            }
            width += S(40);
            int maxWidth = Math.Min(S(600), (int)(screen.WorkingArea.Width * 0.6));
            Present(screen, Math.Max(S(240), Math.Min(width, maxWidth)));
        }

        public void ShowPrediction(string prediction, string prefix, IntPtr foreground)
        {
            if (string.IsNullOrEmpty(prediction)) { Dismiss(); return; }
            Screen screen = TargetScreen(foreground);
            ApplyScale(ScaleFor(screen));
            isPredictionMode = true;
            predictionWord = prediction;
            predictionPrefix = prefix ?? "";
            AccessibleDescription = "Tab to accept prediction: " + prediction;
            hideTimer.Stop();
            hideTimer.Start();

            int width;
            using (Graphics g = CreateGraphics())
                width = (int)(g.MeasureString("⇥ Tab", popupFont).Width + g.MeasureString(prediction, popupFont).Width + g.MeasureString("Esc", popupFont).Width) + S(64);
            int maxWidth = Math.Min(S(600), (int)(screen.WorkingArea.Width * 0.6));
            Present(screen, Math.Min(Math.Max(S(180), width), maxWidth));
        }

        // Always the top right corner of the screen being typed on, so it is in the
        // same place every time and never covers the text. The size is set after
        // the window is shown too: moving to a screen with other scaling can make
        // Windows resize it.
        void Present(Screen screen, int width)
        {
            Rectangle area = screen.WorkingArea;
            Size size = new Size(width, Math.Max(S(36), popupFont.Height + S(16)));
            Rectangle bounds = new Rectangle(new Point(area.Right - size.Width - S(20), area.Top + S(20)), size);
            Bounds = bounds;
            Invalidate();
            if (!Visible) Show();
            if (Bounds != bounds) Bounds = bounds;
        }

        public void Dismiss()
        {
            hideTimer.Stop();
            isPredictionMode = false;
            Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(6)))
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 30, 34)))
                    g.FillPath(bg, path);
                using (Pen border = new Pen(Color.FromArgb(60, 64, 72), 1))
                    g.DrawPath(border, path);
            }

            int x = S(12);
            int y = (Height - popupFont.Height) / 2;
            int inset = S(4), radius = S(4);
            if (isPredictionMode)
            {
                string tabBadge = "⇥ Tab";
                SizeF tabSz = g.MeasureString(tabBadge, popupFont);
                Rectangle pill = new Rectangle(x - inset, inset, (int)tabSz.Width + 2 * inset, Height - 2 * inset);
                using (GraphicsPath p = RoundedRect(pill, radius))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    g.FillPath(b, p);
                using (SolidBrush txt = new SolidBrush(Color.White))
                    g.DrawString(tabBadge, popupFont, txt, x, y);

                x += (int)tabSz.Width + S(14);
                using (SolidBrush wordBrush = new SolidBrush(Color.FromArgb(240, 243, 246)))
                    g.DrawString(predictionWord, popupFont, wordBrush, x, y);

                SizeF escSz = g.MeasureString("Esc", popupFont);
                using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(140, 144, 152)))
                    g.DrawString("Esc", popupFont, hintBrush, Width - escSz.Width - S(8), y);
                return;
            }

            SizeF hintSz = g.MeasureString("⌫", popupFont);
            int hintX = Width - (int)hintSz.Width - S(8);
            for (int i = 0; i < currentChoices.Length && x < hintX - S(8); i++)
            {
                string word = currentChoices[i];
                bool active = string.Equals(word, currentFix, StringComparison.OrdinalIgnoreCase);
                string text = (i + 1) + ". " + word;
                SizeF sz = g.MeasureString(text, popupFont);

                if (active)
                {
                    Rectangle pill = new Rectangle(x - inset, inset, (int)sz.Width + 2 * inset, Height - 2 * inset);
                    using (GraphicsPath p = RoundedRect(pill, radius))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(22, 163, 74)))
                        g.FillPath(b, p);
                    using (SolidBrush txt = new SolidBrush(Color.White))
                        g.DrawString(text, popupFont, txt, x, y);
                }
                else
                {
                    using (SolidBrush txt = new SolidBrush(Color.FromArgb(180, 184, 192)))
                        g.DrawString(text, popupFont, txt, x, y);
                }
                x += (int)sz.Width + S(14);
            }

            using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(180, 184, 192)))
                g.DrawString("⌫", popupFont, hintBrush, hintX, y);
        }

        static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = Math.Max(2, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
