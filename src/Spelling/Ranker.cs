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

    

        public enum KeyboardLayoutKind { Auto, Qwerty, Qwertz, Azerty, Dvorak, Colemak }

    

        static readonly bool[,] NearQwerty = BuildNearTable(new string[] { "qwertyuiop", "asdfghjkl", "zxcvbnm" });

        static readonly bool[,] NearQwertz = BuildNearTable(new string[] { "qwertzuiop", "asdfghjkl", "yxcvbnm" });

        static readonly bool[,] NearAzerty = BuildNearTable(new string[] { "azertyuiop", "qsdfghjklm", "wxcvbn" });

        static readonly bool[,] NearDvorak = BuildNearTable(new string[] { "pyfgcrl", "aoeuidhtns", "qjkxbmwvz" });

        static readonly bool[,] NearColemak = BuildNearTable(new string[] { "qwfpgjluy", "arstdhneio", "zxcvbkm" });

    

        bool[,] currentNear = NearQwerty;

        public KeyboardLayoutKind Layout = KeyboardLayoutKind.Auto;

    

        public void SetLayout(KeyboardLayoutKind kind)

        {

            Layout = kind;

            switch (kind)

            {

                case KeyboardLayoutKind.Qwertz: currentNear = NearQwertz; break;

                case KeyboardLayoutKind.Azerty: currentNear = NearAzerty; break;

                case KeyboardLayoutKind.Dvorak: currentNear = NearDvorak; break;

                case KeyboardLayoutKind.Colemak: currentNear = NearColemak; break;

                default: currentNear = NearQwerty; break;

            }

        }

    

        public static KeyboardLayoutKind ParseLayout(string name)

        {

            if (string.IsNullOrEmpty(name) || name.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return KeyboardLayoutKind.Auto;

            if (name.Equals("QWERTZ", StringComparison.OrdinalIgnoreCase)) return KeyboardLayoutKind.Qwertz;

            if (name.Equals("AZERTY", StringComparison.OrdinalIgnoreCase)) return KeyboardLayoutKind.Azerty;

            if (name.Equals("Dvorak", StringComparison.OrdinalIgnoreCase)) return KeyboardLayoutKind.Dvorak;

            if (name.Equals("Colemak", StringComparison.OrdinalIgnoreCase)) return KeyboardLayoutKind.Colemak;

            return KeyboardLayoutKind.Qwerty;

        }

    

        public static KeyboardLayoutKind DetectLayout(IntPtr layoutHandle)

        {

            long id = layoutHandle.ToInt64() & 0xFFFF;

            long dev = (layoutHandle.ToInt64() >> 16) & 0xFFFF;

            if (dev == 0x0001 || dev == 0x0003 || dev == 0x0004) return KeyboardLayoutKind.Dvorak;

            if (id == 0x0407 || id == 0x0807 || id == 0x041B || id == 0x0405) return KeyboardLayoutKind.Qwertz;

            if (id == 0x040C || id == 0x080C) return KeyboardLayoutKind.Azerty;

            return KeyboardLayoutKind.Qwerty;

        }

    

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

            if (string.IsNullOrEmpty(typed)) return null;

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

                if (lower == "i" || lower.StartsWith("i'")) return all[0].Word;

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

            if (n == 0) return m;

            if (m == 0) return n;

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

    

        double SwapCost(char x, char y)

        {

            if (x == y) return 0;

            if (IsVowel(x) && IsVowel(y)) return VowelSwap;

            if (x >= 'a' && x <= 'z' && y >= 'a' && y <= 'z' && currentNear[x - 'a', y - 'a']) return NearbySwap;

            return 1.0;

        }

    

        static bool IsVowel(char c) { return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u'; }

    

        // Letters that sit next to each other on a keyboard layout, plus pairs that sound alike.

        static bool[,] BuildNearTable(string[] rows)

        {

            bool[,] near = new bool[26, 26];

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

}

