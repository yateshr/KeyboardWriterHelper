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
    // ---- A keyboard shortcut such as Ctrl+Alt+K ----

    sealed class Hotkey
    {
        public readonly bool Ctrl, Alt, Shift;
        public readonly Keys Key;

        public Hotkey(bool ctrl, bool alt, bool shift, Keys key)
        {
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Key = key;
        }

        // Ctrl or Alt is required, so that a shortcut can never be a key used for typing.
        public bool IsValid
        {
            get
            {
                return (Ctrl || Alt) && Key != Keys.None && (Key & Keys.Modifiers) == 0 && Key != Keys.ControlKey &&
                       Key != Keys.ShiftKey && Key != Keys.Menu && Key != Keys.LWin && Key != Keys.RWin;
            }
        }

        // "Ctrl+Alt+K" -> the shortcut, or null if the text is not a usable one.
        public static Hotkey Parse(string text)
        {
            bool ctrl = false, alt = false, shift = false;
            Keys key = Keys.None;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                else if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') key = Keys.D0 + (part[0] - '0');
                else if (!Enum.TryParse(part, true, out key)) return null;
            }
            Hotkey hotkey = new Hotkey(ctrl, alt, shift, key);
            return hotkey.IsValid ? hotkey : null;
        }

        public override string ToString()
        {
            string name = Key >= Keys.D0 && Key <= Keys.D9 ? ((char)('0' + (Key - Keys.D0))).ToString() : Key.ToString();
            return (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + name;
        }

        public bool Matches(int vk, bool ctrl, bool alt, bool shift)
        {
            return vk == (int)Key && ctrl == Ctrl && alt == Alt && shift == Shift;
        }

        public bool Same(Hotkey other)
        {
            return other != null && Matches((int)other.Key, other.Ctrl, other.Alt, other.Shift);
        }
    }

    // ---- Options, kept in a small key=value file ----

    sealed class Settings
    {
        readonly string path;

        // Read on the keyboard thread, written by the settings window.
        public volatile bool FixOnPunctuation = true, CapitalizeSentences = true, FixTwoCapitals = true,
                             FixPhrases = true, DoubleSpacePeriod, UseContext = true, AcceptVariants = true,
                             RememberTypos, LearnPersonalVocabulary, SmartDashes, SmartArrows, SmartEllipsis,
                             SkipFullscreenGames = true, ShowCandidatePopup = true, SmartCompose;
        public volatile Hotkey PauseHotkey = Hotkey.Parse("Ctrl+Alt+K"), NextHotkey = Hotkey.Parse("Ctrl+Alt+N");
        // The English spelling that supplies the suggestions ("" = the first one installed).
        public volatile string MainLanguage = "";
        public volatile string KeyboardLayout = "Auto";

        // path = null keeps the defaults and saves nothing (the self-test).
        public Settings(string path)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) return;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), text = line.Substring(eq + 1).Trim();
                    bool value = text != "false";
                    if (key == "fixOnPunctuation") FixOnPunctuation = value;
                    else if (key == "capitalizeSentences") CapitalizeSentences = value;
                    else if (key == "fixTwoCapitals") FixTwoCapitals = value;
                    else if (key == "fixPhrases") FixPhrases = value;
                    else if (key == "doubleSpacePeriod") DoubleSpacePeriod = value;
                    else if (key == "useContext") UseContext = value;
                    else if (key == "acceptVariants") AcceptVariants = value;
                    else if (key == "rememberTypos") RememberTypos = value;
                    else if (key == "learnPersonalVocabulary") LearnPersonalVocabulary = text == "true";
                    else if (key == "smartDashes") SmartDashes = value;
                    else if (key == "smartArrows") SmartArrows = value;
                    else if (key == "smartEllipsis") SmartEllipsis = value;
                    else if (key == "skipFullscreenGames") SkipFullscreenGames = value;
                    else if (key == "showCandidatePopup") ShowCandidatePopup = value;
                    else if (key == "smartCompose") SmartCompose = value;
                    else if (key == "mainLanguage") MainLanguage = text;
                    else if (key == "keyboardLayout") KeyboardLayout = text;
                    else if (key == "pauseHotkey") PauseHotkey = Hotkey.Parse(text) ?? PauseHotkey;
                    else if (key == "nextHotkey") NextHotkey = Hotkey.Parse(text) ?? NextHotkey;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Writes a whole file so that a crash or a full disk never leaves it half
        // written: to a temporary file next to it, which then replaces it. Throws
        // IOException or UnauthorizedAccessException when that is not possible
        // (the file open elsewhere, read-only); the old file is then untouched.
        internal static void SafeWriteAllText(string targetPath, string content)
        {
            if (string.IsNullOrEmpty(targetPath)) return;
            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content, Encoding.UTF8);
                if (!File.Exists(targetPath)) File.Move(temp, targetPath);
                else
                {
                    // Copying over is not atomic, but unlike deleting first it never
                    // leaves no file at all (some network drives cannot Replace).
                    try { File.Replace(temp, targetPath, null, true); }
                    catch (PlatformNotSupportedException) { File.Copy(temp, targetPath, true); }
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static void SafeWriteAllLines(string targetPath, string[] lines)
        {
            SafeWriteAllText(targetPath, string.Join("\r\n", lines) + "\r\n");
        }

        public void Save()
        {
            if (path == null) return;
            try
            {
                SafeWriteAllText(path,
                    "fixOnPunctuation=" + Text(FixOnPunctuation) +
                    "capitalizeSentences=" + Text(CapitalizeSentences) +
                    "fixTwoCapitals=" + Text(FixTwoCapitals) +
                    "fixPhrases=" + Text(FixPhrases) +
                    "doubleSpacePeriod=" + Text(DoubleSpacePeriod) +
                    "useContext=" + Text(UseContext) +
                    "acceptVariants=" + Text(AcceptVariants) +
                    "rememberTypos=" + Text(RememberTypos) +
                    "learnPersonalVocabulary=" + Text(LearnPersonalVocabulary) +
                    "smartDashes=" + Text(SmartDashes) +
                    "smartArrows=" + Text(SmartArrows) +
                    "smartEllipsis=" + Text(SmartEllipsis) +
                    "skipFullscreenGames=" + Text(SkipFullscreenGames) +
                    "showCandidatePopup=" + Text(ShowCandidatePopup) +
                    "smartCompose=" + Text(SmartCompose) +
                    "mainLanguage=" + MainLanguage + "\r\n" +
                    "keyboardLayout=" + KeyboardLayout + "\r\n" +
                    "pauseHotkey=" + PauseHotkey + "\r\n" +
                    "nextHotkey=" + NextHotkey + "\r\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Text(bool value) { return (value ? "true" : "false") + "\r\n"; }

        public SpellOptions SpellOptions()
        {
            return new SpellOptions { MainLanguage = MainLanguage, AcceptVariants = AcceptVariants, UseContext = UseContext, KeyboardLayout = KeyboardLayout };
        }
    }

    // ---- A user-editable list kept in a text file, one entry per line ----
    // Used for the words never to correct, the programs never to correct in
    // and, as "typo=correction" lines, the user's own fixes.

    sealed class ListFile
    {
        readonly object gate = new object();
        // Entry -> what stands after its "=" (null in a plain list).
        readonly Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string header;
        readonly bool pairs;
        DateTime loadedStamp = DateTime.MinValue;
        // Edits not written yet, in order: (line or key, true = added). Kept so
        // that edits made in Notepad meanwhile can be merged instead of overwritten.
        readonly List<KeyValuePair<string, bool>> pending = new List<KeyValuePair<string, bool>>();
        // The file does not exist yet and must be written with the defaults.
        bool mustCreate;

        public readonly string FilePath;

        public ListFile(string path, string header, IEnumerable<string> defaults, bool pairs)
        {
            FilePath = path;
            this.header = header;
            this.pairs = pairs;
            if (File.Exists(path))
            {
                try
                {
                    loadedStamp = File.GetLastWriteTimeUtc(path);
                    Load(File.ReadAllLines(path));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                foreach (string entry in defaults) Put(entry);
                mustCreate = true;
                Sync();
            }
        }

        // Call with the gate held. Lines that make no sense are dropped.
        void Put(string line)
        {
            int eq = pairs ? line.IndexOf('=') : -1;
            if (!pairs) entries[line] = null;
            else if (eq > 0 && eq < line.Length - 1) entries[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }

        // Call with the gate held, or before other threads can see this list.
        void Load(string[] lines)
        {
            entries.Clear();
            foreach (string line in lines)
            {
                string entry = line.Trim();
                if (entry.Length > 0 && !entry.StartsWith("#")) Put(entry);
            }
        }

        // The key of an entry: the whole line, or what stands before "=".
        string KeyOf(string entry)
        {
            int eq = pairs ? entry.IndexOf('=') : -1;
            return eq > 0 ? entry.Substring(0, eq).Trim() : entry;
        }

        public bool Contains(string entry)
        {
            lock (gate) return entries.ContainsKey(entry);
        }

        // What stands after "entry=", or null.
        public string Get(string entry)
        {
            string value;
            lock (gate) return entries.TryGetValue(entry, out value) ? value : null;
        }

        // The lines of the file, sorted.
        public string[] All()
        {
            lock (gate)
            {
                string[] all = new string[entries.Count];
                int i = 0;
                foreach (KeyValuePair<string, string> entry in entries)
                    all[i++] = entry.Value == null ? entry.Key : entry.Key + "=" + entry.Value;
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                return all;
            }
        }

        // Add and Remove change memory only (they may run on the keyboard thread);
        // Sync() writes the file. A "key=value" list takes and gives whole lines.
        public void Add(string entry)
        {
            lock (gate)
            {
                Put(entry);
                pending.Add(new KeyValuePair<string, bool>(entry, true));
            }
        }

        public void Remove(string entry)
        {
            string key = KeyOf(entry);
            lock (gate)
                if (entries.Remove(key)) pending.Add(new KeyValuePair<string, bool>(key, false));
        }

        // Saves changes and picks up edits made in Notepad. When both happened,
        // the file as it is now is taken and the edits made here go on top.
        public void Sync()
        {
            try
            {
                DateTime onDisk = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                string[] diskLines = onDisk != DateTime.MinValue && onDisk != loadedStamp ? File.ReadAllLines(FilePath) : null;
                string[] toSave;
                int written;
                lock (gate)
                {
                    if (diskLines != null)
                    {
                        Load(diskLines);
                        loadedStamp = onDisk;
                        foreach (KeyValuePair<string, bool> edit in pending)
                        {
                            if (edit.Value) Put(edit.Key);
                            else entries.Remove(edit.Key);
                        }
                    }
                    if (pending.Count == 0 && !mustCreate) return;
                    toSave = All();
                    written = pending.Count;
                }
                string content = header + "\r\n" + string.Join("\r\n", toSave) + (toSave.Length > 0 ? "\r\n" : "");
                Settings.SafeWriteAllText(FilePath, content);
                lock (gate)
                {
                    // Edits made while the file was being written wait for the next Sync.
                    pending.RemoveRange(0, written);
                    mustCreate = false;
                    loadedStamp = File.GetLastWriteTimeUtc(FilePath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static ListFile MyWords(string folder)
        {
            return new ListFile(Path.Combine(folder, "my-words.txt"),
                "# One word per line. Words listed here are never auto-corrected.", new string[0], false);
        }

        public static ListFile MyFixes(string folder)
        {
            return new ListFile(Path.Combine(folder, "my-fixes.txt"),
                "# Your own fixes and shortcuts, one per line:  typo=correction\r\n" +
                "# The left side is one to four words (\"could of been=could have been\").", new string[0], true);
        }

        // Starts as data\excluded-apps.txt; from then on the list is the user's.
        public static ListFile ExcludedApps(string folder)
        {
            return new ListFile(Path.Combine(folder, "excluded-apps.txt"),
                "# One program per line (the name shown in Task Manager > Details). Nothing typed in these is corrected.",
                Data.Lines("excluded-apps.txt"), false);
        }
    }

    // ---- How often each fix was made ----
    // Kept in memory. Written to a file only if the user switches that on.

    sealed class TypoLog
    {
        const int MaxEntries = 2000;

        readonly object gate = new object();
        // "typed<tab>fix" -> times
        readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly string path;
        // Count changes made and written, so a change made while the file is
        // being written is not taken as saved.
        int changes, savedChanges;

        public TypoLog(string path, bool load)
        {
            this.path = path;
            if (!load || !File.Exists(path)) return;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] parts = line.Split('\t');
                    int times;
                    if (parts.Length == 3 && int.TryParse(parts[0], out times) && times > 0)
                        counts[parts[1] + "\t" + parts[2]] = times;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // change = +1 for a fix made, -1 for one taken back.
        public void Count(string typed, string fix, int change)
        {
            string key = typed + "\t" + fix;
            lock (gate)
            {
                int times;
                counts.TryGetValue(key, out times);
                times += change;
                if (times > 0) counts[key] = times;
                else counts.Remove(key);
                // Keep memory and disk bounded: prune single-use slips first, then lowest counts.
                if (counts.Count > MaxEntries)
                {
                    List<string> toRemove = new List<string>();
                    foreach (KeyValuePair<string, int> pair in counts)
                        if (pair.Value == 1) toRemove.Add(pair.Key);
                    foreach (string k in toRemove) counts.Remove(k);

                    if (counts.Count > MaxEntries)
                    {
                        List<KeyValuePair<string, int>> pairs = new List<KeyValuePair<string, int>>(counts);
                        pairs.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return a.Value.CompareTo(b.Value); });
                        int targetCount = MaxEntries * 3 / 4;
                        int removeN = counts.Count - targetCount;
                        for (int i = 0; i < removeN && i < pairs.Count; i++)
                            counts.Remove(pairs[i].Key);
                    }
                }
                changes++;
            }
        }

        // { typed, fix, times }, the most frequent first.
        public string[][] Top(int max)
        {
            List<string[]> all = new List<string[]>();
            lock (gate)
                foreach (KeyValuePair<string, int> entry in counts)
                {
                    string[] parts = entry.Key.Split('\t');
                    all.Add(new string[] { parts[0], parts[1], entry.Value.ToString() });
                }
            all.Sort(delegate(string[] x, string[] y)
            {
                int byCount = int.Parse(y[2]).CompareTo(int.Parse(x[2]));
                return byCount != 0 ? byCount : string.CompareOrdinal(x[0], y[0]);
            });
            if (all.Count > max) all.RemoveRange(max, all.Count - max);
            return all.ToArray();
        }

        public void Save()
        {
            string[] lines;
            int now;
            lock (gate)
            {
                if (changes == savedChanges) return;
                now = changes;
                lines = new string[counts.Count];
                int i = 0;
                foreach (KeyValuePair<string, int> entry in counts) lines[i++] = entry.Value + "\t" + entry.Key;
            }
            try
            {
                Settings.SafeWriteAllLines(path, lines);
                lock (gate) savedChanges = now;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Removes the file; the counts of this session stay in memory.
        public void Forget()
        {
            try { File.Delete(path); }
            catch (IOException) { }
            lock (gate) changes++;
        }
    }
}
