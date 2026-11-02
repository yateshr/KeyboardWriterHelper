using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardWriterHelper
{
    // ---- Floating Candidate Popup ----

    sealed class CandidatePopup : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        string currentFix = "";
        string[] currentChoices = new string[0];
        bool isPredictionMode;
        string predictionWord = "";
        string predictionPrefix = "";

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
            BackColor = Color.FromArgb(32, 34, 38);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            // Font.Height grows with Windows scaling; the pill grows with it.
            Size = new Size(320, Math.Max(36, Font.Height + 16));
            AccessibleName = "Word Suggestions";
            AccessibleRole = AccessibleRole.ToolTip;

            hideTimer.Interval = 2500;
            hideTimer.Tick += delegate { Hide(); hideTimer.Stop(); };
        }

        public void ShowCandidates(string fix, string[] choices, IntPtr foreground)
        {
            if (choices == null || choices.Length == 0) { Hide(); return; }
            isPredictionMode = false;
            currentFix = fix;
            currentChoices = choices;
            AccessibleDescription = string.Join(", ", choices);
            hideTimer.Stop();
            hideTimer.Start();

            using (Graphics g = CreateGraphics())
            {
                int totalWidth = 24;
                for (int i = 0; i < choices.Length; i++)
                {
                    string text = (i + 1) + ". " + choices[i];
                    SizeF sz = g.MeasureString(text, Font);
                    totalWidth += (int)sz.Width + 16;
                }
                totalWidth += 40;
                Width = Math.Max(240, Math.Min(totalWidth, 600));
            }
            PositionPopup(foreground);
            Invalidate();
            if (!Visible) Show();
        }
        public void ShowPrediction(string prediction, string prefix, IntPtr foreground)
        {
            if (string.IsNullOrEmpty(prediction)) { Dismiss(); return; }
            isPredictionMode = true;
            predictionWord = prediction;
            predictionPrefix = prefix ?? "";
            AccessibleDescription = "Tab to accept prediction: " + prediction;
            hideTimer.Stop();
            hideTimer.Start();

            using (Graphics g = CreateGraphics())
            {
                SizeF szTab = g.MeasureString("⇥ Tab", Font);
                SizeF szWord = g.MeasureString(prediction, Font);
                Width = Math.Max(180, (int)(szTab.Width + szWord.Width + 56));
            }

            PositionPopup(foreground);
            Invalidate();
            if (!Visible) Show();
        }

        // Always the top right corner of the screen being typed on, so it is in the
        // same place every time and never covers the text.
        void PositionPopup(IntPtr foreground)
        {
            Screen scr = foreground != IntPtr.Zero ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position);
            Rectangle area = scr.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Top + 20);
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

            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 30, 34)))
                    g.FillPath(bg, path);
                using (Pen border = new Pen(Color.FromArgb(60, 64, 72), 1))
                    g.DrawPath(border, path);
            }

            int x = 12;
            int y = (Height - Font.Height) / 2;
            if (isPredictionMode)
            {
                string tabBadge = "⇥ Tab";
                SizeF tabSz = g.MeasureString(tabBadge, Font);
                Rectangle pill = new Rectangle(x - 4, 4, (int)tabSz.Width + 8, Height - 8);
                using (GraphicsPath p = RoundedRect(pill, 4))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    g.FillPath(b, p);
                using (SolidBrush txt = new SolidBrush(Color.White))
                    g.DrawString(tabBadge, Font, txt, x, y);

                x += (int)tabSz.Width + 14;
                using (SolidBrush wordBrush = new SolidBrush(Color.FromArgb(240, 243, 246)))
                    g.DrawString(predictionWord, Font, wordBrush, x, y);

                string esc = "Esc";
                using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(140, 144, 152)))
                    g.DrawString(esc, Font, hintBrush, Width - 32, y);
                return;
            }

            for (int i = 0; i < currentChoices.Length && x < Width - 40; i++)
            {
                string word = currentChoices[i];
                bool active = string.Equals(word, currentFix, StringComparison.OrdinalIgnoreCase);
                string text = (i + 1) + ". " + word;
                SizeF sz = g.MeasureString(text, Font);

                if (active)
                {
                    Rectangle pill = new Rectangle(x - 4, 4, (int)sz.Width + 8, Height - 8);
                    using (GraphicsPath p = RoundedRect(pill, 4))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(22, 163, 74)))
                        g.FillPath(b, p);
                    using (SolidBrush txt = new SolidBrush(Color.White))
                        g.DrawString(text, Font, txt, x, y);
                }
                else
                {
                    using (SolidBrush txt = new SolidBrush(Color.FromArgb(180, 184, 192)))
                        g.DrawString(text, Font, txt, x, y);
                }
                x += (int)sz.Width + 14;
            }

            string hint = "⌫";
            using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(180, 184, 192)))
                g.DrawString(hint, Font, hintBrush, Width - 22, y);
        }

        static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
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
