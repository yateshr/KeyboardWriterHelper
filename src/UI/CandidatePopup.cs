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

            Size = new Size(320, 36);

            AccessibleName = "Word Suggestions";

            AccessibleRole = AccessibleRole.ToolTip;

    

            hideTimer.Interval = 2500;

            hideTimer.Tick += delegate { Hide(); hideTimer.Stop(); };

        }

    

        public void ShowCandidates(string fix, string[] choices, IntPtr foreground)

        {

            if (choices == null || choices.Length == 0) { Hide(); return; }

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

    

            Point target = Point.Empty;

            bool foundCaret = false;

            if (foreground != IntPtr.Zero)

            {

                uint tid = Native.GetWindowThreadProcessId(foreground, IntPtr.Zero);

                Native.GUITHREADINFO gui = new Native.GUITHREADINFO();

                gui.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));

                if (Native.GetGUIThreadInfo(tid, ref gui) && gui.hwndCaret != IntPtr.Zero && gui.rcCaret.Right > gui.rcCaret.Left)

                {

                    Native.POINT pt = new Native.POINT { X = gui.rcCaret.Left, Y = gui.rcCaret.Bottom + 4 };

                    if (Native.ClientToScreen(gui.hwndCaret, ref pt))

                    {

                        target = new Point(pt.X, pt.Y);

                        foundCaret = true;

                    }

                }

            }

    

            Screen scr = Screen.FromPoint(foundCaret ? target : (foreground != IntPtr.Zero ? Screen.FromHandle(foreground).WorkingArea.Location : Cursor.Position));

            if (foundCaret)

            {

                int left = Math.Max(scr.WorkingArea.Left + 5, Math.Min(target.X, scr.WorkingArea.Right - Width - 5));

                int top = target.Y;

                if (top + Height > scr.WorkingArea.Bottom)

                    top = Math.Max(scr.WorkingArea.Top + 5, target.Y - Height - 24);

                Location = new Point(left, top);

            }

            else

            {

                Native.RECT rect;

                if (foreground != IntPtr.Zero && Native.GetWindowRect(foreground, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)

                {

                    int left = Math.Max(scr.WorkingArea.Left + 10, Math.Min(rect.Right - Width - 20, scr.WorkingArea.Right - Width - 10));

                    int top = Math.Max(scr.WorkingArea.Top + 10, Math.Min(rect.Bottom - Height - 20, scr.WorkingArea.Bottom - Height - 10));

                    Location = new Point(left, top);

                }

                else

                {

                    Location = new Point(scr.WorkingArea.Right - Width - 20, scr.WorkingArea.Bottom - Height - 20);

                }

            }

    

            Invalidate();

            if (!Visible) Show();

        }

    

        public void Dismiss()

        {

            hideTimer.Stop();

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

