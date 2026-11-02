using System;

namespace KeyboardWriterHelper
{
    // Applies learned preferences underneath the user's explicit lists. This
    // layer observes correction feedback, never ordinary typing or sentences.
    sealed class Personalization
    {
        readonly PersonalLearning learning;
        readonly Func<string, bool> manualWord;
        readonly Func<string, string> manualFix;
        readonly Func<bool> fixPhrases;
        bool feedbackEligible;
        long feedbackGeneration;
        string feedbackTyped;

        public event Action<string> Learned;

        public Personalization(PersonalLearning learning, Func<string, bool> manualWord,
            Func<string, string> manualFix, Func<bool> fixPhrases)
        {
            this.learning = learning;
            this.manualWord = manualWord;
            this.manualFix = manualFix;
            this.fixPhrases = fixPhrases;
        }

        public bool IsIgnored(string text)
        {
            return manualWord(text) || (learning != null && manualFix(text) == null && learning.IsProtected(text));
        }

        public string OwnFix(string text)
        {
            string fix = manualFix(text);
            if (fix != null) return fix;
            if (learning == null || (!fixPhrases() && text.IndexOf(' ') >= 0)) return null;
            return learning.GetFix(text);
        }

        public void Attach(Engine engine)
        {
            engine.Corrected += OnCorrected;
            engine.Rejected += OnRejected;
            engine.ChoiceCommitted += OnChoiceCommitted;
        }

        void OnCorrected(string typed, string fix)
        {
            feedbackEligible = learning != null && learning.Enabled && !manualWord(typed) && manualFix(typed) == null &&
                !string.Equals(typed, fix, StringComparison.OrdinalIgnoreCase);
            feedbackTyped = typed;
            feedbackGeneration = learning == null ? 0 : learning.FeedbackGeneration;
        }

        bool CanRecord(string typed)
        {
            bool eligible = feedbackEligible && learning != null && learning.Enabled &&
                learning.FeedbackGeneration == feedbackGeneration &&
                string.Equals(typed, feedbackTyped, StringComparison.OrdinalIgnoreCase) &&
                !manualWord(typed) && manualFix(typed) == null;
            feedbackEligible = false;
            return eligible;
        }

        // Backspace right after a fix and then a Space: keep the word as typed.
        // A Backspace followed by punctuation or more typing is no rejection.
        void OnRejected(string typed, string fix)
        {
            if (CanRecord(typed) && learning.RecordRejection(typed) && Learned != null) Learned(typed);
        }

        void OnChoiceCommitted(string typed, string initialFix, string selected)
        {
            if (!CanRecord(typed)) return;
            bool activated = string.Equals(typed, selected, StringComparison.OrdinalIgnoreCase)
                ? learning.RecordRejection(typed) : learning.RecordChoice(typed, selected);
            if (activated && Learned != null) Learned(typed);
        }
    }
}
