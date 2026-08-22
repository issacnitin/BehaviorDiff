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
        int genericTemplates = manifest.Members.Count(member =>
            member.Status == PatchStatus.Skipped
            && member.SkipReason == NeutralSkipReason.Unobservable
            && member.Detail == "Go: GenericTemplate");
        string[] concreteGenericFragments =
        [
            ".Identity[int](", ".Identity[string](", ".PairValues[int,string](",
            ".Box[int].Get(", ".Box[string].Get(",
        ];
        int concreteGenerics = concreteGenericFragments.Count(fragment =>
            manifest.Members.Count(member =>
                member.Status == PatchStatus.Patched
                && member.MethodFullName?.Contains(fragment, StringComparison.Ordinal) == true) == 1);
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
            || manifest.Members.Count != 45
            || patched != 38
            || skipped != 7
            || genericTemplates != 3
            || concreteGenerics != concreteGenericFragments.Length
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
            "GO_MANIFEST_PARSE modules={0} members={1} patched={2} skipped={3} templates={4} concrete={5} unreadableFields={6} ambiguousMapEntries={7} written={8}",
            manifest.Assemblies.Count,
            manifest.Members.Count,
            patched,
            skipped,
            genericTemplates,
            concreteGenerics,
            digest.UnreadableFields,
            digest.AmbiguousMapEntries,
            writer.Written);
        return 0;
    }
}
