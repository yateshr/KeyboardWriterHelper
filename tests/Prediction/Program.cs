using System;
using System.Diagnostics;
using System.Reflection;

namespace KeyboardWriterHelper
{
    static class PredictionTests
    {
        static int Main(string[] args)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Ranker ranker = new Ranker();
            Console.WriteLine("Model load: {0} ms", timer.ElapsedMilliseconds);
            string[][] cases = {
                new[] { "", "please", "rea", "read" },
                new[] { "", "please", "vis", "visit" },
                new[] { "", "send", "ema", "email" },
                new[] { "", "your", "brow", "browser" },
                new[] { "", "your", "acc", "account" },
                new[] { "look", "forward", "", "to" },
                new[] { "as", "well", "", "as" },
                new[] { "thank", "you", "v", "very" },
                new[] { "", "thank", "", "you" },
                new[] { "", "united", "", "states" },
                new[] { "", "as", "w", "well" },
                new[] { "in", "order", "", "to" },
                new[] { "let", "me", "", "know" },
                new[] { "thank", "you", "s", "so" },
                new[] { "thank", "you", "f", "for" },
                new[] { "", "git", "c", "commit" },
                new[] { "", "docker", "r", "run" },
                new[] { "", "", "nam", (string)null },
                new[] { "", "please", "zzzz", (string)null }
            };
            int failed = 0;
            foreach (string[] c in cases)
            {
                string actual = ranker.PredictNext(c[0], c[1], c[2]);
                if (actual != c[3])
                {
                    failed++;
                    Console.WriteLine("FAIL {0} {1} [{2}]: expected {3}, got {4}", c[0], c[1], c[2], c[3], actual);
                }
            }
            // Warm JIT and measure repeated prefixes as users edit/retype words.
            for (int i = 0; i < 1000; i++) ranker.PredictNext("", "please", "rea");
            timer.Restart();
            for (int i = 0; i < 100000; i++)
            {
                string[] c = cases[i % cases.Length];
                ranker.PredictNext(c[0], c[1], c[2]);
            }
            Console.WriteLine("100,000 predictions: {0:F2} ms; {1}/{2} cases passed", timer.Elapsed.TotalMilliseconds, cases.Length - failed, cases.Length);
            Func<string, string, string, string> uncached = (Func<string, string, string, string>)Delegate.CreateDelegate(
                typeof(Func<string, string, string, string>), ranker,
                typeof(Ranker).GetMethod("PredictNextUncached", BindingFlags.Instance | BindingFlags.NonPublic));
            timer.Restart();
            for (int i = 0; i < 100000; i++) uncached("", "please", "rea");
            double cold = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            for (int i = 0; i < 100000; i++) ranker.PredictNext("", "please", "rea");
            Console.WriteLine("Repeated prefix, 100,000 calls: uncached {0:F2} ms, cached {1:F2} ms", cold, timer.Elapsed.TotalMilliseconds);
            // Exercise cache clearing, cached misses and case normalization.
            for (int i = 0; i < 100; i++) ranker.PredictNext("", "please", "zz" + i);
            if (ranker.PredictNext("", "PLEASE", "REA") != "read") failed++;
            if (ranker.PredictNext("", "send", "ema") != "email") failed++;
            if (ranker.PredictNext("", "please", "zzzz") != null) failed++;
            Console.WriteLine("Prediction failures (including cache checks): {0}", failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
