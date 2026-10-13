// Keyboard Writer Helper - system-wide spelling autocorrect for Windows.
// Watches typed words with a low-level keyboard hook and, when a word is
// finished with a space, replaces a misspelling with the most likely intended
// word. Nothing typed is stored or sent anywhere.
//
// Three threads:
//   UI        tray icon and menu.
//   Keyboard  the hooks and the typing state machine (Engine). It must NOT be an
//             STA thread: COM refuses outgoing calls from an STA thread while a
//             hook callback is being dispatched (RPC_E_CANTCALLOUT_ININPUTSYNCCALL),
//             and blocking waits on an STA thread pump messages, which would let
//             a second key re-enter the hook.
//   Spelling  the Windows dictionary and the ranking. The keyboard thread waits
//             for it only briefly, so a slow lookup can never stall typing.
//
// Build: build.cmd (uses the C# compiler that ships with Windows).

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardWriterHelper
{
    // ---- Windows Spell Checking API (COM) ----

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { [return: MarshalAs(UnmanagedType.Interface)] get; }
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        [return: MarshalAs(UnmanagedType.Interface)]
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    class SpellCheckerFactoryClass { }

    // Only the first three methods are used; the rest of the vtable is left undeclared.
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig]
        int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
        int CorrectiveAction { get; }
        string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    }

    sealed class WindowsDictionary
    {
        const int ActionReplace = 2, ActionDelete = 3;
        const int MaxSuggestions = 12;

        // checkers[0] supplies the suggestions; the others only widen what counts as correct.
        readonly List<ISpellChecker> checkers = new List<ISpellChecker>();

        public readonly string Languages;

        public WindowsDictionary()
        {
            ISpellCheckerFactory factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            List<string> tags = new List<string>();
            foreach (string tag in new string[] { "en-US", "en-GB", "en-IN", "en-AU", "en-CA" })
            {
                if (!factory.IsSupported(tag)) continue;
                checkers.Add(factory.CreateSpellChecker(tag));
                tags.Add(tag);
            }
            if (checkers.Count == 0)
                throw new InvalidOperationException("No English spell-check dictionary is installed in Windows.");
            Languages = string.Join(", ", tags.ToArray());
        }

        // A spelling accepted by any installed English variant is correct, so both
        // "color" and "colour" are left alone.
        public bool IsCorrect(string word)
        {
            int action;
            string replacement;
            foreach (ISpellChecker checker in checkers)
                if (!HasError(checker, word, out action, out replacement)) return true;
            return false;
        }

        // Windows' own candidates for a misspelled word, best first (possibly empty).
        // Null when the word is correct. certain = Windows has a fixed replacement
        // for this typo or offers exactly one word; measured, that answer is
        // practically always right, while the first of several is right two times in three.
        public List<string> Candidates(string word, out bool certain)
        {
            certain = false;
            int action = 0;
            string replacement = null;
            for (int i = 0; i < checkers.Count; i++)
            {
                int a;
                string r;
                if (!HasError(checkers[i], word, out a, out r)) return null;
                if (i == 0) { action = a; replacement = r; }
            }

            List<string> result = new List<string>();
            if (action == ActionDelete) return result;
            bool fixedReplacement = action == ActionReplace && !string.IsNullOrEmpty(replacement);
            if (fixedReplacement) result.Add(replacement);

            IEnumString suggestions = checkers[0].Suggest(word);
            try
            {
                string[] one = new string[1];
                while (result.Count < MaxSuggestions && suggestions.Next(1, one, IntPtr.Zero) == 0)
                    if (!string.IsNullOrEmpty(one[0]) && !result.Contains(one[0])) result.Add(one[0]);
            }
            finally { Marshal.ReleaseComObject(suggestions); }
            certain = fixedReplacement || result.Count == 1;
            return result;
        }

        static bool HasError(ISpellChecker checker, string word, out int action, out string replacement)
        {
            action = 0;
            replacement = null;
            IEnumSpellingError errors = checker.Check(word);
            try
            {
                ISpellingError error;
                if (errors.Next(out error) != 0 || error == null) return false;
                try
                {
                    action = error.CorrectiveAction;
                    if (action == ActionReplace) replacement = error.Replacement;
                }
                finally { Marshal.ReleaseComObject(error); }
                return true;
            }
            finally { Marshal.ReleaseComObject(errors); }
        }
    }

    // ---- Choosing the most likely intended word ----
    //
    // Windows' first suggestion is often not the best one ("improrvie" gives
    // "improvise"). Every candidate is scored by how common the word is and how
    // cheap the typing slip from it to what was typed would be. Not thread-safe.

    sealed class Scored
    {
        public string Word;
        public double Cost, Score;
        public bool FromWindows;
    }

    sealed class Ranker
    {
        // Tuned with --eval --grid on misspelling test sets.
        public double FrequencyWeight = 0.75; // score per factor of ten in how common a word is
        public double EditWeight = 3.0;       // score lost per unit of edit cost
        public double WindowsBonus = 0.5;     // trust in Windows' own order (first choice gets all of it)
        public bool UseOmissionCandidates = true;
        public double MinScore = -2.5;         // an uncertain best guess scoring below this is left alone
        public double MaxFixCost = 3.5;       // beyond this the best guess is not trusted

        const string FrequencyResource = "english-frequency.txt";
        const int MaxValidations = 8;
        const int MaxLength = 48;
        const int MaxOmittedLetters = 2;
        const double DoubledLetter = 0.5, Apostrophe = 0.3, Transposed = 0.5,
                     VowelSwap = 0.7, NearbySwap = 0.8, FirstLetterPenalty = 0.7,
                     MissingVowel = 0.6, MissingConsonant = 0.9;
        // Score penalties for kinds of candidate that are rarely what was meant.
        const double SplitPenalty = 2.0, ProperNounPenalty = 2.0;
        // Log10 count assumed for words missing from the frequency list.
        const float UnknownLog = 1.0f;
        // A regional spelling is at most this many factors of ten rarer than its twin.
        const double VariantFrequencyGap = 1.3;
        // A word missing from the frequency list may only win with a cheaper edit than this.
        const double ObscureFixCost = 1.0;
        // Words up to this length are handled cautiously (see Best).
        public const int ShortWord = 3;
        const double MeantMinLog = 4.0, MeantGap = 1.5, WeakPartLog = 4.5;
        const double CautiousMaxCost = 0.6, CautiousMinLog = 5.0, NameSafeLog = 3.0;

        static readonly bool[,] Near = BuildNearTable();

        readonly Dictionary<string, float> logByWord = new Dictionary<string, float>(StringComparer.Ordinal);
        // Plain a-z words only, used to propose candidates Windows did not offer.
        readonly List<string> words = new List<string>();
        readonly List<float> logs = new List<float>();
        readonly double[,] grid = new double[MaxLength + 1, MaxLength + 1];

        // Word list: "word count" per line, produced by build_frequency.py from two
        // public lists (OpenSubtitles 2018 counts, github.com/hermitdave/FrequencyWords,
        // CC-BY-SA-4.0; Google Web 1T counts, norvig.com/ngrams).
        public Ranker()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FrequencyResource);
            if (stream == null) throw new InvalidOperationException("The word-frequency list is missing from this build.");
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int space = line.IndexOf(' ');
                    if (space <= 0) continue;
                    long count;
                    if (!long.TryParse(line.Substring(space + 1), out count) || count <= 0) continue;
                    string word = line.Substring(0, space);
                    if (word.Length > MaxLength || logByWord.ContainsKey(word)) continue;
                    float log = (float)Math.Log10(count);
                    logByWord[word] = log;
                    if (word.Length >= 3 && IsPlain(word))
                    {
                        words.Add(word);
                        logs.Add(log);
                    }
                }
            }
        }

        // Picks the best replacement for a misspelled word, or null if nothing is
        // convincing. windows = the dictionary's suggestions in its own order;
        // certain = the dictionary vouches for the first one. cautious = the word
        // is short or capitalised, where a wrong guess is likely (abbreviations,
        // names, words from another language), so only an obvious fix is accepted.
        public string Best(string typed, IList<string> windows, bool certain, bool cautious,
            Func<string, bool> isCorrect, List<Scored> trace)
        {
            string lower = typed.ToLowerInvariant();
            if (lower.Length > MaxLength) return null;

            List<Scored> all = new List<Scored>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < windows.Count; i++)
            {
                string candidate = windows[i];
                string candidateLower = candidate.ToLowerInvariant();
                if (candidateLower.Length == 0 || candidateLower.Length > MaxLength || !seen.Add(candidateLower)) continue;
                if (IsWeakSplit(lower, candidateLower)) continue;
                double cost = Cost(lower, candidateLower);
                double score = FrequencyWeight * LogFrequency(candidateLower) - EditWeight * cost + WindowsBonus / (i + 1);
                // Typed in lower case, so a name is unlikely - unless only the capital was missing.
                if (candidate != candidateLower && candidateLower != lower && !candidateLower.StartsWith("i'"))
                    score -= ProperNounPenalty;
                all.Add(new Scored { Word = candidate, Cost = cost, Score = score, FromWindows = true });
            }

            // The letters are right and only capitals are missing ("english",
            // "github"): there is nothing to weigh. Not for very short tokens,
            // which are more often abbreviations ("rs", "nd", "imo").
            // Only when it is Windows' own first choice: "frist" also lists "Frist"
            // (a surname) after "first".
            if (all.Count > 0 && all[0].Cost == 0)
            {
                if (trace != null) trace.Add(all[0]);
                return lower.Length > ShortWord ? all[0].Word : null;
            }

            // Windows sometimes fails to offer the obvious word when letters were
            // simply left out ("avaible", "rember"). Add common words that contain
            // everything typed, in order, with at most two more letters.
            if (!certain && UseOmissionCandidates && lower.Length >= 4)
            {
                for (int k = 0; k < words.Count; k++)
                {
                    string word = words[k];
                    int more = word.Length - lower.Length;
                    if (more < 1 || more > MaxOmittedLetters || word[0] != lower[0]) continue;
                    if (!IsSubsequence(lower, word) || !seen.Add(word)) continue;
                    double cost = Cost(lower, word);
                    all.Add(new Scored { Word = word, Cost = cost, Score = FrequencyWeight * logs[k] - EditWeight * cost });
                }
            }

            // When the dictionary vouches for its answer, take it; otherwise rank.
            Scored winner = certain && all.Count > 0 ? all[0] : null;
            all.Sort(delegate(Scored x, Scored y) { return y.Score.CompareTo(x.Score); });
            if (trace != null) trace.AddRange(all);

            // The frequency list contains slang and typos, so anything Windows did
            // not suggest itself must pass the dictionary before it can count.
            int validations = 0;
            foreach (Scored s in all)
            {
                if (winner != null) break;
                if (!s.FromWindows)
                {
                    if (validations >= MaxValidations) continue;
                    validations++;
                    if (!isCorrect(s.Word)) continue;
                }
                winner = s;
            }
            if (winner == null || winner.Cost > MaxFixCost) return null;
            // A weak best guess is a coin toss; leaving the word alone costs less.
            if (!certain && winner.Score < MinScore) return null;
            string fix = winner.Word.ToLowerInvariant();
            double commonness = LogFrequency(fix);
            // Swapping an unknown word for an obscure one ("rexx" -> "roex") is a
            // guess, usually at a name.
            if (!certain && winner.Cost >= ObscureFixCost && commonness <= UnknownLog) return null;
            // A word people really use ("bruh", "yeet", "venv") that is nearly as
            // common as its replacement is most likely meant. A typo of a common
            // word ("teh") is far rarer than the word it was meant to be.
            if (!certain && winner.Cost >= ObscureFixCost && LogFrequency(lower) >= MeantMinLog &&
                LogFrequency(lower) >= commonness - MeantGap) return null;
            // "approx" -> "approx.": an abbreviation, not a misspelling.
            if (fix.EndsWith(".")) return null;
            if (IsSpellingVariant(lower, fix)) return null;
            if (cautious)
            {
                // A capitalised word the dictionary does not know is probably a
                // name; never turn it into an uncommon word ("Tarun" -> "Taron").
                if (char.IsUpper(typed[0]) && commonness < NameSafeLog) return null;
                // Unsure about a short or capitalised word: only an obvious slip
                // (two letters swapped, a doubled letter) of a very common word.
                if (!certain && (winner.Cost > CautiousMaxCost || commonness < CautiousMinLog)) return null;
            }
            return winner.Word;
        }

        // "publically" -> "public ally": Windows offers a missing space where the
        // parts are not both everyday words, which is never what was meant.
        bool IsWeakSplit(string typed, string candidate)
        {
            if (candidate.IndexOf(' ') < 0 || candidate.Replace(" ", "") != typed) return false;
            foreach (string part in candidate.Split(' '))
                if (part.Length < 3 || LogFrequency(part) < WeakPartLog) return true;
            return false;
        }

        // "color"/"colour", "center"/"centre", "canceled"/"cancelled": both are
        // correct English, whichever dictionaries happen to be installed. What was
        // typed must itself be a common spelling, or "realy" would pass as a
        // variant of "really".
        bool IsSpellingVariant(string typed, string fix)
        {
            return American(typed) == American(fix) && LogFrequency(typed) >= LogFrequency(fix) - VariantFrequencyGap;
        }

        static string American(string word)
        {
            string w = word.Replace("our", "or").Replace("ise", "ize").Replace("isi", "izi").Replace("isa", "iza")
                           .Replace("yse", "yze").Replace("ysi", "yzi").Replace("ll", "l").Replace("ogue", "og")
                           .Replace("ence", "ense").Replace("ae", "e").Replace("grey", "gray");
            if (w.EndsWith("tre") || w.EndsWith("bre")) w = w.Substring(0, w.Length - 2) + "er";
            return w;
        }

        double LogFrequency(string lower)
        {
            float log;
            if (logByWord.TryGetValue(lower, out log)) return log;

            // "a lot", "well-known": as likely as its rarest part, less a penalty
            // because a missing space is rarer than a misspelling.
            if (lower.IndexOf(' ') >= 0 || lower.IndexOf('-') >= 0)
            {
                double rarest = double.MaxValue;
                foreach (string part in lower.Split(new char[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
                    rarest = Math.Min(rarest, LogFrequency(part));
                return rarest == double.MaxValue ? UnknownLog : rarest - SplitPenalty;
            }

            // The list stores contractions split: "don't" is "don" + "'t".
            int apostrophe = lower.IndexOf('\'');
            if (apostrophe > 0 && apostrophe < lower.Length - 1)
            {
                float head, tail;
                if (logByWord.TryGetValue(lower.Substring(0, apostrophe), out head) &&
                    logByWord.TryGetValue(lower.Substring(apostrophe), out tail))
                    return Math.Min(head, tail);
            }
            return UnknownLog;
        }

        static bool IsSubsequence(string part, string whole)
        {
            int i = 0;
            for (int j = 0; j < whole.Length && i < part.Length; j++)
                if (part[i] == whole[j]) i++;
            return i == part.Length;
        }

        // Weighted Damerau-Levenshtein distance from what was typed (a) to a
        // candidate (b). Slips people actually make are cheaper: a doubled or
        // un-doubled letter, two letters swapped, a left-out vowel, a vowel for a
        // vowel, a neighbouring key.
        double Cost(string a, string b)
        {
            int n = a.Length, m = b.Length;
            double[,] d = grid;
            d[0, 0] = 0;
            for (int i = 1; i <= n; i++) d[i, 0] = d[i - 1, 0] + ExtraCost(a, i - 1);
            for (int j = 1; j <= m; j++) d[0, j] = d[0, j - 1] + MissingCost(b, j - 1);

            for (int i = 1; i <= n; i++)
            {
                char x = a[i - 1];
                double extra = ExtraCost(a, i - 1);
                for (int j = 1; j <= m; j++)
                {
                    char y = b[j - 1];
                    double best = d[i - 1, j - 1] + SwapCost(x, y);
                    double v = d[i - 1, j] + extra;
                    if (v < best) best = v;
                    v = d[i, j - 1] + MissingCost(b, j - 1);
                    if (v < best) best = v;
                    if (i > 1 && j > 1 && x != y && x == b[j - 2] && a[i - 2] == y)
                    {
                        v = d[i - 2, j - 2] + Transposed;
                        if (v < best) best = v;
                    }
                    d[i, j] = best;
                }
            }

            double cost = d[n, m];
            // People rarely get the first letter wrong, other than swapping the first two.
            if (a[0] != b[0] && !(n > 1 && m > 1 && a[0] == b[1] && a[1] == b[0])) cost += FirstLetterPenalty;
            return cost;
        }

        // A typed character that should not be there.
        static double ExtraCost(string a, int i)
        {
            char c = a[i];
            if (c == '\'') return Apostrophe;
            if ((i > 0 && a[i - 1] == c) || (i + 1 < a.Length && a[i + 1] == c)) return DoubledLetter;
            return 1.0;
        }

        // A character of the candidate that was not typed.
        static double MissingCost(string b, int j)
        {
            char c = b[j];
            if (c == '\'') return Apostrophe;
            if ((j > 0 && b[j - 1] == c) || (j + 1 < b.Length && b[j + 1] == c)) return DoubledLetter;
            if (c == ' ' || c == '-') return 1.0;
            return IsVowel(c) ? MissingVowel : MissingConsonant;
        }

        static double SwapCost(char x, char y)
        {
            if (x == y) return 0;
            if (IsVowel(x) && IsVowel(y)) return VowelSwap;
            if (x >= 'a' && x <= 'z' && y >= 'a' && y <= 'z' && Near[x - 'a', y - 'a']) return NearbySwap;
            return 1.0;
        }

        static bool IsVowel(char c) { return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u'; }

        // Letters that sit next to each other on a QWERTY keyboard, plus pairs
        // that sound alike.
        static bool[,] BuildNearTable()
        {
            bool[,] near = new bool[26, 26];
            string[] rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
            for (int r = 0; r < rows.Length; r++)
            {
                for (int i = 0; i < rows[r].Length; i++)
                {
                    if (i + 1 < rows[r].Length) Pair(near, rows[r][i], rows[r][i + 1]);
                    if (r + 1 < rows.Length)
                    {
                        // Each lower row is shifted half a key to the right.
                        if (i < rows[r + 1].Length) Pair(near, rows[r][i], rows[r + 1][i]);
                        if (i > 0 && i - 1 < rows[r + 1].Length) Pair(near, rows[r][i], rows[r + 1][i - 1]);
                    }
                }
            }
            foreach (string pair in new string[] { "ck", "cs", "sz", "iy", "ey", "fv", "gj", "td", "bp", "kq" })
                Pair(near, pair[0], pair[1]);
            return near;
        }

        static void Pair(bool[,] near, char x, char y)
        {
            near[x - 'a', y - 'a'] = true;
            near[y - 'a', x - 'a'] = true;
        }

        static bool IsPlain(string word)
        {
            foreach (char c in word)
                if (c < 'a' || c > 'z') return false;
            return true;
        }

    }

    // Dictionary + ranking. Create and use on one thread.
    sealed class Speller
    {
        public readonly WindowsDictionary Dictionary = new WindowsDictionary();
        public readonly Ranker Ranker = new Ranker();

        // Returns the replacement for a misspelled word, or null to leave it alone.
        // cautious: see Ranker.Best.
        public string Fix(string word, bool cautious)
        {
            bool certain;
            List<string> windows = Dictionary.Candidates(word, out certain);
            if (windows == null) return null;
            string best = Ranker.Best(word, windows, certain, cautious, Dictionary.IsCorrect, null);
            return best == word ? null : best;
        }
    }

    // Counts only - never the text that was typed.
    sealed class Counters
    {
        public int Keys, Words, Corrections, Undone, TooSlow, Errors, SlowestKeyMs;
        public string LastError;

        public void Error(Exception ex)
        {
            Errors++;
            LastError = ex.GetType().Name + ": " + ex.Message.Trim();
        }
    }

    // Runs the Speller on its own thread and remembers the answers.
    sealed class SpellService
    {
        sealed class Request
        {
            public string Word, Result;
            public bool Cautious;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            // Cautious and normal lookups of one word can differ.
            public string Key { get { return (Cautious ? "?" : "") + Word; } }
        }

        readonly BlockingCollection<Request> queue = new BlockingCollection<Request>();
        readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Counters counters;
        Exception startupError;

        public string Languages { get; private set; }

        public SpellService(Counters counters)
        {
            this.counters = counters;
            Thread worker = new Thread(Work);
            worker.Name = "Spelling";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            ready.WaitOne();
            if (startupError != null) throw new InvalidOperationException(startupError.Message, startupError);
        }

        void Work()
        {
            Speller speller = null;
            try
            {
                speller = new Speller();
                Languages = speller.Dictionary.Languages;
                // The first lookup loads the dictionary; pay for that now, not on a keystroke.
                speller.Fix("speling", false);
            }
            catch (Exception ex) { startupError = ex; }
            finally { ready.Set(); }
            if (startupError != null) return;

            foreach (Request request in queue.GetConsumingEnumerable())
            {
                string result = null;
                try { result = speller.Fix(request.Word, request.Cautious); }
                catch (Exception ex) { counters.Error(ex); }
                lock (cache)
                {
                    if (cache.Count > 5000) cache.Clear();
                    cache[request.Key] = result;
                }
                request.Result = result;
                request.Done.Set();
            }
        }

        // Call from a non-STA thread. If the answer is not ready within budgetMs the
        // word is left alone this time (tooSlow) and the answer is cached for the next.
        public string Suggest(string word, bool cautious, int budgetMs, out bool tooSlow)
        {
            tooSlow = false;
            Request request = new Request { Word = word, Cautious = cautious };
            string cached;
            lock (cache)
                if (cache.TryGetValue(request.Key, out cached)) return cached;

            queue.Add(request);
            if (request.Done.WaitOne(budgetMs)) return request.Result;
            tooSlow = true;
            return null;
        }
    }

    // ---- Options, kept in a small key=value file ----

    sealed class Settings
    {
        readonly string path;

        // Read on the keyboard thread, written by the settings window.
        public volatile bool FixOnPunctuation = true, CapitalizeSentences = true;

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
                    bool value = line.Substring(eq + 1).Trim() != "false";
                    string key = line.Substring(0, eq).Trim();
                    if (key == "fixOnPunctuation") FixOnPunctuation = value;
                    else if (key == "capitalizeSentences") CapitalizeSentences = value;
                }
            }
            catch (IOException) { }
        }

        public void Save()
        {
            if (path == null) return;
            try
            {
                File.WriteAllText(path,
                    "fixOnPunctuation=" + (FixOnPunctuation ? "true" : "false") + "\r\n" +
                    "capitalizeSentences=" + (CapitalizeSentences ? "true" : "false") + "\r\n");
            }
            catch (IOException) { }
        }
    }

    // ---- A user-editable list kept in a text file, one entry per line ----
    // Used for the words never to correct and the programs never to correct in.

    sealed class ListFile
    {
        readonly object gate = new object();
        readonly HashSet<string> entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly string header;
        DateTime loadedStamp = DateTime.MinValue;
        bool unsaved;

        public readonly string FilePath;

        public ListFile(string path, string header, string[] defaults)
        {
            FilePath = path;
            this.header = header;
            if (File.Exists(path)) Reload();
            else
            {
                foreach (string entry in defaults) entries.Add(entry);
                unsaved = true;
                Sync();
            }
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
                        if (entry.Length > 0 && !entry.StartsWith("#")) entries.Add(entry);
                    }
                }
            }
            catch (IOException) { }
        }

        public bool Contains(string entry)
        {
            lock (gate) return entries.Contains(entry);
        }

        public string[] All()
        {
            lock (gate)
            {
                string[] all = new string[entries.Count];
                entries.CopyTo(all);
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                return all;
            }
        }

        // Add and Remove change memory only (they may run on the keyboard thread);
        // Sync() writes the file.
        public void Add(string entry)
        {
            lock (gate)
                if (entries.Add(entry)) unsaved = true;
        }

        public void Remove(string entry)
        {
            lock (gate)
                if (entries.Remove(entry)) unsaved = true;
        }

        // Saves changes, or picks up edits made in Notepad.
        public void Sync()
        {
            try
            {
                string[] toSave = null;
                lock (gate)
                {
                    if (unsaved)
                    {
                        toSave = new string[entries.Count];
                        entries.CopyTo(toSave);
                        unsaved = false;
                    }
                }
                if (toSave != null)
                {
                    Array.Sort(toSave, StringComparer.OrdinalIgnoreCase);
                    File.WriteAllText(FilePath, header + "\r\n" + string.Join("\r\n", toSave) + (toSave.Length > 0 ? "\r\n" : ""));
                    lock (gate) loadedStamp = File.GetLastWriteTimeUtc(FilePath);
                }
                else if (File.GetLastWriteTimeUtc(FilePath) != loadedStamp) Reload();
            }
            catch (IOException) { }
        }

        public static ListFile MyWords(string folder)
        {
            return new ListFile(Path.Combine(folder, "my-words.txt"),
                "# One word per line. Words listed here are never auto-corrected.", new string[0]);
        }

        // Terminals (a changed command runs as something else), password managers
        // and remote-desktop clients. Code editors are not listed: people also
        // write prose in them.
        public static ListFile ExcludedApps(string folder)
        {
            return new ListFile(Path.Combine(folder, "excluded-apps.txt"),
                "# One program per line (the name shown in Task Manager > Details). Nothing typed in these is corrected.",
                new string[] {
                    "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
                    "wsl.exe", "bash.exe", "mintty.exe", "alacritty.exe", "wezterm-gui.exe", "putty.exe",
                    "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe",
                    "mstsc.exe", "vmconnect.exe", "vmware.exe", "VirtualBoxVM.exe", "TeamViewer.exe", "AnyDesk.exe" });
        }
    }

    // ---- Typing state machine (no Windows dependencies, so it can be simulated) ----

    sealed class Engine
    {
        sealed class Undo { public string Original, Fix, Terminator; public bool AtSentenceStart; }

        const int MaxWordLength = 40;

        // Typos that are themselves (rare) dictionary words, plus a few fixed answers.
        static readonly Dictionary<string, string> CommonFixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "im", "I'm" }, { "ive", "I've" }, { "wich", "which" }, { "thier", "their" }, { "alot", "a lot" },
            { "dont", "don't" }, { "cant", "can't" }, { "wont", "won't" }, { "didnt", "didn't" },
            { "doesnt", "doesn't" }, { "isnt", "isn't" }, { "wasnt", "wasn't" }, { "arent", "aren't" },
            { "werent", "weren't" }, { "havent", "haven't" }, { "hasnt", "hasn't" }, { "hadnt", "hadn't" },
            { "couldnt", "couldn't" }, { "shouldnt", "shouldn't" }, { "wouldnt", "wouldn't" },
            { "thats", "that's" }, { "whats", "what's" }, { "youre", "you're" }, { "theyre", "they're" },
            { "tommorow", "tomorrow" }, { "untill", "until" }, { "wierd", "weird" }, { "definatly", "definitely" },
            { "infact", "in fact" }, { "incase", "in case" }, { "atleast", "at least" }, { "ofcourse", "of course" },
            { "eachother", "each other" }, { "abit", "a bit" }, { "infront", "in front" }, { "inspite", "in spite" },
            { "donot", "do not" }, { "iam", "I am" }, { "thankyou", "thank you" },
            { "hes", "he's" }, { "shes", "she's" }, { "weve", "we've" }, { "youve", "you've" }, { "theyve", "they've" },
            { "itll", "it'll" }, { "thatll", "that'll" }, { "heres", "here's" }, { "theres", "there's" },
            { "wheres", "where's" }, { "hows", "how's" }, { "whos", "who's" }, { "wouldve", "would've" },
            { "couldve", "could've" }, { "shouldve", "should've" },
            { "becoz", "because" }, { "bcoz", "because" }, { "becuz", "because" }, { "enuf", "enough" },
            { "verry", "very" }, { "beter", "better" }, { "probly", "probably" }, { "tounge", "tongue" },
            { "tjat", "that" }, { "yhe", "the" }, { "rhe", "the" }, { "amd", "and" }, { "witj", "with" },
            { "jnow", "know" }, { "knoe", "know" }, { "kniw", "know" }, { "frim", "from" }, { "evry", "every" },
            { "woud", "would" }, { "shud", "should" }, { "culd", "could" }, { "chek", "check" }
        };

        // Commands, code words and abbreviations the dictionary does not know and
        // would turn into something else ("mkdir" -> "media", "elif" -> "elf").
        static readonly HashSet<string> NeverCorrect = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "npm", "npx", "pnpm", "yarn", "git", "sudo", "apt", "ssh", "scp", "pwd", "cls", "wget", "curl", "mkdir",
            "rmdir", "chmod", "chown", "grep", "sed", "awk", "cd", "ls", "rm", "cp", "mv", "nano", "vim", "pip",
            "conda", "dotnet", "nuget", "rustc", "kubectl", "wsl", "zsh", "cmd", "ipconfig", "localhost",
            "const", "var", "func", "def", "elif", "init", "async", "await", "int", "str", "bool", "enum", "struct",
            "impl", "args", "argv", "kwargs", "param", "params", "config", "configs", "env", "src", "dist", "stdin",
            "stdout", "stderr", "printf", "println", "typeof", "sizeof", "readonly", "namespace", "boolean", "tuple",
            "dict", "regex", "mutex", "endif", "ifdef", "malloc", "json", "yaml", "toml", "html", "css", "js", "ts",
            "tsx", "jsx", "py", "api", "apis", "url", "urls", "uri", "http", "https", "sql", "db", "repo", "repos",
            "dev", "prod", "admin", "auth", "oauth", "login", "logout", "signup", "signin", "username", "frontend",
            "backend", "fullstack", "middleware", "webhook", "webhooks", "endpoint", "endpoints", "sync", "idx",
            "cnt", "ctx", "req", "res", "tmp", "btn", "msg", "pkg", "cfg", "lib", "libs", "util", "utils", "app",
            "apps", "ui", "ux", "ai", "ok", "wifi", "gray", "vs", "etc", "approx", "ie", "eg",
            "vscode", "chatgpt", "openai", "gpu", "cpu", "usb", "numpy", "pytest", "nodejs", "nextjs", "reactjs",
            "supabase", "vercel", "copilot", "figma", "canva", "proj", "impl", "misc", "govt", "dept", "mgmt",
            "hrs", "mins", "secs", "nos", "qty", "inr", "usd",
            // Chat shorthand people type on purpose.
            "ur", "bro", "gud", "nite", "lol", "lmao", "rofl", "omg", "idk", "imo", "imho", "tbh", "fyi", "asap",
            "brb", "haha", "hehe", "hmm", "ya", "yea", "yeah", "yep", "yup", "nope", "nah", "gonna", "wanna",
            "gotta", "kinda", "sorta", "dunno", "lemme", "gimme", "coz", "cuz", "pls", "plz", "thx", "thnx",
            "tmrw", "tmr", "abt", "frnd", "kk", "okk", "hii", "heyy", "tc", "gn", "gm", "np", "ty",
            // Everyday Indian words and services.
            "dosa", "idli", "daal", "upi", "kyc", "otp", "emi", "ifsc", "neft", "paytm", "aadhaar", "aadhar",
            "timepass", "updation", "crorepati", "saheb", "ustad", "mehendi", "jio", "swiggy", "zomato",
            "flipkart", "phonepe", "gpay", "tcs",
            // More slang, tools and short forms the dictionary turns into real words.
            "yo", "bruh", "bday", "wassup", "fave", "yeet", "simp", "highkey", "lowkey", "fomo", "srry", "sry",
            "aint", "eslint", "venv", "mongodb", "postgres", "postgresql", "mysql", "sqlite", "redis", "nginx",
            "regd", "recd", "engg", "pune", "iyer", "tarun"
        };

        // Common Hindi and Gujarati words as typed in English letters. Seeing one
        // means this line is not English, so nothing more in it is corrected.
        // Words that are also English ("to", "pan", "tame") or look like a slip
        // of a common English word ("tha", "ane") are deliberately absent.
        static readonly HashSet<string> OtherLanguage = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hai", "hain", "hoon", "kya", "kyun", "kyu", "kaise", "kaisa", "kab", "kahan", "kaha",
            "kaun", "kitna", "kitne", "nahi", "nahin", "nhi", "haan", "yaar", "bhai", "bhaiya", "didi",
            "acha", "accha", "achha", "theek", "thik", "chalo", "jaldi", "abhi", "aaj", "kal", "parso", "bahut",
            "bohot", "thoda", "zyada", "jyada", "mera", "meri", "tera", "teri", "tumhara", "hamara", "apna",
            "apni", "aap", "tum", "mujhe", "tujhe", "usko", "isko", "unko", "kuch", "sabhi", "koi", "kisi",
            "liye", "wala", "wali", "raha", "rahi", "rahe", "gaya", "gayi", "gaye", "karo", "karna", "karta",
            "karti", "karte", "kiya", "hoga", "hogi", "hua", "hui", "aur", "lekin", "magar", "phir", "toh",
            "bhi", "kyunki", "isliye", "agar", "yahan", "wahan", "idhar", "udhar", "andar", "bahar", "upar",
            "neeche", "paisa", "paise", "ghar", "kaam", "baat", "raat", "subah", "shaam", "dost", "pyaar",
            "zindagi", "namaste", "dhanyavad", "shukriya", "arre", "arey", "oye", "matlab", "sahi", "galat",
            "milte", "milna", "dekho", "dekh", "suno", "bolo", "khana", "pani", "ji",
            "kem", "cho", "chho", "che", "chhe", "chu", "chhu", "shu", "tamne", "tamaru", "tamari",
            "tamara", "maru", "mari", "ame", "apde", "aapde", "nathi", "hatu", "hati", "kare",
            "karvu", "karu", "kari", "avjo", "aavjo", "saru", "saras", "majama", "maja", "kyare", "ketla",
            "ketlu", "kaka", "kaki", "masi", "barabar", "khub", "aaje", "thayu",
            "gayo", "gayu", "jaie", "aavo", "aavu", "aavsho", "karsho", "joiye", "gamtu", "chalse"
        };

        // A full stop after these is not the end of a sentence.
        static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dr", "mr", "mrs", "ms", "prof", "sr", "jr", "st", "vs", "etc", "inc", "ltd", "co", "no", "fig",
            "approx", "min", "max", "dept", "govt", "mgmt", "eg", "ie", "al", "ca", "est", "ref", "tel", "ext"
        };

        readonly Func<string, bool, string> suggest; // (word, cautious) -> fix or null
        readonly Func<string, bool> isIgnored;
        readonly Action<int, string> emit; // (backspaces, text to type)

        readonly StringBuilder word = new StringBuilder();
        // Closing punctuation typed after the word, e.g. the ")." in "wrod). "
        readonly StringBuilder trail = new StringBuilder();
        // True while the caret sits in text we cannot vouch for (after a paste, an
        // arrow key, a URL-like token...). Cleared by the next whitespace.
        bool tainted;
        bool afterSentenceEnd = true;
        bool wordAtSentenceStart;
        // Set once a Hindi or Gujarati word is seen; lasts until the next clean break.
        bool otherLanguage;
        // The word an undo just put back: it is not to be corrected again.
        string keepAsTyped;
        Undo undo;
        // The previous word ended a sentence, so this one gets a capital letter.
        bool capNext;

        // Fix a word as soon as , ; : ! or ? follows it (a full stop waits for the
        // space, because "example.com" is not a sentence end).
        public volatile bool FixOnPunctuation = true;
        public volatile bool CapitalizeSentences = true;

        public event Action<string, string> Corrected;
        public event Action<string> Undone;

        public Engine(Func<string, bool, string> suggest, Func<string, bool> isIgnored, Action<int, string> emit)
        {
            this.suggest = suggest;
            this.isIgnored = isIgnored;
            this.emit = emit;
        }

        // clean = the caret is most likely at the start of fresh text.
        public void Break(bool clean)
        {
            Clear();
            undo = null;
            capNext = false;
            tainted = !clean;
            if (clean)
            {
                afterSentenceEnd = true;
                otherLanguage = false;
            }
        }

        // Returns true when the key was replaced by emitted text and must be swallowed.
        public bool Char(char c)
        {
            undo = null;

            if (char.IsLetter(c) || (c == '\'' && word.Length > 0 && trail.Length == 0))
            {
                if (tainted) return false;
                // A letter right after punctuation: "example.com", "word,word". Not prose.
                if (trail.Length > 0) { Clear(); tainted = true; return false; }
                if (word.Length == 0)
                {
                    wordAtSentenceStart = afterSentenceEnd;
                    afterSentenceEnd = false;
                }
                word.Append(c);
                if (word.Length > MaxWordLength) { Clear(); tainted = true; }
                return false;
            }

            if (c == ' ')
            {
                bool hadWord = !tainted && word.Length > 0;
                string last = word.ToString();
                string ending = trail.ToString().TrimEnd(')', ']', '}', '"');
                bool swallowed = TryCorrect(trail + " ");
                Clear();
                tainted = false;
                if (hadWord)
                    capNext = ending.Length > 0 && IsSentenceEnd(ending[ending.Length - 1]) &&
                              (ending[ending.Length - 1] != '.' || !IsAbbreviation(last));
                return swallowed;
            }

            // Punctuation right after a word: fix it now if the setting is on.
            if (FixOnPunctuation && !tainted && word.Length > 0 && trail.Length == 0 && ",;:!?".IndexOf(c) >= 0 &&
                TryCorrect(c.ToString()))
            {
                if (IsSentenceEnd(c)) afterSentenceEnd = true;
                capNext = false;
                Clear();
                // Whatever follows with no space is part of the same token.
                tainted = true;
                return true;
            }

            // Punctuation after a word is held back: the word is only corrected once
            // the following space shows the token really ended there.
            if (!tainted && word.Length > 0 && IsCloser(c))
            {
                trail.Append(c);
                if (IsSentenceEnd(c)) afterSentenceEnd = true;
                return false;
            }

            if (!tainted && word.Length == 0 && IsOpener(c)) return false;

            if (IsSentenceEnd(c)) afterSentenceEnd = true;
            Clear();
            capNext = false;
            tainted = true;
            return false;
        }

        static bool IsAbbreviation(string w)
        {
            return w.Length == 1 || Abbreviations.Contains(w);
        }

        void Clear()
        {
            word.Length = 0;
            trail.Length = 0;
            keepAsTyped = null;
        }

        // Backspace straight after a correction works as on a phone keyboard: it
        // removes the space and puts the word back as typed, ready to be edited,
        // and that word is not corrected again. Otherwise it edits the tracked word.
        public bool Backspace()
        {
            if (undo != null)
            {
                Undo u = undo;
                undo = null;
                string punctuation = u.Terminator.Substring(0, u.Terminator.Length - 1);
                emit(u.Fix.Length + u.Terminator.Length, u.Original + punctuation);
                Clear();
                word.Append(u.Original);
                trail.Append(punctuation);
                keepAsTyped = u.Original;
                tainted = false;
                wordAtSentenceStart = u.AtSentenceStart;
                if (Undone != null) Undone(u.Original);
                return true;
            }
            if (trail.Length > 0) trail.Length--;
            else if (word.Length > 0) word.Length--;
            else tainted = true;
            return false;
        }

        // terminator = any held-back punctuation plus the space being typed.
        bool TryCorrect(string terminator)
        {
            if (tainted || word.Length == 0) return false;
            string original = word.ToString();
            if (original == keepAsTyped) return false;
            string fix = Decide(original, terminator);
            if (capNext && CapitalizeSentences) fix = Capitalise(original, fix);
            if (fix == null) return false;
            // The space itself has not reached the screen yet, hence the -1.
            emit(original.Length + terminator.Length - 1, fix + terminator);
            undo = new Undo { Original = original, Fix = fix, Terminator = terminator, AtSentenceStart = wordAtSentenceStart };
            if (Corrected != null) Corrected(original, fix);
            return true;
        }

        string Decide(string w, string terminator)
        {
            if (OtherLanguage.Contains(w)) otherLanguage = true;
            if (otherLanguage || isIgnored(w)) return null;
            // "i think", "i, too" - but not the list markers "i." and "i)".
            if (w == "i") return " ,?!".IndexOf(terminator[0]) >= 0 ? "I" : null;
            if (w.Length < 2 || w[w.Length - 1] == '\'') return null;
            // Acronyms and camelCase are deliberate.
            for (int i = 1; i < w.Length; i++)
                if (char.IsUpper(w[i])) return null;
            // Abbreviations (npm, ctx, dr) and stretched words (sooo) are not misspellings.
            if (NeverCorrect.Contains(w) || !HasVowel(w) || HasTripledLetter(w)) return null;

            bool capitalised = char.IsUpper(w[0]);
            string fix;
            if (!CommonFixes.TryGetValue(w, out fix))
            {
                fix = suggest(w, capitalised || w.Length <= Ranker.ShortWord);
                if (fix == null) return null;
                // A capital mid-sentence is most likely a name: only accept a fix
                // that is itself a capitalised word ("Febuary" -> "February").
                if (capitalised && !wordAtSentenceStart && !char.IsUpper(fix[0])) return null;
            }
            if (capitalised && char.IsLower(fix[0]))
                fix = char.ToUpperInvariant(fix[0]) + fix.Substring(1);
            return fix == w ? null : fix;
        }

        // The first word of a sentence gets a capital - unless it is something the
        // user deliberately writes in lower case.
        string Capitalise(string w, string fix)
        {
            string target = fix ?? w;
            if (!char.IsLower(target[0])) return fix;
            if (fix == null)
            {
                if (otherLanguage || isIgnored(w) || NeverCorrect.Contains(w) || w.EndsWith("'")) return null;
                for (int i = 1; i < w.Length; i++)
                    if (char.IsUpper(w[i])) return null;
            }
            return char.ToUpperInvariant(target[0]) + target.Substring(1);
        }

        static bool HasVowel(string w)
        {
            foreach (char c in w)
                if ("aeiouyAEIOUY".IndexOf(c) >= 0) return true;
            return false;
        }

        static bool HasTripledLetter(string w)
        {
            for (int i = 2; i < w.Length; i++)
                if (w[i] == w[i - 1] && w[i] == w[i - 2]) return true;
            return false;
        }

        static bool IsSentenceEnd(char c) { return c == '.' || c == '!' || c == '?'; }
        static bool IsCloser(char c) { return ".,;:!?)]}\"".IndexOf(c) >= 0; }
        static bool IsOpener(char c) { return "([{\"'".IndexOf(c) >= 0; }
    }

    // ---- Win32 ----

    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
        public const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        public const uint LLKHF_INJECTED = 0x10;
        public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
        public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
                         VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
                         VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        public const int LANG_ENGLISH = 0x09;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern short VkKeyScanW(char ch);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // File name of the program that owns a window ("notepad.exe"), or "" if unknown.
        public static string ProcessNameOf(IntPtr window)
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == 0) return "";
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return "";
            try
            {
                StringBuilder path = new StringBuilder(1024);
                int size = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref size) ? Path.GetFileName(path.ToString()) : "";
            }
            finally { CloseHandle(process); }
        }

        public static void AddKey(List<INPUT> inputs, ushort vk, ushort scan, uint flags, IntPtr extraInfo)
        {
            INPUT down = new INPUT();
            down.type = INPUT_KEYBOARD;
            down.U.ki.wVk = vk;
            down.U.ki.wScan = scan;
            down.U.ki.dwFlags = flags;
            down.U.ki.dwExtraInfo = extraInfo;
            INPUT up = down;
            up.U.ki.dwFlags = flags | KEYEVENTF_KEYUP;
            inputs.Add(down);
            inputs.Add(up);
        }

        public static void Send(List<INPUT> inputs)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }

    // ---- The keyboard thread: hooks, key translation, typing the fix ----

    sealed class Corrector : IDisposable
    {
        // Marks our own injected keystrokes so the hook can tell them apart.
        static readonly IntPtr Magic = (IntPtr)0x4B5748;
        // Longest the keyboard thread waits for a spelling answer. Windows drops a
        // hook that takes too long (LowLevelHooksTimeout, 300 ms by default).
        const int LookupBudgetMs = 100;
        // Backspace right after a fix always undoes it; the word is only added to
        // "my words" for good when the same fix is undone this many times, so a
        // stray Backspace cannot switch a correction off.
        const int UndosBeforeLearning = 2;

        readonly SpellService spelling;
        readonly ListFile myWords, excludedApps;
        readonly Dictionary<string, int> undoCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Counters counters;
        readonly Settings settings;
        // Keys injected with this marker count as typed by the user (self-test only).
        readonly IntPtr testMarker;
        readonly Engine engine;
        readonly Thread thread;
        readonly ManualResetEvent started = new ManualResetEvent(false);
        // Held in fields so the delegates are not garbage collected while hooked.
        readonly Native.HookProc keyProc, mouseProc;
        readonly byte[] keyState = new byte[256];
        readonly StringBuilder keyChars = new StringBuilder(8);
        IntPtr keyHook, mouseHook, lastForeground;
        uint threadId;
        Exception startupError;
        volatile bool enabled = true;
        volatile string lastApp = "";
        bool wasEnabled = true;
        // The key currently held down, to recognise auto-repeat.
        int heldKey;

        // Both raised on the keyboard thread.
        public event Action<string, string> Corrected;
        public event Action EnabledChanged;
        // A word was added to "my words" because its fix was undone repeatedly.
        public event Action<string> Learned;

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; }
        }

        // The program the user last typed in ("notepad.exe").
        public string LastApp { get { return lastApp; } }

        public Corrector(SpellService spelling, ListFile myWords, ListFile excludedApps, Counters counters, Settings settings, IntPtr testMarker)
        {
            this.settings = settings;
            this.spelling = spelling;
            this.myWords = myWords;
            this.excludedApps = excludedApps;
            this.counters = counters;
            this.testMarker = testMarker;
            engine = new Engine(Lookup, myWords.Contains, Type);
            engine.Corrected += OnCorrected;
            engine.Undone += OnUndone;
            keyProc = KeyProc;
            mouseProc = MouseProc;
            Rehearse();

            thread = new Thread(Run);
            thread.Name = "Keyboard";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            started.WaitOne();
            if (startupError != null)
                throw new InvalidOperationException("Could not install the keyboard hook: " + startupError.Message);
        }

        public void Dispose()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }

        // Runs the whole correction path once before the hooks exist, so the first
        // real correction does not pay for JIT compilation inside the hook callback.
        void Rehearse()
        {
            Engine rehearsal = new Engine(Lookup, myWords.Contains, delegate { });
            foreach (char c in "Teh wrold ") rehearsal.Char(c);
            rehearsal.Backspace();
            Native.ProcessNameOf(Native.GetForegroundWindow());
            Type(0, "");
            counters.Words = counters.TooSlow = 0;
        }

        void Run()
        {
            try
            {
                IntPtr module = Native.GetModuleHandle(null);
                keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, module, 0);
                if (keyHook == IntPtr.Zero)
                    throw new InvalidOperationException("Windows error " + Marshal.GetLastWin32Error());
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
                threadId = Native.GetCurrentThreadId();
            }
            catch (Exception ex) { startupError = ex; }
            finally { started.Set(); }
            if (startupError != null) return;

            // Hook callbacks are delivered from inside GetMessage.
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }

            Native.UnhookWindowsHookEx(keyHook);
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        }

        string Lookup(string word, bool cautious)
        {
            counters.Words++;
            bool tooSlow;
            string fix = spelling.Suggest(word, cautious, LookupBudgetMs, out tooSlow);
            if (tooSlow) counters.TooSlow++;
            return fix;
        }

        void OnCorrected(string from, string to)
        {
            counters.Corrections++;
            if (Corrected != null) Corrected(from, to);
        }

        void OnUndone(string word)
        {
            counters.Undone++;
            int times;
            undoCounts.TryGetValue(word, out times);
            undoCounts[word] = ++times;
            if (times >= UndosBeforeLearning && !myWords.Contains(word))
            {
                myWords.Add(word);
                if (Learned != null) Learned(word);
            }
        }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                // The caret may now be in the middle of existing text ("walk|" + "ing"),
                // so the next word is left alone, like after an arrow key.
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                    engine.Break(false);
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                {
                    Native.KBDLLHOOKSTRUCT up = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (IsUserKey(up) && (int)up.vkCode == heldKey) heldKey = 0;
                }
                else if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                {
                    Native.KBDLLHOOKSTRUCT key = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    long begin = Stopwatch.GetTimestamp();
                    bool swallow = false;
                    // A failure here must never block the user's typing.
                    try { swallow = HandleKey(key); }
                    catch (Exception ex)
                    {
                        counters.Error(ex);
                        engine.Break(false);
                    }
                    int ms = (int)((Stopwatch.GetTimestamp() - begin) * 1000 / Stopwatch.Frequency);
                    if (ms > counters.SlowestKeyMs) counters.SlowestKeyMs = ms;
                    if (swallow) return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
        }

        static bool IsDown(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        // False for keystrokes injected by a program (ours included).
        bool IsUserKey(Native.KBDLLHOOKSTRUCT key)
        {
            return (key.flags & Native.LLKHF_INJECTED) == 0 ||
                   (testMarker != IntPtr.Zero && key.dwExtraInfo == testMarker);
        }

        bool HandleKey(Native.KBDLLHOOKSTRUCT key)
        {
            if (!IsUserKey(key))
            {
                // Text typed by another program (or us) is not the user's spelling.
                if (key.dwExtraInfo != Magic) engine.Break(false);
                return false;
            }
            counters.Keys++;

            int vk = (int)key.vkCode;
            if (vk == Native.VK_SHIFT || vk == Native.VK_CONTROL || vk == Native.VK_MENU || vk == Native.VK_CAPITAL ||
                vk == Native.VK_LWIN || vk == Native.VK_RWIN || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x90 || vk == 0x91)
                return false;

            // A held key repeating (a game's movement keys, "sooooo", holding
            // Backspace) is not a word being spelled.
            bool repeat = vk == heldKey;
            heldKey = vk;

            bool ctrl = IsDown(Native.VK_CONTROL), alt = IsDown(Native.VK_MENU);
            // Left Alt only: Right Alt (AltGr) is how some layouts type letters.
            if (ctrl && IsDown(Native.VK_LMENU) && !IsDown(Native.VK_RMENU) && vk == 'K')
            {
                if (!repeat)
                {
                    enabled = !enabled;
                    if (EnabledChanged != null) EnabledChanged();
                }
                return true;
            }

            bool on = enabled;
            if (on != wasEnabled)
            {
                wasEnabled = on;
                engine.Break(false);
            }
            if (!on) return false;
            engine.FixOnPunctuation = settings.FixOnPunctuation;
            engine.CapitalizeSentences = settings.CapitalizeSentences;

            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != lastForeground)
            {
                lastForeground = foreground;
                lastApp = Native.ProcessNameOf(foreground);
                engine.Break(true);
            }

            if (repeat || excludedApps.Contains(lastApp))
            {
                engine.Break(false);
                return false;
            }

            if (ctrl || alt || IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN))
            {
                engine.Break(false);
                return false;
            }

            if (vk == Native.VK_BACK) return engine.Backspace();
            // Enter and Tab never trigger a correction: they submit forms, and the
            // word before them could be a password or a command.
            if (vk == Native.VK_RETURN || vk == Native.VK_TAB) { engine.Break(true); return false; }
            if (vk == Native.VK_ESCAPE || (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || (vk >= 0x70 && vk <= 0x87))
            {
                engine.Break(false);
                return false;
            }

            Array.Clear(keyState, 0, keyState.Length);
            if (IsDown(Native.VK_SHIFT)) keyState[Native.VK_SHIFT] = 0x80;
            if ((Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0) keyState[Native.VK_CAPITAL] = 1;
            IntPtr layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(foreground, IntPtr.Zero));
            // Only English keyboards: other languages must not go through an English dictionary.
            if (((int)(layout.ToInt64() & 0x3FF)) != Native.LANG_ENGLISH)
            {
                engine.Break(false);
                return false;
            }
            keyChars.Length = 0;
            // Flag 4: translate without disturbing the keyboard's dead-key state.
            int n = Native.ToUnicodeEx(key.vkCode, key.scanCode, keyState, keyChars, keyChars.Capacity, 4, layout);
            if (n != 1)
            {
                engine.Break(false);
                return false;
            }
            return engine.Char(keyChars[0]);
        }

        // Engine output: erase the misspelled word and type the replacement.
        void Type(int backspaces, string text)
        {
            // Real scan codes, for programs that read those instead of the key code.
            ushort backScan = (ushort)Native.MapVirtualKey(Native.VK_BACK, 0);
            ushort spaceScan = (ushort)Native.MapVirtualKey(Native.VK_SPACE, 0);
            List<Native.INPUT> inputs = new List<Native.INPUT>((backspaces + text.Length) * 2);
            for (int i = 0; i < backspaces; i++) Native.AddKey(inputs, Native.VK_BACK, backScan, 0, Magic);
            foreach (char c in text)
            {
                if (c == ' ') Native.AddKey(inputs, Native.VK_SPACE, spaceScan, 0, Magic);
                else Native.AddKey(inputs, 0, c, Native.KEYEVENTF_UNICODE, Magic);
            }
            Native.Send(inputs);
        }
    }

    // ---- Tray application ----

    sealed class TrayApp : ApplicationContext
    {
        const string AppName = "Keyboard Writer Helper";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "KeyboardWriterHelper";
        const int MaxTrayText = 63; // NotifyIcon.Text throws beyond this

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem enabledItem, appItem, recentItem, startupItem, errorItem, statsItem;
        readonly Icon iconOn, iconOff, iconSkipped;
        readonly Counters counters = new Counters();
        readonly Settings settings;
        readonly ListFile myWords, excludedApps;
        readonly Corrector corrector;
        readonly List<string[]> recent = new List<string[]>(); // { typed, fixed }
        // Gives the keyboard thread a way to run code on the UI thread.
        readonly Control ui = new Control();
        readonly System.Windows.Forms.Timer housekeeping = new System.Windows.Forms.Timer();
        readonly string statsPath, lifetimePath, ownExe;
        readonly DateTime startedAt = DateTime.Now;
        // Totals from earlier runs; counts only, never text.
        long baseWords, baseCorrections, baseUndone;
        string lastStats, lastLifetime;
        // The program in front, ignoring this app's own windows.
        string currentApp = "";
        SettingsForm settingsForm;

        public TrayApp()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyboardWriterHelper");
            Directory.CreateDirectory(folder);
            statsPath = Path.Combine(folder, "stats.txt");
            lifetimePath = Path.Combine(folder, "lifetime.txt");
            ownExe = Path.GetFileName(Application.ExecutablePath);
            settings = new Settings(Path.Combine(folder, "settings.txt"));
            myWords = ListFile.MyWords(folder);
            excludedApps = ListFile.ExcludedApps(folder);
            LoadLifetime();
            SpellService spelling = new SpellService(counters);
            RepairStartupPath();

            iconOn = MakeIcon(Color.FromArgb(22, 163, 74));
            iconOff = MakeIcon(Color.FromArgb(120, 120, 120));
            iconSkipped = MakeIcon(Color.FromArgb(217, 119, 6));

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem(AppName + " (" + spelling.Languages + ")");
            title.Enabled = false;
            errorItem = new ToolStripMenuItem();
            errorItem.Enabled = false;
            errorItem.Visible = false;
            statsItem = new ToolStripMenuItem();
            statsItem.Enabled = false;
            enabledItem = new ToolStripMenuItem("Auto-correct on   (Ctrl+Alt+K)", null, delegate { Toggle(); });
            appItem = new ToolStripMenuItem("", null, delegate { ToggleApp(); });
            recentItem = new ToolStripMenuItem("Recent corrections");
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings and statistics...", null, delegate { ShowSettings(); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!IsStartupEnabled()); });
            startupItem.Checked = IsStartupEnabled();
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, delegate { ExitThread(); });
            menu.Items.AddRange(new ToolStripItem[] {
                title, errorItem, statsItem, new ToolStripSeparator(), enabledItem, appItem, recentItem,
                new ToolStripSeparator(), settingsItem, startupItem,
                new ToolStripSeparator(), exitItem });
            menu.Opening += delegate { RefreshMenu(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Toggle(); };

            IntPtr forceHandle = ui.Handle;
            corrector = new Corrector(spelling, myWords, excludedApps, counters, settings, IntPtr.Zero);
            corrector.Corrected += OnCorrected;
            corrector.Learned += delegate(string word)
            {
                ui.BeginInvoke(new MethodInvoker(delegate { Say("\"" + word + "\" will not be corrected any more (you undid it twice)."); }));
            };
            // The hotkey gives no other sign that it worked, so say so.
            corrector.EnabledChanged += delegate { ui.BeginInvoke(new MethodInvoker(delegate { ShowState(); AnnounceState(); })); };

            ShowState();
            tray.Visible = true;

            housekeeping.Interval = 1000;
            housekeeping.Tick += delegate { Housekeeping(); };
            housekeeping.Start();

            tray.ShowBalloonTip(6000, AppName,
                "Running. Misspelled words are fixed when you press Space. Press Backspace right after a fix to undo it.",
                ToolTipIcon.Info);
        }

        protected override void ExitThreadCore()
        {
            housekeeping.Stop();
            corrector.Dispose();
            myWords.Sync();
            excludedApps.Sync();
            settings.Save();
            WriteStats();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }

        // ---- What the settings window needs ----

        public Settings Settings { get { return settings; } }
        public ListFile MyWords { get { return myWords; } }
        public ListFile ExcludedApps { get { return excludedApps; } }
        public string CurrentApp { get { return currentApp; } }

        public bool Enabled
        {
            get { return corrector.Enabled; }
            set { corrector.Enabled = value; ShowState(); }
        }

        public string[][] RecentCorrections()
        {
            lock (recent) return recent.ToArray();
        }

        // Adds a word the user does not want corrected.
        public void NeverCorrect(string word)
        {
            myWords.Add(word);
            myWords.Sync();
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

        void Toggle()
        {
            Enabled = !Enabled;
        }

        bool IsSkipped(string app)
        {
            return app.Length > 0 && excludedApps.Contains(app);
        }

        // Icon colour: green = correcting, amber = on but skipping the program in
        // front, grey = paused.
        void ShowState()
        {
            bool on = corrector.Enabled;
            bool skipped = on && IsSkipped(currentApp);
            enabledItem.Checked = on;
            tray.Icon = !on ? iconOff : skipped ? iconSkipped : iconOn;
            tray.Text = Clip(AppName + (!on ? " - paused" : skipped ? " - skipped in " + currentApp : " - on"));
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
            Say(corrector.Enabled ? "Auto-correct is ON." : "Auto-correct is PAUSED. Press Ctrl+Alt+K to turn it back on.");
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
            lock (recent)
            {
                recent.Insert(0, new string[] { from, to });
                if (recent.Count > 10) recent.RemoveAt(recent.Count - 1);
            }
        }

        void Housekeeping()
        {
            myWords.Sync();
            excludedApps.Sync();
            WriteStats();

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
                    AppName + " - counters only, never what you typed\r\n" +
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
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // ---- Settings window: options, my words, skipped programs, statistics ----

    sealed class SettingsForm : Form
    {
        readonly TrayApp app;
        readonly Label statsLabel = new Label();
        readonly ListBox recentList = new ListBox();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        readonly ListBox wordsList = new ListBox(), appsList = new ListBox();
        string[] shownWords = new string[0], shownApps = new string[0];

        public SettingsForm(TrayApp app)
        {
            this.app = app;
            Text = "Keyboard Writer Helper - settings";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9f);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(GeneralTab());
            tabs.TabPages.Add(ListTab("My words", wordsList,
                "Words listed here are never auto-corrected.", "Add word", AddWord, RemoveWord, null));
            tabs.TabPages.Add(ListTab("Programs", appsList,
                "Nothing typed in these programs is corrected. The tray icon turns amber while one of them is in front.",
                "Add program", AddProgram, RemoveProgram, "Add the program in front (now)"));
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
            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.Padding = new Padding(14);
            flow.Controls.Add(Check("Auto-correct is on   (Ctrl+Alt+K turns it on or off)",
                app.Enabled, delegate(bool v) { app.Enabled = v; }));
            flow.Controls.Add(Check("Fix a word as soon as , ; : ! or ? is typed after it (otherwise: when Space is pressed)",
                app.Settings.FixOnPunctuation, delegate(bool v) { app.Settings.FixOnPunctuation = v; app.Settings.Save(); }));
            flow.Controls.Add(Check("Capitalise the first word of a sentence (after . ! or ?  and a space)",
                app.Settings.CapitalizeSentences, delegate(bool v) { app.Settings.CapitalizeSentences = v; app.Settings.Save(); }));
            flow.Controls.Add(Check("Start with Windows", TrayApp.IsStartupEnabled(), delegate(bool v) { app.SetStartup(v); }));
            Label note = new Label();
            note.AutoSize = false;
            note.Size = new Size(440, 90);
            note.Margin = new Padding(3, 16, 3, 3);
            note.Text = "Backspace right after a fix undoes it. Undo the same fix twice and the word is added to My words.\r\n\r\n" +
                        "A full stop never triggers a fix by itself (example.com is not a sentence); the next Space does.";
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
            box.Size = new Size(450, 38);
            box.CheckedChanged += delegate { changed(box.Checked); };
            return box;
        }

        TabPage ListTab(string title, ListBox list, string help, string addLabel, Action<string> add, Action<string> remove, string addNowLabel)
        {
            TabPage page = new TabPage(title);
            Label label = new Label();
            label.SetBounds(12, 10, 470, 40);
            label.Text = help;
            list.SetBounds(12, 55, 470, 230);
            list.IntegralHeight = false;
            list.SelectionMode = SelectionMode.MultiExtended;
            TextBox input = new TextBox();
            input.SetBounds(12, 300, 290, 24);
            Button addButton = new Button();
            addButton.Text = addLabel;
            addButton.SetBounds(308, 298, 90, 27);
            Action doAdd = delegate
            {
                string text = input.Text.Trim();
                if (text.Length == 0) return;
                add(text);
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
            removeButton.Text = "Remove";
            removeButton.SetBounds(404, 298, 78, 27);
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
                now.SetBounds(12, 335, 240, 27);
                now.Click += delegate
                {
                    if (app.CurrentApp.Length > 0) add(app.CurrentApp);
                    Reload();
                };
                page.Controls.Add(now);
            }
            return page;
        }

        TabPage StatsTab()
        {
            TabPage page = new TabPage("Statistics");
            statsLabel.SetBounds(12, 10, 470, 215);
            Label recentLabel = new Label();
            recentLabel.SetBounds(12, 228, 470, 18);
            recentLabel.Text = "Recent corrections (kept in memory only):";
            recentList.SetBounds(12, 248, 380, 130);
            recentList.IntegralHeight = false;
            Button never = new Button();
            never.Text = "Never correct";
            never.SetBounds(398, 248, 84, 27);
            never.Click += delegate
            {
                string[][] items = app.RecentCorrections();
                int i = recentList.SelectedIndex;
                if (i < 0 || i >= items.Length) return;
                app.NeverCorrect(items[i][0]);
                Reload();
            };
            page.Controls.AddRange(new Control[] { statsLabel, recentLabel, recentList, never });
            return page;
        }

        void AddWord(string text)
        {
            foreach (string w in text.Split(new char[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                app.MyWords.Add(w);
            app.MyWords.Sync();
        }

        void RemoveWord(string word) { app.MyWords.Remove(word); app.MyWords.Sync(); }

        void AddProgram(string text)
        {
            if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text += ".exe";
            app.ExcludedApps.Add(text);
            app.ExcludedApps.Sync();
        }

        void RemoveProgram(string name) { app.ExcludedApps.Remove(name); app.ExcludedApps.Sync(); }

        // Brings every list and number up to date without disturbing what is selected.
        void Reload()
        {
            Fill(wordsList, ref shownWords, app.MyWords.All());
            Fill(appsList, ref shownApps, app.ExcludedApps.All());
            statsLabel.Text = app.StatsText();
            string[][] items = app.RecentCorrections();
            bool same = recentList.Items.Count == items.Length;
            for (int i = 0; same && i < items.Length; i++)
                same = (string)recentList.Items[i] == items[i][0] + "  →  " + items[i][1];
            if (same) return;
            recentList.Items.Clear();
            foreach (string[] pair in items) recentList.Items.Add(pair[0] + "  →  " + pair[1]);
        }

        static void Fill(ListBox list, ref string[] shown, string[] now)
        {
            if (string.Join("\n", shown) == string.Join("\n", now)) return;
            shown = now;
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(now);
            list.EndUpdate();
        }
    }

    // ---- End-to-end self-test ----
    // Opens its own small window, types into it through the real keyboard hook and
    // SendInput path, and reports what ended up in the text box.

    sealed class SelfTestForm : Form
    {
        static readonly IntPtr TestMarker = (IntPtr)0x4B5754;
        const int MaxFocusWaitTicks = 80, SettleTicks = 12;

        readonly TextBox box = new TextBox();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly string script;
        readonly Corrector corrector;
        int next, focusWait, settle;

        public readonly Counters Counters = new Counters();
        public string Screen, Problem;

        // excludeSelf: put this test program on the skip list, to prove that
        // nothing is corrected in a skipped program.
        public SelfTestForm(string script, bool excludeSelf)
        {
            this.script = script.Replace("{BS}", "\b");
            Text = "Keyboard Writer Helper - self-test (closes by itself)";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 110);
            TopMost = true;
            box.Multiline = true;
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Segoe UI", 11f);
            Controls.Add(box);

            string folder = Path.Combine(Path.GetTempPath(), "KeyboardWriterHelper-selftest-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(folder);
            ListFile excluded = ListFile.ExcludedApps(folder);
            if (excludeSelf) excluded.Add(Path.GetFileName(Application.ExecutablePath));
            corrector = new Corrector(new SpellService(Counters), ListFile.MyWords(folder), excluded, Counters, new Settings(null), TestMarker);

            timer.Interval = 30;
            timer.Tick += delegate { Step(); };
            Shown += delegate { Activate(); box.Focus(); timer.Start(); };
            FormClosed += delegate
            {
                timer.Stop();
                corrector.Dispose();
                try { Directory.Delete(folder, true); } catch (IOException) { }
            };
        }

        void Step()
        {
            // Never type unless our own window has the keyboard.
            if (Native.GetForegroundWindow() != Handle || !box.Focused)
            {
                if (next > 0) { Finish("the test window lost keyboard focus part-way through"); return; }
                if (++focusWait > MaxFocusWaitTicks) { Finish("the test window could not get keyboard focus"); return; }
                Native.SetForegroundWindow(Handle);
                Activate();
                box.Focus();
                return;
            }

            if (next < script.Length)
            {
                SendChar(script[next++]);
                return;
            }
            if (++settle > SettleTicks) Finish(null);
        }

        void SendChar(char c)
        {
            List<Native.INPUT> inputs = new List<Native.INPUT>();
            if (c == '\b')
            {
                Native.AddKey(inputs, Native.VK_BACK, 0, 0, TestMarker);
            }
            else
            {
                short scan = Native.VkKeyScanW(c);
                bool shift = (scan & 0x100) != 0;
                Native.AddKey(inputs, (ushort)(scan & 0xFF), 0, 0, TestMarker);
                if (shift)
                {
                    // Wrap the key in Shift down ... Shift up.
                    List<Native.INPUT> shiftKey = new List<Native.INPUT>();
                    Native.AddKey(shiftKey, Native.VK_SHIFT, 0, 0, TestMarker);
                    inputs.Insert(0, shiftKey[0]);
                    inputs.Add(shiftKey[1]);
                }
            }
            Native.Send(inputs);
        }

        void Finish(string problem)
        {
            timer.Stop();
            Problem = problem;
            Screen = box.Text;
            Close();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--check") return Check(args);
            if (args.Length >= 2 && args[0] == "--simulate") return Simulate(args[1]);
            if (args.Length >= 2 && args[0] == "--eval") return Eval(args);
            if (args.Length >= 2 && args[0] == "--eval-ok") return EvalOk(args);
            if (args.Length >= 2 && args[0] == "--selftest") return SelfTest(args);

            bool first;
            using (Mutex single = new Mutex(true, "KeyboardWriterHelper.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("Keyboard Writer Helper is already running (look in the system tray).",
                        "Keyboard Writer Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.EnableVisualStyles();
                try
                {
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Keyboard Writer Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        // --check word [word...]: show the chosen fix and how the candidates ranked.
        static int Check(string[] args)
        {
            Speller speller = new Speller();
            TextWriter o = Out();
            o.WriteLine("dictionaries: " + speller.Dictionary.Languages);
            for (int i = 1; i < args.Length; i++)
            {
                string word = args[i];
                Stopwatch sw = Stopwatch.StartNew();
                string typed = TypeAlone(speller, word);
                long ms = sw.ElapsedMilliseconds;
                bool certain;
                List<string> windows = speller.Dictionary.Candidates(word, out certain);
                if (windows == null)
                {
                    o.WriteLine(word + " -> " + (typed == word ? "(ok)" : typed + "   [built-in fix]"));
                    continue;
                }
                List<Scored> trace = new List<Scored>();
                speller.Ranker.Best(word, windows, certain, false, speller.Dictionary.IsCorrect, trace);
                StringBuilder ranked = new StringBuilder();
                for (int k = 0; k < trace.Count && k < 5; k++)
                    ranked.AppendFormat(" {0}{1}({2:0.0}|{3:0.0})", trace[k].Word, trace[k].FromWindows ? "" : "*", trace[k].Cost, trace[k].Score);
                o.WriteLine(word + " -> " + (typed == word ? "(left alone)" : typed) + "   [" + ms + " ms" + (certain ? ", certain" : "") +
                    "]  windows: " + string.Join(", ", windows.ToArray()) + "   ranked:" + ranked);
            }
            return 0;
        }

        // --simulate "text": run text through the typing engine as if it were typed
        // and print what would end up on screen. {BS} stands for Backspace.
        static int Simulate(string text)
        {
            Speller speller = new Speller();
            StringBuilder screen = new StringBuilder();
            Engine engine = NewEngine(speller, screen);
            foreach (char c in text.Replace("{BS}", "\b"))
            {
                if (c == '\b')
                {
                    if (!engine.Backspace() && screen.Length > 0) screen.Length--;
                }
                else if (!engine.Char(c)) screen.Append(c);
            }
            Out().WriteLine(screen.ToString());
            return 0;
        }

        // An Engine whose output goes to a string instead of the keyboard.
        static Engine NewEngine(Speller speller, StringBuilder screen)
        {
            return new Engine(speller.Fix, delegate { return false; },
                delegate(int backspaces, string typed)
                {
                    screen.Length -= Math.Min(backspaces, screen.Length);
                    screen.Append(typed);
                });
        }

        // What a word turns into when typed on its own and followed by a space.
        static string TypeAlone(Speller speller, string word)
        {
            StringBuilder screen = new StringBuilder();
            Engine engine = NewEngine(speller, screen);
            foreach (char c in word + " ")
                if (!engine.Char(c)) screen.Append(c);
            return screen.ToString(0, screen.Length - 1);
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
        static List<string[]> ReadCases(string[] args, out bool misses, out bool caps)
        {
            misses = caps = false;
            List<string[]> cases = new List<string[]>(); // { label, word }
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--misses") { misses = true; continue; }
                if (args[i] == "--caps") { caps = true; continue; }
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

        // --eval [--misses] file...: how many misspellings come out right. Lines are
        // "intended: typed typed ..."; alternatives for intended are separated by |.
        static int Eval(string[] args)
        {
            bool misses, caps;
            List<string[]> cases = ReadCases(args, out misses, out caps);
            TextWriter o = Out();
            Speller speller = new Speller();
            int right = 0, wrong = 0, windowsFirst = 0;
            foreach (string[] c in cases)
            {
                string[] intended = c[0].Split('|');
                bool certain;
                List<string> windows = speller.Dictionary.Candidates(c[1], out certain);
                if (windows != null && windows.Count > 0 && Among(windows[0], intended)) windowsFirst++;

                string got = TypeAlone(speller, c[1]);
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
            return 0;
        }

        // --eval-ok [--misses] [--caps] file...: how many CORRECT words are left
        // alone. Lines are "category: word word ...". --caps types each word with a
        // capital, as at the start of a sentence.
        static int EvalOk(string[] args)
        {
            bool misses, caps;
            List<string[]> cases = ReadCases(args, out misses, out caps);
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
                string got = TypeAlone(speller, word);
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
            return 0;
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
