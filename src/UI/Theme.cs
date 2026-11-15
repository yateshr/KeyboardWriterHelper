using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardWriterHelper
{
    // ---- Look of the app's windows: colours and type ----

    enum Face { Title, Heading, Body, BodyStrong, Caption, CaptionStrong, Stat, Nav, Icon }

    // One place for every colour and font, so the windows look like one thing.
    // Type scale (pixels at 100 % scaling): title 26, section heading 15, body 14,
    // caption 12.5, big numbers 30. Sizes are in pixels and multiplied by the
    // window's own DPI, so they stay sharp on every screen and Windows scaling.
    static class Theme
    {
        public static bool Dark;
        public static Color Bg, Sidebar, Card, Border, Divider, Text, Muted, Accent, AccentText, AccentSoft,
            OnAccent, AccentHover, FieldBg, Danger, Hover;

        public const string Regular = "Segoe UI", Semibold = "Segoe UI Semibold", IconFont = "Segoe MDL2 Assets";

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static readonly Dictionary<string, bool> installed = new Dictionary<string, bool>();

        // Follows the Windows "app mode" (light or dark).
        public static void Load()
        {
            Dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (v is int && (int)v == 0) Dark = true;
                }
            }
            catch (Exception) { }
            // KWH_THEME=light or dark forces a look (to check both).
            string forced = Environment.GetEnvironmentVariable("KWH_THEME");
            if (forced == "light") Dark = false;
            else if (forced == "dark") Dark = true;

            if (Dark)
            {
                Bg = Color.FromArgb(24, 26, 30);
                Sidebar = Color.FromArgb(19, 21, 24);
                Card = Color.FromArgb(33, 36, 41);
                Border = Color.FromArgb(62, 67, 76);
                Divider = Color.FromArgb(47, 51, 58);
                Text = Color.FromArgb(236, 238, 241);
                Muted = Color.FromArgb(152, 159, 170);
                Accent = Color.FromArgb(52, 190, 105);
                AccentHover = Color.FromArgb(74, 210, 125);
                AccentText = Color.FromArgb(86, 214, 134);
                AccentSoft = Color.FromArgb(30, 68, 46);
                OnAccent = Color.FromArgb(7, 30, 17);
                FieldBg = Color.FromArgb(26, 28, 32);
                Danger = Color.FromArgb(248, 113, 113);
                Hover = Color.FromArgb(42, 46, 52);
            }
            else
            {
                Bg = Color.FromArgb(247, 248, 250);
                Sidebar = Color.FromArgb(240, 242, 245);
                Card = Color.FromArgb(255, 255, 255);
                Border = Color.FromArgb(221, 225, 231);
                Divider = Color.FromArgb(236, 239, 243);
                Text = Color.FromArgb(22, 26, 33);
                Muted = Color.FromArgb(98, 107, 120);
                Accent = Color.FromArgb(21, 128, 61);
                AccentHover = Color.FromArgb(22, 101, 52);
                AccentText = Color.FromArgb(21, 128, 61);
                AccentSoft = Color.FromArgb(219, 242, 228);
                OnAccent = Color.White;
                FieldBg = Color.White;
                Danger = Color.FromArgb(185, 28, 28);
                Hover = Color.FromArgb(232, 235, 240);
            }
        }

        public static int Px(Control c, int pixelsAt96)
        {
            return (int)Math.Round(pixelsAt96 * c.DeviceDpi / 96.0);
        }

        static bool Has(string family)
        {
            bool ok;
            if (installed.TryGetValue(family, out ok)) return ok;
            ok = false;
            try
            {
                foreach (FontFamily f in FontFamily.Families)
                    if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            }
            catch (Exception) { }
            installed[family] = ok;
            return ok;
        }

        public static bool HasIcons { get { return Has(IconFont); } }

        public static Font Get(Face face, int dpi)
        {
            string key = face + "@" + dpi;
            Font font;
            lock (fonts)
            {
                if (fonts.TryGetValue(key, out font)) return font;
                string family = Has(Regular) ? Regular : SystemFonts.MessageBoxFont.Name;
                string strong = Has(Semibold) ? Semibold : family;
                float px;
                string name = family;
                FontStyle style = FontStyle.Regular;
                switch (face)
                {
                    case Face.Title: px = 26; name = strong; break;
                    case Face.Heading: px = 15; name = strong; break;
                    case Face.Body: px = 14; break;
                    case Face.BodyStrong: px = 14; name = strong; break;
                    case Face.Caption: px = 12.5f; break;
                    case Face.CaptionStrong: px = 12.5f; name = strong; break;
                    case Face.Stat: px = 30; name = strong; break;
                    case Face.Nav: px = 14; break;
                    default: px = 16; name = Has(IconFont) ? IconFont : family; break;
                }
                font = new Font(name, px * dpi / 96f, style, GraphicsUnit.Pixel);
                fonts[key] = font;
                return font;
            }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
        public const TextFormatFlags OneLine = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

        public static int TextHeight(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, new Size(Math.Max(10, width), 100000), Wrap).Height;
        }

        public static int TextWidth(string text, Font font)
        {
            return TextRenderer.MeasureText(text, font, new Size(100000, 100000), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);

        // Thin, dark-aware scrollbars and menus on a standard control.
        public static void StyleNative(Control c)
        {
            try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (Exception) { }
        }

        // The grey hint a text box shows while it is empty.
        public static void SetHint(TextBox box, string hint)
        {
            try { SendMessage(box.Handle, 0x1501, IntPtr.Zero, hint); }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Dark title bar to match the dark window.
        public static void StyleTitleBar(Form form)
        {
            try
            {
                int on = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(form.Handle, 19, ref on, 4);
            }
            catch (Exception) { }
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(2f, radius * 2f);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Darker (factor below 1) or lighter (above 1) version of a colour.
        public static Color Shade(Color c, double factor)
        {
            if (factor <= 1)
                return Color.FromArgb(c.A, (int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));
            double t = factor - 1;
            return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));
        }

        // The logo: a keycap with a white letter A and a check badge on its corner.
        // Drawn from shapes (no font), in the colour given: green when it is running.
        // The same drawing as assets\logo-lib.ps1, which makes the icon files.
        public static void DrawLogo(Graphics g, Rectangle r, Color back)
        {
            SmoothingMode oldSmoothing = g.SmoothingMode;
            System.Drawing.Drawing2D.PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            float n = r.Width, x0 = r.X, y0 = r.Y;
            Color shadow = Color.FromArgb(70, 4, 50, 24);

            using (GraphicsPath plate = Round(new RectangleF(x0, y0, n, n), n * 0.225f))
            using (LinearGradientBrush b = new LinearGradientBrush(new PointF(0, y0), new PointF(0, y0 + n), Shade(back, 0.62), Shade(back, 0.33)))
                g.FillPath(b, plate);

            RectangleF faceRect = new RectangleF(x0 + n * 0.065f, y0 + n * 0.045f, n * 0.87f, n * 0.80f);
            using (GraphicsPath face = Round(faceRect, n * 0.17f))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(new PointF(0, faceRect.Top), new PointF(0, faceRect.Bottom), Shade(back, 1.32), back))
                    g.FillPath(b, face);
                using (Pen hi = new Pen(Color.FromArgb(110, 255, 255, 255), Math.Max(1f, n * 0.012f)))
                    g.DrawPath(hi, face);
            }

            // the letter A (a soft shadow first, then the white letter)
            for (int pass = 0; pass < 2; pass++)
            {
                float dy = pass == 0 ? n * 0.016f : 0f;
                Color ink = pass == 0 ? shadow : Color.White;
                using (Pen leg = new Pen(ink, n * 0.088f))
                using (Pen bar = new Pen(ink, n * 0.072f))
                {
                    leg.StartCap = leg.EndCap = LineCap.Round;
                    leg.LineJoin = LineJoin.Round;
                    bar.StartCap = bar.EndCap = LineCap.Round;
                    g.DrawLines(leg, new PointF[] {
                        new PointF(x0 + n * 0.255f, y0 + n * 0.690f + dy),
                        new PointF(x0 + n * 0.455f, y0 + n * 0.215f + dy),
                        new PointF(x0 + n * 0.655f, y0 + n * 0.690f + dy) });
                    g.DrawLine(bar, x0 + n * 0.352f, y0 + n * 0.535f + dy, x0 + n * 0.558f, y0 + n * 0.535f + dy);
                }
            }

            // the badge
            float bx = x0 + n * 0.735f, by = y0 + n * 0.715f, br = n * 0.185f;
            using (SolidBrush b = new SolidBrush(shadow))
                g.FillEllipse(b, bx - br, by - br + n * 0.016f, 2 * br, 2 * br);
            using (SolidBrush b = new SolidBrush(Shade(back, 0.55)))
                g.FillEllipse(b, bx - br, by - br, 2 * br, 2 * br);
            using (Pen ring = new Pen(Color.White, Math.Max(1f, n * 0.026f)))
                g.DrawEllipse(ring, bx - br, by - br, 2 * br, 2 * br);
            using (Pen check = new Pen(Color.White, Math.Max(1f, n * 0.056f)))
            {
                check.StartCap = check.EndCap = LineCap.Round;
                check.LineJoin = LineJoin.Round;
                g.DrawLines(check, new PointF[] {
                    new PointF(bx - br * 0.50f, by + br * 0.02f),
                    new PointF(bx - br * 0.12f, by + br * 0.42f),
                    new PointF(bx + br * 0.55f, by - br * 0.38f) });
            }
            g.SmoothingMode = oldSmoothing;
            g.PixelOffsetMode = oldOffset;
        }
    }

    // ---- Layout ----

    // Anything a Stack or Card can place: it says how tall it is at a given width.
    interface IFlow
    {
        int Measure(int width);
    }

    // Controls that paint everything themselves, without flicker.
    abstract class Surface : Control
    {
        protected Surface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Margin = Padding.Empty;
        }

        protected Font F(Face face) { return Theme.Get(face, DeviceDpi); }
        protected int P(int pixelsAt96) { return Theme.Px(this, pixelsAt96); }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            if (Parent != null) Parent.PerformLayout();
            Invalidate();
        }
    }

    // Children one under the other, full width, scrolling up and down when they do not fit.
    // It never scrolls sideways: when the vertical scrollbar is needed, the children are
    // made that much narrower first.
    sealed class Stack : Panel
    {
        bool busy;

        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hwnd, int bar, bool show);

        public Stack()
        {
            AutoScroll = true;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleNative(this);
        }

        // Windows may show a sideways scrollbar for a moment while the sizes settle; it is never wanted.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x83) ShowScrollBar(Handle, 0, false); // WM_NCCALCSIZE, SB_HORZ
            base.WndProc(ref m);
        }

        // Places the children for the given width and returns the height they need.
        int Place(int width, int scrollY)
        {
            int y = Padding.Top;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                y += Theme.Px(this, c.Margin.Top);
                IFlow flow = c as IFlow;
                int h = flow != null ? flow.Measure(width) : c.Height;
                c.SetBounds(Padding.Left, y + scrollY, width, h);
                y += h + Theme.Px(this, c.Margin.Bottom);
            }
            return y + Padding.Bottom;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (busy || Width <= 0) return;
            busy = true;
            try
            {
                int scrollY = AutoScrollPosition.Y;
                int full = Width - Padding.Horizontal;
                int total = Place(full, scrollY);
                if (total > ClientSize.Height + (Width - ClientSize.Width) * 0 && total > Height)
                {
                    // Needs the vertical scrollbar: the children get narrower by its width.
                    int bar = Width - ClientSize.Width;
                    if (bar <= 0) bar = SystemInformation.VerticalScrollBarWidth * DeviceDpi / 96;
                    total = Place(full - bar, scrollY);
                }
                AutoScrollMinSize = new Size(0, total);
            }
            finally { busy = false; }
        }
    }

    // A group of rows in one rounded box, with a thin line between them.
    sealed class Card : Surface, IFlow
    {
        bool busy;

        public Card()
        {
            BackColor = Theme.Bg;
            TabStop = false;
        }

        public int Measure(int width)
        {
            int h = 2;
            foreach (Control c in Controls)
            {
                IFlow f = c as IFlow;
                h += f != null ? f.Measure(width - 2) : c.Height;
            }
            h += Math.Max(0, Controls.Count - 1);
            return h;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (busy) return;
            busy = true;
            try
            {
                int y = 1;
                foreach (Control c in Controls)
                {
                    IFlow f = c as IFlow;
                    int h = f != null ? f.Measure(Width - 2) : c.Height;
                    c.SetBounds(1, y, Width - 2, h);
                    y += h + 1;
                }
            }
            finally { busy = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(box, P(8)))
            {
                using (SolidBrush fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
                using (Pen line = new Pen(Theme.Border)) g.DrawPath(line, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            using (Pen divider = new Pen(Theme.Divider))
            {
                for (int i = 0; i < Controls.Count - 1; i++)
                {
                    int y = Controls[i].Bottom;
                    g.DrawLine(divider, P(18), y, Width - P(18), y);
                }
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.BackColor = Theme.Card;
        }
    }

    // Text laid out under a given width: headings, notes and paragraphs.
    sealed class TextBlock : Surface, IFlow
    {
        string text = "";
        Face face;
        Color? colour;

        public TextBlock(string text, Face face, Color? colour)
        {
            this.text = text;
            this.face = face;
            this.colour = colour;
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Value
        {
            get { return text; }
            set { if (text != value) { text = value; AccessibleName = value; if (Parent != null) Parent.PerformLayout(); Invalidate(); } }
        }

        public int Measure(int width) { return Math.Max(P(4), Theme.TextHeight(text, F(face), width)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, text, F(face), ClientRectangle,
                colour.HasValue ? colour.Value : (face == Face.Caption || face == Face.CaptionStrong ? Theme.Muted : Theme.Text),
                Theme.Wrap);
        }
    }

    // ---- Controls ----

    // On/off switch with its label and an explanation under it.
    sealed class ToggleRow : Surface, IFlow
    {
        readonly string title, description;
        bool isChecked, hot;

        public event EventHandler CheckedChanged;

        public ToggleRow(string title, string description, bool value)
        {
            this.title = title;
            this.description = description ?? "";
            isChecked = value;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleName = title;
            AccessibleDescription = this.description;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        int TextWidth(int width) { return width - 2 * P(18) - P(44) - P(20); }

        public int Measure(int width)
        {
            int h = Theme.TextHeight(title, F(Face.Body), TextWidth(width));
            if (description.Length > 0) h += P(2) + Theme.TextHeight(description, F(Face.Caption), TextWidth(width));
            return Math.Max(P(58), h + 2 * P(15));
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; Focus(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new ToggleAccessible(this); }

        sealed class ToggleAccessible : ControlAccessibleObject
        {
            readonly ToggleRow row;
            public ToggleAccessible(ToggleRow owner) : base(owner) { row = owner; }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = AccessibleStates.Focusable;
                    if (row.Checked) s |= AccessibleStates.Checked;
                    if (row.Focused) s |= AccessibleStates.Focused;
                    return s;
                }
            }
            public override string DefaultAction { get { return "Toggle"; } }
            public override void DoDefaultAction() { row.Checked = !row.Checked; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            int pad = P(18);
            int textW = TextWidth(Width);
            int y = P(15);
            int th = Theme.TextHeight(title, F(Face.Body), textW);
            TextRenderer.DrawText(g, title, F(Face.Body), new Rectangle(pad, y, textW, th), Theme.Text, Theme.Wrap);
            if (description.Length > 0)
                TextRenderer.DrawText(g, description, F(Face.Caption),
                    new Rectangle(pad, y + th + P(2), textW, Height - y - th), Theme.Muted, Theme.Wrap);

            // The switch, centred on the first line of text.
            int sw = P(40), sh = P(22);
            int sx = Width - pad - sw;
            int sy = y + Math.Max(0, (th - sh) / 2);
            Rectangle track = new Rectangle(sx, sy, sw, sh);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Round(track, sh / 2))
            {
                if (isChecked) using (SolidBrush b = new SolidBrush(hot ? Theme.AccentHover : Theme.Accent)) g.FillPath(b, path);
                else using (Pen p = new Pen(Theme.Muted, Math.Max(1.5f, P(1)))) g.DrawPath(p, path);
            }
            int knob = isChecked ? P(14) : P(12);
            int kx = isChecked ? sx + sw - knob - P(5) : sx + P(5) + (P(14) - knob) / 2;
            using (SolidBrush b = new SolidBrush(isChecked ? Theme.OnAccent : Theme.Muted))
                g.FillEllipse(b, kx, sy + (sh - knob) / 2, knob, knob);
            if (Focused && ShowFocusCues)
                using (GraphicsPath ring = Theme.Round(Rectangle.Inflate(track, P(3), P(3)), sh / 2 + P(3)))
                using (Pen p = new Pen(Theme.AccentText, Math.Max(1.5f, P(2)))) g.DrawPath(p, ring);
        }
    }

    // A row with a label and explanation on the left and a control on the right.
    sealed class ValueRow : Surface, IFlow
    {
        readonly string title, description;
        readonly Control field;
        readonly int fieldWidth;

        public ValueRow(string title, string description, Control field, int fieldWidthAt96)
        {
            this.title = title;
            this.description = description ?? "";
            this.field = field;
            fieldWidth = fieldWidthAt96;
            TabStop = false;
            Controls.Add(field);
            field.AccessibleName = title;
        }

        int TextWidth(int width) { return width - 2 * P(18) - P(fieldWidth) - P(20); }

        public int Measure(int width)
        {
            int h = Theme.TextHeight(title, F(Face.Body), TextWidth(width));
            if (description.Length > 0) h += P(2) + Theme.TextHeight(description, F(Face.Caption), TextWidth(width));
            return Math.Max(P(58), Math.Max(h, field.Height) + 2 * P(15));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int w = P(fieldWidth);
            field.SetBounds(Width - P(18) - w, (Height - field.Height) / 2, w, field.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            int textW = TextWidth(Width);
            int y = P(15);
            int th = Theme.TextHeight(title, F(Face.Body), textW);
            TextRenderer.DrawText(g, title, F(Face.Body), new Rectangle(P(18), y, textW, th), Theme.Text, Theme.Wrap);
            if (description.Length > 0)
                TextRenderer.DrawText(g, description, F(Face.Caption),
                    new Rectangle(P(18), y + th + P(2), textW, Height - y - th), Theme.Muted, Theme.Wrap);
        }
    }

    enum ButtonKind { Primary, Secondary, Danger, Tab }

    // A flat button: filled in the accent colour, outlined, or red text.
    sealed class Btn : Surface
    {
        readonly ButtonKind kind;
        bool hot, down;
        public bool Selected;

        public Btn(string text, ButtonKind kind)
        {
            Text = text;
            this.kind = kind;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = text.Replace("&", "");
            Size = new Size(P(100), P(36));
        }

        public int PreferredWidth { get { return Theme.TextWidth(Text.Replace("&", ""), F(Face.BodyStrong)) + 2 * P(18); } }
        public int PreferredHeight { get { return P(36); } }

        public override Size GetPreferredSize(Size proposedSize) { return new Size(PreferredWidth, PreferredHeight); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Focus(); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, text, line = Color.Empty;
            if (kind == ButtonKind.Primary)
            {
                fill = down || hot ? Theme.AccentHover : Theme.Accent;
                text = Theme.OnAccent;
            }
            else if (kind == ButtonKind.Tab)
            {
                fill = Selected ? Theme.AccentSoft : (hot ? Theme.Hover : (Parent != null ? Parent.BackColor : Theme.Bg));
                text = Selected ? Theme.AccentText : Theme.Muted;
            }
            else
            {
                fill = down ? Theme.Divider : (hot ? Theme.Hover : Theme.Card);
                text = kind == ButtonKind.Danger ? Theme.Danger : Theme.Text;
                line = Theme.Border;
            }
            if (!Enabled) text = Theme.Muted;
            using (GraphicsPath path = Theme.Round(box, P(6)))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (line != Color.Empty) using (Pen p = new Pen(line)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text.Replace("&", ""), F(Face.BodyStrong), ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                using (GraphicsPath ring = Theme.Round(Rectangle.Inflate(box, -P(3), -P(3)), P(4)))
                using (Pen p = new Pen(kind == ButtonKind.Primary ? Theme.OnAccent : Theme.AccentText, Math.Max(1f, P(1)))) g.DrawPath(p, ring);
        }
    }

    // A one-line text box in a rounded frame, with a hint while it is empty.
    sealed class Field : Surface
    {
        public readonly TextBox Input = new TextBox();
        public string Placeholder = "";

        public Field(bool readOnly)
        {
            Input.BorderStyle = BorderStyle.None;
            Input.ReadOnly = readOnly;
            Input.BackColor = Theme.FieldBg;
            Input.ForeColor = Theme.Text;
            Controls.Add(Input);
            Input.Enter += delegate { Invalidate(); };
            Input.Leave += delegate { Invalidate(); };
            Input.TextChanged += delegate { Invalidate(); };
            Input.HandleCreated += delegate { Theme.StyleNative(Input); if (Placeholder.Length > 0) Theme.SetHint(Input, Placeholder); };
            BackColor = Theme.Bg;
            TabStop = false;
            Size = new Size(P(100), P(38));
        }

        public override Size GetPreferredSize(Size proposedSize) { return new Size(proposedSize.Width, P(38)); }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            Height = P(38);
            base.OnDpiChangedAfterParent(e);
        }

        public override string Text
        {
            get { return Input.Text; }
            set { Input.Text = value; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Font f = F(Face.Body);
            if (Input.Font != f) Input.Font = f;
            int h = Input.PreferredHeight;
            Input.SetBounds(P(12), (Height - h) / 2, Math.Max(10, Width - 2 * P(12)), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(box, P(6)))
            {
                using (SolidBrush b = new SolidBrush(Theme.FieldBg)) g.FillPath(b, path);
                using (Pen p = new Pen(Input.Focused ? Theme.Accent : Theme.Border, Input.Focused ? Math.Max(1.5f, P(2)) : 1f)) g.DrawPath(p, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Input.Focus(); }
    }

    // A list in a rounded frame with comfortable rows and a message when empty.
    sealed class ListFrame : Surface
    {
        public readonly ListBox List = new ListBox();
        readonly string emptyText;
        bool isEmpty;

        public ListFrame(string emptyText)
        {
            this.emptyText = emptyText;
            List.BorderStyle = BorderStyle.None;
            List.IntegralHeight = false;
            List.DrawMode = DrawMode.OwnerDrawFixed;
            List.BackColor = Theme.Card;
            List.ForeColor = Theme.Text;
            List.DrawItem += DrawItem;
            List.HandleCreated += delegate { Theme.StyleNative(List); };
            Controls.Add(List);
            TabStop = false;
        }

        // The list is hidden while it has nothing, and the frame says so instead.
        public void UpdateEmpty()
        {
            bool now = List.Items.Count == 0;
            if (now == isEmpty && List.Visible == !now) return;
            isEmpty = now;
            List.Visible = !now;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = P(6);
            List.ItemHeight = P(32);
            List.Font = F(Face.Body);
            List.SetBounds(pad, pad, Width - 2 * pad, Height - 2 * pad);
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Rectangle row = new Rectangle(e.Bounds.X + P(2), e.Bounds.Y + P(1), e.Bounds.Width - P(4), e.Bounds.Height - P(2));
            using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, e.Bounds);
            if (selected)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Theme.Round(row, P(5)))
                using (SolidBrush b = new SolidBrush(Theme.AccentSoft)) g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, Convert.ToString(List.Items[e.Index]), List.Font,
                new Rectangle(row.X + P(10), row.Y, row.Width - P(14), row.Height),
                selected ? Theme.AccentText : Theme.Text, Theme.OneLine);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), P(8)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
            if (isEmpty)
                TextRenderer.DrawText(g, emptyText, F(Face.Body), new Rectangle(P(24), 0, Width - P(48), Height), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // A drop-down list in the same style as the other fields.
    sealed class Dropdown : Surface
    {
        public readonly List<string> Items = new List<string>();
        int selected = -1;
        bool hot;

        public event EventHandler SelectedIndexChanged;

        public Dropdown()
        {
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.ComboBox;
            Size = new Size(P(100), P(38));
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < 0 || value >= Items.Count || value == selected) return;
                selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        // Sets the shown choice without raising the changed event.
        public void Choose(int index)
        {
            if (index < 0 || index >= Items.Count) return;
            selected = index;
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize) { return new Size(proposedSize.Width, P(38)); }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            Height = P(38);
            base.OnDpiChangedAfterParent(e);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); ShowMenu(); }
        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) { SelectedIndex = selected + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { SelectedIndex = selected - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { ShowMenu(); e.Handled = true; }
        }

        void ShowMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.ShowImageMargin = false;
            menu.BackColor = Theme.Card;
            menu.Font = F(Face.Body);
            menu.MinimumSize = new Size(Width, 0);
            for (int i = 0; i < Items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(Items[i]);
                item.Font = F(i == selected ? Face.BodyStrong : Face.Body);
                item.Padding = new Padding(P(6), P(7), P(6), P(7));
                item.Click += delegate { SelectedIndex = index; };
                menu.Items.Add(item);
            }
            menu.Closed += delegate { menu.Dispose(); };
            menu.Show(this, new Point(0, Height + P(2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            bool focus = Focused && ShowFocusCues;
            using (GraphicsPath path = Theme.Round(box, P(6)))
            {
                using (SolidBrush b = new SolidBrush(hot ? Theme.Hover : Theme.FieldBg)) g.FillPath(b, path);
                using (Pen p = new Pen(focus ? Theme.Accent : Theme.Border, focus ? Math.Max(1.5f, P(2)) : 1f)) g.DrawPath(p, path);
            }
            string text = selected >= 0 && selected < Items.Count ? Items[selected] : "";
            TextRenderer.DrawText(g, text, F(Face.Body), new Rectangle(P(12), 0, Width - P(40), Height), Theme.Text, Theme.OneLine);
            int cx = Width - P(20), cy = Height / 2;
            using (Pen chevron = new Pen(Theme.Muted, Math.Max(1.5f, P(2))))
            {
                chevron.StartCap = chevron.EndCap = LineCap.Round;
                chevron.LineJoin = LineJoin.Round;
                g.DrawLines(chevron, new Point[] { new Point(cx - P(4), cy - P(2)), new Point(cx, cy + P(2)), new Point(cx + P(4), cy - P(2)) });
            }
        }
    }

    // One entry of the side menu.
    sealed class NavItem : Surface, IFlow
    {
        public readonly string Caption, Glyph;
        public bool Selected;
        bool hot;

        public NavItem(string caption, string glyph)
        {
            Caption = caption;
            Glyph = glyph;
            BackColor = Theme.Sidebar;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PageTab;
            AccessibleName = caption;
        }

        public int Measure(int width) { return P(38); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                Control next = Parent == null ? null : Parent.GetNextControl(this, e.KeyCode == Keys.Down);
                NavItem item = next as NavItem;
                if (item != null) { item.Focus(); item.OnClick(EventArgs.Empty); }
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Sidebar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle pill = new Rectangle(P(10), P(2), Width - P(20), Height - P(4));
            if (Selected || hot)
                using (GraphicsPath path = Theme.Round(pill, P(6)))
                using (SolidBrush b = new SolidBrush(Selected ? Theme.AccentSoft : Theme.Hover)) g.FillPath(b, path);
            if (Selected)
                using (GraphicsPath bar = Theme.Round(new Rectangle(P(10), Height / 2 - P(9), P(3), P(18)), P(2)))
                using (SolidBrush b = new SolidBrush(Theme.AccentText)) g.FillPath(b, bar);
            Color text = Selected ? Theme.AccentText : Theme.Text;
            int x = P(28);
            if (Glyph != null && Theme.HasIcons)
            {
                TextRenderer.DrawText(g, Glyph, F(Face.Icon), new Rectangle(x, 0, P(22), Height), text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                x += P(34);
            }
            TextRenderer.DrawText(g, Caption, F(Selected ? Face.BodyStrong : Face.Nav), new Rectangle(x, 0, Width - x - P(12), Height), text, Theme.OneLine);
            if (Focused && ShowFocusCues)
                using (GraphicsPath ring = Theme.Round(Rectangle.Inflate(pill, -P(1), -P(1)), P(5)))
                using (Pen p = new Pen(Theme.AccentText, Math.Max(1f, P(1)))) g.DrawPath(p, ring);
        }
    }

    // Big numbers with a caption: the statistics.
    sealed class TileRow : Surface, IFlow
    {
        readonly string[] captions;
        string[] values;

        public TileRow(string[] captions)
        {
            this.captions = captions;
            values = new string[captions.Length];
            for (int i = 0; i < values.Length; i++) values[i] = "0";
            TabStop = false;
            AccessibleRole = AccessibleRole.Table;
        }

        public void SetValues(string[] now)
        {
            bool same = now.Length == values.Length;
            for (int i = 0; same && i < now.Length; i++) same = now[i] == values[i];
            if (same) return;
            values = now;
            AccessibleDescription = string.Join(", ", captions) + ": " + string.Join(", ", values);
            Invalidate();
        }

        public int Measure(int width) { return P(96); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int gap = P(12), n = captions.Length;
            int w = (Width - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                Rectangle tile = new Rectangle(i * (w + gap), 0, w - 1, Height - 1);
                using (GraphicsPath path = Theme.Round(tile, P(8)))
                {
                    using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                    using (Pen p = new Pen(Theme.Border)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, values[i], F(Face.Stat), new Rectangle(tile.X + P(18), tile.Y + P(12), tile.Width - P(30), P(44)),
                    Theme.Text, Theme.OneLine);
                TextRenderer.DrawText(g, captions[i], F(Face.Caption), new Rectangle(tile.X + P(18), tile.Y + P(58), tile.Width - P(30), P(26)),
                    Theme.Muted, Theme.Wrap);
            }
        }
    }

    // The logo at any size.
    sealed class LogoView : Surface
    {
        public LogoView()
        {
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = TrayApp.AppName + " logo";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawLogo(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Color.FromArgb(22, 163, 74));
        }
    }

    // The state line at the foot of the side menu: a dot and one sentence.
    sealed class StatusView : Surface
    {
        string text = "";
        Color dot = Color.Gray;

        public StatusView() { BackColor = Theme.Sidebar; TabStop = false; AccessibleRole = AccessibleRole.StaticText; }

        public void Set(string value, Color colour)
        {
            if (value == text && colour == dot) return;
            text = value;
            dot = colour;
            AccessibleName = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Sidebar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = P(9);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, P(28), (Height - d) / 2, d, d);
            TextRenderer.DrawText(g, text, F(Face.Caption), new Rectangle(P(46), 0, Width - P(56), Height), Theme.Muted, Theme.OneLine);
        }
    }

    // The page title and one line under it.
    sealed class PageHeader : Surface
    {
        string title = "", subtitle = "";

        public PageHeader() { TabStop = false; AccessibleRole = AccessibleRole.TitleBar; }

        public void Set(string newTitle, string newSubtitle)
        {
            title = newTitle;
            subtitle = newSubtitle;
            AccessibleName = newTitle;
            AccessibleDescription = newSubtitle;
            Height = HeightFor(Width);
            Invalidate();
        }

        public int HeightFor(int width)
        {
            int inner = Math.Max(10, width - 2 * P(36));
            return P(28) + Theme.TextHeight(title, F(Face.Title), inner) + P(6) + Theme.TextHeight(subtitle, F(Face.Body), inner) + P(22);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = HeightFor(Width);
            if (h != Height) Height = h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            int inner = Width - 2 * P(36);
            int th = Theme.TextHeight(title, F(Face.Title), inner);
            TextRenderer.DrawText(g, title, F(Face.Title), new Rectangle(P(36), P(28), inner, th), Theme.Text, Theme.Wrap);
            TextRenderer.DrawText(g, subtitle, F(Face.Body), new Rectangle(P(36), P(28) + th + P(6), inner, Height - th - P(34)), Theme.Muted, Theme.Wrap);
        }
    }

    // The tray menu in the same colours and type.
    sealed class MenuColors : ProfessionalColorTable
    {
        public MenuColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.AccentSoft; } }
        public override Color MenuItemSelected { get { return Theme.AccentSoft; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.AccentSoft; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.AccentSoft; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Divider; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
        public override Color CheckBackground { get { return Theme.AccentSoft; } }
        public override Color CheckSelectedBackground { get { return Theme.AccentSoft; } }
        public override Color CheckPressedBackground { get { return Theme.AccentSoft; } }
    }

    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Theme.AccentText : Theme.Text) : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }
    }
}
