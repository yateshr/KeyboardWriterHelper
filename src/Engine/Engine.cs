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

    // ---- Typing state machine (no Windows dependencies, so it can be simulated) ----

    

    sealed class Engine

    {

        sealed class Undo

        {

            // Lead + Original stood on screen and Fix replaced it. Lead is "" or,

            // when the word before was replaced as well, that word and its space.

            public string Lead, Original, Fix, Terminator;

            // The word before, as it was when this one was typed.

            public string Previous, SecondPrevious, ThirdPrevious;

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

        readonly Func<string, string, string, bool, Suggestion> suggestWithContext; // (word, secondPrevious, previous, cautious)

        readonly Func<string, string, string> predict; // (previous, prefix) -> predictedWord

        readonly Func<string, bool> isIgnored;

        // The user's own fix for a word or a two-word phrase, or null.

        readonly Func<string, string> ownFix;

        readonly Action<int, string> emit; // (backspaces, text to type)

    

        readonly StringBuilder word = new StringBuilder();

        // Closing punctuation typed after the word, e.g. the ")." in "wrod). "

        readonly StringBuilder trail = new StringBuilder();

        readonly StringBuilder symbols = new StringBuilder();

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

        string secondPrevious;

        string thirdPrevious;

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

        public volatile bool SmartDashes;

        public volatile bool SmartArrows;

        public volatile bool SmartEllipsis;

        public volatile bool SmartCompose = true;

    

        sealed class PredictionUndo

        {

            public string Prefix;

            public string Prediction;

            public string Remaining;

        }



        PredictionUndo predictionUndo;

        string currentPrediction;

        string currentPrefix;



        public bool CanAcceptPrediction { get { return currentPrediction != null; } }

        public string CurrentPrediction { get { return currentPrediction; } }



        // (typed, fix). A phrase fix reports both words.

        public event Action<string, string> Corrected;

        public event Action<string, string> Undone;

        // (typed, fix before, fix now): another candidate was picked.

        public event Action<string, string, string> Changed;

        public event Action<string, string[]> CandidateOffered;

        public event Action CandidateDismissed;

        public event Action<string, string> PredictionOffered;

        public event Action PredictionDismissed;

    

        public Engine(Func<string, string, bool, Suggestion> suggest, Func<string, bool> isIgnored,

            Func<string, string> ownFix, Action<int, string> emit)

            : this(suggest, isIgnored, ownFix, emit, null)

        {

        }



        public Engine(Func<string, string, bool, Suggestion> suggest, Func<string, bool> isIgnored,

            Func<string, string> ownFix, Action<int, string> emit, Func<string, string, string> predict)

        {

            this.suggest = suggest;

            this.isIgnored = isIgnored;

            this.ownFix = ownFix;

            this.emit = emit;

            this.predict = predict;

        }



        public Engine(Func<string, string, string, bool, Suggestion> suggestWithContext, Func<string, bool> isIgnored,

            Func<string, string> ownFix, Action<int, string> emit, Func<string, string, string> predict)

        {

            this.suggestWithContext = suggestWithContext;

            this.suggest = delegate(string w, string p, bool c) { return suggestWithContext(w, null, p, c); };

            this.isIgnored = isIgnored;

            this.ownFix = ownFix;

            this.emit = emit;

            this.predict = predict;

        }



        public bool AcceptPrediction()

        {

            if (currentPrediction == null) return false;

            string pred = currentPrediction;

            string pfx = currentPrefix ?? "";

            currentPrediction = null;

            currentPrefix = null;



            string remaining = pred.Length >= pfx.Length ? pred.Substring(pfx.Length) : pred;

            remaining += " ";



            emit(0, remaining);



            predictionUndo = new PredictionUndo { Prefix = pfx, Prediction = pred, Remaining = remaining };

            undo = null;



            thirdPrevious = secondPrevious;

            secondPrevious = previous;

            previous = pred;

            Clear();

            spaceAfterWord = true;

            tainted = false;



            if (PredictionDismissed != null) PredictionDismissed();

            return true;

        }



        void DismissPrediction()

        {

            if (currentPrediction != null)

            {

                currentPrediction = null;

                currentPrefix = null;

                if (PredictionDismissed != null) PredictionDismissed();

            }

        }

    

        // clean = the caret is most likely at the start of fresh text.

        public void Break(bool clean)

        {

            Clear();

            undo = null;

            predictionUndo = null;

            DismissPrediction();

            capNext = false;

            thirdPrevious = secondPrevious = previous = null;

            spaceAfterWord = madeFullStop = false;

            symbols.Length = 0;

            tainted = !clean;

            if (CandidateDismissed != null) CandidateDismissed();

            if (clean)

            {

                afterSentenceEnd = true;

                otherLanguage = false;

            }

        }

    

        // Returns true when the key was replaced by emitted text and must be swallowed.

        public bool Char(char c)

        {

            if (c == '\u2018' || c == '\u2019') c = '\'';

            if (undo != null && CandidateDismissed != null) CandidateDismissed();

            undo = null;

            predictionUndo = null;

            bool secondSpace = spaceAfterWord && c == ' ';

            spaceAfterWord = madeFullStop = false;

    

            if (symbols.Length < 16) symbols.Append(c);

            else { symbols.Remove(0, 1); symbols.Append(c); }

    

            if (char.IsLetter(c) || (c == '\'' && word.Length > 0 && trail.Length == 0))

            {

                if (tainted) return false;

                // A letter right after punctuation: "example.com", "word,word". Not prose.

                if (trail.Length > 0) { Clear(); tainted = true; DismissPrediction(); return false; }

                if (word.Length == 0)

                {

                    wordAtSentenceStart = afterSentenceEnd;

                    afterSentenceEnd = false;

                }

                word.Append(c);

                if (word.Length > MaxWordLength) { Clear(); tainted = true; DismissPrediction(); return false; }

                if (SmartCompose && predict != null && !tainted && word.Length >= 1)
                {
                    string pfx = word.ToString();
                    string pred = predict(previous == null ? null : previous.ToLowerInvariant(), pfx.ToLowerInvariant());
                    if (!string.IsNullOrEmpty(pred) && !pred.Equals(pfx, StringComparison.OrdinalIgnoreCase))
                    {
                        if (char.IsUpper(pfx[0]) && char.IsLower(pred[0]))
                            pred = char.ToUpperInvariant(pred[0]) + pred.Substring(1);
                        currentPrediction = pred;
                        currentPrefix = pfx;
                        if (PredictionOffered != null) PredictionOffered(currentPrediction, currentPrefix);
                    }
                    else
                    {
                        DismissPrediction();
                    }
                }
                else
                {
                    DismissPrediction();
                }

                return false;

            }

    

            if (c == ' ')

            {

                string sym = symbols.ToString();

                if (SmartDashes && sym.EndsWith("--- "))

                {

                    emit(3, "— ");

                    undo = new Undo { Lead = "", Original = "---", Fix = "—", Terminator = " ", Previous = previous, SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, Choices = new string[] { "—", "---" } };

                    symbols.Length = 0;

                    Clear();

                    tainted = false;

                    thirdPrevious = secondPrevious = previous = null;

                    if (CandidateOffered != null) CandidateOffered("—", undo.Choices);

                    return true;

                }

                if (SmartDashes && sym.EndsWith("-- "))

                {

                    emit(2, "— ");

                    undo = new Undo { Lead = "", Original = "--", Fix = "—", Terminator = " ", Previous = previous, SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, Choices = new string[] { "—", "--" } };

                    symbols.Length = 0;

                    Clear();

                    tainted = false;

                    thirdPrevious = secondPrevious = previous = null;

                    if (CandidateOffered != null) CandidateOffered("—", undo.Choices);

                    return true;

                }

                if (SmartEllipsis && sym.EndsWith("... "))

                {

                    emit(3, "… ");

                    undo = new Undo { Lead = "", Original = "...", Fix = "…", Terminator = " ", Previous = previous, SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, Choices = new string[] { "…", "..." } };

                    symbols.Length = 0;

                    Clear();

                    tainted = false;

                    afterSentenceEnd = true;

                    capNext = true;

                    thirdPrevious = secondPrevious = previous = null;

                    if (CandidateOffered != null) CandidateOffered("…", undo.Choices);

                    return true;

                }

                if (SmartArrows && sym.EndsWith("-> "))

                {

                    emit(2, "→ ");

                    undo = new Undo { Lead = "", Original = "->", Fix = "→", Terminator = " ", Previous = previous, SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, Choices = new string[] { "→", "->" } };

                    symbols.Length = 0;

                    Clear();

                    tainted = false;

                    thirdPrevious = secondPrevious = previous = null;

                    if (CandidateOffered != null) CandidateOffered("→", undo.Choices);

                    return true;

                }

                if (SmartArrows && sym.EndsWith("=> "))

                {

                    emit(2, "⇒ ");

                    undo = new Undo { Lead = "", Original = "=>", Fix = "⇒", Terminator = " ", Previous = previous, SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, Choices = new string[] { "⇒", "=>" } };

                    symbols.Length = 0;

                    Clear();

                    tainted = false;

                    thirdPrevious = secondPrevious = previous = null;

                    if (CandidateOffered != null) CandidateOffered("⇒", undo.Choices);

                    return true;

                }

                if (SmartArrows && sym.EndsWith("<- "))

                {

                    emit(2, "← ");

                    undo = new Undo { Lead = "", Original = "<-", Fix = "←", Terminator = " ", Previous = previous, SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, Choices = new string[] { "←", "<-" } };

                    symbols.Length = 0;

                    Clear();

                    tainted = false;

                    thirdPrevious = secondPrevious = previous = null;

                    if (CandidateOffered != null) CandidateOffered("←", undo.Choices);

                    return true;

                }

    

                // As on a phone keyboard. The first space is already on screen.

                if (secondSpace && DoubleSpacePeriod)

                {

                    emit(1, ". ");

                    madeFullStop = true;

                    afterSentenceEnd = true;

                    capNext = true;

                    thirdPrevious = secondPrevious = previous = null;

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

                thirdPrevious = plain ? secondPrevious : null;

                secondPrevious = plain ? previous : null;

                previous = plain ? LastWord(shown) : null;

                spaceAfterWord = plain;

                if (hadWord)

                    capNext = ending.Length > 0 && IsSentenceEnd(ending[ending.Length - 1]) &&

                              (ending[ending.Length - 1] != '.' || !IsAbbreviation(last));

                if (SmartCompose && predict != null && plain && previous != null)
                {
                    string pred = predict(previous.ToLowerInvariant(), "");
                    if (!string.IsNullOrEmpty(pred))
                    {
                        if (capNext && char.IsLower(pred[0]))
                            pred = char.ToUpperInvariant(pred[0]) + pred.Substring(1);
                        currentPrediction = pred;
                        currentPrefix = "";
                        if (PredictionOffered != null) PredictionOffered(currentPrediction, currentPrefix);
                    }
                    else
                    {
                        DismissPrediction();
                    }
                }
                else
                {
                    DismissPrediction();
                }

                return swallowed;

            }

    

            // Punctuation right after a word: fix it now if the setting is on.

            if (FixOnPunctuation && !tainted && word.Length > 0 && trail.Length == 0 && ",;:!?".IndexOf(c) >= 0 &&

                TryCorrect(c.ToString()))

            {

                DismissPrediction();

                if (IsSentenceEnd(c)) afterSentenceEnd = true;

                capNext = false;

                thirdPrevious = secondPrevious = previous = null;

                Clear();

                // Whatever follows with no space is part of the same token.

                tainted = true;

                return true;

            }

    

            // Punctuation after a word is held back: the word is only corrected once

            // the following space shows the token really ended there.

            if (!tainted && word.Length > 0 && IsCloser(c))

            {

                DismissPrediction();

                trail.Append(c);

                if (IsSentenceEnd(c)) afterSentenceEnd = true;

                return false;

            }

    

            thirdPrevious = secondPrevious = previous = null;

            DismissPrediction();

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

            if (predictionUndo != null)
            {
                PredictionUndo pu = predictionUndo;
                predictionUndo = null;
                DismissPrediction();

                emit(pu.Remaining.Length, "");
                word.Length = 0;
                word.Append(pu.Prefix);
                trail.Length = 0;
                previous = secondPrevious;
                secondPrevious = thirdPrevious;
                thirdPrevious = null;
                spaceAfterWord = false;
                tainted = false;

                if (SmartCompose && predict != null && word.Length >= 1)
                {
                    string pfx = word.ToString();
                    string pred = predict(previous == null ? null : previous.ToLowerInvariant(), pfx.ToLowerInvariant());
                    if (!string.IsNullOrEmpty(pred) && !pred.Equals(pfx, StringComparison.OrdinalIgnoreCase))
                    {
                        if (char.IsUpper(pfx[0]) && char.IsLower(pred[0]))
                            pred = char.ToUpperInvariant(pred[0]) + pred.Substring(1);
                        currentPrediction = pred;
                        currentPrefix = pfx;
                        if (PredictionOffered != null) PredictionOffered(currentPrediction, currentPrefix);
                    }
                }
                return true;
            }

            bool fullStop = madeFullStop;

            spaceAfterWord = madeFullStop = false;

            DismissPrediction();

            if (fullStop)

            {

                // Back to the two spaces that were typed.

                emit(2, "  ");

                afterSentenceEnd = false;

                capNext = false;

                return true;

            }

            if (symbols.Length > 0) symbols.Length--;

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

                secondPrevious = u.SecondPrevious;

                thirdPrevious = u.ThirdPrevious;

                if (CandidateDismissed != null) CandidateDismissed();

                // Not an undo if "next suggestion" had already brought the typed text back.

                if (u.Fix != u.Lead + u.Original && Undone != null) Undone(u.Lead + u.Original, u.Fix);

                return true;

            }

            if (trail.Length > 0) trail.Length--;

            else if (word.Length > 0)
            {
                word.Length--;
                if (SmartCompose && predict != null && !tainted && word.Length >= 1)
                {
                    string pfx = word.ToString();
                    string pred = predict(previous == null ? null : previous.ToLowerInvariant(), pfx.ToLowerInvariant());
                    if (!string.IsNullOrEmpty(pred) && !pred.Equals(pfx, StringComparison.OrdinalIgnoreCase))
                    {
                        if (char.IsUpper(pfx[0]) && char.IsLower(pred[0]))
                            pred = char.ToUpperInvariant(pred[0]) + pred.Substring(1);
                        currentPrediction = pred;
                        currentPrefix = pfx;
                        if (PredictionOffered != null) PredictionOffered(currentPrediction, currentPrefix);
                    }
                }
            }

            else

            {

                tainted = true;

                thirdPrevious = secondPrevious = previous = null;

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

            if (CandidateOffered != null) CandidateOffered(u.Fix, u.Choices);

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

                              SecondPrevious = secondPrevious, ThirdPrevious = thirdPrevious, AtSentenceStart = wordAtSentenceStart, Choices = Choices(typed, fix) };

            if (Corrected != null) Corrected(typed, fix);

            if (CandidateOffered != null) CandidateOffered(fix, undo.Choices);

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

        // `lead` when the replacement also covers the words before.

        string Decide(string w, string terminator)

        {

            string phraseLead;

            string phrase = PhraseFix(w, out phraseLead);

            if (phrase != null) return AsPhrase(phraseLead, w, phrase);

    

            string fix = DecideWord(w, terminator);

            // The corrected word may complete a phrase: "their si" -> "there is".

            if (fix != null && (phrase = PhraseFix(fix, out phraseLead)) != null) return AsPhrase(phraseLead, w, phrase);

            return fix;

        }

    

        // What "words... current" should be, or null.

        string PhraseFix(string current, out string phraseLead)

        {

            phraseLead = null;

            if (previous == null) return null;

    

            // 1. Try 4-word phrase first

            if (thirdPrevious != null && secondPrevious != null)

            {

                string four = thirdPrevious + " " + secondPrevious + " " + previous + " " + current;

                if (!isIgnored(four))

                {

                    string fix4 = ownFix(four);

                    if (fix4 != null) fix4 = ExpandSnippet(fix4);

                    if (fix4 == null && FixPhrases && !otherLanguage && !isIgnored(current))

                        PhraseFixes.TryGetValue(four, out fix4);

                    if (fix4 != null)

                    {

                        phraseLead = thirdPrevious + " " + secondPrevious + " " + previous + " ";

                        return fix4;

                    }

                }

            }

    

            // 2. Try 3-word phrase

            if (secondPrevious != null)

            {

                string three = secondPrevious + " " + previous + " " + current;

                if (!isIgnored(three))

                {

                    string fix3 = ownFix(three);

                    if (fix3 != null) fix3 = ExpandSnippet(fix3);

                    if (fix3 == null && FixPhrases && !otherLanguage && !isIgnored(current))

                        PhraseFixes.TryGetValue(three, out fix3);

                    if (fix3 != null)

                    {

                        phraseLead = secondPrevious + " " + previous + " ";

                        return fix3;

                    }

                }

            }

    

            // 3. Try 2-word phrase

            string two = previous + " " + current;

            if (!isIgnored(two))

            {

                string fix = ownFix(two);

                if (fix != null) fix = ExpandSnippet(fix);

                if (fix == null && FixPhrases && !otherLanguage && !isIgnored(current))

                    PhraseFixes.TryGetValue(two, out fix);

                if (fix != null)

                {

                    phraseLead = previous + " ";

                    return fix;

                }

            }

    

            return null;

        }

    

        string AsPhrase(string phraseLead, string w, string phrase)

        {

            if (char.IsUpper(phraseLead[0]) && char.IsLower(phrase[0])) phrase = Capital(phrase);

            // A phrase listed as its own fix ("and fro") is only there to keep the

            // single-word fixes away from it.

            if (phrase == phraseLead + w) return null;

            lead = phraseLead;

            others = null;

            return phrase;

        }

    

        static string ExpandSnippet(string fix)

        {

            if (string.IsNullOrEmpty(fix)) return fix;

            if (fix.IndexOf('{') < 0 && fix.IndexOf(':') < 0) return fix;

            DateTime now = DateTime.Now;

            string res = fix;

            if (res.IndexOf("{date}", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, "{date}", now.ToString("yyyy-MM-dd"));

            if (res.IndexOf(":date:", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, ":date:", now.ToString("yyyy-MM-dd"));

            if (res.IndexOf("{time}", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, "{time}", now.ToString("HH:mm"));

            if (res.IndexOf(":time:", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, ":time:", now.ToString("HH:mm"));

            if (res.IndexOf("{datetime}", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, "{datetime}", now.ToString("yyyy-MM-dd HH:mm"));

            if (res.IndexOf(":datetime:", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, ":datetime:", now.ToString("yyyy-MM-dd HH:mm"));

            if (res.IndexOf("{clip}", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, "{clip}", Native.GetClipboardText());

            if (res.IndexOf(":clip:", StringComparison.OrdinalIgnoreCase) >= 0)

                res = ReplaceIgnoreCase(res, ":clip:", Native.GetClipboardText());

            return res;

        }

    

        static string ReplaceIgnoreCase(string text, string search, string replacement)

        {

            int index = text.IndexOf(search, StringComparison.OrdinalIgnoreCase);

            while (index >= 0)

            {

                text = text.Substring(0, index) + replacement + text.Substring(index + search.Length);

                index = text.IndexOf(search, index + replacement.Length, StringComparison.OrdinalIgnoreCase);

            }

            return text;

        }

    

        string DecideWord(string w, string terminator)

        {

            // The user's own fixes and shortcuts apply everywhere, as written.

            string fix = isIgnored(w) ? null : ownFix(w);

            if (fix != null)

            {

                fix = ExpandSnippet(fix);

                if (string.IsNullOrEmpty(fix)) return null;

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

                Suggestion s;
                if (suggestWithContext != null)
                {
                    s = suggestWithContext(w, secondPrevious == null ? null : secondPrevious.ToLowerInvariant(),
                        previous == null ? null : previous.ToLowerInvariant(),
                        capitalised || w.Length <= Ranker.ShortWord);
                }
                else
                {
                    s = suggest(w, previous == null ? null : previous.ToLowerInvariant(),
                        capitalised || w.Length <= Ranker.ShortWord);
                }

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

}

