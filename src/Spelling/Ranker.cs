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
        // Score ranks the candidates; Plain is the same without sentence
        // context, and is what the "is this convincing" limits look at.
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
        public double ContextWeight = 0.5;    // score per factor of ten by which sentence context makes a candidate likelier
        public bool UseContext = true;
        // "color"/"colour": leave either alone, whichever dictionary is in use.
        public bool AcceptVariants = true;

        const string FrequencyResource = "english-frequency.txt", PairResource = "english-pairs.txt",
                     TrigramResource = "english-trigrams.txt", ModelResource = "english-model.bin";

        const int MaxValidations = 8;
        // Further candidates remembered for "next suggestion".
        const int MaxOthers = 3;
        // Sentence context never counts for more than this many factors of ten.
        const double MaxContext = 1.5;
        const int MaxLength = 48;
        const int MaxOmittedLetters = 2;
        const double DoubledLetter = 0.5, Apostrophe = 0.3, Transposed = 0.5,
                     VowelSwap = 0.7, NearbySwap = 0.8, FirstLetterPenalty = 0.7,
                     MissingVowel = 0.6, MissingConsonant = 0.9;
        // Score penalties for kinds of candidate that are rarely what was meant.
        const double SplitPenalty = 2.0, ProperNounPenalty = 2.0;
        // Log10 count assumed for words missing from the frequency list.
        const float UnknownLog = 0.0f;
        // A regional spelling is at most this many factors of ten rarer than its twin.
        const double VariantFrequencyGap = 1.3;
        // A word missing from the frequency list may only win with a cheaper edit than this.
        const double ObscureFixCost = 1.0;
        // Words up to this length are handled cautiously (see Best).
        public const int ShortWord = 3;
        const double MeantMinLog = 4.0, MeantGap = 1.5, WeakPartLog = 4.5;
        const double CautiousMaxCost = 0.6, CautiousMinLog = 5.0, NameSafeLog = 3.0, KnownNameLog = 3.8;
        // Context may overturn the strongest spelling-only candidate only with a
        // visible margin. This keeps a weak topic association from auto-fixing a
        // word that the dictionary and edit score prefer.
        const double ContextSwitchMargin = 0.35;

        public enum KeyboardLayoutKind { Auto, Qwerty, Qwertz, Azerty, Dvorak, Colemak }

        // The letter rows as the physical keys lie, from the keys of Q, A and Z
        // on; a space is a key without a letter (Dvorak's top row starts with ' , .).
        static readonly bool[,] NearQwerty = BuildNearTable(new string[] { "qwertyuiop", "asdfghjkl", "zxcvbnm" });
        static readonly bool[,] NearQwertz = BuildNearTable(new string[] { "qwertzuiop", "asdfghjkl", "yxcvbnm" });
        static readonly bool[,] NearAzerty = BuildNearTable(new string[] { "azertyuiop", "qsdfghjklm", "wxcvbn" });
        static readonly bool[,] NearDvorak = BuildNearTable(new string[] { "   pyfgcrl", "aoeuidhtns", " qjkxbmwvz" });
        static readonly bool[,] NearColemak = BuildNearTable(new string[] { "qwfpgjluy", "arstdhneio", "zxcvbkm" });

        // Read by the spelling thread; set by it (SetLayout) and by the keyboard
        // thread (UpdateActiveLayout), under layoutGate.
        volatile bool[,] currentNear = NearQwerty;
        readonly object layoutGate = new object();
        // The rows the Auto table was built from, to build it only when they change.
        string[] autoRows;
        public KeyboardLayoutKind Layout = KeyboardLayoutKind.Auto;

        public void SetLayout(KeyboardLayoutKind kind)
        {
            lock (layoutGate)
            {
                Layout = kind;
                autoRows = null;
                switch (kind)
                {
                    case KeyboardLayoutKind.Qwertz: currentNear = NearQwertz; break;
                    case KeyboardLayoutKind.Azerty: currentNear = NearAzerty; break;
                    case KeyboardLayoutKind.Dvorak: currentNear = NearDvorak; break;
                    case KeyboardLayoutKind.Colemak: currentNear = NearColemak; break;
                    default: currentNear = NearQwerty; break;
                }
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

        // Auto: which keys are neighbours comes from the keyboard layout in use,
        // read off its keys (Native.LetterRows), so any layout works: QWERTZ,
        // AZERTY, Dvorak, Colemak and the rest. Called on the keyboard thread with
        // the same array until the layout changes.
        public void UpdateActiveLayout(string[] rows)
        {
            lock (layoutGate)
            {
                if (Layout != KeyboardLayoutKind.Auto || rows == null || ReferenceEquals(rows, autoRows)) return;
                autoRows = rows;
                int letters = 0;
                foreach (string row in rows)
                    foreach (char c in row)
                        if (c != ' ') letters++;
                // Not a Latin layout (or not readable): keep to QWERTY.
                currentNear = letters >= 20 ? BuildNearTable(rows) : NearQwerty;
            }
        }

        readonly Dictionary<string, float> logByWord = new Dictionary<string, float>(StringComparer.Ordinal);
        // Plain a-z words only, used to propose candidates Windows did not offer.
        readonly List<string> words = new List<string>();
        readonly List<float> logs = new List<float>();
        // Log10 count of "first second", keyed by the two words' numbers; and how
        // often each word stands first and second in all pairs together.
        readonly Dictionary<string, int> numberByWord = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] wordByNumber;
        int[][] topFollowers;
        long[][] topFollowerCounts;
        Dictionary<long, float> pairLogs = new Dictionary<long, float>();
        Dictionary<long, float> trigramLogs = new Dictionary<long, float>();
        Dictionary<long, int[]> topTrigramFollowers = new Dictionary<long, int[]>();
        Dictionary<long, float[]> topTrigramCounts = new Dictionary<long, float[]>();
        double[] asFirst, asSecond;
        double pairTotal;
        readonly double[,] grid = new double[MaxLength + 1, MaxLength + 1];

        public Ranker()
        {
            if (!TryLoadBinaryModel())
            {
                LoadTextCorpus();
            }
        }

        bool TryLoadBinaryModel()
        {
            if (!Data.HasResource(ModelResource)) return false;
            try
            {
                using (Stream stream = Data.OpenStream(ModelResource))
                {
                    if (stream == null) return false;
                    using (BinaryReader br = new BinaryReader(stream, Encoding.UTF8))
                    {
                        uint magic = br.ReadUInt32();
                        if (magic != 0x4D48574B) return false; // 'KWHM'
                        uint version = br.ReadUInt32();
                        if (version != 1) return false;

                        uint wordCount = br.ReadUInt32();
                        wordByNumber = new string[wordCount];
                        topFollowers = new int[wordCount][];
                        topFollowerCounts = new long[wordCount][];
                        asFirst = new double[wordCount];
                        asSecond = new double[wordCount];

                        for (uint i = 0; i < wordCount; i++)
                        {
                            ushort len = br.ReadUInt16();
                            byte[] bytes = br.ReadBytes(len);
                            string word = Encoding.UTF8.GetString(bytes);
                            float log = br.ReadSingle();

                            int id = (int)i;
                            wordByNumber[id] = word;
                            logByWord[word] = log;
                            numberByWord[word] = id;
                            if (word.Length >= 3 && IsPlain(word))
                            {
                                words.Add(word);
                                logs.Add(log);
                            }
                        }

                        uint pairCount = br.ReadUInt32();
                        for (uint i = 0; i < pairCount; i++)
                        {
                            int id1 = (int)br.ReadUInt32();
                            int id2 = (int)br.ReadUInt32();
                            float logVal = br.ReadSingle();
                            long count = br.ReadInt64();

                            pairLogs[PairKey(id1, id2)] = logVal;
                            asFirst[id1] += count;
                            asSecond[id2] += count;
                            pairTotal += count;
                            RecordTopFollower(id1, id2, count);
                        }

                        uint trigramCount = br.ReadUInt32();
                        for (uint i = 0; i < trigramCount; i++)
                        {
                            int id1 = (int)br.ReadUInt32();
                            int id2 = (int)br.ReadUInt32();
                            int id3 = (int)br.ReadUInt32();
                            float logVal = br.ReadSingle();

                            trigramLogs[TrigramKey(id1, id2, id3)] = logVal;
                            RecordTopTrigramFollower(id1, id2, id3, logVal);
                        }

                        return true;
                    }
                }
            }
            catch
            {
                logByWord.Clear();
                words.Clear();
                logs.Clear();
                numberByWord.Clear();
                pairLogs.Clear();
                trigramLogs.Clear();
                topTrigramFollowers.Clear();
                topTrigramCounts.Clear();
                wordByNumber = null;
                topFollowers = null;
                topFollowerCounts = null;
                asFirst = null;
                asSecond = null;
                pairTotal = 0;
                return false;
            }
        }

        void LoadTextCorpus()
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

            int totalWords = numberByWord.Count;
            wordByNumber = new string[totalWords];
            foreach (KeyValuePair<string, int> kv in numberByWord)
                wordByNumber[kv.Value] = kv.Key;

            topFollowers = new int[totalWords][];
            topFollowerCounts = new long[totalWords][];
            asFirst = new double[totalWords];
            asSecond = new double[totalWords];
            using (StreamReader reader = Data.Open(PairResource))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
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
                    RecordTopFollower(first, second, count);
                }
            }

            if (Data.HasResource(TrigramResource))
            {
                using (StreamReader reader = Data.Open(TrigramResource))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int s1 = line.IndexOf(' ');
                        if (s1 <= 0) continue;
                        int s2 = line.IndexOf(' ', s1 + 1);
                        if (s2 <= 0) continue;
                        int s3 = line.IndexOf(' ', s2 + 1);
                        if (s3 <= 0) continue;

                        string w1 = line.Substring(0, s1);
                        string w2 = line.Substring(s1 + 1, s2 - s1 - 1);
                        string w3 = line.Substring(s2 + 1, s3 - s2 - 1);
                        long count = 0;
                        for (int i = s3 + 1; i < line.Length; i++) count = count * 10 + (line[i] - '0');

                        int id1, id2, id3;
                        if (count <= 0 || !numberByWord.TryGetValue(w1, out id1) ||
                            !numberByWord.TryGetValue(w2, out id2) ||
                            !numberByWord.TryGetValue(w3, out id3)) continue;

                        float logVal = (float)Math.Log10(count);
                        trigramLogs[TrigramKey(id1, id2, id3)] = logVal;
                        RecordTopTrigramFollower(id1, id2, id3, logVal);
                    }
                }
            }
        }

        void RecordTopTrigramFollower(int first, int second, int third, float count)
        {
            long key = TrigramPrefixKey(first, second);
            int[] f;
            float[] c;
            if (!topTrigramFollowers.TryGetValue(key, out f))
            {
                topTrigramFollowers[key] = new int[] { third };
                topTrigramCounts[key] = new float[] { count };
                return;
            }
            c = topTrigramCounts[key];
            int len = f.Length;
            const int maxFollowers = 32;
            if (len < maxFollowers)
            {
                int[] newF = new int[len + 1];
                float[] newC = new float[len + 1];
                int pos = 0;
                while (pos < len && c[pos] >= count)
                {
                    newF[pos] = f[pos];
                    newC[pos] = c[pos];
                    pos++;
                }
                newF[pos] = third;
                newC[pos] = count;
                while (pos < len)
                {
                    newF[pos + 1] = f[pos];
                    newC[pos + 1] = c[pos];
                    pos++;
                }
                topTrigramFollowers[key] = newF;
                topTrigramCounts[key] = newC;
            }
            else if (count > c[maxFollowers - 1])
            {
                int pos = maxFollowers - 1;
                while (pos > 0 && c[pos - 1] < count)
                {
                    f[pos] = f[pos - 1];
                    c[pos] = c[pos - 1];
                    pos--;
                }
                f[pos] = third;
                c[pos] = count;
            }
        }

        void RecordTopFollower(int first, int second, long count)
        {
            int[] f = topFollowers[first];
            long[] c = topFollowerCounts[first];
            int len = f == null ? 0 : f.Length;
            const int limit = 32;
            if (len == limit && count <= c[limit - 1]) return;
            int pos = 0;
            while (pos < len && c[pos] >= count) pos++;
            if (pos >= limit) return;
            if (len < limit)
            {
                Array.Resize(ref f, len + 1);
                Array.Resize(ref c, len + 1);
                topFollowers[first] = f;
                topFollowerCounts[first] = c;
            }
            for (int i = Math.Min(len, limit - 1); i > pos; i--)
            {
                f[i] = f[i - 1];
                c[i] = c[i - 1];
            }
            f[pos] = second;
            c[pos] = count;
        }

        public string PredictNext(string previous, string prefix)
        {
            return PredictNext(null, previous, prefix);
        }

        // A guess for a word not started yet must be at least this share of what
        // follows the words before; below it the pill would flash after every word.
        const double MinNextShare = 0.05;

        // The hook and spelling service can share this ranker. Cache only one
        // active context and bound prefix entries; never retain typed sentences.
        readonly object predictionGate = new object();
        readonly Dictionary<string, string> predictionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string predictionFirst, predictionPrevious;

        // A small deterministic safety net for common continuations. The corpus
        // remains the primary source; these phrases make prediction useful even
        // when a compact model has no surviving follower for a perfectly normal
        // sentence. Values are complete words, never automatic replacements.
        static readonly Dictionary<string, string[]> CommonNextWords = BuildCommonNextWords();

        static Dictionary<string, string[]> BuildCommonNextWords()
        {
            Dictionary<string, string[]> result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            AddNext(result, "i", "am", "would", "have", "will", "can");
            AddNext(result, "i would", "like", "love", "be", "rather");
            AddNext(result, "i am", "going", "sorry", "sure", "happy");
            AddNext(result, "please", "let", "send", "check", "do");
            AddNext(result, "please let", "me");
            AddNext(result, "let me", "know", "help", "check");
            AddNext(result, "thank you", "for", "very", "so");
            AddNext(result, "as well", "as");
            AddNext(result, "look forward", "to");
            AddNext(result, "in order", "to");
            AddNext(result, "would like", "to");
            AddNext(result, "need to", "know", "make", "get", "be");
            AddNext(result, "going to", "be", "the", "have");
            AddNext(result, "one of", "the", "my", "our", "these");
            AddNext(result, "at the", "same", "end", "moment", "time");
            AddNext(result, "on the", "way", "other", "same", "next");
            AddNext(result, "for the", "first", "last", "next", "same");
            AddNext(result, "see you", "soon", "tomorrow", "there");
            AddNext(result, "have a", "good", "great", "nice", "look");
            AddNext(result, "have an", "idea", "opportunity", "answer");
            return result;
        }

        static void AddNext(Dictionary<string, string[]> result, string key, params string[] words)
        {
            result[key] = words;
        }

        // The likely next word, or the likely end of the word being typed (prefix).
        // Only from the words before: with nothing to go on there is no guess, as
        // the first word in a box is where Tab most often means "next field"
        // ("Name" + Tab must not become "Names ").
        public string PredictNext(string secondPrevious, string previous, string prefix)
        {
            lock (predictionGate)
            {
                if (!string.Equals(predictionFirst, secondPrevious, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(predictionPrevious, previous, StringComparison.OrdinalIgnoreCase))
                {
                    predictionCache.Clear();
                    predictionFirst = secondPrevious;
                    predictionPrevious = previous;
                }
                string key = prefix ?? "";
                string result;
                if (predictionCache.TryGetValue(key, out result)) return result;
                result = PredictNextUncached(secondPrevious, previous, prefix);
                if (predictionCache.Count >= 64) predictionCache.Clear();
                predictionCache[key] = result;
                return result;
            }
        }

        string PredictNextUncached(string secondPrevious, string previous, string prefix)
        {
            if (string.IsNullOrEmpty(previous)) return null;
            string pfx = prefix == null ? "" : prefix.ToLowerInvariant();
            int second;
            if (!numberByWord.TryGetValue(previous.ToLowerInvariant(), out second))
            {
                string[] unknownCommon;
                string unknownKey = (secondPrevious == null ? "" : secondPrevious.ToLowerInvariant() + " ") + previous.ToLowerInvariant();
                if (CommonNextWords.TryGetValue(unknownKey, out unknownCommon))
                    for (int i = 0; i < unknownCommon.Length; i++)
                        if (Completes(unknownCommon[i], pfx)) return unknownCommon[i];
                return null;
            }

            // 1. The two words before.
            int first;
            int[] followers;
            if (!string.IsNullOrEmpty(secondPrevious) && numberByWord.TryGetValue(secondPrevious.ToLowerInvariant(), out first) &&
                topTrigramFollowers.TryGetValue(TrigramPrefixKey(first, second), out followers))
            {
                float[] logs3 = topTrigramCounts[TrigramPrefixKey(first, second)];
                float pairLog;
                bool knownPair = pairLogs.TryGetValue(PairKey(first, second), out pairLog);
                for (int i = 0; i < followers.Length; i++)
                {
                    string candidate = wordByNumber[followers[i]];
                    if (!Completes(candidate, pfx)) continue;
                    if (pfx.Length > 0 || (knownPair && Math.Pow(10, logs3[i] - pairLog) >= MinNextShare)) return candidate;
                    break;
                }
            }

            // 2. The word before.
            followers = topFollowers[second];
            if (followers != null)
            {
                long[] counts = topFollowerCounts[second];
                for (int i = 0; i < followers.Length; i++)
                {
                    string candidate = wordByNumber[followers[i]];
                    if (!Completes(candidate, pfx)) continue;
                    if (pfx.Length > 0 || counts[i] >= MinNextShare * asFirst[second]) return candidate;
                    break;
                }
            }

            // The statistical model is intentionally conservative. Fall back
            // only after it has no candidate, and still require a typed prefix
            // match (or an explicit multi-word phrase) before showing a pill.
            string key = (secondPrevious == null ? "" : secondPrevious.ToLowerInvariant() + " ") + previous.ToLowerInvariant();
            string[] common;
            if (CommonNextWords.TryGetValue(key, out common))
            {
                for (int i = 0; i < common.Length; i++)
                    if (Completes(common[i], pfx)) return common[i];
            }
            return null;
        }

        static bool Completes(string candidate, string prefix)
        {
            return prefix.Length == 0 || (candidate.Length > prefix.Length && candidate.StartsWith(prefix, StringComparison.Ordinal));
        }

        // Multiplied, not shifted: a long's hash is its two halves xor-ed, which
        // for two small numbers side by side collides all the time.
        static long PairKey(int first, int second) { return first * 1000003L + second; }

        static long TrigramKey(int first, int second, int third)
        {
            return (((long)(uint)first) << 34) | (((long)(uint)second) << 17) | (long)(uint)third;
        }

        static long TrigramPrefixKey(int first, int second)
        {
            return (((long)(uint)first) << 17) | (long)(uint)second;
        }

        // By how many factors of ten the words before make a candidate likelier
        // (or, below zero, less likely) than it is anyway: how often they
        // stand together, against how often they would by chance.
        double Context(string secondPrevious, string previous, string candidate)
        {
            if (previous == null) return Context((string[])null, candidate);
            return Context(secondPrevious == null ? new string[] { previous } : new string[] { secondPrevious, previous }, candidate);
        }

        // Score the candidate against up to six earlier words. The adjacent pair
        // and trigram remain strongest; older words provide a decaying topic
        // signal, useful when the immediately preceding word is uninformative.
        double Context(string[] context, string candidate)
        {
            if (!UseContext || context == null || context.Length == 0) return 0;
            double score = 0;
            int last = context.Length - 1;
            if (last > 0)
            {
                double tri = ContextTrigram(context[last - 1], context[last], candidate);
                if (tri > 0) score += tri;
            }
            double[] weights = { 1.0, 0.4, 0.2, 0.12, 0.08, 0.05 };
            for (int distance = 0; distance < weights.Length && last - distance >= 0; distance++)
                score += weights[distance] * ContextPair(context[last - distance], candidate);
            return Math.Max(-MaxContext, Math.Min(MaxContext, score));
        }

        double Context(string previous, string candidate)
        {
            return Context(null, previous, candidate);
        }

        double ContextTrigram(string w1Str, string w2Str, string w3Str)
        {
            if (w1Str == null || w2Str == null || w3Str == null) return 0;
            int space = w3Str.IndexOf(' ');
            string w3Word = space > 0 ? w3Str.Substring(0, space) : w3Str;

            int first, second, third;
            if (!numberByWord.TryGetValue(w1Str.ToLowerInvariant(), out first) ||
                !numberByWord.TryGetValue(w2Str.ToLowerInvariant(), out second) ||
                !numberByWord.TryGetValue(w3Word.ToLowerInvariant(), out third)) return 0;

            float triLog;
            if (!trigramLogs.TryGetValue(TrigramKey(first, second, third), out triLog)) return 0;
            return 1.2;
        }

        double ContextPair(string prev, string candidate)
        {
            if (prev == null) return 0;
            int space = candidate.IndexOf(' ');
            int first, second;
            if (!numberByWord.TryGetValue(prev, out first) ||
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
        // context = up to six earlier words, lower case, oldest first: it helps
        // choose between candidates but never makes a weak guess acceptable.
        // others, if given, receives the next-best candidates.
        public string Best(string typed, string previous, IList<string> windows, bool certain, bool cautious,
            Func<string, bool> isCorrect, List<Scored> trace, List<string> others)
        {
            return Best(typed, previous == null ? null : new string[] { previous }, windows, certain, cautious, isCorrect, trace, others);
        }

        public string Best(string typed, string secondPrevious, string previous, IList<string> windows, bool certain, bool cautious,
            Func<string, bool> isCorrect, List<Scored> trace, List<string> others)
        {
            string[] context = previous == null ? null : secondPrevious == null ? new string[] { previous } : new string[] { secondPrevious, previous };
            return Best(typed, context, windows, certain, cautious, isCorrect, trace, others);
        }

        public string Best(string typed, string[] context, IList<string> windows, bool certain, bool cautious,
            Func<string, bool> isCorrect, List<Scored> trace, List<string> others)
        {
            if (string.IsNullOrEmpty(typed)) return null;
            string lower = typed.ToLowerInvariant().Replace('\u2018', '\'').Replace('\u2019', '\'');
            if (lower.Length > MaxLength) return null;

            List<Scored> all = new List<Scored>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < windows.Count; i++)
            {
                string candidate = windows[i];
                string candidateLower = candidate.ToLowerInvariant().Replace('\u2018', '\'').Replace('\u2019', '\'');
                if (candidateLower.Length == 0 || candidateLower.Length > MaxLength || !seen.Add(candidateLower)) continue;
                if (IsWeakSplit(lower, candidateLower)) continue;
                double cost = Cost(lower, candidateLower);
                double score = FrequencyWeight * LogFrequency(candidateLower) - EditWeight * cost + WindowsBonus / (i + 1);
                // Typed in lower case, so a name is unlikely - unless only the capital was missing.
                if (candidate != candidateLower && candidateLower != lower && !candidateLower.StartsWith("i'"))
                    score -= ProperNounPenalty;
                all.Add(new Scored { Word = candidate, Cost = cost, Plain = score,
                    Score = score + ContextWeight * Context(context, candidateLower), FromWindows = true });
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
                    all.Add(new Scored { Word = word, Cost = cost, Plain = score, Score = score + ContextWeight * Context(context, word) });
                }
            }

            // When the dictionary vouches for its answer, take it; otherwise rank.
            Scored plainWinner = null;
            foreach (Scored candidate in all)
                if (candidate.FromWindows && (plainWinner == null || candidate.Plain > plainWinner.Plain)) plainWinner = candidate;
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
            if (!certain && plainWinner != null && winner != plainWinner &&
                winner.Score < plainWinner.Score + ContextSwitchMargin) return null;
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
            // An unknown lower-case word turned into a different name ("yatesh" ->
            // "Yates") is most likely a name the dictionary lacks. Only well-known
            // names win: days, months, countries, big cities ("londn" -> "London").
            if (!certain && char.IsLower(typed[0]) && char.IsUpper(winner.Word[0]) && fix != lower &&
                commonness < KnownNameLog) return null;
            // "approx" -> "approx.": an abbreviation, not a misspelling.
            if (fix.EndsWith(".")) return null;
            if (AcceptVariants && IsSpellingVariant(lower, fix)) return null;
            if (cautious)
            {
                // A capitalised word the dictionary does not know is probably a
                // name; never turn it into an uncommon word ("Tarun" -> "Taron").
                if (char.IsUpper(typed[0]) && commonness < NameSafeLog) return null;
                // Unsure about a short or capitalised word: only an obvious slip
                // (two letters swapped, a doubled letter) or high-confidence single proximity/vowel substitution.
                bool allowedCost = winner.Cost <= CautiousMaxCost ||
                    (winner.Cost <= 0.85 && commonness >= CautiousMinLog && LogFrequency(lower) <= commonness - 2.5);
                if (!certain && (!allowedCost || commonness < CautiousMinLog)) return null;
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
            string w = word;
            if (w.EndsWith("our") && w.Length > 4 && w != "scour" && w != "flour")
                w = w.Substring(0, w.Length - 3) + "or";
            else if (w.EndsWith("ours") && w.Length > 5 && w != "scours" && w != "flours")
                w = w.Substring(0, w.Length - 4) + "ors";
            else if (w.EndsWith("oured") && w.Length > 6)
                w = w.Substring(0, w.Length - 5) + "ored";
            else if (w.EndsWith("ouring") && w.Length > 7)
                w = w.Substring(0, w.Length - 6) + "oring";
            // "judgement", "acknowledgement", "ageing".
            w = w.Replace("dgement", "dgment").Replace("ageing", "aging");
            w = w.Replace("ise", "ize").Replace("isi", "izi").Replace("isa", "iza")
                 .Replace("yse", "yze").Replace("ysi", "yzi");
            w = w.Replace("elled", "eled").Replace("elling", "eling").Replace("eller", "eler").Replace("ellor", "elor")
                 .Replace("alled", "aled").Replace("alling", "aling")
                 .Replace("llery", "lry").Replace("skilful", "skillful");
            if (w.EndsWith("ogue")) w = w.Substring(0, w.Length - 4) + "og";
            else if (w.EndsWith("ogues")) w = w.Substring(0, w.Length - 5) + "ogs";
            if (w == "defence" || w == "offence" || w == "pretence" || w == "licence")
                w = w.Substring(0, w.Length - 2) + "se";
            w = w.Replace("ae", "e").Replace("oe", "e").Replace("grey", "gray");
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
            if (BaseChar(x) == BaseChar(y)) return 0.2;
            if (IsVowel(x) && IsVowel(y)) return VowelSwap;
            char bx = BaseChar(x), by = BaseChar(y);
            if (bx >= 'a' && bx <= 'z' && by >= 'a' && by <= 'z' && currentNear[bx - 'a', by - 'a']) return NearbySwap;
            return 1.0;
        }

        static char BaseChar(char c)
        {
            if (c >= 'a' && c <= 'z') return c;
            switch (c)
            {
                case 'á': case 'à': case 'ä': case 'â': return 'a';
                case 'é': case 'è': case 'ë': case 'ê': return 'e';
                case 'í': case 'ì': case 'ï': case 'î': return 'i';
                case 'ó': case 'ò': case 'ö': case 'ô': return 'o';
                case 'ú': case 'ù': case 'ü': case 'û': return 'u';
                case 'ç': return 'c';
                case 'ñ': return 'n';
                default: return c;
            }
        }

        static bool IsVowel(char c)
        {
            return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' ||
                   c == 'á' || c == 'à' || c == 'ä' || c == 'â' ||
                   c == 'é' || c == 'è' || c == 'ë' || c == 'ê' ||
                   c == 'í' || c == 'ì' || c == 'ï' || c == 'î' ||
                   c == 'ó' || c == 'ò' || c == 'ö' || c == 'ô' ||
                   c == 'ú' || c == 'ù' || c == 'ü' || c == 'û';
        }

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

        // Keys without a letter (' ') have no neighbours.
        static void Pair(bool[,] near, char x, char y)
        {
            if (x < 'a' || x > 'z' || y < 'a' || y > 'z') return;
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
