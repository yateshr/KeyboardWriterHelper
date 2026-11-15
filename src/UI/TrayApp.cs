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
    // ---- Tray application ----

    sealed class TrayApp : ApplicationContext
    {
        public const string AppName = "Keyboard Writer Helper";
        public const string Author = "YATESH ROHIT";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "KeyboardWriterHelper";
        const int MaxTrayText = 63; // NotifyIcon.Text throws beyond this
        const int MaxRecent = 10, MaxTopTypos = 50;

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem enabledItem, appItem, recentItem, startupItem, errorItem, statsItem;
        readonly Icon iconOn, iconOff, iconSkipped;
        readonly Counters counters = new Counters();
        readonly Settings settings;
        readonly ListFile myWords, myFixes, excludedApps, carefulApps;
        readonly PersonalLearning learning;
        readonly TypoLog typos;
        readonly SpellService spelling;
        readonly Corrector corrector;
        readonly CandidatePopup candidatePopup;
        readonly List<string[]> recent = new List<string[]>(); // { typed, fixed }
        // Gives the keyboard thread a way to run code on the UI thread.
        readonly Control ui = new Control();
        readonly System.Windows.Forms.Timer housekeeping = new System.Windows.Forms.Timer();
        readonly string folder, statsPath, lifetimePath, ownExe;
        readonly DateTime startedAt = DateTime.Now;
        // Totals from earlier runs; counts only, never text.
        long baseWords, baseCorrections, baseUndone;
        string lastStats, lastLifetime;
        static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);
        DateTime statsWrittenAt = DateTime.MinValue;
        // The program in front, ignoring this app's own windows.
        string currentApp = "";
        // When a "pause for..." ends; MinValue when there is none.
        DateTime pausedUntil = DateTime.MinValue;
        SettingsForm settingsForm;

        // folder = where the settings, the user's lists and the counters are kept.
        public TrayApp(string folder)
        {
            this.folder = folder;
            Directory.CreateDirectory(folder);
            ErrorLog.Init(folder);
            statsPath = Path.Combine(folder, "stats.txt");
            lifetimePath = Path.Combine(folder, "lifetime.txt");
            ownExe = Path.GetFileName(Application.ExecutablePath);
            string settingsPath = Path.Combine(folder, "settings.txt");
            bool firstRun = !File.Exists(settingsPath);
            settings = new Settings(settingsPath);
            myWords = ListFile.MyWords(folder);
            myFixes = ListFile.MyFixes(folder);
            excludedApps = ListFile.ExcludedApps(folder);
            carefulApps = ListFile.CarefulApps(folder);
            learning = new PersonalLearning(Path.Combine(folder, "personal-learning.txt"), settings.LearnPersonalVocabulary);
            typos = new TypoLog(Path.Combine(folder, "typo-counts.txt"), settings.RememberTypos);
            LoadLifetime();
            spelling = new SpellService(counters, settings.SpellOptions());
            RepairStartupPath();

            iconOn = MakeIcon(Color.FromArgb(22, 163, 74));
            iconOff = MakeIcon(Color.FromArgb(120, 120, 120));
            iconSkipped = MakeIcon(Color.FromArgb(217, 119, 6));

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem(AppName + " " + Program.Version);
            title.Enabled = false;
            errorItem = new ToolStripMenuItem();
            errorItem.Enabled = false;
            errorItem.Visible = false;
            statsItem = new ToolStripMenuItem();
            statsItem.Enabled = false;
            enabledItem = new ToolStripMenuItem("", null, delegate { Enabled = !Enabled; });
            ToolStripMenuItem pauseItem = new ToolStripMenuItem("Pause for");
            pauseItem.DropDownItems.Add("10 minutes", null, delegate { PauseFor(10); });
            pauseItem.DropDownItems.Add("1 hour", null, delegate { PauseFor(60); });
            pauseItem.DropDownItems.Add("Until I turn it back on", null, delegate { Enabled = false; });
            appItem = new ToolStripMenuItem("", null, delegate { ToggleApp(); });
            recentItem = new ToolStripMenuItem("Recent corrections");
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings and statistics...", null, delegate { ShowSettings(); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!IsStartupEnabled()); });
            startupItem.Checked = IsStartupEnabled();
            updateItem = new ToolStripMenuItem("Check for updates", null, delegate { OnUpdateItem(); });
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, delegate { ExitThread(); });
            menu.Items.AddRange(new ToolStripItem[] {
                title, errorItem, statsItem, new ToolStripSeparator(), enabledItem, pauseItem, appItem, recentItem,
                new ToolStripSeparator(), settingsItem, startupItem, updateItem,
                new ToolStripSeparator(), exitItem });
            Theme.Load();
            menu.Renderer = new MenuRenderer();
            menu.Font = Theme.Get(Face.Body, 96);
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Text;
            menu.Padding = new Padding(0, 4, 0, 4);
            menu.Opening += delegate { RefreshMenu(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Enabled = !Enabled; };
            tray.BalloonTipClicked += delegate { if (pendingUpdate != null) OfferUpdate(); };

            candidatePopup = new CandidatePopup();

            IntPtr forceHandle = ui.Handle;
            corrector = new Corrector(spelling, myWords, myFixes, excludedApps, counters, settings, IntPtr.Zero, learning, carefulApps);
            corrector.Corrected += OnCorrected;
            corrector.Undone += delegate(string typed, string fix) { typos.Count(typed, fix, -1); };
            corrector.Changed += OnChanged;
            corrector.CandidateOffered += delegate(string fix, string[] choices)
            {
                if (settings.ShowCandidatePopup && !carefulApps.Contains(corrector.LastApp)) OnUi(delegate { candidatePopup.Corner = settings.PopupCorner; candidatePopup.ShowCandidates(fix, choices, Native.GetForegroundWindow()); });
            };
            corrector.CandidateDismissed += delegate { OnUi(candidatePopup.Dismiss); };
            corrector.PredictionOffered += delegate(string pred, string pfx)
            {
                if (settings.SmartCompose && settings.ShowCandidatePopup)
                    OnUi(delegate { candidatePopup.Corner = settings.PopupCorner; candidatePopup.ShowPrediction(pred, pfx, Native.GetForegroundWindow()); });
            };
            corrector.PredictionDismissed += delegate { OnUi(candidatePopup.Dismiss); };
            corrector.Learned += delegate(string word)
            {
                string preferred = learning.GetFix(word);
                string message = preferred == null ? "Learned to leave \"" + word + "\" as typed."
                    : "Learned your preference: " + word + " → " + preferred + ".";
                OnUi(delegate { Say(message + " Manage this in Settings > Learning."); });
            };
            // The hotkey gives no other sign that it worked, so say so.
            corrector.EnabledChanged += delegate { OnUi(delegate { pausedUntil = DateTime.MinValue; ShowState(); AnnounceState(); }); };

            ShowState();
            tray.Visible = true;

            housekeeping.Interval = 1000;
            housekeeping.Tick += delegate { Housekeeping(); };
            housekeeping.Start();
            // Once, not at every sign-in: a short welcome, a moment after start.
            if (firstRun) welcomeAt = DateTime.UtcNow.AddSeconds(2);
            AppDomain.CurrentDomain.ProcessExit += delegate { DisposeResources(); };
            AppDomain.CurrentDomain.UnhandledException += delegate { DisposeResources(); };
            Application.ApplicationExit += delegate { DisposeResources(); };
        }
        // Runs action on the UI thread. Called from the keyboard and mouse threads,
        // possibly while the app is closing.
        void OnUi(System.Windows.Forms.MethodInvoker action)
        {
            try { if (!ui.IsDisposed) ui.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        // An exception on the UI thread: shown in the tray menu and the statistics
        // instead of a crash dialog.
        public void ReportError(Exception ex)
        {
            counters.Error(ex);
        }

        bool disposedResources;
        void DisposeResources()
        {
            if (disposedResources) return;
            disposedResources = true;
            try
            {
                housekeeping.Stop();
                if (candidatePopup != null) candidatePopup.Dispose();
                if (corrector != null) corrector.Dispose();
                if (spelling != null) spelling.Dispose();
                if (myWords != null) myWords.Sync();
                if (myFixes != null) myFixes.Sync();
                if (excludedApps != null) excludedApps.Sync();
                if (carefulApps != null) carefulApps.Sync();
                if (learning != null) learning.Save();
                if (settings != null) settings.Save();
                if (settings != null && settings.RememberTypos && typos != null) typos.Save();
                WriteStats();
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.Dispose();
                }
                if (iconOn != null) iconOn.Dispose();
                if (iconOff != null) iconOff.Dispose();
                if (iconSkipped != null) iconSkipped.Dispose();
            }
            catch { }
        }
        protected override void ExitThreadCore()
        {
            DisposeResources();
            base.ExitThreadCore();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeResources();
            base.Dispose(disposing);
        }

        // ---- What the settings window needs ----

        public Settings Settings { get { return settings; } }
        public ListFile MyWords { get { return myWords; } }
        public ListFile MyFixes { get { return myFixes; } }
        public ListFile ExcludedApps { get { return excludedApps; } }
        public ListFile CarefulApps { get { return carefulApps; } }
        public PersonalLearning Learning { get { return learning; } }
        public string CurrentApp { get { return currentApp; } }
        public string DataFolder { get { return folder; } }
        // The dictionaries in use, and every English one Windows has.
        public string Languages { get { return spelling.Languages; } }
        public string[] InstalledLanguages { get { return spelling.Installed; } }

        // Turning it on or off by hand ends a "pause for...".
        public bool Enabled
        {
            get { return corrector.Enabled; }
            set { corrector.Enabled = value; pausedUntil = DateTime.MinValue; ShowState(); }
        }

        public string[][] RecentCorrections()
        {
            lock (recent) return recent.ToArray();
        }

        // { typed, fix, times }, the most frequent first.
        public string[][] TopTypos()
        {
            return typos.Top(MaxTopTypos);
        }

        // A word (or two-word phrase) the user does not want corrected.
        public void NeverCorrect(string typed)
        {
            learning.Remove(typed);
            learning.Save();
            myFixes.Remove(typed);
            myFixes.Sync();
            myWords.Add(typed);
            myWords.Sync();
        }

        // From now on `typed` always becomes `fix`.
        public void AlwaysFix(string typed, string fix)
        {
            learning.Remove(typed);
            learning.Save();
            myWords.Remove(typed);
            myWords.Sync();
            myFixes.Add(typed + "=" + fix);
            myFixes.Sync();
        }

        // After a change to the spelling choices in Settings.
        public void ApplySpelling()
        {
            spelling.Configure(settings.SpellOptions());
        }

        // Unticking deletes what was saved.
        public void SetRememberTypos(bool on)
        {
            settings.RememberTypos = on;
            settings.Save();
            if (on) typos.Save();
            else typos.Forget();
        }

        public void SetPersonalLearning(bool on)
        {
            learning.Enabled = on;
            settings.LearnPersonalVocabulary = on;
            settings.Save();
            learning.Save();
        }

        public void ForgetLearned(string typed)
        {
            learning.Remove(typed);
            learning.Save();
        }

        public void ClearPersonalLearning()
        {
            learning.Clear();
            learning.Save();
        }

        // The numbers behind the statistics page, formatted: this session (words,
        // corrections, undone), all time (words, corrections, share undone), health
        // (slowest key in ms, slow lookups, errors).
        public string[] StatsNumbers()
        {
            long words = baseWords + counters.Words, fixes = baseCorrections + counters.Corrections, undone = baseUndone + counters.Undone;
            return new string[] {
                counters.Words.ToString("N0"), counters.Corrections.ToString("N0"), counters.Undone.ToString("N0"),
                words.ToString("N0"), fixes.ToString("N0"), Share(undone, fixes),
                counters.SlowestKeyMs.ToString("N0"), counters.TooSlow.ToString("N0"), counters.Errors.ToString("N0") };
        }

        // For the tests: the settings window's layout problems, one per line (empty = fine).
        public string[] CheckSettingsLayout()
        {
            SettingsForm form = new SettingsForm(this);
            try { return form.CheckLayout().ToArray(); }
            finally { form.Close(); }
        }

        public void SaveSettingsShots(string folder)
        {
            Directory.CreateDirectory(folder);
            SettingsForm form = new SettingsForm(this);
            form.Show();
            form.SaveShots(folder);
            form.Close();
            ContextMenuStrip menu = tray.ContextMenuStrip;
            RefreshMenu();
            menu.Show(new Point(80, 80));
            for (int k = 0; k < 5; k++) { Application.DoEvents(); Thread.Sleep(40); }
            using (Bitmap bmp = new Bitmap(menu.Width, menu.Height))
            {
                menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
                bmp.Save(Path.Combine(folder, "tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            menu.Close();
            using (CandidatePopup popup = new CandidatePopup())
            {
                popup.ShowCandidates("there's", new string[] { "there's", "theirs", "there" }, IntPtr.Zero);
                for (int k = 0; k < 5; k++) { Application.DoEvents(); Thread.Sleep(40); }
                using (Bitmap bmp = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(bmp, new Rectangle(0, 0, popup.Width, popup.Height));
                    bmp.Save(Path.Combine(folder, "popup-fix.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                popup.ShowPrediction("something", "som", IntPtr.Zero);
                for (int k = 0; k < 5; k++) { Application.DoEvents(); Thread.Sleep(40); }
                using (Bitmap bmp = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(bmp, new Rectangle(0, 0, popup.Width, popup.Height));
                    bmp.Save(Path.Combine(folder, "popup-prediction.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            using (DialogForm dialog = new DialogForm("Version 2.2 is available", "You have 2.1. The app restarts by itself after the update.",
                "- New look. The settings window is redesigned.\r\n- Light and dark, following Windows.\r\n- Fixed resrat becoming resort.", "Update now", "Later"))
            {
                dialog.Show();
                for (int k = 0; k < 5; k++) { Application.DoEvents(); Thread.Sleep(40); }
                using (Bitmap bmp = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bmp, new Rectangle(0, 0, dialog.Width, dialog.Height));
                    bmp.Save(Path.Combine(folder, "dialog-update.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }        }

        public string LastErrorText { get { return counters.LastError; } }

        public void CheckForUpdatesNow() { OnUpdateItem(); }

        public string StatsText()
        {
            long words = baseWords + counters.Words, fixes = baseCorrections + counters.Corrections, undone = baseUndone + counters.Undone;
            return
                "This session (since " + startedAt.ToString("HH:mm") + ")\r\n" +
                "    words checked: " + counters.Words + "\r\n" +
                "    corrections made: " + counters.Corrections + "\r\n" +
                "    corrections you undid: " + counters.Undone + "\r\n\r\n" +
                "All time\r\n" +
                "    words checked: " + words + "\r\n" +
                "    corrections made: " + fixes + "\r\n" +
                "    corrections you undid: " + undone + "  (" + Share(undone, fixes) + " of corrections)\r\n\r\n" +
                "Health\r\n" +
                "    slowest key handling: " + counters.SlowestKeyMs + " ms\r\n" +
                "    lookups too slow (word left alone): " + counters.TooSlow + "\r\n" +
                "    errors: " + counters.Errors + (counters.LastError != null ? "  (" + counters.LastError + ")" : "");
        }

        static string Share(long part, long whole)
        {
            return whole == 0 ? "0%" : (100.0 * part / whole).ToString("0") + "%";
        }

        void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
                settingsForm = new SettingsForm(this);
            settingsForm.Show();
            settingsForm.Activate();
        }

        // ---- State ----

        void PauseFor(int minutes)
        {
            corrector.Enabled = false;
            pausedUntil = DateTime.Now.AddMinutes(minutes);
            ShowState();
            Say("Auto-correct is paused until " + pausedUntil.ToString("HH:mm") + ".");
        }

        bool IsSkipped(string app)
        {
            if (app.Length > 0 && excludedApps.Contains(app)) return true;
            if (settings.SkipFullscreenGames && Native.IsForegroundFullScreen(Native.GetForegroundWindow())) return true;
            return false;
        }

        // Icon colour: green = correcting, amber = on but skipping the program in
        // front, grey = paused. Also called after a shortcut was changed.
        public void ShowState()
        {
            bool on = corrector.Enabled;
            bool skipped = on && IsSkipped(currentApp);
            string until = pausedUntil == DateTime.MinValue ? "" : " until " + pausedUntil.ToString("HH:mm");
            enabledItem.Checked = on;
            enabledItem.Text = "Auto-correct on   (" + settings.PauseHotkey + ")";
            // Called every second: only tell the shell when something changed.
            Icon icon = !on ? iconOff : skipped ? iconSkipped : iconOn;
            string text = Clip(AppName + (!on ? " - paused" + until : skipped ? " - skipped in " + currentApp : " - on"));
            if (tray.Icon != icon) tray.Icon = icon;
            if (tray.Text != text) tray.Text = text;
        }

        static string Clip(string text)
        {
            return text.Length <= MaxTrayText ? text : text.Substring(0, MaxTrayText);
        }

        void Say(string message)
        {
            tray.ShowBalloonTip(2500, AppName, message, ToolTipIcon.Info);
        }

        void AnnounceState()
        {
            Say(corrector.Enabled ? "Auto-correct is ON." : "Auto-correct is PAUSED. Press " + settings.PauseHotkey + " to turn it back on.");
        }

        // Adds or removes the program in front from the skip list.
        void ToggleApp()
        {
            string app = currentApp;
            if (app.Length == 0) return;
            bool nowSkipped = !excludedApps.Contains(app);
            if (nowSkipped) excludedApps.Add(app);
            else excludedApps.Remove(app);
            excludedApps.Sync();
            carefulApps.Sync();
            ShowState();
            Say(nowSkipped ? "Auto-correct is OFF in " + app + "." : "Auto-correct is ON in " + app + ".");
        }

        // Called on the keyboard thread.
        void OnCorrected(string from, string to)
        {
            typos.Count(from, to, 1);
            lock (recent)
            {
                recent.Insert(0, new string[] { from, to });
                if (recent.Count > MaxRecent) recent.RemoveAt(recent.Count - 1);
            }
        }

        // Called on the keyboard thread: "next suggestion" swapped the fix.
        void OnChanged(string from, string before, string now)
        {
            if (before != from) typos.Count(from, before, -1);
            if (now != from) typos.Count(from, now, 1);
            lock (recent)
            {
                if (recent.Count > 0 && recent[0][0] == from) recent[0][1] = now;
            }
        }

        // ---- Updates ----

        ToolStripMenuItem updateItem;
        UpdateInfo pendingUpdate;
        bool checkingForUpdate, installingUpdate;
        // First look half a minute after start, then once a day.
        DateTime nextUpdateCheck = DateTime.UtcNow.AddSeconds(30);

        // Silent unless there is a new version (or the user asked).
        void CheckForUpdate(bool asked)
        {
            if (checkingForUpdate || installingUpdate) return;
            checkingForUpdate = true;
            Thread worker = new Thread(delegate()
            {
                UpdateInfo found = null;
                string failure = null;
                try { found = Updater.Find(); }
                catch (Exception ex) { failure = ex.Message; }
                OnUi(delegate
                {
                    checkingForUpdate = false;
                    if (found != null)
                    {
                        pendingUpdate = found;
                        updateItem.Text = "Update to " + found.Version + "...";
                        if (asked) OfferUpdate();
                        else tray.ShowBalloonTip(10000, AppName, "Version " + found.Version + " is available. Click here to update.", ToolTipIcon.Info);
                    }
                    else if (asked)
                    {
                        if (failure == null) Dialog.Tell(null, "You have the latest version", "Version " + Updater.Current() + " is the newest one.");
                        else Dialog.Tell(null, "Could not check for updates", failure);
                    }
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void OnUpdateItem()
        {
            if (pendingUpdate != null) OfferUpdate();
            else CheckForUpdate(true);
        }

        void OfferUpdate()
        {
            UpdateInfo info = pendingUpdate;
            if (info == null || installingUpdate) return;
            // The release notes are Markdown; show them as plain text.
            string notes = string.IsNullOrEmpty(info.Notes) ? null :
                (info.Notes.Length > 700 ? info.Notes.Substring(0, 700) + "..." : info.Notes).Replace("**", "").Replace("`", "");
            if (!Dialog.Ask(null, "Version " + info.Version + " is available",
                "You have " + Updater.Current() + ". The app restarts by itself after the update.", notes, "Update now", "Later"))
                return;
            installingUpdate = true;
            Say("Downloading version " + info.Version + "...");
            string exe = Application.ExecutablePath;
            Thread worker = new Thread(delegate()
            {
                string error = Updater.Install(info, exe);
                OnUi(delegate
                {
                    installingUpdate = false;
                    if (error != null)
                    {
                        Dialog.Tell(null, "The update was not installed", error);
                        return;
                    }
                    try { Updater.Restart(exe); }
                    catch (Exception ex)
                    {
                        Dialog.Tell(null, "Updated, but the new version did not start", ex.Message + "\r\nStart it from " + exe);
                        return;
                    }
                    ExitThread();
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // The first-run welcome: when to show it (MinValue = nothing to show).
        DateTime welcomeAt = DateTime.MinValue;

        void ShowWelcome()
        {
            if (Dialog.Ask(null, "Welcome to " + AppName,
                "It is running in the system tray, next to the clock. Type as usual: a misspelled word is fixed when you press Space, " +
                "and Backspace right after a fix undoes it. Ctrl+Alt+K pauses and resumes it.\r\n\r\n" +
                "The suggestion bar shows the other possible words in a corner of the screen; you can choose which corner in Settings.",
                null, "Open settings", "Got it"))
                ShowSettings();
        }

        // ---- Diagnostics: is it working, and if not, why ----

        // Keys seen, words checked, corrections, hook put back, errors, slowest key (ms).
        public string[] DiagnosticsNumbers()
        {
            return new string[] {
                counters.Keys.ToString("N0"), counters.Words.ToString("N0"), counters.Corrections.ToString("N0"),
                counters.HookReinstalls.ToString("N0"), counters.Errors.ToString("N0"), counters.SlowestKeyMs.ToString("N0") };
        }

        // What to look at first.
        public string DiagnosticsHint()
        {
            if (!corrector.Enabled) return "Auto-correct is paused, so nothing is being corrected. Turn it on in General, or press the pause shortcut.";
            if (counters.Keys == 0 && DateTime.Now - startedAt > TimeSpan.FromSeconds(45))
                return "No key has reached the app yet. Type something in any program. If this stays at 0, something is blocking the keyboard: " +
                       "a program running as administrator, remote-desktop or security software. Copy the report and send it with your question.";
            if (counters.Keys > 0 && counters.Words == 0)
                return "Keys are arriving but no word has been checked yet. Finish a word with Space.";
            if (counters.Errors > 0) return "Some errors were caught. Open the error log for details; the app kept running.";
            return "Everything looks normal. If a word was not corrected, it may be a real word (those are left alone on purpose), " +
                   "or the program in front is in the Programs list.";
        }

        // The programs whose keys were ignored, and why. No typed text.
        public string IgnoredText()
        {
            string[] lines = counters.SkippedLines();
            return lines.Length == 0 ? "Nothing was ignored." : string.Join("\r\n", lines);
        }

        public string WhereText()
        {
            bool admin = false;
            try { admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { }
            return "Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " (64-bit)" : "") + "\r\n" +
                   "Runs as administrator: " + (admin ? "yes" : "no") + ". A program running as administrator does not send its keys to this app unless this app is too.\r\n" +
                   "Last program typed in: " + (corrector.LastApp.Length > 0 ? corrector.LastApp : "none yet") + "\r\n" +
                   "Dictionaries: " + spelling.Languages;
        }

        // The whole report, for pasting into a message. Numbers and names only.
        public string DiagnosticsText()
        {
            string[] n = DiagnosticsNumbers();
            StringBuilder s = new StringBuilder();
            s.AppendLine(AppName + " " + Program.Version);
            s.AppendLine("Started: " + startedAt.ToString("yyyy-MM-dd HH:mm:ss") + "   Auto-correct: " + (corrector.Enabled ? "on" : "paused"));
            s.AppendLine(WhereText());
            s.AppendLine();
            s.AppendLine("Keys seen: " + n[0] + "   Words checked: " + n[1] + "   Corrections: " + n[2]);
            s.AppendLine("Keyboard hook put back: " + n[3] + "   Errors: " + n[4] + "   Slowest key (ms): " + n[5]);
            if (counters.LastError != null) s.AppendLine("Last error: " + counters.LastError);
            s.AppendLine();
            s.AppendLine("Keys ignored (program: reason = count):");
            s.AppendLine(IgnoredText());
            s.AppendLine();
            s.AppendLine("Settings: fixOnPunctuation=" + settings.FixOnPunctuation + " capitalize=" + settings.CapitalizeSentences +
                " phrases=" + settings.FixPhrases + " context=" + settings.UseContext + " skipFullscreen=" + settings.SkipFullscreenGames +
                " popup=" + settings.ShowCandidatePopup + " corner=" + settings.PopupCorner + " compose=" + settings.SmartCompose +
                " updates=" + settings.CheckForUpdates + " layout=" + settings.KeyboardLayout + " language=" + settings.MainLanguage);
            s.AppendLine("Lists: " + myWords.All().Length + " words, " + myFixes.All().Length + " fixes, " + excludedApps.All().Length + " never-correct programs, " + carefulApps.All().Length + " careful programs");
            return s.ToString();
        }

        public string ErrorLogPath { get { return ErrorLog.Path; } }

        void Housekeeping()
        {
            if (welcomeAt != DateTime.MinValue && DateTime.UtcNow >= welcomeAt)
            {
                welcomeAt = DateTime.MinValue;
                ShowWelcome();
            }
            if (settings.CheckForUpdates && DateTime.UtcNow >= nextUpdateCheck)
            {
                nextUpdateCheck = DateTime.UtcNow.AddHours(24);
                if (pendingUpdate == null) CheckForUpdate(false);
            }
            corrector.Watchdog();
            myWords.Sync();
            myFixes.Sync();
            excludedApps.Sync();
            learning.Save();
            if (settings.RememberTypos) typos.Save();
            // The counters change with every key; writing them each second while
            // typing is needless disk work. Also written at exit.
            if (DateTime.UtcNow - statsWrittenAt >= StatsInterval)
            {
                statsWrittenAt = DateTime.UtcNow;
                WriteStats();
            }

            if (pausedUntil != DateTime.MinValue && DateTime.Now >= pausedUntil)
            {
                pausedUntil = DateTime.MinValue;
                if (!corrector.Enabled)
                {
                    corrector.Enabled = true;
                    Say("Auto-correct is back ON.");
                }
            }

            // Our own windows (menu, settings) do not count as "the program in front".
            string app = Native.ProcessNameOf(Native.GetForegroundWindow());
            if (app.Length > 0 && !string.Equals(app, ownExe, StringComparison.OrdinalIgnoreCase) && app != currentApp)
                currentApp = app;
            ShowState();
        }

        void RefreshMenu()
        {
            recentItem.DropDownItems.Clear();
            string[][] items = RecentCorrections();
            if (items.Length > 0)
            {
                ToolStripMenuItem hint = new ToolStripMenuItem("Click one to never correct that word again");
                hint.Enabled = false;
                recentItem.DropDownItems.Add(hint);
                foreach (string[] pair in items)
                {
                    string typed = pair[0];
                    recentItem.DropDownItems.Add(typed + "  →  " + pair[1], null, delegate
                    {
                        NeverCorrect(typed);
                        Say("\"" + typed + "\" will not be corrected any more.");
                    });
                }
            }
            else recentItem.DropDownItems.Add("(none yet)").Enabled = false;

            string app = currentApp;
            appItem.Visible = app.Length > 0;
            bool skipped = IsSkipped(app);
            appItem.Text = skipped ? "Turn ON in " + app + "  (currently skipped)" : "Turn OFF in " + app;

            statsItem.Text = counters.Corrections + " fixed, " + counters.Undone + " undone this session";

            string error = counters.LastError;
            errorItem.Visible = error != null;
            if (error != null) errorItem.Text = "Problem: " + (error.Length > 90 ? error.Substring(0, 90) + "..." : error);
        }

        // ---- Files ----

        void LoadLifetime()
        {
            try
            {
                if (!File.Exists(lifetimePath)) return;
                foreach (string line in File.ReadAllLines(lifetimePath))
                {
                    int eq = line.IndexOf('=');
                    long value;
                    if (eq <= 0 || !long.TryParse(line.Substring(eq + 1), out value)) continue;
                    string key = line.Substring(0, eq);
                    if (key == "words") baseWords = value;
                    else if (key == "corrections") baseCorrections = value;
                    else if (key == "undone") baseUndone = value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Health counters for troubleshooting. Numbers only, never typed text.
        void WriteStats()
        {
            string body =
                "keys seen: " + counters.Keys + "\r\n" +
                "words checked: " + counters.Words + "\r\n" +
                "corrections made: " + counters.Corrections + "\r\n" +
                "corrections undone: " + counters.Undone + "\r\n" +
                "lookups too slow (word left alone): " + counters.TooSlow + "\r\n" +
                "slowest key handling (ms): " + counters.SlowestKeyMs + "\r\n" +
                "keyboard hook put back (Windows had dropped it): " + counters.HookReinstalls + "\r\n" +
                "errors: " + counters.Errors + "\r\n" +
                "last error: " + (counters.LastError ?? "none") + "\r\n";
            string[] skipped = counters.SkippedLines();
            if (skipped.Length > 0)
                body += "keys ignored (program: reason = count):\r\n  " + string.Join("\r\n  ", skipped) + "\r\n";
            if (body == lastStats) return;
            try
            {
                File.WriteAllText(statsPath,
                    AppName + " " + Program.Version + " - counters only, never what you typed\r\n" +
                    "running since: " + startedAt.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                    "updated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + body);
                lastStats = body;
                string life =
                    "words=" + (baseWords + counters.Words) + "\r\n" +
                    "corrections=" + (baseCorrections + counters.Corrections) + "\r\n" +
                    "undone=" + (baseUndone + counters.Undone) + "\r\n";
                if (life != lastLifetime)
                {
                    // The all-time totals must survive a crash while writing.
                    Settings.SafeWriteAllText(lifetimePath, life);
                    lastLifetime = life;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- Start with Windows ----

        public static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunValue) != null;
        }

        // If the exe was moved since "Start with Windows" was ticked, the stored
        // path is stale and Windows would silently start nothing.
        static void RepairStartupPath()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null || key.GetValue(RunValue) == null) return;
                string current = "\"" + Application.ExecutablePath + "\"";
                if (!string.Equals(key.GetValue(RunValue) as string, current, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(RunValue, current);
            }
        }

        public void SetStartup(bool on)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValue, false);
            }
            startupItem.Checked = IsStartupEnabled();
        }

        static Icon MakeIcon(Color back)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                // The same logo as everywhere else, in the colour that shows the state.
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Theme.DrawLogo(g, new Rectangle(0, 0, 32, 32), back);
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);
    }
}
