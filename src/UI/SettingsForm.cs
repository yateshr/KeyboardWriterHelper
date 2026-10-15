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

    // ---- Settings window ----

    

    sealed class SettingsForm : Form

    {

        const string Arrow = "  →  ";

        // Usable width and left edge inside a tab page.

        const int Left0 = 12, Wide = 520;

    

        readonly TrayApp app;

        readonly Label statsLabel = new Label(), aboutLabel = new Label();

        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();

        readonly ListBox wordsList = new ListBox(), fixesList = new ListBox(), appsList = new ListBox(), typosList = new ListBox();

        readonly RadioButton frequentTypos = new RadioButton(), recentTypos = new RadioButton();

        string[] shownWords = new string[0], shownFixes = new string[0], shownApps = new string[0], shownTypos = new string[0];

        // { typed, fix } behind each line of the typos list.

        string[][] typoItems = new string[0][];

    

        public SettingsForm(TrayApp app)

        {

            this.app = app;

            Text = TrayApp.AppName + " - settings";

            StartPosition = FormStartPosition.CenterScreen;

            ClientSize = new Size(Wide + 2 * Left0 + 8, 440);

            FormBorderStyle = FormBorderStyle.FixedDialog;

            MaximizeBox = false;

            Font = new Font("Segoe UI", 9f);

    

            TabControl tabs = new TabControl();

            tabs.Dock = DockStyle.Fill;

            tabs.TabPages.Add(GeneralTab());

            tabs.TabPages.Add(CorrectionsTab());

            tabs.TabPages.Add(ListTab("My words", wordsList,

                "Words listed here are never auto-corrected.", "&Add word", AddWord, RemoveWord, null));

            tabs.TabPages.Add(ListTab("My fixes", fixesList,

                "Your own fixes, shortcuts and snippets:  typo=correction . " +

                "Supports {date}, {time}, {datetime}, {clip} (e.g. now={time}, today={date}, myclip={clip}).",

                "&Add fix", AddFix, RemoveFix, null));

            tabs.TabPages.Add(ListTab("Programs", appsList,

                "Nothing typed in these programs is corrected. The tray icon turns amber while one of them is in front.",

                "&Add program", AddProgram, RemoveProgram, "Add the program in front (now)"));

            tabs.TabPages.Add(TyposTab());

            tabs.TabPages.Add(StatsTab());

            Controls.Add(tabs);

    

            refresh.Interval = 1000;

            refresh.Tick += delegate { Reload(); };

            Shown += delegate { Reload(); refresh.Start(); };

            FormClosed += delegate { refresh.Stop(); };

        }

    

        TabPage GeneralTab()

        {

            TabPage page = new TabPage("General");

            CheckBox on = Check("Auto-correct is on", app.Enabled, delegate(bool v) { app.Enabled = v; });

            on.SetBounds(Left0, 12, Wide, 24);

            CheckBox startup = Check("Start with Windows", TrayApp.IsStartupEnabled(), delegate(bool v) { app.SetStartup(v); });

            startup.SetBounds(Left0, 40, Wide, 24);

    

            TextBox pauseBox = HotkeyBox(

                delegate { return app.Settings.PauseHotkey; }, delegate(Hotkey h) { app.Settings.PauseHotkey = h; },

                delegate { return app.Settings.NextHotkey; });

            pauseBox.SetBounds(Left0 + 230, 84, 150, 24);

            TextBox nextBox = HotkeyBox(

                delegate { return app.Settings.NextHotkey; }, delegate(Hotkey h) { app.Settings.NextHotkey = h; },

                delegate { return app.Settings.PauseHotkey; });

            nextBox.SetBounds(Left0 + 230, 114, 150, 24);

    

            ComboBox language = new ComboBox();

            language.DropDownStyle = ComboBoxStyle.DropDownList;

            language.SetBounds(Left0 + 230, 196, 230, 24);

            string[] installed = app.InstalledLanguages;

            foreach (string tag in installed) language.Items.Add(LanguageName(tag));

            int chosen = Array.FindIndex(installed, delegate(string tag) { return string.Equals(tag, app.Settings.MainLanguage, StringComparison.OrdinalIgnoreCase); });

            language.SelectedIndex = Math.Max(0, chosen);

            language.SelectedIndexChanged += delegate

            {

                app.Settings.MainLanguage = installed[language.SelectedIndex];

                SpellingChanged();

            };

            CheckBox variants = Check("The other English spellings are correct too (colour and color are both left alone)",

                app.Settings.AcceptVariants, delegate(bool v) { app.Settings.AcceptVariants = v; SpellingChanged(); });

            variants.SetBounds(Left0, 226, Wide, 24);

    

            ComboBox layoutBox = new ComboBox();

            layoutBox.DropDownStyle = ComboBoxStyle.DropDownList;

            layoutBox.SetBounds(Left0 + 230, 256, 160, 24);

            layoutBox.Items.AddRange(new object[] { "Auto", "QWERTY", "QWERTZ", "AZERTY", "Dvorak", "Colemak" });

            int layoutIdx = layoutBox.FindStringExact(app.Settings.KeyboardLayout);

            layoutBox.SelectedIndex = layoutIdx >= 0 ? layoutIdx : 0;

            layoutBox.SelectedIndexChanged += delegate

            {

                app.Settings.KeyboardLayout = (string)layoutBox.SelectedItem;

                SpellingChanged();

            };

    

            aboutLabel.SetBounds(Left0, 340, Wide, 40);

            aboutLabel.ForeColor = SystemColors.GrayText;

            ShowAbout();

    

            page.Controls.AddRange(new Control[] {

                on, startup,

                Caption("Shortcut to pause and resume:", 87), pauseBox,

                Caption("Shortcut for the next suggestion:", 117), nextBox,

                Note("To change a shortcut, click its box and press the new keys (with Ctrl or Alt). " +

                     "The next-suggestion shortcut works straight after a fix: it swaps in the next-best word.", 144, 36),

                Caption("Suggestions use the spelling of:", 199), language, variants,

                Caption("Keyboard layout proximity:", 259), layoutBox, aboutLabel });

            return page;

        }

    

        void SpellingChanged()

        {

            app.Settings.Save();

            app.ApplySpelling();

            ShowAbout();

        }

    

        void ShowAbout()

        {

            aboutLabel.Text = TrayApp.AppName + " " + Program.Version + "\r\nDictionaries in use: " + app.Languages;

        }

    

        static string LanguageName(string tag)

        {

            try { return CultureInfo.GetCultureInfo(tag).EnglishName; }

            catch (CultureNotFoundException) { return tag; }

        }

    

        static Label Caption(string text, int top)

        {

            Label label = new Label();

            label.Text = text;

            label.SetBounds(Left0, top, 226, 20);

            return label;

        }

    

        static Label Note(string text, int top, int height)

        {

            Label label = new Label();

            label.Text = text;

            label.ForeColor = SystemColors.GrayText;

            label.SetBounds(Left0, top, Wide, height);

            return label;

        }

    

        // Shows a shortcut and takes a new one when keys are pressed in it.

        // other = the shortcut it must not be the same as.

        TextBox HotkeyBox(Func<Hotkey> get, Action<Hotkey> set, Func<Hotkey> other)

        {

            TextBox box = new TextBox();

            box.ReadOnly = true;

            box.BackColor = SystemColors.Window;

            box.Text = get().ToString();

            box.KeyDown += delegate(object sender, KeyEventArgs e)

            {

                e.SuppressKeyPress = true;

                Hotkey pressed = new Hotkey(e.Control, e.Alt, e.Shift, e.KeyCode);

                if (!pressed.IsValid || pressed.Same(other())) return;

                set(pressed);

                app.Settings.Save();

                app.ShowState();

                box.Text = pressed.ToString();

            };

            return box;

        }

    

        TabPage CorrectionsTab()

        {

            TabPage page = new TabPage("Corrections");

            FlowLayoutPanel flow = new FlowLayoutPanel();

            flow.Dock = DockStyle.Fill;

            flow.FlowDirection = FlowDirection.TopDown;

            flow.WrapContents = false;

            flow.Padding = new Padding(Left0 - 3, 8, 0, 0);

            Settings o = app.Settings;

            flow.Controls.Add(Check("Fix a word as soon as , ; : ! or ? is typed after it (otherwise: when Space is pressed)",

                o.FixOnPunctuation, delegate(bool v) { o.FixOnPunctuation = v; o.Save(); }));

            flow.Controls.Add(Check("Capitalise the first word of a sentence (after . ! or ? and a space)",

                o.CapitalizeSentences, delegate(bool v) { o.CapitalizeSentences = v; o.Save(); }));

            flow.Controls.Add(Check("Fix two capitals at the start of a word (THe → The)",

                o.FixTwoCapitals, delegate(bool v) { o.FixTwoCapitals = v; o.Save(); }));

            flow.Controls.Add(Check("Fix real words in the wrong place (their is → there is, more then → more than)",

                o.FixPhrases, delegate(bool v) { o.FixPhrases = v; o.Save(); }));

            flow.Controls.Add(Check("Let the word before help choose the fix when several words are possible",

                o.UseContext, delegate(bool v) { o.UseContext = v; SpellingChanged(); }));

            flow.Controls.Add(Check("Turn two spaces after a word into a full stop and a space",

                o.DoubleSpacePeriod, delegate(bool v) { o.DoubleSpacePeriod = v; o.Save(); }));

            flow.Controls.Add(Check("Smart typography: dashes (-- to —), ellipsis (... to …), and arrows (-> to →, => to ⇒)",

                o.SmartDashes, delegate(bool v) { o.SmartDashes = o.SmartArrows = o.SmartEllipsis = v; o.Save(); }));

            flow.Controls.Add(Check("Skip correction in fullscreen games and exclusive-mode programs",

                o.SkipFullscreenGames, delegate(bool v) { o.SkipFullscreenGames = v; o.Save(); }));

            flow.Controls.Add(Check("Show floating candidate pill when cycling suggestions",

                o.ShowCandidatePopup, delegate(bool v) { o.ShowCandidatePopup = v; o.Save(); }));

            Label note = Note(

                "Backspace right after a fix undoes it. Undo the same fix twice and the word is added to My words.\r\n\r\n" +

                "A full stop never triggers a fix by itself (example.com is not a sentence); the next Space does.", 0, 80);

            note.Margin = new Padding(3, 14, 3, 3);

            flow.Controls.Add(note);

            page.Controls.Add(flow);

            return page;

        }

    

        static CheckBox Check(string text, bool value, Action<bool> changed)

        {

            CheckBox box = new CheckBox();

            box.Text = text;

            box.Checked = value;

            box.AutoSize = false;

            box.Size = new Size(Wide, 30);

            box.CheckedChanged += delegate { changed(box.Checked); };

            return box;

        }

    

        // add returns false when the text was not usable (it then stays in the box).

        TabPage ListTab(string title, ListBox list, string help, string addLabel, Func<string, bool> add, Action<string> remove, string addNowLabel)

        {

            TabPage page = new TabPage(title);

            Label label = new Label();

            label.SetBounds(Left0, 10, Wide, 48);

            label.Text = help;

            list.SetBounds(Left0, 62, Wide, 250);

            list.IntegralHeight = false;

            list.SelectionMode = SelectionMode.MultiExtended;

            list.TabIndex = 0;

            TextBox input = new TextBox();

            input.SetBounds(Left0, 324, Wide - 190, 24);

            input.TabIndex = 1;

            Button addButton = new Button();

            addButton.Text = addLabel;

            addButton.SetBounds(Left0 + Wide - 184, 322, 100, 27);

            addButton.TabIndex = 2;

            Action doAdd = delegate

            {

                string text = input.Text.Trim();

                if (text.Length == 0 || !add(text)) return;

                input.Text = "";

                Reload();

            };

            addButton.Click += delegate { doAdd(); };

            input.KeyDown += delegate(object s, KeyEventArgs e)

            {

                if (e.KeyCode != Keys.Enter) return;

                e.SuppressKeyPress = true;

                doAdd();

            };

            Button removeButton = new Button();

            removeButton.Text = "&Remove";

            removeButton.SetBounds(Left0 + Wide - 78, 322, 78, 27);

            removeButton.TabIndex = 3;

            removeButton.Click += delegate

            {

                foreach (object item in new ArrayList(list.SelectedItems)) remove((string)item);

                Reload();

            };

            page.Controls.AddRange(new Control[] { label, list, input, addButton, removeButton });

            if (addNowLabel != null)

            {

                Button now = new Button();

                now.Text = addNowLabel;

                now.SetBounds(Left0, 358, 240, 27);

                now.Click += delegate

                {

                    if (app.CurrentApp.Length > 0) add(app.CurrentApp);

                    Reload();

                };

                page.Controls.Add(now);

            }

            return page;

        }

    

        TabPage TyposTab()

        {

            TabPage page = new TabPage("Typos");

            frequentTypos.Text = "Most frequent";

            frequentTypos.Checked = true;

            frequentTypos.SetBounds(Left0, 10, 130, 24);

            recentTypos.Text = "Most recent";

            recentTypos.SetBounds(Left0 + 136, 10, 130, 24);

            frequentTypos.CheckedChanged += delegate { Reload(); };

            typosList.SetBounds(Left0, 40, Wide, 240);

            typosList.IntegralHeight = false;

    

            Button always = new Button();

            always.Text = "&Always fix it this way";

            always.SetBounds(Left0, 290, 170, 27);

            always.Click += delegate

            {

                string[] item = SelectedTypo();

                if (item == null) return;

                app.AlwaysFix(item[0], item[1]);

                Reload();

            };

            Button never = new Button();

            never.Text = "&Never correct it";

            never.SetBounds(Left0 + 178, 290, 140, 27);

            never.Click += delegate

            {

                string[] item = SelectedTypo();

                if (item == null) return;

                app.NeverCorrect(item[0]);

                Reload();

            };

            CheckBox remember = Check("Keep the counts between sessions (in a file on this PC only; unticking deletes it)",

                app.Settings.RememberTypos, delegate(bool v) { app.SetRememberTypos(v); });

            remember.SetBounds(Left0, 328, Wide, 24);

            page.Controls.AddRange(new Control[] {

                frequentTypos, recentTypos, typosList, always, never, remember,

                Note("These are the fixes the app made. Unless the box above is ticked they are kept in memory only " +

                     "and are gone when the app closes.", 356, 36) });

            return page;

        }

    

        string[] SelectedTypo()

        {

            int i = typosList.SelectedIndex;

            return i >= 0 && i < typoItems.Length ? typoItems[i] : null;

        }

    

        TabPage StatsTab()

        {

            TabPage page = new TabPage("Statistics");

            statsLabel.SetBounds(Left0, 10, Wide, 230);

            Button open = new Button();

            open.Text = "Open the data folder";

            open.SetBounds(Left0, 250, 170, 27);

            open.Click += delegate { try { Process.Start(new ProcessStartInfo { FileName = app.DataFolder, UseShellExecute = true }); } catch { } };

            page.Controls.AddRange(new Control[] {

                statsLabel, open,

                Note("The folder holds your settings, My words, My fixes, the program list and these counters. " +

                     "The lists are plain text: edits made in Notepad are picked up within a second.", 284, 36) });

            return page;

        }

    

        bool AddWord(string text)

        {

            foreach (string w in text.Split(new char[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))

                app.MyWords.Add(w);

            app.MyWords.Sync();

            return true;

        }

    

        void RemoveWord(string word) { app.MyWords.Remove(word); app.MyWords.Sync(); }

    

        bool AddFix(string text)

        {

            int eq = text.IndexOf('=');

            string typed = eq > 0 ? text.Substring(0, eq).Trim() : "", fix = eq > 0 ? text.Substring(eq + 1).Trim() : "";

            if (!IsTypable(typed) || fix.Length == 0)

            {

                MessageBox.Show(this,

                    "Write it as  typo=correction , for example  brb=be right back .\r\n\r\n" +

                    "The left side is one word or two words, made of letters.",

                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

                return false;

            }

            app.AlwaysFix(typed, fix);

            return true;

        }

    

        // One word or two, made of letters (an apostrophe inside is fine): only

        // that can be recognised as it is typed.

        static bool IsTypable(string text)

        {

            string[] words = text.Split(' ');

            if (words.Length > 2) return false;

            foreach (string w in words)

            {

                if (w.Length == 0 || !char.IsLetter(w[0])) return false;

                foreach (char c in w)

                    if (!char.IsLetter(c) && c != '\'') return false;

            }

            return true;

        }

    

        void RemoveFix(string line) { app.MyFixes.Remove(line); app.MyFixes.Sync(); }

    

        bool AddProgram(string text)

        {

            if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text += ".exe";

            app.ExcludedApps.Add(text);

            app.ExcludedApps.Sync();

            return true;

        }

    

        void RemoveProgram(string name) { app.ExcludedApps.Remove(name); app.ExcludedApps.Sync(); }

    

        // Brings every list and number up to date without disturbing what is selected.

        void Reload()

        {

            Fill(wordsList, ref shownWords, app.MyWords.All());

            Fill(appsList, ref shownApps, app.ExcludedApps.All());

            Fill(fixesList, ref shownFixes, app.MyFixes.All());

            statsLabel.Text = app.StatsText();

    

            bool frequent = frequentTypos.Checked;

            string[][] items = frequent ? app.TopTypos() : app.RecentCorrections();

            string[] lines = new string[items.Length];

            for (int i = 0; i < items.Length; i++)

                lines[i] = (frequent ? items[i][2] + " ×   " : "") + items[i][0] + Arrow + items[i][1];

            typoItems = items;

            Fill(typosList, ref shownTypos, lines);

        }

    

        static bool SequenceEquals(string[] a, string[] b)

        {

            if (ReferenceEquals(a, b)) return true;

            if (a == null || b == null || a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)

                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;

            return true;

        }

    

        static void Fill(ListBox list, ref string[] shown, string[] now)

        {

            if (SequenceEquals(shown, now)) return;

            shown = now;

            object prevSelected = list.SelectedItem;

            list.BeginUpdate();

            list.Items.Clear();

            list.Items.AddRange(now);

            if (prevSelected != null)

            {

                int idx = list.Items.IndexOf(prevSelected);

                if (idx >= 0) list.SelectedIndex = idx;

            }

            list.EndUpdate();

        }

    }

}

