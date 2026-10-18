using System;
using System.Collections.Generic;

namespace KeyboardWriterHelper
{
    // Small, deliberately explicit subject patterns for online typing. A word
    // ending in s is not sufficient evidence of either a noun or its number.
    static class SubjectAgreement
    {
        static readonly Dictionary<string, bool> NounNumbers = BuildNouns();

        static readonly HashSet<string> SingularDeterminers = Words(
            "a an this that each every another either neither");
        static readonly HashSet<string> PluralDeterminers = Words(
            "these those both many several few fewer");
        static readonly HashSet<string> EitherDeterminers = Words(
            "the my your his her its our their no some any all");
        static readonly HashSet<string> Adjectives = Words(
            "new old recent current previous next last first second third " +
            "small large big little good bad important available missing " +
            "selected existing different same other local remote open closed " +
            "active inactive original final main primary secondary empty full");
        static readonly HashSet<string> SingularPronouns = Words(
            "everyone everybody someone somebody anyone anybody nobody " +
            "everything something anything nothing");
        // Wait for this evidence after "everyone have": the bare phrase can
        // instead introduce a valid command, such as "Everyone have fun".
        // Avoid adjective/noun combinations such as "have fried rice" or
        // "have written notes ready", which are also grammatical commands.
        static readonly HashSet<string> ClearPerfectParticiples = Words(
            "been done eaten gone arrived");

        // These are accepted only when they are the entire prefix, so "and"
        // cannot turn "the file and the folder" into a singular subject. Do
        // not add before/after: "Before the files is a folder" is inversion.
        static readonly HashSet<string> Introducers = Words(
            "and but so then because although though when while " +
            "today yesterday now currently usually normally sometimes often " +
            "finally apparently fortunately unfortunately indeed clearly also");

        // Do not infer arbitrary endings. In particular, bare work/use/need
        // can begin noun compounds or a modal ("the user work experience",
        // "the file need not be saved"), and let/put/read can be past tense.
        static readonly Dictionary<string, string> ThirdPerson = Pairs(
            "contain contains include includes require requires depend depends " +
            "exist exists remain remains belong belongs prefer prefers " +
            "seem seems appear appears become becomes occur occurs " +
            "involve involves provide provides receive receives believe believes " +
            "understand understands allow allows ensure ensures indicate indicates " +
            "represent represents know knows");
        static readonly Dictionary<string, string> BareByThirdPerson = Reverse(ThirdPerson);

        // These third-person forms are also safe in the plural direction. The
        // inverse is intentionally absent because the bare forms are ambiguous.
        static readonly Dictionary<string, string> AdditionalPluralVerbs = Pairs(
            "works work needs need wants want uses use makes make takes take " +
            "gives give comes come goes go runs run looks look thinks think");

        // context is the full rolling sentence suffix, oldest first. Returning
        // a replacement includes exactly the subject words being erased; any
        // introductory words stay on screen. At most three previous words are
        // replaced, keeping the Engine's phrase undo and candidate list intact.
        public static string Correct(string[] context, string current, out string phraseLead)
        {
            phraseLead = null;
            if (context == null || context.Length == 0 || string.IsNullOrEmpty(current)) return null;

            // The Engine retains six words. Limiting recognition to five means
            // a truncated context is never mistaken for a sentence beginning.
            if (context.Length > 5) return null;
            for (int i = 0; i < context.Length; i++)
                if (string.IsNullOrEmpty(context[i])) return null;
            if (!IsLowerWord(current)) return null;

            string deferred = DeferredIndefinite(context, current, out phraseLead);
            if (deferred != null) return deferred;

            int subjectStart;
            bool plural;
            if (!FindSubject(context, out subjectStart, out plural)) return null;
            if (!HasStatementPrefix(context, subjectStart)) return null;

            // Internal capitals are commonly names or titles. A capitalized
            // first word at the actual sentence beginning is ordinary prose.
            for (int i = subjectStart; i < context.Length; i++)
                if (!IsPlainWord(context[i], i == 0)) return null;

            // "Everybody remain seated", "Someone have a go", and "Nobody
            // do that" are valid imperatives. A complete reporting prefix
            // supplies enough evidence for the indicative form instead.
            if (IsIndefinite(context, subjectStart) && subjectStart != 2 &&
                (current == "have" || current == "do" || current == "don't" ||
                 ThirdPerson.ContainsKey(current))) return null;

            string replacement = CorrectVerb(current, plural);
            if (replacement == null) return null;

            string[] subject = new string[context.Length - subjectStart];
            Array.Copy(context, subjectStart, subject, 0, subject.Length);
            phraseLead = string.Join(" ", subject) + " ";
            return phraseLead + replacement;
        }

        static string DeferredIndefinite(string[] context, string current, out string phraseLead)
        {
            phraseLead = null;
            if (context.Length < 2 || context[context.Length - 1] != "have" ||
                !ClearPerfectParticiples.Contains(current)) return null;
            string[] subjectContext = new string[context.Length - 1];
            Array.Copy(context, subjectContext, subjectContext.Length);
            int start;
            bool plural;
            if (!FindSubject(subjectContext, out start, out plural) || plural ||
                !IsIndefinite(subjectContext, start) || start == 2 ||
                !HasStatementPrefix(subjectContext, start)) return null;
            for (int i = start; i < subjectContext.Length; i++)
                if (!IsPlainWord(subjectContext[i], i == 0)) return null;
            string[] subject = new string[subjectContext.Length - start];
            Array.Copy(subjectContext, start, subject, 0, subject.Length);
            string subjectText = string.Join(" ", subject) + " ";
            phraseLead = subjectText + "have ";
            return subjectText + "has " + current;
        }

        static bool IsIndefinite(string[] context, int start)
        {
            return SingularPronouns.Contains(context[start]) ||
                (context.Length - start == 2 && EqualsWord(context[start], "no") &&
                 EqualsWord(context[start + 1], "one"));
        }

        static bool FindSubject(string[] context, out int start, out bool plural)
        {
            start = context.Length - 1;
            plural = false;
            if (SingularPronouns.Contains(context[start])) return true;
            if (context.Length >= 2 && EqualsWord(context[context.Length - 2], "no") &&
                EqualsWord(context[context.Length - 1], "one"))
            {
                start = context.Length - 2;
                return true;
            }

            if (context.Length < 2 || !NounNumbers.TryGetValue(context[context.Length - 1], out plural))
                return false;
            start = context.Length - 2;
            if (context.Length >= 3 && Adjectives.Contains(context[start])) start--;
            string determiner = context[start];
            return EitherDeterminers.Contains(determiner) ||
                (plural ? PluralDeterminers.Contains(determiner) : SingularDeterminers.Contains(determiner));
        }

        static bool HasStatementPrefix(string[] context, int subjectStart)
        {
            if (subjectStart == 0) return true;
            if (subjectStart == 1)
                return Introducers.Contains(context[0]) && IsPlainWord(context[0], true);

            // Complete, vetted reporting prefixes. Unknown preceding words,
            // prepositions, relative clauses and subjunctive governors fail.
            if (subjectStart == 2 && IsPlainWord(context[0], true) && IsLowerWord(context[1]))
            {
                string pronoun = context[0].ToLowerInvariant();
                string reporting = context[1];
                if (pronoun == "i" || pronoun == "we" || pronoun == "you" || pronoun == "they")
                    return reporting == "think" || reporting == "know" || reporting == "said";
                if (pronoun == "he" || pronoun == "she")
                    return reporting == "thinks" || reporting == "knows" || reporting == "said";
            }
            return false;
        }

        static string CorrectVerb(string current, bool plural)
        {
            if (plural)
            {
                switch (current)
                {
                    case "is": case "am": return "are";
                    case "was": return "were";
                    case "has": return "have";
                    case "does": return "do";
                    case "isn't": return "aren't";
                    case "wasn't": return "weren't";
                    case "hasn't": return "haven't";
                    case "doesn't": return "don't";
                }
                string bare;
                if (BareByThirdPerson.TryGetValue(current, out bare) ||
                    AdditionalPluralVerbs.TryGetValue(current, out bare)) return bare;
            }
            else
            {
                switch (current)
                {
                    case "are": case "am": return "is";
                    case "have": return "has";
                    case "do": return "does";
                    case "aren't": return "isn't";
                    case "haven't": return "hasn't";
                    case "don't": return "doesn't";
                }
                // "were" remains available for the subjunctive; "be" is
                // also grammatical after demands and in other special clauses.
                // These bare tokens can introduce ordinary technical noun
                // compounds: user allow list, file include guard, and server
                // receive buffer. Their later words have not been typed yet.
                if (current == "allow" || current == "include" || current == "receive") return null;
                string third;
                if (ThirdPerson.TryGetValue(current, out third)) return third;
            }
            return null;
        }

        static bool IsPlainWord(string value, bool allowInitialCapital)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 'a' && c <= 'z') continue;
                if (i == 0 && allowInitialCapital && c >= 'A' && c <= 'Z') continue;
                return false;
            }
            return true;
        }

        static bool IsLowerWord(string value)
        {
            for (int i = 0; i < value.Length; i++)
                if ((value[i] < 'a' || value[i] > 'z') && value[i] != '\'') return false;
            return true;
        }

        static bool EqualsWord(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        static HashSet<string> Words(string values)
        {
            return new HashSet<string>(values.Split(' '), StringComparer.OrdinalIgnoreCase);
        }

        static Dictionary<string, string> Pairs(string values)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] words = values.Split(' ');
            for (int i = 0; i < words.Length; i += 2) result.Add(words[i], words[i + 1]);
            return result;
        }

        static Dictionary<string, string> Reverse(Dictionary<string, string> source)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in source) result.Add(pair.Value, pair.Key);
            return result;
        }

        static Dictionary<string, bool> BuildNouns()
        {
            Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string[] pairs = ("file files folder folders document documents message messages email emails " +
                "user users customer customers account accounts invoice invoices receipt receipts " +
                "order orders product products package packages parcel parcels report reports result results " +
                "answer answers question questions issue issues error errors problem problems solution solutions " +
                "option options setting settings window windows button buttons screen screens " +
                "keyboard keyboards computer computers laptop laptops device devices printer printers " +
                "server servers database databases application applications program programs " +
                "project projects task tasks request requests requirement requirements " +
                "employee employees manager managers student students teacher teachers " +
                "member members visitor visitors passenger passengers driver drivers " +
                "patient patients doctor doctors child children person people man men woman women " +
                "boy boys girl girls parent parents friend friends neighbor neighbors neighbour neighbours " +
                "book books page pages chapter chapters paragraph paragraphs sentence sentences " +
                "word words letter letters number numbers name names address addresses " +
                "item items object objects record records table tables column columns row rows " +
                "picture pictures image images photo photos video videos song songs " +
                "door doors room rooms building buildings house houses car cars bus buses " +
                "train trains plane planes road roads street streets city cities country countries " +
                "test tests example examples case cases rule rules feature features version versions " +
                "update updates change changes correction corrections suggestion suggestions " +
                "dictionary dictionaries language languages grammar grammars").Split(' ');
            for (int i = 0; i < pairs.Length; i += 2)
            {
                result.Add(pairs[i], false);
                result.Add(pairs[i + 1], true);
            }
            return result;
        }
    }
}
