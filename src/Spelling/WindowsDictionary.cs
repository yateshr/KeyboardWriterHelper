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

    sealed class WindowsDictionary : IDisposable
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
            try
            {
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
            finally
            {
                if (factory != null)
                {
                    try { Marshal.ReleaseComObject(factory); }
                    catch { }
                }
            }
        }

        public void Dispose()
        {
            foreach (ISpellChecker checker in checkers)
            {
                if (checker != null)
                {
                    try { Marshal.ReleaseComObject(checker); }
                    catch { }
                }
            }
            checkers.Clear();
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
}
