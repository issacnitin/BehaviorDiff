using System;
using System.Collections.Generic;
using System.Reflection;
using BehaviorDiff.Contracts;
using BehaviorDiff.Tracer;

namespace BehaviorDiff.Weaver
{
    /// <summary>One member the weaver considered, woven or declined.</summary>
    internal sealed class MemberPlan
    {
        internal int Token { get; set; }

        internal string FullName { get; set; } = string.Empty;

        internal string? FilePath { get; set; }

        internal int Line { get; set; }

        internal string SourceResolution { get; set; } = string.Empty;

        internal ReturnKind ReturnKind { get; set; }

        internal bool IsTestRoot { get; set; }

        internal string[] ParameterNames { get; set; } = new string[0];

        /// <summary>Null when the member is to be woven; otherwise the reason it is not.</summary>
        internal string? SkipReason { get; set; }

        /// <summary>Assigned only to woven members, dense and zero-based.</summary>
        internal int WeaveIndex { get; set; } = -1;
    }

    /// <summary>
    /// The reflection pass. Every descriptor field comes from the same code the Harmony backend runs, so
    /// the two backends agree on scope, identity and source location by construction rather than by review.
    /// </summary>
    internal static class Descriptors
    {
        private const BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static List<MemberPlan> Plan(Assembly assembly, TracerOptions options)
        {
            var plans = new List<MemberPlan>();
            using var locations = new SourceLocationResolver();

            foreach (Type type in assembly.GetTypes())
            {
                // Out of scope means no manifest entry at all, which is not the same as a skip: the engine
                // reads a skip as "exists and is unobservable" and an absence as "not part of this system".
                if (!MethodSelector.IsInScope(type, options))
                {
                    continue;
                }

                SkipReason typeReason = MethodSelector.EvaluateType(type, options);

                var members = new List<MethodBase>();
                members.AddRange(type.GetMethods(MemberFlags));
                members.AddRange(type.GetConstructors(MemberFlags));

                foreach (MethodBase member in members)
                {
                    SkipReason reason = typeReason != SkipReason.None
                        ? typeReason
                        : MethodSelector.Evaluate(member);

                    ParameterInfo[] parameters = member.GetParameters();
                    var plan = new MemberPlan
                    {
                        Token = member.MetadataToken,
                        FullName = MethodSelector.BuildFullName(member, parameters),
                        ReturnKind = MethodSelector.ClassifyReturn(member),
                        IsTestRoot = MethodSelector.IsTestRoot(member, options.TestAttributeNames),
                        SkipReason = reason == SkipReason.None ? null : reason.ToString(),
                    };

                    var names = new string[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        names[i] = parameters[i].Name ?? ("arg" + i.ToString());
                    }

                    plan.ParameterNames = names;

                    locations.Resolve(member, out string? filePath, out int line, out string resolution);
                    plan.FilePath = filePath;
                    plan.Line = line;
                    plan.SourceResolution = resolution;

                    plans.Add(plan);
                }
            }

            return plans;
        }
    }
}
