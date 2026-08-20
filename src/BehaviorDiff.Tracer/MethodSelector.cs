using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Why a member is not observable. Every value here ends up in the coverage manifest, because an
    /// unobservable member is a hole in the frontier rule, not an implementation detail.
    /// </summary>
    internal enum SkipReason
    {
        None = 0,

        /// <summary>Declared on a generic type definition. Harmony only; Cecil weaves the definition.</summary>
        GenericTypeDefinition,

        CompilerGeneratedType,

        /// <summary>Declared on an async or iterator state machine.</summary>
        StateMachineType,

        /// <summary>In an excluded namespace.</summary>
        ExcludedNamespace,

        /// <summary>The member itself is [CompilerGenerated], e.g. a local function.</summary>
        CompilerGenerated,

        /// <summary>Property accessor, event accessor, or operator.</summary>
        PropertyOrOperator,

        /// <summary>Abstract, extern, or runtime-implemented: no IL to intercept.</summary>
        NoBody,

        /// <summary>Open generic method. Harmony only; Cecil weaves the definition.</summary>
        GenericDefinition,

        /// <summary>Returns or takes a ref struct or pointer, which cannot round-trip through object[].</summary>
        ByRefOrPointer,

        /// <summary>
        /// A static constructor. KNOWN LIMITATION, deliberate on both backends: a cctor runs under the CLR's
        /// type-initialization lock, so a hook called from inside one can load a type whose own initializer is
        /// blocked behind a lock the caller holds. This project has already hit that deadlock once, from a
        /// static constructor in the xunit adapter, and its failure mode is a startup hang with no output.
        /// Cecil weaves at build time rather than patching at runtime, but the woven Enter call still executes
        /// under that lock, so weaving does not make it safe. Revisit only with a real finding that needs it.
        /// </summary>
        TypeInitializer,

        /// <summary>Inherited object/ValueType member, not the target's own behavior.</summary>
        DeclaredOnSystemType,

        /// <summary>No usable declaring type.</summary>
        Unresolvable,
    }

    /// <summary>Decides which members get instrumented, and records a reason for every one that does not.</summary>
    internal static class MethodSelector
    {
        /// <summary>
        /// True when the type's namespace matches an include prefix. Types outside the include set are not
        /// enumerated at all and never reach the manifest; everything inside it does, even when skipped.
        /// </summary>
        internal static bool IsInScope(Type type, TracerOptions options)
        {
            return MatchesAny(type.Namespace, options.IncludeNamespacePrefixes);
        }

        /// <summary>
        /// Type-level verdict for an in-scope type. A non-None result still means every member gets a
        /// manifest entry carrying this reason.
        /// </summary>
        internal static SkipReason EvaluateType(Type type, TracerOptions options)
        {
            if (MatchesAny(type.Namespace, options.ExcludeNamespacePrefixes))
            {
                return SkipReason.ExcludedNamespace;
            }

            if (typeof(IAsyncStateMachine).IsAssignableFrom(type))
            {
                return SkipReason.StateMachineType;
            }

            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.IndexOf('<') >= 0)
            {
                return SkipReason.CompilerGeneratedType;
            }

            return type.FullName is null ? SkipReason.Unresolvable : SkipReason.None;
        }

        internal static SkipReason Evaluate(MethodBase method)
        {
            Type? declaring = method.DeclaringType;
            if (declaring is null)
            {
                return SkipReason.Unresolvable;
            }

            bool isConstructor = method is ConstructorInfo;

            // A type initialiser runs once, at a moment the runtime controls; patching it is not safe.
            if (isConstructor && method.IsStatic)
            {
                return SkipReason.TypeInitializer;
            }

            if (method.IsAbstract || method.GetMethodBody() is null)
            {
                return SkipReason.NoBody;
            }

            if ((method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0)
            {
                return SkipReason.NoBody;
            }

            if (method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || method.Name.IndexOf('<') >= 0)
            {
                return SkipReason.CompilerGenerated;
            }

            // Constructors are special-name by definition, so this filter only applies to methods.
            if (!isConstructor && method.IsSpecialName)
            {
                return SkipReason.PropertyOrOperator;
            }

            if (method is MethodInfo methodInfo)
            {
                Type returnType = methodInfo.ReturnType;
                if (returnType.IsByRef || returnType.IsPointer || IsByRefLike(returnType))
                {
                    return SkipReason.ByRefOrPointer;
                }
            }

            // Harmony materialises __args as object[]; a ref struct parameter cannot be boxed into it.
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type parameterType = parameter.ParameterType;
                if (parameterType.IsByRef)
                {
                    Type? elementType = parameterType.GetElementType();
                    if (elementType is null)
                    {
                        return SkipReason.Unresolvable;
                    }

                    parameterType = elementType;
                }

                if (parameterType.IsPointer || IsByRefLike(parameterType))
                {
                    return SkipReason.ByRefOrPointer;
                }
            }

            if (declaring == typeof(object) || declaring == typeof(ValueType))
            {
                return SkipReason.DeclaredOnSystemType;
            }

            return SkipReason.None;
        }

        internal static ReturnKind ClassifyReturn(MethodBase method)
        {
            if (method is not MethodInfo methodInfo)
            {
                // Constructors produce no value; args are the whole point of tracing them.
                return ReturnKind.Void;
            }

            Type returnType = methodInfo.ReturnType;

            if (returnType == typeof(void))
            {
                return ReturnKind.Void;
            }

            if (returnType == typeof(ValueTask))
            {
                return ReturnKind.ValueTask;
            }

            if (returnType.IsGenericType)
            {
                Type definition = returnType.GetGenericTypeDefinition();
                if (definition == typeof(Task<>))
                {
                    return ReturnKind.TaskOfT;
                }

                if (definition == typeof(ValueTask<>))
                {
                    return ReturnKind.ValueTaskOfT;
                }
            }

            if (typeof(Task).IsAssignableFrom(returnType))
            {
                return ReturnKind.Task;
            }

            return ReturnKind.Sync;
        }

        /// <summary>
        /// True when the member is a test entry point. Matched against attribute type names walking the
        /// inheritance chain, so the tracer stays independent of any particular test framework version.
        /// Uses GetCustomAttributesData rather than GetCustomAttributes: reading attribute metadata must
        /// not run attribute constructors inside the process under test.
        /// </summary>
        internal static bool IsTestRoot(MethodBase method, IReadOnlyList<string> testAttributeNames)
        {
            if (testAttributeNames.Count == 0)
            {
                return false;
            }

            IList<CustomAttributeData> attributes;
            try
            {
                attributes = method.GetCustomAttributesData();
            }
            catch (Exception)
            {
                return false;
            }

            foreach (CustomAttributeData attribute in attributes)
            {
                for (Type? type = attribute.AttributeType; type != null; type = type.BaseType)
                {
                    string? fullName = type.FullName;
                    if (fullName is null)
                    {
                        continue;
                    }

                    for (int i = 0; i < testAttributeNames.Count; i++)
                    {
                        if (string.Equals(fullName, testAttributeNames[i], StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Matched against an assembly's direct references. Naming conventions are not used: "Foo.Tests"
        // does not hold across real repos, and a project can be a test assembly under any name.
        private static readonly string[] TestFrameworkReferencePrefixes =
        {
            "xunit",
            "nunit",
            "NUnit",
            "MSTest",
            "Microsoft.VisualStudio.TestPlatform.TestFramework",
        };

        /// <summary>
        /// True when the assembly directly references a test framework, which makes everything it declares
        /// harness code. <paramref name="trigger"/> receives the reference that matched.
        /// </summary>
        /// <remarks>
        /// Only direct references are visible in metadata, so an assembly that reaches a test framework
        /// purely transitively is not detected. That is the conservative direction: it would be classified
        /// as subject code and remain a frontier candidate.
        /// </remarks>
        internal static bool IsTestAssembly(Assembly assembly, out string? trigger)
        {
            trigger = null;

            AssemblyName[] references;
            try
            {
                references = assembly.GetReferencedAssemblies();
            }
            catch (Exception)
            {
                return false;
            }

            string? best = null;
            foreach (AssemblyName reference in references)
            {
                string name = reference.Name ?? string.Empty;
                foreach (string prefix in TestFrameworkReferencePrefixes)
                {
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Lowest ordinal wins so the recorded trigger is stable between runs.
                    if (best is null || string.CompareOrdinal(name, best) < 0)
                    {
                        best = name;
                    }

                    break;
                }
            }

            trigger = best;
            return best != null;
        }

        private static bool MatchesAny(string? ns, IReadOnlyList<string> prefixes)
        {
            if (string.IsNullOrEmpty(ns))
            {
                return false;
            }

            for (int i = 0; i < prefixes.Count; i++)
            {
                string prefix = prefixes[i];
                if (ns!.Equals(prefix, StringComparison.Ordinal)
                    || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Type.IsByRefLike does not exist in netstandard2.0, so the ref struct marker attribute is
        /// matched by name instead.
        /// </summary>
        private static bool IsByRefLike(Type type)
        {
            if (!type.IsValueType)
            {
                return false;
            }

            foreach (CustomAttributeData attribute in type.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The engine's join key, and the key step 5 compares method sets on. Both instrumentation backends
        /// must call this one implementation: a formatting difference reads downstream as total scope loss.
        /// </summary>
        internal static string BuildFullName(MethodBase member, ParameterInfo[] parameters)
        {
            var builder = new StringBuilder(96);
            Type? declaring = member.DeclaringType;
            builder.Append(declaring?.FullName ?? "<unknown>").Append('.').Append(member.Name).Append('(');

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                Type parameterType = parameters[i].ParameterType;
                builder.Append(parameterType.FullName ?? parameterType.Name);
            }

            return builder.Append(')').ToString();
        }
    }
}
