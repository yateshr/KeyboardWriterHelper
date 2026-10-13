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
// No word list lives in this file: the built-in ones are the text files in
// data\ (compiled into the exe), the user's own are in %APPDATA%.
//
// Build: build.cmd (uses the C# compiler that ships with Windows).

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
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
    // ---- The text files compiled into the exe (data\*.txt and the two count lists) ----
    // Blank lines and lines starting with # are skipped.

    static class Data
    {
        public static StreamReader Open(string name)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new InvalidOperationException("\"" + name + "\" is missing from this build.");
            return new StreamReader(stream, Encoding.UTF8);
        }

        public static List<string> Lines(string name)
        {
            List<string> lines = new List<string>();
            using (StreamReader reader = Open(name))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length > 0 && line[0] != '#') lines.Add(line);
                }
            }
            return lines;
        }

        // Any number of words per line.
        public static HashSet<string> Words(string name)
        {
            HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Lines(name))
                foreach (string word in line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    words.Add(word);
            return words;
        }

        // "key=value" lines.
        public static Dictionary<string, string> Pairs(string name)
        {
            Dictionary<string, string> pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Lines(name))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && eq < line.Length - 1) pairs[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return pairs;
        }
    }

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

        // The dictionaries in use, the one supplying suggestions first.
        public readonly string Languages;
        // Every English spelling of data\languages.txt that Windows has a dictionary for.
        public readonly string[] Installed;

        // main = the spelling that supplies the suggestions ("" = the first one
        // installed). others = the other installed spellings also count as correct.
        public WindowsDictionary(string main, bool others)
        {
            ISpellCheckerFactory factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            List<string> tags = new List<string>();
            foreach (string tag in Data.Lines("languages.txt"))
                if (factory.IsSupported(tag)) tags.Add(tag);
            if (tags.Count == 0)
                throw new InvalidOperationException("No English spell-check dictionary is installed in Windows.");
            Installed = tags.ToArray();

            int chosen = tags.FindIndex(delegate(string tag) { return string.Equals(tag, main, StringComparison.OrdinalIgnoreCase); });
            if (chosen > 0)
            {
                string tag = tags[chosen];
                tags.RemoveAt(chosen);
                tags.Insert(0, tag);
            }
            if (!others) tags.RemoveRange(1, tags.Count - 1);
            foreach (string tag in tags) checkers.Add(factory.CreateSpellChecker(tag));
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
        // Score ranks the candidates; Plain is the same without the help of the
        // word before, and is what the "is this convincing" limits look at.
        public double Cost, Score, Plain;
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
        public double ContextWeight = 0.5;    // score per factor of ten by which the word before makes a candidate likelier
        public bool UseContext = true;
        // "color"/"colour": leave either alone, whichever dictionary is in use.
        public bool AcceptVariants = true;

        const string FrequencyResource = "english-frequency.txt", PairResource = "english-pairs.txt";
        const int MaxValidations = 8;
        // Further candidates remembered for "next suggestion".
        const int MaxOthers = 3;
        // The word before never counts for more than this many factors of ten.
        const double MaxContext = 1.5;
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
        // Log10 count of "first second", keyed by the two words' numbers; and how
        // often each word stands first and second in all pairs together.
        readonly Dictionary<string, int> numberByWord = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<long, float> pairLogs = new Dictionary<long, float>();
        readonly double[] asFirst, asSecond;
        readonly double pairTotal;
        readonly double[,] grid = new double[MaxLength + 1, MaxLength + 1];

        // Word list: "word count" per line, produced by build_frequency.py from two
        // public lists (OpenSubtitles 2018 counts, github.com/hermitdave/FrequencyWords,
        // CC-BY-SA-4.0; Google Web 1T counts, norvig.com/ngrams).
        // Pair list: "first second count" per line, produced by build_pairs.py
        // from the Google Web 1T two-word counts (norvig.com/ngrams).
        public Ranker()
        {
            using (StreamReader reader = Data.Open(FrequencyResource))
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
                    numberByWord[word] = numberByWord.Count;
                    if (word.Length >= 3 && IsPlain(word))
                    {
                        words.Add(word);
                        logs.Add(log);
                    }
                }
            }

            asFirst = new double[numberByWord.Count];
            asSecond = new double[numberByWord.Count];
            using (StreamReader reader = Data.Open(PairResource))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // A quarter of a million lines: no Split, no TryParse.
                    int space = line.IndexOf(' '), space2 = line.IndexOf(' ', space + 1);
                    if (space <= 0 || space2 < 0) continue;
                    long count = 0;
                    for (int i = space2 + 1; i < line.Length; i++) count = count * 10 + (line[i] - '0');
                    int first, second;
                    if (count <= 0 || !numberByWord.TryGetValue(line.Substring(0, space), out first) ||
                        !numberByWord.TryGetValue(line.Substring(space + 1, space2 - space - 1), out second)) continue;
                    pairLogs[PairKey(first, second)] = (float)Math.Log10(count);
                    asFirst[first] += count;
                    asSecond[second] += count;
                    pairTotal += count;
                }
            }
        }

        // Multiplied, not shifted: a long's hash is its two halves xor-ed, which
        // for two small numbers side by side collides all the time.
        static long PairKey(int first, int second) { return first * 1000003L + second; }

        // By how many factors of ten the word before makes a candidate likelier
        // (or, below zero, less likely) than it is anyway: how often the two
        // stand together, against how often they would by chance. Being common
        // is already in the score, so "the" gains nothing from following everything.
        // A pair missing from the list says nothing either way: "is improve" is
        // rare, yet "improrvie" after "is" still means "improve".
        double Context(string previous, string candidate)
        {
            if (previous == null || !UseContext) return 0;
            int space = candidate.IndexOf(' ');
            int first, second;
            if (!numberByWord.TryGetValue(previous, out first) ||
                !numberByWord.TryGetValue(space > 0 ? candidate.Substring(0, space) : candidate, out second)) return 0;
            if (asFirst[first] == 0 || asSecond[second] == 0) return 0;
            float log;
            if (!pairLogs.TryGetValue(PairKey(first, second), out log)) return 0;
            double byChance = Math.Log10(asFirst[first] * asSecond[second] / pairTotal);
            return Math.Max(-MaxContext, Math.Min(MaxContext, log - byChance));
        }

        // Picks the best replacement for a misspelled word, or null if nothing is
        // convincing. windows = the dictionary's suggestions in its own order;
        // certain = the dictionary vouches for the first one. cautious = the word
        // is short or capitalised, where a wrong guess is likely (abbreviations,
        // names, words from another language), so only an obvious fix is accepted.
        // previous = the word typed just before, lower case, or null: it helps
        // choose between candidates but never makes a weak guess acceptable.
        // others, if given, receives the next-best candidates.
        public string Best(string typed, string previous, IList<string> windows, bool certain, bool cautious,
            Func<string, bool> isCorrect, List<Scored> trace, List<string> others)
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
                all.Add(new Scored { Word = candidate, Cost = cost, Plain = score,
                    Score = score + ContextWeight * Context(previous, candidateLower), FromWindows = true });
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
                    double score = FrequencyWeight * logs[k] - EditWeight * cost;
                    all.Add(new Scored { Word = word, Cost = cost, Plain = score, Score = score + ContextWeight * Context(previous, word) });
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
            if (!certain && winner.Plain < MinScore) return null;
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
            if (AcceptVariants && IsSpellingVariant(lower, fix)) return null;
            if (cautious)
            {
                // A capitalised word the dictionary does not know is probably a
                // name; never turn it into an uncommon word ("Tarun" -> "Taron").
                if (char.IsUpper(typed[0]) && commonness < NameSafeLog) return null;
                // Unsure about a short or capitalised word: only an obvious slip
                // (two letters swapped, a doubled letter) of a very common word.
                if (!certain && (winner.Cost > CautiousMaxCost || commonness < CautiousMinLog)) return null;
            }

            if (others != null)
            {
                foreach (Scored s in all)
                {
                    if (others.Count >= MaxOthers) break;
                    if (s == winner || s.Cost > MaxFixCost || s.Word.EndsWith(".")) continue;
                    if (!s.FromWindows)
                    {
                        if (validations >= MaxValidations) continue;
                        validations++;
                        if (!isCorrect(s.Word)) continue;
                    }
                    others.Add(s.Word);
                }
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

    // What the speller says about one word.
    sealed class Suggestion
    {
        // The dictionary accepts the word as typed.
        public bool Known;
        // The replacement, or null to leave the word alone.
        public string Fix;
        // Further candidates, best first, for "next suggestion".
        public string[] Others = new string[0];
    }

    // The spelling choices of the settings window.
    sealed class SpellOptions
    {
        // The English spelling that supplies the suggestions ("" = the first one installed).
        public string MainLanguage = "";
        // Other English spellings are correct too ("colour" and "color").
        public bool AcceptVariants = true;
        // Let the word before help choose between candidates.
        public bool UseContext = true;
    }

    // Dictionary + ranking. Create and use on one thread.
    sealed class Speller
    {
        public readonly WindowsDictionary Dictionary;
        public readonly Ranker Ranker;

        public Speller() : this(new SpellOptions(), null) { }

        // ranker: the one of an earlier Speller, to save loading the lists again.
        public Speller(SpellOptions options, Ranker ranker)
        {
            Dictionary = new WindowsDictionary(options.MainLanguage, options.AcceptVariants);
            Ranker = ranker ?? new Ranker();
            Ranker.AcceptVariants = options.AcceptVariants;
            Ranker.UseContext = options.UseContext;
        }

        // previous: the word typed just before, lower case, or null.
        // cautious: see Ranker.Best.
        public Suggestion Check(string word, string previous, bool cautious)
        {
            Suggestion result = new Suggestion();
            bool certain;
            List<string> windows = Dictionary.Candidates(word, out certain);
            if (windows == null)
            {
                result.Known = true;
                return result;
            }
            List<string> others = new List<string>();
            string best = Ranker.Best(word, previous, windows, certain, cautious, Dictionary.IsCorrect, null, others);
            if (best != null && best != word)
            {
                result.Fix = best;
                result.Others = others.ToArray();
            }
            return result;
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
            public string Word, Previous;
            public bool Cautious;
            public Suggestion Result;
            // Set instead of Word: switch to these spelling choices.
            public SpellOptions Options;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            // Cautious and normal lookups of one word can differ.
            public string WordKey { get { return (Cautious ? "?" : "") + Word; } }
            public string Key { get { return Previous == null ? WordKey : WordKey + " " + Previous; } }
        }

        readonly BlockingCollection<Request> queue = new BlockingCollection<Request>();
        readonly Dictionary<string, Suggestion> cache = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Counters counters;
        Exception startupError;
        volatile bool useContext;

        // The dictionaries in use, and every English one Windows has.
        public string Languages { get; private set; }
        public string[] Installed { get; private set; }

        public SpellService(Counters counters, SpellOptions options)
        {
            this.counters = counters;
            useContext = options.UseContext;
            Thread worker = new Thread(delegate() { Work(options); });
            worker.Name = "Spelling";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            ready.WaitOne();
            if (startupError != null) throw new InvalidOperationException(startupError.Message, startupError);
        }

        void Work(SpellOptions options)
        {
            Speller speller = null;
            try
            {
                speller = new Speller(options, null);
                Languages = speller.Dictionary.Languages;
                Installed = speller.Dictionary.Installed;
                // The first lookup loads the dictionary; pay for that now, not on a keystroke.
                speller.Check("speling", null, false);
            }
            catch (Exception ex) { startupError = ex; }
            finally { ready.Set(); }
            if (startupError != null) return;

            foreach (Request request in queue.GetConsumingEnumerable())
            {
                if (request.Options != null)
                {
                    try
                    {
                        speller = new Speller(request.Options, speller.Ranker);
                        Languages = speller.Dictionary.Languages;
                        speller.Check("speling", null, false);
                    }
                    catch (Exception ex) { counters.Error(ex); }
                    lock (cache) cache.Clear();
                    request.Done.Set();
                    continue;
                }

                Suggestion result = null;
                try { result = speller.Check(request.Word, request.Previous, request.Cautious); }
                catch (Exception ex) { counters.Error(ex); }
                if (result != null)
                {
                    lock (cache)
                    {
                        if (cache.Count > 5000) cache.Clear();
                        cache[request.Key] = result;
                        if (result.Known) cache[request.WordKey] = result;
                    }
                }
                request.Result = result;
                request.Done.Set();
            }
        }

        // Switches to other spelling choices; returns once they are in effect.
        public void Configure(SpellOptions options)
        {
            useContext = options.UseContext;
            Request request = new Request { Options = options };
            queue.Add(request);
            request.Done.WaitOne(5000);
        }

        // Call from a non-STA thread. If the answer is not ready within budgetMs the
        // word is left alone this time (tooSlow, null) and the answer is cached for the next.
        public Suggestion Suggest(string word, string previous, bool cautious, int budgetMs, out bool tooSlow)
        {
            tooSlow = false;
            Request request = new Request { Word = word, Previous = useContext ? previous : null, Cautious = cautious };
            Suggestion cached;
            lock (cache)
            {
                // A correctly spelled word is correct whatever came before it.
                if (cache.TryGetValue(request.WordKey, out cached) && (cached.Known || request.Previous == null)) return cached;
                if (cache.TryGetValue(request.Key, out cached)) return cached;
            }

            queue.Add(request);
            if (request.Done.WaitOne(budgetMs)) return request.Result;
            tooSlow = true;
            return null;
        }
    }

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
                             RememberTypos;
        public volatile Hotkey PauseHotkey = Hotkey.Parse("Ctrl+Alt+K"), NextHotkey = Hotkey.Parse("Ctrl+Alt+N");
        // The English spelling that supplies the suggestions ("" = the first one installed).
        public volatile string MainLanguage = "";

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
                    else if (key == "mainLanguage") MainLanguage = text;
                    else if (key == "pauseHotkey") PauseHotkey = Hotkey.Parse(text) ?? PauseHotkey;
                    else if (key == "nextHotkey") NextHotkey = Hotkey.Parse(text) ?? NextHotkey;
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
                    "fixOnPunctuation=" + Text(FixOnPunctuation) +
                    "capitalizeSentences=" + Text(CapitalizeSentences) +
                    "fixTwoCapitals=" + Text(FixTwoCapitals) +
                    "fixPhrases=" + Text(FixPhrases) +
                    "doubleSpacePeriod=" + Text(DoubleSpacePeriod) +
                    "useContext=" + Text(UseContext) +
                    "acceptVariants=" + Text(AcceptVariants) +
                    "rememberTypos=" + Text(RememberTypos) +
                    "mainLanguage=" + MainLanguage + "\r\n" +
                    "pauseHotkey=" + PauseHotkey + "\r\n" +
                    "nextHotkey=" + NextHotkey + "\r\n");
            }
            catch (IOException) { }
        }

        static string Text(bool value) { return (value ? "true" : "false") + "\r\n"; }

        public SpellOptions SpellOptions()
        {
            return new SpellOptions { MainLanguage = MainLanguage, AcceptVariants = AcceptVariants, UseContext = UseContext };
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
                    if (unsaved)
                    {
                        toSave = All();
                        unsaved = false;
                    }
                }
                if (toSave != null)
                {
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
                unsaved = false;
                lines = new string[counts.Count];
                int i = 0;
                foreach (KeyValuePair<string, int> entry in counts) lines[i++] = entry.Value + "\t" + entry.Key;
            }
            try { File.WriteAllLines(path, lines); }
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

    // ---- Typing state machine (no Windows dependencies, so it can be simulated) ----

    sealed class Engine
    {
        sealed class Undo
        {
            // Lead + Original stood on screen and Fix replaced it. Lead is "" or,
            // when the word before was replaced as well, that word and its space.
            public string Lead, Original, Fix, Terminator;
            // The word before, as it was when this one was typed.
            public string Previous;
            public bool AtSentenceStart;
            // Everything the text could be: the fix, the next-best candidates and
            // last what was typed. Choice is the one on screen.
            public string[] Choices;
            public int Choice;
        }

        const int MaxWordLength = 40;

        // The built-in lists; each file in data\ says at its top what it is for.
        static readonly Dictionary<string, string> CommonFixes = Data.Pairs("fixes.txt");
        static readonly Dictionary<string, string> PhraseFixes = Data.Pairs("phrase-fixes.txt");
        static readonly HashSet<string> NeverCorrect = Data.Words("never-correct.txt");
        static readonly HashSet<string> OtherLanguage = Data.Words("other-language.txt");
        static readonly HashSet<string> Abbreviations = Data.Words("abbreviations.txt");

        readonly Func<string, string, bool, Suggestion> suggest; // (word, word before in lower case, cautious)
        readonly Func<string, bool> isIgnored;
        // The user's own fix for a word or a two-word phrase, or null.
        readonly Func<string, string> ownFix;
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
        // The word before the one being typed, as it stands on screen, when
        // exactly one space separates the two. Otherwise null.
        string previous;
        // The last key was the Space that ended a plain word.
        bool spaceAfterWord;
        // The last key turned two spaces into a full stop.
        bool madeFullStop;
        // Set by Decide: the word before is replaced too; the next-best candidates.
        string lead;
        string[] others;

        // Fix a word as soon as , ; : ! or ? follows it (a full stop waits for the
        // space, because "example.com" is not a sentence end).
        public volatile bool FixOnPunctuation = true;
        public volatile bool CapitalizeSentences = true;
        // "THe" -> "The".
        public volatile bool FixTwoCapitals = true;
        // The built-in two-word fixes ("their is" -> "there is").
        public volatile bool FixPhrases = true;
        // Two spaces after a word become ". ".
        public volatile bool DoubleSpacePeriod;

        // (typed, fix). A phrase fix reports both words.
        public event Action<string, string> Corrected;
        public event Action<string, string> Undone;
        // (typed, fix before, fix now): another candidate was picked.
        public event Action<string, string, string> Changed;

        public Engine(Func<string, string, bool, Suggestion> suggest, Func<string, bool> isIgnored,
            Func<string, string> ownFix, Action<int, string> emit)
        {
            this.suggest = suggest;
            this.isIgnored = isIgnored;
            this.ownFix = ownFix;
            this.emit = emit;
        }

        // clean = the caret is most likely at the start of fresh text.
        public void Break(bool clean)
        {
            Clear();
            undo = null;
            capNext = false;
            previous = null;
            spaceAfterWord = madeFullStop = false;
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
            bool secondSpace = spaceAfterWord && c == ' ';
            spaceAfterWord = madeFullStop = false;

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
                // As on a phone keyboard. The first space is already on screen.
                if (secondSpace && DoubleSpacePeriod)
                {
                    emit(1, ". ");
                    madeFullStop = true;
                    afterSentenceEnd = true;
                    capNext = true;
                    previous = null;
                    return true;
                }

                bool hadWord = !tainted && word.Length > 0;
                bool plain = hadWord && trail.Length == 0;
                string last = word.ToString();
                string ending = trail.ToString().TrimEnd(')', ']', '}', '"');
                bool swallowed = TryCorrect(trail + " ");
                string shown = swallowed ? undo.Fix : last;
                Clear();
                tainted = false;
                previous = plain ? LastWord(shown) : null;
                spaceAfterWord = plain;
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
                previous = null;
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

            previous = null;
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
            bool fullStop = madeFullStop;
            spaceAfterWord = madeFullStop = false;
            if (fullStop)
            {
                // Back to the two spaces that were typed.
                emit(2, "  ");
                afterSentenceEnd = false;
                capNext = false;
                return true;
            }
            if (undo != null)
            {
                Undo u = undo;
                undo = null;
                string punctuation = u.Terminator.Substring(0, u.Terminator.Length - 1);
                emit(u.Fix.Length + u.Terminator.Length, u.Lead + u.Original + punctuation);
                Clear();
                word.Append(u.Original);
                trail.Append(punctuation);
                keepAsTyped = u.Original;
                tainted = false;
                wordAtSentenceStart = u.AtSentenceStart;
                previous = u.Previous;
                // Not an undo if "next suggestion" had already brought the typed text back.
                if (u.Fix != u.Lead + u.Original && Undone != null) Undone(u.Lead + u.Original, u.Fix);
                return true;
            }
            if (trail.Length > 0) trail.Length--;
            else if (word.Length > 0) word.Length--;
            else
            {
                tainted = true;
                previous = null;
            }
            return false;
        }

        // True straight after a correction, while Next() has something to swap.
        public bool CanNext { get { return undo != null; } }

        // Straight after a correction: swaps it for the next-best candidate. After
        // the last one comes the text as typed, then the first fix again.
        // False when there is nothing to swap.
        public bool Next()
        {
            if (undo == null) return false;
            Undo u = undo;
            string before = u.Fix;
            u.Choice = (u.Choice + 1) % u.Choices.Length;
            u.Fix = u.Choices[u.Choice];
            emit(before.Length + u.Terminator.Length, u.Fix + u.Terminator);
            if (previous != null) previous = LastWord(u.Fix);
            if (Changed != null) Changed(u.Lead + u.Original, before, u.Fix);
            return true;
        }

        // terminator = any held-back punctuation plus the space being typed.
        bool TryCorrect(string terminator)
        {
            if (tainted || word.Length == 0) return false;
            string original = word.ToString();
            if (original == keepAsTyped) return false;
            lead = "";
            others = null;
            string fix = Decide(original, terminator);
            if (capNext && CapitalizeSentences) fix = Capitalise(original, fix);
            if (fix == null) return false;
            string typed = lead + original;
            // The space itself has not reached the screen yet, hence the -1.
            emit(typed.Length + terminator.Length - 1, fix + terminator);
            undo = new Undo { Lead = lead, Original = original, Fix = fix, Terminator = terminator, Previous = previous,
                              AtSentenceStart = wordAtSentenceStart, Choices = Choices(typed, fix) };
            if (Corrected != null) Corrected(typed, fix);
            return true;
        }

        // The fix, the other candidates with the same capital, then the text as typed.
        string[] Choices(string typed, string fix)
        {
            List<string> choices = new List<string>();
            choices.Add(fix);
            if (others != null)
            {
                foreach (string other in others)
                {
                    string shaped = char.IsUpper(fix[0]) && char.IsLower(other[0]) ? Capital(other) : other;
                    if (shaped != typed && !choices.Contains(shaped)) choices.Add(shaped);
                }
            }
            choices.Add(typed);
            return choices.ToArray();
        }

        // The replacement for the word just finished, or null to leave it. Sets
        // `lead` when the replacement also covers the word before.
        string Decide(string w, string terminator)
        {
            // Two words first: "their is" -> "there is", or a phrase of the user's.
            string phrase = PhraseFix(w);
            if (phrase != null) return AsPhrase(w, phrase);

            string fix = DecideWord(w, terminator);
            // The corrected word may complete a phrase: "their si" -> "there is".
            if (fix != null && (phrase = PhraseFix(fix)) != null) return AsPhrase(w, phrase);
            return fix;
        }

        // What "word-before second" should be, or null.
        string PhraseFix(string second)
        {
            if (previous == null) return null;
            string two = previous + " " + second;
            if (isIgnored(two)) return null;
            string fix = ownFix(two);
            if (fix == null && FixPhrases && !otherLanguage && !isIgnored(second)) PhraseFixes.TryGetValue(two, out fix);
            return fix;
        }

        string AsPhrase(string w, string phrase)
        {
            if (char.IsUpper(previous[0]) && char.IsLower(phrase[0])) phrase = Capital(phrase);
            // A phrase listed as its own fix ("and fro") is only there to keep the
            // single-word fixes away from it.
            if (phrase == previous + " " + w) return null;
            lead = previous + " ";
            others = null;
            return phrase;
        }

        string DecideWord(string w, string terminator)
        {
            // The user's own fixes and shortcuts apply everywhere, as written.
            string fix = isIgnored(w) ? null : ownFix(w);
            if (fix != null)
            {
                if (char.IsUpper(w[0]) && char.IsLower(fix[0])) fix = Capital(fix);
                return fix == w ? null : fix;
            }

            if (OtherLanguage.Contains(w)) otherLanguage = true;
            if (otherLanguage || isIgnored(w)) return null;
            // "i think", "i, too" - but not the list markers "i." and "i)".
            if (w == "i") return " ,?!".IndexOf(terminator[0]) >= 0 ? "I" : null;
            if (w.Length < 2 || w[w.Length - 1] == '\'') return null;

            // A capital after the first letter means an acronym or camelCase, which
            // are deliberate - unless Shift was just held a moment too long ("THe").
            string typed = w;
            bool slip = false;
            for (int i = 1; i < w.Length && !slip; i++) slip = char.IsUpper(w[i]);
            if (slip)
            {
                if (!FixTwoCapitals || !IsShiftSlip(w)) return null;
                w = w.Substring(0, 1) + char.ToLowerInvariant(w[1]) + w.Substring(2);
            }
            // Abbreviations (npm, ctx, dr) and stretched words (sooo) are not misspellings.
            if (!HasVowel(w) || HasTripledLetter(w)) return null;
            if (NeverCorrect.Contains(w)) return slip ? w : null;

            bool capitalised = char.IsUpper(w[0]);
            if (!CommonFixes.TryGetValue(w, out fix))
            {
                Suggestion s = suggest(w, previous == null ? null : previous.ToLowerInvariant(),
                    capitalised || w.Length <= Ranker.ShortWord);
                if (s == null) return null;
                fix = s.Fix;
                // A capital mid-sentence is most likely a name: only accept a fix
                // that is itself a capitalised word ("Febuary" -> "February").
                if (fix != null && capitalised && !wordAtSentenceStart && !char.IsUpper(fix[0])) fix = null;
                // Spelled right; only the second capital was wrong.
                if (fix == null) return slip && s.Known ? w : null;
                others = s.Others;
            }
            if (capitalised && char.IsLower(fix[0])) fix = Capital(fix);
            return fix == typed ? null : fix;
        }

        // "THe", "HEllo": two capitals, then lower case. Not "IDs", "PCs" or
        // "OSes" (the plural of an acronym), nor "PC's".
        static bool IsShiftSlip(string w)
        {
            if (w.Length < 3 || !char.IsUpper(w[0]) || !char.IsUpper(w[1]) || !char.IsLower(w[2])) return false;
            for (int i = 3; i < w.Length; i++)
                if (char.IsUpper(w[i])) return false;
            string rest = w.Substring(2);
            return rest != "s" && rest != "es";
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
            return Capital(target);
        }

        static string Capital(string text) { return char.ToUpperInvariant(text[0]) + text.Substring(1); }

        static string LastWord(string text) { return text.Substring(text.LastIndexOf(' ') + 1); }

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
                         VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
                         VK_NOTHING = 0xE8; // a code no keyboard key has
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
        readonly ListFile myWords, myFixes, excludedApps;
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
        // The key currently held down, to recognise auto-repeat, and whether it
        // was taken as one of our shortcuts.
        int heldKey;
        bool heldIsShortcut;
        // Presses of the next-suggestion shortcut not acted on yet. The swap is
        // made when the last of Ctrl/Alt/Shift is let go: typed while they are
        // still down, the Backspaces would arrive as Ctrl+Alt+Backspace.
        int pendingNext;
        // That last key. Windows may still count it as down while its key-up is
        // being handled, so the swap starts by letting go of it once more.
        int liftKey;

        // All raised on the keyboard thread. Corrected and Undone: (typed, fix).
        // Changed: (typed, fix before, fix now) - another candidate was picked.
        public event Action<string, string> Corrected, Undone;
        public event Action<string, string, string> Changed;
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

        public Corrector(SpellService spelling, ListFile myWords, ListFile myFixes, ListFile excludedApps,
            Counters counters, Settings settings, IntPtr testMarker)
        {
            this.settings = settings;
            this.spelling = spelling;
            this.myWords = myWords;
            this.myFixes = myFixes;
            this.excludedApps = excludedApps;
            this.counters = counters;
            this.testMarker = testMarker;
            engine = new Engine(Lookup, myWords.Contains, myFixes.Get, Type);
            engine.Corrected += OnCorrected;
            engine.Undone += OnUndone;
            engine.Changed += delegate(string typed, string before, string now) { if (Changed != null) Changed(typed, before, now); };
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
            Engine rehearsal = new Engine(Lookup, myWords.Contains, myFixes.Get, delegate { });
            foreach (char c in "Teh wrold ") rehearsal.Char(c);
            rehearsal.Next();
            rehearsal.Backspace();
            lastForeground = Native.GetForegroundWindow();
            Native.ProcessNameOf(lastForeground);
            Type(0, "");
            lastForeground = IntPtr.Zero;
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

        Suggestion Lookup(string word, string previous, bool cautious)
        {
            counters.Words++;
            bool tooSlow;
            Suggestion result = spelling.Suggest(word, previous, cautious, LookupBudgetMs, out tooSlow);
            if (tooSlow) counters.TooSlow++;
            return result;
        }

        void OnCorrected(string from, string to)
        {
            counters.Corrections++;
            if (Corrected != null) Corrected(from, to);
        }

        void OnUndone(string word, string fix)
        {
            counters.Undone++;
            if (Undone != null) Undone(word, fix);
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
                    // Done from inside this call, like every other fix: what is
                    // typed here goes ahead of anything the user types next.
                    if (IsUserKey(up) && pendingNext > 0 && !ModifierHeld((int)up.vkCode))
                        SwapPending((int)up.vkCode);
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

        // The next-suggestion shortcut was pressed and the last of its keys
        // (released) is going up.
        void SwapPending(int released)
        {
            int times = pendingNext;
            pendingNext = 0;
            long begin = Stopwatch.GetTimestamp();
            try
            {
                while (times-- > 0)
                {
                    liftKey = released;
                    engine.Next();
                }
            }
            catch (Exception ex)
            {
                counters.Error(ex);
                engine.Break(false);
            }
            liftKey = 0;
            int ms = (int)((Stopwatch.GetTimestamp() - begin) * 1000 / Stopwatch.Frequency);
            if (ms > counters.SlowestKeyMs) counters.SlowestKeyMs = ms;
        }

        // Is a Shift, Ctrl or Alt key down, not counting the one being let go
        // right now? (Its own state may or may not be updated yet.)
        static bool ModifierHeld(int released)
        {
            for (int vk = Native.VK_LSHIFT; vk <= Native.VK_RMENU; vk++)
            {
                // The left and right key of a pair, and the pair's common code.
                int common = Native.VK_SHIFT + (vk - Native.VK_LSHIFT) / 2;
                if (vk != released && common != released && IsDown(vk)) return true;
            }
            return false;
        }

        // Alt pressed and let go with nothing in between opens the menu bar of
        // the program in front. A shortcut key we swallow counts as nothing, so
        // put a key without meaning in its place.
        void MaskAlt()
        {
            List<Native.INPUT> inputs = new List<Native.INPUT>(2);
            Native.AddKey(inputs, Native.VK_NOTHING, 0, 0, Magic);
            Native.Send(inputs);
        }

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
            // A shortcut held down does nothing more, and does not reach the program either.
            if (repeat && heldIsShortcut) return true;
            heldIsShortcut = false;
            // Any key but the next-suggestion shortcut itself calls the swap off.
            int nextPresses = pendingNext;
            pendingNext = 0;

            bool ctrl = IsDown(Native.VK_CONTROL), alt = IsDown(Native.VK_MENU), shift = IsDown(Native.VK_SHIFT);
            // Never with Right Alt (AltGr): that is how some layouts type letters.
            bool chord = (ctrl || alt) && !IsDown(Native.VK_RMENU) && !IsDown(Native.VK_LWIN) && !IsDown(Native.VK_RWIN);
            if (chord && settings.PauseHotkey.Matches(vk, ctrl, alt, shift))
            {
                heldIsShortcut = true;
                if (!ctrl) MaskAlt();
                enabled = !enabled;
                if (EnabledChanged != null) EnabledChanged();
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
            engine.FixTwoCapitals = settings.FixTwoCapitals;
            engine.FixPhrases = settings.FixPhrases;
            engine.DoubleSpacePeriod = settings.DoubleSpacePeriod;

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

            // Straight after a fix: pick its next-best candidate (once the keys are
            // let go, see pendingNext). At any other moment these keys belong to
            // the program in front and pass through.
            if (chord && settings.NextHotkey.Matches(vk, ctrl, alt, shift) && engine.CanNext)
            {
                heldIsShortcut = true;
                if (!ctrl) MaskAlt();
                pendingNext = nextPresses + 1;
                return true;
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
            // Another window came to the front while the word was looked up: the
            // fix belongs to the old one and must not be typed into the new one.
            if (Native.GetForegroundWindow() != lastForeground) return;

            // Real scan codes, for programs that read those instead of the key code.
            ushort backScan = (ushort)Native.MapVirtualKey(Native.VK_BACK, 0);
            ushort spaceScan = (ushort)Native.MapVirtualKey(Native.VK_SPACE, 0);
            List<Native.INPUT> inputs = new List<Native.INPUT>((backspaces + text.Length) * 2 + 2);
            if (liftKey != 0)
            {
                // Only the key-up: pressing Alt or Ctrl on its own means something
                // to some programs.
                List<Native.INPUT> pair = new List<Native.INPUT>(2);
                Native.AddKey(pair, (ushort)liftKey, 0, 0, Magic);
                inputs.Add(pair[1]);
                liftKey = 0;
            }
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

            IntPtr forceHandle = ui.Handle;
            corrector = new Corrector(spelling, myWords, myFixes, excludedApps, counters, settings, IntPtr.Zero);
            corrector.Corrected += OnCorrected;
            corrector.Undone += delegate(string typed, string fix) { typos.Count(typed, fix, -1); };
            corrector.Changed += OnChanged;
            corrector.Learned += delegate(string word)
            {
                ui.BeginInvoke(new MethodInvoker(delegate { Say("\"" + word + "\" will not be corrected any more (you undid it twice)."); }));
            };
            // The hotkey gives no other sign that it worked, so say so.
            corrector.EnabledChanged += delegate
            {
                ui.BeginInvoke(new MethodInvoker(delegate { pausedUntil = DateTime.MinValue; ShowState(); AnnounceState(); }));
            };

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
            myFixes.Sync();
            excludedApps.Sync();
            settings.Save();
            if (settings.RememberTypos) typos.Save();
            WriteStats();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
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
            return app.Length > 0 && excludedApps.Contains(app);
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
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

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
                "Words listed here are never auto-corrected.", "Add word", AddWord, RemoveWord, null));
            tabs.TabPages.Add(ListTab("My fixes", fixesList,
                "Your own fixes and shortcuts, written as  typo=correction . They work for a misspelling " +
                "(recieve=receive), a shortcut (brb=be right back) and a two-word slip (could of=could have).",
                "Add fix", AddFix, RemoveFix, null));
            tabs.TabPages.Add(ListTab("Programs", appsList,
                "Nothing typed in these programs is corrected. The tray icon turns amber while one of them is in front.",
                "Add program", AddProgram, RemoveProgram, "Add the program in front (now)"));
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

            aboutLabel.SetBounds(Left0, 340, Wide, 40);
            aboutLabel.ForeColor = SystemColors.GrayText;
            ShowAbout();

            page.Controls.AddRange(new Control[] {
                on, startup,
                Caption("Shortcut to pause and resume:", 87), pauseBox,
                Caption("Shortcut for the next suggestion:", 117), nextBox,
                Note("To change a shortcut, click its box and press the new keys (with Ctrl or Alt). " +
                     "The next-suggestion shortcut works straight after a fix: it swaps in the next-best word.", 144, 36),
                Caption("Suggestions use the spelling of:", 199), language, variants, aboutLabel });
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
            TextBox input = new TextBox();
            input.SetBounds(Left0, 324, Wide - 190, 24);
            Button addButton = new Button();
            addButton.Text = addLabel;
            addButton.SetBounds(Left0 + Wide - 184, 322, 100, 27);
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
            removeButton.Text = "Remove";
            removeButton.SetBounds(Left0 + Wide - 78, 322, 78, 27);
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
            always.Text = "Always fix it this way";
            always.SetBounds(Left0, 290, 170, 27);
            always.Click += delegate
            {
                string[] item = SelectedTypo();
                if (item == null) return;
                app.AlwaysFix(item[0], item[1]);
                Reload();
            };
            Button never = new Button();
            never.Text = "Never correct it";
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
            open.Click += delegate { Process.Start(app.DataFolder); };
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
        // Stands in the script for the "next suggestion" shortcut ({NEXT}).
        public const char NextKey = '\u0001';

        readonly TextBox box = new TextBox();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly string script;
        readonly Settings settings = new Settings(null);
        readonly Corrector corrector;
        int next, focusWait, settle;

        public readonly Counters Counters = new Counters();
        public string Screen, Problem;

        // excludeSelf: put this test program on the skip list, to prove that
        // nothing is corrected in a skipped program.
        public SelfTestForm(string script, bool excludeSelf)
        {
            this.script = script.Replace("{BS}", "\b").Replace("{NEXT}", NextKey.ToString());
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
            corrector = new Corrector(new SpellService(Counters, settings.SpellOptions()), ListFile.MyWords(folder),
                ListFile.MyFixes(folder), excluded, Counters, settings, TestMarker);

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
            else if (c == NextKey)
            {
                // Modifiers down, the key, modifiers up.
                Hotkey hotkey = settings.NextHotkey;
                Native.AddKey(inputs, (ushort)hotkey.Key, 0, 0, TestMarker);
                foreach (int modifier in new int[] { hotkey.Shift ? Native.VK_SHIFT : 0, hotkey.Alt ? Native.VK_MENU : 0, hotkey.Ctrl ? Native.VK_CONTROL : 0 })
                {
                    if (modifier == 0) continue;
                    List<Native.INPUT> pair = new List<Native.INPUT>();
                    Native.AddKey(pair, (ushort)modifier, 0, 0, TestMarker);
                    inputs.Insert(0, pair[0]);
                    inputs.Add(pair[1]);
                }
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
                Engine = new Engine(speller.Check, Words.Contains,
                    delegate(string typed)
                    {
                        string fix;
                        return Fixes.TryGetValue(typed, out fix) ? fix : null;
                    },
                    delegate(int backspaces, string typed)
                    {
                        Screen.Length -= Math.Min(backspaces, Screen.Length);
                        Screen.Append(typed);
                    });
            }

            // Types the text and returns what is on screen. {BS} stands for
            // Backspace, {NEXT} for the next-suggestion shortcut.
            public string Type(string text)
            {
                foreach (char c in text.Replace("{BS}", "\b").Replace("{NEXT}", SelfTestForm.NextKey.ToString()))
                {
                    if (c == '\b')
                    {
                        if (!Engine.Backspace() && Screen.Length > 0) Screen.Length--;
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
                    punctuation = capitals = twoCapitals = phrases = context = true;
                    doubleSpace = false;
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
                        string expected = line.Substring(arrow + Arrow.Length, line.Length - arrow - Arrow.Length - 1);
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

        // --eval [--misses] [--before word] file...: how many misspellings come out
        // right. Lines are "intended: typed typed ..."; alternatives for intended
        // are separated by |. --before types each one after that word, to see
        // what the word before does to the answers.
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
            return 0;
        }

        // --eval-ok [--misses] [--caps] [--before word] file...: how many CORRECT
        // words are left alone. Lines are "category: word word ...". --caps types
        // each word with a capital, as at the start of a sentence.
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
