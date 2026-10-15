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

                             RememberTypos, SmartDashes, SmartArrows, SmartEllipsis,

                             SkipFullscreenGames = true, ShowCandidatePopup = true;

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

                    else if (key == "smartDashes") SmartDashes = value;

                    else if (key == "smartArrows") SmartArrows = value;

                    else if (key == "smartEllipsis") SmartEllipsis = value;

                    else if (key == "skipFullscreenGames") SkipFullscreenGames = value;

                    else if (key == "showCandidatePopup") ShowCandidatePopup = value;

                    else if (key == "mainLanguage") MainLanguage = text;

                    else if (key == "keyboardLayout") KeyboardLayout = text;

                    else if (key == "pauseHotkey") PauseHotkey = Hotkey.Parse(text) ?? PauseHotkey;

                    else if (key == "nextHotkey") NextHotkey = Hotkey.Parse(text) ?? NextHotkey;

                }

            }

            catch (IOException) { }

        }

    

        internal static void SafeWriteAllText(string targetPath, string content)

        {

            if (string.IsNullOrEmpty(targetPath)) return;

            string dir = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            File.WriteAllText(temp, content, Encoding.UTF8);

            if (File.Exists(targetPath))

            {

                try { File.Replace(temp, targetPath, null, true); }

                catch

                {

                    File.Delete(targetPath);

                    File.Move(temp, targetPath);

                }

            }

            else

            {

                File.Move(temp, targetPath);

            }

        }

    

        internal static void SafeWriteAllLines(string targetPath, string[] lines)

        {

            if (string.IsNullOrEmpty(targetPath)) return;

            string dir = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            File.WriteAllLines(temp, lines, Encoding.UTF8);

            if (File.Exists(targetPath))

            {

                try { File.Replace(temp, targetPath, null, true); }

                catch

                {

                    File.Delete(targetPath);

                    File.Move(temp, targetPath);

                }

            }

            else

            {

                File.Move(temp, targetPath);

            }

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

                    "smartDashes=" + Text(SmartDashes) +

                    "smartArrows=" + Text(SmartArrows) +

                    "smartEllipsis=" + Text(SmartEllipsis) +

                    "skipFullscreenGames=" + Text(SkipFullscreenGames) +

                    "showCandidatePopup=" + Text(ShowCandidatePopup) +

                    "mainLanguage=" + MainLanguage + "\r\n" +

                    "keyboardLayout=" + KeyboardLayout + "\r\n" +

                    "pauseHotkey=" + PauseHotkey + "\r\n" +

                    "nextHotkey=" + NextHotkey + "\r\n");

            }

            catch (IOException) { }

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

        bool unsaved;

    

        public readonly string FilePath;

    

        public ListFile(string path, string header, IEnumerable<string> defaults, bool pairs)

        {

            FilePath = path;

            this.header = header;

            this.pairs = pairs;

            if (File.Exists(path)) Reload();

            else

            {

                foreach (string entry in defaults) Put(entry);

                unsaved = true;

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

    

        void Reload()

        {

            try

            {

                DateTime stamp = File.GetLastWriteTimeUtc(FilePath);

                string[] lines = File.ReadAllLines(FilePath);

                lock (gate)

                {

                    loadedStamp = stamp;

                    entries.Clear();

                    foreach (string line in lines)

                    {

                        string entry = line.Trim();

                        if (entry.Length > 0 && !entry.StartsWith("#")) Put(entry);

                    }

                }

            }

            catch (IOException) { }

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

                unsaved = true;

            }

        }

    

        public void Remove(string entry)

        {

            int eq = pairs ? entry.IndexOf('=') : -1;

            lock (gate)

                if (entries.Remove(eq > 0 ? entry.Substring(0, eq).Trim() : entry)) unsaved = true;

        }

    

        // Saves changes, or picks up edits made in Notepad.

        public void Sync()

        {

            try

            {

                string[] toSave = null;

                lock (gate)

                {

                    if (unsaved) toSave = All();

                }

                if (toSave != null)

                {

                    string content = header + "\r\n" + string.Join("\r\n", toSave) + (toSave.Length > 0 ? "\r\n" : "");

                    Settings.SafeWriteAllText(FilePath, content);

                    lock (gate)

                    {

                        unsaved = false;

                        loadedStamp = File.GetLastWriteTimeUtc(FilePath);

                    }

                }

                else if (File.GetLastWriteTimeUtc(FilePath) != loadedStamp) Reload();

            }

            catch (IOException) { }

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

                "# The left side is one word, or two words (\"could of=could have\").", new string[0], true);

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

        bool unsaved;

    

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

                // Keep the file small: one-off slips go first.

                if (counts.Count > MaxEntries)

                    foreach (string once in new List<string>(counts.Keys))

                        if (counts[once] == 1) counts.Remove(once);

                unsaved = true;

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

            lock (gate)

            {

                if (!unsaved) return;

                lines = new string[counts.Count];

                int i = 0;

                foreach (KeyValuePair<string, int> entry in counts) lines[i++] = entry.Value + "\t" + entry.Key;

            }

            try

            {

                Settings.SafeWriteAllLines(path, lines);

                lock (gate) unsaved = false;

            }

            catch (IOException) { }

        }

    

        // Removes the file; the counts of this session stay in memory.

        public void Forget()

        {

            try { File.Delete(path); }

            catch (IOException) { }

            lock (gate) unsaved = true;

        }

    }

}

