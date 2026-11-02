using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KeyboardWriterHelper
{
    static class PersonalizationTests
    {
        static int passed, failed;

        static int Main()
        {
            Run("rejection threshold and case-insensitive vocabulary", RejectionThreshold);
            Run("alternative threshold and conflicting feedback", ChoiceThreshold);
            Run("disabled learning neither learns nor applies", DisabledLearning);
            Run("persistence retains pending evidence and explicit removals", Persistence);
            Run("invalid inputs and inspectable immutable snapshots", InvalidInputs);
            Run("corrupt files are retained until explicit reset", CorruptFiles);
            Run("storage failure is visible and retryable", StorageFailure);
            Run("learning storage stays bounded", BoundedStorage);
            Run("only the final selected candidate is committed", FinalCandidate);
            Run("returning to the automatic candidate is not feedback", CycleBack);
            Run("undo and discarded selections do not confirm choices", DiscardedChoices);
            Run("settings and reset invalidate earlier feedback", StaleFeedback);
            Run("failed output cannot teach a correction", FailedOutput);
            Run("explicit undo protects vocabulary without affecting ordinary typing", UndoLearning);
            Run("committed alternative is learned and preserves capitalization", AlternativeLearning);
            Run("selecting the original text learns rejection", OriginalSelection);
            Run("phrase rejection protects only that phrase", PhraseRejection);
            Run("manual words and fixes override learned entries", ManualOverrides);
            Run("disabled grammar skips learned phrases but permits manual phrases", PhraseSetting);
            Run("clearing learning preserves explicit personal lists", ClearPreservesManual);
            Run("case-only corrections and compose do not train vocabulary", NonSpellingFeedback);
            Run("a Backspace before punctuation or an edit is no rejection", BackspaceIsNotRejection);
            Run("version 1 files lose their rejections and keep their choices", VersionOneFile);
            Console.WriteLine("Personalization regression tests: {0} passed, {1} failed", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + "; expected " + expected + ", got " + actual);
        }

        static void Protect(PersonalLearning learning, string typed)
        {
            learning.RecordRejection(typed);
            learning.RecordRejection(typed);
        }

        static void Prefer(PersonalLearning learning, string typed, string fix)
        {
            for (int i = 0; i < 3; i++) learning.RecordChoice(typed, fix);
        }

        static void RejectionThreshold()
        {
            PersonalLearning learning = new PersonalLearning(null, true);
            Check(!learning.RecordRejection("Tarun"), "one undo must not activate protection");
            Check(!learning.IsProtected("tarun"), "pending evidence must not affect typing");
            Equal(1, learning.All()[0].Confirmations, "pending feedback is inspectable");
            Check(learning.RecordRejection("tarun"), "second explicit rejection activates protection");
            Check(learning.IsProtected("TARUN"), "protection must be case-insensitive");
            Check(!learning.RecordRejection("Tarun"), "an active entry must not announce activation repeatedly");
            Equal(2, learning.All()[0].Confirmations, "evidence remains bounded at threshold");
            Equal<string>(null, learning.GetFix("tarun"), "protected words have no replacement");
        }

        static void ChoiceThreshold()
        {
            PersonalLearning learning = new PersonalLearning(null, true);
            learning.RecordChoice("misp", "miss");
            learning.RecordChoice("Misp", "Miss");
            Equal<string>(null, learning.GetFix("misp"), "two choices are insufficient");
            Check(learning.RecordChoice("misp", "miss"), "third consistent choice activates preference");
            Equal("miss", learning.GetFix("MISP"), "same candidate across capitalization counts together");
            learning.RecordChoice("misp", "most");
            Equal<string>(null, learning.GetFix("misp"), "a conflicting alternative resets evidence");
            Equal(1, learning.All()[0].Confirmations, "conflicting choices do not accumulate together");
            Prefer(learning, "misp", "most");
            learning.RecordRejection("misp");
            Check(!learning.IsProtected("misp"), "a rejection needs its own second confirmation");
            Equal<string>(null, learning.GetFix("misp"), "rejection removes a contrary active preference");
            learning.RecordRejection("misp");
            Check(learning.IsProtected("misp"), "two rejects replace the preference with protection");
            learning.RecordChoice("misp", "miss");
            Check(!learning.IsProtected("misp"), "new explicit choices reset contradictory rejection evidence");
        }

        static void DisabledLearning()
        {
            PersonalLearning learning = new PersonalLearning(null, false);
            Protect(learning, "teh");
            Prefer(learning, "misp", "miss");
            Equal(0, learning.All().Length, "disabled feedback must retain nothing");
            learning.Enabled = true;
            Protect(learning, "teh");
            Prefer(learning, "misp", "miss");
            learning.Enabled = false;
            Check(!learning.IsProtected("teh"), "disabled protection must not affect typing");
            Equal<string>(null, learning.GetFix("misp"), "disabled preferences must not affect typing");
            Equal(2, learning.All().Length, "disabling keeps learned entries inspectable");
            learning.Enabled = true;
            Check(learning.IsProtected("teh"), "re-enabling restores retained protection");
            Equal("miss", learning.GetFix("misp"), "re-enabling restores preferences");
        }

        static void Persistence()
        {
            using (TempFolder folder = new TempFolder())
            {
                string path = Path.Combine(folder.Path, "learning.txt");
                PersonalLearning original = new PersonalLearning(path, true);
                original.RecordRejection("tarun");
                original.RecordChoice("misp", "miss");
                original.Save();
                Equal<string>(null, original.LastError, "initial save succeeds");
                PersonalLearning restored = new PersonalLearning(path, true);
                Equal(2, restored.All().Length, "pending evidence survives restart");
                Check(restored.RecordRejection("Tarun"), "pending rejection continues after restart");
                restored.RecordChoice("misp", "miss");
                restored.RecordChoice("misp", "miss");
                restored.Save();
                restored = new PersonalLearning(path, true);
                Check(restored.IsProtected("tarun"), "active protection survives restart");
                Equal("miss", restored.GetFix("misp"), "active preference survives restart");
                restored.Remove("TARUN");
                restored.Save();
                restored = new PersonalLearning(path, false);
                Equal(1, restored.All().Length, "per-entry removal is persistent");
                restored.Clear();
                restored.Save();
                Equal(0, new PersonalLearning(path, true).All().Length, "clear is persistent even while disabled");
                Equal(0, Directory.GetFiles(folder.Path, "*.tmp").Length, "successful saves leave no temporary files");
            }
        }

        static void InvalidInputs()
        {
            PersonalLearning learning = new PersonalLearning(null, true);
            string[] invalid = { null, "", "  ", "user@example.com", "https://example.com", "abc123", "a\tb", "a\nb", "{clip}", "one two three four five", new string('a', 41), "word'", "'word", "two--words" };
            foreach (string text in invalid)
            {
                learning.RecordRejection(text);
                learning.RecordChoice("misp", text);
                learning.RecordChoice(text, "miss");
            }
            learning.RecordChoice("name", "Name");
            Equal(0, learning.All().Length, "invalid and capitalization-only examples must not be stored");
            Protect(learning, "  my   name  ");
            Check(learning.IsProtected("my name"), "spaces normalize without retaining arbitrary whitespace");
            Protect(learning, "O’Neil");
            Check(learning.IsProtected("O’Neil"), "names with a typographic apostrophe are supported");
            PersonalLearningEntry[] snapshot = learning.All();
            snapshot[0] = null;
            Equal(2, learning.All().Length, "modifying a returned array cannot mutate the store");
        }

        static void CorruptFiles()
        {
            using (TempFolder folder = new TempFolder())
            {
                string path = Path.Combine(folder.Path, "learning.txt");
                PersonalLearning valid = new PersonalLearning(path, true);
                Protect(valid, "tarun");
                valid.Save();
                string good = File.ReadAllText(path);
                string[] corrupt =
                {
                    "KeyboardWriterHelper.PersonalLearning\t999\nP\t2\ttarun\n",
                    good + "not a valid entry\n",
                    good + "P\t2\ttarun\n",
                    good + "P\t999\tother\n",
                    good + "C\t3\tname\tName\n",
                    good + "P\t2\tuser@example.com\n"
                };
                foreach (string broken in corrupt)
                {
                    File.WriteAllText(path, broken, new UTF8Encoding(false));
                    PersonalLearning loaded = new PersonalLearning(path, true);
                    Check(loaded.LastError != null, "malformed data is reported");
                    Equal(0, loaded.All().Length, "partially parsed files are never applied");
                    Protect(loaded, "another");
                    loaded.Save();
                    Equal(broken, File.ReadAllText(path), "ordinary feedback must preserve malformed existing files");
                    loaded.Clear();
                    loaded.Save();
                    Equal<string>(null, loaded.LastError, "explicit reset repairs the file");
                    Equal(0, new PersonalLearning(path, true).All().Length, "reset file is readable");
                }
                File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0xff });
                Check(new PersonalLearning(path, true).LastError != null, "invalid UTF-8 is rejected safely");
            }
        }

        static void StorageFailure()
        {
            using (TempFolder folder = new TempFolder())
            {
                string blocker = Path.Combine(folder.Path, "blocked");
                File.WriteAllText(blocker, "retained");
                string target = Path.Combine(blocker, "learning.txt");
                PersonalLearning learning = new PersonalLearning(target, true);
                learning.Clear();
                Protect(learning, "tarun");
                learning.Save();
                Check(learning.LastError != null, "unwritable storage must expose an error");
                Check(learning.IsProtected("tarun"), "an IO failure must not lose in-memory evidence");
                Equal("retained", File.ReadAllText(blocker), "failed save does not alter the blocking user file");
                File.Move(blocker, blocker + ".preserved");
                Directory.CreateDirectory(blocker);
                learning.Save();
                Equal<string>(null, learning.LastError, "save retries after the underlying problem is resolved");
                Check(new PersonalLearning(target, true).IsProtected("tarun"), "retry persists previously unsaved evidence");
                Equal(0, Directory.GetFiles(blocker, "*.tmp").Length, "failed and retried saves leave no temporary artifacts");
            }
        }

        static void BoundedStorage()
        {
            PersonalLearning learning = new PersonalLearning(null, true);
            for (int i = 0; i < 2100; i++) learning.RecordRejection("term" + Letters(i));
            Equal(2000, learning.All().Length, "the number of retained entries is bounded");
            learning.RecordRejection("terma");
            Check(learning.IsProtected("terma"), "existing evidence can still reach threshold at capacity");
            learning.Remove("terma");
            learning.RecordRejection("newterm");
            Equal(2000, learning.All().Length, "removing an entry makes space for new feedback");
        }

        static string Letters(int number)
        {
            string text = "";
            do { text = (char)('a' + number % 26) + text; number = number / 26 - 1; } while (number >= 0);
            return text;
        }

        static void FinalCandidate()
        {
            Fixture fixture = new Fixture();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Next();
            Equal(0, fixture.Committed.Count, "cycling alone does not confirm feedback");
            fixture.Type("x");
            Equal(1, fixture.Committed.Count, "continuing typing commits once");
            Equal("misp|mist|most", fixture.Committed[0], "feedback includes original, automatic and final selected text");
            fixture.Type("yz");
            Equal(1, fixture.Committed.Count, "later letters cannot recommit the old correction");
            Equal(1, fixture.Learning.All()[0].Confirmations, "only the final candidate gets evidence");
            Equal("most", fixture.Learning.All()[0].Replacement, "intermediate alternatives are not learned");
        }

        static void CycleBack()
        {
            Fixture fixture = new Fixture();
            fixture.Type("misp ");
            for (int i = 0; i < 4; i++) fixture.Next();
            fixture.Type("x");
            Equal(0, fixture.Committed.Count, "returning to choice zero creates no choice event");
            Equal(0, fixture.Learning.All().Length, "cycling all the way around does not train the model");
        }

        static void DiscardedChoices()
        {
            Fixture fixture = new Fixture();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Engine.Break(false);
            fixture.Type("x");
            Equal(0, fixture.Committed.Count, "caret/focus breaks discard tentative choices");
            Equal(0, fixture.Learning.All().Length, "discarded choices leave no retained feedback");
            fixture.Start();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Backspace();
            fixture.Type(" ");
            Equal(0, fixture.Committed.Count, "undoing a selected alternative does not confirm it");
            Equal("misp|miss", fixture.Undone[0], "undo reports the visible alternative, not the first automatic fix");
            Equal(1, fixture.Learning.All()[0].Confirmations, "undo creates one rejection only");
            Equal<string>(null, fixture.Learning.All()[0].Replacement, "undo never establishes a correction preference");
            fixture.Start();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Learning.Enabled = false;
            fixture.Type("x");
            Equal(1, fixture.Learning.All()[0].Confirmations, "disabling before commit prevents late feedback");
        }

        static void FailedOutput()
        {
            Fixture fixture = new Fixture();
            fixture.Type("misp");
            fixture.FailNextEmit = true;
            fixture.Type(" ");
            Equal(1, fixture.OutputFailures, "initial correction failure was exercised");
            Check(!fixture.Engine.CanNext, "a failed correction leaves no candidate to confirm");
            fixture.Type("x");
            Equal(0, fixture.Learning.All().Length, "failed correction cannot teach anything");
            fixture.Start();
            fixture.Type("misp ");
            fixture.FailNextEmit = true;
            fixture.Next();
            fixture.Type("x");
            Equal(2, fixture.OutputFailures, "candidate replacement failure was exercised");
            Equal(0, fixture.Committed.Count, "a failed alternative cannot be committed");
            fixture.Start();
            fixture.Type("misp ");
            fixture.FailNextEmit = true;
            fixture.Backspace();
            Equal(3, fixture.OutputFailures, "undo output failure was exercised");
            Equal(0, fixture.Undone.Count, "a failed undo creates no rejection event");
            Equal(0, fixture.Learning.All().Length, "all failed outputs leave learning unchanged");
        }

        static void StaleFeedback()
        {
            Fixture fixture = new Fixture();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Learning.Enabled = false;
            fixture.Learning.Enabled = true;
            fixture.Type("x");
            Equal(0, fixture.Learning.All().Length, "disable/re-enable must invalidate an earlier tentative choice");
            fixture.Start();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Learning.Clear();
            fixture.Type("x");
            Equal(0, fixture.Learning.All().Length, "clear must not be undone by feedback from an earlier correction");
            fixture.Start();
            fixture.Type("misp ");
            fixture.Next();
            fixture.Learning.RecordRejection("misp");
            fixture.Learning.Remove("misp");
            fixture.Type("x");
            Equal(0, fixture.Learning.All().Length, "removing an entry invalidates pending feedback");
            fixture.Start();
            fixture.Learning.Enabled = false;
            fixture.Type("misp ");
            fixture.Next();
            fixture.Learning.Enabled = true;
            fixture.Type("x");
            Equal(0, fixture.Learning.All().Length, "a correction made while disabled is never eligible later");
        }

        static void UndoLearning()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start(); fixture.Type("wierd ");
                Equal("weird ", fixture.Screen.ToString(), "the deterministic built-in correction was exercised");
                fixture.Backspace();
                fixture.Type(" ");
            }
            Check(fixture.Learning.IsProtected("wierd"), "repeated explicit undo protects common deterministic corrections");
            fixture.Start();
            fixture.Type("wierd cat dog ");
            Equal("wierd cat dog ", fixture.Screen.ToString(), "protection is applied before built-in corrections");
            fixture.Start(); fixture.Type("one. wierd ");
            Equal("one. wierd ", fixture.Screen.ToString(), "protection also suppresses sentence capitalization");
            Equal(1, fixture.Learning.All().Length, "ordinary known words and automatic acceptance are not recorded");
            Equal(0, fixture.ManualWords.Count, "automatic learning never changes the explicit word list");
        }

        static void AlternativeLearning()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 3; i++)
            {
                fixture.Start();
                fixture.Type("misp ");
                fixture.Next();
                fixture.Type("x");
            }
            Equal("miss", fixture.Learning.GetFix("misp"), "three explicit final selections activate preference");
            fixture.Start();
            fixture.Type("misp ");
            Equal("miss ", fixture.Screen.ToString(), "the learned candidate supersedes the dictionary's automatic choice");
            fixture.Start();
            fixture.Type("Misp ");
            Equal("Miss ", fixture.Screen.ToString(), "learned fixes preserve a typed initial capital");
            Equal(0, fixture.ManualFixes.Count, "learning does not mutate explicit fixes");
        }

        static void OriginalSelection()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start();
                fixture.Type("misp ");
                fixture.Next(); fixture.Next(); fixture.Next();
                fixture.Type("x");
            }
            Check(fixture.Learning.IsProtected("misp"), "committing the original is explicit rejection evidence");
            fixture.Start();
            fixture.Type("misp ");
            Equal("misp ", fixture.Screen.ToString(), "repeated original selection protects the typed spelling");
        }

        static void PhraseRejection()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start(); fixture.Type("he have "); fixture.Backspace(); fixture.Type(" ");
            }
            Check(fixture.Learning.IsProtected("he have"), "grammar rejection uses the full phrase");
            Check(!fixture.Learning.IsProtected("have"), "phrase rejection must not globally ignore its last word");
            fixture.Start(); fixture.Type("he have ");
            Equal("he have ", fixture.Screen.ToString(), "learned phrase protection suppresses dynamic grammar");
            fixture.Start(); fixture.Type("they has ");
            Equal("they have ", fixture.Screen.ToString(), "other agreement corrections continue working");
        }

        static void ManualOverrides()
        {
            Fixture fixture = new Fixture();
            Prefer(fixture.Learning, "misp", "miss");
            fixture.ManualWords.Add("misp");
            fixture.Type("misp ");
            Equal("misp ", fixture.Screen.ToString(), "manual words defeat learned replacement");
            fixture.ManualWords.Clear();
            Protect(fixture.Learning, "misp");
            fixture.ManualFixes["misp"] = "custom";
            fixture.Start(); fixture.Type("misp ");
            Equal("custom ", fixture.Screen.ToString(), "manual fixes defeat learned protection");
            Prefer(fixture.Learning, "misp", "miss");
            fixture.Start(); fixture.Type("misp ");
            Equal("custom ", fixture.Screen.ToString(), "manual fixes defeat learned replacement");
            fixture.Learning.Clear();
            fixture.ManualFixes["clipx"] = "{clip}";
            fixture.Engine.Clipboard = delegate { return "private phrase"; };
            for (int i = 0; i < 2; i++)
            {
                fixture.Start(); fixture.Type("clipx ");
                Equal("private phrase ", fixture.Screen.ToString(), "the expanded clipboard shortcut was exercised");
                fixture.Backspace();
                fixture.Type(" ");
            }
            Equal(0, fixture.Learning.All().Length, "manual snippet feedback never persists expanded clipboard contents");
        }

        static void PhraseSetting()
        {
            Fixture fixture = new Fixture();
            Prefer(fixture.Learning, "purple fish", "purple bird");
            fixture.Type("purple fish ");
            Equal("purple bird ", fixture.Screen.ToString(), "learned multiword fixes apply during grammar correction");
            fixture.Engine.FixPhrases = false;
            fixture.Start(); fixture.Type("purple fish ");
            Equal("purple fish ", fixture.Screen.ToString(), "grammar disabled skips learned phrase preferences");
            fixture.ManualFixes["purple fish"] = "purple cat";
            fixture.Start(); fixture.Type("purple fish ");
            Equal("purple cat ", fixture.Screen.ToString(), "manual phrase shortcuts still work with grammar disabled");
        }

        static void ClearPreservesManual()
        {
            Fixture fixture = new Fixture();
            fixture.ManualWords.Add("wierd");
            fixture.ManualFixes["misp"] = "custom";
            Protect(fixture.Learning, "tarun");
            Prefer(fixture.Learning, "wrod", "word");
            fixture.Learning.Clear();
            fixture.Type("wierd misp ");
            Equal("wierd custom ", fixture.Screen.ToString(), "clear affects only learned data");
            Equal(1, fixture.ManualWords.Count, "manual vocabulary is preserved");
            Equal(1, fixture.ManualFixes.Count, "manual replacements are preserved");
            Equal(0, fixture.Learning.All().Length, "learned data has been cleared");
        }

        static void NonSpellingFeedback()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start(); fixture.Type("i "); fixture.Backspace(); fixture.Type(" ");
                fixture.Start(); fixture.Type("THe "); fixture.Backspace(); fixture.Type(" ");
                fixture.Start(); fixture.Type("one. then "); fixture.Backspace(); fixture.Type(" ");
            }
            Equal(0, fixture.Learning.All().Length, "capitalization-only rejections do not teach lexical protection");
            fixture.Engine.SmartDashes = true;
            fixture.Start(); fixture.Type("-- "); fixture.Backspace();
            Equal(0, fixture.Learning.All().Length, "typography undo does not train vocabulary");
            Engine compose = new Engine(delegate { return new Suggestion { Known = true }; }, delegate { return false; },
                delegate { return null; }, delegate { }, delegate(string second, string previous, string prefix) { return "hello"; });
            int feedback = 0;
            compose.ChoiceCommitted += delegate { feedback++; };
            compose.Undone += delegate { feedback++; };
            compose.Char('h');
            Check(compose.AcceptPrediction(), "compose prediction was accepted");
            compose.Backspace();
            Equal(0, feedback, "compose acceptance and undo are not spelling feedback");
        }

        static void BackspaceIsNotRejection()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 3; i++)
            {
                // Backspace to take the space away, then a comma: the fix goes back.
                fixture.Start(); fixture.Type("wierd "); fixture.Backspace(); fixture.Type(",");
                Equal("weird,", fixture.Screen.ToString(), "punctuation after the Backspace puts the fix back");
                // Backspace, then more letters: the word is being edited.
                fixture.Start(); fixture.Type("wierd "); fixture.Backspace(); fixture.Type("o ");
                // Backspace, then Enter or a click: the undo stands but says nothing.
                fixture.Start(); fixture.Type("wierd "); fixture.Backspace(); fixture.Engine.Break(true);
            }
            Equal(0, fixture.Learning.All().Length, "none of these is a rejection");
            Equal(6, fixture.Undone.Count, "the edits and the breaks still count as undone, the put-back fixes do not");

            // Putting the fix back and undoing it again is insisting on the typed word.
            fixture.Start(); fixture.Type("wierd "); fixture.Backspace(); fixture.Type(",");
            fixture.Backspace();
            Equal("wierd", fixture.Screen.ToString(), "the second undo takes the comma away with the fix");
            fixture.Type(",");
            Equal("wierd,", fixture.Screen.ToString(), "the fix is put back only once");
            fixture.Start();
            Equal(1, fixture.Learning.All().Length, "insisting counts as one rejection");
        }

        static void VersionOneFile()
        {
            using (TempFolder folder = new TempFolder())
            {
                string path = Path.Combine(folder.Path, "learning.txt");
                File.WriteAllText(path, "KeyboardWriterHelper.PersonalLearning\t1\n" +
                    "P\t2\treprot\nP\t1\tchnage\nC\t3\tmisp\tmiss\n");
                PersonalLearning learning = new PersonalLearning(path, true);
                Equal<string>(null, learning.LastError, "a version 1 file loads");
                Check(!learning.IsProtected("reprot"), "rejections counted by the old rule are dropped");
                Equal(1, learning.All().Length, "only the chosen alternative remains");
                Equal("miss", learning.GetFix("misp"), "chosen alternatives are kept");
                learning.Save();
                Check(File.ReadAllText(path).StartsWith("KeyboardWriterHelper.PersonalLearning\t2\n"), "the file is rewritten as version 2");
                Equal(1, new PersonalLearning(path, true).All().Length, "the rewritten file loads");
            }
        }

        // Uses the production policy and store with a deterministic dictionary.
        // Windows injection remains covered by the existing keyboard self-test.
        sealed class Fixture
        {
            public readonly PersonalLearning Learning = new PersonalLearning(null, true);
            public readonly HashSet<string> ManualWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ManualFixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly StringBuilder Screen = new StringBuilder();
            public readonly List<string> Committed = new List<string>(), Undone = new List<string>();
            public readonly Engine Engine;
            public readonly Personalization Policy;
            public bool FailNextEmit;
            public int OutputFailures;

            public Fixture()
            {
                Policy = new Personalization(Learning, ManualWords.Contains,
                    delegate(string text) { string fix; return ManualFixes.TryGetValue(text, out fix) ? fix : null; },
                    delegate { return Engine.FixPhrases; });
                Engine = new Engine(Suggest, Policy.IsIgnored, Policy.OwnFix, Emit, null);
                Engine.DoubleSpacePeriod = Engine.SmartDashes = Engine.SmartArrows = Engine.SmartEllipsis = false;
                Policy.Attach(Engine);
                Engine.ChoiceCommitted += delegate(string typed, string initial, string selected)
                {
                    Committed.Add(typed + "|" + initial + "|" + selected);
                };
                Engine.Undone += delegate(string typed, string fix)
                {
                    Undone.Add(typed + "|" + fix);
                };
            }

            static Suggestion Suggest(string word, string[] context, bool cautious)
            {
                if (word.Equals("misp", StringComparison.OrdinalIgnoreCase))
                    return new Suggestion { Fix = "mist", Others = new string[] { "miss", "most" } };
                return new Suggestion { Known = true };
            }

            void Emit(int backspaces, string text)
            {
                if (FailNextEmit)
                {
                    FailNextEmit = false;
                    throw new IOException("Simulated unavailable target");
                }
                if (backspaces > Screen.Length) throw new InvalidOperationException("Engine erased before the tracked text");
                Screen.Length -= backspaces;
                Screen.Append(text);
            }

            public void Start() { Engine.Break(true); Screen.Length = 0; }

            public void Type(string text)
            {
                foreach (char c in text)
                {
                    bool swallowed = false;
                    try { swallowed = Engine.Char(c); }
                    catch (IOException) { OutputFailures++; Engine.Break(false); }
                    if (!swallowed) Screen.Append(c);
                }
            }

            public void Next()
            {
                try { Check(Engine.Next(), "a correction should be available to cycle"); }
                catch (IOException) { OutputFailures++; Engine.Break(false); }
            }

            public void Backspace()
            {
                bool swallowed = false;
                try { swallowed = Engine.Backspace(); }
                catch (IOException) { OutputFailures++; Engine.Break(false); }
                if (!swallowed && Screen.Length > 0) Screen.Length--;
            }
        }

        sealed class TempFolder : IDisposable
        {
            public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kwh-personalization-" + Guid.NewGuid().ToString("N"));
            public TempFolder() { Directory.CreateDirectory(Path); }
            public void Dispose() { Directory.Delete(Path, true); }
        }
    }
}
