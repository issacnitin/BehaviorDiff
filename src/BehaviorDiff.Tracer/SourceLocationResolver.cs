using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Maps a <see cref="MethodBase"/> to its declaring source file and line by reading sequence points
    /// out of the portable PDB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Portable PDBs only, either side-by-side or embedded in the PE - what the .NET SDK emits by default
    /// (<c>DebugType=portable</c> / <c>embedded</c>). Behaviour when that does not hold:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>No PDB at all</b>: every member of the assembly resolves to
    /// <see cref="SourceResolution.NoPdb"/>.</description></item>
    /// <item><description><b>Windows-format (full) PDB</b>: <c>TryOpenAssociatedPortablePdb</c> returns
    /// false because the debug directory entry is not a portable one. Reading it would need DiaSymReader,
    /// which is not referenced. Result is <see cref="SourceResolution.NoPdb"/>, not a wrong path.</description></item>
    /// <item><description><b>Embedded PDB</b>: handled - the debug directory carries the compressed image
    /// and the PE reader is kept alive to back it.</description></item>
    /// <item><description><b>Side-by-side PDB moved away from the assembly</b>: the debug directory path is
    /// probed and the file is absent, so <see cref="SourceResolution.NoPdb"/>.</description></item>
    /// </list>
    /// <para>
    /// None of these produce a null that could be mistaken for "this member is not in a changed file". The
    /// outcome is always explicit, because the engine classifies an unresolved path as EXPECTED and would
    /// otherwise silently drop every divergence in the affected assembly.
    /// </para>
    /// </remarks>
    internal sealed class SourceLocationResolver : IDisposable
    {
        private readonly Dictionary<Assembly, PdbHandle?> _pdbs = new Dictionary<Assembly, PdbHandle?>();
        private readonly Dictionary<Type, string?> _typeLocations = new Dictionary<Type, string?>();
        private readonly object _gate = new object();
        private bool _disposed;

        /// <summary>Resolves a member's source location, reporting how the answer was reached.</summary>
        internal void Resolve(MethodBase method, out string? filePath, out int line, out string resolution)
        {
            filePath = null;
            line = 0;
            resolution = SourceResolution.Unresolved;

            Type? declaringType = method.DeclaringType;
            if (declaringType is null)
            {
                return;
            }

            PdbHandle? pdb = GetPdb(declaringType.Assembly);
            if (pdb is null)
            {
                resolution = SourceResolution.NoPdb;
                return;
            }

            if (TryResolveToken(pdb, method.MetadataToken, out filePath, out line))
            {
                resolution = SourceResolution.SequencePoints;
                return;
            }

            // An async or iterator kickoff method carries no sequence points of its own; the body lives on
            // the generated state machine's MoveNext.
            MethodInfo? moveNext = FindStateMachineMoveNext(method);
            if (moveNext != null && TryResolveToken(pdb, moveNext.MetadataToken, out filePath, out line))
            {
                resolution = SourceResolution.StateMachine;
                return;
            }

            // An implicit constructor has no sequence points and no state machine. The file is still known
            // - it is wherever the type is declared - so fall back to any sibling member that does resolve.
            // The line is genuinely unknown and stays 0 rather than borrowing the sibling's.
            string? typeLocation = GetTypeLocation(pdb, declaringType);
            if (typeLocation != null)
            {
                filePath = typeLocation;
                line = 0;
                resolution = SourceResolution.DeclaringType;
                return;
            }

            resolution = SourceResolution.Unresolved;
        }

        private string? GetTypeLocation(PdbHandle pdb, Type type)
        {
            lock (_gate)
            {
                if (_typeLocations.TryGetValue(type, out string? cached))
                {
                    return cached;
                }
            }

            string? resolved = null;
            try
            {
                const BindingFlags Flags =
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                var tokens = new List<int>();
                foreach (MethodInfo sibling in type.GetMethods(Flags))
                {
                    tokens.Add(sibling.MetadataToken);
                }

                foreach (ConstructorInfo sibling in type.GetConstructors(Flags))
                {
                    tokens.Add(sibling.MetadataToken);
                }

                // Ordered so the answer is the same on every run.
                tokens.Sort();

                foreach (int token in tokens)
                {
                    if (TryResolveToken(pdb, token, out string? path, out _) && path != null)
                    {
                        resolved = path;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                resolved = null;
            }

            lock (_gate)
            {
                _typeLocations[type] = resolved;
            }

            return resolved;
        }

        private static MethodInfo? FindStateMachineMoveNext(MethodBase method)
        {
            Type? stateMachine =
                method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                ?? method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;

            if (stateMachine is null)
            {
                return null;
            }

            return stateMachine.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
        }

        private static bool TryResolveToken(PdbHandle pdb, int metadataToken, out string? filePath, out int line)
        {
            filePath = null;
            line = 0;

            int rowNumber = metadataToken & 0x00FFFFFF;
            if (rowNumber <= 0)
            {
                return false;
            }

            try
            {
                MetadataReader reader = pdb.Reader;
                if (rowNumber > reader.MethodDebugInformation.Count)
                {
                    return false;
                }

                MethodDebugInformation debugInfo =
                    reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(rowNumber));

                if (debugInfo.SequencePointsBlob.IsNil)
                {
                    return false;
                }

                foreach (SequencePoint point in debugInfo.GetSequencePoints())
                {
                    if (point.IsHidden)
                    {
                        continue;
                    }

                    Document document = reader.GetDocument(point.Document);
                    filePath = reader.GetString(document.Name);
                    line = point.StartLine;
                    return true;
                }
            }
            catch (BadImageFormatException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        }

        private PdbHandle? GetPdb(Assembly assembly)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_pdbs.TryGetValue(assembly, out PdbHandle? cached))
                {
                    return cached;
                }

                PdbHandle? handle = OpenPdb(assembly);
                _pdbs[assembly] = handle;
                return handle;
            }
        }

        private static PdbHandle? OpenPdb(Assembly assembly)
        {
            string? location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException)
            {
                return null;
            }

            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return null;
            }

            FileStream? peStream = null;
            PEReader? peReader = null;
            try
            {
                peStream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                peReader = new PEReader(peStream);

                // Handles both the embedded PDB case and the side-by-side .pdb named in the debug directory.
                if (!peReader.TryOpenAssociatedPortablePdb(
                        location,
                        OpenPdbFile,
                        out MetadataReaderProvider? provider,
                        out _)
                    || provider is null)
                {
                    peReader.Dispose();
                    peStream.Dispose();
                    return null;
                }

                return new PdbHandle(peReader, provider);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                peReader?.Dispose();
                peStream?.Dispose();
                return null;
            }
        }

        private static Stream? OpenPdbFile(string path)
        {
            try
            {
                return File.Exists(path)
                    ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (KeyValuePair<Assembly, PdbHandle?> entry in _pdbs)
                {
                    entry.Value?.Dispose();
                }

                _pdbs.Clear();
            }
        }

        /// <summary>The PE reader must outlive the provider: an embedded PDB is backed by the PE image.</summary>
        private sealed class PdbHandle : IDisposable
        {
            private readonly PEReader _peReader;
            private readonly MetadataReaderProvider _provider;

            internal PdbHandle(PEReader peReader, MetadataReaderProvider provider)
            {
                _peReader = peReader;
                _provider = provider;
                Reader = provider.GetMetadataReader();
            }

            internal MetadataReader Reader { get; }

            public void Dispose()
            {
                _provider.Dispose();
                _peReader.Dispose();
            }
        }
    }
}
