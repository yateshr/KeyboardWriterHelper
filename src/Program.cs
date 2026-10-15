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
    static class Program
    {
        // "1.1 (rev 14)": build.cmd takes it from version.txt and the git history.
        public static readonly string Version = ReadVersion();

        static string ReadVersion()
        {
            object[] found = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            return found.Length > 0 ? ((AssemblyInformationalVersionAttribute)found[0]).InformationalVersion : "(no version)";
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--version")
            {
                Out().WriteLine(TrayApp.AppName + " " + Version);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--check") return Check(args);
            if (args.Length >= 2 && args[0] == "--simulate") return Simulate(args[1]);
            if (args.Length >= 2 && args[0] == "--cases") return Cases(args);
            if (args.Length >= 2 && args[0] == "--eval") return Eval(args);
            if (args.Length >= 2 && args[0] == "--eval-ok") return EvalOk(args);
            if (args.Length >= 2 && args[0] == "--selftest") return SelfTest(args);
            if (args.Length == 1 && args[0] == "--lint") return Lint();
            if (args.Length == 1 && args[0] == "--layout")
            {
                // The letters of the keyboard layout in use, as key proximity sees them.
                TextWriter o = Out();
                foreach (string row in Native.LetterRows(Native.GetKeyboardLayout(0))) o.WriteLine("[" + row + "]");
                return 0;
            }

            bool first;
            using (Mutex single = new Mutex(true, "KeyboardWriterHelper.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show(TrayApp.AppName + " is already running (look in the system tray).",
                        TrayApp.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.EnableVisualStyles();
                // Every key waits for the keyboard thread; avoid long blocking
                // garbage collections while it runs.
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                try
                {
                    Application.Run(new TrayApp(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyboardWriterHelper")));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, TrayApp.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            return 0;
        }

        static TextWriter Out()
        {
            StreamWriter w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            w.AutoFlush = true;
            return w;
        }

        // A typing engine whose output goes to a string instead of the keyboard,
        // with its own "my words" and "my fixes".
        sealed class Desk
        {
            public readonly StringBuilder Screen = new StringBuilder();
            public readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Fixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Engine Engine;

            public Desk(Speller speller)
            {
                Engine = new Engine((Func<string, string, string, bool, Suggestion>)speller.Check, Words.Contains,
                    delegate(string typed)
                    {
                        string fix;
                        return Fixes.TryGetValue(typed, out fix) ? fix : null;
                    },
                    delegate(int backspaces, string typed)
                    {
                        Screen.Length -= Math.Min(backspaces, Screen.Length);
                        Screen.Append(typed);
                    },
                    (Func<string, string, string, string>)speller.Ranker.PredictNext);
                // Snippets give the same text on every run.
                Engine.Clock = delegate { return new DateTime(2026, 1, 2, 3, 4, 0); };
                Engine.Clipboard = delegate { return "line one\r\nline two\ttab"; };
            }

            // Types the text and returns what is on screen. {BS} stands for
            // Backspace, {NEXT} for the next-suggestion shortcut, {TAB} for Tab / Smart Compose.
            public string Type(string text)
            {
                foreach (char c in text.Replace("{BS}", "\b").Replace("{NEXT}", SelfTestForm.NextKey.ToString()).Replace("{TAB}", "\t"))
                {
                    if (c == '\b')
                    {
                        if (!Engine.Backspace() && Screen.Length > 0) Screen.Length--;
                    }
                    else if (c == '\t')
                    {
                        if (!Engine.AcceptPrediction()) Screen.Append('\t');
                    }
                    else if (c == SelfTestForm.NextKey) Engine.Next();
                    else if (!Engine.Char(c)) Screen.Append(c);
                }
                return Screen.ToString();
            }
        }

        // --check word [word...]: show the chosen fix and how the candidates ranked.
        // "before word" (in quotes) shows what the word before changes.
        static int Check(string[] args)
        {
            Speller speller = new Speller();
            TextWriter o = Out();
            o.WriteLine("dictionaries: " + speller.Dictionary.Languages);
            for (int i = 1; i < args.Length; i++)
            {
                string[] parts = args[i].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string word = parts[parts.Length - 1];
                string previous = parts.Length > 1 ? parts[parts.Length - 2].ToLowerInvariant() : null;
                Stopwatch sw = Stopwatch.StartNew();
                string typed = new Desk(speller).Type(args[i] + " ").TrimEnd();
                long ms = sw.ElapsedMilliseconds;
                bool certain;
                List<string> windows = speller.Dictionary.Candidates(word, out certain);
                if (windows == null)
                {
                    o.WriteLine(args[i] + " -> " + (typed == args[i] ? "(ok)" : typed + "   [built-in fix]"));
                    continue;
                }
                List<Scored> trace = new List<Scored>();
                speller.Ranker.Best(word, previous, windows, certain, false, speller.Dictionary.IsCorrect, trace, null);
                StringBuilder ranked = new StringBuilder();
                for (int k = 0; k < trace.Count && k < 5; k++)
                    ranked.AppendFormat(" {0}{1}({2:0.0}|{3:0.0})", trace[k].Word, trace[k].FromWindows ? "" : "*", trace[k].Cost, trace[k].Score);
                o.WriteLine(args[i] + " -> " + (typed == args[i] ? "(left alone)" : typed) + "   [" + ms + " ms" + (certain ? ", certain" : "") +
                    "]  windows: " + string.Join(", ", windows.ToArray()) + "   ranked:" + ranked);
            }
            return 0;
        }

        // --simulate "text": run text through the typing engine as if it were typed
        // and print what would end up on screen.
        static int Simulate(string text)
        {
            Out().WriteLine(new Desk(new Speller()).Type(text));
            return 0;
        }

        // What a word turns into when typed on its own and followed by a space.
        static string TypeAlone(Speller speller, string word)
        {
            return TypeAfter(speller, null, word);
        }

        // The same with another word typed before it (null for none).
        static string TypeAfter(Speller speller, string before, string word)
        {
            string lead = before == null ? "" : before + " ";
            string screen = new Desk(speller).Type(lead + word + " ");
            screen = screen.Substring(0, screen.Length - 1);
            return screen.StartsWith(lead) ? screen.Substring(lead.Length) : screen;
        }

        // What the "@" lines of a cases file have set up so far.
        sealed class CaseSetup
        {
            readonly List<string> words = new List<string>();
            readonly List<string[]> fixes = new List<string[]>();
            bool punctuation = true, capitals = true, twoCapitals = true, phrases = true, doubleSpace, context = true;
            bool smartDashes, smartArrows, smartEllipsis, smartCompose = true;

            // "word tarun", "fix brb=be right back", "<option> on|off", "reset".
            public bool Apply(string line)
            {
                string[] parts = line.Split(new char[] { ' ' }, 2);
                string name = parts[0], value = parts.Length > 1 ? parts[1].Trim() : "";
                int eq = value.IndexOf('=');
                bool on = value == "on";
                if (name == "reset")
                {
                    words.Clear();
                    fixes.Clear();
                    punctuation = capitals = twoCapitals = phrases = context = smartCompose = true;
                    doubleSpace = smartDashes = smartArrows = smartEllipsis = false;
                }
                else if (name == "word" && value.Length > 0) words.Add(value);
                else if (name == "fix" && eq > 0) fixes.Add(new string[] { value.Substring(0, eq), value.Substring(eq + 1) });
                else if (!on && value != "off") return false;
                else if (name == "punctuation") punctuation = on;
                else if (name == "capitals") capitals = on;
                else if (name == "twocapitals") twoCapitals = on;
                else if (name == "phrases") phrases = on;
                else if (name == "doublespace") doubleSpace = on;
                else if (name == "context") context = on;
                else if (name == "smartdashes") smartDashes = on;
                else if (name == "smartarrows") smartArrows = on;
                else if (name == "smartellipsis") smartEllipsis = on;
                else if (name == "smartcompose") smartCompose = on;
                else return false;
                return true;
            }

            public Desk NewDesk(Speller speller)
            {
                speller.Ranker.UseContext = context;
                Desk desk = new Desk(speller);
                foreach (string word in words) desk.Words.Add(word);
                foreach (string[] fix in fixes) desk.Fixes[fix[0]] = fix[1];
                desk.Engine.FixOnPunctuation = punctuation;
                desk.Engine.CapitalizeSentences = capitals;
                desk.Engine.FixTwoCapitals = twoCapitals;
                desk.Engine.FixPhrases = phrases;
                desk.Engine.DoubleSpacePeriod = doubleSpace;
                desk.Engine.SmartDashes = smartDashes;
                desk.Engine.SmartArrows = smartArrows;
                desk.Engine.SmartEllipsis = smartEllipsis;
                desk.Engine.SmartCompose = smartCompose;
                return desk;
            }
        }

        // --cases file...: typing tests through the engine. Each line is
        //     "what is typed" => "what must be on screen"
        // with {BS} and {NEXT} as in Desk.Type. Lines starting with @ set up the
        // cases after them (see CaseSetup.Apply); # starts a comment.
        // Exit code 1 when a case fails.
        static int Cases(string[] args)
        {
            const string Arrow = "\" => \"";
            TextWriter o = Out();
            Speller speller = new Speller();
            int passed = 0, failed = 0;
            for (int f = 1; f < args.Length; f++)
            {
                CaseSetup setup = new CaseSetup();
                string[] lines = File.ReadAllLines(args[f]);
                for (int n = 0; n < lines.Length; n++)
                {
                    string line = lines[n].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string where = Path.GetFileName(args[f]) + " line " + (n + 1) + ": ";
                    int arrow = line.IndexOf(Arrow);
                    if (line[0] == '@')
                    {
                        if (setup.Apply(line.Substring(1).Trim())) continue;
                        o.WriteLine(where + "cannot read this setup line");
                        failed++;
                    }
                    else if (line[0] != '"' || arrow < 1 || line.Length < arrow + Arrow.Length + 1 || line[line.Length - 1] != '"')
                    {
                        o.WriteLine(where + "cannot read this case");
                        failed++;
                    }
                    else
                    {
                        string typed = line.Substring(1, arrow - 1);
                        string expected = line.Substring(arrow + Arrow.Length, line.Length - arrow - Arrow.Length - 1).Replace("\\t", "\t");
                        string got = setup.NewDesk(speller).Type(typed);
                        if (got == expected) passed++;
                        else
                        {
                            failed++;
                            o.WriteLine(where + "\"" + typed + "\" gave \"" + got + "\", expected \"" + expected + "\"");
                        }
                    }
                }
            }
            o.WriteLine("typing cases: " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        // --selftest "text to type" ["expected result" ["skip-me"]]: end-to-end test
        // through the real hook. Exit code 0 = matched (or no expectation given).
        // "skip-me" runs it as a skipped program, where nothing may be corrected.
        static int SelfTest(string[] args)
        {
            TextWriter o = Out();
            Application.EnableVisualStyles();
            SelfTestForm form;
            try { form = new SelfTestForm(args[1], args.Length > 3 && args[3] == "skip-me"); }
            catch (Exception ex) { o.WriteLine("FAILED to start: " + ex.Message); return 2; }
            Application.Run(form);

            Counters c = form.Counters;
            o.WriteLine("typed   : " + args[1]);
            o.WriteLine("screen  : " + form.Screen);
            if (args.Length > 2) o.WriteLine("expected: " + args[2]);
            o.WriteLine("keys=" + c.Keys + " words=" + c.Words + " corrections=" + c.Corrections + " undone=" + c.Undone +
                " tooSlow=" + c.TooSlow + " slowestKeyMs=" + c.SlowestKeyMs + " errors=" + c.Errors +
                (c.LastError != null ? " lastError=" + c.LastError : ""));
            if (form.Problem != null) { o.WriteLine("ABORTED : " + form.Problem); return 2; }
            if (args.Length > 2)
            {
                bool pass = form.Screen == args[2];
                o.WriteLine(pass ? "PASS" : "FAIL");
                return pass ? 0 : 1;
            }
            return 0;
        }

        // Reads lines of "label: word word ..." from the files named in args.
        static List<string[]> ReadCases(string[] args, out bool misses, out bool caps, out string before)
        {
            misses = caps = false;
            before = null;
            List<string[]> cases = new List<string[]>(); // { label, word }
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--misses") { misses = true; continue; }
                if (args[i] == "--caps") { caps = true; continue; }
                if (args[i] == "--before" && i + 1 < args.Length) { before = args[++i]; continue; }
                if (args[i].StartsWith("--min-") || args[i].StartsWith("--max-")) { i++; continue; }
                foreach (string line in File.ReadAllLines(args[i]))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string label = line.Substring(0, colon).Trim();
                    foreach (string word in line.Substring(colon + 1).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        cases.Add(new string[] { label, word });
                }
            }
            return cases;
        }

        // --eval [--misses] [--before word] [--min-right %] [--max-wrong %] file...:
        // how many misspellings come out right. Lines are "intended: typed typed
        // ..."; alternatives for intended are separated by |. --before types each
        // one after that word, to see what the word before does to the answers.
        // Exit code 1 when a --min-/--max- limit is not met.
        static int Eval(string[] args)
        {
            bool misses, caps;
            string before;
            List<string[]> cases = ReadCases(args, out misses, out caps, out before);
            TextWriter o = Out();
            Speller speller = new Speller();
            int right = 0, wrong = 0, windowsFirst = 0;
            foreach (string[] c in cases)
            {
                string[] intended = c[0].Split('|');
                bool certain;
                List<string> windows = speller.Dictionary.Candidates(c[1], out certain);
                if (windows != null && windows.Count > 0 && Among(windows[0], intended)) windowsFirst++;

                string got = TypeAfter(speller, before, c[1]);
                if (Among(got, intended)) { right++; continue; }
                if (got != c[1]) wrong++;
                if (misses)
                    o.WriteLine("  miss: " + c[1] + " -> " + (got == c[1] ? "(left alone)" : got) + "   wanted " + c[0] +
                        "   windows: " + (windows == null ? "(accepts it)" : string.Join(", ", windows.ToArray())));
            }
            o.WriteLine("misspellings: " + cases.Count + "   (dictionaries: " + speller.Dictionary.Languages + ")");
            o.WriteLine("Windows' first suggestion right: " + Percent(windowsFirst, cases.Count));
            o.WriteLine("this app: right " + Percent(right, cases.Count) + ", changed to a wrong word " + Percent(wrong, cases.Count) +
                ", left alone " + Percent(cases.Count - right - wrong, cases.Count));
            bool ok = Within(o, "right", right, cases.Count, Limit(args, "--min-right"), true) &
                      Within(o, "changed to a wrong word", wrong, cases.Count, Limit(args, "--max-wrong"), false);
            return ok ? 0 : 1;
        }

        // --eval-ok [--misses] [--caps] [--before word] [--max-changed %] file...:
        // how many CORRECT words are left alone. Lines are "category: word word
        // ...". --caps types each word with a capital, as at the start of a
        // sentence. Exit code 1 when more than --max-changed are changed.
        static int EvalOk(string[] args)
        {
            bool misses, caps;
            string before;
            List<string[]> cases = ReadCases(args, out misses, out caps, out before);
            TextWriter o = Out();
            Speller speller = new Speller();
            Dictionary<string, int[]> byCategory = new Dictionary<string, int[]>(); // { total, changed, recapitalised }
            List<string> order = new List<string>();
            int[] all = new int[3];
            foreach (string[] c in cases)
            {
                string word = caps ? char.ToUpperInvariant(c[1][0]) + c[1].Substring(1) : c[1];
                int[] counts;
                if (!byCategory.TryGetValue(c[0], out counts))
                {
                    byCategory[c[0]] = counts = new int[3];
                    order.Add(c[0]);
                }
                counts[0]++;
                all[0]++;
                string got = TypeAfter(speller, before, word);
                if (got == word) continue;
                int kind = Same(got, word) ? 2 : 1;
                counts[kind]++;
                all[kind]++;
                if (misses && kind == 1) o.WriteLine("  changed: " + word + " -> " + got + "   (" + c[0] + ")");
            }
            foreach (string category in order)
            {
                int[] n = byCategory[category];
                o.WriteLine(category.PadRight(14) + " changed " + Percent(n[1], n[0]) + ", only re-capitalised " + n[2]);
            }
            o.WriteLine("ALL".PadRight(14) + " changed " + Percent(all[1], all[0]) + ", only re-capitalised " + all[2]);
            return Within(o, "correct words changed", all[1], all[0], Limit(args, "--max-changed"), false) ? 0 : 1;
        }

        // The percentage after a flag such as "--min-right 95", or NaN if not given.
        static double Limit(string[] args, string flag)
        {
            double value;
            for (int i = 1; i + 1 < args.Length; i++)
                if (args[i] == flag && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return value;
            return double.NaN;
        }

        // Is part/whole on the right side of the limit (a percentage)? Says so if not.
        static bool Within(TextWriter o, string what, int part, int whole, double limit, bool atLeast)
        {
            if (double.IsNaN(limit) || whole == 0) return true;
            double share = 100.0 * part / whole;
            if (atLeast ? share >= limit : share <= limit) return true;
            o.WriteLine("FAILED: " + what + " is " + share.ToString("0.0", CultureInfo.InvariantCulture) + "%, " +
                (atLeast ? "below" : "above") + " the limit of " + limit.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            return false;
        }

        // --lint: checks the built-in lists for entries that are listed twice or
        // contradict each other. Exit code 1 when there are any.
        static int Lint()
        {
            TextWriter o = Out();
            int problems = 0;
            foreach (string name in new string[] { "fixes.txt", "phrase-fixes.txt" })
            {
                HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in Data.Lines(name))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || eq == line.Length - 1)
                    {
                        o.WriteLine(name + ": cannot read \"" + line + "\"");
                        problems++;
                    }
                    else if (!keys.Add(line.Substring(0, eq).Trim()))
                    {
                        o.WriteLine(name + ": \"" + line.Substring(0, eq).Trim() + "\" is listed twice");
                        problems++;
                    }
                }
            }
            foreach (string name in new string[] { "never-correct.txt", "other-language.txt", "abbreviations.txt" })
            {
                HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in Data.Lines(name))
                    foreach (string word in line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        if (!words.Add(word))
                        {
                            o.WriteLine(name + ": \"" + word + "\" is listed twice");
                            problems++;
                        }
            }
            HashSet<string> never = Data.Words("never-correct.txt");
            foreach (KeyValuePair<string, string> fix in Data.Pairs("fixes.txt"))
                if (never.Contains(fix.Key))
                {
                    o.WriteLine("\"" + fix.Key + "\" is in both fixes.txt and never-correct.txt");
                    problems++;
                }
            o.WriteLine("built-in lists: " + (problems == 0 ? "ok" : problems + " problem(s)"));
            return problems == 0 ? 0 : 1;
        }

        static bool Among(string word, string[] choices)
        {
            foreach (string choice in choices)
                if (Same(word, choice)) return true;
            return false;
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        static string Percent(int part, int whole)
        {
            return part + "/" + whole + " = " + (whole == 0 ? 0 : 100.0 * part / whole).ToString("0.0") + "%";
        }
    }
}
