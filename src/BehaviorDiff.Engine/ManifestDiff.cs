using System;
using System.Collections.Generic;
using System.Linq;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Engine
{
    internal sealed class ToolingGap
    {
        internal string Scope { get; init; } = string.Empty;

        internal string Assembly { get; init; } = string.Empty;

        internal string? MethodFullName { get; init; }

        internal string BaseState { get; init; } = string.Empty;

        internal string PrState { get; init; } = string.Empty;

        internal string Reason { get; init; } = string.Empty;
    }

    /// <summary>
    /// Differences in what the tracer could observe, as opposed to differences in what the code did.
    /// </summary>
    /// <remarks>
    /// A member patched in one run and skipped in the other produces events on one side only, which is
    /// indistinguishable from a call that was added or removed. These are computed before any event
    /// comparison and subtracted from the divergence list, because reporting one as a behavior change is
    /// a false positive that looks exactly like a true one.
    /// </remarks>
    internal static class ManifestDiff
    {
        internal static IReadOnlyList<ToolingGap> Compute(RunData baseRun, RunData prRun)
        {
            var gaps = new List<ToolingGap>();

            var methods = new HashSet<string>(baseRun.Members.Keys, StringComparer.Ordinal);
            methods.UnionWith(prRun.Members.Keys);

            foreach (string method in methods.OrderBy(m => m, StringComparer.Ordinal))
            {
                baseRun.Members.TryGetValue(method, out ManifestEntry? baseEntry);
                prRun.Members.TryGetValue(method, out ManifestEntry? prEntry);

                string baseState = baseEntry is null ? "absent" : baseEntry.Status.ToString();
                string prState = prEntry is null ? "absent" : prEntry.Status.ToString();

                if (string.Equals(baseState, prState, StringComparison.Ordinal))
                {
                    continue;
                }

                // A member added inside a repository-excluded namespace is intentionally unobservable on
                // both sides: it did not exist in base, and policy guarantees it cannot emit PR events.
                // This commonly occurs when a refactor introduces a compiler-generated closure. It is not
                // a loss of coverage; every other absent/skipped transition remains a tooling gap.
                ManifestEntry? present = baseEntry ?? prEntry;
                if ((baseEntry is null || prEntry is null)
                    && present?.Status == PatchStatus.Skipped
                    && present.SkipReason == "ExcludedNamespace")
                {
                    continue;
                }

                gaps.Add(new ToolingGap
                {
                    Scope = "member",
                    Assembly = baseEntry?.Assembly ?? prEntry?.Assembly ?? string.Empty,
                    MethodFullName = method,
                    BaseState = baseState,
                    PrState = prState,
                    Reason = "observability differs between runs: " + baseState + " -> " + prState,
                });
            }

            var assemblyNames = new HashSet<string>(baseRun.Assemblies.Keys, StringComparer.Ordinal);
            assemblyNames.UnionWith(prRun.Assemblies.Keys);

            foreach (string name in assemblyNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                baseRun.Assemblies.TryGetValue(name, out AssemblyManifestEntry? baseEntry);
                prRun.Assemblies.TryGetValue(name, out AssemblyManifestEntry? prEntry);

                // Only assemblies that actually produced events matter; an uninstrumented assembly present
                // in one run's manifest and not the other's says nothing about coverage of traced code.
                if (baseEntry?.Instrumented != true && prEntry?.Instrumented != true)
                {
                    continue;
                }

                AddFlagGap(gaps, name, "sourceUnavailable", baseEntry?.SourceUnavailable, prEntry?.SourceUnavailable);
                AddFlagGap(gaps, name, "sourcePartial", baseEntry?.SourcePartial, prEntry?.SourcePartial);
            }

            return gaps;
        }

        private static void AddFlagGap(List<ToolingGap> gaps, string assembly, string flag, bool? baseValue, bool? prValue)
        {
            if (baseValue == prValue)
            {
                return;
            }

            gaps.Add(new ToolingGap
            {
                Scope = "assembly:" + flag,
                Assembly = assembly,
                BaseState = baseValue?.ToString() ?? "absent",
                PrState = prValue?.ToString() ?? "absent",
                Reason = "tracer coverage flag '" + flag + "' differs between runs",
            });
        }

        /// <summary>Members whose divergences must be suppressed, from the member-scoped gaps.</summary>
        internal static HashSet<string> AffectedMethods(IReadOnlyList<ToolingGap> gaps)
        {
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (ToolingGap gap in gaps)
            {
                if (gap.MethodFullName != null)
                {
                    methods.Add(gap.MethodFullName);
                }
            }

            return methods;
        }
    }
}
