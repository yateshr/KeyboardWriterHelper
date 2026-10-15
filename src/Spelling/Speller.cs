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
        public string KeyboardLayout = "Auto";
    }

    // Dictionary + ranking. Create and use on one thread.
    sealed class Speller : IDisposable
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
            Ranker.SetLayout(Ranker.ParseLayout(options.KeyboardLayout));
        }

        public void Dispose()
        {
            if (Dictionary != null) Dictionary.Dispose();
        }

        // previous: the word typed just before, lower case, or null.
        // cautious: see Ranker.Best.
        public Suggestion Check(string word, string previous, bool cautious)
        {
            return Check(word, null, previous, cautious);
        }

        public Suggestion Check(string word, string secondPrevious, string previous, bool cautious)
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
            string best = Ranker.Best(word, secondPrevious, previous, windows, certain, cautious, Dictionary.IsCorrect, null, others);
            if (best != null && best != word)
            {
                result.Fix = best;
                result.Others = others.ToArray();
            }
            return result;
        }

        public string PredictNext(string previous, string prefix)
        {
            return Ranker.PredictNext(previous, prefix);
        }

        public string PredictNext(string secondPrevious, string previous, string prefix)
        {
            return Ranker.PredictNext(secondPrevious, previous, prefix);
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
    sealed class SpellService : IDisposable
    {
        sealed class Request
        {
            public string Word, SecondPrevious, Previous;
            public bool Cautious;
            public Suggestion Result;
            // Set instead of Word: switch to these spelling choices.
            public SpellOptions Options;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public volatile bool Abandoned;
            // Cautious and normal lookups of one word can differ.
            public string WordKey { get { return (Cautious ? "?" : "") + Word; } }
            public string Key { get { return Previous == null ? WordKey : WordKey + (SecondPrevious == null ? "" : " " + SecondPrevious) + " " + Previous; } }
        }

        readonly BlockingCollection<Request> queue = new BlockingCollection<Request>();
        readonly Dictionary<string, Suggestion> cache = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Counters counters;
        Exception startupError;
        volatile bool useContext;
        volatile Speller speller;

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
            speller = null;
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
                        Speller oldSpeller = speller;
                        speller = new Speller(request.Options, oldSpeller != null ? oldSpeller.Ranker : null);
                        if (oldSpeller != null) oldSpeller.Dispose();
                        Languages = speller.Dictionary.Languages;
                        speller.Check("speling", null, false);
                    }
                    catch (Exception ex) { counters.Error(ex); }
                    lock (cache) cache.Clear();
                    request.Done.Set();
                    if (request.Abandoned) request.Done.Dispose();
                    continue;
                }

                Suggestion result = null;
                try { result = speller.Check(request.Word, request.SecondPrevious, request.Previous, request.Cautious); }
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
                if (request.Abandoned) request.Done.Dispose();
            }
        }

        // Switches to other spelling choices; returns once they are in effect.
        public void Configure(SpellOptions options)
        {
            useContext = options.UseContext;
            Request request = new Request { Options = options };
            queue.Add(request);
            request.Done.Wait(5000);
            request.Done.Dispose();
        }

        // Call from a non-STA thread. If the answer is not ready within budgetMs the
        // word is left alone this time (tooSlow, null) and the answer is cached for the next.
        public Suggestion Suggest(string word, string previous, bool cautious, int budgetMs, out bool tooSlow)
        {
            return Suggest(word, null, previous, cautious, budgetMs, out tooSlow);
        }

        public Suggestion Suggest(string word, string secondPrevious, string previous, bool cautious, int budgetMs, out bool tooSlow)
        {
            tooSlow = false;
            string wordKey = (cautious ? "?" : "") + word;
            string key = (!useContext || previous == null) ? wordKey : wordKey + (secondPrevious == null ? "" : " " + secondPrevious) + " " + previous;
            lock (cache)
            {
                Suggestion cached;
                if (cache.TryGetValue(wordKey, out cached) && (cached.Known || !useContext || previous == null)) return cached;
                if (cache.TryGetValue(key, out cached)) return cached;
            }

            Request request = new Request { Word = word, SecondPrevious = useContext ? secondPrevious : null, Previous = useContext ? previous : null, Cautious = cautious };
            queue.Add(request);
            if (request.Done.Wait(budgetMs))
            {
                Suggestion res = request.Result;
                request.Done.Dispose();
                return res;
            }
            request.Abandoned = true;
            tooSlow = true;
            return null;
        }

        public string PredictNext(string previous, string prefix)
        {
            Speller s = speller;
            return s != null && s.Ranker != null ? s.Ranker.PredictNext(previous, prefix) : null;
        }

        public string PredictNext(string secondPrevious, string previous, string prefix)
        {
            Speller s = speller;
            return s != null && s.Ranker != null ? s.Ranker.PredictNext(secondPrevious, previous, prefix) : null;
        }

        public void UpdateLayout(IntPtr layoutHandle)
        {
            if (speller != null && speller.Ranker != null)
                speller.Ranker.UpdateActiveLayout(layoutHandle);
        }

        public void Dispose()
        {
            try { queue.CompleteAdding(); }
            catch { }
            if (speller != null) speller.Dispose();
        }
    }
}
