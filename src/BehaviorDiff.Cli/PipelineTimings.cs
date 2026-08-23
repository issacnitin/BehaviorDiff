using System;
using System.Globalization;

namespace BehaviorDiff.Cli
{
    internal sealed class PipelineTimings
    {
        internal long BuildMilliseconds { get; set; }

        internal long WeaveMilliseconds { get; set; }

        internal long InstrumentedRunMilliseconds { get; set; }

        internal long CacheRestoreMilliseconds { get; set; }

        internal long CacheStoreMilliseconds { get; set; }

        internal long DiffMilliseconds { get; set; }

        internal long FrontierMilliseconds { get; set; }

        internal long MeasuredTotalMilliseconds => BuildMilliseconds + WeaveMilliseconds
            + InstrumentedRunMilliseconds + CacheRestoreMilliseconds + CacheStoreMilliseconds
            + DiffMilliseconds + FrontierMilliseconds;

        internal void Report()
        {
            Console.WriteLine();
            Console.WriteLine("=== timing ===");
            Print("build", BuildMilliseconds);
            Print("weave", WeaveMilliseconds);
            Print("instrumented runs", InstrumentedRunMilliseconds);
            Print("cache restore", CacheRestoreMilliseconds);
            Print("cache store", CacheStoreMilliseconds);
            Print("engine diff", DiffMilliseconds);
            Print("engine frontier", FrontierMilliseconds);
            Print("measured total", MeasuredTotalMilliseconds);
        }

        private static void Print(string label, long milliseconds) => Console.WriteLine(
            "  " + label.PadRight(19) + ": " + milliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
    }
}