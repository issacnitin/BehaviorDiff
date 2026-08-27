using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RealDiff.Tracer;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace RealDiff.Weaver
{
    /// <summary>Imported references to the hook surface, resolved once per module.</summary>
    internal sealed class Refs
    {
        internal Refs(ModuleDefinition module)
        {
            Type hooks = typeof(WeaveHooks);
            RegisterAssembly = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.RegisterAssembly)));
            Register = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.Register)));
            RegisterSkipped = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.RegisterSkipped)));
            EnsureSession = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.EnsureSession)));
            Enter = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.Enter)));
            ExitValue = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitValue)));
            ExitVoid = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitVoid)));
            ExitException = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitException)));
            ExitTask = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitTask)));
            ExitTaskOf = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitTaskOf)));
            ExitValueTask = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitValueTask)));
            ExitValueTaskOf = module.ImportReference(hooks.GetMethod(nameof(WeaveHooks.ExitValueTaskOf)));
            ExceptionType = module.ImportReference(typeof(Exception));
        }

        internal MethodReference RegisterAssembly { get; }

        internal MethodReference Register { get; }

        internal MethodReference RegisterSkipped { get; }

        internal MethodReference EnsureSession { get; }

        internal MethodReference Enter { get; }

        internal MethodReference ExitValue { get; }

        internal MethodReference ExitVoid { get; }

        internal MethodReference ExitException { get; }

        internal MethodReference ExitTask { get; }

        internal MethodReference ExitTaskOf { get; }

        internal MethodReference ExitValueTask { get; }

        internal MethodReference ExitValueTaskOf { get; }

        internal TypeReference ExceptionType { get; }
    }

    internal static class Program
    {
        /// <summary>A weaver decline, not a MethodSelector skip: these are in Harmony's scope but not yet woven.</summary>
        private const string AsyncDecline = "WeaverAsyncNotSupported";

        private static int Main(string[] args)
        {
            string? assemblyPath = null;
            string? include = null;
            string? exclude = null;
            bool isTestAssembly = false;

            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--assembly": assemblyPath = args[++i]; break;
                    case "--include": include = args[++i]; break;
                    case "--exclude": exclude = args[++i]; break;
                }
            }

            isTestAssembly = Array.IndexOf(args, "--test-assembly") >= 0;

            if (assemblyPath is null || include is null)
            {
                Console.Error.WriteLine("usage: --assembly <dll> --include <ns,ns> [--exclude <ns,ns>] [--test-assembly]");
                return 2;
            }

            var options = new TracerOptions
            {
                IncludeNamespacePrefixes = Split(include),
                ExcludeNamespacePrefixes = Split(exclude),
            };

            Assembly reflected = Assembly.LoadFrom(assemblyPath);
            List<MemberPlan> plans = Descriptors.Plan(reflected, options);

            int index = 0;
            foreach (MemberPlan plan in plans.Where(p => p.SkipReason is null))
            {
                plan.WeaveIndex = index++;
            }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)));

            // DebugType=none produces no PDB. Demanding one would refuse to instrument the assembly at all,
            // which is worse than instrumenting it with no source lines: the manifest then records
            // SourceUnavailable and the engine's gate can see it.
            bool hasSymbols = File.Exists(Path.ChangeExtension(assemblyPath, ".pdb"));
            var readerParameters = new ReaderParameters
            {
                ReadSymbols = hasSymbols,
                ReadWrite = false,
                AssemblyResolver = resolver,
            };

            using ModuleDefinition module = ModuleDefinition.ReadModule(assemblyPath, readerParameters);
            var refs = new Refs(module);

            TypeDefinition moduleType = module.GetType("<Module>")
                ?? throw new InvalidOperationException("no <Module> type");

            // Descriptor indices are process-global but assigned per assembly, so each module carries the
            // base its own indices are relative to.
            // Assembly-visible, not private: every woven method in the module reads it, not just <Module>.
            var baseField = new FieldDefinition(
                "<RealDiffBase>",
                Mono.Cecil.FieldAttributes.Assembly | Mono.Cecil.FieldAttributes.Static,
                module.TypeSystem.Int32);
            moduleType.Fields.Add(baseField);

            var byToken = new Dictionary<int, MethodDefinition>();
            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    byToken[method.MetadataToken.ToInt32()] = method;
                }
            }

            var failures = new List<string>();
            int woven = 0;

            foreach (MemberPlan plan in plans.Where(p => p.SkipReason is null))
            {
                if (!byToken.TryGetValue(plan.Token, out MethodDefinition? target))
                {
                    failures.Add("no Cecil method for token 0x" + plan.Token.ToString("x8") + " " + plan.FullName);
                    continue;
                }

                try
                {
                    VariableDefinition frameLocal = Weave(target, plan, refs, baseField);
                    woven++;

                    string? assertion = Verify(target, frameLocal, refs);
                    if (assertion != null)
                    {
                        failures.Add(plan.FullName + ": " + assertion);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(plan.FullName + ": " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }
            }

            EmitModuleInitializer(module, moduleType, baseField, plans, refs, reflected.GetName().Name ?? "unknown", isTestAssembly);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("WEAVE FAILED (" + failures.Count + "):");
                foreach (string failure in failures.Take(20))
                {
                    Console.Error.WriteLine("  " + failure);
                }

                return 1;
            }

            string outputPath = assemblyPath;
module.Write(outputPath + ".woven", new WriterParameters { WriteSymbols = File.Exists(Path.ChangeExtension(assemblyPath, ".pdb")) });

            int declined = plans.Count(p => p.SkipReason == AsyncDecline);
            int skipped = plans.Count(p => p.SkipReason != null && p.SkipReason != AsyncDecline);
            Console.WriteLine("discovered : " + plans.Count);
            Console.WriteLine("woven      : " + woven);
            Console.WriteLine("skipped    : " + skipped + " (MethodSelector)");
            Console.WriteLine("declined   : " + declined + " (" + AsyncDecline + ")");
            Console.WriteLine("reconciles : " + (woven + skipped + declined == plans.Count));
            Console.WriteLine("output     : " + outputPath + ".woven");
            return woven + skipped + declined == plans.Count ? 0 : 1;
        }

        private static VariableDefinition Weave(MethodDefinition method, MemberPlan plan, Refs refs, FieldDefinition baseField)
        {
            MethodBody body = method.Body;
            body.SimplifyMacros();

            // Without this the frame local is not zero-initialised and the body fails verification.
            body.InitLocals = true;

            ModuleDefinition module = method.Module;
            var frame = new VariableDefinition(module.TypeSystem.Object);
            body.Variables.Add(frame);

            bool isVoid = method.ReturnType.FullName == "System.Void";
            VariableDefinition? result = null;
            if (!isVoid)
            {
                result = new VariableDefinition(method.ReturnType);
                body.Variables.Add(result);
            }

            var caught = new VariableDefinition(refs.ExceptionType);
            body.Variables.Add(caught);

            ILProcessor il = body.GetILProcessor();
            Instruction firstOriginal = body.Instructions[0];

            // Epilogue first, so the rewritten returns have a branch target.
            var epilogue = new List<Instruction> { Instruction.Create(OpCodes.Ldloc, frame) };
            switch (plan.ReturnKind)
            {
                case ReturnKind.Void:
                    epilogue.Add(Instruction.Create(OpCodes.Call, refs.ExitVoid));
                    break;

                case ReturnKind.Sync:
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    if (NeedsBox(method.ReturnType))
                    {
                        epilogue.Add(Instruction.Create(OpCodes.Box, method.ReturnType));
                    }

                    epilogue.Add(Instruction.Create(OpCodes.Call, refs.ExitValue));
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    break;

                case ReturnKind.Task:
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    epilogue.Add(Instruction.Create(OpCodes.Call, refs.ExitTask));
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    break;

                case ReturnKind.TaskOfT:
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    epilogue.Add(Instruction.Create(OpCodes.Call, Instantiate(refs.ExitTaskOf, method.ReturnType)));
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    break;

                // The hook returns the replacement because the original may only be consumed once.
                case ReturnKind.ValueTask:
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    epilogue.Add(Instruction.Create(OpCodes.Call, refs.ExitValueTask));
                    epilogue.Add(Instruction.Create(OpCodes.Stloc, result));
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    break;

                case ReturnKind.ValueTaskOfT:
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    epilogue.Add(Instruction.Create(OpCodes.Call, Instantiate(refs.ExitValueTaskOf, method.ReturnType)));
                    epilogue.Add(Instruction.Create(OpCodes.Stloc, result));
                    epilogue.Add(Instruction.Create(OpCodes.Ldloc, result));
                    break;

                default:
                    throw new InvalidOperationException("unhandled ReturnKind " + plan.ReturnKind);
            }

            epilogue.Add(Instruction.Create(OpCodes.Ret));
            Instruction afterTry = epilogue[0];

            var handler = new List<Instruction>
            {
                Instruction.Create(OpCodes.Stloc, caught),
                Instruction.Create(OpCodes.Ldloc, frame),
                Instruction.Create(OpCodes.Ldloc, caught),
                Instruction.Create(OpCodes.Call, refs.ExitException),
                Instruction.Create(OpCodes.Rethrow),
            };

            // Rewrite returns before appending, so the originals are still the only ret instructions.
            foreach (Instruction instruction in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
            {
                if (isVoid)
                {
                    instruction.OpCode = OpCodes.Leave;
                    instruction.Operand = afterTry;
                }
                else
                {
                    // A pre-existing branch may target this ret. Operands point at the instruction object,
                    // so inserting before it would let such a branch skip the store and leave the stack
                    // inconsistent across paths. Mutate in place and insert the leave after instead.
                    Instruction leave = Instruction.Create(OpCodes.Leave, afterTry);
                    il.InsertAfter(instruction, leave);
                    instruction.OpCode = OpCodes.Stloc;
                    instruction.Operand = result;
                }
            }

            foreach (Instruction instruction in handler.Concat(epilogue))
            {
                il.Append(instruction);
            }

            // InsertBefore keeps insertion order, so the prologue is emitted forwards, not reversed.
            foreach (Instruction instruction in BuildPrologue(method, plan, frame, refs, baseField))
            {
                il.InsertBefore(firstOriginal, instruction);
            }

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = refs.ExceptionType,
                TryStart = firstOriginal,
                TryEnd = handler[0],
                HandlerStart = handler[0],
                HandlerEnd = afterTry,
            });

            body.OptimizeMacros();
            return frame;
        }

        private static List<Instruction> BuildPrologue(
            MethodDefinition method,
            MemberPlan plan,
            VariableDefinition frame,
            Refs refs,
            FieldDefinition baseField)
        {
            var instructions = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldsfld, baseField),
                Instruction.Create(OpCodes.Ldc_I4, plan.WeaveIndex),
                Instruction.Create(OpCodes.Add),
                Instruction.Create(OpCodes.Ldc_I4, method.Parameters.Count),
                Instruction.Create(OpCodes.Newarr, method.Module.TypeSystem.Object),
            };

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                ParameterDefinition parameter = method.Parameters[i];
                instructions.Add(Instruction.Create(OpCodes.Dup));
                instructions.Add(Instruction.Create(OpCodes.Ldc_I4, i));
                instructions.Add(Instruction.Create(OpCodes.Ldarg, parameter));

                // MethodSelector admits by-ref parameters because Harmony dereferences them when it builds
                // __args. Hand-emitted IL has to do that itself, or the managed pointer lands in the object[].
                if (parameter.ParameterType is ByReferenceType byRef)
                {
                    TypeReference element = byRef.ElementType;
                    if (NeedsBox(element))
                    {
                        instructions.Add(Instruction.Create(OpCodes.Ldobj, element));
                        instructions.Add(Instruction.Create(OpCodes.Box, element));
                    }
                    else
                    {
                        instructions.Add(Instruction.Create(OpCodes.Ldind_Ref));
                    }
                }
                else if (NeedsBox(parameter.ParameterType))
                {
                    instructions.Add(Instruction.Create(OpCodes.Box, parameter.ParameterType));
                }

                instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));
            }

            instructions.Add(Instruction.Create(OpCodes.Call, refs.Enter));
            instructions.Add(Instruction.Create(OpCodes.Stloc, frame));
            return instructions;
        }

        private static bool NeedsBox(TypeReference type) =>
            type.IsValueType || type.IsGenericParameter;

        /// <summary>Binds the hook's T to the T of the method's Task&lt;T&gt; or ValueTask&lt;T&gt;.</summary>
        private static MethodReference Instantiate(MethodReference open, TypeReference returnType)
        {
            var generic = new GenericInstanceMethod(open);
            generic.GenericArguments.Add(((GenericInstanceType)returnType).GenericArguments[0]);
            return generic;
        }

        /// <summary>
        /// Structural assertions. A frame that never reaches the handler yields no event, and a missing
        /// event is indistinguishable from a behaviour change downstream, so this is a build error.
        /// </summary>
        private static string? Verify(MethodDefinition method, VariableDefinition frame, Refs refs)
        {
            MethodBody body = method.Body;

            if (!body.Variables.Contains(frame))
            {
                return "frame local is missing";
            }

            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                if (handler.CatchType?.FullName != refs.ExceptionType.FullName)
                {
                    continue;
                }

                bool loadedFrame = false;
                for (Instruction? i = handler.HandlerStart; i != null && i != handler.HandlerEnd; i = i.Next)
                {
                    if (LoadsLocal(i) == frame.Index)
                    {
                        loadedFrame = true;
                    }

                    if (i.OpCode == OpCodes.Call
                        && i.Operand is MethodReference call
                        && call.Name == refs.ExitException.Name
                        && !loadedFrame)
                    {
                        return "handler calls ExitException without loading the frame local";
                    }
                }
            }

            return null;
        }

        /// <summary>OptimizeMacros rewrites ldloc to operand-less short forms, so the index has to be recovered.</summary>
        private static int LoadsLocal(Instruction instruction)
        {
            if (instruction.OpCode == OpCodes.Ldloc_0) return 0;
            if (instruction.OpCode == OpCodes.Ldloc_1) return 1;
            if (instruction.OpCode == OpCodes.Ldloc_2) return 2;
            if (instruction.OpCode == OpCodes.Ldloc_3) return 3;
            if ((instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S)
                && instruction.Operand is VariableDefinition variable)
            {
                return variable.Index;
            }

            return -1;
        }

        private static void EmitModuleInitializer(
            ModuleDefinition module,
            TypeDefinition moduleType,
            FieldDefinition baseField,
            List<MemberPlan> plans,
            Refs refs,
            string assemblyName,
            bool isTestAssembly)
        {
            MethodDefinition? cctor = moduleType.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor is null)
            {
                cctor = new MethodDefinition(
                    ".cctor",
                    MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                        | MethodAttributes.RTSpecialName | MethodAttributes.Static,
                    module.TypeSystem.Void);
                cctor.Body = new MethodBody(cctor);
                cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                moduleType.Methods.Add(cctor);
            }

            cctor.Body.InitLocals = true;

            ILProcessor il = cctor.Body.GetILProcessor();
            Instruction ret = cctor.Body.Instructions[cctor.Body.Instructions.Count - 1];

            void Emit(Instruction instruction) => il.InsertBefore(ret, instruction);

            Emit(Instruction.Create(OpCodes.Ldstr, assemblyName));
            Emit(Instruction.Create(isTestAssembly ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            Emit(Instruction.Create(OpCodes.Ldc_I4, plans.Count));
            Emit(Instruction.Create(OpCodes.Call, refs.RegisterAssembly));
            Emit(Instruction.Create(OpCodes.Stsfld, baseField));

            foreach (MemberPlan plan in plans)
            {
                if (plan.SkipReason is null)
                {
                    Emit(Instruction.Create(OpCodes.Ldsfld, baseField));
                    Emit(Instruction.Create(OpCodes.Ldc_I4, plan.WeaveIndex));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.FullName));
                    Emit(plan.FilePath is null
                        ? Instruction.Create(OpCodes.Ldnull)
                        : Instruction.Create(OpCodes.Ldstr, plan.FilePath));
                    Emit(Instruction.Create(OpCodes.Ldc_I4, plan.Line));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.SourceResolution));
                    Emit(Instruction.Create(OpCodes.Ldc_I4, (int)plan.ReturnKind));
                    Emit(Instruction.Create(plan.IsTestRoot ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                    Emit(Instruction.Create(OpCodes.Ldstr, string.Join(",", plan.ParameterNames)));
                    Emit(Instruction.Create(OpCodes.Call, refs.Register));
                }
                else
                {
                    Emit(Instruction.Create(OpCodes.Ldsfld, baseField));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.FullName));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.SkipReason));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.SkipDetail));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.ReturnKind.ToString()));
                    Emit(Instruction.Create(plan.IsTestRoot ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                    Emit(plan.FilePath is null
                        ? Instruction.Create(OpCodes.Ldnull)
                        : Instruction.Create(OpCodes.Ldstr, plan.FilePath));
                    Emit(Instruction.Create(OpCodes.Ldc_I4, plan.Line));
                    Emit(Instruction.Create(OpCodes.Ldstr, plan.SourceResolution));
                    Emit(Instruction.Create(OpCodes.Call, refs.RegisterSkipped));
                }
            }

            // Start the session once every descriptor in this module is registered. This is what lets a woven
            // process trace with no test-framework adapter present; the first module to run here wins.
            Emit(Instruction.Create(OpCodes.Call, refs.EnsureSession));
        }

        private static string[] Split(string? value) =>
            string.IsNullOrEmpty(value)
                ? new string[0]
                : value!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
