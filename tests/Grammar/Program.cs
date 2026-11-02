using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KeyboardWriterHelper
{
    // Exercises the production grammar state machine without Windows APIs.
    // Dictionary answers are deterministic: words are known except the explicit
    // spelling fixture. The same quoted cases also run through --cases on Windows.
    static class GrammarTests
    {
        static int passed, failed;

        static int Main(string[] args)
        {
            string[] files = args.Length == 0
                ? new string[] { "grammar-cases.txt", "must-not-change.txt" } : args;
            foreach (string file in files) RunCases(file);
            Run("dictionary correction completes auxiliary grammar", SpellThenGrammar);
            Run("learned phrase rejection respects noun agreement boundaries", LearnedNounRejection);
            Run("learned rejection protects deferred agreement phrases", LearnedDeferredRejection);
            Run("learned phrase rejection respects auxiliary boundaries", LearnedAuxiliaryRejection);
            Run("candidate cycling restores context for following words", CycleContext);
            Run("undo restores context for following words", UndoContext);
            Run("input directives and control tokens are strict", StrictInput);
            Console.WriteLine("Grammar regression tests: {0} passed, {1} failed", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            try { test(); passed++; }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        static void Equal(string expected, string actual)
        {
            if (expected != actual)
                throw new InvalidOperationException("expected \"" + Escape(expected) + "\", got \"" + Escape(actual) + "\"");
        }

        static string Escape(string text) { return text.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n"); }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static void RunCases(string path)
        {
            string actualPath = File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(actualPath))
            {
                Run(path, delegate { throw new FileNotFoundException("Required grammar fixture is missing", actualPath); });
                return;
            }
            const string Arrow = "\" => \"";
            CaseSetup setup = new CaseSetup();
            string[] lines = File.ReadAllLines(actualPath);
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string where = Path.GetFileName(path) + " line " + (n + 1);
                if (line[0] == '@')
                {
                    try { setup.Apply(line.Substring(1).Trim()); }
                    catch (Exception error) { failed++; Console.WriteLine("FAIL " + where + ": " + error.Message); }
                    continue;
                }
                int arrow = line.IndexOf(Arrow, StringComparison.Ordinal);
                if (line[0] != '"' || arrow < 1 || line.Length <= arrow + Arrow.Length || line[line.Length - 1] != '"')
                {
                    failed++;
                    Console.WriteLine("FAIL " + where + ": invalid quoted case");
                    continue;
                }
                string typed = line.Substring(1, arrow - 1);
                string expected = line.Substring(arrow + Arrow.Length, line.Length - arrow - Arrow.Length - 1).Replace("\\t", "\t");
                Run(where + " [" + typed + "]", delegate { Equal(expected, setup.NewFixture().Type(typed)); });
            }
        }

        static void SpellThenGrammar()
        {
            Fixture fixture = new Fixture();
            Equal("He doesn't like it. ", fixture.Type("He doesn't likse it. "));
            fixture.Start();
            Equal("Does she like it? ", fixture.Type("Does she likse it? "));
            fixture.Start();
            Equal("The files are ready. ", fixture.Type("The files iz ready. "));
        }

        static void LearnedNounRejection()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start();
                Equal("The files are ", fixture.Type("The files is "));
                fixture.Type("{BS}");
                Equal("The files is", fixture.Screen.ToString());
                // The Space keeps it as typed: that is the rejection.
                fixture.Type(" ");
            }
            Check(fixture.Learning.All().Length == 1, "only explicit rejection evidence should be retained");
            fixture.Start();
            Equal("The files is ready. ", fixture.Type("The files is ready. "));
            fixture.Start();
            Equal("The folders are ready. ", fixture.Type("The folders is ready. "));
            fixture.Start();
            Equal("He is ready. ", fixture.Type("He are ready. "));
        }

        static void LearnedAuxiliaryRejection()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start();
                Equal("He doesn't like ", fixture.Type("He doesn't likes "));
                fixture.Type("{BS} ");
            }
            fixture.Start();
            Equal("He doesn't likes it. ", fixture.Type("He doesn't likes it. "));
            fixture.Start();
            Equal("He doesn't work here. ", fixture.Type("He doesn't works here. "));
            Check(!fixture.Learning.IsProtected("likes"), "phrase rejection must not globally protect its verb");
        }

        static void LearnedDeferredRejection()
        {
            Fixture fixture = new Fixture();
            for (int i = 0; i < 2; i++)
            {
                fixture.Start();
                Equal("Everyone has arrived ", fixture.Type("Everyone have arrived "));
                fixture.Type("{BS} ");
            }
            fixture.Start();
            Equal("Everyone have arrived. ", fixture.Type("Everyone have arrived. "));
            fixture.Start();
            Equal("Everyone has been here. ", fixture.Type("Everyone have been here. "));
            Check(!fixture.Learning.IsProtected("have"), "deferred phrase rejection must not globally protect have");
        }

        static void CycleContext()
        {
            Fixture fixture = new Fixture();
            Equal("The files is ", fixture.Type("The files is {NEXT}"));
            Equal("The files are ready. ", fixture.Type("{NEXT}ready. "));
            fixture.Start();
            Equal("He doesn't likes ", fixture.Type("He doesn't likes {NEXT}"));
            Equal("He doesn't like it. ", fixture.Type("{NEXT}it. "));
        }

        static void UndoContext()
        {
            Fixture fixture = new Fixture();
            Equal("The files is ready. ", fixture.Type("The files is {BS} ready. "));
            fixture.Start();
            Equal("He doesn't likes it. ", fixture.Type("He doesn't likes {BS} it. "));
            fixture.Start();
            Equal("The files are ready. They work here. ", fixture.Type("The files is ready. they works here. "));
        }

        static void StrictInput()
        {
            CaseSetup setup = new CaseSetup();
            bool rejected = false;
            try { setup.Apply("phrases maybe"); } catch (FormatException) { rejected = true; }
            Check(rejected, "invalid toggle value must fail");
            rejected = false;
            try { setup.Apply("unknown on"); } catch (FormatException) { rejected = true; }
            Check(rejected, "unknown directives must fail");
            rejected = false;
            try { new Fixture().Type("hello {UNKNOWN}"); } catch (FormatException) { rejected = true; }
            Check(rejected, "unknown control tokens must fail");
            Equal("hello\t", new Fixture().Type("hello{TAB}"));
        }

        sealed class CaseSetup
        {
            readonly HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> fixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool punctuation = true, capitals = true, twoCapitals = true, phrases = true;
            bool doubleSpace, smartDashes, smartArrows, smartEllipsis, smartCompose = true;

            public void Apply(string line)
            {
                string[] parts = line.Split(new char[] { ' ' }, 2);
                string name = parts[0], value = parts.Length > 1 ? parts[1].Trim() : "";
                if (name == "reset" && value.Length == 0)
                {
                    words.Clear(); fixes.Clear();
                    punctuation = capitals = twoCapitals = phrases = smartCompose = true;
                    doubleSpace = smartDashes = smartArrows = smartEllipsis = false;
                    return;
                }
                if (name == "word" && value.Length > 0) { words.Add(value); return; }
                int equals = value.IndexOf('=');
                if (name == "fix" && equals > 0 && equals < value.Length - 1)
                {
                    fixes[value.Substring(0, equals)] = value.Substring(equals + 1);
                    return;
                }
                if (value != "on" && value != "off") throw new FormatException("Invalid directive: " + line);
                bool on = value == "on";
                switch (name)
                {
                    case "punctuation": punctuation = on; break;
                    case "capitals": capitals = on; break;
                    case "twocapitals": twoCapitals = on; break;
                    case "phrases": phrases = on; break;
                    case "doublespace": doubleSpace = on; break;
                    case "smartdashes": smartDashes = on; break;
                    case "smartarrows": smartArrows = on; break;
                    case "smartellipsis": smartEllipsis = on; break;
                    case "smartcompose": smartCompose = on; break;
                    default: throw new FormatException("Unknown directive: " + line);
                }
            }

            public Fixture NewFixture()
            {
                Fixture fixture = new Fixture();
                foreach (string word in words) fixture.Words.Add(word);
                foreach (KeyValuePair<string, string> fix in fixes) fixture.Fixes[fix.Key] = fix.Value;
                fixture.Engine.FixOnPunctuation = punctuation;
                fixture.Engine.CapitalizeSentences = capitals;
                fixture.Engine.FixTwoCapitals = twoCapitals;
                fixture.Engine.FixPhrases = phrases;
                fixture.Engine.DoubleSpacePeriod = doubleSpace;
                fixture.Engine.SmartDashes = smartDashes;
                fixture.Engine.SmartArrows = smartArrows;
                fixture.Engine.SmartEllipsis = smartEllipsis;
                fixture.Engine.SmartCompose = smartCompose;
                return fixture;
            }
        }

        sealed class Fixture
        {
            public readonly PersonalLearning Learning = new PersonalLearning(null, true);
            public readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Fixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly StringBuilder Screen = new StringBuilder();
            public readonly Engine Engine;

            public Fixture()
            {
                Personalization policy = new Personalization(Learning, Words.Contains,
                    delegate(string text) { string fix; return Fixes.TryGetValue(text, out fix) ? fix : null; },
                    delegate { return Engine.FixPhrases; });
                Engine = new Engine(Suggest, policy.IsIgnored, policy.OwnFix, Emit, null);
                policy.Attach(Engine);
            }

            static Suggestion Suggest(string word, string[] context, bool cautious)
            {
                if (word.Equals("likse", StringComparison.OrdinalIgnoreCase)) return new Suggestion { Fix = "likes" };
                if (word.Equals("iz", StringComparison.OrdinalIgnoreCase)) return new Suggestion { Fix = "is" };
                return new Suggestion { Known = true };
            }

            void Emit(int backspaces, string text)
            {
                if (backspaces > Screen.Length) throw new InvalidOperationException("Engine erased before the tracked text");
                Screen.Length -= backspaces;
                Screen.Append(text);
            }

            public void Start() { Engine.Break(true); Screen.Length = 0; }

            public string Type(string text)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '{')
                    {
                        int close = text.IndexOf('}', i);
                        if (close < 0) throw new FormatException("Unclosed control token");
                        string token = text.Substring(i, close - i + 1);
                        if (token == "{BS}")
                        {
                            if (!Engine.Backspace() && Screen.Length > 0) Screen.Length--;
                        }
                        else if (token == "{NEXT}") Engine.Next();
                        else if (token == "{TAB}")
                        {
                            if (!Engine.AcceptPrediction()) Screen.Append('\t');
                        }
                        else throw new FormatException("Unknown control token: " + token);
                        i = close;
                    }
                    else if (!Engine.Char(c)) Screen.Append(c);
                }
                return Screen.ToString();
            }
        }
    }
}
