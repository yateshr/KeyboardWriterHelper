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

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        const string RunValue = "KeyboardWriterHelper";

        const int MaxTrayText = 63; // NotifyIcon.Text throws beyond this

        const int MaxRecent = 10, MaxTopTypos = 50;

    

        readonly NotifyIcon tray;

        readonly ToolStripMenuItem enabledItem, appItem, recentItem, startupItem, errorItem, statsItem;

        readonly Icon iconOn, iconOff, iconSkipped;

        readonly Counters counters = new Counters();

        readonly Settings settings;

        readonly ListFile myWords, myFixes, excludedApps;

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

            statsPath = Path.Combine(folder, "stats.txt");

            lifetimePath = Path.Combine(folder, "lifetime.txt");

            ownExe = Path.GetFileName(Application.ExecutablePath);

            settings = new Settings(Path.Combine(folder, "settings.txt"));

            myWords = ListFile.MyWords(folder);

            myFixes = ListFile.MyFixes(folder);

            excludedApps = ListFile.ExcludedApps(folder);

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

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, delegate { ExitThread(); });

            menu.Items.AddRange(new ToolStripItem[] {

                title, errorItem, statsItem, new ToolStripSeparator(), enabledItem, pauseItem, appItem, recentItem,

                new ToolStripSeparator(), settingsItem, startupItem,

                new ToolStripSeparator(), exitItem });

            menu.Opening += delegate { RefreshMenu(); };

    

            tray = new NotifyIcon();

            tray.ContextMenuStrip = menu;

            tray.DoubleClick += delegate { Enabled = !Enabled; };

    

            candidatePopup = new CandidatePopup();

    

            IntPtr forceHandle = ui.Handle;

            corrector = new Corrector(spelling, myWords, myFixes, excludedApps, counters, settings, IntPtr.Zero);

            corrector.Corrected += OnCorrected;

            corrector.Undone += delegate(string typed, string fix) { typos.Count(typed, fix, -1); };

            corrector.Changed += OnChanged;

            corrector.CandidateOffered += delegate(string fix, string[] choices)

            {

                if (!settings.ShowCandidatePopup) return;

                ui.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate

                {

                    IntPtr fg = Native.GetForegroundWindow();

                    candidatePopup.ShowCandidates(fix, choices, fg);

                });

            };

            corrector.CandidateDismissed += delegate

            {

                ui.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate

                {

                    candidatePopup.Dismiss();

                });

            };

            corrector.Learned += delegate(string word)

            {

                ui.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate { Say("\"" + word + "\" will not be corrected any more (you undid it twice)."); });

            };

            // The hotkey gives no other sign that it worked, so say so.

            corrector.EnabledChanged += delegate

            {

                ui.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate { pausedUntil = DateTime.MinValue; ShowState(); AnnounceState(); });

            };

    

            ShowState();

            tray.Visible = true;

    

            housekeeping.Interval = 1000;

            housekeeping.Tick += delegate { Housekeeping(); };

            housekeeping.Start();

            tray.ShowBalloonTip(6000, AppName,

                "Running. Misspelled words are fixed when you press Space. Press Backspace right after a fix to undo it.",

                ToolTipIcon.Info);

            AppDomain.CurrentDomain.ProcessExit += delegate { DisposeResources(); };

            AppDomain.CurrentDomain.UnhandledException += delegate { DisposeResources(); };

            Application.ApplicationExit += delegate { DisposeResources(); };

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

            myFixes.Remove(typed);

            myFixes.Sync();

            myWords.Add(typed);

            myWords.Sync();

        }

    

        // From now on `typed` always becomes `fix`.

        public void AlwaysFix(string typed, string fix)

        {

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

            tray.Icon = !on ? iconOff : skipped ? iconSkipped : iconOn;

            tray.Text = Clip(AppName + (!on ? " - paused" + until : skipped ? " - skipped in " + currentApp : " - on"));

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

    

        void Housekeeping()

        {

            myWords.Sync();

            myFixes.Sync();

            excludedApps.Sync();

            if (settings.RememberTypos) typos.Save();

            WriteStats();

    

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

                "errors: " + counters.Errors + "\r\n" +

                "last error: " + (counters.LastError ?? "none") + "\r\n";

            if (body == lastStats) return;

            lastStats = body;

            try

            {

                File.WriteAllText(statsPath,

                    AppName + " " + Program.Version + " - counters only, never what you typed\r\n" +

                    "running since: " + startedAt.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +

                    "updated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + body);

                string life =

                    "words=" + (baseWords + counters.Words) + "\r\n" +

                    "corrections=" + (baseCorrections + counters.Corrections) + "\r\n" +

                    "undone=" + (baseUndone + counters.Undone) + "\r\n";

                if (life != lastLifetime)

                {

                    lastLifetime = life;

                    File.WriteAllText(lifetimePath, life);

                }

            }

            catch (IOException) { }

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

                using (Graphics g = Graphics.FromImage(bmp))

                using (Brush fill = new SolidBrush(back))

                using (Font font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))

                using (StringFormat center = new StringFormat())

                {

                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    g.FillEllipse(fill, 1, 1, 30, 30);

                    center.Alignment = StringAlignment.Center;

                    center.LineAlignment = StringAlignment.Center;

                    g.DrawString("Aa", font, Brushes.White, new RectangleF(0, 0, 32, 31), center);

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

