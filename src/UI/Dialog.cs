using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KeyboardWriterHelper
{
    // ---- Message dialogs in the look of the app ----

    // Replaces the grey Windows message box: same colours and type as the
    // settings window, Enter for the main button and Esc for the other.
    static class Dialog
    {
        // True when the main button was chosen. no = null gives a single OK-style button.
        public static bool Ask(IWin32Window owner, string title, string message, string notes, string yes, string no)
        {
            using (DialogForm form = new DialogForm(title, message, notes, yes, no))
                return form.ShowDialog(owner) == DialogResult.Yes;
        }

        public static void Tell(IWin32Window owner, string title, string message)
        {
            Ask(owner, title, message, null, "OK", null);
        }
    }

    // A rounded box with a few lines of smaller text: release notes.
    sealed class NoteBox : Surface
    {
        readonly string text;

        public NoteBox(string text) { this.text = text; TabStop = false; AccessibleRole = AccessibleRole.StaticText; AccessibleName = text; }

        public int Measure(int width) { return Theme.TextHeight(text, F(Face.Caption), width - 2 * P(14)) + 2 * P(12); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), P(8)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, text, F(Face.Caption), new Rectangle(P(14), P(12), Width - 2 * P(14), Height - 2 * P(12)),
                Theme.Muted, Theme.Wrap);
        }
    }

    sealed class DialogForm : Form
    {
        readonly TextBlock heading, body;
        readonly NoteBox notes;
        readonly Btn yes, no;

        int P(int pixelsAt96) { return Theme.Px(this, pixelsAt96); }

        public DialogForm(string title, string message, string notesText, string yesText, string noText)
        {
            Theme.Load();
            AutoScaleMode = AutoScaleMode.None;
            Text = TrayApp.AppName;
            Font = Theme.Get(Face.Body, DeviceDpi);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            heading = new TextBlock(title, Face.Heading, null);
            body = new TextBlock(message, Face.Body, null);
            if (!string.IsNullOrEmpty(notesText)) notes = new NoteBox(notesText);
            yes = new Btn(yesText, ButtonKind.Primary);
            yes.Click += delegate { DialogResult = DialogResult.Yes; };
            Controls.Add(heading);
            Controls.Add(body);
            if (notes != null) Controls.Add(notes);
            Controls.Add(yes);
            if (noText != null)
            {
                no = new Btn(noText, ButtonKind.Secondary);
                no.Click += delegate { DialogResult = DialogResult.No; };
                Controls.Add(no);
            }
            ClientSize = new Size(P(500), P(200));
            LayoutAll();
        }

        void LayoutAll()
        {
            int pad = P(28), width = P(500) - 2 * pad;
            int y = P(26);
            int h = heading.Measure(width);
            heading.SetBounds(pad, y, width, h);
            y += h + P(10);
            h = body.Measure(width);
            body.SetBounds(pad, y, width, h);
            y += h;
            if (notes != null)
            {
                y += P(16);
                h = notes.Measure(width);
                notes.SetBounds(pad, y, width, h);
                y += h;
            }
            y += P(26);
            int bh = P(36), gap = P(10);
            int x = P(500) - pad - yes.PreferredWidth;
            yes.SetBounds(x, y, yes.PreferredWidth, bh);
            if (no != null)
            {
                x -= gap + no.PreferredWidth;
                no.SetBounds(x, y, no.PreferredWidth, bh);
            }
            ClientSize = new Size(P(500), y + bh + P(24));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            yes.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { DialogResult = no != null ? DialogResult.No : DialogResult.Yes; e.Handled = true; }
        }
    }
}
