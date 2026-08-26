using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RealDiff.Cli
{
    /// <summary>What restore actually resolved for one test project, as opposed to what its csproj says.</summary>
    internal sealed class ResolvedTestProject
    {
        internal string Path { get; init; } = string.Empty;

        internal string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

        internal string XunitPackage { get; init; } = string.Empty;

        internal string XunitVersion { get; init; } = string.Empty;

        internal List<string> AllTfms { get; } = new();

        internal List<string> TraceableTfms { get; } = new();

        internal List<(string Tfm, string Reason)> RejectedTfms { get; } = new();

        internal bool UsesExistingTracerXunit { get; init; }

        /// <summary>
        /// The HIGHEST traceable framework. This used to be the lowest: Harmony could not emit on net9.0 or
        /// later, so the oldest target was the only one certain to be instrumentable. Cecil weaves at build
        /// time and has no such limit, so the preference inverts to the framework the project is most likely
        /// to actually ship on, which is the one whose behavior is worth diffing.
        /// </summary>
        internal string TraceTfm => TraceableTfms.Count == 0 ? string.Empty : TraceableTfms[0];

        internal string AdapterAssemblyPath { get; set; } = string.Empty;
    }

    internal static class Assets
    {
        // Package names that carry BeforeAfterTestAttribute, most specific first.
        private static readonly string[] XunitPackages = { "xunit.extensibility.core", "xunit.core", "xunit" };

        /// <summary>
        /// Reads the resolved xunit version and TFM set from project.assets.json.
        /// </summary>
        /// <remarks>
        /// The csproj is not a reliable source: Central Package Management moves the version into
        /// Directory.Packages.props, and a transitive pin can decide it without appearing in either file.
        /// The assets file is what restore actually chose.
        /// </remarks>
        internal static ResolvedTestProject Read(string projectPath)
        {
            string assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                throw new CliException(
                    "No restore output for " + Path.GetFileName(projectPath) + " at " + assetsPath
                    + ". The unmodified build should have produced it.");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            JsonElement root = document.RootElement;

            string package = string.Empty;
            string version = string.Empty;
            if (root.TryGetProperty("libraries", out JsonElement libraries))
            {
                foreach (string candidate in XunitPackages)
                {
                    foreach (JsonProperty library in libraries.EnumerateObject())
                    {
                        int slash = library.Name.IndexOf('/');
                        if (slash > 0 && string.Equals(library.Name[..slash], candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            package = candidate;
                            version = library.Name[(slash + 1)..];
                            break;
                        }
                    }

                    if (version.Length > 0)
                    {
                        break;
                    }
                }
            }

            var resolved = new ResolvedTestProject
            {
                Path = projectPath,
                XunitPackage = package,
                XunitVersion = version,
                UsesExistingTracerXunit = root.TryGetProperty("libraries", out JsonElement resolvedLibraries)
                    && resolvedLibraries.EnumerateObject().Any(library =>
                        library.Name.StartsWith("RealDiff.Tracer.Xunit/", StringComparison.OrdinalIgnoreCase)),
            };

            if (root.TryGetProperty("project", out JsonElement project)
                && project.TryGetProperty("frameworks", out JsonElement frameworks))
            {
                foreach (JsonProperty framework in frameworks.EnumerateObject())
                {
                    resolved.AllTfms.Add(framework.Name);
                    if (Version(framework.Name) is int major && major >= 5)
                    {
                        resolved.TraceableTfms.Add(framework.Name);
                    }
                    else
                    {
                        resolved.RejectedTfms.Add((framework.Name, "tracer requires net5.0 or later"));
                    }
                }
            }

            resolved.TraceableTfms.Sort((a, b) => (Version(b) ?? 0).CompareTo(Version(a) ?? 0));
            foreach (string lower in resolved.TraceableTfms.Skip(1))
            {
                resolved.RejectedTfms.Add((lower, "traceable, but a higher traceable target exists and is preferred"));
            }

            return resolved;
        }

        /// <summary>Major version of a netN.0 moniker, or null when it is not one.</summary>
        private static int? Version(string tfm)
        {
            if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rest = tfm[3..];
            int dot = rest.IndexOf('.');
            if (dot <= 0)
            {
                return null;
            }

            return int.TryParse(rest[..dot], out int major) ? major : null;
        }
    }

    internal static class AdapterBuilder
    {
        /// <summary>
        /// Compiles one adapter per test project against that project's own resolved xunit version.
        /// </summary>
        /// <remarks>
        /// A single pre-built adapter cannot work: it bakes in a compile-time reference to one xunit
        /// version, and the C# compiler rejects a reference to an assembly built against a higher version
        /// than the consumer resolves (CS1705). Compiling per project removes the constraint rather than
        /// moving it - MediatR's two test projects resolve 2.6.1 and 2.5.3 and each gets its own adapter.
        /// Generated outside the worktree so it never sees the repo's Directory.Build.props.
        /// </remarks>
        internal static void BuildAll(string workDirectory, string kitDirectory, IEnumerable<ResolvedTestProject> projects)
        {
            string root = Path.Combine(workDirectory, "adapters");
            Directory.CreateDirectory(root);

            // Nothing above this directory should influence the adapter build.
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");

            string source = ReadEmbeddedAdapterSource();

            foreach (ResolvedTestProject project in projects)
            {
                if (project.UsesExistingTracerXunit)
                {
                    continue;
                }

                string directory = Path.Combine(root, project.Name);
                Directory.CreateDirectory(directory);

                File.WriteAllText(Path.Combine(directory, "TraceTestAttribute.cs"), source, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, "adapter.csproj"), Csproj(project, kitDirectory), new UTF8Encoding(false));

                ProcessResult result = Shell.Run(
                    "dotnet",
                    new[] { "build", "adapter.csproj", "-c", "Release", "--nologo", "-v", "quiet" },
                    directory);

                if (!result.Ok)
                {
                    throw new CliException(
                        "XUNIT VERSION COMPATIBILITY: the trace adapter could not be compiled against "
                        + project.XunitPackage + " " + project.XunitVersion + " (as resolved by "
                        + project.Name + ", target " + project.TraceTfm + ")." + Environment.NewLine
                        + "    This is a RealDiff limitation, not a fault in the repository under test."
                        + Environment.NewLine + Shell.Tail(result.Output, 20));
                }

                string assembly = Path.Combine(directory, "bin", "Release", project.TraceTfm, "RealDiff.Adapter." + project.Name + ".dll");
                if (!File.Exists(assembly))
                {
                    throw new CliException(
                        "XUNIT VERSION COMPATIBILITY: adapter build reported success but produced no assembly at " + assembly);
                }

                project.AdapterAssemblyPath = assembly;
            }
        }

        private static string ReadEmbeddedAdapterSource()
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TraceTestAttribute.cs");
            if (stream is null)
            {
                throw new CliException("Adapter source is missing from the CLI assembly.");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static string Csproj(ResolvedTestProject project, string kitDirectory) =>
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{project.TraceTfm}</TargetFramework>
                <AssemblyName>RealDiff.Adapter.{project.Name}</AssemblyName>
                <RootNamespace>RealDiff.Tracer</RootNamespace>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <DebugType>portable</DebugType>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <NoWarn>$(NoWarn);CS1591</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="TraceTestAttribute.cs" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="{project.XunitPackage}" Version="{project.XunitVersion}" />
                <Reference Include="RealDiff.Tracer">
                  <HintPath>{Path.Combine(kitDirectory, "RealDiff.Tracer.dll")}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>
            </Project>
            """;
    }
}
