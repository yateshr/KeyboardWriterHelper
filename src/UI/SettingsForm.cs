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

    // A side menu on the left, one page at a time on the right. The look (colours,
    // type, controls) is in Theme.cs; this file is what each page says and does.
    sealed class SettingsForm : Form
    {
        const string Arrow = "  →  ";
        const string Home = "https://github.com/yateshr/KeyboardWriterHelper";

        readonly TrayApp app;
        readonly ListFrame wordsFrame, fixesFrame, appsFrame, typosFrame, learningFrame;
        readonly ListBox wordsList, fixesList, appsList, typosList, learningList;
        readonly TextBlock learningStatus;
        readonly Btn frequentTypos, recentTypos;
        readonly TileRow sessionTiles, allTimeTiles, healthTiles;
        readonly TextBlock healthError;
        readonly StatusView status;
        readonly PageHeader header;
        readonly Panel pageHost, sidebar;
        readonly Stack nav;
        readonly List<NavItem> navItems = new List<NavItem>();
        readonly List<Control> pages = new List<Control>();
        readonly List<string[]> titles = new List<string[]>();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        string[] shownWords = new string[0], shownFixes = new string[0], shownApps = new string[0], shownTypos = new string[0];
        string[] shownLearning = new string[0];
        PersonalLearningEntry[] learningItems = new PersonalLearningEntry[0];
        // { typed, fix } behind each line of the typos list.
        string[][] typoItems = new string[0][];
        bool frequentChosen = true;
        int current = -1;
        static int lastPage;

        int P(int pixelsAt96) { return Theme.Px(this, pixelsAt96); }

        public SettingsForm(TrayApp app)
        {
            this.app = app;
            Theme.Load();
            status = new StatusView();
            header = new PageHeader();
            pageHost = new Panel();
            sidebar = new Panel();
            nav = new Stack();
            SuspendLayout();
            // Sizes are worked out from the window's own DPI (see Theme.cs), so
            // WinForms' own scaling is off.
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = Theme.Get(Face.Body, DeviceDpi);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Text = TrayApp.AppName + " - settings";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(P(920), P(660));
            MinimumSize = new Size(P(780), P(540));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            wordsFrame = new ListFrame("No words yet. Words you add here are never changed.");
            fixesFrame = new ListFrame("No fixes yet. Add one as typo=correction.");
            appsFrame = new ListFrame("No programs listed.");
            typosFrame = new ListFrame("Nothing corrected yet. Fixes made while you type show up here.");
            learningFrame = new ListFrame("Nothing learned yet.");
            wordsList = wordsFrame.List;
            fixesList = fixesFrame.List;
            appsList = appsFrame.List;
            typosList = typosFrame.List;
            learningList = learningFrame.List;
            wordsList.SelectionMode = fixesList.SelectionMode = appsList.SelectionMode = learningList.SelectionMode = SelectionMode.MultiExtended;
            learningList.HorizontalScrollbar = true;
            learningStatus = new TextBlock("", Face.Caption, null);
            frequentTypos = new Btn("Most frequent", ButtonKind.Tab);
            recentTypos = new Btn("Most recent", ButtonKind.Tab);
            sessionTiles = new TileRow(new string[] { "Words checked", "Corrections made", "Corrections you undid" });
            allTimeTiles = new TileRow(new string[] { "Words checked", "Corrections made", "Of corrections undone" });
            healthTiles = new TileRow(new string[] { "Slowest key (ms)", "Slow lookups skipped", "Errors" });
            healthError = new TextBlock("", Face.Caption, Theme.Danger);

            AddPage("General", "", "General", "The basics: when it runs, the shortcuts and the language.", GeneralPage());
            AddPage("Corrections", "", "Corrections", "Choose what gets fixed while you type.", CorrectionsPage());
            AddPage("My words", "", "My words", "Words you use that are spelled right. They are never changed.",
                ListPage(wordsFrame, "Type a word and press Enter", "Add word", AddWord, RemoveWord, null));
            AddPage("My fixes", "", "My fixes", "Your own shortcuts and snippets: typo=correction. Supports {date}, {time}, {datetime} and {clip}.",
                ListPage(fixesFrame, "brb=be right back", "Add fix", AddFix, RemoveFix, null));
            AddPage("Programs", "", "Programs", "Nothing typed in these programs is corrected. The tray icon turns amber while one is in front.",
                ListPage(appsFrame, "notepad.exe", "Add program", AddProgram, RemoveProgram, "Add the program in front"));
            AddPage("Typos", "", "Typos", "The fixes the app made. Mark one as always or never.", TyposPage());
            AddPage("Learning", "", "Learning", "The app can learn from how you undo and choose corrections.", LearningPage());
            AddPage("Statistics", "", "Statistics", "Numbers only. Nothing you type is ever recorded here.", StatsPage());
            AddPage("About", "", "About", "Version, updates and credits.", AboutPage());

            // Fill first, then the edges: that is the order WinForms docks in.
            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Bg;
            pageHost.Dock = DockStyle.Fill;
            pageHost.BackColor = Theme.Bg;
            header.Dock = DockStyle.Top;
            content.Controls.Add(pageHost);
            content.Controls.Add(header);

            sidebar.Dock = DockStyle.Left;
            sidebar.BackColor = Theme.Sidebar;
            nav.Dock = DockStyle.Fill;
            nav.BackColor = Theme.Sidebar;
            nav.Padding = new Padding(0, P(4), 0, 0);
            status.Dock = DockStyle.Bottom;
            BrandView brand = new BrandView();
            brand.Dock = DockStyle.Top;
            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(status);
            sidebar.Controls.Add(brand);
            foreach (NavItem item in navItems) nav.Controls.Add(item);

            Controls.Add(content);
            Controls.Add(sidebar);
            ApplySizes();
            ShowPage(Math.Min(lastPage, pages.Count - 1));
            ResumeLayout(true);

            refresh.Interval = 1000;
            refresh.Tick += delegate { Reload(); };
            Shown += delegate { Reload(); refresh.Start(); };
            FormClosed += delegate { refresh.Stop(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleTitleBar(this);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Font = Theme.Get(Face.Body, DeviceDpi);
            ApplySizes();
        }

        void ApplySizes()
        {
            sidebar.Width = P(244);
            status.Height = P(52);
            header.Height = header.HeightFor(header.Width);
            nav.Padding = new Padding(0, P(4), 0, 0);
            MinimumSize = new Size(P(780), P(540));
        }

        // One PNG per page, to look at the design without clicking through it.
        public void SaveShots(string folder)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                ShowPage(i);
                Reload();
                for (int k = 0; k < 5; k++) { Application.DoEvents(); Thread.Sleep(40); }
                using (Bitmap bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(Path.Combine(folder, "page-" + i + "-" + titles[i][0].Replace(' ', '-') + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        void AddPage(string caption, string glyph, string title, string subtitle, Control page)
        {
            NavItem item = new NavItem(caption, glyph);
            int index = navItems.Count;
            item.Click += delegate { ShowPage(index); };
            navItems.Add(item);
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            pageHost.Padding = new Padding(P(36), 0, P(36), P(24));
            pageHost.Controls.Add(page);
            pages.Add(page);
            titles.Add(new string[] { title, subtitle });
        }

        void ShowPage(int index)
        {
            if (index == current) return;
            current = lastPage = index;
            for (int i = 0; i < pages.Count; i++)
            {
                navItems[i].Selected = i == index;
                navItems[i].Invalidate();
                pages[i].Visible = i == index;
            }
            header.Set(titles[index][0], titles[index][1]);
            pages[index].Focus();
        }

        // ---- Building blocks ----

        static TextBlock Heading(string text, bool first)
        {
            TextBlock block = new TextBlock(text, Face.Heading, null);
            block.Margin = new Padding(0, first ? 4 : 26, 0, 10);
            block.AccessibleRole = AccessibleRole.Grouping;
            return block;
        }

        static TextBlock Note(string text)
        {
            TextBlock block = new TextBlock(text, Face.Caption, null);
            block.Margin = new Padding(0, 14, 0, 0);
            return block;
        }

        static Card Group(params Control[] rows)
        {
            Card card = new Card();
            foreach (Control row in rows) card.Controls.Add(row);
            return card;
        }

        static ToggleRow Toggle(string title, string description, bool value, Action<bool> changed)
        {
            ToggleRow row = new ToggleRow(title, description, value);
            row.CheckedChanged += delegate { changed(row.Checked); };
            return row;
        }

        Stack NewStack()
        {
            Stack stack = new Stack();
            stack.Padding = new Padding(0, 0, P(6), P(16));
            return stack;
        }

        // ---- Pages ----

        Control GeneralPage()
        {
            Stack page = NewStack();

            Field pauseBox = HotkeyBox(
                delegate { return app.Settings.PauseHotkey; }, delegate(Hotkey h) { app.Settings.PauseHotkey = h; },
                delegate { return app.Settings.NextHotkey; });
            Field nextBox = HotkeyBox(
                delegate { return app.Settings.NextHotkey; }, delegate(Hotkey h) { app.Settings.NextHotkey = h; },
                delegate { return app.Settings.PauseHotkey; });

            Dropdown language = new Dropdown();
            string[] installed = app.InstalledLanguages;
            foreach (string tag in installed) language.Items.Add(LanguageName(tag));
            int chosen = Array.FindIndex(installed, delegate(string tag) { return string.Equals(tag, app.Settings.MainLanguage, StringComparison.OrdinalIgnoreCase); });
            language.Choose(Math.Max(0, chosen));
            language.SelectedIndexChanged += delegate
            {
                app.Settings.MainLanguage = installed[language.SelectedIndex];
                SpellingChanged();
            };

            Dropdown layoutBox = new Dropdown();
            layoutBox.Items.AddRange(new string[] { "Auto", "QWERTY", "QWERTZ", "AZERTY", "Dvorak", "Colemak" });
            int layoutIdx = layoutBox.Items.IndexOf(app.Settings.KeyboardLayout);
            layoutBox.Choose(layoutIdx >= 0 ? layoutIdx : 0);
            layoutBox.SelectedIndexChanged += delegate
            {
                app.Settings.KeyboardLayout = layoutBox.Items[layoutBox.SelectedIndex];
                SpellingChanged();
            };

            page.Controls.Add(Heading("Auto-correct", true));
            page.Controls.Add(Group(
                Toggle("Auto-correct is on", "Fixes misspelled words as you type, in every program.", app.Enabled, delegate(bool v) { app.Enabled = v; }),
                Toggle("Start with Windows", "Runs quietly in the tray each time you sign in.", TrayApp.IsStartupEnabled(), delegate(bool v) { app.SetStartup(v); })));
            page.Controls.Add(Heading("Shortcuts", false));
            page.Controls.Add(Group(
                new ValueRow("Pause and resume", "Click the box, then press the new keys. Use Ctrl or Alt.", pauseBox, 190),
                new ValueRow("Next suggestion", "Works straight after a fix: swaps in the next-best word.", nextBox, 190)));
            page.Controls.Add(Heading("Language", false));
            page.Controls.Add(Group(
                new ValueRow("Suggestions use the spelling of", "The English that the suggestions follow.", language, 250),
                Toggle("Other English spellings are correct too", "Colour and color are both left alone.",
                    app.Settings.AcceptVariants, delegate(bool v) { app.Settings.AcceptVariants = v; SpellingChanged(); }),
                new ValueRow("Keyboard layout", "Used to guess which neighbouring key you meant. Auto follows Windows.", layoutBox, 190)));
            return page;
        }

        void SpellingChanged()
        {
            app.Settings.Save();
            app.ApplySpelling();
        }

        static string LanguageName(string tag)
        {
            try { return CultureInfo.GetCultureInfo(tag).EnglishName; }
            catch (CultureNotFoundException) { return tag; }
        }

        // Shows a shortcut and takes a new one when keys are pressed in it.
        // other = the shortcut it must not be the same as.
        Field HotkeyBox(Func<Hotkey> get, Action<Hotkey> set, Func<Hotkey> other)
        {
            Field field = new Field(true);
            field.Placeholder = "Press the keys";
            field.Input.Text = get().ToString();
            field.Input.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                e.SuppressKeyPress = true;
                Hotkey pressed = new Hotkey(e.Control, e.Alt, e.Shift, e.KeyCode);
                if (!pressed.IsValid || pressed.Same(other())) return;
                set(pressed);
                app.Settings.Save();
                app.ShowState();
                field.Input.Text = pressed.ToString();
            };
            return field;
        }

        Control CorrectionsPage()
        {
            Stack page = NewStack();
            Settings o = app.Settings;
            page.Controls.Add(Heading("While you type", true));
            page.Controls.Add(Group(
                Toggle("Fix a word as soon as punctuation follows", "A comma, semicolon, colon, ! or ? after a word fixes it at once. Otherwise a fix happens when you press Space.",
                    o.FixOnPunctuation, delegate(bool v) { o.FixOnPunctuation = v; o.Save(); }),
                Toggle("Capitalise the first word of a sentence", "After a full stop, ! or ? and a space.",
                    o.CapitalizeSentences, delegate(bool v) { o.CapitalizeSentences = v; o.Save(); }),
                Toggle("Fix two capitals at the start of a word", "THe becomes The.",
                    o.FixTwoCapitals, delegate(bool v) { o.FixTwoCapitals = v; o.Save(); }),
                Toggle("Fix grammar and phrase mistakes", "The files is becomes the files are. Their is becomes there is.",
                    o.FixPhrases, delegate(bool v) { o.FixPhrases = v; o.Save(); }),
                Toggle("Let the word before help choose the fix", "Used when several words are possible.",
                    o.UseContext, delegate(bool v) { o.UseContext = v; SpellingChanged(); }),
                Toggle("Turn two spaces into a full stop", "Two spaces after a word become a full stop and a space.",
                    o.DoubleSpacePeriod, delegate(bool v) { o.DoubleSpacePeriod = v; o.Save(); })));
            page.Controls.Add(Heading("Typography", false));
            page.Controls.Add(Group(
                Toggle("Smart dashes, ellipsis and arrows", "-- becomes —, ... becomes …, -> becomes → and => becomes ⇒.",
                    o.SmartDashes, delegate(bool v) { o.SmartDashes = o.SmartArrows = o.SmartEllipsis = v; o.Save(); })));
            page.Controls.Add(Heading("Suggestions", false));
            page.Controls.Add(Group(
                Toggle("Show suggestions in the top-right corner", "A small bar lists the other possible words after a fix, and Smart Compose's guess.",
                    o.ShowCandidatePopup, delegate(bool v) { o.ShowCandidatePopup = v; o.Save(); }),
                Toggle("Smart Compose", "Guesses the next word from the words before it. Tab accepts it while it is shown.",
                    o.SmartCompose, delegate(bool v) { o.SmartCompose = v; o.Save(); })));
            page.Controls.Add(Heading("Where it works", false));
            page.Controls.Add(Group(
                Toggle("Skip full-screen games", "Does nothing in full-screen games and exclusive-mode programs, so it never gets in the way.",
                    o.SkipFullscreenGames, delegate(bool v) { o.SkipFullscreenGames = v; o.Save(); })));
            page.Controls.Add(Note("Backspace right after a fix undoes it. A full stop never triggers a fix by itself (example.com is not a sentence); the next Space does."));
            return page;
        }

        // add returns false when the text was not usable (it then stays in the box).
        Control ListPage(ListFrame frame, string hint, string addLabel, Func<string, bool> add, Action<string> remove, string addNowLabel)
        {
            ToolPage page = new ToolPage();
            Field input = new Field(false);
            input.Placeholder = hint;
            input.Input.AccessibleName = addLabel;
            Btn addButton = new Btn(addLabel, ButtonKind.Primary);
            Action doAdd = delegate
            {
                string text = input.Input.Text.Trim();
                if (text.Length == 0 || !add(text)) return;
                input.Input.Text = "";
                Reload();
            };
            addButton.Click += delegate { doAdd(); };
            input.Input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                doAdd();
            };
            Btn removeButton = new Btn("Remove selected", ButtonKind.Secondary);
            removeButton.Click += delegate
            {
                foreach (object item in new ArrayList(frame.List.SelectedItems)) remove((string)item);
                Reload();
            };
            page.TopStretch = input;
            page.TopEnd.Add(addButton);
            if (addNowLabel != null)
            {
                Btn now = new Btn(addNowLabel, ButtonKind.Secondary);
                now.Click += delegate
                {
                    if (app.CurrentApp.Length > 0) add(app.CurrentApp);
                    Reload();
                };
                page.TopEnd.Insert(0, now);
            }
            page.Fill = frame;
            page.BottomEnd.Add(removeButton);
            page.Controls.Add(frame);
            page.Controls.Add(input);
            foreach (Control c in page.TopEnd) page.Controls.Add(c);
            page.Controls.Add(removeButton);
            return page;
        }

        Control TyposPage()
        {
            ToolPage page = new ToolPage();
            frequentTypos.Selected = true;
            frequentTypos.Click += delegate { frequentChosen = true; frequentTypos.Selected = true; recentTypos.Selected = false; frequentTypos.Invalidate(); recentTypos.Invalidate(); Reload(); };
            recentTypos.Click += delegate { frequentChosen = false; frequentTypos.Selected = false; recentTypos.Selected = true; frequentTypos.Invalidate(); recentTypos.Invalidate(); Reload(); };
            Btn always = new Btn("Always fix it this way", ButtonKind.Primary);
            always.Click += delegate
            {
                string[] item = SelectedTypo();
                if (item == null) return;
                app.AlwaysFix(item[0], item[1]);
                Reload();
            };
            Btn never = new Btn("Never correct it", ButtonKind.Secondary);
            never.Click += delegate
            {
                string[] item = SelectedTypo();
                if (item == null) return;
                app.NeverCorrect(item[0]);
                Reload();
            };
            page.TopStart.Add(frequentTypos);
            page.TopStart.Add(recentTypos);
            page.Fill = typosFrame;
            page.BottomStart.Add(always);
            page.BottomStart.Add(never);
            page.Footer.Add(Group(Toggle("Keep the counts between sessions",
                "Saved in a file on this PC only. Switching this off deletes the file. Until then the counts live in memory and are gone when the app closes.",
                app.Settings.RememberTypos, delegate(bool v) { app.SetRememberTypos(v); })));
            page.Controls.Add(typosFrame);
            page.Controls.Add(frequentTypos);
            page.Controls.Add(recentTypos);
            page.Controls.Add(always);
            page.Controls.Add(never);
            foreach (Control c in page.Footer) page.Controls.Add(c);
            return page;
        }

        string[] SelectedTypo()
        {
            int i = typosList.SelectedIndex;
            return i >= 0 && i < typoItems.Length ? typoItems[i] : null;
        }

        Control LearningPage()
        {
            ToolPage page = new ToolPage();
            ToggleRow learn = Toggle("Learn from my corrections",
                "Keeps what you teach it on this PC. Turning it off pauses learning and the use of saved preferences.",
                app.Settings.LearnPersonalVocabulary, delegate(bool on) { app.SetPersonalLearning(on); Reload(); });
            Card learnCard = Group(learn);
            TextBlock help = new TextBlock("Undo a spelling or phrase correction twice to protect it. Choose the same alternative three times " +
                "with the next-suggestion shortcut to learn a preferred fix; start the next word to confirm each choice.", Face.Caption, null);
            Btn forget = new Btn("Forget selected", ButtonKind.Secondary);
            forget.Click += delegate
            {
                List<string> selected = new List<string>();
                foreach (int index in learningList.SelectedIndices)
                    if (index < learningItems.Length) selected.Add(learningItems[index].Typed);
                foreach (string typed in selected) app.ForgetLearned(typed);
                Reload();
            };
            Btn clear = new Btn("Clear all learning", ButtonKind.Danger);
            clear.Click += delegate
            {
                if (!Dialog.Ask(this, "Clear all learning?", "Forget all learned words, preferences and feedback counts? " +
                    "Your manual My words and My fixes lists will be kept.", null, "Clear all", "Keep")) return;
                app.ClearPersonalLearning();
                Reload();
            };
            page.Header.Add(learnCard);
            page.Header.Add(help);
            page.Fill = learningFrame;
            page.BottomStart.Add(forget);
            page.BottomEnd.Add(clear);
            page.Footer.Add(learningStatus);
            page.Controls.Add(learnCard);
            page.Controls.Add(help);
            page.Controls.Add(learningFrame);
            page.Controls.Add(forget);
            page.Controls.Add(clear);
            page.Controls.Add(learningStatus);
            return page;
        }

        Control StatsPage()
        {
            Stack page = NewStack();
            page.Controls.Add(Heading("This session", true));
            page.Controls.Add(sessionTiles);
            page.Controls.Add(Heading("All time", false));
            page.Controls.Add(allTimeTiles);
            page.Controls.Add(Heading("Health", false));
            page.Controls.Add(healthTiles);
            healthError.Margin = new Padding(0, 10, 0, 0);
            page.Controls.Add(healthError);
            Btn open = new Btn("Open the data folder", ButtonKind.Secondary);
            open.Margin = new Padding(0, 24, 0, 0);
            open.Click += delegate { Open(app.DataFolder); };
            page.Controls.Add(new ButtonSlot(open));
            page.Controls.Add(Note("The folder holds your settings, My words, My fixes, the program list and these counters. " +
                "The lists are plain text: edits made in Notepad are picked up within a second."));
            return page;
        }

        Control AboutPage()
        {
            Stack page = NewStack();
            page.Controls.Add(new AboutHeader());
            page.Controls.Add(Heading("Updates", false));
            Btn check = new Btn("Check now", ButtonKind.Secondary);
            check.Click += delegate { app.CheckForUpdatesNow(); };
            page.Controls.Add(Group(
                Toggle("Check for new versions", "Looks about once a day. You are only told when there is a newer version; otherwise nothing is shown.",
                    app.Settings.CheckForUpdates, delegate(bool v) { app.Settings.CheckForUpdates = v; app.Settings.Save(); }),
                new ValueRow("You have version " + Updater.Current(), "Updates are checked against their published checksum before they are installed.", check, 130)));
            page.Controls.Add(Heading("Privacy", false));
            page.Controls.Add(new TextBlock("Spelling, grammar and Smart Compose all run on this computer. Nothing you type is uploaded or saved unless you ask for it.", Face.Body, null));
            page.Controls.Add(Heading("Dictionaries in use", false));
            page.Controls.Add(new TextBlock(app.Languages, Face.Body, null));
            page.Controls.Add(Heading("Credits", false));
            page.Controls.Add(new TextBlock("The built-in word data comes from open projects: Wikipedia's common misspellings, the Birkbeck corpus, " +
                "Peter Norvig's data, LibreOffice AutoCorrect, the GitHub Typo Corpus, SymSpell, Hunspell and SCOWL, and more. Thank you to everyone who shared them.", Face.Body, null));
            Btn credits = new Btn("Credits and licences", ButtonKind.Secondary);
            credits.Click += delegate { Open(Home + "/blob/main/THIRD_PARTY_NOTICES.md"); };
            Btn project = new Btn("Project on GitHub", ButtonKind.Secondary);
            project.Click += delegate { Open(Home); };
            credits.Margin = new Padding(0, 16, 0, 0);
            page.Controls.Add(new ButtonSlot(credits, project));
            return page;
        }

        static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
            catch (Exception) { }
        }

        // ---- What the pages do ----

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
                Dialog.Tell(this, "That is not a fix yet",
                    "Write it as  typo=correction , for example  brb=be right back .\r\n\r\n" +
                    "The left side is one to four words, made of letters.");
                return false;
            }
            app.AlwaysFix(typed, fix);
            return true;
        }

        // One to four words (as many as the engine looks back), made of letters
        // (an apostrophe inside is fine): only that can be recognised as it is typed.
        static bool IsTypable(string text)
        {
            string[] words = text.Split(' ');
            if (words.Length > 4) return false;
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
            Fill(wordsFrame, ref shownWords, app.MyWords.All());
            Fill(appsFrame, ref shownApps, app.ExcludedApps.All());
            Fill(fixesFrame, ref shownFixes, app.MyFixes.All());

            string[] numbers = app.StatsNumbers();
            sessionTiles.SetValues(new string[] { numbers[0], numbers[1], numbers[2] });
            allTimeTiles.SetValues(new string[] { numbers[3], numbers[4], numbers[5] });
            healthTiles.SetValues(new string[] { numbers[6], numbers[7], numbers[8] });
            healthError.Value = app.LastErrorText == null ? "" : "Last error: " + app.LastErrorText;
            healthError.Visible = app.LastErrorText != null;

            learningItems = app.Learning.All();
            string[] learnedLines = new string[learningItems.Length];
            int active = 0;
            for (int i = 0; i < learningItems.Length; i++)
            {
                PersonalLearningEntry entry = learningItems[i];
                if (entry.IsActive) active++;
                string action = entry.Replacement == null ? "leave as typed" : entry.Replacement;
                int threshold = entry.Replacement == null ? PersonalLearning.RejectionThreshold : PersonalLearning.ChoiceThreshold;
                learnedLines[i] = entry.Typed + Arrow + action + (entry.IsActive ? "  [learned]" :
                    "  [pending " + entry.Confirmations + "/" + threshold + "]");
            }
            Fill(learningFrame, ref shownLearning, learnedLines);
            learningStatus.Value = app.Learning.LastError ?? (active + " learned, " + (learningItems.Length - active) +
                " awaiting more feedback. " + (app.Learning.Enabled ? "Learning is on." : "Learning is off; saved entries are inactive.") +
                " Manual My words and My fixes always take priority.");

            string[][] items = frequentChosen ? app.TopTypos() : app.RecentCorrections();
            string[] lines = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
                lines[i] = (frequentChosen ? items[i][2] + " ×   " : "") + items[i][0] + Arrow + items[i][1];
            typoItems = items;
            Fill(typosFrame, ref shownTypos, lines);

            if (app.Enabled) status.Set("Auto-correct is on", Theme.Accent);
            else status.Set("Auto-correct is paused", Theme.Muted);
        }

        static bool SequenceEquals(string[] a, string[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        static void Fill(ListFrame frame, ref string[] shown, string[] now)
        {
            if (SequenceEquals(shown, now)) { frame.UpdateEmpty(); return; }
            shown = now;
            ListBox list = frame.List;
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
            frame.UpdateEmpty();
        }

        // ---- Pieces only this window uses ----

        // The logo, the name and the version at the top of the side menu.
        sealed class BrandView : Surface
        {
            public BrandView()
            {
                BackColor = Theme.Sidebar;
                TabStop = false;
                AccessibleRole = AccessibleRole.StaticText;
                AccessibleName = TrayApp.AppName + " " + Program.Version;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Height != P(92)) Height = P(92);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Sidebar);
                int size = P(40);
                Theme.DrawLogo(g, new Rectangle(P(24), P(26), size, size), Color.FromArgb(22, 163, 74));
                int x = P(24) + size + P(12);
                string shortVersion = Updater.Current();
                TextRenderer.DrawText(g, "Keyboard Writer", F(Face.BodyStrong), new Rectangle(x, P(24), Width - x - P(8), P(22)), Theme.Text, Theme.OneLine);
                TextRenderer.DrawText(g, "Helper  " + shortVersion, F(Face.Caption), new Rectangle(x, P(46), Width - x - P(8), P(20)), Theme.Muted, Theme.OneLine);
            }
        }

        // The big logo and name on the About page.
        sealed class AboutHeader : Surface, IFlow
        {
            public AboutHeader() { TabStop = false; AccessibleRole = AccessibleRole.StaticText; AccessibleName = TrayApp.AppName + " " + Program.Version + ", made by " + TrayApp.Author; }

            public int Measure(int width) { return P(104); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Bg);
                int size = P(72);
                Theme.DrawLogo(g, new Rectangle(0, P(8), size, size), Color.FromArgb(22, 163, 74));
                int x = size + P(20);
                TextRenderer.DrawText(g, TrayApp.AppName, F(Face.Title), new Rectangle(x, P(8), Width - x, P(40)), Theme.Text, Theme.OneLine);
                TextRenderer.DrawText(g, "Version " + Program.Version, F(Face.Body), new Rectangle(x, P(48), Width - x, P(24)), Theme.Muted, Theme.OneLine);
                TextRenderer.DrawText(g, "Made by " + TrayApp.Author, F(Face.BodyStrong), new Rectangle(x, P(74), Width - x, P(24)), Theme.Text, Theme.OneLine);
            }
        }

        // Buttons in a row, left to right.
        sealed class ButtonSlot : Panel, IFlow
        {
            readonly Btn[] buttons;

            public ButtonSlot(params Btn[] buttons)
            {
                this.buttons = buttons;
                BackColor = Theme.Bg;
                Padding spacing = buttons.Length > 0 ? buttons[0].Margin : Padding.Empty;
                foreach (Btn b in buttons) Controls.Add(b);
                Margin = spacing;
            }

            public int Measure(int width) { return Theme.Px(this, 36); }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int x = 0, gap = Theme.Px(this, 10);
                foreach (Btn b in buttons)
                {
                    b.Margin = new Padding(0);
                    b.SetBounds(x, 0, b.PreferredWidth, Height);
                    x += b.PreferredWidth + gap;
                }
            }
        }

        // A page that is mostly one list: things above it, things below it.
        sealed class ToolPage : Panel
        {
            public Control Fill, TopStretch;
            public readonly List<Control> TopStart = new List<Control>(), TopEnd = new List<Control>();
            public readonly List<Control> BottomStart = new List<Control>(), BottomEnd = new List<Control>();
            // Full-width items (cards, paragraphs) above and below.
            public readonly List<Control> Header = new List<Control>(), Footer = new List<Control>();

            public ToolPage() { BackColor = Theme.Bg; DoubleBuffered = true; }

            int P(int v) { return Theme.Px(this, v); }

            static int Pref(Control c) { Btn b = c as Btn; return b != null ? b.PreferredWidth : c.Width; }

            int FlowHeight(List<Control> items, int width, int gap)
            {
                int h = 0;
                foreach (Control c in items)
                {
                    IFlow f = c as IFlow;
                    h += (f != null ? f.Measure(width) : c.Height) + gap;
                }
                return h;
            }

            int Place(List<Control> items, int y, int width, int gap)
            {
                foreach (Control c in items)
                {
                    IFlow f = c as IFlow;
                    int h = f != null ? f.Measure(width) : c.Height;
                    c.SetBounds(0, y, width, h);
                    y += h + gap;
                }
                return y;
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int w = Width, gap = P(12);
                int row = P(38);
                bool top = TopStretch != null || TopStart.Count > 0 || TopEnd.Count > 0;
                bool bottom = BottomStart.Count > 0 || BottomEnd.Count > 0;

                int y = Place(Header, 0, w, gap);
                if (top)
                {
                    int x = 0;
                    foreach (Control c in TopStart) { c.SetBounds(x, y + P(1), Pref(c), P(36)); x += Pref(c) + P(8); }
                    int right = w;
                    for (int i = TopEnd.Count - 1; i >= 0; i--)
                    {
                        Control c = TopEnd[i];
                        right -= Pref(c);
                        c.SetBounds(right, y + P(1), Pref(c), P(36));
                        right -= P(8);
                    }
                    if (TopStretch != null) TopStretch.SetBounds(x, y, Math.Max(P(80), right - x + P(8)), row);
                    y += row + gap;
                }

                int footerH = FlowHeight(Footer, w, gap);
                int bottomH = bottom ? P(36) + gap : 0;
                int fillH = Math.Max(P(80), Height - y - bottomH - footerH);
                Fill.SetBounds(0, y, w, fillH);
                y += fillH + gap;
                if (bottom)
                {
                    int x = 0;
                    foreach (Control c in BottomStart) { c.SetBounds(x, y, Pref(c), P(36)); x += Pref(c) + P(8); }
                    int right = w;
                    for (int i = BottomEnd.Count - 1; i >= 0; i--)
                    {
                        Control c = BottomEnd[i];
                        right -= Pref(c);
                        c.SetBounds(right, y, Pref(c), P(36));
                        right -= P(8);
                    }
                    y += P(36) + gap;
                }
                Place(Footer, y, w, gap);
            }
        }
    }
}
