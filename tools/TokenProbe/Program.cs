using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : FindSampleApp();
        Console.WriteLine("target: " + path);

        int failures = 0;

        Assembly reflected = Assembly.LoadFrom(path);

        // Tokens are scoped to a module. Every C# assembly is single-module, which is precisely what makes
        // this assumption invisible when it breaks.
        Module[] reflectedModules = reflected.GetModules();
        Console.WriteLine("reflection modules : " + reflectedModules.Length);
        if (reflectedModules.Length != 1)
        {
            Console.WriteLine("  FAIL multi-module assembly: tokens are not unique across modules");
            failures++;
        }

        using ModuleDefinition cecilModule = ModuleDefinition.ReadModule(path);
        Console.WriteLine("cecil modules      : " + cecilModule.Assembly.Modules.Count);
        if (cecilModule.Assembly.Modules.Count != 1)
        {
            Console.WriteLine("  FAIL multi-module assembly on the Cecil side");
            failures++;
        }

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var reflectedByToken = new Dictionary<int, List<string>>();
        foreach (Type type in GetTypes(reflected))
        {
            foreach (MethodBase method in type.GetMethods(Flags).Cast<MethodBase>()
                .Concat(type.GetConstructors(Flags)))
            {
                if (!reflectedByToken.TryGetValue(method.MetadataToken, out List<string>? names))
                {
                    names = new List<string>();
                    reflectedByToken[method.MetadataToken] = names;
                }

                names.Add((type.FullName + "::" + method.Name).Replace('+', '/'));
            }
        }

        // A constructed generic method reports its definition's token, so instances would collide here.
        var collisions = reflectedByToken.Where(p => p.Value.Count > 1).ToList();
        Console.WriteLine("reflection methods : " + reflectedByToken.Values.Sum(v => v.Count));
        Console.WriteLine("distinct tokens    : " + reflectedByToken.Count);
        Console.WriteLine("token collisions   : " + collisions.Count);
        if (collisions.Count > 0)
        {
            failures++;
            foreach (KeyValuePair<int, List<string>> collision in collisions.Take(5))
            {
                Console.WriteLine("  FAIL 0x" + collision.Key.ToString("x8") + " -> " + string.Join(" | ", collision.Value));
            }
        }

        var cecilTokens = new Dictionary<int, string>();
        foreach (TypeDefinition type in cecilModule.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                cecilTokens[method.MetadataToken.ToInt32()] = type.FullName + "::" + method.Name;
            }
        }

        Console.WriteLine("cecil methods      : " + cecilTokens.Count);

        var onlyReflection = reflectedByToken.Keys.Except(cecilTokens.Keys).ToList();

        // Assembly.GetTypes() never returns the <Module> pseudo-type, so its initializer is invisible to
        // reflection. Neither backend can instrument it, and it is where the weaver emits registration.
        var onlyCecil = cecilTokens.Keys.Except(reflectedByToken.Keys)
            .Where(t => !cecilTokens[t].StartsWith("<Module>::", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine("only in reflection : " + onlyReflection.Count);
        Console.WriteLine("only in cecil      : " + onlyCecil.Count + " (excluding <Module>)");

        foreach (int token in onlyReflection.Take(5))
        {
            Console.WriteLine("  R-only 0x" + token.ToString("x8") + " " + string.Join(",", reflectedByToken[token]));
        }

        foreach (int token in onlyCecil.Take(5))
        {
            Console.WriteLine("  C-only 0x" + token.ToString("x8") + " " + cecilTokens[token]);
        }

        if (onlyReflection.Count > 0 || onlyCecil.Count > 0)
        {
            failures++;
        }

        // Names must agree too: matching token counts with mismatched targets would be worse than a gap.
        int nameMismatches = 0;
        foreach (KeyValuePair<int, List<string>> entry in reflectedByToken)
        {
            if (cecilTokens.TryGetValue(entry.Key, out string? cecilName) && cecilName != entry.Value[0])
            {
                if (nameMismatches < 5)
                {
                    Console.WriteLine("  NAME 0x" + entry.Key.ToString("x8") + " reflection=" + entry.Value[0] + " cecil=" + cecilName);
                }

                nameMismatches++;
            }
        }

        Console.WriteLine("name mismatches    : " + nameMismatches);
        if (nameMismatches > 0)
        {
            failures++;
        }

        Console.WriteLine(failures == 0
            ? "TOKEN MAPPING IS 1:1 AND EXACT - scope selection can be a lookup"
            : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.WriteLine("  NOTE partial type load: " + ex.Types.Count(t => t != null) + " of " + ex.Types.Length);
            return ex.Types.Where(t => t != null)!;
        }
    }

    private static string FindSampleApp()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string[] found = Directory.GetFiles(root, "SampleApp.dll", SearchOption.AllDirectories);
        string? release = found.FirstOrDefault(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase));
        return release ?? found.FirstOrDefault() ?? throw new FileNotFoundException("SampleApp.dll not found under " + root);
    }
}
