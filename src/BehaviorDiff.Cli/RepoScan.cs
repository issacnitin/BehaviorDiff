using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BehaviorDiff.Cli
{
    internal sealed class TestProject
    {
        internal string Path { get; init; } = string.Empty;

        internal string Framework { get; init; } = string.Empty;
    }

    internal sealed class RepoScanResult
    {
        internal List<TestProject> XunitProjects { get; } = new();

        internal List<TestProject> OtherFrameworks { get; } = new();

        internal List<string> DebugTypeOverrides { get; } = new();

        /// <summary>Top-level namespace segments the tracer will instrument, derived from project names.</summary>
        internal SortedSet<string> NamespacePrefixes { get; } = new(StringComparer.Ordinal);
    }

    internal static class RepoScan
    {
        private static readonly Regex PackageReference =
            new(@"PackageReference\s+Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DebugType =
            new(@"<DebugType>\s*([^<\s]+)\s*</DebugType>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Excluded =
            new(@"<BehaviorDiffExclude>\s*true\s*</BehaviorDiffExclude>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Identifies which projects the tracer must reach and whether anything would defeat Step 0.
        /// </summary>
        /// <remarks>
        /// Framework detection is by package reference rather than assumption: a repo on NUnit or MSTest
        /// would otherwise build, run, and produce an empty trace that the engine cannot distinguish from
        /// a clean result.
        /// </remarks>
        internal static RepoScanResult Scan(string root)
        {
            var result = new RepoScanResult();

            foreach (string project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                if (project.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || project.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                string text = File.ReadAllText(project);
                var packages = PackageReference.Matches(text)
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                bool isTestHost = packages.Contains("Microsoft.NET.Test.Sdk");
                bool isXunit = packages.Contains("xunit") || packages.Contains("xunit.core") || packages.Contains("xunit.v3");
                bool isNUnit = packages.Contains("NUnit");
                bool isMsTest = packages.Contains("MSTest.TestFramework") || packages.Contains("MSTest");
                bool isExcluded = Excluded.IsMatch(text);

                // Microsoft.NET.Test.Sdk is what makes a project a test project. A library that merely
                // references xunit.core - a test helper or an extension to the framework itself - would
                // otherwise get the tracer injected into it, which patches and traces a non-test assembly.
                if (isTestHost && isExcluded)
                {
                    result.OtherFrameworks.Add(new TestProject { Path = project, Framework = "explicitly excluded" });
                }
                else if (isTestHost && isXunit)
                {
                    result.XunitProjects.Add(new TestProject { Path = project, Framework = packages.Contains("xunit.v3") ? "xunit.v3" : "xunit" });
                }
                else if (isTestHost && isNUnit)
                {
                    result.OtherFrameworks.Add(new TestProject { Path = project, Framework = "NUnit" });
                }
                else if (isTestHost && isMsTest)
                {
                    result.OtherFrameworks.Add(new TestProject { Path = project, Framework = "MSTest" });
                }
                else if (isTestHost)
                {
                    result.OtherFrameworks.Add(new TestProject { Path = project, Framework = "unknown (has Microsoft.NET.Test.Sdk but no recognised framework)" });
                }

                AddDebugTypeOverride(result, project, text);

                // Scope is derived from the repo's own project names rather than asked for. The tracer
                // instruments by namespace prefix, and an empty scope silently produces an empty trace.
                string name = Path.GetFileNameWithoutExtension(project);
                if (!name.StartsWith("BehaviorDiff.", StringComparison.Ordinal) && name != "BehaviorDiff")
                {
                    int dot = name.IndexOf('.');
                    result.NamespacePrefixes.Add(dot < 0 ? name : name.Substring(0, dot));
                }
            }

            foreach (string props in Directory.EnumerateFiles(root, "Directory.Build.props", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "Directory.Build.targets", SearchOption.AllDirectories)))
            {
                AddDebugTypeOverride(result, props, File.ReadAllText(props));
            }

            return result;
        }

        private static void AddDebugTypeOverride(RepoScanResult result, string file, string text)
        {
            foreach (Match match in DebugType.Matches(text))
            {
                string value = match.Groups[1].Value;
                if (!string.Equals(value, "portable", StringComparison.OrdinalIgnoreCase)
                    && !value.StartsWith("$(", StringComparison.Ordinal))
                {
                    result.DebugTypeOverrides.Add(file + " -> <DebugType>" + value + "</DebugType>");
                }
            }
        }
    }

    /// <summary>
    /// Everything the tracer needs, assembled outside the worktree and injected through MSBuild's
    /// CustomAfterMicrosoftCommonTargets hook.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chosen over a Directory.Build.props dropped into the worktree because that file is repo-owned:
    /// MSBuild imports only the nearest one, so writing ours would silently replace the repo's own and
    /// change how its projects build. A vstest data collector was the other candidate; it can observe a
    /// run but cannot add the assembly-level attribute the tracer needs to stamp events with a TestId,
    /// and it would have to be configured identically through three separate runsettings paths.
    /// </para>
    /// <para>
    /// CustomAfterMicrosoftCommonTargets is passed on the command line as a global property, so it is
    /// byte-identical across all three runs by construction and leaves no file inside the worktree. An
    /// asymmetry between runs would surface as manifest gaps rather than findings, which is the failure
    /// this design is avoiding.
    /// </para>
    /// </remarks>
    internal static class InjectionKit
    {
        private static readonly string[] RuntimeFiles =
        {
            "MonoMod.Core.dll",
            "MonoMod.Utils.dll",
            "MonoMod.Backports.dll",
            "MonoMod.ILHelpers.dll",
            "System.Reflection.Metadata.dll",
            "System.Collections.Immutable.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "Mono.Cecil.dll",
        };

        private static readonly string[] ReferenceFiles =
        {
            "BehaviorDiff.Contracts.dll",
            "BehaviorDiff.Tracer.dll",
        };

        internal static string Build(string workDirectory)
        {
            string kit = Path.Combine(workDirectory, "kit");
            string runtime = Path.Combine(kit, "runtime");
            Directory.CreateDirectory(runtime);

            string source = AppContext.BaseDirectory;
            var missing = new List<string>();

            foreach (string file in ReferenceFiles)
            {
                string from = Path.Combine(source, file);
                if (!File.Exists(from))
                {
                    missing.Add(file);
                    continue;
                }

                File.Copy(from, Path.Combine(kit, file), overwrite: true);
            }

            if (missing.Count > 0)
            {
                throw new CliException("Tracer assemblies missing from " + source + ": " + string.Join(", ", missing));
            }

            foreach (string file in RuntimeFiles)
            {
                string from = Path.Combine(source, file);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(runtime, file), overwrite: true);
                }
            }

            File.WriteAllText(Path.Combine(kit, "BehaviorDiffBootstrap.cs"), Bootstrap, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(kit, "BehaviorDiff.Inject.targets"), Targets, new UTF8Encoding(false));

            return kit;
        }

        // The assembly-level attribute is what stamps events with a TestId; the module initializer only
        // moves patching earlier, and is omitted on targets that predate ModuleInitializerAttribute.
        private const string Bootstrap = """
            [assembly: BehaviorDiff.Tracer.TraceTest]

            #if NET5_0_OR_GREATER
            namespace BehaviorDiff.Injected
            {
                internal static class BehaviorDiffBootstrap
                {
                    [System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Initialize()
                    {
                        BehaviorDiff.Tracer.TraceSession.InitializeFromEnvironment();
                    }
                }
            }
            #endif
            """;

        private const string Targets = """
            <Project>
              <PropertyGroup>
                <BehaviorDiffIsTarget Condition="'$(BehaviorDiffTestProjects)' != '' And $(BehaviorDiffTestProjects.ToLowerInvariant().Contains($(MSBuildProjectFullPath.ToLowerInvariant())))">true</BehaviorDiffIsTarget>
                <!-- Only the one TFM being traced. A multi-targeted project still builds its other TFMs
                     untouched, and the adapter is compiled for this TFM only. -->
                <BehaviorDiffInject Condition="'$(BehaviorDiffIsTarget)' == 'true' And '$(TargetFramework)' == '$(BehaviorDiffTraceTfm)'">true</BehaviorDiffInject>
                 <!-- Strong-named .NET 5+ test assemblies can load the unsigned injection kit, but the
                     compiler reports CS8002 and repositories with warnings-as-errors reject the build. -->
                 <NoWarn Condition="'$(BehaviorDiffInject)' == 'true'">$(NoWarn);CS8002</NoWarn>
              </PropertyGroup>

              <ItemGroup Condition="'$(BehaviorDiffInject)' == 'true'">
                <Compile Include="$(BehaviorDiffKitDir)BehaviorDiffBootstrap.cs" Link="BehaviorDiffBootstrap.cs" />

                <Reference Include="BehaviorDiff.Contracts">
                  <HintPath>$(BehaviorDiffKitDir)BehaviorDiff.Contracts.dll</HintPath>
                  <Private>true</Private>
                </Reference>
                <Reference Include="BehaviorDiff.Tracer">
                  <HintPath>$(BehaviorDiffKitDir)BehaviorDiff.Tracer.dll</HintPath>
                  <Private>true</Private>
                </Reference>
                <Reference Include="BehaviorDiffAdapter">
                  <HintPath>$(BehaviorDiffAdapterPath)</HintPath>
                  <Private>true</Private>
                </Reference>

                <None Include="$(BehaviorDiffKitDir)runtime\*.dll">
                  <Link>%(Filename)%(Extension)</Link>
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </None>
              </ItemGroup>
            </Project>
            """;
    }
}
