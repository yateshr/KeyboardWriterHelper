using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace KeyboardWriterHelper
{
    // Only explicit feedback is recorded. No sentences, clipboard contents or
    // automatically accepted corrections are observed by this store.
    sealed class PersonalLearningEntry
    {
        public readonly string Typed, Replacement;
        public readonly int Confirmations;
        public readonly bool IsActive;

        internal PersonalLearningEntry(string typed, string replacement, int confirmations)
        {
            Typed = typed;
            Replacement = replacement;
            Confirmations = confirmations;
            IsActive = confirmations >= (replacement == null ? PersonalLearning.RejectionThreshold : PersonalLearning.ChoiceThreshold);
        }
    }

    sealed class PersonalLearning
    {
        internal const int RejectionThreshold = 2, ChoiceThreshold = 3;
        const int MaxEntries = 2000, MaxWords = 4, MaxWordLength = 40;
        const int MaxTextLength = MaxWords * (MaxWordLength + 1) - 1;
        const int MaxLineLength = MaxTextLength * 2 + 16, MaxFileBytes = 2 * 1024 * 1024;
        const string Header = "KeyboardWriterHelper.PersonalLearning\t1";
        readonly object gate = new object(), saveGate = new object();
        readonly Dictionary<string, PersonalLearningEntry> entries = new Dictionary<string, PersonalLearningEntry>(StringComparer.OrdinalIgnoreCase);
        readonly string path;
        bool enabled, protectExistingFile;
        long revision, savedRevision, feedbackGeneration;
        string lastError;

        public PersonalLearning(string path, bool enabled)
        {
            this.path = path;
            this.enabled = enabled;
            Load();
        }

        public bool Enabled
        {
            get { lock (gate) return enabled; }
            set
            {
                lock (gate)
                {
                    if (enabled == value) return;
                    enabled = value;
                    feedbackGeneration++;
                }
            }
        }

        public string LastError { get { lock (gate) return lastError; } }
        internal long FeedbackGeneration { get { lock (gate) return feedbackGeneration; } }

        public bool RecordRejection(string typed)
        {
            string key;
            if (!TryNormalize(typed, out key)) return false;
            lock (gate)
            {
                if (!enabled) return false;
                PersonalLearningEntry previous;
                bool exists = entries.TryGetValue(key, out previous);
                if (!exists && entries.Count >= MaxEntries) return false;
                int count = exists && previous.Replacement == null ? Math.Min(previous.Confirmations + 1, RejectionThreshold) : 1;
                if (exists && previous.Replacement == null && previous.Confirmations == count) return false;
                PersonalLearningEntry next = new PersonalLearningEntry(key, null, count);
                entries[key] = next;
                revision++;
                return next.IsActive && (!exists || !previous.IsActive || previous.Replacement != null);
            }
        }

        public bool RecordChoice(string typed, string replacement)
        {
            string key, fix;
            if (!TryNormalize(typed, out key) || !TryNormalize(replacement, out fix) ||
                string.Equals(key, fix, StringComparison.OrdinalIgnoreCase)) return false;
            lock (gate)
            {
                if (!enabled) return false;
                PersonalLearningEntry previous;
                bool exists = entries.TryGetValue(key, out previous);
                if (!exists && entries.Count >= MaxEntries) return false;
                bool same = exists && string.Equals(previous.Replacement, fix, StringComparison.OrdinalIgnoreCase);
                int count = same ? Math.Min(previous.Confirmations + 1, ChoiceThreshold) : 1;
                if (same && previous.Confirmations == count) return false;
                // The first spelling of an alternative is retained while its
                // evidence grows, so capitalization cannot inflate that evidence.
                PersonalLearningEntry next = new PersonalLearningEntry(key, same ? previous.Replacement : fix, count);
                entries[key] = next;
                revision++;
                return next.IsActive && (!exists || !previous.IsActive || !same);
            }
        }

        public bool IsProtected(string typed)
        {
            string key;
            if (!TryNormalize(typed, out key)) return false;
            lock (gate)
            {
                PersonalLearningEntry entry;
                return enabled && entries.TryGetValue(key, out entry) && entry.IsActive && entry.Replacement == null;
            }
        }

        public string GetFix(string typed)
        {
            string key;
            if (!TryNormalize(typed, out key)) return null;
            lock (gate)
            {
                PersonalLearningEntry entry;
                return enabled && entries.TryGetValue(key, out entry) && entry.IsActive ? entry.Replacement : null;
            }
        }

        // Immutable copies include pending evidence so the user can inspect and
        // remove everything retained, even while learning is switched off.
        public PersonalLearningEntry[] All()
        {
            lock (gate) return Snapshot();
        }

        PersonalLearningEntry[] Snapshot()
        {
            PersonalLearningEntry[] result = new PersonalLearningEntry[entries.Count];
            entries.Values.CopyTo(result, 0);
            Array.Sort(result, delegate(PersonalLearningEntry a, PersonalLearningEntry b)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(a.Typed, b.Typed);
            });
            return result;
        }

        public void Remove(string typed)
        {
            string key;
            if (!TryNormalize(typed, out key)) return;
            lock (gate)
            {
                if (entries.Remove(key))
                {
                    revision++;
                    feedbackGeneration++;
                }
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                // Clearing is an explicit instruction to replace even a corrupt
                // or unsupported old file. Ordinary feedback never overwrites it.
                protectExistingFile = false;
                revision++;
                feedbackGeneration++;
            }
        }

        // The caller runs this on a timer/background thread, never in the hook.
        // Serialize writers, snapshot under the short memory lock, then do IO
        // without it. Failed saves leave the revision dirty for the next retry.
        public void Save()
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (saveGate)
            {
                PersonalLearningEntry[] snapshot;
                long snapshotRevision;
                lock (gate)
                {
                    if (protectExistingFile || revision == savedRevision) return;
                    snapshot = Snapshot();
                    snapshotRevision = revision;
                }
                try
                {
                    StringBuilder text = new StringBuilder(Header).Append('\n');
                    foreach (PersonalLearningEntry entry in snapshot)
                    {
                        text.Append(entry.Replacement == null ? 'P' : 'C').Append('\t');
                        text.Append(entry.Confirmations.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(entry.Typed);
                        if (entry.Replacement != null) text.Append('\t').Append(entry.Replacement);
                        text.Append('\n');
                    }
                    AtomicWrite(path, text.ToString());
                    lock (gate)
                    {
                        savedRevision = snapshotRevision;
                        lastError = null;
                    }
                }
                catch (Exception error)
                {
                    if (!IsStorageError(error)) throw;
                    lock (gate) lastError = "Could not save personal learning: " + error.Message;
                }
            }
        }

        void Load()
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                byte[] bytes;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaxFileBytes) throw new InvalidDataException("The file exceeds the supported size.");
                    bytes = new byte[(int)stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int count = stream.Read(bytes, read, bytes.Length - read);
                        if (count == 0) throw new InvalidDataException("The file changed while being read.");
                        read += count;
                    }
                    if (stream.ReadByte() != -1) throw new InvalidDataException("The file changed while being read.");
                }
                string text = new UTF8Encoding(false, true).GetString(bytes);
                Dictionary<string, PersonalLearningEntry> loaded = new Dictionary<string, PersonalLearningEntry>(StringComparer.OrdinalIgnoreCase);
                using (StringReader reader = new StringReader(text))
                {
                    string header = ReadLine(reader);
                    if (header != null && header.Length != 0 && header[0] == '\uFEFF') header = header.Substring(1);
                    if (header != Header) throw new InvalidDataException("The file format or version is unsupported.");
                    string line;
                    while ((line = ReadLine(reader)) != null)
                    {
                        if (loaded.Count >= MaxEntries) throw new InvalidDataException("The file contains too many entries.");
                        string[] fields = line.Split('\t');
                        bool preserve = fields.Length == 3 && fields[0] == "P";
                        if (!preserve && !(fields.Length == 4 && fields[0] == "C")) throw new InvalidDataException("The file contains an invalid entry.");
                        int count;
                        string key, fix = null;
                        if (!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 ||
                            count > (preserve ? RejectionThreshold : ChoiceThreshold) ||
                            !TryNormalize(fields[2], out key) || key != fields[2] ||
                            (!preserve && (!TryNormalize(fields[3], out fix) || fix != fields[3] || string.Equals(key, fix, StringComparison.OrdinalIgnoreCase))) ||
                            loaded.ContainsKey(key)) throw new InvalidDataException("The file contains an invalid entry.");
                        loaded.Add(key, new PersonalLearningEntry(key, fix, count));
                    }
                }
                foreach (KeyValuePair<string, PersonalLearningEntry> entry in loaded) entries.Add(entry.Key, entry.Value);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error)
            {
                if (!IsStorageError(error)) throw;
                protectExistingFile = true;
                lastError = "Personal learning could not be loaded; the existing file was retained. " + error.Message;
            }
        }

        static string ReadLine(StringReader reader)
        {
            StringBuilder line = new StringBuilder();
            int value;
            while ((value = reader.Read()) != -1)
            {
                if (value == '\n') return line.ToString();
                if (value == '\r')
                {
                    if (reader.Peek() == '\n') reader.Read();
                    return line.ToString();
                }
                if (line.Length >= MaxLineLength) throw new InvalidDataException("The file contains an oversized entry.");
                line.Append((char)value);
            }
            return line.Length == 0 ? null : line.ToString();
        }

        static bool TryNormalize(string text, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength) return false;
            try { text = text.Normalize(NormalizationForm.FormC).Replace('\u2018', '\'').Replace('\u2019', '\''); }
            catch (ArgumentException) { return false; }
            StringBuilder result = new StringBuilder(text.Length);
            int words = 0, wordLength = 0;
            bool lastWasLetter = false, separator = false;
            foreach (char ch in text)
            {
                if (ch == ' ')
                {
                    if (wordLength != 0 && !lastWasLetter) return false;
                    if (wordLength != 0) separator = true;
                    wordLength = 0;
                    lastWasLetter = false;
                    continue;
                }
                bool letter = char.IsLetter(ch);
                UnicodeCategory category = char.GetUnicodeCategory(ch);
                bool mark = category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark;
                bool connector = ch == '\'' || ch == '-';
                if (!letter && !(mark && lastWasLetter) && !(connector && lastWasLetter)) return false;
                if (wordLength == 0)
                {
                    if (!letter || ++words > MaxWords) return false;
                    if (separator) result.Append(' ');
                    separator = false;
                }
                if (++wordLength > MaxWordLength) return false;
                result.Append(ch);
                lastWasLetter = letter || mark;
            }
            if (words == 0 || (wordLength != 0 && !lastWasLetter)) return false;
            normalized = result.ToString();
            return true;
        }

        static bool IsStorageError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is SecurityException ||
                   error is ArgumentException || error is NotSupportedException;
        }

        static void AtomicWrite(string target, string text)
        {
            string directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool ownsTemporary = false;
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                ownsTemporary = false;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (SecurityException) { }
                }
            }
        }
    }
}
