using System;
using System.Linq;
using BehaviorDiff.Contracts;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: manifest-parse-proof <manifest.ndjson>");
            return 2;
        }

        CoverageManifest manifest;
        try
        {
            manifest = ManifestFile.Read(args[0]);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }

        int patched = manifest.Assemblies.Sum(module => module.PatchedMembers);
        int skipped = manifest.Assemblies.Sum(module => module.SkippedMembers);
        bool reconciled = manifest.Assemblies.All(module =>
            module.Discovery == AssemblyDiscovery.GoAstRewrite
            && module.PatchFailedMembers == 0
            && module.DiscoveredMembers == module.PatchedMembers + module.SkippedMembers
            && module.DiscoveredMembers == manifest.Members.Count(member => member.Assembly == module.Assembly));
        DigestStatsEntry? digest = manifest.DigestStats;
        WriterStatsEntry? writer = manifest.WriterStats;

        if (manifest.Metadata?.Schema != TraceFormat.Schema
            || manifest.Metadata.Language != TraceFormat.GoLanguage
            || manifest.Assemblies.Count != 2
            || manifest.Members.Count != 34
            || patched != 30
            || skipped != 4
            || !reconciled
            || digest is null
            || digest.UnreadableFields <= 0
            || writer is null
            || writer.Enqueued != writer.Written
            || writer.Dropped != 0)
        {
            Console.Error.WriteLine("Go manifest did not satisfy the shared contract.");
            return 1;
        }

        Console.WriteLine(
            "GO_MANIFEST_PARSE modules={0} members={1} patched={2} skipped={3} unreadableFields={4} ambiguousMapEntries={5} written={6}",
            manifest.Assemblies.Count,
            manifest.Members.Count,
            patched,
            skipped,
            digest.UnreadableFields,
            digest.AmbiguousMapEntries,
            writer.Written);
        return 0;
    }
}
