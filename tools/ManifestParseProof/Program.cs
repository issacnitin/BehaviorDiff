using System;
using System.Collections.Generic;
using BehaviorDiff.Contracts;

internal static class Program
{
    private static int Main()
    {
        // A real line taken from the manifest the sample run just produced, plus two mutations.
        const string old1 = "{\"kind\":\"assembly\",\"assembly\":\"SampleApp\",\"discovery\":\"StartupEnumeration\",\"tracedCalls\":5}";
        const string old2 = "{\"kind\":\"assembly\",\"assembly\":\"SampleApp\",\"discovery\":\"AssemblyLoadEvent\",\"latePatched\":true,\"tracedCalls\":5}";
        const string woven = "{\"kind\":\"assembly\",\"assembly\":\"SampleApp\",\"discovery\":\"BuildTimeWeave\",\"latePatched\":false,\"tracedCalls\":5}";
        const string bogus = "{\"kind\":\"assembly\",\"assembly\":\"SampleApp\",\"discovery\":\"SomeFutureMechanism\",\"tracedCalls\":5}";
        const string contra = "{\"kind\":\"assembly\",\"assembly\":\"SampleApp\",\"discovery\":\"BuildTimeWeave\",\"latePatched\":true,\"tracedCalls\":5}";

        var cases = new List<(string Name, string Line, bool ExpectOk)>
        {
            ("pre-change StartupEnumeration", old1, true),
            ("pre-change AssemblyLoadEvent+late", old2, true),
            ("new BuildTimeWeave", woven, true),
            ("unknown discovery value", bogus, false),
            ("BuildTimeWeave + latePatched", contra, false),
        };

        int failures = 0;
        foreach ((string name, string line, bool expectOk) in cases)
        {
            bool ok = ManifestNdjson.TryParseLine(
                line,
                out ManifestEntry? _,
                out AssemblyManifestEntry? asm,
                out DigestStatsEntry? _,
                out UnruledEnumerableEntry? _,
                out WriterStatsEntry? _,
                out string? error);

            string got = ok
                ? "OK discovery=" + (asm is null ? "<null>" : asm.Discovery.ToString())
                  + " latePatched=" + (asm is null ? "?" : asm.LatePatched.ToString())
                : "REJECTED: " + error;

            bool pass = ok == expectOk;
            if (!pass)
            {
                failures++;
            }

            Console.WriteLine((pass ? "  pass  " : "  FAIL  ") + name.PadRight(32) + got);
        }

        Console.WriteLine(failures == 0 ? "ALL PARSE CASES BEHAVED AS SPECIFIED" : failures + " CASE(S) WRONG");
        return failures == 0 ? 0 : 1;
    }
}
